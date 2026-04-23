# Inspect the FolderSize SQLite database. Run after a build.
$repoRoot = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $repoRoot "FolderSize\bin\Debug\net8.0-windows\Microsoft.Data.Sqlite.dll"
if (-not (Test-Path $dll)) { throw "Build the project first; missing $dll" }
Add-Type -Path $dll -ErrorAction Stop

$dbPath = Join-Path $env:LOCALAPPDATA "FolderSize\scans.db"
if (-not (Test-Path $dbPath)) { throw "No DB at $dbPath; run the app at least once." }
$conn = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$dbPath")
$conn.Open()

$cmd = $conn.CreateCommand()
$cmd.CommandText = "SELECT path, compressed, scanned_at, file_count, size, LENGTH(data) as blob_size, duration_ms FROM scans ORDER BY scanned_at DESC"
$reader = $cmd.ExecuteReader()
"{0,-50} {1,-6} {2,-20} {3,12} {4,18} {5,12} {6,10}" -f "path", "cmpr", "scanned_at", "files", "size", "blob", "dur_ms"
while ($reader.Read()) {
    $path = $reader.GetString(0)
    $compressed = $reader.GetInt64(1)
    $scannedAt = [DateTimeOffset]::FromUnixTimeSeconds($reader.GetInt64(2)).LocalDateTime
    $fileCount = $reader.GetInt64(3)
    $size = $reader.GetInt64(4)
    $blobSize = $reader.GetInt64(5)
    $durationMs = $reader.GetInt64(6)
    "{0,-50} {1,-6} {2,-20:yyyy-MM-dd HH:mm} {3,12:N0} {4,18:N0} {5,12:N0} {6,10:N0}" -f $path, $compressed, $scannedAt, $fileCount, $size, $blobSize, $durationMs
}
$reader.Close()
$conn.Close()
