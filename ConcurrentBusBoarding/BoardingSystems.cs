using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Creatures;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
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
using CreatureResident = Game.Creatures.Resident;
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
        internal List<BoardingZonePiece> Pieces;
    }

    internal struct BoardingZonePiece
    {
        internal Entity Lane;
        internal Curve Curve;
        internal float2 Bounds;
        internal float Width;
        internal int Direction;
    }

    internal struct ConcurrentBoardingActive : IComponentData
    {
        internal Entity Stop;
        internal Entity Route;
        internal byte SelectedForVehicleAi;
        internal byte UsesNativeBoarding;
    }

    internal struct ConcurrentRouteHandoff : IComponentData
    {
        internal Entity Route;
        internal uint ExpiresFrame;
    }

    internal struct BoardingZoneApproach : IComponentData
    {
    }

    internal struct BoardingZoneFallback : IComponentData
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
        private PrefabSystem m_PrefabSystem;
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
                ComponentType.ReadOnly<Owner>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<PathOwner>(),
                ComponentType.ReadOnly<CarCurrentLane>(),
                ComponentType.ReadOnly<CurrentRoute>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>(),
                ComponentType.Exclude<TripSource>(),
                ComponentType.Exclude<OutOfControl>());
            m_Stops = GetEntityQuery(
                ComponentType.ReadWrite<BoardingVehicle>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
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
                    ConcurrentBoardingActive active = managed
                        ? EntityManager.GetComponentData<ConcurrentBoardingActive>(bus)
                        : default;
                    if (!BoardingHelpers.HasLoadedCarPrefab(EntityManager, m_PrefabSystem, bus,
                            out Entity vehiclePrefab))
                    {
                        CrashBreadcrumbs.Write($"boarding-skip unresolved-prefab bus={CrashBreadcrumbs.Id(bus)} prefab={CrashBreadcrumbs.Id(vehiclePrefab)}");
                        if (managed)
                            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }

                    if (!BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        if (managed)
                            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }

                    Entity route = managed && active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus);
                    if (!BoardingHelpers.CanManageRouteContext(EntityManager, bus, route))
                    {
                        if (managed)
                            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
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

                    EntityManager.AddComponentData(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus),
                        UsesNativeBoarding = 1
                    });
                    activeBuses.Add(bus);
                    occupiedLength += candidateLength;
                }

                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus) ||
                        EntityManager.HasComponent<BoardingZoneApproach>(bus))
                        continue;

                    bool closeToStop = hasZone && BoardingHelpers.IsCloseToStop(EntityManager, bus, zone);
                    float speed = BoardingHelpers.GetSpeed(EntityManager, bus);
                    if (!closeToStop || speed > BoardingPolicy.BoardingSpeedTolerance)
                        continue;
                    float candidateLength = BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    if (!BoardingPolicy.CanAdmit(zone.IsCustom, pullIn, activeBuses.Count, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), true))
                        continue;

                    CrashBreadcrumbs.Write($"boarding-begin before bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    BeginBoarding(bus);
                    CrashBreadcrumbs.Write($"boarding-begin state-written bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    EntityManager.AddComponentData(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus)
                    });
                    CrashBreadcrumbs.Write($"boarding-begin active-added bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
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
                    PrepareForVehicleAi(bus, stop, bus == selected);

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

        private void PrepareForVehicleAi(Entity bus, Entity stop, bool selected)
        {
            VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
            ConcurrentBoardingActive active = EntityManager.GetComponentData<ConcurrentBoardingActive>(bus);
            transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
            transport.m_State |= PublicTransportFlags.EnRoute;
            if (BoardingPolicy.ShouldExposeBoardingToVehicleAi(active.UsesNativeBoarding != 0, selected))
                transport.m_State |= PublicTransportFlags.Boarding;
            else
                transport.m_State &= ~PublicTransportFlags.Boarding;
            EntityManager.SetComponentData(bus, transport);
            EntityManager.SetComponentData(bus, new ConcurrentBoardingActive
            {
                Stop = stop,
                Route = active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus),
                SelectedForVehicleAi = selected ? (byte)1 : (byte)0,
                UsesNativeBoarding = active.UsesNativeBoarding
            });
        }

        private Entity GetCurrentRoute(Entity bus) => EntityManager.HasComponent<CurrentRoute>(bus)
            ? EntityManager.GetComponentData<CurrentRoute>(bus).m_Route
            : Entity.Null;

        private static void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
                groups.Add(stop, list = new List<Entity>());
            list.Add(bus);
        }
    }

    [UpdateAfter(typeof(TransportCarAISystem))]
    [UpdateBefore(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(PassengerDistributionSystem))]
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
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
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
                    bool fallbackPlacement = EntityManager.HasComponent<BoardingZoneFallback>(bus);
                    if (!BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        ClearPlacement(bus);
                        continue;
                    }
                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    const PublicTransportFlags approaching = PublicTransportFlags.EnRoute |
                        PublicTransportFlags.Arriving | PublicTransportFlags.Testing |
                        PublicTransportFlags.Boarding | PublicTransportFlags.RequireStop;
                    if (!approachingZone && !fallbackPlacement &&
                        !EntityManager.HasComponent<ConcurrentBoardingActive>(bus) &&
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
                        ClearPlacement(bus);
                    continue;
                }

                var buses = new List<BoardingZoneBus>(entry.Value.Count);
                foreach (Entity bus in entry.Value)
                {
                    if (!BoardingHelpers.IsCloseToStop(EntityManager, bus, zone))
                    {
                        ClearPlacement(bus);
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

                float zoneLength = BoardingHelpers.GetZoneLength(zone);
                float usedLength = 0f;
                int accepted = 0;
                foreach (BoardingZoneBus bus in buses)
                {
                    if (bus.Length <= 0f || usedLength + bus.Length > zoneLength ||
                        (!zone.IsCustom && !zone.IsPullIn && accepted >= BoardingPolicy.OrdinaryStopLimit))
                    {
                        ClearPlacement(bus.Entity);
                        continue;
                    }

                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus.Entity))
                    {
                        ClearPlacement(bus.Entity);
                    }
                    else
                    {
                        ClearApproach(bus.Entity);
                        if (!EntityManager.HasComponent<BoardingZoneFallback>(bus.Entity))
                            EntityManager.AddComponent<BoardingZoneFallback>(bus.Entity);
                    }
                    usedLength += bus.Length + BoardingPolicy.BusGap;
                    accepted++;
                }
            }
            CrashBreadcrumbs.Write("approach-cycle after");
        }

        private void ClearApproach(Entity bus)
        {
            if (EntityManager.HasComponent<BoardingZoneApproach>(bus))
                EntityManager.RemoveComponent<BoardingZoneApproach>(bus);
        }

        private void ClearFallback(Entity bus)
        {
            if (EntityManager.HasComponent<BoardingZoneFallback>(bus))
                EntityManager.RemoveComponent<BoardingZoneFallback>(bus);
        }

        private void ClearPlacement(Entity bus)
        {
            ClearApproach(bus);
            ClearFallback(bus);
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
        private SimulationSystem m_SimulationSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadWrite<ConcurrentBoardingActive>(),
                ComponentType.ReadOnly<VehiclePublicTransport>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
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
                    ConcurrentBoardingActive active = EntityManager.GetComponentData<ConcurrentBoardingActive>(bus);
                    if (!BoardingHelpers.CanManageRouteContext(EntityManager, bus, active.Route))
                    {
                        CrashBreadcrumbs.Write($"active-removed invalid-route bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(active.Route)}");
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }
                    EnsureRouteAssociation(bus, active);
                    if (!BoardingHelpers.IsBus(EntityManager, bus) ||
                        !BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        CrashBreadcrumbs.Write($"active-removed no-stop bus={CrashBreadcrumbs.Id(bus)}");
                        BeginRouteHandoff(bus, active.Route);
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    if (active.Stop != stop)
                    {
                        CrashBreadcrumbs.Write($"active-complete bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(active.Stop)} next={CrashBreadcrumbs.Id(stop)}");
                        BeginRouteHandoff(bus, active.Route);
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }

                    if (active.SelectedForVehicleAi != 0)
                    {
                        active.SelectedForVehicleAi = 0;
                        bool boardingAfterVehicleAi =
                            (transport.m_State & PublicTransportFlags.Boarding) != 0;
                        if (BoardingPolicy.ShouldAdoptNativeBoarding(
                                active.UsesNativeBoarding != 0, true, boardingAfterVehicleAi))
                        {
                            active.UsesNativeBoarding = 1;
                            CrashBreadcrumbs.Write($"active-adopted native bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                        }
                        if (active.UsesNativeBoarding != 0 &&
                            !boardingAfterVehicleAi)
                        {
                            CrashBreadcrumbs.Write($"active-complete native bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                            BeginRouteHandoff(bus, active.Route);
                            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                            continue;
                        }
                        if (BoardingPolicy.ShouldCompleteManagedBoarding(
                                active.UsesNativeBoarding != 0, true) &&
                            TryCompleteBoarding(bus, stop, ref transport))
                        {
                            CrashBreadcrumbs.Write($"active-complete follower bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                            BeginRouteHandoff(bus, active.Route);
                            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                            continue;
                        }
                        EntityManager.SetComponentData(bus, active);
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

        private void BeginRouteHandoff(Entity bus, Entity route)
        {
            if (!BoardingHelpers.CanManageRouteContext(EntityManager, bus, route))
                return;

            var handoff = new ConcurrentRouteHandoff
            {
                Route = route,
                ExpiresFrame = m_SimulationSystem.frameIndex + 512u
            };
            if (EntityManager.HasComponent<ConcurrentRouteHandoff>(bus))
                EntityManager.SetComponentData(bus, handoff);
            else
                EntityManager.AddComponentData(bus, handoff);
        }

        private void EnsureRouteAssociation(Entity bus, ConcurrentBoardingActive active)
        {
            if (EntityManager.HasComponent<CurrentRoute>(bus) ||
                !BoardingHelpers.CanManageRouteContext(EntityManager, bus, active.Route))
                return;

            CrashBreadcrumbs.Write($"route-restored bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(active.Route)}");
            EntityManager.AddComponentData(bus, new CurrentRoute(active.Route));
        }

        private bool TryCompleteBoarding(Entity bus, Entity stop, ref VehiclePublicTransport transport)
        {
            uint frame = m_SimulationSystem.frameIndex;
            bool timedOut = transport.m_DepartureFrame != 0 &&
                frame >= transport.m_DepartureFrame + 1800u;
            transport.m_MaxBoardingDistance = transport.m_MinWaitingDistance == float.MaxValue ||
                transport.m_MinWaitingDistance == 0f || timedOut
                ? float.MaxValue
                : transport.m_MinWaitingDistance + 1f;
            transport.m_MinWaitingDistance = float.MaxValue;

            if (!BoardingPolicy.CanFinishBoarding(frame, transport.m_DepartureFrame,
                    transport.m_MaxBoardingDistance, ArePassengersReady(bus)) ||
                !TryAdvanceToNextWaypoint(bus))
                return false;

            transport.m_State &= ~(PublicTransportFlags.Arriving | PublicTransportFlags.Boarding |
                PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
            transport.m_State |= PublicTransportFlags.EnRoute;
            EntityManager.SetComponentData(bus, transport);

            BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
            if (slot.m_Vehicle == bus)
            {
                slot.m_Vehicle = Entity.Null;
                EntityManager.SetComponentData(stop, slot);
            }
            return true;
        }

        private bool ArePassengersReady(Entity bus)
        {
            if (!EntityManager.HasBuffer<Passenger>(bus))
                return true;
            DynamicBuffer<Passenger> passengers = EntityManager.GetBuffer<Passenger>(bus, true);
            foreach (Passenger passenger in passengers)
            {
                if (EntityManager.HasComponent<CurrentVehicle>(passenger.m_Passenger) &&
                    (EntityManager.GetComponentData<CurrentVehicle>(passenger.m_Passenger).m_Flags &
                        CreatureVehicleFlags.Ready) == 0)
                    return false;
            }
            return true;
        }

        private bool TryAdvanceToNextWaypoint(Entity bus)
        {
            if (!EntityManager.HasComponent<CurrentRoute>(bus) ||
                !EntityManager.HasComponent<PathOwner>(bus) ||
                !EntityManager.HasComponent<Target>(bus))
                return false;

            CurrentRoute currentRoute = EntityManager.GetComponentData<CurrentRoute>(bus);
            PathOwner pathOwner = EntityManager.GetComponentData<PathOwner>(bus);
            Target target = EntityManager.GetComponentData<Target>(bus);
            if (!BoardingHelpers.IsUsableRouteWaypoint(
                    EntityManager, currentRoute.m_Route, target.m_Target))
                return false;
            Waypoint waypoint = EntityManager.GetComponentData<Waypoint>(target.m_Target);

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(currentRoute.m_Route, true);
            if (waypoints.Length == 0 || waypoint.m_Index < 0 || waypoint.m_Index >= waypoints.Length)
                return false;

            Entity oldWaypoint = target.m_Target;
            Entity nextWaypoint = waypoints[(waypoint.m_Index + 1) % waypoints.Length].m_Waypoint;
            if (nextWaypoint == oldWaypoint ||
                !BoardingHelpers.IsUsableRouteWaypoint(EntityManager, currentRoute.m_Route, nextWaypoint))
                return false;

            CrashBreadcrumbs.Write($"completion-target before bus={CrashBreadcrumbs.Id(bus)} old={CrashBreadcrumbs.Id(oldWaypoint)} next={CrashBreadcrumbs.Id(nextWaypoint)}");
            VehicleUtils.SetTarget(ref pathOwner, ref target, nextWaypoint);
            EntityManager.SetComponentData(bus, pathOwner);
            EntityManager.SetComponentData(bus, target);
            CrashBreadcrumbs.Write($"completion-target after bus={CrashBreadcrumbs.Id(bus)} next={CrashBreadcrumbs.Id(nextWaypoint)}");
            return true;
        }

        private static void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
                groups.Add(stop, list = new List<Entity>());
            list.Add(bus);
        }
    }

    [UpdateAfter(typeof(TransportCarAISystem))]
    [UpdateBefore(typeof(PassengerDistributionSystem))]
    public partial class RouteHandoffSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private SimulationSystem m_SimulationSystem;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        public override int GetUpdateOffset(SystemUpdatePhase phase) => 1;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadWrite<ConcurrentRouteHandoff>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            using NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp);
            foreach (Entity bus in buses)
            {
                ConcurrentRouteHandoff handoff = EntityManager.GetComponentData<ConcurrentRouteHandoff>(bus);
                if (m_SimulationSystem.frameIndex >= handoff.ExpiresFrame ||
                    !BoardingHelpers.CanManageRouteContext(EntityManager, bus, handoff.Route))
                {
                    EntityManager.RemoveComponent<ConcurrentRouteHandoff>(bus);
                    continue;
                }

                if (EntityManager.HasComponent<CurrentRoute>(bus))
                {
                    if (EntityManager.GetComponentData<CurrentRoute>(bus).m_Route != handoff.Route)
                        EntityManager.RemoveComponent<ConcurrentRouteHandoff>(bus);
                    continue;
                }

                CrashBreadcrumbs.Write($"route-handoff restored bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(handoff.Route)}");
                EntityManager.AddComponentData(bus, new CurrentRoute(handoff.Route));
            }
        }
    }

    [UpdateAfter(typeof(ResidentAISystem))]
    [UpdateBefore(typeof(HumanNavigationSystem))]
    public partial class PassengerWaitingSpreadSystem : GameSystemBase
    {
        private EntityQuery m_Residents;
        private BoardingZoneRenderSystem m_ZoneRenderSystem;
        private SimulationSystem m_SimulationSystem;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Residents = GetEntityQuery(
                ComponentType.ReadOnly<CreatureResident>(),
                ComponentType.ReadOnly<UpdateFrame>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadWrite<HumanCurrentLane>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ZoneRenderSystem = World.GetOrCreateSystemManaged<BoardingZoneRenderSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            RequireForUpdate(m_Residents);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Match ResidentAISystem's native 16-way shared-component partition. This keeps the queue-area
            // correction on the residents updated this frame without a full-city main-thread scan.
            m_Residents.SetSharedComponentFilter(new UpdateFrame(m_SimulationSystem.frameIndex % 16u));
            using NativeArray<Entity> residents = m_Residents.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in residents)
            {
                CreatureResident resident = EntityManager.GetComponentData<CreatureResident>(entity);
                if ((resident.m_Flags & ResidentFlags.WaitingTransport) == 0)
                    continue;

                HumanCurrentLane currentLane = EntityManager.GetComponentData<HumanCurrentLane>(entity);
                Entity stop = currentLane.m_QueueEntity;
                if (!BoardingHelpers.IsPassengerBusStop(EntityManager, stop) ||
                    !m_ZoneRenderSystem.TryGetObservedZone(stop, out BoardingZone zone) ||
                    !EntityManager.HasComponent<Transform>(stop))
                    continue;

                Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                if (!EntityManager.HasComponent<ObjectGeometryData>(prefab))
                    continue;

                float2 bounds = BoardingHelpers.GetZoneBounds(zone);
                uint hash = math.hash(new uint2((uint)entity.Index, (uint)entity.Version));
                float unit = (hash & 65535u) / 65535f;
                float progress = BoardingPolicy.WaitingPosition(bounds.x, bounds.y, zone.Direction, unit);
                float3 stopOnRoad = MathUtils.Position(zone.Curve.m_Bezier, zone.CurvePosition);
                float3 waitingOnRoad = MathUtils.Position(zone.Curve.m_Bezier, progress);
                Transform stopTransform = EntityManager.GetComponentData<Transform>(stop);
                Sphere3 queueArea = CreatureUtils.GetQueueArea(
                    EntityManager.GetComponentData<ObjectGeometryData>(prefab), stopTransform.m_Position);
                queueArea.position += waitingOnRoad - stopOnRoad;
                currentLane.m_QueueArea = queueArea;
                EntityManager.SetComponentData(entity, currentLane);
            }
        }
    }

    [UpdateAfter(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(CarMoveSystem))]
    public partial class BoardingHoldSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private int m_LastActiveCount = -1;
        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadOnly<ConcurrentBoardingActive>(),
                ComponentType.ReadWrite<CarNavigation>(),
                ComponentType.ReadWrite<Moving>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            using NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp);
            if (buses.Length != m_LastActiveCount)
            {
                m_LastActiveCount = buses.Length;
                CrashBreadcrumbs.Write($"hold active={buses.Length}");
            }
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
        internal static bool CanManageRouteContext(EntityManager entityManager, Entity bus, Entity route)
        {
            bool validRoute = bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<VehiclePublicTransport>(bus) &&
                entityManager.HasComponent<Target>(bus) &&
                !entityManager.HasComponent<Deleted>(bus) &&
                !entityManager.HasComponent<Game.Tools.Temp>(bus) &&
                !entityManager.HasComponent<TripSource>(bus) &&
                !entityManager.HasComponent<OutOfControl>(bus);
            if (!validRoute)
                return false;

            if (entityManager.HasComponent<CurrentRoute>(bus) &&
                entityManager.GetComponentData<CurrentRoute>(bus).m_Route != route)
                return false;

            VehiclePublicTransport transport = entityManager.GetComponentData<VehiclePublicTransport>(bus);
            const PublicTransportFlags retiring = PublicTransportFlags.Returning |
                PublicTransportFlags.Evacuating | PublicTransportFlags.PrisonerTransport |
                PublicTransportFlags.RequiresMaintenance | PublicTransportFlags.Refueling |
                PublicTransportFlags.AbandonRoute | PublicTransportFlags.DummyTraffic |
                PublicTransportFlags.Disabled;
            const PublicTransportFlags active = PublicTransportFlags.EnRoute |
                PublicTransportFlags.Arriving | PublicTransportFlags.Boarding |
                PublicTransportFlags.Testing | PublicTransportFlags.RequireStop;
            bool isRetiring = (transport.m_State & retiring) != 0 ||
                (transport.m_State & active) == 0;
            Entity target = entityManager.GetComponentData<Target>(bus).m_Target;
            return BoardingPolicy.CanRestoreRoute(
                IsUsableRoute(entityManager, route),
                IsUsableRouteWaypoint(entityManager, route, target),
                isRetiring);
        }

        internal static bool IsUsableRouteWaypoint(EntityManager entityManager, Entity route, Entity waypoint)
        {
            if (!IsUsableRoute(entityManager, route) || waypoint == Entity.Null ||
                !entityManager.Exists(waypoint) ||
                entityManager.HasComponent<Deleted>(waypoint) ||
                entityManager.HasComponent<Game.Tools.Temp>(waypoint) ||
                !entityManager.HasComponent<Waypoint>(waypoint) ||
                !entityManager.HasComponent<Owner>(waypoint) ||
                entityManager.GetComponentData<Owner>(waypoint).m_Owner != route)
                return false;

            Waypoint data = entityManager.GetComponentData<Waypoint>(waypoint);
            DynamicBuffer<RouteWaypoint> waypoints = entityManager.GetBuffer<RouteWaypoint>(route, true);
            return data.m_Index >= 0 && data.m_Index < waypoints.Length &&
                waypoints[data.m_Index].m_Waypoint == waypoint;
        }

        internal static void ReleaseConcurrentBoarding(
            EntityManager entityManager, Entity bus, ConcurrentBoardingActive active)
        {
            if (active.UsesNativeBoarding == 0 && bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<VehiclePublicTransport>(bus))
            {
                VehiclePublicTransport transport = entityManager.GetComponentData<VehiclePublicTransport>(bus);
                transport.m_State &= ~PublicTransportFlags.Boarding;
                entityManager.SetComponentData(bus, transport);

                if (active.Stop != Entity.Null && entityManager.Exists(active.Stop) &&
                    entityManager.HasComponent<BoardingVehicle>(active.Stop))
                {
                    BoardingVehicle slot = entityManager.GetComponentData<BoardingVehicle>(active.Stop);
                    bool changed = false;
                    if (slot.m_Vehicle == bus)
                    {
                        slot.m_Vehicle = Entity.Null;
                        changed = true;
                    }
                    if (slot.m_Testing == bus)
                    {
                        slot.m_Testing = Entity.Null;
                        changed = true;
                    }
                    if (changed)
                        entityManager.SetComponentData(active.Stop, slot);
                }
            }

            if (bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<ConcurrentBoardingActive>(bus))
                entityManager.RemoveComponent<ConcurrentBoardingActive>(bus);
        }

        private static bool IsUsableRoute(EntityManager entityManager, Entity route)
        {
            return route != Entity.Null && entityManager.Exists(route) &&
                !entityManager.HasComponent<Deleted>(route) &&
                !entityManager.HasComponent<Game.Tools.Temp>(route) &&
                entityManager.HasBuffer<RouteWaypoint>(route);
        }

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

            // A bus already beside its target stop proves which physical side/lane it is using. Compare its
            // current lane with the native final EndOfPath: the current lane may still be an adjacent approach
            // lane, while the final lane is the actual bay. Farther-away buses contribute neither.
            if (bus != Entity.Null && hasStopPosition && entityManager.HasComponent<Transform>(bus) &&
                math.distance(entityManager.GetComponentData<Transform>(bus).m_Position, stopWorldPosition) <=
                    BoardingPolicy.PhysicalLaneCaptureDistance)
            {
                if (entityManager.HasComponent<CarCurrentLane>(bus))
                {
                    CarCurrentLane current = entityManager.GetComponentData<CarCurrentLane>(bus);
                    ConsiderLane(entityManager, current.m_Lane,
                        current.m_CurvePosition.z >= current.m_CurvePosition.x ? 1 : -1,
                        hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width, ref stopDistance, ref direction);
                }
                if (entityManager.HasBuffer<CarNavigationLane>(bus))
                {
                    DynamicBuffer<CarNavigationLane> navigation = entityManager.GetBuffer<CarNavigationLane>(bus, true);
                    if (navigation.Length > 0)
                    {
                        CarNavigationLane last = navigation[navigation.Length - 1];
                        if ((last.m_Flags & Game.Vehicles.CarLaneFlags.EndOfPath) != 0)
                            ConsiderLane(entityManager, last.m_Lane,
                                last.m_CurvePosition.y >= last.m_CurvePosition.x ? 1 : -1,
                                hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width,
                                ref stopDistance, ref direction);
                    }
                }
                physical = lane != Entity.Null;
            }

            if (!physical)
            {
                ConsiderLane(entityManager, routeLane.m_EndLane, routeDirection, hasStopPosition, stopWorldPosition,
                    ref lane, ref curve, ref width, ref stopDistance, ref direction);
                if (lane == Entity.Null)
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
            BuildZonePieces(entityManager, waypoint, ref zone);
            ApplyOverride(entityManager, stop, ref zone);
            return true;
        }

        private static void BuildZonePieces(EntityManager entityManager, Entity waypoint, ref BoardingZone zone)
        {
            zone.Pieces = new List<BoardingZonePiece>();
            float2 firstBounds = zone.Direction >= 0
                ? new float2(0f, math.clamp(zone.CurvePosition, 0f, 1f))
                : new float2(math.clamp(zone.CurvePosition, 0f, 1f), 1f);
            zone.Pieces.Add(new BoardingZonePiece
            {
                Lane = zone.Lane,
                Curve = zone.Curve,
                Bounds = firstBounds,
                Width = zone.Width,
                Direction = zone.Direction
            });

            if (!entityManager.HasComponent<Owner>(waypoint) || !entityManager.HasComponent<Waypoint>(waypoint))
                return;
            Entity route = entityManager.GetComponentData<Owner>(waypoint).m_Owner;
            if (route == Entity.Null || !entityManager.HasBuffer<RouteSegment>(route))
                return;

            DynamicBuffer<RouteSegment> segments = entityManager.GetBuffer<RouteSegment>(route, true);
            if (segments.Length == 0)
                return;
            int waypointIndex = entityManager.GetComponentData<Waypoint>(waypoint).m_Index;
            float3 rear = PieceRear(zone.Pieces[0]);
            float available = PieceLength(zone.Pieces[0]);
            bool foundCurrentLane = false;

            for (int offset = 1; offset <= segments.Length && available < BoardingPolicy.MaximumCustomZoneLength; offset++)
            {
                int segmentIndex = (waypointIndex - offset + segments.Length) % segments.Length;
                Entity segment = segments[segmentIndex].m_Segment;
                if (segment == Entity.Null || !entityManager.HasBuffer<PathElement>(segment))
                    break;
                DynamicBuffer<PathElement> path = entityManager.GetBuffer<PathElement>(segment, true);
                for (int i = path.Length - 1; i >= 0 && available < BoardingPolicy.MaximumCustomZoneLength; i--)
                {
                    PathElement element = path[i];
                    if (!foundCurrentLane)
                    {
                        if (element.m_Target == zone.Lane)
                            foundCurrentLane = true;
                        continue;
                    }
                    if (element.m_Target == zone.Lane ||
                        !TryGetLaneGeometry(entityManager, element.m_Target, out Curve curve, out float width))
                        continue;

                    float2 delta = math.clamp(element.m_TargetDelta, 0f, 1f);
                    float2 bounds = new float2(math.min(delta.x, delta.y), math.max(delta.x, delta.y));
                    if ((bounds.y - bounds.x) * curve.m_Length < 0.1f)
                        continue;
                    int direction = element.m_TargetDelta.y >= element.m_TargetDelta.x ? 1 : -1;
                    BoardingZonePiece piece = new BoardingZonePiece
                    {
                        Lane = element.m_Target,
                        Curve = curve,
                        Bounds = bounds,
                        Width = width,
                        Direction = direction
                    };
                    if (math.distance(PieceFront(piece), rear) > 12f)
                        continue;
                    foundCurrentLane = true;
                    zone.Pieces.Add(piece);
                    rear = PieceRear(piece);
                    available += PieceLength(piece);
                }
            }
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
            zone.CustomOffset = math.isfinite(custom.m_Offset) ? custom.m_Offset : 0f;
            zone.CustomLength = math.isfinite(custom.m_Length)
                ? math.clamp(custom.m_Length, BoardingPolicy.MinimumCustomZoneLength,
                    BoardingPolicy.MaximumCustomZoneLength)
                : BoardingPolicy.MinimumCustomZoneLength;
        }

        internal static bool TryGetLaneGeometry(EntityManager entityManager, Entity lane, out Curve curve, out float width)
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
            if (!math.isfinite(width) || width <= 0f)
                width = 3.5f;
            return IsFiniteCurve(curve);
        }

        internal static bool IsRenderableZone(EntityManager entityManager, Entity stop, BoardingZone zone)
        {
            if (!IsPassengerBusStop(entityManager, stop) || zone.Pieces == null || zone.Pieces.Count == 0)
                return false;

            bool hasLength = false;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                if (piece.Lane == Entity.Null || !entityManager.Exists(piece.Lane) ||
                    entityManager.HasComponent<Deleted>(piece.Lane) ||
                    entityManager.HasComponent<Game.Tools.Temp>(piece.Lane) ||
                    !entityManager.HasComponent<NetCarLane>(piece.Lane) ||
                    !entityManager.HasComponent<Curve>(piece.Lane) ||
                    !math.all(math.isfinite(piece.Bounds)) ||
                    piece.Bounds.x < 0f || piece.Bounds.y > 1f || piece.Bounds.x > piece.Bounds.y ||
                    !math.isfinite(piece.Width) || piece.Width <= 0f ||
                    !IsFiniteCurve(piece.Curve))
                    return false;
                hasLength |= PieceLength(piece) > 0.01f;
            }
            return hasLength;
        }

        private static bool IsFiniteCurve(Curve curve)
        {
            if (!math.isfinite(curve.m_Length) || curve.m_Length <= 0f)
                return false;
            float3 start = MathUtils.Position(curve.m_Bezier, 0f);
            float3 middle = MathUtils.Position(curve.m_Bezier, 0.5f);
            float3 end = MathUtils.Position(curve.m_Bezier, 1f);
            return math.all(math.isfinite(start)) &&
                math.all(math.isfinite(middle)) &&
                math.all(math.isfinite(end));
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
            float remaining = GetRequestedZoneLength(zone);
            float length = 0f;
            if (zone.Pieces == null)
                return 0f;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                float pieceLength = math.min(PieceLength(piece), remaining);
                length += pieceLength;
                remaining -= pieceLength;
                if (remaining <= 0f)
                    break;
            }
            return length;
        }

        internal static float2 GetZoneBounds(BoardingZone zone)
        {
            BoardingPolicy.GetZoneBounds(zone.IsPullIn, zone.CurvePosition, zone.Curve.m_Length,
                zone.Direction, zone.IsCustom, zone.CustomOffset, zone.CustomLength,
                out float start, out float end);
            return new float2(start, end);
        }

        internal static float GetRequestedZoneLength(BoardingZone zone)
        {
            if (zone.IsCustom)
                return math.clamp(zone.CustomLength, BoardingPolicy.MinimumCustomZoneLength,
                    BoardingPolicy.MaximumCustomZoneLength);
            if (zone.IsPullIn && zone.Pieces != null && zone.Pieces.Count > 0)
                return PieceLength(zone.Pieces[0]);
            return BoardingPolicy.OrdinaryZoneLength;
        }

        internal static bool TryGetRearEdge(BoardingZone zone, out BoardingZonePiece rearPiece, out float2 rearBounds)
        {
            rearPiece = default;
            rearBounds = default;
            float remaining = GetRequestedZoneLength(zone);
            if (zone.Pieces == null)
                return false;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                rearPiece = piece;
                rearBounds = TrimFromFront(piece, remaining);
                remaining -= PieceLength(piece);
                if (remaining <= 0f)
                    return true;
            }
            return zone.Pieces.Count > 0;
        }

        internal static bool TryGetDistanceFromFront(BoardingZone zone, float3 point, out float distanceFromFront)
        {
            distanceFromFront = 0f;
            float bestDistance = float.MaxValue;
            float traversed = 0f;
            if (zone.Pieces == null)
                return false;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                float lateral = MathUtils.Distance(piece.Curve.m_Bezier, point, out float position);
                if (position >= piece.Bounds.x - 0.01f && position <= piece.Bounds.y + 0.01f && lateral < bestDistance)
                {
                    bestDistance = lateral;
                    float local = piece.Direction >= 0
                        ? (piece.Bounds.y - position) * piece.Curve.m_Length
                        : (position - piece.Bounds.x) * piece.Curve.m_Length;
                    distanceFromFront = traversed + math.clamp(local, 0f, PieceLength(piece));
                }
                traversed += PieceLength(piece);
            }
            return bestDistance <= 20f;
        }

        internal static float2 TrimFromFront(BoardingZonePiece piece, float length)
        {
            float range = math.min(PieceLength(piece), math.max(0f, length)) / math.max(1f, piece.Curve.m_Length);
            return piece.Direction >= 0
                ? new float2(piece.Bounds.y - range, piece.Bounds.y)
                : new float2(piece.Bounds.x, piece.Bounds.x + range);
        }

        internal static float PieceLength(BoardingZonePiece piece) =>
            (piece.Bounds.y - piece.Bounds.x) * piece.Curve.m_Length;

        internal static float3 PieceFront(BoardingZonePiece piece) =>
            MathUtils.Position(piece.Curve.m_Bezier, piece.Direction >= 0 ? piece.Bounds.y : piece.Bounds.x);

        internal static float3 PieceRear(BoardingZonePiece piece) =>
            MathUtils.Position(piece.Curve.m_Bezier, piece.Direction >= 0 ? piece.Bounds.x : piece.Bounds.y);

        internal static bool IsPassengerBusStop(EntityManager entityManager, Entity stop)
        {
            if (stop == Entity.Null || !entityManager.Exists(stop) ||
                entityManager.HasComponent<Deleted>(stop) ||
                entityManager.HasComponent<Game.Tools.Temp>(stop) ||
                !entityManager.HasComponent<BoardingVehicle>(stop) ||
                !entityManager.HasComponent<PrefabRef>(stop))
                return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(stop).m_Prefab;
            if (prefab == Entity.Null || !entityManager.Exists(prefab) ||
                entityManager.HasComponent<Deleted>(prefab) ||
                entityManager.HasComponent<Game.Tools.Temp>(prefab) ||
                !entityManager.HasComponent<TransportStopData>(prefab))
                return false;
            TransportStopData data = entityManager.GetComponentData<TransportStopData>(prefab);
            return data.m_TransportType == TransportType.Bus && data.m_PassengerTransport;
        }

        internal static bool IsBus(EntityManager entityManager, Entity vehicle)
        {
            if (vehicle == Entity.Null || !entityManager.Exists(vehicle) ||
                entityManager.HasComponent<Deleted>(vehicle) ||
                entityManager.HasComponent<Game.Tools.Temp>(vehicle) ||
                !entityManager.HasComponent<PrefabRef>(vehicle))
                return false;
            Entity prefab = entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            return prefab != Entity.Null && entityManager.Exists(prefab) &&
                !entityManager.HasComponent<Deleted>(prefab) &&
                !entityManager.HasComponent<Game.Tools.Temp>(prefab) &&
                entityManager.HasComponent<PublicTransportVehicleData>(prefab) &&
                entityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_TransportType == TransportType.Bus;
        }

        internal static bool HasLoadedCarPrefab(EntityManager entityManager, PrefabSystem prefabSystem, Entity vehicle,
            out Entity prefab)
        {
            prefab = Entity.Null;
            if (!entityManager.Exists(vehicle) || !entityManager.HasComponent<PrefabRef>(vehicle))
                return false;
            prefab = entityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
            return prefab != Entity.Null && entityManager.Exists(prefab) &&
                !entityManager.HasComponent<Deleted>(prefab) &&
                !entityManager.HasComponent<Game.Tools.Temp>(prefab) &&
                prefabSystem.TryGetPrefab(prefab, out CarPrefab _);
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
            return stop != Entity.Null && entityManager.Exists(stop) &&
                !entityManager.HasComponent<Deleted>(stop) &&
                !entityManager.HasComponent<Game.Tools.Temp>(stop) &&
                entityManager.HasComponent<BoardingVehicle>(stop);
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
            float remaining = GetRequestedZoneLength(zone);
            if (zone.Pieces == null)
                return false;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                float2 bounds = TrimFromFront(piece, remaining);
                float distance = MathUtils.Distance(piece.Curve.m_Bezier, point, out float curvePosition);
                float tolerance = BoardingPolicy.BoardingPositionTolerance / math.max(1f, piece.Curve.m_Length);
                if (curvePosition >= bounds.x - tolerance && curvePosition <= bounds.y + tolerance &&
                    distance <= piece.Width * 0.5f + BoardingPolicy.BoardingPositionTolerance)
                    return true;
                remaining -= PieceLength(piece);
                if (remaining <= 0f)
                    break;
            }
            return false;
        }
    }
}
