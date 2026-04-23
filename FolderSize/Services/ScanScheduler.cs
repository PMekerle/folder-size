using System;
using System.Collections.Generic;
using System.Linq;

namespace FolderSize.Services;

public enum SchedulerAction
{
    RunNow,
    Queue,
    CancelActiveAndQueue,
    CancelQueuedAndQueue,
    AlreadyInProgress,
}

public sealed class SchedulerDecision
{
    public SchedulerAction Action { get; init; }
    public string? CancelActivePath { get; init; }
    public IReadOnlyList<string> CancelQueuedPaths { get; init; } = Array.Empty<string>();
}

// Pure coordination logic: given the currently active + queued paths on a single drive,
// decide what to do when a new scan request arrives for that drive.
// Extracted as a static method so it can be exercised without WPF/dispatcher plumbing.
public static class ScanScheduler
{
    public static SchedulerDecision Decide(
        string newPath,
        bool forceRescan,
        string? activePath,
        IReadOnlyList<string> queuedPaths)
    {
        var qs = queuedPaths ?? Array.Empty<string>();

        // Nothing active on this drive
        if (string.IsNullOrEmpty(activePath))
        {
            // Drop any queued entries made redundant by this request.
            var redundant = qs.Where(q => IsSame(q, newPath) || IsAncestor(newPath, q) || IsAncestor(q, newPath)).ToList();
            if (redundant.Any(q => IsSame(q, newPath)) && !forceRescan)
                return new SchedulerDecision { Action = SchedulerAction.AlreadyInProgress };
            if (redundant.Count > 0)
                return new SchedulerDecision { Action = SchedulerAction.CancelQueuedAndQueue, CancelQueuedPaths = redundant };
            return new SchedulerDecision { Action = SchedulerAction.RunNow };
        }

        // Exact same path as active
        if (IsSame(activePath, newPath))
        {
            return forceRescan
                ? new SchedulerDecision { Action = SchedulerAction.CancelActiveAndQueue, CancelActivePath = activePath }
                : new SchedulerDecision { Action = SchedulerAction.AlreadyInProgress };
        }

        // Active is ancestor of new, or new is ancestor of active.
        // Cancel active (its work either gets redone as part of new scope, or is now out-of-scope).
        if (IsAncestor(activePath, newPath) || IsAncestor(newPath, activePath))
        {
            var redundant = qs.Where(q => IsSame(q, newPath) || IsAncestor(newPath, q) || IsAncestor(q, newPath)).ToList();
            return new SchedulerDecision
            {
                Action = SchedulerAction.CancelActiveAndQueue,
                CancelActivePath = activePath,
                CancelQueuedPaths = redundant,
            };
        }

        // Unrelated to active on same drive: check queue.
        if (qs.Any(q => IsSame(q, newPath)) && !forceRescan)
            return new SchedulerDecision { Action = SchedulerAction.AlreadyInProgress };

        // Queue, and drop any queued entries made redundant by this one.
        var dropped = qs.Where(q => !IsSame(q, newPath) && (IsAncestor(newPath, q) || IsAncestor(q, newPath))).ToList();
        if (dropped.Count > 0)
            return new SchedulerDecision { Action = SchedulerAction.CancelQueuedAndQueue, CancelQueuedPaths = dropped };
        return new SchedulerDecision { Action = SchedulerAction.Queue };
    }

    public static string DriveRootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            var r = System.IO.Path.GetPathRoot(path);
            return (r ?? "").TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch { return ""; }
    }

    public static bool IsSame(string a, string b)
    {
        if (a == null || b == null) return false;
        return string.Equals(
            a.TrimEnd('\\', '/'),
            b.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAncestor(string ancestor, string descendant)
    {
        if (string.IsNullOrEmpty(ancestor) || string.IsNullOrEmpty(descendant)) return false;
        var a = ancestor.TrimEnd('\\', '/').ToLowerInvariant();
        var d = descendant.TrimEnd('\\', '/').ToLowerInvariant();
        if (a == d) return false;
        return d.StartsWith(a + "\\", StringComparison.Ordinal) ||
               d.StartsWith(a + "/", StringComparison.Ordinal);
    }
}
