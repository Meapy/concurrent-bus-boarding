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
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, 1, 0f, 12f), 0.74f),
            "increasing first packed target");
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, 1, 13.5f, 12f), 0.605f),
            "increasing next packed target");
        Expect(Near(BoardingPolicy.PackedTarget(0.2f, 0.8f, 100f, -1, 0f, 12f), 0.26f),
            "decreasing first packed target");
        Expect(BoardingPolicy.IsAhead(0.5f, 0.74f, 100f, 1), "increasing target is ahead");
        Expect(BoardingPolicy.IsAhead(0.5f, 0.26f, 100f, -1), "decreasing target is ahead");
        Expect(!BoardingPolicy.IsAhead(0.75f, 0.74f, 100f, 1), "never reverse to packed target");
        Expect(BoardingPolicy.CanProjectTarget(3f, 5f, 0.9f), "near aligned navigation lane");
        Expect(!BoardingPolicy.CanProjectTarget(6f, 5f, 0.9f), "distant navigation lane");
        Expect(!BoardingPolicy.CanProjectTarget(3f, 5f, 0.49f), "misaligned navigation lane");
        Expect(BoardingPolicy.IsSettledAtPackedPosition(true, 0.49f, 0.5f, 100f, 1f, 0.9f),
            "stopped aligned approach settles at packed target");
        Expect(!BoardingPolicy.IsSettledAtPackedPosition(true, 0.47f, 0.5f, 100f, 0f, 1f),
            "distant approach cannot settle");
        Expect(!BoardingPolicy.IsSettledAtPackedPosition(true, 0.5f, 0.5f, 100f, 1.01f, 1f),
            "moving approach cannot settle");
        Expect(!BoardingPolicy.IsSettledAtPackedPosition(true, 0.5f, 0.5f, 100f, 0f, 0.89f),
            "misaligned approach cannot settle");
        Expect(!BoardingPolicy.IsSettledAtPackedPosition(false, 0.5f, 0.5f, 100f, 0f, 1f),
            "unassigned bus cannot settle");
        Expect(BoardingPolicy.ShouldDrawZone(false, false, false), "show all overlay mode");
        Expect(!BoardingPolicy.ShouldDrawZone(true, false, false), "hide unselected overlay");
        Expect(BoardingPolicy.ShouldDrawZone(true, true, false), "show selected overlay");
        Expect(BoardingPolicy.ShouldDrawZone(true, false, true), "show editing overlay");
        Expect(Near(BoardingPolicy.WaitingPosition(0.2f, 0.8f, 1, 0f), 0.8f),
            "increasing waiting starts at zone front");
        Expect(Near(BoardingPolicy.WaitingPosition(0.2f, 0.8f, -1, 0f), 0.2f),
            "decreasing waiting starts at zone front");
        Expect(Near(BoardingPolicy.WaitingPosition(0.2f, 0.8f, 1, 0.5f), 0.65f),
            "waiting crowd is biased toward the front");
        Expect(Near(BoardingPolicy.WaitingPosition(0.2f, 0.8f, -1, 1f), 0.8f),
            "waiting crowd still spans the full zone");
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
        Expect(start == 0f && Near(end, 0.5f), "pull-in ends at increasing-direction stop");
        BoardingPolicy.GetZoneBounds(false, 0.5f, 100f, 1, true, -10f, 40f, out start, out end);
        Expect(Near(start, 0.1f) && Near(end, 0.5f), "custom zone ignores legacy offset and ends at stop");
        BoardingPolicy.GetZoneBounds(false, 0.5f, 100f, -1, true, 10f, 40f, out start, out end);
        Expect(Near(start, 0.5f) && Near(end, 0.9f), "decreasing custom zone ends at stop");
        BoardingPolicy.GetZoneBounds(false, 0.5f, 0f, 1, true, 0f, 40f, out start, out end);
        Expect(start == 0.5f && end == 0.5f, "invalid custom lane has no boarding area");
        Expect(BoardingPolicy.RotationIndex(3, 0, 0) == 0, "rotation start");
        Expect(BoardingPolicy.RotationIndex(3, 1, 0) == 1, "rotation advance");
        Expect(BoardingPolicy.PassengerSelectionTurn(0) == 0, "passenger selection starts at first sweep");
        Expect(BoardingPolicy.PassengerSelectionTurn(15) == 0,
            "passenger selection stays fixed for every resident partition");
        Expect(BoardingPolicy.PassengerSelectionTurn(16) == 1,
            "passenger selection advances after a complete resident sweep");
        for (uint frame = 0; frame < BoardingPolicy.ResidentUpdateFrames; frame++)
            Expect(BoardingPolicy.RotationIndex(2, BoardingPolicy.PassengerSelectionTurn(frame), 0) == 0,
                "all first-sweep resident partitions see the lead bus");
        for (uint frame = BoardingPolicy.ResidentUpdateFrames;
             frame < BoardingPolicy.ResidentUpdateFrames * 2;
             frame++)
            Expect(BoardingPolicy.RotationIndex(2, BoardingPolicy.PassengerSelectionTurn(frame), 0) == 1,
                "all second-sweep resident partitions see the following bus");
        Expect(!BoardingPolicy.CanFinishBoarding(99, 100, float.MaxValue, true), "boarding dwell must finish");
        Expect(!BoardingPolicy.CanFinishBoarding(100, 100, 12f, true), "waiting passengers must finish");
        Expect(!BoardingPolicy.CanFinishBoarding(100, 100, float.MaxValue, false), "onboard transitions must finish");
        Expect(BoardingPolicy.CanFinishBoarding(100, 100, float.MaxValue, true), "completed follower can leave");
        Expect(BoardingPolicy.ShouldExposeBoardingToVehicleAi(true, true),
            "selected native session remains visible to vehicle AI");
        Expect(!BoardingPolicy.ShouldExposeBoardingToVehicleAi(false, true),
            "synthetic session never enters native completion");
        Expect(!BoardingPolicy.ShouldExposeBoardingToVehicleAi(true, false),
            "unselected native session stays out of vehicle AI");
        Expect(BoardingPolicy.ShouldCompleteManagedBoarding(false, true),
            "selected synthetic session uses managed completion");
        Expect(!BoardingPolicy.ShouldCompleteManagedBoarding(true, true),
            "native session never uses synthetic completion");
        Expect(BoardingPolicy.ShouldAdoptNativeBoarding(false, true, true),
            "vehicle AI can replace a selected synthetic session with a native session");
        Expect(!BoardingPolicy.ShouldAdoptNativeBoarding(false, false, true),
            "unselected session cannot claim native adoption");
        Expect(BoardingPolicy.CanRestoreRoute(true, true, false), "valid active route can be restored");
        Expect(!BoardingPolicy.CanRestoreRoute(true, false, false), "stale target blocks route restoration");
        Expect(!BoardingPolicy.CanRestoreRoute(true, true, true), "retiring bus blocks route restoration");
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
