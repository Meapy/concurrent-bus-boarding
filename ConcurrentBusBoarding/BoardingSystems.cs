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
        // Frame the session was admitted. Drives the unconditional dwell deadline.
        internal uint AdmittedFrame;
        // The route waypoint this session is serving. VehicleTiming lives on the waypoint, and the
        // held time has to be repaid there when the session ends.
        internal Entity Waypoint;
        // Holds the passenger-facing stop slot for a whole 16-frame tick, in phase with the car AI.
        internal byte SelectedForPassengers;
        // This bus's own boarding progress. When the count stops changing across consecutive
        // completion attempts, its share of the passenger exchange is finished.
        internal int LastPassengerCount;
        internal byte IdleAttempts;
        // Diagnostic: set once the resident AI has reported any waiting cim near this bus.
        internal byte SawWaitingPassenger;
        // Phase two of departure: no new passengers admitted, waiting for in-flight boarders.
        internal byte DoorsClosing;
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
        private uint m_LastReportFrame;
        private int m_SingleBusVisits;
        private int m_ContendedVisits;

        private readonly Dictionary<Entity, List<Entity>> m_BusesByStop = new();
        private readonly Dictionary<Entity, BoardingZone> m_Zones = new();
        // A List is used as the pool rather than a Stack: under net48 with these references
        // Stack<T> is ambiguous between System and mscorlib.
        private readonly List<List<Entity>> m_ListPool = new();
        private readonly List<Entity> m_ActiveBuses = new();

        private void ReleaseStopLists()
        {
            foreach (KeyValuePair<Entity, List<Entity>> entry in m_BusesByStop)
            {
                entry.Value.Clear();
                m_ListPool.Add(entry.Value);
            }
            m_BusesByStop.Clear();
        }

        private List<Entity> RentList()
        {
            int last = m_ListPool.Count - 1;
            if (last < 0)
                return new List<Entity>();
            List<Entity> list = m_ListPool[last];
            m_ListPool.RemoveAt(last);
            return list;
        }

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
            // Runtime kill switch. Active sessions are released by PassengerDistributionSystem,
            // which hands each bus straight back to native AI.
            if (Mod.Settings != null && !Mod.Settings.EnableConcurrentBoarding)
                return;

            // Collections are reused between updates. This runs several times a second over every
            // bus in the city, so allocating them per update was pure GC pressure.
            ReleaseStopLists();
            m_Zones.Clear();
            Dictionary<Entity, List<Entity>> busesByStop = m_BusesByStop;
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
                            AbandonSession(bus, active);
                        continue;
                    }

                    if (!BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        if (managed)
                            AbandonSession(bus, active);
                        continue;
                    }

                    Entity route = managed && active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus);
                    if (!BoardingHelpers.CanManageRouteContext(EntityManager, bus, route))
                    {
                        if (managed)
                            AbandonSession(bus, active);
                        continue;
                    }

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    const PublicTransportFlags approaching = PublicTransportFlags.EnRoute |
                        PublicTransportFlags.Arriving | PublicTransportFlags.Testing |
                        PublicTransportFlags.Boarding | PublicTransportFlags.RequireStop;
                    if (!managed && (transport.m_State & approaching) == 0)
                        continue;

                    // Zone geometry is deliberately NOT resolved here. Building it walks the route's
                    // segment and path-element buffers and allocates, and it is only needed for
                    // stops the mod might actually manage.
                    Add(busesByStop, stop, bus);
                }
            }

            foreach (KeyValuePair<Entity, List<Entity>> entry in busesByStop)
            {
                Entity stop = entry.Key;

                // Cheap gate first. A stop with a single bus and no live session is left entirely to
                // native AI, so resolving its boarding zone would be wasted work - and that is the
                // overwhelming majority of stop visits.
                bool hasSession = false;
                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus))
                    {
                        hasSession = true;
                        break;
                    }
                }
                if (entry.Value.Count <= 1 && !hasSession)
                {
                    m_SingleBusVisits++;
                    continue;
                }

                // Resolve the zone once per candidate stop rather than once per bus.
                foreach (Entity bus in entry.Value)
                    BoardingHelpers.ObserveZone(EntityManager, m_Zones, stop, bus);

                bool hasZone = m_Zones.TryGetValue(stop, out BoardingZone zone);
                bool pullIn = hasZone && zone.IsPullIn;
                List<Entity> activeBuses = m_ActiveBuses;
                activeBuses.Clear();
                float occupiedLength = 0f;
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);

                int contenders = 0;
                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(bus))
                    {
                        activeBuses.Add(bus);
                        occupiedLength += BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    }
                    if (hasZone && BoardingHelpers.IsCloseToStop(EntityManager, bus, zone))
                        contenders++;
                }

                // With one bus at the stop there is no contention to resolve, so leave it entirely
                // to native AI: no session, no hold, no slot override. Sessions already running are
                // not disturbed, so a departing partner cannot cut another bus's boarding short.
                bool engage = BoardingPolicy.ShouldEngageConcurrentBoarding(contenders);
                if (!engage && activeBuses.Count == 0)
                {
                    m_SingleBusVisits++;
                    continue;
                }
                if (engage)
                    m_ContendedVisits++;

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
                    if (!engage ||
                        !BoardingPolicy.CanAdmit(zone.IsCustom, pullIn, activeBuses.Count, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), closeToStop))
                        continue;

                    // Do not inherit native's far-future departure frame; see ClampManagedDeparture.
                    transport.m_DepartureFrame = BoardingPolicy.ClampManagedDeparture(
                        m_SimulationSystem.frameIndex, transport.m_DepartureFrame);
                    EntityManager.SetComponentData(bus, transport);
                    EntityManager.AddComponentData(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus),
                        UsesNativeBoarding = 1,
                        AdmittedFrame = m_SimulationSystem.frameIndex,
                        Waypoint = EntityManager.GetComponentData<Target>(bus).m_Target,
                        LastPassengerCount = BoardingHelpers.GetPassengerCount(EntityManager, bus)
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
                    if (!closeToStop)
                        continue;
                    float candidateLength = BoardingHelpers.GetVehicleLength(EntityManager, bus);
                    bool canAdmit = engage && BoardingPolicy.CanAdmit(
                        zone.IsCustom, pullIn, activeBuses.Count, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), true);
                    if (!canAdmit)
                        continue;

                    VehiclePublicTransport transport =
                        EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    if (BoardingPolicy.ShouldRequestStop(
                            canAdmit, (transport.m_State & PublicTransportFlags.Boarding) != 0) &&
                        (transport.m_State & PublicTransportFlags.RequireStop) == 0)
                    {
                        transport.m_State |= PublicTransportFlags.RequireStop;
                        EntityManager.SetComponentData(bus, transport);
                        CrashBreadcrumbs.Write($"require-stop bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    }

                    if (!BoardingPolicy.CanBeginSyntheticBoarding(activeBuses.Count) ||
                        BoardingHelpers.GetSpeed(EntityManager, bus) >
                            BoardingPolicy.BoardingSpeedTolerance)
                        continue;

                    CrashBreadcrumbs.Write($"boarding-begin before bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    BeginBoarding(bus);
                    CrashBreadcrumbs.Write($"boarding-begin state-written bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    EntityManager.AddComponentData(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus),
                        AdmittedFrame = m_SimulationSystem.frameIndex,
                        Waypoint = EntityManager.GetComponentData<Target>(bus).m_Target,
                        LastPassengerCount = BoardingHelpers.GetPassengerCount(EntityManager, bus)
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

            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastReportFrame >= 4096u)
            {
                m_LastReportFrame = frame;
                Mod.Log.Info(
                    $"Concurrent boarding engagement: contended stop visits={m_ContendedVisits}, " +
                    $"single-bus visits left to native AI={m_SingleBusVisits}.");
            }
        }

        /// <summary>
        /// Drops a session whose context is no longer valid, repaying the line time it held first.
        /// </summary>
        private void AbandonSession(Entity bus, ConcurrentBoardingActive active)
        {
            BoardingHelpers.RepayHeldTime(EntityManager, m_SimulationSystem.frameIndex, active,
                out _, out _);
            BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
        }

        private void BeginBoarding(Entity bus)
        {
            VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
            transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
            transport.m_State |= PublicTransportFlags.EnRoute | PublicTransportFlags.Boarding;
            // Native StartBoarding starts the window CLOSED at 0 and each StopBoarding tick widens it
            // to m_MinWaitingDistance + 1, admitting the nearest waiting cims one wave at a time.
            // Opening it fully here breaks that ratchet, so match the native starting value exactly.
            transport.m_DepartureFrame = m_SimulationSystem.frameIndex + 64u;
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
            if (BoardingPolicy.ShouldExposeBoardingToVehicleAi(active.UsesNativeBoarding != 0))
                transport.m_State |= PublicTransportFlags.Boarding;
            else
                transport.m_State &= ~PublicTransportFlags.Boarding;
            EntityManager.SetComponentData(bus, transport);
            EntityManager.SetComponentData(bus, new ConcurrentBoardingActive
            {
                Stop = stop,
                Route = active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus),
                SelectedForVehicleAi = selected ? (byte)1 : (byte)0,
                UsesNativeBoarding = active.UsesNativeBoarding,
                AdmittedFrame = active.AdmittedFrame != 0u
                    ? active.AdmittedFrame
                    : m_SimulationSystem.frameIndex,
                Waypoint = active.Waypoint,
                SelectedForPassengers = selected ? (byte)1 : (byte)0,
                LastPassengerCount = active.LastPassengerCount,
                IdleAttempts = active.IdleAttempts,
                SawWaitingPassenger = active.SawWaitingPassenger,
                DoorsClosing = active.DoorsClosing
            });
        }

        private Entity GetCurrentRoute(Entity bus) => EntityManager.HasComponent<CurrentRoute>(bus)
            ? EntityManager.GetComponentData<CurrentRoute>(bus).m_Route
            : Entity.Null;

        private void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
            {
                list = RentList();
                groups.Add(stop, list);
            }
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
        private const uint HealthReportFrames = 4096u;

        private EntityQuery m_Buses;
        private SimulationSystem m_SimulationSystem;
        private uint m_LastReportFrame;
        private int m_ExpiredSessions;
        private int m_NativeCompletions;
        private int m_ManagedCompletions;
        private int m_CompletionAttempts;
        private int m_GateDwell;
        private int m_GateDistance;
        private int m_GatePassengers;
        private int m_GateSettled;
        private int m_BlockedByWaypoint;
        private int m_StickySlotHolds;
        private int m_RepaidSessions;
        private float m_RepaidFrames;
        private float m_LastRepayBefore;
        private float m_LastRepayAfter;
        private int m_SessionsThatSawAWaitingCim;
        private int m_PassengersBoarded;
        private int m_PassengersAlighted;
        private int m_UnreadyPassengers;
        private int m_UnreadyForOtherVehicle;
        private int m_DoorsClosed;

        private readonly Dictionary<Entity, List<Entity>> m_Boarding = new();
        // See ConcurrentBoardingSystem: Stack<T> is ambiguous under net48 here.
        private readonly List<List<Entity>> m_ListPool = new();

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
            // Runs every frame, so reuse the grouping rather than allocating it each time.
            foreach (KeyValuePair<Entity, List<Entity>> entry in m_Boarding)
            {
                entry.Value.Clear();
                m_ListPool.Add(entry.Value);
            }
            m_Boarding.Clear();
            Dictionary<Entity, List<Entity>> boarding = m_Boarding;
            using (NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity bus in buses)
                {
                    ConcurrentBoardingActive active = EntityManager.GetComponentData<ConcurrentBoardingActive>(bus);
                    // Kill switch: release immediately and hand the bus back to native AI.
                    if (Mod.Settings != null && !Mod.Settings.EnableConcurrentBoarding)
                    {
                        RepayHeldTime(active);
                        BoardingHelpers.ForceReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }
                    // Unconditional deadline. Whatever the session state, a bus may never be held
                    // beyond the configured dwell; otherwise a single stuck session removes a
                    // vehicle from its line permanently and the line's service decays.
                    if (BoardingPolicy.HasSessionExpired(m_SimulationSystem.frameIndex, active.AdmittedFrame,
                            Mod.GetManagedBoardingTimeoutFrames()))
                    {
                        CrashBreadcrumbs.Write($"session-expired bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(active.Stop)}");
                        m_ExpiredSessions++;
                        TryAdvanceToNextWaypoint(bus);
                        BeginRouteHandoff(bus, active.Route);
                        RepayHeldTime(active);
                        BoardingHelpers.ForceReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }
                    if (!BoardingHelpers.CanManageRouteContext(EntityManager, bus, active.Route))
                    {
                        CrashBreadcrumbs.Write($"active-removed invalid-route bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(active.Route)}");
                        RepayHeldTime(active);
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }
                    EnsureRouteAssociation(bus, active);
                    if (!BoardingHelpers.IsBus(EntityManager, bus) ||
                        !BoardingHelpers.TryGetStop(EntityManager, bus, out Entity stop))
                    {
                        CrashBreadcrumbs.Write($"active-removed no-stop bus={CrashBreadcrumbs.Id(bus)}");
                        BeginRouteHandoff(bus, active.Route);
                        RepayHeldTime(active);
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                        continue;
                    }

                    VehiclePublicTransport transport = EntityManager.GetComponentData<VehiclePublicTransport>(bus);
                    if (active.Stop != stop)
                    {
                        CrashBreadcrumbs.Write($"active-complete bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(active.Stop)} next={CrashBreadcrumbs.Id(stop)}");
                        BeginRouteHandoff(bus, active.Route);
                        RepayHeldTime(active);
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
                            m_NativeCompletions++;
                            BeginRouteHandoff(bus, active.Route);
                            RepayHeldTime(active);
                        BoardingHelpers.ReleaseConcurrentBoarding(EntityManager, bus, active);
                            continue;
                        }
                        if (BoardingPolicy.ShouldCompleteManagedBoarding(
                                active.UsesNativeBoarding != 0, true, m_SimulationSystem.frameIndex,
                                active.AdmittedFrame, BoardingPolicy.NativeCompletionGraceFrames) &&
                            TryCompleteBoarding(bus, stop, ref transport, ref active))
                        {
                            CrashBreadcrumbs.Write($"active-complete follower bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                            m_ManagedCompletions++;
                            BeginRouteHandoff(bus, active.Route);
                            RepayHeldTime(active);
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

                // Never rotate away from a bus that still has a cim climbing aboard. That cim holds
                // CurrentVehicle without the Ready flag and can only finish while the slot points at
                // its bus; rotating strands it, and an unready passenger blocks its bus from ever
                // departing. The dwell deadline bounds this hold, so it cannot starve the stop.
                if (slot.m_Vehicle != Entity.Null &&
                    entry.Value.Contains(slot.m_Vehicle) &&
                    !BoardingHelpers.ArePassengersReady(EntityManager, slot.m_Vehicle))
                {
                    m_StickySlotHolds++;
                    continue;
                }

                // The winner is chosen once per 16-frame tick by ConcurrentBoardingSystem, in the
                // same pass that runs before the car AI. Holding the slot for that whole tick is
                // what lets native StopBoarding's BoardingVehicle.m_Vehicle test actually succeed;
                // recomputing the rotation here from frameIndex drifts out of phase with the AI.
                Entity selected = entry.Value[0];
                foreach (Entity bus in entry.Value)
                {
                    if (EntityManager.GetComponentData<ConcurrentBoardingActive>(bus).SelectedForPassengers != 0)
                    {
                        selected = bus;
                        break;
                    }
                }
                if (slot.m_Vehicle == selected)
                    continue;
                slot.m_Vehicle = selected;
                EntityManager.SetComponentData(stop, slot);
            }

            ReportSessionHealth();
        }

        // Bounded ridership-decay telemetry: if the active session count or the oldest session age
        // climbs monotonically across a session, buses are being latched and never released.
        private void ReportSessionHealth()
        {
            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastReportFrame < HealthReportFrames)
                return;
            m_LastReportFrame = frame;

            int active = m_Buses.CalculateEntityCount();
            uint oldest = 0u;
            using (NativeArray<Entity> buses = m_Buses.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity bus in buses)
                {
                    uint admitted = EntityManager.GetComponentData<ConcurrentBoardingActive>(bus).AdmittedFrame;
                    if (admitted != 0u && frame > admitted && frame - admitted > oldest)
                        oldest = frame - admitted;
                }
            }
            int ended = m_NativeCompletions + m_ManagedCompletions + m_ExpiredSessions;
            Mod.Log.Info(
                $"Concurrent boarding health: {active} active, oldest {oldest} frames; " +
                $"ended={ended} (native={m_NativeCompletions} managed={m_ManagedCompletions} " +
                $"expired={m_ExpiredSessions}); sessions that ever saw a waiting cim=" +
                $"{m_SessionsThatSawAWaitingCim}; boarded={m_PassengersBoarded} " +
                $"alighted={m_PassengersAlighted}; sticky={m_StickySlotHolds}.");
            // Gates are independent: one attempt can fail several at once. Percentages are of
            // attempts, not of each other.
            Mod.Log.Info(
                $"Concurrent boarding gates: attempts={m_CompletionAttempts} dwell={m_GateDwell} " +
                $"distance={m_GateDistance} passengers={m_GatePassengers} settled={m_GateSettled} " +
                $"waypoint={m_BlockedByWaypoint}; doors closed={m_DoorsClosed}; " +
                $"unready passengers={m_UnreadyPassengers} " +
                $"of which pointing at another vehicle={m_UnreadyForOtherVehicle}.");
            Mod.Log.Info(
                $"Line time repaid: {m_RepaidSessions} sessions, {(int)m_RepaidFrames}f total, " +
                $"last correction {m_LastRepayBefore:0.#} -> {m_LastRepayAfter:0.#}.");
        }

        private void RepayHeldTime(ConcurrentBoardingActive active)
        {
            float repay = BoardingHelpers.RepayHeldTime(EntityManager,
                m_SimulationSystem.frameIndex, active, out float before, out float after);
            if (repay <= 0f)
                return;

            m_RepaidSessions++;
            m_RepaidFrames += repay;
            m_LastRepayBefore = before;
            m_LastRepayAfter = after;
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

        private bool TryCompleteBoarding(Entity bus, Entity stop, ref VehiclePublicTransport transport,
            ref ConcurrentBoardingActive active)
        {
            uint frame = m_SimulationSystem.frameIndex;
            bool timedOut = BoardingPolicy.HasBoardingTimedOut(
                frame, transport.m_DepartureFrame, Mod.GetManagedBoardingTimeoutFrames());
            if (timedOut)
                CrashBreadcrumbs.Write($"boarding-timeout follower bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");

            // Measured before the ratchet overwrites it. This is the un-masked version of the
            // question the old distance counter was supposed to answer: did the resident AI ever
            // find a waiting cim near this bus at all?
            if (transport.m_MinWaitingDistance != float.MaxValue && active.SawWaitingPassenger == 0)
            {
                active.SawWaitingPassenger = 1;
                m_SessionsThatSawAWaitingCim++;
            }

            transport.m_MaxBoardingDistance = transport.m_MinWaitingDistance == float.MaxValue ||
                transport.m_MinWaitingDistance == 0f || timedOut
                ? float.MaxValue
                : transport.m_MinWaitingDistance + 1f;
            transport.m_MinWaitingDistance = float.MaxValue;

            // The native waiting-distance ratchet assumes one bus serves the whole queue. Here the
            // passenger slot rotates between concurrent buses, so a busy stop can keep resupplying
            // a nearby waiting cim and the ratchet never closes. Track this bus's own exchange
            // instead: once its passenger count stops changing across consecutive attempts, its
            // share of the boarding is finished whatever the queue is still doing.
            int passengers = BoardingHelpers.GetPassengerCount(EntityManager, bus);
            if (passengers != active.LastPassengerCount)
            {
                int delta = passengers - active.LastPassengerCount;
                if (delta > 0)
                    m_PassengersBoarded += delta;
                else
                    m_PassengersAlighted -= delta;
                active.LastPassengerCount = passengers;
                active.IdleAttempts = 0;
            }
            else if (active.IdleAttempts < byte.MaxValue)
            {
                active.IdleAttempts++;
            }
            bool passengersReady = ArePassengersReady(bus);

            // Every gate measured independently on every attempt. The previous if/else chain only
            // ever reported the first failing gate, and exchangeSettled silently masked the
            // distance gate entirely, which is what made the round 5 reading worthless.
            m_CompletionAttempts++;
            if (frame < transport.m_DepartureFrame)
                m_GateDwell++;
            if (transport.m_MaxBoardingDistance != float.MaxValue)
                m_GateDistance++;
            if (!passengersReady)
            {
                m_GatePassengers++;
                BoardingHelpers.CountUnreadyPassengers(EntityManager, bus,
                    out int unready, out int unreadyForOtherVehicle);
                m_UnreadyPassengers += unready;
                m_UnreadyForOtherVehicle += unreadyForOtherVehicle;
            }
            if (active.IdleAttempts >= BoardingPolicy.IdleAttemptsBeforeDeparture)
                m_GateSettled++;

            // Phase two: shut the doors, but only on the window cap. The native ratchet needs
            // several ticks to widen from 0, so closing early on a quiet passenger count made buses
            // leave before anyone could board.
            if (BoardingPolicy.ShouldCloseDoors(active.DoorsClosing != 0, frame, active.AdmittedFrame,
                    BoardingPolicy.BoardingWindowFrames))
            {
                active.DoorsClosing = 1;
                m_DoorsClosed++;
            }

            if (active.DoorsClosing != 0)
            {
                // Keep the window shut so no new cim starts boarding while the last ones finish.
                transport.m_MaxBoardingDistance = 0f;
                if (!BoardingPolicy.CanDepartAfterDoorsClosed(passengersReady, timedOut))
                    return false;
            }
            else if (!BoardingPolicy.CanFinishBoarding(frame, transport.m_DepartureFrame,
                    transport.m_MaxBoardingDistance, passengersReady, timedOut))
            {
                return false;
            }
            if (!TryAdvanceToNextWaypoint(bus))
            {
                m_BlockedByWaypoint++;
                return false;
            }

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
            return BoardingHelpers.ArePassengersReady(EntityManager, bus);
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

        private void Add(Dictionary<Entity, List<Entity>> groups, Entity stop, Entity bus)
        {
            if (!groups.TryGetValue(stop, out List<Entity> list))
            {
                int last = m_ListPool.Count - 1;
                if (last < 0)
                {
                    list = new List<Entity>();
                }
                else
                {
                    list = m_ListPool[last];
                    m_ListPool.RemoveAt(last);
                }
                groups.Add(stop, list);
            }
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
                BoardingPolicy.LimitWaitingBoundsToReach(bounds.x, bounds.y, zone.Curve.m_Length,
                    zone.Direction, BoardingPolicy.PassengerReachDistance,
                    out float reachStart, out float reachEnd);
                uint hash = math.hash(new uint2((uint)entity.Index, (uint)entity.Version));
                float unit = (hash & 65535u) / 65535f;
                float progress = BoardingPolicy.WaitingPosition(reachStart, reachEnd, zone.Direction, unit);
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
        // Diagnostic counterpart to ArePassengersReady. Reports how many passengers in this bus's
        // buffer are unready, and how many of those hold a CurrentVehicle pointing at some other
        // vehicle. ArePassengersReady does not check vehicle identity, so a passenger whose
        // CurrentVehicle is not this bus would block departure indefinitely.
        internal static void CountUnreadyPassengers(EntityManager entityManager, Entity bus,
            out int unready, out int unreadyForOtherVehicle)
        {
            unready = 0;
            unreadyForOtherVehicle = 0;
            if (bus == Entity.Null || !entityManager.Exists(bus) ||
                !entityManager.HasBuffer<Passenger>(bus))
                return;
            DynamicBuffer<Passenger> passengers = entityManager.GetBuffer<Passenger>(bus, true);
            foreach (Passenger passenger in passengers)
            {
                if (!entityManager.HasComponent<CurrentVehicle>(passenger.m_Passenger))
                    continue;
                CurrentVehicle current = entityManager.GetComponentData<CurrentVehicle>(passenger.m_Passenger);
                if ((current.m_Flags & CreatureVehicleFlags.Ready) != 0)
                    continue;
                unready++;
                if (current.m_Vehicle != bus)
                    unreadyForOtherVehicle++;
            }
        }

        // A passenger that holds CurrentVehicle without the Ready flag is mid-transition into that
        // bus. It blocks departure, and it can only finish while the stop's BoardingVehicle slot
        // still points at the bus it is climbing into.
        internal static bool ArePassengersReady(EntityManager entityManager, Entity bus)
        {
            if (bus == Entity.Null || !entityManager.Exists(bus) ||
                !entityManager.HasBuffer<Passenger>(bus))
                return true;
            DynamicBuffer<Passenger> passengers = entityManager.GetBuffer<Passenger>(bus, true);
            foreach (Passenger passenger in passengers)
            {
                if (entityManager.HasComponent<CurrentVehicle>(passenger.m_Passenger) &&
                    (entityManager.GetComponentData<CurrentVehicle>(passenger.m_Passenger).m_Flags &
                        CreatureVehicleFlags.Ready) == 0)
                    return false;
            }
            return true;
        }

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

        /// <summary>
        /// Gives back the time a session held a bus beyond a normal dwell.
        ///
        /// Installed IL: TransportBoardingJob.BeginBoarding derives
        /// VehicleTiming.m_AverageTravelTime from the gap between departures, and
        /// TransportLineTickJob.RefreshLineSegments uses that value as a floor on each route
        /// segment's duration, which sums into the line's duration and so its pathfinding cost. Held
        /// time falls inside that gap, so without this the mod makes the lines it helps look
        /// permanently slower and residents stop being routed to their stops.
        /// </summary>
        internal static float RepayHeldTime(EntityManager entityManager, uint frame,
            ConcurrentBoardingActive active, out float before, out float after)
        {
            before = 0f;
            after = 0f;
            Entity waypoint = active.Waypoint;
            if (waypoint == Entity.Null || !entityManager.Exists(waypoint) ||
                !entityManager.HasComponent<VehicleTiming>(waypoint))
                return 0f;

            float repay = BoardingPolicy.HeldTimeToRepay(frame, active.AdmittedFrame,
                BoardingPolicy.ManagedDepartureFrames);
            if (repay <= 0f)
                return 0f;

            VehicleTiming timing = entityManager.GetComponentData<VehicleTiming>(waypoint);
            before = timing.m_AverageTravelTime;
            timing.m_AverageTravelTime = math.max(0f, before - repay);
            after = timing.m_AverageTravelTime;
            entityManager.SetComponentData(waypoint, timing);
            return repay;
        }

        // Deadline escape hatch. Unlike ReleaseConcurrentBoarding this also clears a native session's
        // boarding state, because an expired native session is exactly the case where the car AI has
        // stopped making progress and must be handed a clean, movable vehicle.
        internal static void ForceReleaseConcurrentBoarding(
            EntityManager entityManager, Entity bus, ConcurrentBoardingActive active)
        {
            if (bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<VehiclePublicTransport>(bus))
            {
                VehiclePublicTransport transport = entityManager.GetComponentData<VehiclePublicTransport>(bus);
                transport.m_State &= ~(PublicTransportFlags.Boarding | PublicTransportFlags.Testing |
                    PublicTransportFlags.RequireStop | PublicTransportFlags.Arriving);
                transport.m_State |= PublicTransportFlags.EnRoute;
                transport.m_MaxBoardingDistance = float.MaxValue;
                transport.m_MinWaitingDistance = float.MaxValue;
                entityManager.SetComponentData(bus, transport);
            }

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

            if (bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<ConcurrentBoardingActive>(bus))
                entityManager.RemoveComponent<ConcurrentBoardingActive>(bus);
        }

        internal static void ReleaseConcurrentBoarding(
            EntityManager entityManager, Entity bus, ConcurrentBoardingActive active)
        {
            // Only a synthetic session invented the Boarding flag, so only it may clear it.
            if (active.UsesNativeBoarding == 0 && bus != Entity.Null && entityManager.Exists(bus) &&
                entityManager.HasComponent<VehiclePublicTransport>(bus))
            {
                VehiclePublicTransport transport = entityManager.GetComponentData<VehiclePublicTransport>(bus);
                transport.m_State &= ~PublicTransportFlags.Boarding;
                entityManager.SetComponentData(bus, transport);
            }

            // The stop slot must always be released, whatever kind of session this was.
            // PassengerDistributionSystem writes BoardingVehicle.m_Vehicle for every active session,
            // so leaving it set points the stop at a bus that has driven away. Installed IL shows
            // TransportBoardingJob.BeginBoarding aborts when the slot is held by another vehicle in
            // the Boarding state - which that departed bus will be at its next stop - so the
            // abandoned stop can never board anyone again.
            if (bus != Entity.Null && active.Stop != Entity.Null && entityManager.Exists(active.Stop) &&
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

        internal static int GetPassengerCount(EntityManager entityManager, Entity vehicle)
        {
            return entityManager.HasBuffer<Passenger>(vehicle)
                ? entityManager.GetBuffer<Passenger>(vehicle, true).Length
                : 0;
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
