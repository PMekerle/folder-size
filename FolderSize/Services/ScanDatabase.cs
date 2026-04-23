using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using FolderSize.Models;

namespace FolderSize.Services;

public sealed class ScanDatabase
{
    private readonly string _dbPath;
    private readonly HashSet<string> _savedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ScanDatabase()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderSize");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "scans.db");
        Initialize();
        LoadPaths();
    }

    public string DbPath => _dbPath;

    public bool HasScan(string path)
    {
        lock (_gate)
        {
            return _savedPaths.Contains(Normalize(path));
        }
    }

    public IReadOnlyCollection<string> SavedPaths
    {
        get { lock (_gate) return _savedPaths.ToArray(); }
    }

    public long GetDbFileSize()
    {
        try { return new FileInfo(_dbPath).Length; }
        catch { return 0; }
    }

    public List<ScanSummary> GetAllSummaries()
    {
        var list = new List<ScanSummary>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT path, scanned_at, size, size_on_disk, file_count, LENGTH(data) as blob_size, duration_ms
                FROM scans
                ORDER BY scanned_at DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new ScanSummary
                {
                    Path = reader.GetString(0),
                    ScannedAt = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)).LocalDateTime,
                    Size = reader.GetInt64(2),
                    SizeOnDisk = reader.GetInt64(3),
                    FileCount = reader.GetInt64(4),
                    BlobSize = reader.GetInt64(5),
                    DurationMs = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                });
            }
        }
        catch (Exception ex)
        {
            Log.Error("GetAllSummaries failed", ex);
        }
        return list;
    }

    public List<ScanSummary> GetTopLevelSummaries()
    {
        var all = GetAllSummaries();
        var result = new List<ScanSummary>();
        foreach (var x in all)
        {
            bool covered = false;
            foreach (var y in all)
            {
                if (ReferenceEquals(x, y)) continue;
                if (IsAncestor(y.Path, x.Path) && y.ScannedAt > x.ScannedAt)
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) result.Add(x);
        }
        return result;
    }

    private static bool IsAncestor(string ancestor, string descendant)
    {
        if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant)) return false;
        var a = ancestor.TrimEnd('\\', '/').ToLowerInvariant();
        var d = descendant.TrimEnd('\\', '/').ToLowerInvariant();
        if (a == d) return false;
        return d.StartsWith(a + "\\", StringComparison.Ordinal) ||
               d.StartsWith(a + "/", StringComparison.Ordinal);
    }

    private void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS scans (
                path TEXT PRIMARY KEY,
                data BLOB NOT NULL,
                scanned_at INTEGER NOT NULL,
                size INTEGER NOT NULL,
                size_on_disk INTEGER NOT NULL,
                file_count INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();

        using var alter1 = conn.CreateCommand();
        alter1.CommandText = "ALTER TABLE scans ADD COLUMN duration_ms INTEGER NOT NULL DEFAULT 0;";
        try { alter1.ExecuteNonQuery(); } catch { }

        using var alter2 = conn.CreateCommand();
        alter2.CommandText = "ALTER TABLE scans ADD COLUMN compressed INTEGER NOT NULL DEFAULT 0;";
        try { alter2.ExecuteNonQuery(); } catch { }
    }

    private void LoadPaths()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT path FROM scans";
            using var reader = cmd.ExecuteReader();
            lock (_gate)
            {
                _savedPaths.Clear();
                while (reader.Read())
                {
                    _savedPaths.Add(reader.GetString(0));
                }
            }
            Log.Info($"ScanDatabase: loaded {_savedPaths.Count} saved paths");
        }
        catch (Exception ex)
        {
            Log.Error("ScanDatabase.LoadPaths failed", ex);
        }
    }

    public void Save(string path, FolderNode root, long durationMs = 0)
    {
        try
        {
            var normalized = Normalize(path);
            var dto = FolderNodeDto.From(root);
            var json = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOpts);
            var compressed = Compress(json);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            using var conn = Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT INTO scans (path, data, scanned_at, size, size_on_disk, file_count, duration_ms, compressed)
                    VALUES ($p, $d, $t, $s, $o, $f, $dur, 1)
                    ON CONFLICT(path) DO UPDATE SET
                        data=excluded.data,
                        scanned_at=excluded.scanned_at,
                        size=excluded.size,
                        size_on_disk=excluded.size_on_disk,
                        file_count=excluded.file_count,
                        duration_ms=excluded.duration_ms,
                        compressed=excluded.compressed;";
                cmd.Parameters.AddWithValue("$p", normalized);
                cmd.Parameters.AddWithValue("$d", compressed);
                cmd.Parameters.AddWithValue("$t", now);
                cmd.Parameters.AddWithValue("$s", root.Size);
                cmd.Parameters.AddWithValue("$o", root.SizeOnDisk);
                cmd.Parameters.AddWithValue("$f", root.FileCount);
                cmd.Parameters.AddWithValue("$dur", durationMs);
                cmd.ExecuteNonQuery();
            }

            // Any descendant scan is now covered by this (fresher) ancestor scan — delete them.
            var descendants = new List<string>();
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT path FROM scans WHERE path != $p AND scanned_at <= $t";
                sel.Parameters.AddWithValue("$p", normalized);
                sel.Parameters.AddWithValue("$t", now);
                using var reader = sel.ExecuteReader();
                while (reader.Read())
                {
                    var p = reader.GetString(0);
                    if (IsAncestor(normalized, p)) descendants.Add(p);
                }
            }
            foreach (var d in descendants)
            {
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM scans WHERE path = $p";
                del.Parameters.AddWithValue("$p", d);
                del.ExecuteNonQuery();
            }

            lock (_gate)
            {
                foreach (var d in descendants) _savedPaths.Remove(d);
                _savedPaths.Add(normalized);
            }

            if (descendants.Count > 0)
            {
                Log.Info($"ScanDatabase: removed {descendants.Count} descendant scan(s) covered by {normalized}");
            }

            double ratio = json.Length > 0 ? (double)compressed.Length / json.Length : 0;
            Log.Info($"ScanDatabase: saved {normalized} (json {json.Length / 1024} KB, compressed {compressed.Length / 1024} KB, ratio {ratio:P0})");

            MaybeVacuum();
        }
        catch (Exception ex)
        {
            Log.Error($"ScanDatabase.Save failed for {path}", ex);
        }
    }

    // Run VACUUM only when the DB file has meaningful slack (> 50% overhead and > 1 MB).
    // Keeps the file compact without paying for a rewrite on every save.
    private void MaybeVacuum()
    {
        try
        {
            long fileSize = new FileInfo(_dbPath).Length;
            long dataSize;
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(LENGTH(data)), 0) FROM scans";
                dataSize = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            long overhead = fileSize - dataSize;
            if (overhead > 1_000_000 && overhead > dataSize / 2)
            {
                Vacuum();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"MaybeVacuum check failed: {ex.Message}");
        }
    }

    private static byte[] Compress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            gz.Write(data, 0, data.Length);
        }
        return ms.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        using var ms2 = new MemoryStream();
        gz.CopyTo(ms2);
        return ms2.ToArray();
    }

    public FolderNode? Load(string path)
    {
        try
        {
            var normalized = Normalize(path);
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT data, compressed FROM scans WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", normalized);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var bytes = (byte[])reader["data"];
            long compressed = reader.GetInt64(1);
            if (compressed == 1) bytes = Decompress(bytes);
            var dto = JsonSerializer.Deserialize<FolderNodeDto>(bytes, JsonOpts);
            return dto?.ToNode();
        }
        catch (Exception ex)
        {
            Log.Error($"ScanDatabase.Load failed for {path}", ex);
            return null;
        }
    }

    public DateTime? GetScanTime(string path)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scanned_at FROM scans WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", Normalize(path));
            var r = cmd.ExecuteScalar();
            if (r is null || r == DBNull.Value) return null;
            return DateTimeOffset.FromUnixTimeSeconds((long)r).LocalDateTime;
        }
        catch { return null; }
    }

    public FolderNode? LoadMerged(string path)
    {
        var root = Load(path);
        if (root == null) return null;

        DateTime rootTime;
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT scanned_at FROM scans WHERE path=$p";
            cmd.Parameters.AddWithValue("$p", Normalize(path));
            var result = cmd.ExecuteScalar();
            if (result is null) return root;
            rootTime = DateTimeOffset.FromUnixTimeSeconds((long)result).UtcDateTime;
        }
        catch (Exception ex)
        {
            Log.Error($"LoadMerged time lookup failed for {path}", ex);
            return root;
        }

        var all = GetAllSummaries();
        var newerDescendants = all
            .Where(s => IsAncestor(path, s.Path) && s.ScannedAt.ToUniversalTime() > rootTime)
            .OrderBy(s => s.ScannedAt)
            .ToList();

        if (newerDescendants.Count == 0) return root;

        int applied = 0;
        foreach (var sub in newerDescendants)
        {
            var subRoot = Load(sub.Path);
            if (subRoot == null) continue;
            if (ReplaceSubtree(root, sub.Path, subRoot)) applied++;
        }

        if (applied > 0)
        {
            RecomputeAggregates(root);
            Log.Info($"LoadMerged({path}): merged {applied} newer descendant scan(s)");
        }
        return root;
    }

    private static bool ReplaceSubtree(FolderNode root, string targetPath, FolderNode replacement)
    {
        var node = FindNode(root, targetPath);
        if (node == null || node == root) return false;
        var parent = node.Parent;
        if (parent == null) return false;
        int idx = parent.Children.IndexOf(node);
        if (idx < 0) return false;

        replacement.Name = node.Name;
        replacement.Parent = parent;
        parent.Children[idx] = replacement;
        return true;
    }

    private static FolderNode? FindNode(FolderNode root, string targetPath)
    {
        var target = NormalizePathForMatch(targetPath);
        if (NormalizePathForMatch(root.FullPath) == target) return root;
        foreach (var c in root.Children)
        {
            var found = FindNode(c, targetPath);
            if (found != null) return found;
        }
        return null;
    }

    private static string NormalizePathForMatch(string p) => (p ?? "").TrimEnd('\\', '/').ToLowerInvariant();

    private static void RecomputeAggregates(FolderNode node)
    {
        if (!node.IsDirectory || node.IsReparsePoint) return;
        if (node.Children.Count == 0) return;

        long size = 0, onDisk = 0, count = 0;
        foreach (var c in node.Children)
        {
            if (c.IsDirectory && !c.IsReparsePoint)
            {
                RecomputeAggregates(c);
            }
            size += c.Size;
            onDisk += c.SizeOnDisk;
            count += c.FileCount;
        }
        node.Size = size;
        node.SizeOnDisk = onDisk;
        node.FileCount = count;
    }

    public void Delete(string path)
    {
        try
        {
            var normalized = Normalize(path);
            using (var conn = Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM scans WHERE path=$p";
                cmd.Parameters.AddWithValue("$p", normalized);
                cmd.ExecuteNonQuery();
            }
            lock (_gate) _savedPaths.Remove(normalized);
            Vacuum();
        }
        catch (Exception ex)
        {
            Log.Error($"ScanDatabase.Delete failed for {path}", ex);
        }
    }

    public void Vacuum()
    {
        try
        {
            long before = 0, after = 0;
            try { before = new FileInfo(_dbPath).Length; } catch { }
            using (var conn = Open())
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "VACUUM;";
                cmd.ExecuteNonQuery();
            }
            try { after = new FileInfo(_dbPath).Length; } catch { }
            Log.Info($"ScanDatabase: VACUUM {before / 1024} KB -> {after / 1024} KB");
        }
        catch (Exception ex)
        {
            Log.Error("ScanDatabase.Vacuum failed", ex);
        }
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return path ?? "";
        return path.TrimEnd('\\', '/').ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = false,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };
}

public sealed class ScanSummary
{
    public string Path { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    public long Size { get; set; }
    public long SizeOnDisk { get; set; }
    public long FileCount { get; set; }
    public long BlobSize { get; set; }
    public long DurationMs { get; set; }
}

public sealed class FolderNodeDto
{
    public string N { get; set; } = "";        // Name
    public string P { get; set; } = "";        // FullPath (only stored for root; reconstructed for children)
    public byte F { get; set; }                // flags: 1=IsDirectory, 2=IsReparsePoint
    public long S { get; set; }                // Size (recursive)
    public long O { get; set; }                // SizeOnDisk (recursive)
    public long C { get; set; }                // FileCount (recursive)
    public long DS { get; set; }               // DirectFileSize
    public long DO { get; set; }               // DirectFileSizeOnDisk
    public long DC { get; set; }               // DirectFileCount
    public List<FolderNodeDto>? K { get; set; } // Children (folders only)

    // Legacy field aliases (old format before compact JSON keys)
    public string? Name { set { if (!string.IsNullOrEmpty(value)) N = value; } }
    public string? FullPath { set { if (!string.IsNullOrEmpty(value)) P = value; } }
    public bool IsDirectory { set { if (value) F |= 1; } }
    public bool IsReparsePoint { set { if (value) F |= 2; } }
    public long Size { set { if (value != 0) S = value; } }
    public long SizeOnDisk { set { if (value != 0) O = value; } }
    public long FileCount { set { if (value != 0) C = value; } }
    public List<FolderNodeDto>? Children { set { if (value != null && value.Count > 0) K = value; } }

    public bool IsLegacy => F == 0 && !string.IsNullOrEmpty(N);

    public static FolderNodeDto From(FolderNode n, bool includeFullPath = true)
    {
        byte flags = 0;
        if (n.IsDirectory) flags |= 1;
        if (n.IsReparsePoint) flags |= 2;

        var dto = new FolderNodeDto
        {
            N = n.Name,
            P = includeFullPath ? n.FullPath : "",
            F = flags,
            S = n.Size,
            O = n.SizeOnDisk,
            C = n.FileCount,
            DS = n.DirectFileSize,
            DO = n.DirectFileSizeOnDisk,
            DC = n.DirectFileCount,
        };

        var folderChildren = n.Children.Where(c => c.IsDirectory || c.IsReparsePoint).ToList();
        if (folderChildren.Count > 0)
        {
            dto.K = new List<FolderNodeDto>(folderChildren.Count);
            foreach (var c in folderChildren)
                dto.K.Add(From(c, includeFullPath: false));
        }
        return dto;
    }

    public FolderNode ToNode(string? parentPath = null)
    {
        string fullPath = !string.IsNullOrEmpty(P) ? P : System.IO.Path.Combine(parentPath ?? "", N);
        var n = new FolderNode
        {
            Name = N,
            FullPath = fullPath,
            IsDirectory = (F & 1) != 0,
            IsReparsePoint = (F & 2) != 0,
            Size = S,
            SizeOnDisk = O,
            FileCount = C,
            DirectFileSize = DS,
            DirectFileSizeOnDisk = DO,
            DirectFileCount = DC,
        };
        if (K != null)
        {
            foreach (var c in K)
            {
                var child = c.ToNode(fullPath);
                child.Parent = n;
                n.Children.Add(child);
            }
        }
        return n;
    }
}
