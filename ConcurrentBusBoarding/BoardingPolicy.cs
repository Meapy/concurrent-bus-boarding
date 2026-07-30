namespace ConcurrentBusBoarding
{
    internal static class BoardingPolicy
    {
        internal const int OrdinaryStopLimit = 2;
        internal const float BusGap = 1.5f;
        internal const float OrdinaryZoneLength = 26f;
        internal const float BoardingPositionTolerance = 2f;
        internal const float BoardingSpeedTolerance = 1f;
        internal const float BoardingHeadingTolerance = 0.9f;
        internal const float PhysicalLaneCaptureDistance = 40f;
        // How far a cim can reasonably be from a bus and still board it. This was briefly also used to
        // reject distant buses from admission, on the theory that they were unboardable. Measurement
        // disproved that - concurrent buses board and unload normally at zone distances - so the
        // admission gate was reverted.
        internal const float PassengerReachDistance = 20f;
        internal const float MinimumCustomZoneLength = 6f;
        internal const float MaximumCustomZoneLength = 200f;
        internal const uint ResidentUpdateFrames = 16u;
        internal const uint ManagedBoardingTimeoutFrames = 1800u;
        // How long the native boarding lifecycle is given to complete a session on its own before
        // the managed gates take over. Comfortably longer than a normal native dwell and far below
        // ManagedBoardingTimeoutFrames, so the deadline stays a last resort rather than the norm.
        internal const uint NativeCompletionGraceFrames = 256u;
        // Consecutive selected completion attempts with no change to this bus's passenger count
        // before its share of the exchange counts as finished.
        internal const byte IdleAttemptsBeforeDeparture = 3;
        // Longest a session keeps admitting new passengers before it closes its doors. At a busy
        // stop the arrival stream never stops on its own, so without a cap the bus keeps accepting
        // boarders, always has someone mid-transition, and can never satisfy the readiness gate.
        internal const uint BoardingWindowFrames = 128u;
        // Native StartBoarding can push m_DepartureFrame up to 4096 frames out. A managed session
        // must not inherit that: it holds the bus for minutes where a vanilla dwell is seconds, and
        // inflated dwell raises the line's measured waiting time, which is what the pathfinder uses
        // to decide whether a cim takes the bus at all.
        internal const uint ManagedDepartureFrames = 64u;

        // Time a bus is held beyond a normal dwell is time the game measures as line travel time,
        // because VehicleTiming.m_AverageTravelTime is derived from the gap between departures. That
        // average is then a floor on every segment's duration, so a held bus permanently inflates
        // the line's duration and the pathfinder prices the line out. The mod repays the excess.
        internal static float HeldTimeToRepay(uint frame, uint admittedFrame, uint nominalDwell)
        {
            if (admittedFrame == 0u || frame <= admittedFrame)
                return 0f;
            uint held = frame - admittedFrame;
            return held > nominalDwell ? held - nominalDwell : 0f;
        }

        internal static uint ClampManagedDeparture(uint frame, uint departureFrame)
        {
            uint latest = frame + ManagedDepartureFrames;
            return departureFrame > latest ? latest : departureFrame;
        }
        private const double SimulationFramesPerMinute = 182.044444444444;

        internal static bool IsPullInLane(bool secondaryLane, bool splitsFromRoad, bool mergesIntoRoad,
            bool sameRoadLaneTransition)
        {
            return secondaryLane || sameRoadLaneTransition || (splitsFromRoad && mergesIntoRoad);
        }

        internal static bool CanAdmit(
            bool customZone,
            bool pullInLane,
            int activeBusCount,
            float occupiedLength,
            float candidateLength,
            float zoneLength,
            bool candidateCloseToStop)
        {
            if (!candidateCloseToStop)
                return false;
            if (customZone)
                return true;
            if (!pullInLane)
                return activeBusCount < OrdinaryStopLimit;
            if (candidateLength <= 0f || zoneLength <= 0f)
                return false;

            return occupiedLength + candidateLength + activeBusCount * BusGap <= zoneLength;
        }

        internal static float PackedTarget(float start, float end, float laneLength, int direction,
            float usedLength, float vehicleLength)
        {
            if (laneLength <= 0f)
                return direction >= 0 ? end : start;
            float inset = (usedLength + vehicleLength * 0.5f) / laneLength;
            return direction >= 0 ? Clamp(end - inset, start, end) : Clamp(start + inset, start, end);
        }

        internal static bool IsAhead(float progress, float target, float laneLength, int direction)
        {
            return (target - progress) * direction * laneLength > BoardingPositionTolerance;
        }

        internal static bool CanProjectTarget(float distance, float maximumDistance, float tangentDot)
        {
            return distance <= maximumDistance && tangentDot >= 0.5f;
        }

        internal static bool IsSettledAtPackedPosition(bool approaching, float progress, float target,
            float laneLength, float speed, float headingDot)
        {
            float distance = (progress - target) * laneLength;
            if (distance < 0f)
                distance = -distance;
            return approaching && distance <= BoardingPositionTolerance &&
                speed <= BoardingSpeedTolerance && headingDot >= BoardingHeadingTolerance;
        }

        internal static bool ShouldDrawZone(bool selectedOnly, bool selected, bool editing)
        {
            return !selectedOnly || selected || editing;
        }

        internal static bool PreferZoneCandidate(float currentDistance, bool currentPullIn, bool currentPhysical,
            float candidateDistance, bool candidatePullIn, bool candidatePhysical)
        {
            if (currentPhysical != candidatePhysical)
                return candidatePhysical;
            return candidateDistance < currentDistance ||
                (candidateDistance == currentDistance && candidatePullIn && !currentPullIn);
        }

        internal static void GetZoneBounds(bool pullInLane, float stopPosition, float laneLength,
            int direction, bool customZone, float customOffset, float customLength,
            out float start, out float end)
        {
            if (laneLength <= 0f)
            {
                start = stopPosition;
                end = stopPosition;
                return;
            }

            float length = customZone ? customLength : pullInLane ? laneLength : OrdinaryZoneLength;
            float range = length < laneLength ? length / laneLength : 1f;
            if (direction >= 0)
            {
                start = stopPosition > range ? stopPosition - range : 0f;
                end = stopPosition;
            }
            else
            {
                start = stopPosition;
                end = stopPosition + range < 1f ? stopPosition + range : 1f;
            }
        }

        internal static int RotationIndex(int count, uint turn, uint salt)
        {
            return count <= 1 ? 0 : (int)((turn + salt) % (uint)count);
        }

        // The mod exists to resolve contention between buses at one stop. With a single bus there is
        // nothing to resolve, so taking it over only replaces a short native dwell with a longer
        // managed one. Admitting the lead unconditionally did exactly that at every stop on every
        // line, inflating round-trip times citywide.
        internal static bool ShouldEngageConcurrentBoarding(int busesAtStop)
        {
            return busesAtStop > 1;
        }

        internal static bool CanBeginSyntheticBoarding(int activeBusCount)
        {
            return activeBusCount > 0;
        }

        internal static bool ShouldRequestStop(bool canAdmit, bool boarding)
        {
            return canAdmit && !boarding;
        }

        // Departure is two-phase, as a real stop is. Phase one accepts passengers. Phase two closes
        // the doors so the cims already climbing aboard can finish, because a bus that never stops
        // admitting new boarders never reaches "all passengers ready".
        // Deliberately does NOT close on a settled passenger count. The native window starts closed
        // and widens each tick, so early attempts see no boarding simply because nobody is admitted
        // yet - treating that as "finished" made buses depart empty. Only the window cap closes the
        // doors, and it exists purely so a stalled ratchet cannot hold the bus forever.
        internal static bool ShouldCloseDoors(bool doorsClosing, uint frame, uint admittedFrame,
            uint windowFrames)
        {
            if (doorsClosing)
                return false;
            return admittedFrame != 0u && frame >= admittedFrame + windowFrames;
        }

        // Once the doors are shut the only remaining question is whether the in-flight boarders
        // have finished. The dwell and waiting-distance gates have already been satisfied by
        // definition, so re-testing them here would just reintroduce the stall.
        internal static bool CanDepartAfterDoorsClosed(bool passengersReady, bool timedOut)
        {
            return passengersReady || timedOut;
        }

        // Mirrors native StopBoarding: the ratchet is only finished once it has widened all the way
        // to float.MaxValue, meaning no waiting cim was left behind.
        internal static bool CanFinishBoarding(uint frame, uint departureFrame, float maxBoardingDistance,
            bool passengersReady, bool timedOut)
        {
            return frame >= departureFrame && maxBoardingDistance == float.MaxValue &&
                (passengersReady || timedOut);
        }

        internal static uint BoardingTimeoutFrames(int minutes)
        {
            return minutes > 0 && minutes <= 60
                ? (uint)(minutes * SimulationFramesPerMinute) + 1u
                : ManagedBoardingTimeoutFrames;
        }

        internal static bool HasBoardingTimedOut(uint frame, uint departureFrame, uint timeoutFrames)
        {
            return departureFrame != 0 && frame >= departureFrame &&
                frame - departureFrame >= timeoutFrames;
        }

        // A native boarding session must stay continuously visible to the car AI. Clearing the flag
        // between admission and completion makes the AI re-enter StartBoarding, which re-arms
        // m_DepartureFrame and prevents the bus from ever departing. Concurrency is expressed only
        // through the rotating passenger-facing BoardingVehicle slot.
        internal static bool ShouldExposeBoardingToVehicleAi(bool usesNativeBoarding)
        {
            return usesNativeBoarding;
        }

        // Unconditional session deadline. This deliberately does not read m_DepartureFrame, because
        // that field is the one a re-entered native StartBoarding corrupts.
        internal static bool HasSessionExpired(uint frame, uint admittedFrame, uint timeoutFrames)
        {
            return admittedFrame != 0u && frame > admittedFrame && frame - admittedFrame >= timeoutFrames;
        }

        // A follower is held stationary short of its native path end, and installed IL shows native
        // StopBoarding is only reached after PathEndReached. So for a follower the native lifecycle
        // can never complete, whatever its flags say. Give the native path a grace window - long
        // enough for a genuine lead bus that did reach its endpoint - then complete the session on
        // the managed passenger and dwell gates instead of letting it run to the dwell deadline.
        internal static bool ShouldCompleteManagedBoarding(bool usesNativeBoarding, bool selected,
            uint frame, uint admittedFrame, uint graceFrames)
        {
            if (!selected)
                return false;
            if (!usesNativeBoarding)
                return true;
            return admittedFrame != 0u && frame >= admittedFrame + graceFrames;
        }

        internal static bool ShouldAdoptNativeBoarding(
            bool usesNativeBoarding, bool selected, bool boardingAfterVehicleAi)
        {
            return !usesNativeBoarding && selected && boardingAfterVehicleAi;
        }

        internal static bool CanRestoreRoute(bool validRoute, bool validTarget, bool retiring)
        {
            return validRoute && validTarget && !retiring;
        }

        internal static float TransitCostMultiplier(int attractiveness)
        {
            return attractiveness < 50 || attractiveness > 200 ? 1f : 100f / attractiveness;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

    }
}
