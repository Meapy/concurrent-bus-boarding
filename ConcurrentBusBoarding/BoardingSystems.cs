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
using Unity.Jobs;
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

    // Enableable rather than added and removed. Starting or ending a session is then an ordinary
    // write a simulation job can make, and takes effect at once for every later reader in the same
    // simulation frame. Adding or removing it was a structural change: from the main thread that
    // forced every running job in the city to finish first, and deferred through a command buffer
    // it would not land until the end of the rendered frame, several simulation frames later.
    // BoardingStateProvisionSystem gives every road public-transport vehicle both components,
    // disabled, before it can reach a stop.
    internal struct ConcurrentBoardingActive : IComponentData, IEnableableComponent
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

    // Enableable for the same reason as ConcurrentBoardingActive.
    internal struct ConcurrentRouteHandoff : IComponentData, IEnableableComponent
    {
        internal Entity Route;
        internal uint ExpiresFrame;
    }

    /// <summary>
    /// Gives every road public-transport vehicle a disabled <see cref="ConcurrentBoardingActive"/>
    /// and <see cref="ConcurrentRouteHandoff"/>, so the simulation jobs only ever enable and disable
    /// them. Runs in Modification1, where structural changes belong, through that phase's barrier.
    /// A vehicle only needs them once it reaches a stop, and it spawns at a depot, so it always has
    /// them in time; ConcurrentBoardingSystem leaves a vehicle without them to native AI regardless.
    /// </summary>
    public partial class BoardingStateProvisionSystem : GameSystemBase
    {
        private EntityQuery m_WithoutSession;
        private EntityQuery m_WithoutHandoff;
        private ModificationBarrier1 m_Barrier;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            // Absent, not None: None would also match the disabled components this system adds.
            m_WithoutSession = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<VehiclePublicTransport>(), ComponentType.ReadOnly<CarCurrentLane>() },
                Absent = new[] { ComponentType.ReadOnly<ConcurrentBoardingActive>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            });
            m_WithoutHandoff = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<VehiclePublicTransport>(), ComponentType.ReadOnly<CarCurrentLane>() },
                Absent = new[] { ComponentType.ReadOnly<ConcurrentRouteHandoff>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Game.Tools.Temp>() }
            });
            m_Barrier = World.GetOrCreateSystemManaged<ModificationBarrier1>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Chunk-level checks; neither waits for a job. The usual answer is that nothing is missing.
            if (!m_WithoutSession.IsEmptyIgnoreFilter)
                Provision<ConcurrentBoardingActive>(m_WithoutSession);
            if (!m_WithoutHandoff.IsEmptyIgnoreFilter)
                Provision<ConcurrentRouteHandoff>(m_WithoutHandoff);
        }

        private void Provision<T>(EntityQuery query) where T : unmanaged, IComponentData, IEnableableComponent
        {
            using NativeArray<Entity> vehicles = query.ToEntityArray(Allocator.Temp);
            EntityCommandBuffer buffer = m_Barrier.CreateCommandBuffer();
            buffer.AddComponent<T>(vehicles);
            foreach (Entity vehicle in vehicles)
                buffer.SetComponentEnabled<T>(vehicle, false);
        }
    }

    public partial class ConcurrentBoardingSystem : GameSystemBase
    {
        private const uint ReportFrames = 4096u;
        private const int ContendedVisits = 0;
        private const int SingleBusVisits = 1;

        private EntityQuery m_Buses;
        private EntityQuery m_Stops;
        private SimulationSystem m_SimulationSystem;
        private SimulationLookups m_Lookups;
        private ComponentLookup<ConcurrentBoardingActive> m_Active;
        private NativeArray<int> m_Counters;
        private JobHandle m_PreviousJob;
        private uint m_Turn;
        private uint m_LastReportFrame;

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
            m_Lookups = SimulationLookups.Create(this);
            m_Active = GetComponentLookup<ConcurrentBoardingActive>(false);
            m_Counters = new NativeArray<int>(2, Allocator.Persistent);
            RequireForUpdate(m_Buses);
            RequireForUpdate(m_Stops);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            m_PreviousJob.Complete();
            m_Counters.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Runtime kill switch. Active sessions are released by PassengerDistributionSystem,
            // which hands each bus straight back to native AI.
            if (Mod.Settings != null && !Mod.Settings.EnableConcurrentBoarding)
                return;

            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastReportFrame >= ReportFrames)
            {
                // The counters belong to the previous tick's job, sixteen frames ago, so completing it
                // is normally free. The totals are cumulative, so reporting one tick behind loses nothing.
                m_LastReportFrame = frame;
                m_PreviousJob.Complete();
                Mod.LogInfo(
                    $"Concurrent boarding engagement: contended stop visits={m_Counters[ContendedVisits]}, " +
                    $"single-bus visits left to native AI={m_Counters[SingleBusVisits]}.");
            }

            m_Lookups.Update(this);
            m_Active.Update(this);
            NativeList<Entity> buses = m_Buses.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            JobHandle handle = new AdmissionJob
            {
                m_Buses = buses,
                m_Lookups = m_Lookups,
                m_Active = m_Active,
                m_Counters = m_Counters,
                m_Frame = frame,
                m_Turn = m_Turn
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));
            buses.Dispose(handle);
            m_PreviousJob = handle;
            Dependency = handle;

            m_Turn++;
        }

        // Not Burst compiled: it shares the managed zone-geometry code with the overlay, and handles
        // a few hundred vehicles once every sixteen frames. What mattered was getting it off the main
        // thread, where it made the main thread wait for the whole simulation.
        private struct AdmissionJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buses;
            public SimulationLookups m_Lookups;
            public ComponentLookup<ConcurrentBoardingActive> m_Active;
            public NativeArray<int> m_Counters;
            public uint m_Frame;
            public uint m_Turn;

            public void Execute()
            {
                StopGroups busesByStop = new StopGroups(Allocator.Temp);
                NativeList<Entity> group = new NativeList<Entity>(8, Allocator.Temp);
                NativeList<Entity> activeBuses = new NativeList<Entity>(8, Allocator.Temp);
                try
                {
                    CollectCandidates(ref busesByStop);
                    for (int index = 0; index < busesByStop.Count; index++)
                    {
                        busesByStop.GetBuses(index, group);
                        ManageStop(busesByStop.GetStop(index), group, activeBuses);
                    }
                }
                finally
                {
                    activeBuses.Dispose();
                    group.Dispose();
                    busesByStop.Dispose();
                }
            }

            private void CollectCandidates(ref StopGroups busesByStop)
            {
                for (int index = 0; index < m_Buses.Length; index++)
                {
                    Entity bus = m_Buses[index];
                    if (!BoardingHelpers.IsBus(ref m_Lookups, bus))
                        continue;
                    bool managed = IsActive(bus);
                    ConcurrentBoardingActive active = managed ? m_Active[bus] : default;
                    if (!BoardingHelpers.HasLoadedCarPrefab(ref m_Lookups, bus, out Entity vehiclePrefab))
                    {
                        CrashBreadcrumbs.Write($"boarding-skip unresolved-prefab bus={CrashBreadcrumbs.Id(bus)} prefab={CrashBreadcrumbs.Id(vehiclePrefab)}");
                        if (managed)
                            AbandonSession(bus, active);
                        continue;
                    }

                    if (!BoardingHelpers.TryGetStop(ref m_Lookups, bus, out Entity stop))
                    {
                        if (managed)
                            AbandonSession(bus, active);
                        continue;
                    }

                    Entity route = managed && active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus);
                    if (!BoardingHelpers.CanManageRouteContext(ref m_Lookups, bus, route))
                    {
                        if (managed)
                            AbandonSession(bus, active);
                        continue;
                    }

                    VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];
                    const PublicTransportFlags approaching = PublicTransportFlags.EnRoute |
                        PublicTransportFlags.Arriving | PublicTransportFlags.Testing |
                        PublicTransportFlags.Boarding | PublicTransportFlags.RequireStop;
                    if (!managed && (transport.m_State & approaching) == 0)
                        continue;

                    // Zone geometry is deliberately NOT resolved here. Building it walks the route's
                    // segment and path-element buffers and allocates, and it is only needed for
                    // stops the mod might actually manage.
                    busesByStop.Add(stop, bus);
                }
            }

            private void ManageStop(Entity stop, NativeList<Entity> buses, NativeList<Entity> activeBuses)
            {
                // Cheap gate first. A stop with a single bus and no live session is left entirely to
                // native AI, so resolving its boarding zone would be wasted work - and that is the
                // overwhelming majority of stop visits.
                bool hasSession = false;
                foreach (Entity bus in buses)
                {
                    if (IsActive(bus))
                    {
                        hasSession = true;
                        break;
                    }
                }
                if (buses.Length <= 1 && !hasSession)
                {
                    m_Counters[SingleBusVisits]++;
                    return;
                }

                // Resolve the zone once per candidate stop rather than once per bus.
                bool hasZone = false;
                BoardingZone zone = default;
                foreach (Entity bus in buses)
                    BoardingHelpers.ObserveZone(ref m_Lookups, stop, bus, ref hasZone, ref zone);

                bool pullIn = hasZone && zone.IsPullIn;
                activeBuses.Clear();
                float occupiedLength = 0f;
                BoardingVehicle slot = m_Lookups.m_BoardingVehicle[stop];

                int contenders = 0;
                foreach (Entity bus in buses)
                {
                    if (IsActive(bus))
                    {
                        activeBuses.Add(bus);
                        occupiedLength += BoardingHelpers.GetVehicleLength(ref m_Lookups, bus);
                    }
                    if (hasZone && BoardingHelpers.IsCloseToStop(ref m_Lookups, bus, zone))
                        contenders++;
                }

                // With one bus at the stop there is no contention to resolve, so leave it entirely
                // to native AI: no session, no hold, no slot override. Sessions already running are
                // not disturbed, so a departing partner cannot cut another bus's boarding short.
                bool engage = BoardingPolicy.ShouldEngageConcurrentBoarding(contenders);
                if (!engage && activeBuses.Length == 0)
                {
                    m_Counters[SingleBusVisits]++;
                    return;
                }
                if (engage)
                    m_Counters[ContendedVisits]++;

                foreach (Entity bus in buses)
                {
                    if (IsActive(bus))
                        continue;
                    VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];

                    if ((transport.m_State & PublicTransportFlags.Boarding) == 0)
                        continue;

                    bool closeToStop = hasZone && BoardingHelpers.IsCloseToStop(ref m_Lookups, bus, zone);
                    float candidateLength = BoardingHelpers.GetVehicleLength(ref m_Lookups, bus);
                    if (!hasZone)
                        continue;
                    if (!engage ||
                        !BoardingPolicy.CanAdmit(zone.IsCustom, pullIn, activeBuses.Length, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), closeToStop))
                        continue;
                    // Not yet provisioned: leave it to native AI until it is. See BoardingStateProvisionSystem.
                    if (!m_Active.HasComponent(bus))
                        continue;

                    // Do not inherit native's far-future departure frame; see ClampManagedDeparture.
                    transport.m_DepartureFrame = BoardingPolicy.ClampManagedDeparture(
                        m_Frame, transport.m_DepartureFrame);
                    m_Lookups.m_PublicTransport[bus] = transport;
                    StartSession(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus),
                        UsesNativeBoarding = 1,
                        AdmittedFrame = m_Frame,
                        Waypoint = m_Lookups.m_Target[bus].m_Target,
                        LastPassengerCount = BoardingHelpers.GetPassengerCount(ref m_Lookups, bus)
                    });
                    activeBuses.Add(bus);
                    occupiedLength += candidateLength;
                }

                foreach (Entity bus in buses)
                {
                    if (IsActive(bus))
                        continue;

                    bool closeToStop = hasZone && BoardingHelpers.IsCloseToStop(ref m_Lookups, bus, zone);
                    if (!closeToStop)
                        continue;
                    float candidateLength = BoardingHelpers.GetVehicleLength(ref m_Lookups, bus);
                    bool canAdmit = engage && BoardingPolicy.CanAdmit(
                        zone.IsCustom, pullIn, activeBuses.Length, occupiedLength,
                        candidateLength, BoardingHelpers.GetZoneLength(zone), true);
                    if (!canAdmit)
                        continue;

                    VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];
                    if (BoardingPolicy.ShouldRequestStop(
                            canAdmit, (transport.m_State & PublicTransportFlags.Boarding) != 0) &&
                        (transport.m_State & PublicTransportFlags.RequireStop) == 0)
                    {
                        transport.m_State |= PublicTransportFlags.RequireStop;
                        m_Lookups.m_PublicTransport[bus] = transport;
                        CrashBreadcrumbs.Write($"require-stop bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    }

                    if (!BoardingPolicy.CanBeginSyntheticBoarding(activeBuses.Length) ||
                        BoardingHelpers.GetSpeed(ref m_Lookups, bus) >
                            BoardingPolicy.BoardingSpeedTolerance)
                        continue;
                    if (!m_Active.HasComponent(bus))
                        continue;

                    CrashBreadcrumbs.Write($"boarding-begin before bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    BeginBoarding(bus);
                    CrashBreadcrumbs.Write($"boarding-begin state-written bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    StartSession(bus, new ConcurrentBoardingActive
                    {
                        Stop = stop,
                        Route = GetCurrentRoute(bus),
                        AdmittedFrame = m_Frame,
                        Waypoint = m_Lookups.m_Target[bus].m_Target,
                        LastPassengerCount = BoardingHelpers.GetPassengerCount(ref m_Lookups, bus)
                    });
                    CrashBreadcrumbs.Write($"boarding-begin active-added bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                    activeBuses.Add(bus);
                    occupiedLength += candidateLength;
                    if (slot.m_Testing == bus)
                        slot.m_Testing = Entity.Null;
                }

                if (activeBuses.Length == 0)
                {
                    m_Lookups.m_BoardingVehicle[stop] = slot;
                    return;
                }

                Entity selected = activeBuses[BoardingPolicy.RotationIndex(activeBuses.Length, m_Turn, (uint)stop.Index)];
                foreach (Entity bus in activeBuses)
                    PrepareForVehicleAi(bus, stop, bus == selected);

                if (slot.m_Vehicle == Entity.Null || BoardingHelpers.IsBus(ref m_Lookups, slot.m_Vehicle))
                    slot.m_Vehicle = selected;
                if (slot.m_Testing != Entity.Null && IsActive(slot.m_Testing))
                    slot.m_Testing = Entity.Null;
                m_Lookups.m_BoardingVehicle[stop] = slot;
            }

            private bool IsActive(Entity bus) => BoardingHelpers.IsSessionActive(ref m_Active, bus);

            private void StartSession(Entity bus, ConcurrentBoardingActive active)
            {
                m_Active[bus] = active;
                m_Active.SetComponentEnabled(bus, true);
            }

            /// <summary>
            /// Drops a session whose context is no longer valid, repaying the line time it held first.
            /// </summary>
            private void AbandonSession(Entity bus, ConcurrentBoardingActive active)
            {
                BoardingHelpers.RepayHeldTime(ref m_Lookups, m_Frame, active, out _, out _);
                BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
            }

            private void BeginBoarding(Entity bus)
            {
                VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];
                transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
                transport.m_State |= PublicTransportFlags.EnRoute | PublicTransportFlags.Boarding;
                // Native StartBoarding starts the window CLOSED at 0 and each StopBoarding tick widens it
                // to m_MinWaitingDistance + 1, admitting the nearest waiting cims one wave at a time.
                // Opening it fully here breaks that ratchet, so match the native starting value exactly.
                transport.m_DepartureFrame = m_Frame + 64u;
                transport.m_MaxBoardingDistance = 0f;
                transport.m_MinWaitingDistance = float.MaxValue;
                m_Lookups.m_PublicTransport[bus] = transport;
            }

            private void PrepareForVehicleAi(Entity bus, Entity stop, bool selected)
            {
                VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];
                ConcurrentBoardingActive active = m_Active[bus];
                transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
                transport.m_State |= PublicTransportFlags.EnRoute;
                if (BoardingPolicy.ShouldExposeBoardingToVehicleAi(active.UsesNativeBoarding != 0))
                    transport.m_State |= PublicTransportFlags.Boarding;
                else
                    transport.m_State &= ~PublicTransportFlags.Boarding;
                m_Lookups.m_PublicTransport[bus] = transport;
                m_Active[bus] = new ConcurrentBoardingActive
                {
                    Stop = stop,
                    Route = active.Route != Entity.Null ? active.Route : GetCurrentRoute(bus),
                    SelectedForVehicleAi = selected ? (byte)1 : (byte)0,
                    UsesNativeBoarding = active.UsesNativeBoarding,
                    AdmittedFrame = active.AdmittedFrame != 0u
                        ? active.AdmittedFrame
                        : m_Frame,
                    Waypoint = active.Waypoint,
                    SelectedForPassengers = selected ? (byte)1 : (byte)0,
                    LastPassengerCount = active.LastPassengerCount,
                    IdleAttempts = active.IdleAttempts,
                    SawWaitingPassenger = active.SawWaitingPassenger,
                    DoorsClosing = active.DoorsClosing
                };
            }

            private Entity GetCurrentRoute(Entity bus) =>
                m_Lookups.m_CurrentRoute.TryGetComponent(bus, out CurrentRoute route) ? route.m_Route : Entity.Null;
        }
    }

    [UpdateAfter(typeof(TransportCarAISystem))]
    [UpdateBefore(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(ResidentAISystem))]
    public partial class PassengerDistributionSystem : GameSystemBase
    {
        private const uint HealthReportFrames = 4096u;

        // Indices into m_Counters. Written by the job, read on the main thread only at report time.
        private const int ExpiredSessions = 0;
        private const int NativeCompletions = 1;
        private const int ManagedCompletions = 2;
        private const int CompletionAttempts = 3;
        private const int GateDwell = 4;
        private const int GateDistance = 5;
        private const int GatePassengers = 6;
        private const int GateSettled = 7;
        private const int BlockedByWaypoint = 8;
        private const int StickySlotHolds = 9;
        private const int RepaidSessions = 10;
        private const int SessionsThatSawAWaitingCim = 11;
        private const int PassengersBoarded = 12;
        private const int PassengersAlighted = 13;
        private const int UnreadyPassengers = 14;
        private const int UnreadyForOtherVehicle = 15;
        private const int DoorsClosed = 16;
        // Snapshot of the sessions left after the job's frame, not cumulative.
        private const int ActiveSessions = 17;
        private const int OldestSession = 18;
        private const int CounterCount = 19;

        // Indices into m_Repayment.
        private const int RepaidFrames = 0;
        private const int LastRepayBefore = 1;
        private const int LastRepayAfter = 2;

        private EntityQuery m_Buses;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private SimulationLookups m_Lookups;
        private ComponentLookup<ConcurrentBoardingActive> m_Active;
        private ComponentLookup<ConcurrentRouteHandoff> m_Handoff;
        private ComponentLookup<PathOwner> m_PathOwner;
        private NativeArray<int> m_Counters;
        private NativeArray<float> m_Repayment;
        private JobHandle m_PreviousJob;
        private uint m_LastReportFrame;

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
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_Lookups = SimulationLookups.Create(this);
            m_Active = GetComponentLookup<ConcurrentBoardingActive>(false);
            m_Handoff = GetComponentLookup<ConcurrentRouteHandoff>(false);
            m_PathOwner = GetComponentLookup<PathOwner>(false);
            m_Counters = new NativeArray<int>(CounterCount, Allocator.Persistent);
            m_Repayment = new NativeArray<float>(3, Allocator.Persistent);
            // Every provisioned bus matches while its session is disabled, because this checks
            // chunks and not enabled bits, so this system runs every frame once any bus exists.
            // Scheduling an empty job is cheaper than the sync it would take to find out.
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnDestroy()
        {
            m_PreviousJob.Complete();
            m_Counters.Dispose();
            m_Repayment.Dispose();
            base.OnDestroy();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastReportFrame >= HealthReportFrames)
            {
                // The one wait left in this system, once per 4,096 frames: the previous frame's
                // job has to finish before its counters can be read.
                m_LastReportFrame = frame;
                m_PreviousJob.Complete();
                ReportSessionHealth();
            }

            m_Lookups.Update(this);
            m_Active.Update(this);
            m_Handoff.Update(this);
            m_PathOwner.Update(this);
            NativeList<Entity> buses = m_Buses.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            JobHandle handle = new DistributionJob
            {
                m_Buses = buses,
                m_Lookups = m_Lookups,
                m_Active = m_Active,
                m_Handoff = m_Handoff,
                m_PathOwner = m_PathOwner,
                // CurrentRoute is a structural change, so it has to wait for the barrier. The native
                // car AI adds and removes CurrentRoute through this same barrier.
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_Counters = m_Counters,
                m_Repayment = m_Repayment,
                m_Frame = frame,
                // Managed state, so it is read here rather than from the job.
                m_Enabled = Mod.Settings == null || Mod.Settings.EnableConcurrentBoarding,
                m_TimeoutFrames = Mod.GetManagedBoardingTimeoutFrames()
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));
            buses.Dispose(handle);
            m_EndFrameBarrier.AddJobHandleForProducer(handle);
            m_PreviousJob = handle;
            Dependency = handle;
        }

        // Bounded ridership-decay telemetry: if the active session count or the oldest session age
        // climbs monotonically across a session, buses are being latched and never released.
        private void ReportSessionHealth()
        {
            int ended = m_Counters[NativeCompletions] + m_Counters[ManagedCompletions] + m_Counters[ExpiredSessions];
            Mod.LogInfo(
                $"Concurrent boarding health: {m_Counters[ActiveSessions]} active, oldest {(uint)m_Counters[OldestSession]} frames; " +
                $"ended={ended} (native={m_Counters[NativeCompletions]} managed={m_Counters[ManagedCompletions]} " +
                $"expired={m_Counters[ExpiredSessions]}); sessions that ever saw a waiting cim=" +
                $"{m_Counters[SessionsThatSawAWaitingCim]}; boarded={m_Counters[PassengersBoarded]} " +
                $"alighted={m_Counters[PassengersAlighted]}; sticky={m_Counters[StickySlotHolds]}.");
            // Gates are independent: one attempt can fail several at once. Percentages are of
            // attempts, not of each other.
            Mod.LogInfo(
                $"Concurrent boarding gates: attempts={m_Counters[CompletionAttempts]} dwell={m_Counters[GateDwell]} " +
                $"distance={m_Counters[GateDistance]} passengers={m_Counters[GatePassengers]} settled={m_Counters[GateSettled]} " +
                $"waypoint={m_Counters[BlockedByWaypoint]}; doors closed={m_Counters[DoorsClosed]}; " +
                $"unready passengers={m_Counters[UnreadyPassengers]} " +
                $"of which pointing at another vehicle={m_Counters[UnreadyForOtherVehicle]}.");
            Mod.LogInfo(
                $"Line time repaid: {m_Counters[RepaidSessions]} sessions, {(int)m_Repayment[RepaidFrames]}f total, " +
                $"last correction {m_Repayment[LastRepayBefore]:0.#} -> {m_Repayment[LastRepayAfter]:0.#}.");
        }

        // Runs after the car AI in the same simulation frame, and must keep doing so every frame:
        // the slot choice has to hold for the whole tick. The dependency on the car AI's jobs comes
        // from the PublicTransport it writes. Not Burst compiled, for the reason given on
        // ConcurrentBoardingSystem.AdmissionJob; it handles the two to four live sessions.
        private struct DistributionJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buses;
            public SimulationLookups m_Lookups;
            public ComponentLookup<ConcurrentBoardingActive> m_Active;
            public ComponentLookup<ConcurrentRouteHandoff> m_Handoff;
            public ComponentLookup<PathOwner> m_PathOwner;
            public EntityCommandBuffer m_CommandBuffer;
            public NativeArray<int> m_Counters;
            public NativeArray<float> m_Repayment;
            public uint m_Frame;
            public bool m_Enabled;
            public uint m_TimeoutFrames;

            public void Execute()
            {
                StopGroups boarding = new StopGroups(Allocator.Temp);
                NativeList<Entity> group = new NativeList<Entity>(8, Allocator.Temp);
                try
                {
                    for (int index = 0; index < m_Buses.Length; index++)
                        UpdateSession(m_Buses[index], ref boarding);
                    for (int index = 0; index < boarding.Count; index++)
                    {
                        boarding.GetBuses(index, group);
                        AssignStopSlot(boarding.GetStop(index), group);
                    }
                    RecordSessionSnapshot();
                }
                finally
                {
                    group.Dispose();
                    boarding.Dispose();
                }
            }

            private void UpdateSession(Entity bus, ref StopGroups boarding)
            {
                ConcurrentBoardingActive active = m_Active[bus];
                // Kill switch: release immediately and hand the bus back to native AI.
                if (!m_Enabled)
                {
                    RepayHeldTime(active);
                    BoardingHelpers.ForceReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                    return;
                }
                // Unconditional deadline. Whatever the session state, a bus may never be held
                // beyond the configured dwell; otherwise a single stuck session removes a
                // vehicle from its line permanently and the line's service decays.
                if (BoardingPolicy.HasSessionExpired(m_Frame, active.AdmittedFrame, m_TimeoutFrames))
                {
                    CrashBreadcrumbs.Write($"session-expired bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(active.Stop)}");
                    m_Counters[ExpiredSessions]++;
                    TryAdvanceToNextWaypoint(bus, Entity.Null);
                    BeginRouteHandoff(bus, active.Route);
                    RepayHeldTime(active);
                    BoardingHelpers.ForceReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                    return;
                }
                if (!BoardingHelpers.CanManageRouteContext(ref m_Lookups, bus, active.Route))
                {
                    CrashBreadcrumbs.Write($"active-removed invalid-route bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(active.Route)}");
                    RepayHeldTime(active);
                    BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                    return;
                }
                Entity restoredRoute = EnsureRouteAssociation(bus, active);
                if (!BoardingHelpers.IsBus(ref m_Lookups, bus) ||
                    !BoardingHelpers.TryGetStop(ref m_Lookups, bus, out Entity stop))
                {
                    CrashBreadcrumbs.Write($"active-removed no-stop bus={CrashBreadcrumbs.Id(bus)}");
                    BeginRouteHandoff(bus, active.Route);
                    RepayHeldTime(active);
                    BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                    return;
                }

                VehiclePublicTransport transport = m_Lookups.m_PublicTransport[bus];
                if (active.Stop != stop)
                {
                    CrashBreadcrumbs.Write($"active-complete bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(active.Stop)} next={CrashBreadcrumbs.Id(stop)}");
                    BeginRouteHandoff(bus, active.Route);
                    RepayHeldTime(active);
                    BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                    return;
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
                        m_Counters[NativeCompletions]++;
                        BeginRouteHandoff(bus, active.Route);
                        RepayHeldTime(active);
                        BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                        return;
                    }
                    if (BoardingPolicy.ShouldCompleteManagedBoarding(
                            active.UsesNativeBoarding != 0, true, m_Frame,
                            active.AdmittedFrame, BoardingPolicy.NativeCompletionGraceFrames) &&
                        TryCompleteBoarding(bus, stop, restoredRoute, ref transport, ref active))
                    {
                        CrashBreadcrumbs.Write($"active-complete follower bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");
                        m_Counters[ManagedCompletions]++;
                        BeginRouteHandoff(bus, active.Route);
                        RepayHeldTime(active);
                        BoardingHelpers.ReleaseConcurrentBoarding(ref m_Lookups, ref m_Active, bus, active);
                        return;
                    }
                    m_Active[bus] = active;
                }

                transport.m_State &= ~(PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
                transport.m_State |= PublicTransportFlags.EnRoute | PublicTransportFlags.Boarding;
                m_Lookups.m_PublicTransport[bus] = transport;
                boarding.Add(stop, bus);
            }

            private void AssignStopSlot(Entity stop, NativeList<Entity> buses)
            {
                if (!m_Lookups.m_BoardingVehicle.HasComponent(stop))
                    return;
                BoardingVehicle slot = m_Lookups.m_BoardingVehicle[stop];
                if (slot.m_Vehicle != Entity.Null && !BoardingHelpers.IsBus(ref m_Lookups, slot.m_Vehicle))
                    return;

                // Never rotate away from a bus that still has a cim climbing aboard. That cim holds
                // CurrentVehicle without the Ready flag and can only finish while the slot points at
                // its bus; rotating strands it, and an unready passenger blocks its bus from ever
                // departing. The dwell deadline bounds this hold, so it cannot starve the stop.
                if (slot.m_Vehicle != Entity.Null &&
                    Contains(buses, slot.m_Vehicle) &&
                    !BoardingHelpers.ArePassengersReady(ref m_Lookups, slot.m_Vehicle))
                {
                    m_Counters[StickySlotHolds]++;
                    return;
                }

                // The winner is chosen once per 16-frame tick by ConcurrentBoardingSystem, in the
                // same pass that runs before the car AI. Holding the slot for that whole tick is
                // what lets native StopBoarding's BoardingVehicle.m_Vehicle test actually succeed;
                // recomputing the rotation here from frameIndex drifts out of phase with the AI.
                Entity selected = buses[0];
                foreach (Entity bus in buses)
                {
                    if (m_Active[bus].SelectedForPassengers != 0)
                    {
                        selected = bus;
                        break;
                    }
                }
                if (slot.m_Vehicle == selected)
                    return;
                slot.m_Vehicle = selected;
                m_Lookups.m_BoardingVehicle[stop] = slot;
            }

            // What the health report used to measure when it ran: the sessions still live after
            // this frame, and the age of the oldest.
            private void RecordSessionSnapshot()
            {
                int live = 0;
                uint oldest = 0u;
                for (int index = 0; index < m_Buses.Length; index++)
                {
                    Entity bus = m_Buses[index];
                    if (!BoardingHelpers.IsSessionActive(ref m_Active, bus))
                        continue;
                    live++;
                    uint admitted = m_Active[bus].AdmittedFrame;
                    if (admitted != 0u && m_Frame > admitted && m_Frame - admitted > oldest)
                        oldest = m_Frame - admitted;
                }
                m_Counters[ActiveSessions] = live;
                m_Counters[OldestSession] = (int)oldest;
            }

            private static bool Contains(NativeList<Entity> buses, Entity bus)
            {
                foreach (Entity candidate in buses)
                {
                    if (candidate == bus)
                        return true;
                }
                return false;
            }

            private void RepayHeldTime(ConcurrentBoardingActive active)
            {
                float repay = BoardingHelpers.RepayHeldTime(ref m_Lookups, m_Frame, active,
                    out float before, out float after);
                if (repay <= 0f)
                    return;

                m_Counters[RepaidSessions]++;
                m_Repayment[RepaidFrames] += repay;
                m_Repayment[LastRepayBefore] = before;
                m_Repayment[LastRepayAfter] = after;
            }

            private void BeginRouteHandoff(Entity bus, Entity route)
            {
                if (!BoardingHelpers.CanManageRouteContext(ref m_Lookups, bus, route))
                    return;

                var handoff = new ConcurrentRouteHandoff
                {
                    Route = route,
                    ExpiresFrame = m_Frame + 512u
                };
                if (m_Handoff.HasComponent(bus))
                {
                    m_Handoff[bus] = handoff;
                    m_Handoff.SetComponentEnabled(bus, true);
                }
                else
                {
                    // Not yet provisioned; RouteHandoffSystem only reads it every 16 frames anyway.
                    m_CommandBuffer.AddComponent(bus, handoff);
                }
            }

            // Returns the route it asked the barrier to restore, or Entity.Null. The restore is not
            // visible until the barrier plays back at the end of the rendered frame, so a completion
            // in the same frame needs the route passed to it directly.
            private Entity EnsureRouteAssociation(Entity bus, ConcurrentBoardingActive active)
            {
                if (m_Lookups.m_CurrentRoute.HasComponent(bus) ||
                    !BoardingHelpers.CanManageRouteContext(ref m_Lookups, bus, active.Route))
                    return Entity.Null;

                CrashBreadcrumbs.Write($"route-restored bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(active.Route)}");
                m_CommandBuffer.AddComponent(bus, new CurrentRoute(active.Route));
                return active.Route;
            }

            private bool TryCompleteBoarding(Entity bus, Entity stop, Entity restoredRoute,
                ref VehiclePublicTransport transport, ref ConcurrentBoardingActive active)
            {
                uint frame = m_Frame;
                bool timedOut = BoardingPolicy.HasBoardingTimedOut(
                    frame, transport.m_DepartureFrame, m_TimeoutFrames);
                if (timedOut)
                    CrashBreadcrumbs.Write($"boarding-timeout follower bus={CrashBreadcrumbs.Id(bus)} stop={CrashBreadcrumbs.Id(stop)}");

                // Measured before the ratchet overwrites it. This is the un-masked version of the
                // question the old distance counter was supposed to answer: did the resident AI ever
                // find a waiting cim near this bus at all?
                if (transport.m_MinWaitingDistance != float.MaxValue && active.SawWaitingPassenger == 0)
                {
                    active.SawWaitingPassenger = 1;
                    m_Counters[SessionsThatSawAWaitingCim]++;
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
                int passengers = BoardingHelpers.GetPassengerCount(ref m_Lookups, bus);
                if (passengers != active.LastPassengerCount)
                {
                    int delta = passengers - active.LastPassengerCount;
                    if (delta > 0)
                        m_Counters[PassengersBoarded] += delta;
                    else
                        m_Counters[PassengersAlighted] -= delta;
                    active.LastPassengerCount = passengers;
                    active.IdleAttempts = 0;
                }
                else if (active.IdleAttempts < byte.MaxValue)
                {
                    active.IdleAttempts++;
                }
                bool passengersReady = BoardingHelpers.ArePassengersReady(ref m_Lookups, bus);

                // Every gate measured independently on every attempt. The previous if/else chain only
                // ever reported the first failing gate, and exchangeSettled silently masked the
                // distance gate entirely, which is what made the round 5 reading worthless.
                m_Counters[CompletionAttempts]++;
                if (frame < transport.m_DepartureFrame)
                    m_Counters[GateDwell]++;
                if (transport.m_MaxBoardingDistance != float.MaxValue)
                    m_Counters[GateDistance]++;
                if (!passengersReady)
                {
                    m_Counters[GatePassengers]++;
                    BoardingHelpers.CountUnreadyPassengers(ref m_Lookups, bus,
                        out int unready, out int unreadyForOtherVehicle);
                    m_Counters[UnreadyPassengers] += unready;
                    m_Counters[UnreadyForOtherVehicle] += unreadyForOtherVehicle;
                }
                if (active.IdleAttempts >= BoardingPolicy.IdleAttemptsBeforeDeparture)
                    m_Counters[GateSettled]++;

                // Phase two: shut the doors, but only on the window cap. The native ratchet needs
                // several ticks to widen from 0, so closing early on a quiet passenger count made buses
                // leave before anyone could board.
                if (BoardingPolicy.ShouldCloseDoors(active.DoorsClosing != 0, frame, active.AdmittedFrame,
                        BoardingPolicy.BoardingWindowFrames))
                {
                    active.DoorsClosing = 1;
                    m_Counters[DoorsClosed]++;
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
                if (!TryAdvanceToNextWaypoint(bus, restoredRoute))
                {
                    m_Counters[BlockedByWaypoint]++;
                    return false;
                }

                transport.m_State &= ~(PublicTransportFlags.Arriving | PublicTransportFlags.Boarding |
                    PublicTransportFlags.Testing | PublicTransportFlags.RequireStop);
                transport.m_State |= PublicTransportFlags.EnRoute;
                m_Lookups.m_PublicTransport[bus] = transport;

                BoardingVehicle slot = m_Lookups.m_BoardingVehicle[stop];
                if (slot.m_Vehicle == bus)
                {
                    slot.m_Vehicle = Entity.Null;
                    m_Lookups.m_BoardingVehicle[stop] = slot;
                }
                return true;
            }

            // restoredRoute stands in for a CurrentRoute this job has asked the barrier to add but
            // which has not been played back yet; the old main-thread code added it immediately.
            private bool TryAdvanceToNextWaypoint(Entity bus, Entity restoredRoute)
            {
                Entity route;
                if (m_Lookups.m_CurrentRoute.TryGetComponent(bus, out CurrentRoute currentRoute))
                    route = currentRoute.m_Route;
                else if (restoredRoute != Entity.Null)
                    route = restoredRoute;
                else
                    return false;
                if (!m_PathOwner.HasComponent(bus) || !m_Lookups.m_Target.HasComponent(bus))
                    return false;

                PathOwner pathOwner = m_PathOwner[bus];
                Target target = m_Lookups.m_Target[bus];
                if (!BoardingHelpers.IsUsableRouteWaypoint(ref m_Lookups, route, target.m_Target))
                    return false;
                Waypoint waypoint = m_Lookups.m_Waypoint[target.m_Target];

                DynamicBuffer<RouteWaypoint> waypoints = m_Lookups.m_RouteWaypoints[route];
                if (waypoints.Length == 0 || waypoint.m_Index < 0 || waypoint.m_Index >= waypoints.Length)
                    return false;

                Entity oldWaypoint = target.m_Target;
                Entity nextWaypoint = waypoints[(waypoint.m_Index + 1) % waypoints.Length].m_Waypoint;
                if (nextWaypoint == oldWaypoint ||
                    !BoardingHelpers.IsUsableRouteWaypoint(ref m_Lookups, route, nextWaypoint))
                    return false;

                CrashBreadcrumbs.Write($"completion-target before bus={CrashBreadcrumbs.Id(bus)} old={CrashBreadcrumbs.Id(oldWaypoint)} next={CrashBreadcrumbs.Id(nextWaypoint)}");
                VehicleUtils.SetTarget(ref pathOwner, ref target, nextWaypoint);
                m_PathOwner[bus] = pathOwner;
                m_Lookups.m_Target[bus] = target;
                CrashBreadcrumbs.Write($"completion-target after bus={CrashBreadcrumbs.Id(bus)} next={CrashBreadcrumbs.Id(nextWaypoint)}");
                return true;
            }
        }
    }

    [UpdateAfter(typeof(TransportCarAISystem))]
    [UpdateBefore(typeof(PassengerDistributionSystem))]
    public partial class RouteHandoffSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private SimulationLookups m_Lookups;
        private ComponentLookup<ConcurrentRouteHandoff> m_Handoff;

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
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_Lookups = SimulationLookups.Create(this);
            m_Handoff = GetComponentLookup<ConcurrentRouteHandoff>(false);
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            m_Lookups.Update(this);
            m_Handoff.Update(this);
            NativeList<Entity> buses = m_Buses.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            JobHandle handle = new HandoffJob
            {
                m_Buses = buses,
                m_Lookups = m_Lookups,
                m_Handoff = m_Handoff,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_Frame = m_SimulationSystem.frameIndex
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));
            buses.Dispose(handle);
            m_EndFrameBarrier.AddJobHandleForProducer(handle);
            Dependency = handle;
        }

        private struct HandoffJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buses;
            public SimulationLookups m_Lookups;
            public ComponentLookup<ConcurrentRouteHandoff> m_Handoff;
            public EntityCommandBuffer m_CommandBuffer;
            public uint m_Frame;

            public void Execute()
            {
                for (int index = 0; index < m_Buses.Length; index++)
                {
                    Entity bus = m_Buses[index];
                    ConcurrentRouteHandoff handoff = m_Handoff[bus];
                    if (m_Frame >= handoff.ExpiresFrame ||
                        !BoardingHelpers.CanManageRouteContext(ref m_Lookups, bus, handoff.Route))
                    {
                        m_Handoff.SetComponentEnabled(bus, false);
                        continue;
                    }

                    if (m_Lookups.m_CurrentRoute.TryGetComponent(bus, out CurrentRoute currentRoute))
                    {
                        if (currentRoute.m_Route != handoff.Route)
                            m_Handoff.SetComponentEnabled(bus, false);
                        continue;
                    }

                    CrashBreadcrumbs.Write($"route-handoff restored bus={CrashBreadcrumbs.Id(bus)} route={CrashBreadcrumbs.Id(handoff.Route)}");
                    m_CommandBuffer.AddComponent(bus, new CurrentRoute(handoff.Route));
                }
            }
        }
    }

    [UpdateAfter(typeof(CarNavigationSystem))]
    [UpdateBefore(typeof(CarMoveSystem))]
    public partial class BoardingHoldSystem : GameSystemBase
    {
        private EntityQuery m_Buses;
        private ComponentLookup<CarNavigation> m_Navigation;
        private ComponentLookup<Moving> m_Moving;

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
            m_Navigation = GetComponentLookup<CarNavigation>(false);
            m_Moving = GetComponentLookup<Moving>(false);
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            // Scheduled, not run here. CarNavigation is written by the navigation job over every car in
            // the city, so touching it from the main thread waited for that whole job each frame. As a
            // job it is ordered after navigation and before movement by the components it declares.
            m_Navigation.Update(this);
            m_Moving.Update(this);
            NativeList<Entity> buses = m_Buses.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            JobHandle handle = new HoldJob
            {
                m_Buses = buses,
                m_Navigation = m_Navigation,
                m_Moving = m_Moving
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));
            buses.Dispose(handle);
            Dependency = handle;
        }

        private struct HoldJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Buses;
            public ComponentLookup<CarNavigation> m_Navigation;
            public ComponentLookup<Moving> m_Moving;

            public void Execute()
            {
#if CBB_DIAGNOSTICS
                if (m_Buses.Length != s_LastActiveCount)
                {
                    s_LastActiveCount = m_Buses.Length;
                    CrashBreadcrumbs.Write($"hold active={m_Buses.Length}");
                }
#endif
                for (int index = 0; index < m_Buses.Length; index++)
                {
                    Entity bus = m_Buses[index];
                    CarNavigation navigation = m_Navigation[bus];
                    navigation.m_MaxSpeed = 0f;
                    m_Navigation[bus] = navigation;

                    Moving moving = m_Moving[bus];
                    moving.m_Velocity = float3.zero;
                    moving.m_AngularVelocity = float3.zero;
                    m_Moving[bus] = moving;
                }
            }
        }

#if CBB_DIAGNOSTICS
        // Only the hold job touches it, and each frame's job depends on the last through CarNavigation.
        private static int s_LastActiveCount = -1;
#endif
    }

    internal static class BoardingHelpers
    {
        internal static bool IsSessionActive(ref ComponentLookup<ConcurrentBoardingActive> active, Entity bus)
        {
            return active.HasComponent(bus) && active.IsComponentEnabled(bus);
        }

        // Main-thread form for diagnostics. A disabled session is no session.
        internal static bool IsSessionActive(EntityManager entityManager, Entity bus)
        {
            return entityManager.HasComponent<ConcurrentBoardingActive>(bus) &&
                entityManager.IsComponentEnabled<ConcurrentBoardingActive>(bus);
        }

        // Diagnostic counterpart to ArePassengersReady. Reports how many passengers in this bus's
        // buffer are unready, and how many of those hold a CurrentVehicle pointing at some other
        // vehicle. ArePassengersReady does not check vehicle identity, so a passenger whose
        // CurrentVehicle is not this bus would block departure indefinitely.
        internal static void CountUnreadyPassengers(ref SimulationLookups data, Entity bus,
            out int unready, out int unreadyForOtherVehicle)
        {
            unready = 0;
            unreadyForOtherVehicle = 0;
            if (bus == Entity.Null || !data.Exists(bus) ||
                !data.m_Passengers.TryGetBuffer(bus, out DynamicBuffer<Passenger> passengers))
                return;
            foreach (Passenger passenger in passengers)
            {
                if (!data.m_CurrentVehicle.TryGetComponent(passenger.m_Passenger, out CurrentVehicle current))
                    continue;
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
        internal static bool ArePassengersReady(ref SimulationLookups data, Entity bus)
        {
            if (bus == Entity.Null || !data.Exists(bus) ||
                !data.m_Passengers.TryGetBuffer(bus, out DynamicBuffer<Passenger> passengers))
                return true;
            foreach (Passenger passenger in passengers)
            {
                if (data.m_CurrentVehicle.TryGetComponent(passenger.m_Passenger, out CurrentVehicle current) &&
                    (current.m_Flags & CreatureVehicleFlags.Ready) == 0)
                    return false;
            }
            return true;
        }

        internal static bool CanManageRouteContext(ref SimulationLookups data, Entity bus, Entity route)
        {
            bool validRoute = bus != Entity.Null && data.Exists(bus) &&
                data.m_PublicTransport.HasComponent(bus) &&
                data.m_Target.HasComponent(bus) &&
                !data.IsDeleted(bus) &&
                !data.IsTemp(bus) &&
                !data.m_TripSource.HasComponent(bus) &&
                !data.m_OutOfControl.HasComponent(bus);
            if (!validRoute)
                return false;

            if (data.m_CurrentRoute.TryGetComponent(bus, out CurrentRoute currentRoute) &&
                currentRoute.m_Route != route)
                return false;

            VehiclePublicTransport transport = data.m_PublicTransport[bus];
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
            Entity target = data.m_Target[bus].m_Target;
            return BoardingPolicy.CanRestoreRoute(
                IsUsableRoute(ref data, route),
                IsUsableRouteWaypoint(ref data, route, target),
                isRetiring);
        }

        internal static bool IsUsableRouteWaypoint(ref SimulationLookups data, Entity route, Entity waypoint)
        {
            if (!IsUsableRoute(ref data, route) || waypoint == Entity.Null ||
                !data.Exists(waypoint) ||
                data.IsDeleted(waypoint) ||
                data.IsTemp(waypoint) ||
                !data.m_Waypoint.TryGetComponent(waypoint, out Waypoint waypointData) ||
                !data.m_Owner.TryGetComponent(waypoint, out Owner owner) ||
                owner.m_Owner != route)
                return false;

            DynamicBuffer<RouteWaypoint> waypoints = data.m_RouteWaypoints[route];
            return waypointData.m_Index >= 0 && waypointData.m_Index < waypoints.Length &&
                waypoints[waypointData.m_Index].m_Waypoint == waypoint;
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
        internal static float RepayHeldTime(ref SimulationLookups data, uint frame,
            ConcurrentBoardingActive active, out float before, out float after)
        {
            before = 0f;
            after = 0f;
            Entity waypoint = active.Waypoint;
            if (waypoint == Entity.Null || !data.Exists(waypoint) ||
                !data.m_VehicleTiming.HasComponent(waypoint))
                return 0f;

            float repay = BoardingPolicy.HeldTimeToRepay(frame, active.AdmittedFrame,
                BoardingPolicy.ManagedDepartureFrames);
            if (repay <= 0f)
                return 0f;

            VehicleTiming timing = data.m_VehicleTiming[waypoint];
            before = timing.m_AverageTravelTime;
            timing.m_AverageTravelTime = math.max(0f, before - repay);
            after = timing.m_AverageTravelTime;
            data.m_VehicleTiming[waypoint] = timing;
            return repay;
        }

        // Deadline escape hatch. Unlike ReleaseConcurrentBoarding this also clears a native session's
        // boarding state, because an expired native session is exactly the case where the car AI has
        // stopped making progress and must be handed a clean, movable vehicle.
        internal static void ForceReleaseConcurrentBoarding(ref SimulationLookups data,
            ref ComponentLookup<ConcurrentBoardingActive> sessions, Entity bus, ConcurrentBoardingActive active)
        {
            if (bus != Entity.Null && data.Exists(bus) &&
                data.m_PublicTransport.HasComponent(bus))
            {
                VehiclePublicTransport transport = data.m_PublicTransport[bus];
                transport.m_State &= ~(PublicTransportFlags.Boarding | PublicTransportFlags.Testing |
                    PublicTransportFlags.RequireStop | PublicTransportFlags.Arriving);
                transport.m_State |= PublicTransportFlags.EnRoute;
                transport.m_MaxBoardingDistance = float.MaxValue;
                transport.m_MinWaitingDistance = float.MaxValue;
                data.m_PublicTransport[bus] = transport;
            }

            ReleaseStopSlot(ref data, bus, active);
            EndSession(ref data, ref sessions, bus);
        }

        internal static void ReleaseConcurrentBoarding(ref SimulationLookups data,
            ref ComponentLookup<ConcurrentBoardingActive> sessions, Entity bus, ConcurrentBoardingActive active)
        {
            // Only a synthetic session invented the Boarding flag, so only it may clear it.
            if (active.UsesNativeBoarding == 0 && bus != Entity.Null && data.Exists(bus) &&
                data.m_PublicTransport.HasComponent(bus))
            {
                VehiclePublicTransport transport = data.m_PublicTransport[bus];
                transport.m_State &= ~PublicTransportFlags.Boarding;
                data.m_PublicTransport[bus] = transport;
            }

            // The stop slot must always be released, whatever kind of session this was.
            // PassengerDistributionSystem writes BoardingVehicle.m_Vehicle for every active session,
            // so leaving it set points the stop at a bus that has driven away. Installed IL shows
            // TransportBoardingJob.BeginBoarding aborts when the slot is held by another vehicle in
            // the Boarding state - which that departed bus will be at its next stop - so the
            // abandoned stop can never board anyone again.
            if (bus != Entity.Null)
                ReleaseStopSlot(ref data, bus, active);
            EndSession(ref data, ref sessions, bus);
        }

        private static void ReleaseStopSlot(ref SimulationLookups data, Entity bus, ConcurrentBoardingActive active)
        {
            if (active.Stop == Entity.Null || !data.Exists(active.Stop) ||
                !data.m_BoardingVehicle.HasComponent(active.Stop))
                return;

            BoardingVehicle slot = data.m_BoardingVehicle[active.Stop];
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
                data.m_BoardingVehicle[active.Stop] = slot;
        }

        // Disabling rather than removing: the change is visible to every later system in this
        // simulation frame, exactly as the removal it replaces was.
        private static void EndSession(ref SimulationLookups data,
            ref ComponentLookup<ConcurrentBoardingActive> sessions, Entity bus)
        {
            if (bus != Entity.Null && data.Exists(bus) && sessions.HasComponent(bus))
                sessions.SetComponentEnabled(bus, false);
        }

        private static bool IsUsableRoute(ref SimulationLookups data, Entity route)
        {
            return route != Entity.Null && data.Exists(route) &&
                !data.IsDeleted(route) &&
                !data.IsTemp(route) &&
                data.m_RouteWaypoints.HasBuffer(route);
        }

        // Fills <paramref name="result"/> rather than returning a new dictionary, because this runs on a
        // timer and allocating one per refresh is pure garbage.
        //
        // When either restrict entity is set, only buses whose stop is one of them are resolved. Resolving a
        // zone walks the stop's route lanes and inbound path elements, so in the default selected-only
        // display mode the unrestricted form did that work for every bus in the city and then drew at most
        // two stops. The filter is applied before IsBus because TryGetStop is the cheaper of the two.
        internal static void FindObservedZones(EntityManager entityManager, EntityQuery busQuery,
            Dictionary<Entity, BoardingZone> result, Entity restrictToA, Entity restrictToB)
        {
            result.Clear();
            var data = new EntityManagerAccess(entityManager);
            bool restricted = restrictToA != Entity.Null || restrictToB != Entity.Null;
            using NativeArray<Entity> buses = busQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity bus in buses)
            {
                if (!TryGetStop(ref data, bus, out Entity stop))
                    continue;
                if (restricted && stop != restrictToA && stop != restrictToB)
                    continue;
                if (!IsBus(ref data, bus))
                    continue;
                bool found = result.TryGetValue(stop, out BoardingZone zone);
                ObserveZone(ref data, stop, bus, ref found, ref zone);
                if (found)
                    result[stop] = zone;
            }
        }

        // Keeps the preferred of the zones seen so far for one stop. hasZone and zone carry that
        // running choice between calls.
        internal static void ObserveZone<TData>(ref TData data, Entity stop, Entity bus,
            ref bool hasZone, ref BoardingZone zone) where TData : struct, IBoardingAccess
        {
            if (!IsPassengerBusStop(ref data, stop) ||
                !TryGetPhysicalZone(ref data, stop, bus, out BoardingZone candidate))
                return;

            if (!hasZone ||
                BoardingPolicy.PreferZoneCandidate(zone.StopDistance, zone.IsPullIn, zone.IsPhysical,
                    candidate.StopDistance, candidate.IsPullIn, candidate.IsPhysical))
            {
                zone = candidate;
                hasZone = true;
            }
        }

        internal static bool TryGetStopZone(EntityManager entityManager, Entity stop, out BoardingZone zone)
        {
            var data = new EntityManagerAccess(entityManager);
            return TryGetStopZone(ref data, stop, out zone);
        }

        internal static bool TryGetStopZone<TData>(ref TData data, Entity stop, out BoardingZone zone)
            where TData : struct, IBoardingAccess
        {
            zone = default;
            if (!IsPassengerBusStop(ref data, stop) ||
                !data.TryGetConnectedRoutes(stop, out DynamicBuffer<ConnectedRoute> routes))
                return false;

            bool found = false;
            foreach (ConnectedRoute route in routes)
            {
                if (!TryGetWaypointZone(ref data, stop, Entity.Null, route.m_Waypoint,
                    out BoardingZone candidate))
                    continue;
                if (!found || BoardingPolicy.PreferZoneCandidate(zone.StopDistance, zone.IsPullIn, zone.IsPhysical,
                    candidate.StopDistance, candidate.IsPullIn, candidate.IsPhysical))
                    zone = candidate;
                found = true;
            }
            return found;
        }

        private static bool TryGetPhysicalZone<TData>(ref TData data, Entity stop, Entity bus, out BoardingZone zone)
            where TData : struct, IBoardingAccess
        {
            data.TryGetTarget(bus, out Target target);
            return TryGetWaypointZone(ref data, stop, bus, target.m_Target, out zone);
        }

        private static bool TryGetWaypointZone<TData>(ref TData data, Entity stop, Entity bus, Entity waypoint,
            out BoardingZone zone) where TData : struct, IBoardingAccess
        {
            zone = default;
            if (!data.TryGetRouteLane(waypoint, out RouteLane routeLane))
                return false;

            Entity lane = Entity.Null;
            Curve curve = default;
            float width = 3.5f;
            float stopDistance = float.MaxValue;
            bool hasStopPosition = data.TryGetRoutePosition(waypoint, out Game.Routes.Position stopPositionData);
            float3 stopWorldPosition = hasStopPosition ? stopPositionData.m_Position : default;
            int routeDirection = routeLane.m_EndCurvePos >= routeLane.m_StartCurvePos ? 1 : -1;
            int direction = routeDirection;
            bool physical = false;

            // A bus already beside its target stop proves which physical side/lane it is using. Compare its
            // current lane with the native final EndOfPath: the current lane may still be an adjacent approach
            // lane, while the final lane is the actual bay. Farther-away buses contribute neither.
            if (bus != Entity.Null && hasStopPosition && data.TryGetTransform(bus, out Transform busTransform) &&
                math.distance(busTransform.m_Position, stopWorldPosition) <=
                    BoardingPolicy.PhysicalLaneCaptureDistance)
            {
                if (data.TryGetCarCurrentLane(bus, out CarCurrentLane current))
                {
                    ConsiderLane(ref data, current.m_Lane,
                        current.m_CurvePosition.z >= current.m_CurvePosition.x ? 1 : -1,
                        hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width, ref stopDistance, ref direction);
                }
                if (data.TryGetCarNavigationLanes(bus, out DynamicBuffer<CarNavigationLane> navigation))
                {
                    if (navigation.Length > 0)
                    {
                        CarNavigationLane last = navigation[navigation.Length - 1];
                        if ((last.m_Flags & Game.Vehicles.CarLaneFlags.EndOfPath) != 0)
                            ConsiderLane(ref data, last.m_Lane,
                                last.m_CurvePosition.y >= last.m_CurvePosition.x ? 1 : -1,
                                hasStopPosition, stopWorldPosition, ref lane, ref curve, ref width,
                                ref stopDistance, ref direction);
                    }
                }
                physical = lane != Entity.Null;
            }

            if (!physical)
            {
                ConsiderLane(ref data, routeLane.m_EndLane, routeDirection, hasStopPosition, stopWorldPosition,
                    ref lane, ref curve, ref width, ref stopDistance, ref direction);
                if (lane == Entity.Null)
                    ConsiderLane(ref data, routeLane.m_StartLane, routeDirection, hasStopPosition, stopWorldPosition,
                        ref lane, ref curve, ref width, ref stopDistance, ref direction);
                if (bus != Entity.Null && lane == Entity.Null &&
                    data.TryGetCarNavigationLanes(bus, out DynamicBuffer<CarNavigationLane> navigation))
                {
                    if (navigation.Length > 0)
                    {
                        CarNavigationLane last = navigation[navigation.Length - 1];
                        if ((last.m_Flags & Game.Vehicles.CarLaneFlags.EndOfPath) != 0)
                            ConsiderLane(ref data, last.m_Lane,
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

            NetSlaveLaneFlags topology = GetSlaveLaneFlags(ref data, lane) |
                GetSlaveLaneFlags(ref data, routeLane.m_StartLane) |
                GetSlaveLaneFlags(ref data, routeLane.m_EndLane);
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
                    IsSecondaryLane(ref data, lane) ||
                    IsSecondaryLane(ref data, routeLane.m_StartLane) ||
                    IsSecondaryLane(ref data, routeLane.m_EndLane),
                    splitsFromRoad,
                    mergesIntoRoad,
                    IsSameOwnerTransition(ref data, routeLane.m_StartLane, routeLane.m_EndLane)),
                Direction = direction,
                StopDistance = stopDistance,
                IsPhysical = physical
            };
            // Before BuildZonePieces, not after: the rear walk is bounded by how much zone is actually
            // wanted, and that depends on whether this stop carries a length override.
            ApplyOverride(ref data, stop, ref zone);
            BuildZonePieces(ref data, waypoint, ref zone);
            return true;
        }

        // How much further than the requested zone length the rear walk will collect, so a zone whose
        // pieces are trimmed at the front still has geometry behind the trim point.
        private const float RearWalkMargin = 12f;
        // Backstop for a route whose path elements never form a contiguous rear chain. Reaching this
        // means the walk gave up, not that the zone is complete.
        private const int MaximumRearWalkElements = 4096;

        private static void BuildZonePieces<TData>(ref TData data, Entity waypoint, ref BoardingZone zone)
            where TData : struct, IBoardingAccess
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

            if (!data.TryGetOwner(waypoint, out Owner owner) || !data.TryGetWaypoint(waypoint, out Waypoint waypointData))
                return;
            Entity route = owner.m_Owner;
            if (route == Entity.Null || !data.TryGetRouteSegments(route, out DynamicBuffer<RouteSegment> segments))
                return;

            if (segments.Length == 0)
                return;
            int waypointIndex = waypointData.m_Index;
            float3 rear = PieceRear(zone.Pieces[0]);
            float available = PieceLength(zone.Pieces[0]);
            bool foundCurrentLane = false;

            // Collect only as much rear geometry as this zone can display. This used to chase
            // MaximumCustomZoneLength (200 m) regardless, so an ordinary 26 m stop kept walking; and
            // because `available` only grows when a piece is actually appended, a chain that broke
            // early left the condition permanently unsatisfiable and the walk continued through every
            // remaining segment's entire PathElement buffer. On a long line that is tens of thousands
            // of component lookups on the frame the stop is selected.
            //
            // A custom stop keeps the full budget: its slider can be dragged out to 200 m without the
            // geometry being re-resolved, so the pieces have to already be there.
            float needed = zone.IsCustom
                ? BoardingPolicy.MaximumCustomZoneLength
                : math.min(BoardingPolicy.MaximumCustomZoneLength,
                    GetRequestedZoneLength(zone) + RearWalkMargin);
            int examined = 0;

            for (int offset = 1; offset <= segments.Length && available < needed; offset++)
            {
                int segmentIndex = (waypointIndex - offset + segments.Length) % segments.Length;
                Entity segment = segments[segmentIndex].m_Segment;
                if (segment == Entity.Null || !data.TryGetPathElements(segment, out DynamicBuffer<PathElement> path))
                    break;
                // A segment that adds nothing once the chain is established has broken it, and every
                // segment after this one is further from the stop still, so none of them can attach.
                bool chainEstablished = foundCurrentLane;
                int piecesBefore = zone.Pieces.Count;
                for (int i = path.Length - 1; i >= 0 && available < needed && examined < MaximumRearWalkElements; i--)
                {
                    examined++;
                    PathElement element = path[i];
                    if (!foundCurrentLane)
                    {
                        if (element.m_Target == zone.Lane)
                            foundCurrentLane = true;
                        continue;
                    }
                    if (element.m_Target == zone.Lane ||
                        !TryGetLaneGeometry(ref data, element.m_Target, out Curve curve, out float width))
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
                if (examined >= MaximumRearWalkElements || (chainEstablished && zone.Pieces.Count == piecesBefore))
                    break;
            }
#if CBB_DIAGNOSTICS
            CrashBreadcrumbs.Write($"zone-rear-walk elements={examined} pieces={zone.Pieces.Count} " +
                $"available={available:0.#} needed={needed:0.#} custom={zone.IsCustom} pullin={zone.IsPullIn}");
#endif
        }

        private static void ConsiderLane<TData>(ref TData data, Entity candidate, int candidateDirection,
            bool hasStopPosition, float3 stopPosition, ref Entity lane, ref Curve curve, ref float width,
            ref float bestDistance, ref int direction) where TData : struct, IBoardingAccess
        {
            if (!TryGetLaneGeometry(ref data, candidate, out Curve candidateCurve, out float candidateWidth))
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
            var data = new EntityManagerAccess(entityManager);
            ApplyOverride(ref data, stop, ref zone);
        }

        internal static void ApplyOverride<TData>(ref TData data, Entity stop, ref BoardingZone zone)
            where TData : struct, IBoardingAccess
        {
            zone.IsCustom = data.TryGetZoneOverride(stop, out BoardingZoneOverride custom);
            if (!zone.IsCustom)
                return;

            zone.CustomOffset = math.isfinite(custom.m_Offset) ? custom.m_Offset : 0f;
            zone.CustomLength = math.isfinite(custom.m_Length)
                ? math.clamp(custom.m_Length, BoardingPolicy.MinimumCustomZoneLength,
                    BoardingPolicy.MaximumCustomZoneLength)
                : BoardingPolicy.MinimumCustomZoneLength;
        }

        internal static bool TryGetLaneGeometry<TData>(ref TData data, Entity lane, out Curve curve, out float width)
            where TData : struct, IBoardingAccess
        {
            curve = default;
            width = 3.5f;
            if (lane == Entity.Null || !data.TryGetNetCarLane(lane, out _) ||
                !data.TryGetCurve(lane, out curve))
                return false;

            if (data.TryGetPrefabRef(lane, out PrefabRef prefabRef) &&
                data.TryGetNetLaneData(prefabRef.m_Prefab, out NetLaneData laneData))
                width = laneData.m_Width;
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

        private static bool IsSecondaryLane<TData>(ref TData data, Entity lane) where TData : struct, IBoardingAccess
        {
            if (lane == Entity.Null)
                return false;
            if (data.HasSecondaryLane(lane))
                return true;
            if (!data.TryGetNetCarLane(lane, out NetCarLane carLane))
                return false;
            return (carLane.m_Flags & (NetCarLaneFlags.SecondaryStart | NetCarLaneFlags.SecondaryEnd)) != 0;
        }

        private static NetSlaveLaneFlags GetSlaveLaneFlags<TData>(ref TData data, Entity lane)
            where TData : struct, IBoardingAccess
        {
            return lane != Entity.Null && data.TryGetSlaveLane(lane, out NetSlaveLane slaveLane)
                ? slaveLane.m_Flags
                : 0;
        }

        private static bool IsSameOwnerTransition<TData>(ref TData data, Entity startLane, Entity endLane)
            where TData : struct, IBoardingAccess
        {
            if (startLane == endLane || startLane == Entity.Null || endLane == Entity.Null ||
                !data.TryGetOwner(startLane, out Owner startOwner) || !data.TryGetOwner(endLane, out Owner endOwner))
                return false;
            return startOwner.m_Owner != Entity.Null && startOwner.m_Owner == endOwner.m_Owner;
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
            var data = new EntityManagerAccess(entityManager);
            return IsPassengerBusStop(ref data, stop);
        }

        internal static bool IsPassengerBusStop<TData>(ref TData data, Entity stop) where TData : struct, IBoardingAccess
        {
            if (stop == Entity.Null || !data.Exists(stop) ||
                data.IsDeleted(stop) ||
                data.IsTemp(stop) ||
                !data.HasBoardingVehicle(stop) ||
                !data.TryGetPrefabRef(stop, out PrefabRef prefabRef))
                return false;
            Entity prefab = prefabRef.m_Prefab;
            if (prefab == Entity.Null || !data.Exists(prefab) ||
                data.IsDeleted(prefab) ||
                data.IsTemp(prefab) ||
                !data.TryGetTransportStopData(prefab, out TransportStopData stopData))
                return false;
            return stopData.m_TransportType == TransportType.Bus && stopData.m_PassengerTransport;
        }

        internal static bool IsBus<TData>(ref TData data, Entity vehicle) where TData : struct, IBoardingAccess
        {
            if (vehicle == Entity.Null || !data.Exists(vehicle) ||
                data.IsDeleted(vehicle) ||
                data.IsTemp(vehicle) ||
                !data.TryGetPrefabRef(vehicle, out PrefabRef prefabRef))
                return false;
            Entity prefab = prefabRef.m_Prefab;
            return prefab != Entity.Null && data.Exists(prefab) &&
                !data.IsDeleted(prefab) &&
                !data.IsTemp(prefab) &&
                data.TryGetPublicTransportVehicleData(prefab, out PublicTransportVehicleData vehicleData) &&
                vehicleData.m_TransportType == TransportType.Bus;
        }

        // The same test PrefabSystem.TryGetPrefab<CarPrefab>(Entity) made from the main thread: it
        // succeeds whenever the prefab entity has PrefabData with a non-negative index, and never
        // checks the prefab's type, so the lookup form is an exact equivalent a job can use.
        internal static bool HasLoadedCarPrefab(ref SimulationLookups data, Entity vehicle, out Entity prefab)
        {
            prefab = Entity.Null;
            if (!data.Exists(vehicle) || !data.TryGetPrefabRef(vehicle, out PrefabRef prefabRef))
                return false;
            prefab = prefabRef.m_Prefab;
            return prefab != Entity.Null && data.Exists(prefab) &&
                !data.IsDeleted(prefab) &&
                !data.IsTemp(prefab) &&
                data.m_PrefabData.TryGetComponent(prefab, out PrefabData prefabData) &&
                prefabData.m_Index >= 0;
        }

        internal static bool TryGetStop(EntityManager entityManager, Entity vehicle, out Entity stop)
        {
            var data = new EntityManagerAccess(entityManager);
            return TryGetStop(ref data, vehicle, out stop);
        }

        internal static bool TryGetStop<TData>(ref TData data, Entity vehicle, out Entity stop)
            where TData : struct, IBoardingAccess
        {
            stop = Entity.Null;
            if (!data.TryGetTarget(vehicle, out Target targetData))
                return false;
            Entity target = targetData.m_Target;
            if (data.HasBoardingVehicle(target))
                stop = target;
            else if (data.TryGetConnected(target, out Connected connected))
                stop = connected.m_Connected;
            return stop != Entity.Null && data.Exists(stop) &&
                !data.IsDeleted(stop) &&
                !data.IsTemp(stop) &&
                data.HasBoardingVehicle(stop);
        }

        internal static int GetPassengerCount(ref SimulationLookups data, Entity vehicle)
        {
            return data.m_Passengers.TryGetBuffer(vehicle, out DynamicBuffer<Passenger> passengers)
                ? passengers.Length
                : 0;
        }

        internal static float GetVehicleLength(ref SimulationLookups data, Entity vehicle)
        {
            float length = 0f;
            if (data.m_LayoutElements.TryGetBuffer(vehicle, out DynamicBuffer<LayoutElement> layout))
            {
                foreach (LayoutElement element in layout)
                    length += GetUnitLength(ref data, element.m_Vehicle);
            }
            return length > 0f ? length : GetUnitLength(ref data, vehicle);
        }

        internal static bool IsCloseToStop(ref SimulationLookups data, Entity vehicle, BoardingZone zone)
        {
            return data.TryGetTransform(vehicle, out Transform transform) &&
                IsPointInside(zone, transform.m_Position);
        }

        internal static float GetSpeed(ref SimulationLookups data, Entity vehicle)
        {
            return data.m_Moving.TryGetComponent(vehicle, out Moving moving)
                ? math.length(moving.m_Velocity)
                : 0f;
        }

        private static float GetUnitLength(ref SimulationLookups data, Entity vehicle)
        {
            if (!data.TryGetPrefabRef(vehicle, out PrefabRef prefabRef))
                return 0f;
            return data.m_ObjectGeometryData.TryGetComponent(prefabRef.m_Prefab, out ObjectGeometryData geometry)
                ? math.max(0f, geometry.m_Size.z)
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
