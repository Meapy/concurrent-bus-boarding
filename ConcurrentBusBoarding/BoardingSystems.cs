using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using NetCarLane = Game.Net.CarLane;
using NetCarLaneFlags = Game.Net.CarLaneFlags;
using NetSecondaryLane = Game.Net.SecondaryLane;
using NetSlaveLane = Game.Net.SlaveLane;
using NetSlaveLaneFlags = Game.Net.SlaveLaneFlags;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace ConcurrentBusBoarding
{
    internal struct BoardingZone
    {
        internal Entity Lane;
        internal Curve Curve;
        internal float CurvePosition;
        internal float Width;
        internal bool IsPullIn;
        internal int Direction;
        internal bool IsCustom;
        internal float CustomOffset;
        internal float CustomLength;
        internal float StopDistance;
        internal bool IsPhysical;
    }

    internal struct ConcurrentBoardingActive : IComponentData
    {
        internal byte SelectedForVehicleAi;
    }

    internal struct BoardingZoneApproach : IComponentData
    {
    }

    internal struct BoardingZoneBus
    {
        internal Entity Entity;
        internal float Length;
        internal float Progress;
    }

    public partial class ConcurrentBoardingSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private EntityQuery m_Stops;
        private SimulationSystem m_SimulationSystem;
        private uint m_Turn;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;
        public override int GetUpdateOffset(SystemUpdatePhase phase) => 1;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadOnly<VehiclePublicTransport>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>());
            m_Stops = GetEntityQuery(ComponentType.ReadWrite<BoardingVehicle>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            RequireForUpdate(m_Buses);
            RequireForUpdate(m_Stops);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            var busesByStop = new Dictionary<Entity, List<Entity>>();
            var zones = new Dictionary<Entity, BoardingZone>();

            using (NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity bus in buses)
                {
                    if (!BoardingHelpers.IsBus(EntityManager, bus))
                        continue;

                    bool managed = EntityManager.HasComponent<ConcurrentBoardingActive>(bus);
                    if (!BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        if (managed)
                            EntityManager.RemoveComponent<ConcurrentBoardingActive>(bus);
                        continue;
                    }

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    const PublicTransportFlags approaching = PublicTransportFlags.EnRoute |
                        PublicTransportFlags.Arriving | PublicTransportFlags.Testing |
                        PublicTransportFlags.Boarding | PublicTransportFlags.RequireStop;
                    if (!managed && (transport.m_State & approaching) == 0)
                        continue;

                    Add(busesByStop, stop, bus);
                    BoardingHelpers.ObserveZone(EntityManager, zones, stop, bus);
                }
            }

            foreach (KeyValuePair<Entity, List<Entity>> entry in busesByStop)
            {
                Entity stop = entry.Key;
                bool hasZone = zones.TryGetValue(stop, out BoardingZone zone);
                bool pullIn = hasZone && zone.IsPullIn;
                var activeBuses = new List<Entity>();
                float occupiedLength = 0f;
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);

                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus))
                    {
                        activeBuses.Add(bus);
                        occupiedLength += BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    }
                }

                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus) ||
                        EntityManager.HasComponent<BoardingZoneApproach>(bus))
                        continue;
                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    if ((transport.m_State & PublicTransportFlags.Boarding) == 0)
                        continue;

                    bool closeToStop = hasZone && BoardingHelpers.IsCloseToStop(EntityManager, bus, zone);
                    float candidateLength = BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    if (!hasZone)
                        continue;
                    if (!BoardingPolicy.CanAdmit(zone.IsCustom, pullIn, activeBuses.Count, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), closeToStop))
                        continue;

                    EntityManager.AddComponent<ConcurrentBoardingActive>(bus);
                    activeBuses.Add(bus);
                    occupiedLength += candidateLength;
                }

                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus) ||
                        EntityManager.HasComponent<BoardingZoneApproach>(bus))
                        continue;

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    bool closeToStop = hasZone && BoardingHelpers.IsCloseToStop(EntityManager, bus, zone);
                    float speed = BoardingHelpers.GetSpeed(EntityManager, bus);
                    if (!BoardingPolicy.IsReady(closeToStop, speed))
                        continue;
                    float candidateLength = BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    if (!BoardingPolicy.CanAdmit(zone.IsCustom, pullIn, activeBuses.Count, occupiedLength, candidateLength,
                        BoardingHelpers.GetZoneLength(zone), closeToStop))
                        continue;

                    BeginBoarding(bus);
                    EntityManager.AddComponent<ConcurrentBoardingActive>(bus);
                    activeBuses.Add(bus);
                    occupiedLength += candidateLength;
                    if (slot.m_Testing == bus)
                        slot.m_Testing = Entity.Null;
                }

                if (activeBuses.Count == 0)
                {
                    EntityManager.SetComponentData(stop, slot);
                    continue;
                }

                Entity selected = activeBuses[BoardingPolicy.RotationIndex(activeBuses.Count, m_Turn, (uint)stop.Index)];
                foreach (Entity bus in activeBuses)
                    PrepareForVehicleAi(bus, bus == selected);

                if (slot.m_Vehicle == Entity.Null || BoardingHelpers.IsBus(EntityManager, slot.m_Vehicle))
                    slot.m_Vehicle = selected;
                if (slot.m_Testing != Entity.Null &&
                    EntityManager.HasComponent<ConcurrentBoardingActive>(slot.m_Testing))
                    slot.m_Testing = Entity.Null;
                EntityManager.SetComponentData(stop, slot);
            }

            m_Turn++;
        }

        private void BeginBoarding(Entity bus)
        {
            VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
            transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
            transport.m_State |= PublicTransportFlags.EnRoute | PublicTransportFlags.Boarding;
            transport.m_DepartureFrame = math.max(transport.m_DepartureFrame, m_SimulationSystem.frameIndex + 64u);
            transport.m_MaxBoardingDistance = 0f;
            transport.m_MinWaitingDistance = float.MaxValue;
            EntityManager.SetComponentData(bus, transport);
        }

        private void PrepareForVehicleAi(Entity bus, bool selected)
        {
            VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
            transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
            transport.m_State |= PublicTransportFlags.EnRoute;
            if (selected)
                transport.m_State |= PublicTransportFlags.Boarding;
            else
                transport.m_State &= ~PublicTransportFlags.Boarding;
            EntityManager.SetComponentData(bus, transport);
            EntityManager.SetComponentData(bus, new ConcurrentBoardingActive
            {
                SelectedForVehicleAi = selected ? (byte)1 : (byte)0
            });
        }

        private static void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
                groups.Add(stop, list = new List<Entity>());
            list.Add(bus);
        }
    }

    [UpdateBefore(typeof(ConcurrentBoardingSystem))]
    [UpdateBefore(typeof(TransportCarAISystem))]
    public partial class BoardingZoneApproachSystem : GameSystemBase
    {
        private EntityQuery m_Buses;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;
        public override int GetUpdateOffset(SystemUpdatePhase phase) => 1;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadOnly<VehiclePublicTransport>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>());
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            var busesByStop = new Dictionary<Entity, List<Entity>>();
            var zones = new Dictionary<Entity, BoardingZone>();
            using (NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity bus in buses)
                {
                    if (!BoardingHelpers.IsBus(EntityManager, bus))
                        continue;
                    bool approachingZone = EntityManager.HasComponent<BoardingZoneApproach>(bus);
                    if (!BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        if (approachingZone)
                            EntityManager.RemoveComponent<BoardingZoneApproach>(bus);
                        continue;
                    }
                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    const PublicTransportFlags approaching = PublicTransportFlags.EnRoute |
                        PublicTransportFlags.Arriving | PublicTransportFlags.Testing |
                        PublicTransportFlags.Boarding | PublicTransportFlags.RequireStop;
                    if (!approachingZone && !EntityManager.HasComponent<ConcurrentBoardingActive>(bus) &&
                        (transport.m_State & approaching) == 0)
                        continue;
                    Add(busesByStop, stop, bus);
                    BoardingHelpers.ObserveZone(EntityManager, zones, stop, bus);
                }
            }

            foreach (KeyValuePair<Entity, List<Entity>> entry in busesByStop)
            {
                Entity stop = entry.Key;
                if (!zones.TryGetValue(stop, out BoardingZone zone) ||
                    !EntityManager.HasComponent<BoardingVehicle>(stop))
                {
                    foreach (Entity bus in entry.Value)
                        ClearApproach(bus);
                    continue;
                }

                var buses = new List<BoardingZoneBus>(entry.Value.Count);
                foreach (Entity bus in entry.Value)
                {
                    if (!BoardingHelpers.IsCloseToStop(EntityManager, bus, zone))
                    {
                        ClearApproach(bus);
                        continue;
                    }
                    Transform transform = EntityManager.GetComponentData<Transform>(bus);
                    MathUtils.Distance(zone.Curve.m_Bezier, transform.m_Position, out float progress);
                    buses.Add(new BoardingZoneBus
                    {
                        Entity = bus,
                        Length = BoardingHelpers.GetVehicleLength(EntityManager, bus),
                        Progress = progress
                    });
                }
                buses.Sort((left, right) => zone.Direction >= 0
                    ? right.Progress.CompareTo(left.Progress)
                    : left.Progress.CompareTo(right.Progress));

                float2 bounds = BoardingHelpers.GetZoneBounds(zone);
                float zoneLength = BoardingHelpers.GetZoneLength(zone);
                float usedLength = 0f;
                int accepted = 0;
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
                foreach (BoardingZoneBus bus in buses)
                {
                    if (bus.Length <= 0f || usedLength + bus.Length > zoneLength ||
                        (!zone.IsCustom && !zone.IsPullIn && accepted >= BoardingPolicy.OrdinaryStopLimit))
                    {
                        ClearApproach(bus.Entity);
                        continue;
                    }

                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus.Entity))
                    {
                        ClearApproach(bus.Entity);
                    }
                    else
                    {
                        float target = BoardingPolicy.PackedTarget(bounds.x, bounds.y, zone.Curve.m_Length,
                            zone.Direction, usedLength, bus.Length);
                        if (BoardingPolicy.IsAhead(bus.Progress, target, zone.Curve.m_Length, zone.Direction) &&
                            TryMoveTargetForward(bus.Entity, zone, target))
                        {
                            if (!EntityManager.HasComponent<BoardingZoneApproach>(bus.Entity))
                                EntityManager.AddComponent<BoardingZoneApproach>(bus.Entity);
                            VehiclePublicTransport transport =
                                EntityManager.GetComponentData<VehiclePublicTransport>(bus.Entity);
                            transport.m_State &= ~(PublicTransportFlags.Boarding | PublicTransportFlags.Testing |
                                PublicTransportFlags.RequireStop);
                            transport.m_State |= PublicTransportFlags.EnRoute;
                            EntityManager.SetComponentData(bus.Entity, transport);
                            if (slot.m_Vehicle == bus.Entity)
                                slot.m_Vehicle = Entity.Null;
                            if (slot.m_Testing == bus.Entity)
                                slot.m_Testing = Entity.Null;
                        }
                        else
                        {
                            ClearApproach(bus.Entity);
                        }
                    }
                    usedLength += bus.Length + BoardingPolicy.BusGap;
                    accepted++;
                }
                EntityManager.SetComponentData(stop, slot);
            }
        }

        private bool TryMoveTargetForward(Entity bus, BoardingZone zone, float target)
        {
            if (EntityManager.HasBuffer<CarNavigationLane>(bus))
            {
                DynamicBuffer<CarNavigationLane> navigation = EntityManager.GetBuffer<CarNavigationLane>(bus);
                if (navigation.Length > 0)
                {
                    int index = navigation.Length - 1;
                    CarNavigationLane last = navigation[index];
                    if (last.m_Lane == zone.Lane &&
                        (last.m_CurvePosition.y >= last.m_CurvePosition.x ? 1 : -1) == zone.Direction &&
                        (last.m_Flags & Game.Vehicles.CarLaneFlags.EndOfPath) != 0)
                    {
                        last.m_CurvePosition.y = target;
                        last.m_Flags &= ~Game.Vehicles.CarLaneFlags.EndReached;
                        navigation[index] = last;
                        return true;
                    }
                }
            }

            if (!EntityManager.HasComponent<CarCurrentLane>(bus))
                return false;
            CarCurrentLane current = EntityManager.GetComponentData<CarCurrentLane>(bus);
            if (current.m_Lane != zone.Lane ||
                (current.m_CurvePosition.z >= current.m_CurvePosition.x ? 1 : -1) != zone.Direction)
                return false;
            current.m_CurvePosition.z = target;
            current.m_LaneFlags &= ~Game.Vehicles.CarLaneFlags.EndReached;
            EntityManager.SetComponentData(bus, current);
            return true;
        }

        private void ClearApproach(Entity bus)
        {
            if (EntityManager.HasComponent<BoardingZoneApproach>(bus))
                EntityManager.RemoveComponent<BoardingZoneApproach>(bus);
        }

        private static void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
                groups.Add(stop, list = new List<Entity>());
            list.Add(bus);
        }
    }

    [UpdateAfter(typeof(TransportCarAISystem))]
    [UpdateBefore(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(ResidentAISystem))]
    public partial class PassengerDistributionSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private uint m_Turn;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadWrite<ConcurrentBoardingActive>(),
                ComponentType.ReadOnly<VehiclePublicTransport>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>());
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            var boarding = new Dictionary<Entity, List<Entity>>();
            using (NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity bus in buses)
                {
                    if (!BoardingHelpers.IsBus(EntityManager, bus) ||
                        !BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        EntityManager.RemoveComponent<ConcurrentBoardingActive>(bus);
                        continue;
                    }

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
                    ConcurrentBoardingActive active = EntityManager.GetComponentData<ConcurrentBoardingActive>(bus);
                    if (active.SelectedForVehicleAi != 0 &&
                        (transport.m_State & PublicTransportFlags.Boarding) == 0 &&
                        slot.m_Vehicle != bus)
                    {
                        EntityManager.RemoveComponent<ConcurrentBoardingActive>(bus);
                        continue;
                    }

                    transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
                    transport.m_State |= PublicTransportFlags.EnRoute | PublicTransportFlags.Boarding;
                    EntityManager.SetComponentData(bus, transport);
                    Add(boarding, stop, bus);
                }
            }

            foreach (KeyValuePair<Entity, List<Entity>> entry in boarding)
            {
                Entity stop = entry.Key;
                if (!EntityManager.HasComponent<BoardingVehicle>(stop))
                    continue;
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
                if (slot.m_Vehicle != Entity.Null && !BoardingHelpers.IsBus(EntityManager, slot.m_Vehicle))
                    continue;
                slot.m_Vehicle = entry.Value[BoardingPolicy.RotationIndex(entry.Value.Count, m_Turn, (uint)stop.Index)];
                EntityManager.SetComponentData(stop, slot);
            }

            m_Turn++;
        }

        private static void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
                groups.Add(stop, list = new List<Entity>());
            list.Add(bus);
        }
    }

    [UpdateAfter(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(CarMoveSystem))]
    public partial class BoardingHoldSystem : GameSystemBase
    {
        private EntityQuery m_Buses;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadOnly<ConcurrentBoardingActive>(),
                ComponentType.ReadWrite<CarNavigation>(),
                ComponentType.ReadWrite<Moving>());
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            using NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp);
            foreach (Entity bus in buses)
            {
                CarNavigation navigation = EntityManager.GetComponentData<CarNavigation>(bus);
                navigation.m_MaxSpeed = 0f;
                EntityManager.SetComponentData(bus, navigation);

                Moving moving = EntityManager.GetComponentData<Moving>(bus);
                moving.m_Velocity = float3.zero;
                moving.m_AngularVelocity = float3.zero;
                EntityManager.SetComponentData(bus, moving);
            }
        }
    }

    internal static class BoardingHelpers
    {
        internal static Dictionary<Entity, BoardingZone> FindObservedZones(EntityManager entityManager, EntityQuery busQuery)
        {
            var result = new Dictionary<Entity, BoardingZone>();
            using NativeArray<Entity> buses = busQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity bus in buses)
            {
                if (!IsBus(entityManager, bus) || !TryGetStop(entityManager, bus, out Entity stop))
                    continue;
                ObserveZone(entityManager, result, stop, bus);
            }
            return result;
        }

        internal static void ObserveZone(EntityManager entityManager, Dictionary<Entity, BoardingZone> zones,
            Entity stop, Entity bus)
        {
            if (!IsPassengerBusStop(entityManager, stop) ||
                !TryGetPhysicalZone(entityManager, stop, bus, out BoardingZone zone))
                return;

            if (!zones.TryGetValue(stop, out BoardingZone existing) ||
                BoardingPolicy.PreferZoneCandidate(existing.StopDistance, existing.IsPullIn, existing.IsPhysical,
                    zone.StopDistance, zone.IsPullIn, zone.IsPhysical))
                zones[stop] = zone;
        }

        internal static bool TryGetStopZone(EntityManager entityManager, Entity stop, out BoardingZone zone)
        {
            zone = default;
            if (!IsPassengerBusStop(entityManager, stop) || !entityManager.HasBuffer<ConnectedRoute>(stop))
                return false;

            bool found = false;
            DynamicBuffer<ConnectedRoute> routes = entityManager.GetBuffer<ConnectedRoute>(stop, true);
            foreach (ConnectedRoute route in routes)
            {
                if (!TryGetWaypointZone(entityManager, stop, Entity.Null, route.m_Waypoint,
                    out BoardingZone candidate))
                    continue;
                if (!found || BoardingPolicy.PreferZoneCandidate(zone.StopDistance, zone.IsPullIn, zone.IsPhysical,
                    candidate.StopDistance, candidate.IsPullIn, candidate.IsPhysical))
                    zone = candidate;
                found = true;
            }
            return found;
        }

        private static bool TryGetPhysicalZone(EntityManager entityManager, Entity stop, Entity bus, out BoardingZone zone)
        {
            Entity waypoint = entityManager.GetComponentData<Target>(bus).m_Target;
            return TryGetWaypointZone(entityManager, stop, bus, waypoint, out zone);
        }

        private static bool TryGetWaypointZone(EntityManager entityManager, Entity stop, Entity bus, Entity waypoint,
            out BoardingZone zone)
        {
            zone = default;
            if (!entityManager.HasComponent<RouteLane>(waypoint))
                return false;

            RouteLane routeLane = entityManager.GetComponentData<RouteLane>(waypoint);
            Entity lane = Entity.Null;
            Curve curve = default;
            float width = 3.5f;
            float stopDistance = float.MaxValue;
            bool hasStopPosition = entityManager.HasComponent<Game.Routes.Position>(waypoint);
            float3 stopWorldPosition = hasStopPosition
                ? entityManager.GetComponentData<Game.Routes.Position>(waypoint).m_Position
                : default;
            int routeDirection = routeLane.m_EndCurvePos >= routeLane.m_StartCurvePos ? 1 : -1;
            int direction = routeDirection;
            bool physical = false;

            // A bus already beside its target stop proves which physical side/lane it is using. Farther-away
            // buses must not contribute their current junction or approach lane.
            if (bus != Entity.Null && hasStopPosition && entityManager.HasComponent<Transform>(bus) &&
                math.distance(entityManager.GetComponentData<Transform>(bus).m_Position, stopWorldPosition) <=
                    BoardingPolicy.PhysicalLaneCaptureDistance &&
                entityManager.HasComponent<CarCurrentLane>(bus))
            {
                CarCurrentLane current = entityManager.GetComponentData<CarCurrentLane>(bus);
                ConsiderLane(entityManager, current.m_Lane,
                    current.m_CurvePosition.z >= current.m_CurvePosition.x ? 1 : -1,
                    hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width, ref stopDistance, ref direction);
                physical = lane != Entity.Null;
            }

            if (!physical)
            {
                ConsiderLane(entityManager, routeLane.m_EndLane, routeDirection, hasStopPosition, stopWorldPosition,
                    ref lane, ref curve, ref width, ref stopDistance, ref direction);
                ConsiderLane(entityManager, routeLane.m_StartLane, routeDirection, hasStopPosition, stopWorldPosition,
                    ref lane, ref curve, ref width, ref stopDistance, ref direction);
                if (bus != Entity.Null && lane == Entity.Null && entityManager.HasBuffer<CarNavigationLane>(bus))
                {
                    DynamicBuffer<CarNavigationLane> navigation = entityManager.GetBuffer<CarNavigationLane>(bus, true);
                    if (navigation.Length > 0)
                    {
                        CarNavigationLane last = navigation[navigation.Length - 1];
                        if ((last.m_Flags & Game.Vehicles.CarLaneFlags.EndOfPath) != 0)
                            ConsiderLane(entityManager, last.m_Lane,
                                last.m_CurvePosition.y >= last.m_CurvePosition.x ? 1 : -1,
                                hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width, ref stopDistance, ref direction);
                    }
                }
            }

            if (lane == Entity.Null)
                return false;

            float stopPosition = routeLane.m_EndCurvePos;
            if (hasStopPosition)
                MathUtils.Distance(curve.m_Bezier, stopWorldPosition, out stopPosition);

            NetSlaveLaneFlags topology = GetSlaveLaneFlags(entityManager, lane) |
                GetSlaveLaneFlags(entityManager, routeLane.m_StartLane) |
                GetSlaveLaneFlags(entityManager, routeLane.m_EndLane);
            bool splitsFromRoad = (topology & (NetSlaveLaneFlags.SplitLeft | NetSlaveLaneFlags.SplitRight)) != 0;
            bool mergesIntoRoad = (topology & (NetSlaveLaneFlags.MergingLane |
                NetSlaveLaneFlags.MergeLeft | NetSlaveLaneFlags.MergeRight)) != 0;

            zone = new BoardingZone
            {
                Lane = lane,
                Curve = curve,
                CurvePosition = stopPosition,
                Width = width,
                IsPullIn = BoardingPolicy.IsPullInLane(
                    IsSecondaryLane(entityManager, lane) ||
                    IsSecondaryLane(entityManager, routeLane.m_StartLane) ||
                    IsSecondaryLane(entityManager, routeLane.m_EndLane),
                    splitsFromRoad,
                    mergesIntoRoad,
                    IsSameOwnerTransition(entityManager, routeLane.m_StartLane, routeLane.m_EndLane)),
                Direction = direction,
                StopDistance = stopDistance,
                IsPhysical = physical
            };
            ApplyOverride(entityManager, stop, ref zone);
            return true;
        }

        private static void ConsiderLane(EntityManager entityManager, Entity candidate, int candidateDirection,
            bool hasStopPosition, float3 stopPosition, ref Entity lane, ref Curve curve, ref float width,
            ref float bestDistance, ref int direction)
        {
            if (!TryGetLaneGeometry(entityManager, candidate, out Curve candidateCurve, out float candidateWidth))
                return;
            float distance = hasStopPosition ? MathUtils.Distance(candidateCurve.m_Bezier, stopPosition, out _) : 0f;
            if (lane != Entity.Null &&
                !BoardingPolicy.PreferZoneCandidate(bestDistance, false, false, distance, false, false))
                return;
            lane = candidate;
            curve = candidateCurve;
            width = candidateWidth;
            bestDistance = distance;
            direction = candidateDirection;
        }

        internal static void ApplyOverride(EntityManager entityManager, Entity stop, ref BoardingZone zone)
        {
            zone.IsCustom = entityManager.HasComponent<BoardingZoneOverride>(stop);
            if (!zone.IsCustom)
                return;

            BoardingZoneOverride custom = entityManager.GetComponentData<BoardingZoneOverride>(stop);
            zone.CustomOffset = custom.m_Offset;
            zone.CustomLength = custom.m_Length;
        }

        private static bool TryGetLaneGeometry(EntityManager entityManager, Entity lane, out Curve curve, out float width)
        {
            curve = default;
            width = 3.5f;
            if (lane == Entity.Null || !entityManager.HasComponent<NetCarLane>(lane) ||
                !entityManager.HasComponent<Curve>(lane))
                return false;

            curve = entityManager.GetComponentData<Curve>(lane);
            if (entityManager.HasComponent<PrefabRef>(lane))
            {
                Entity prefab = entityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
                if (entityManager.HasComponent<NetLaneData>(prefab))
                    width = entityManager.GetComponentData<NetLaneData>(prefab).m_Width;
            }
            return true;
        }

        private static bool IsSecondaryLane(EntityManager entityManager, Entity lane)
        {
            if (lane == Entity.Null)
                return false;
            if (entityManager.HasComponent<NetSecondaryLane>(lane))
                return true;
            if (!entityManager.HasComponent<NetCarLane>(lane))
                return false;
            NetCarLaneFlags flags = entityManager.GetComponentData<NetCarLane>(lane).m_Flags;
            return (flags & (NetCarLaneFlags.SecondaryStart | NetCarLaneFlags.SecondaryEnd)) != 0;
        }

        private static NetSlaveLaneFlags GetSlaveLaneFlags(EntityManager entityManager, Entity lane)
        {
            return lane != Entity.Null && entityManager.HasComponent<NetSlaveLane>(lane)
                ? entityManager.GetComponentData<NetSlaveLane>(lane).m_Flags
                : 0;
        }

        private static bool IsSameOwnerTransition(EntityManager entityManager, Entity startLane, Entity endLane)
        {
            if (startLane == endLane || startLane == Entity.Null || endLane == Entity.Null ||
                !entityManager.HasComponent<Owner>(startLane) || !entityManager.HasComponent<Owner>(endLane))
                return false;
            Entity startOwner = entityManager.GetComponentData<Owner>(startLane).m_Owner;
            Entity endOwner = entityManager.GetComponentData<Owner>(endLane).m_Owner;
            return startOwner != Entity.Null && startOwner == endOwner;
        }

        internal static float GetZoneLength(BoardingZone zone)
        {
            float2 bounds = GetZoneBounds(zone);
            return (bounds.y - bounds.x) * zone.Curve.m_Length;
        }

        internal static float2 GetZoneBounds(BoardingZone zone)
        {
            BoardingPolicy.GetZoneBounds(zone.IsPullIn, zone.CurvePosition, zone.Curve.m_Length,
                zone.Direction, zone.IsCustom, zone.CustomOffset, zone.CustomLength,
                out float start, out float end);
            return new float2(start, end);
        }

        internal static bool IsPassengerBusStop(EntityManager entityManager, Entity stop)
        {
            if (!entityManager.HasComponent<BoardingVehicle>(stop) || !entityManager.HasComponent<PrefabRef>(stop))
                return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            if (!entityManager.HasComponent<TransportStopData>(prefab))
                return false;
            TransportStopData data = entityManager.GetComponentData<TransportStopData>(prefab);
            return data.m_TransportType == TransportType.Bus && data.m_PassengerTransport;
        }

        internal static bool IsBus(EntityManager entityManager, Entity vehicle)
        {
            if (vehicle == Entity.Null || !entityManager.Exists(vehicle) || !entityManager.HasComponent<PrefabRef>(vehicle))
                return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            return entityManager.HasComponent<PublicTransportVehicleData>(prefab) &&
                entityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_TransportType == TransportType.Bus;
        }

        internal static bool TryGetStop(EntityManager entityManager, Entity vehicle, out Entity stop)
        {
            stop = Entity.Null;
            if (!entityManager.HasComponent<Target>(vehicle))
                return false;
            Entity target = entityManager.GetComponentData<Target>(vehicle).m_Target;
            if (entityManager.HasComponent<BoardingVehicle>(target))
                stop = target;
            else if (entityManager.HasComponent<Connected>(target))
                stop = entityManager.GetComponentData<Connected>(target).m_Connected;
            return stop != Entity.Null && entityManager.HasComponent<BoardingVehicle>(stop);
        }

        internal static float GetVehicleLength(EntityManager entityManager, Entity vehicle)
        {
            float length = 0f;
            if (entityManager.HasBuffer<LayoutElement>(vehicle))
            {
                DynamicBuffer<LayoutElement> layout = entityManager.GetBuffer<LayoutElement>(vehicle, true);
                foreach (LayoutElement element in layout)
                    length += GetUnitLength(entityManager, element.m_Vehicle);
            }
            return length > 0f ? length : GetUnitLength(entityManager, vehicle);
        }

        internal static bool IsCloseToStop(EntityManager entityManager, Entity vehicle, BoardingZone zone)
        {
            return entityManager.HasComponent<Transform>(vehicle) &&
                IsPointInside(zone, entityManager.GetComponentData<Transform>(vehicle).m_Position);
        }

        internal static float GetSpeed(EntityManager entityManager, Entity vehicle)
        {
            return entityManager.HasComponent<Moving>(vehicle)
                ? math.length(entityManager.GetComponentData<Moving>(vehicle).m_Velocity)
                : 0f;
        }

        private static float GetUnitLength(EntityManager entityManager, Entity vehicle)
        {
            if (!entityManager.HasComponent<PrefabRef>(vehicle))
                return 0f;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            return entityManager.HasComponent<ObjectGeometryData>(prefab)
                ? math.max(0f, entityManager.GetComponentData<ObjectGeometryData>(prefab).m_Size.z)
                : 0f;
        }

        private static bool IsPointInside(BoardingZone zone, float3 point)
        {
            float curvePosition;
            float distance = MathUtils.Distance(zone.Curve.m_Bezier, point, out curvePosition);
            float normalizedTolerance = BoardingPolicy.BoardingPositionTolerance /
                math.max(1f, zone.Curve.m_Length);
            float2 bounds = GetZoneBounds(zone);
            return curvePosition >= bounds.x - normalizedTolerance &&
                curvePosition <= bounds.y + normalizedTolerance &&
                distance <= zone.Width * 0.5f + BoardingPolicy.BoardingPositionTolerance;
        }
    }
}
