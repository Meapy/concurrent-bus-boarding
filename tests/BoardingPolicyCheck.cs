using System;
using ConcurrentBusBoarding;

internal static class BoardingPolicyCheck
{
    private static int Main()
    {
        Expect(BoardingPolicy.IsPullInLane(true, false, false, false), "secondary pull-in lane");
        Expect(BoardingPolicy.IsPullInLane(false, true, true, false), "branch-and-rejoin pull-in lane");
        Expect(BoardingPolicy.IsPullInLane(false, false, false, true), "same-road lane transition");
        Expect(!BoardingPolicy.IsPullInLane(false, true, false, false), "intersection split is not a pull-in lane");
        Expect(!BoardingPolicy.IsPullInLane(false, false, true, false), "intersection merge is not a pull-in lane");
        Expect(!BoardingPolicy.IsPullInLane(false, false, false, false), "ordinary road lane");
        Expect(BoardingPolicy.CanAdmit(false, false, 0, 0, 12, 26, true), "ordinary first bus");
        Expect(BoardingPolicy.CanAdmit(false, false, 1, 12, 12, 26, true), "ordinary second bus");
        Expect(BoardingPolicy.CanAdmit(false, false, 1, 16, 16, 26, true), "ordinary trusts native spacing");
        Expect(!BoardingPolicy.CanAdmit(false, false, 2, 0, 12, 26, true), "ordinary hard cap");
        Expect(!BoardingPolicy.CanAdmit(false, false, 0, 0, 12, 26, false), "ordinary proximity");
        Expect(!BoardingPolicy.CanAdmit(false, true, 1, 12, 12, 40, false), "pull-in proximity");
        Expect(BoardingPolicy.CanAdmit(false, true, 1, 12, 12, 40, true), "pull-in available length");
        Expect(!BoardingPolicy.CanAdmit(false, true, 2, 28, 12, 40, true), "pull-in insufficient length");
        Expect(BoardingPolicy.CanAdmit(true, false, 20, 200, 12, 20, true), "custom zone admits every contained bus");
        Expect(!BoardingPolicy.CanAdmit(true, false, 0, 0, 12, 20, false), "custom zone still requires containment");
        Expect(BoardingPolicy.IsReady(true, 1f), "close and settling");
        Expect(!BoardingPolicy.IsReady(false, 0f), "not close to stop");
        Expect(!BoardingPolicy.IsReady(true, 1.01f), "moving bus");
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, 1, 0f, 12f), 0.74f),
            "increasing first packed target");
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, 1, 13.5f, 12f), 0.605f),
            "increasing next packed target");
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, -1, 0f, 12f), 0.26f),
            "decreasing first packed target");
        Expect(BoardingPolicy.IsAhead(0.5f, 0.74f, 100f, 1), "increasing target is ahead");
        Expect(BoardingPolicy.IsAhead(0.5f, 0.26f, 100f, -1), "decreasing target is ahead");
        Expect(!BoardingPolicy.IsAhead(0.75f, 0.74f, 100f, 1), "never reverse to packed target");
        Expect(BoardingPolicy.ShouldDrawZone(false, false, false), "show all overlay mode");
        Expect(!BoardingPolicy.ShouldDrawZone(true, false, false), "hide unselected overlay");
        Expect(BoardingPolicy.ShouldDrawZone(true, true, false), "show selected overlay");
        Expect(BoardingPolicy.ShouldDrawZone(true, false, true), "show editing overlay");
        Expect(BoardingPolicy.PreferZoneCandidate(20f, false, false, 2f, false, false), "lane nearest the stop wins");
        Expect(!BoardingPolicy.PreferZoneCandidate(2f, false, false, 20f, true, false), "distant junction cannot replace stop lane");
        Expect(BoardingPolicy.PreferZoneCandidate(2f, false, false, 2f, true, false), "equally close pull-in lane wins");
        Expect(BoardingPolicy.PreferZoneCandidate(2f, false, false, 5f, true, true), "nearby physical pull-in lane wins");
        Expect(!BoardingPolicy.PreferZoneCandidate(5f, true, true, 2f, false, false), "inferred lane cannot replace physical lane");
        float start;
        float end;
        BoardingPolicy.GetZoneBounds(false, 0.75f, 100f, 1, false, 0f, 0f, out start, out end);
        Expect(Near(start, 0.49f) && Near(end, 0.75f), "increasing lane extends behind stop");
        BoardingPolicy.GetZoneBounds(false, 0.25f, 100f, -1, false, 0f, 0f, out start, out end);
        Expect(Near(start, 0.25f) && Near(end, 0.51f), "decreasing lane extends behind stop");
        BoardingPolicy.GetZoneBounds(true, 0.5f, 100f, 1, false, 0f, 0f, out start, out end);
        Expect(start == 0f && end == 1f, "pull-in uses full lane");
        BoardingPolicy.GetZoneBounds(false, 0.5f, 100f, 1, true, -10f, 40f, out start, out end);
        Expect(Near(start, 0.2f) && Near(end, 0.6f), "custom zone moves and sizes around its centre");
        BoardingPolicy.GetZoneBounds(false, 0.5f, 0f, 1, true, 0f, 40f, out start, out end);
        Expect(start == 0.5f && end == 0.5f, "invalid custom lane has no boarding area");
        Expect(BoardingPolicy.RotationIndex(3, 0, 0) == 0, "rotation start");
        Expect(BoardingPolicy.RotationIndex(3, 1, 0) == 1, "rotation advance");
        int[] split = new int[3];
        for (uint turn = 0; turn < 10; turn++)
            split[BoardingPolicy.RotationIndex(split.Length, turn, 0)]++;
        Expect(split[0] == 4 && split[1] == 3 && split[2] == 3, "passengers split round-robin");
        Console.WriteLine("Boarding policy checks passed.");
        return 0;
    }

    private static void Expect(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException("Failed: " + name);
    }

    private static bool Near(float actual, float expected)
    {
        return Math.Abs(actual - expected) < 0.0001f;
    }

}
