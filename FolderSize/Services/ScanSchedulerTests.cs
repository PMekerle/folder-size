using System;
using System.Collections.Generic;

namespace FolderSize.Services;

// Console-style test runner for ScanScheduler.
// Invoke via:  FolderSize.exe --test-scheduler
public static class ScanSchedulerTests
{
    private static int _pass;
    private static int _fail;
    private static readonly List<string> _failures = new();

    public static int RunAll()
    {
        _pass = 0; _fail = 0; _failures.Clear();

        Test_NothingActive_RunNow();
        Test_SamePathActive_NotForce_AlreadyInProgress();
        Test_SamePathActive_Force_CancelActiveAndQueue();
        Test_AncestorActive_DescendantRequested_CancelActive();
        Test_DescendantActive_AncestorRequested_CancelActive();
        Test_UnrelatedActive_Queue();
        Test_NewDescendantOfRequested_InQueue_Dropped();
        Test_NewRequestCoversQueuedAncestor_Dropped();
        Test_NothingActive_ButSamePathQueued_AlreadyInProgress();
        Test_DifferentDriveNotChecked();

        Console.WriteLine();
        Console.WriteLine($"==== ScanScheduler tests: {_pass} passed, {_fail} failed ====");
        if (_fail > 0)
        {
            foreach (var f in _failures) Console.WriteLine("FAIL: " + f);
        }
        return _fail == 0 ? 0 : 1;
    }

    private static void Assert(bool cond, string label)
    {
        if (cond) { _pass++; Console.WriteLine($"  PASS  {label}"); }
        else { _fail++; _failures.Add(label); Console.WriteLine($"  FAIL  {label}"); }
    }

    private static void Test_NothingActive_RunNow()
    {
        Console.WriteLine("Scenario: nothing active → RunNow");
        var d = ScanScheduler.Decide(@"C:\Users", false, null, Array.Empty<string>());
        Assert(d.Action == SchedulerAction.RunNow, "action is RunNow");
    }

    private static void Test_SamePathActive_NotForce_AlreadyInProgress()
    {
        Console.WriteLine("Scenario: same path active, not force → AlreadyInProgress");
        var d = ScanScheduler.Decide(@"C:\Users", false, @"C:\Users", Array.Empty<string>());
        Assert(d.Action == SchedulerAction.AlreadyInProgress, "action is AlreadyInProgress");
    }

    private static void Test_SamePathActive_Force_CancelActiveAndQueue()
    {
        Console.WriteLine("Scenario: same path active, force rescan → CancelActiveAndQueue");
        var d = ScanScheduler.Decide(@"C:\Users", true, @"C:\Users", Array.Empty<string>());
        Assert(d.Action == SchedulerAction.CancelActiveAndQueue, "action is CancelActiveAndQueue");
        Assert(ScanScheduler.IsSame(d.CancelActivePath ?? "", @"C:\Users"), "cancels the same active path");
    }

    private static void Test_AncestorActive_DescendantRequested_CancelActive()
    {
        Console.WriteLine("Scenario: ancestor C:\\ active, descendant C:\\Users\\Demo requested → CancelActive");
        var d = ScanScheduler.Decide(@"C:\Users\Demo", false, @"C:\", Array.Empty<string>());
        Assert(d.Action == SchedulerAction.CancelActiveAndQueue, "action is CancelActiveAndQueue");
        Assert(ScanScheduler.IsSame(d.CancelActivePath ?? "", @"C:\"), "cancels the ancestor");
    }

    private static void Test_DescendantActive_AncestorRequested_CancelActive()
    {
        Console.WriteLine("Scenario: descendant active, ancestor requested → CancelActive");
        var d = ScanScheduler.Decide(@"C:\Users", false, @"C:\Users\Demo", Array.Empty<string>());
        Assert(d.Action == SchedulerAction.CancelActiveAndQueue, "action is CancelActiveAndQueue");
        Assert(ScanScheduler.IsSame(d.CancelActivePath ?? "", @"C:\Users\Demo"), "cancels the descendant");
    }

    private static void Test_UnrelatedActive_Queue()
    {
        Console.WriteLine("Scenario: unrelated active on same drive → Queue");
        var d = ScanScheduler.Decide(@"C:\Temp", false, @"C:\Users", Array.Empty<string>());
        Assert(d.Action == SchedulerAction.Queue, "action is Queue");
    }

    private static void Test_NewDescendantOfRequested_InQueue_Dropped()
    {
        Console.WriteLine("Scenario: request C:\\Users, queue has C:\\Users\\Demo → drop the queued descendant");
        var queued = new List<string> { @"C:\Users\Demo" };
        var d = ScanScheduler.Decide(@"C:\Users", false, @"C:\Temp", queued);
        Assert(d.Action == SchedulerAction.CancelQueuedAndQueue, "action is CancelQueuedAndQueue");
        Assert(d.CancelQueuedPaths.Count == 1 && ScanScheduler.IsSame(d.CancelQueuedPaths[0], @"C:\Users\Demo"),
            "drops the queued descendant");
    }

    private static void Test_NewRequestCoversQueuedAncestor_Dropped()
    {
        Console.WriteLine("Scenario: request C:\\Users\\Demo, queue has C:\\Users → drop the queued ancestor too");
        var queued = new List<string> { @"C:\Users" };
        var d = ScanScheduler.Decide(@"C:\Users\Demo", false, @"C:\Temp", queued);
        Assert(d.Action == SchedulerAction.CancelQueuedAndQueue, "action is CancelQueuedAndQueue");
        Assert(d.CancelQueuedPaths.Count == 1 && ScanScheduler.IsSame(d.CancelQueuedPaths[0], @"C:\Users"),
            "drops the queued ancestor");
    }

    private static void Test_NothingActive_ButSamePathQueued_AlreadyInProgress()
    {
        Console.WriteLine("Scenario: nothing active, but same path already queued → AlreadyInProgress");
        var queued = new List<string> { @"C:\Users" };
        var d = ScanScheduler.Decide(@"C:\Users", false, null, queued);
        Assert(d.Action == SchedulerAction.AlreadyInProgress, "action is AlreadyInProgress");
    }

    private static void Test_DifferentDriveNotChecked()
    {
        Console.WriteLine("Scenario: drive routing (unit-level — caller must pass same-drive data only)");
        // ScanScheduler.DriveRootOf is the helper the caller uses to partition.
        Assert(ScanScheduler.DriveRootOf(@"C:\Users") == @"c:", "drive of C:\\Users is c:");
        Assert(ScanScheduler.DriveRootOf(@"D:\Photos") == @"d:", "drive of D:\\Photos is d:");
        Assert(ScanScheduler.DriveRootOf(@"D:\Photos") != ScanScheduler.DriveRootOf(@"C:\Users"),
            "C: and D: map to different drive roots");
    }
}
