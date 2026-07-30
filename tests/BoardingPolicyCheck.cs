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
        // The waiting-band and waiting-position helpers were removed with the passenger spread. The
        // spread could never move a cim: Creature.m_QueueArea is honoured as a bound but ignored as a
        // position, and HumanNavigation.m_TargetPosition is owned by HumanNavigationSystem.
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
        Expect(!BoardingPolicy.ShouldEngageConcurrentBoarding(0),
            "an empty stop needs no concurrent boarding");
        Expect(!BoardingPolicy.ShouldEngageConcurrentBoarding(1),
            "a single bus is left entirely to native AI");
        Expect(BoardingPolicy.ShouldEngageConcurrentBoarding(2),
            "two buses at one stop are what the mod exists to resolve");
        Expect(!BoardingPolicy.CanBeginSyntheticBoarding(0),
            "first bus must use the native boarding lifecycle");
        Expect(BoardingPolicy.CanBeginSyntheticBoarding(1),
            "a following bus can use managed boarding");
        Expect(BoardingPolicy.ShouldRequestStop(true, false),
            "eligible approaching bus requests its target stop");
        Expect(!BoardingPolicy.ShouldRequestStop(false, false),
            "bus outside available zone does not request a stop");
        Expect(!BoardingPolicy.ShouldRequestStop(true, true),
            "boarding bus does not repeat the stop request");
        Expect(!BoardingPolicy.CanFinishBoarding(99, 100, float.MaxValue, true, false),
            "boarding dwell must finish");
        Expect(!BoardingPolicy.CanFinishBoarding(100, 100, 12f, true, false),
            "a partly widened ratchet still has waiting cims to admit");
        Expect(!BoardingPolicy.CanFinishBoarding(100, 100, 0f, true, false),
            "a session cannot finish before its window has opened at all");
        Expect(!BoardingPolicy.CanFinishBoarding(100, 100, float.MaxValue, false, false),
            "onboard transitions must finish");
        Expect(BoardingPolicy.CanFinishBoarding(100, 100, float.MaxValue, true, false),
            "a fully widened ratchet means nobody was left behind, so the bus can leave");
        Expect(BoardingPolicy.CanFinishBoarding(100, 100, float.MaxValue, false, true),
            "timed-out follower can leave despite a stuck passenger transition");
        Expect(BoardingPolicy.ClampManagedDeparture(100, 4196) == 100 + BoardingPolicy.ManagedDepartureFrames,
            "a native far-future departure frame is clamped for a managed session");
        Expect(BoardingPolicy.ClampManagedDeparture(100, 120) == 120,
            "a departure frame already within the managed dwell is left alone");
        Expect(BoardingPolicy.BoardingWindowFrames < BoardingPolicy.ManagedBoardingTimeoutFrames,
            "the boarding window must close well before the dwell deadline");
        Expect(!BoardingPolicy.ShouldCloseDoors(false, 611, 100, 512),
            "the window stays open while the ratchet is still widening");
        Expect(BoardingPolicy.ShouldCloseDoors(false, 612, 100, 512),
            "a stalled ratchet must still close its doors when the window elapses");
        Expect(!BoardingPolicy.ShouldCloseDoors(true, 1000, 100, 512),
            "doors are only closed once per session");
        Expect(BoardingPolicy.BoardingWindowFrames < BoardingPolicy.ManagedBoardingTimeoutFrames,
            "the boarding window must close well before the dwell deadline");
        Expect(BoardingPolicy.CanDepartAfterDoorsClosed(true, false),
            "a closed bus leaves once its boarders are ready");
        Expect(!BoardingPolicy.CanDepartAfterDoorsClosed(false, false),
            "a closed bus still waits for cims already climbing aboard");
        Expect(BoardingPolicy.CanDepartAfterDoorsClosed(false, true),
            "a closed bus leaves anyway once it has timed out");
        Expect(BoardingPolicy.BoardingTimeoutFrames(2) == 365,
            "All Aboard's configured minutes use its exact simulation rate");
        Expect(BoardingPolicy.BoardingTimeoutFrames(0) == BoardingPolicy.ManagedBoardingTimeoutFrames,
            "invalid dwell setting uses the managed fallback");
        Expect(!BoardingPolicy.HasBoardingTimedOut(99, 100, 1),
            "timeout cannot occur before scheduled departure");
        Expect(!BoardingPolicy.HasBoardingTimedOut(464, 100, 365),
            "configured dwell delay remains available");
        Expect(BoardingPolicy.HasBoardingTimedOut(465, 100, 365),
            "configured dwell delay eventually releases the follower");
        Expect(BoardingPolicy.ShouldExposeBoardingToVehicleAi(true),
            "native session stays continuously visible to vehicle AI");
        Expect(!BoardingPolicy.ShouldExposeBoardingToVehicleAi(false),
            "synthetic session never enters native completion");
        Expect(!BoardingPolicy.HasSessionExpired(1000, 0, 1800),
            "a session without an admission frame cannot expire");
        Expect(!BoardingPolicy.HasSessionExpired(1899, 100, 1800),
            "a session within the configured dwell is retained");
        Expect(BoardingPolicy.HasSessionExpired(1900, 100, 1800),
            "a session past the configured dwell is always released");
        Expect(BoardingPolicy.HasSessionExpired(465, 100, BoardingPolicy.BoardingTimeoutFrames(2)),
            "the deadline honours All Aboard's configured dwell");
        Expect(BoardingPolicy.ShouldCompleteManagedBoarding(false, true, 100, 100, 256),
            "selected synthetic session uses managed completion immediately");
        Expect(!BoardingPolicy.ShouldCompleteManagedBoarding(false, false, 100, 100, 256),
            "an unselected session never completes");
        Expect(!BoardingPolicy.ShouldCompleteManagedBoarding(true, true, 355, 100, 256),
            "a native session keeps its grace window");
        Expect(BoardingPolicy.ShouldCompleteManagedBoarding(true, true, 356, 100, 256),
            "a follower the native lifecycle cannot finish falls back to managed completion");
        Expect(BoardingPolicy.NativeCompletionGraceFrames < BoardingPolicy.ManagedBoardingTimeoutFrames,
            "managed completion must be tried well before the dwell deadline");
        Expect(BoardingPolicy.ShouldAdoptNativeBoarding(false, true, true),
            "vehicle AI can replace a selected synthetic session with a native session");
        Expect(!BoardingPolicy.ShouldAdoptNativeBoarding(false, false, true),
            "unselected session cannot claim native adoption");
        Expect(BoardingPolicy.CanRestoreRoute(true, true, false), "valid active route can be restored");
        Expect(!BoardingPolicy.CanRestoreRoute(true, false, false), "stale target blocks route restoration");
        Expect(!BoardingPolicy.CanRestoreRoute(true, true, true), "retiring bus blocks route restoration");
        Expect(Near(BoardingPolicy.TransitCostMultiplier(50), 2f), "minimum attractiveness doubles cost");
        Expect(Near(BoardingPolicy.TransitCostMultiplier(100), 1f), "vanilla attractiveness preserves cost");
        Expect(Near(BoardingPolicy.TransitCostMultiplier(200), 0.5f), "maximum attractiveness halves cost");
        Expect(Near(BoardingPolicy.TransitCostMultiplier(0), 1f), "invalid attractiveness preserves cost");
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
