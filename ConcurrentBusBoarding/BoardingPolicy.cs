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
        internal const float MinimumCustomZoneLength = 6f;
        internal const float MaximumCustomZoneLength = 200f;
        internal const uint ResidentUpdateFrames = 16u;

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

        internal static float WaitingPosition(float start, float end, int direction, float unit)
        {
            float distanceFromFront = Clamp(unit, 0f, 1f);
            distanceFromFront *= distanceFromFront;
            return direction >= 0
                ? end - (end - start) * distanceFromFront
                : start + (end - start) * distanceFromFront;
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

        internal static bool CanBeginSyntheticBoarding(int activeBusCount)
        {
            return activeBusCount > 0;
        }

        internal static uint PassengerSelectionTurn(uint simulationFrame)
        {
            return simulationFrame / ResidentUpdateFrames;
        }

        internal static bool CanFinishBoarding(uint frame, uint departureFrame, float maxBoardingDistance,
            bool passengersReady)
        {
            return frame >= departureFrame && maxBoardingDistance == float.MaxValue && passengersReady;
        }

        internal static bool ShouldExposeBoardingToVehicleAi(bool usesNativeBoarding, bool selected)
        {
            return usesNativeBoarding && selected;
        }

        internal static bool ShouldCompleteManagedBoarding(bool usesNativeBoarding, bool selected)
        {
            return !usesNativeBoarding && selected;
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

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : value > max ? max : value;
        }

    }
}
