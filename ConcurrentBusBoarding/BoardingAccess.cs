using Colossal.Mathematics;
using Game.Common;
using Game.Creatures;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using NetCarLane = Game.Net.CarLane;
using NetSecondaryLane = Game.Net.SecondaryLane;
using NetSlaveLane = Game.Net.SlaveLane;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace ConcurrentBusBoarding
{
    /// <summary>
    /// The component reads shared by the simulation jobs and the main-thread overlay, editor and
    /// diagnostics, so stop and zone resolution exists once.
    ///
    /// The simulation systems read through <see cref="SimulationLookups"/> inside jobs. Every
    /// EntityManager read or write in the simulation phase is a sync point: it makes the main thread
    /// wait for every scheduled job that writes the component, which is what made these systems
    /// cost tens of milliseconds a frame while doing almost no work of their own. The main-thread
    /// callers keep <see cref="EntityManagerAccess"/>: they run outside the simulation, rarely, and a
    /// lookup used on the main thread would need a job completion even to test for a component.
    /// </summary>
    internal interface IBoardingAccess
    {
        bool Exists(Entity entity);
        bool IsDeleted(Entity entity);
        bool IsTemp(Entity entity);
        bool HasBoardingVehicle(Entity entity);
        bool HasSecondaryLane(Entity entity);
        bool TryGetPrefabRef(Entity entity, out PrefabRef value);
        bool TryGetTransportStopData(Entity entity, out TransportStopData value);
        bool TryGetPublicTransportVehicleData(Entity entity, out PublicTransportVehicleData value);
        bool TryGetNetLaneData(Entity entity, out NetLaneData value);
        bool TryGetTarget(Entity entity, out Target value);
        bool TryGetConnected(Entity entity, out Connected value);
        bool TryGetRouteLane(Entity entity, out RouteLane value);
        bool TryGetRoutePosition(Entity entity, out Game.Routes.Position value);
        bool TryGetWaypoint(Entity entity, out Waypoint value);
        bool TryGetOwner(Entity entity, out Owner value);
        bool TryGetTransform(Entity entity, out Transform value);
        bool TryGetCarCurrentLane(Entity entity, out CarCurrentLane value);
        bool TryGetNetCarLane(Entity entity, out NetCarLane value);
        bool TryGetCurve(Entity entity, out Curve value);
        bool TryGetSlaveLane(Entity entity, out NetSlaveLane value);
        bool TryGetZoneOverride(Entity entity, out BoardingZoneOverride value);
        bool TryGetCarNavigationLanes(Entity entity, out DynamicBuffer<CarNavigationLane> value);
        bool TryGetConnectedRoutes(Entity entity, out DynamicBuffer<ConnectedRoute> value);
        bool TryGetRouteSegments(Entity entity, out DynamicBuffer<RouteSegment> value);
        bool TryGetPathElements(Entity entity, out DynamicBuffer<PathElement> value);
    }

    /// <summary>Main-thread access. Each read completes only the jobs writing that component.</summary>
    internal struct EntityManagerAccess : IBoardingAccess
    {
        private readonly EntityManager m_EntityManager;

        internal EntityManagerAccess(EntityManager entityManager)
        {
            m_EntityManager = entityManager;
        }

        public bool Exists(Entity entity) => m_EntityManager.Exists(entity);
        public bool IsDeleted(Entity entity) => m_EntityManager.HasComponent<Deleted>(entity);
        public bool IsTemp(Entity entity) => m_EntityManager.HasComponent<Game.Tools.Temp>(entity);
        public bool HasBoardingVehicle(Entity entity) => m_EntityManager.HasComponent<BoardingVehicle>(entity);
        public bool HasSecondaryLane(Entity entity) => m_EntityManager.HasComponent<NetSecondaryLane>(entity);
        public bool TryGetPrefabRef(Entity entity, out PrefabRef value) => TryGet(entity, out value);
        public bool TryGetTransportStopData(Entity entity, out TransportStopData value) => TryGet(entity, out value);
        public bool TryGetPublicTransportVehicleData(Entity entity, out PublicTransportVehicleData value) =>
            TryGet(entity, out value);
        public bool TryGetNetLaneData(Entity entity, out NetLaneData value) => TryGet(entity, out value);
        public bool TryGetTarget(Entity entity, out Target value) => TryGet(entity, out value);
        public bool TryGetConnected(Entity entity, out Connected value) => TryGet(entity, out value);
        public bool TryGetRouteLane(Entity entity, out RouteLane value) => TryGet(entity, out value);
        public bool TryGetRoutePosition(Entity entity, out Game.Routes.Position value) => TryGet(entity, out value);
        public bool TryGetWaypoint(Entity entity, out Waypoint value) => TryGet(entity, out value);
        public bool TryGetOwner(Entity entity, out Owner value) => TryGet(entity, out value);
        public bool TryGetTransform(Entity entity, out Transform value) => TryGet(entity, out value);
        public bool TryGetCarCurrentLane(Entity entity, out CarCurrentLane value) => TryGet(entity, out value);
        public bool TryGetNetCarLane(Entity entity, out NetCarLane value) => TryGet(entity, out value);
        public bool TryGetCurve(Entity entity, out Curve value) => TryGet(entity, out value);
        public bool TryGetSlaveLane(Entity entity, out NetSlaveLane value) => TryGet(entity, out value);
        public bool TryGetZoneOverride(Entity entity, out BoardingZoneOverride value) => TryGet(entity, out value);
        public bool TryGetCarNavigationLanes(Entity entity, out DynamicBuffer<CarNavigationLane> value) =>
            TryGetBuffer(entity, out value);
        public bool TryGetConnectedRoutes(Entity entity, out DynamicBuffer<ConnectedRoute> value) =>
            TryGetBuffer(entity, out value);
        public bool TryGetRouteSegments(Entity entity, out DynamicBuffer<RouteSegment> value) =>
            TryGetBuffer(entity, out value);
        public bool TryGetPathElements(Entity entity, out DynamicBuffer<PathElement> value) =>
            TryGetBuffer(entity, out value);

        private bool TryGet<T>(Entity entity, out T value) where T : unmanaged, IComponentData
        {
            if (!m_EntityManager.HasComponent<T>(entity))
            {
                value = default;
                return false;
            }
            value = m_EntityManager.GetComponentData<T>(entity);
            return true;
        }

        private bool TryGetBuffer<T>(Entity entity, out DynamicBuffer<T> value) where T : unmanaged, IBufferElementData
        {
            if (!m_EntityManager.HasBuffer<T>(entity))
            {
                value = default;
                return false;
            }
            value = m_EntityManager.GetBuffer<T>(entity, true);
            return true;
        }
    }

    /// <summary>
    /// Job-side access for the simulation systems. Scheduling a job that holds these makes the ECS
    /// dependency manager order it after the jobs writing what it reads, and before the jobs reading
    /// what it writes, without the main thread waiting for either.
    ///
    /// The four writable lookups are writable for every system that uses this struct, including the
    /// ones that only read them: a job may not hold a read-only and a writable lookup of the same
    /// type. That costs some ordering between worker jobs, never a main-thread wait.
    /// </summary>
    internal struct SimulationLookups : IBoardingAccess
    {
        [ReadOnly] public EntityStorageInfoLookup m_Entities;
        [ReadOnly] public ComponentLookup<Deleted> m_Deleted;
        [ReadOnly] public ComponentLookup<Game.Tools.Temp> m_Temp;
        [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRef;
        [ReadOnly] public ComponentLookup<PrefabData> m_PrefabData;
        [ReadOnly] public ComponentLookup<TransportStopData> m_TransportStopData;
        [ReadOnly] public ComponentLookup<PublicTransportVehicleData> m_PublicTransportVehicleData;
        [ReadOnly] public ComponentLookup<ObjectGeometryData> m_ObjectGeometryData;
        [ReadOnly] public ComponentLookup<NetLaneData> m_NetLaneData;
        [ReadOnly] public ComponentLookup<Connected> m_Connected;
        [ReadOnly] public ComponentLookup<RouteLane> m_RouteLane;
        [ReadOnly] public ComponentLookup<Game.Routes.Position> m_RoutePosition;
        [ReadOnly] public ComponentLookup<Waypoint> m_Waypoint;
        [ReadOnly] public ComponentLookup<Owner> m_Owner;
        [ReadOnly] public ComponentLookup<CurrentRoute> m_CurrentRoute;
        [ReadOnly] public ComponentLookup<Transform> m_Transform;
        [ReadOnly] public ComponentLookup<Moving> m_Moving;
        [ReadOnly] public ComponentLookup<CarCurrentLane> m_CarCurrentLane;
        [ReadOnly] public ComponentLookup<NetCarLane> m_NetCarLane;
        [ReadOnly] public ComponentLookup<Curve> m_Curve;
        [ReadOnly] public ComponentLookup<NetSlaveLane> m_SlaveLane;
        [ReadOnly] public ComponentLookup<NetSecondaryLane> m_SecondaryLane;
        [ReadOnly] public ComponentLookup<BoardingZoneOverride> m_ZoneOverride;
        [ReadOnly] public ComponentLookup<TripSource> m_TripSource;
        [ReadOnly] public ComponentLookup<OutOfControl> m_OutOfControl;
        [ReadOnly] public ComponentLookup<CurrentVehicle> m_CurrentVehicle;
        [ReadOnly] public BufferLookup<CarNavigationLane> m_CarNavigationLanes;
        [ReadOnly] public BufferLookup<ConnectedRoute> m_ConnectedRoutes;
        [ReadOnly] public BufferLookup<RouteSegment> m_RouteSegments;
        [ReadOnly] public BufferLookup<RouteWaypoint> m_RouteWaypoints;
        [ReadOnly] public BufferLookup<PathElement> m_PathElements;
        [ReadOnly] public BufferLookup<Passenger> m_Passengers;
        [ReadOnly] public BufferLookup<LayoutElement> m_LayoutElements;
        public ComponentLookup<VehiclePublicTransport> m_PublicTransport;
        public ComponentLookup<Target> m_Target;
        public ComponentLookup<BoardingVehicle> m_BoardingVehicle;
        public ComponentLookup<VehicleTiming> m_VehicleTiming;

        internal static SimulationLookups Create(SystemBase system)
        {
            return new SimulationLookups
            {
                m_Entities = system.GetEntityStorageInfoLookup(),
                m_Deleted = system.GetComponentLookup<Deleted>(true),
                m_Temp = system.GetComponentLookup<Game.Tools.Temp>(true),
                m_PrefabRef = system.GetComponentLookup<PrefabRef>(true),
                m_PrefabData = system.GetComponentLookup<PrefabData>(true),
                m_TransportStopData = system.GetComponentLookup<TransportStopData>(true),
                m_PublicTransportVehicleData = system.GetComponentLookup<PublicTransportVehicleData>(true),
                m_ObjectGeometryData = system.GetComponentLookup<ObjectGeometryData>(true),
                m_NetLaneData = system.GetComponentLookup<NetLaneData>(true),
                m_Connected = system.GetComponentLookup<Connected>(true),
                m_RouteLane = system.GetComponentLookup<RouteLane>(true),
                m_RoutePosition = system.GetComponentLookup<Game.Routes.Position>(true),
                m_Waypoint = system.GetComponentLookup<Waypoint>(true),
                m_Owner = system.GetComponentLookup<Owner>(true),
                m_CurrentRoute = system.GetComponentLookup<CurrentRoute>(true),
                m_Transform = system.GetComponentLookup<Transform>(true),
                m_Moving = system.GetComponentLookup<Moving>(true),
                m_CarCurrentLane = system.GetComponentLookup<CarCurrentLane>(true),
                m_NetCarLane = system.GetComponentLookup<NetCarLane>(true),
                m_Curve = system.GetComponentLookup<Curve>(true),
                m_SlaveLane = system.GetComponentLookup<NetSlaveLane>(true),
                m_SecondaryLane = system.GetComponentLookup<NetSecondaryLane>(true),
                m_ZoneOverride = system.GetComponentLookup<BoardingZoneOverride>(true),
                m_TripSource = system.GetComponentLookup<TripSource>(true),
                m_OutOfControl = system.GetComponentLookup<OutOfControl>(true),
                m_CurrentVehicle = system.GetComponentLookup<CurrentVehicle>(true),
                m_CarNavigationLanes = system.GetBufferLookup<CarNavigationLane>(true),
                m_ConnectedRoutes = system.GetBufferLookup<ConnectedRoute>(true),
                m_RouteSegments = system.GetBufferLookup<RouteSegment>(true),
                m_RouteWaypoints = system.GetBufferLookup<RouteWaypoint>(true),
                m_PathElements = system.GetBufferLookup<PathElement>(true),
                m_Passengers = system.GetBufferLookup<Passenger>(true),
                m_LayoutElements = system.GetBufferLookup<LayoutElement>(true),
                m_PublicTransport = system.GetComponentLookup<VehiclePublicTransport>(false),
                m_Target = system.GetComponentLookup<Target>(false),
                m_BoardingVehicle = system.GetComponentLookup<BoardingVehicle>(false),
                m_VehicleTiming = system.GetComponentLookup<VehicleTiming>(false)
            };
        }

        internal void Update(SystemBase system)
        {
            m_Entities.Update(system);
            m_Deleted.Update(system);
            m_Temp.Update(system);
            m_PrefabRef.Update(system);
            m_PrefabData.Update(system);
            m_TransportStopData.Update(system);
            m_PublicTransportVehicleData.Update(system);
            m_ObjectGeometryData.Update(system);
            m_NetLaneData.Update(system);
            m_Connected.Update(system);
            m_RouteLane.Update(system);
            m_RoutePosition.Update(system);
            m_Waypoint.Update(system);
            m_Owner.Update(system);
            m_CurrentRoute.Update(system);
            m_Transform.Update(system);
            m_Moving.Update(system);
            m_CarCurrentLane.Update(system);
            m_NetCarLane.Update(system);
            m_Curve.Update(system);
            m_SlaveLane.Update(system);
            m_SecondaryLane.Update(system);
            m_ZoneOverride.Update(system);
            m_TripSource.Update(system);
            m_OutOfControl.Update(system);
            m_CurrentVehicle.Update(system);
            m_CarNavigationLanes.Update(system);
            m_ConnectedRoutes.Update(system);
            m_RouteSegments.Update(system);
            m_RouteWaypoints.Update(system);
            m_PathElements.Update(system);
            m_Passengers.Update(system);
            m_LayoutElements.Update(system);
            m_PublicTransport.Update(system);
            m_Target.Update(system);
            m_BoardingVehicle.Update(system);
            m_VehicleTiming.Update(system);
        }

        public bool Exists(Entity entity) => m_Entities.Exists(entity);
        public bool IsDeleted(Entity entity) => m_Deleted.HasComponent(entity);
        public bool IsTemp(Entity entity) => m_Temp.HasComponent(entity);
        public bool HasBoardingVehicle(Entity entity) => m_BoardingVehicle.HasComponent(entity);
        public bool HasSecondaryLane(Entity entity) => m_SecondaryLane.HasComponent(entity);
        public bool TryGetPrefabRef(Entity entity, out PrefabRef value) => m_PrefabRef.TryGetComponent(entity, out value);
        public bool TryGetTransportStopData(Entity entity, out TransportStopData value) =>
            m_TransportStopData.TryGetComponent(entity, out value);
        public bool TryGetPublicTransportVehicleData(Entity entity, out PublicTransportVehicleData value) =>
            m_PublicTransportVehicleData.TryGetComponent(entity, out value);
        public bool TryGetNetLaneData(Entity entity, out NetLaneData value) => m_NetLaneData.TryGetComponent(entity, out value);
        public bool TryGetTarget(Entity entity, out Target value) => m_Target.TryGetComponent(entity, out value);
        public bool TryGetConnected(Entity entity, out Connected value) => m_Connected.TryGetComponent(entity, out value);
        public bool TryGetRouteLane(Entity entity, out RouteLane value) => m_RouteLane.TryGetComponent(entity, out value);
        public bool TryGetRoutePosition(Entity entity, out Game.Routes.Position value) =>
            m_RoutePosition.TryGetComponent(entity, out value);
        public bool TryGetWaypoint(Entity entity, out Waypoint value) => m_Waypoint.TryGetComponent(entity, out value);
        public bool TryGetOwner(Entity entity, out Owner value) => m_Owner.TryGetComponent(entity, out value);
        public bool TryGetTransform(Entity entity, out Transform value) => m_Transform.TryGetComponent(entity, out value);
        public bool TryGetCarCurrentLane(Entity entity, out CarCurrentLane value) =>
            m_CarCurrentLane.TryGetComponent(entity, out value);
        public bool TryGetNetCarLane(Entity entity, out NetCarLane value) => m_NetCarLane.TryGetComponent(entity, out value);
        public bool TryGetCurve(Entity entity, out Curve value) => m_Curve.TryGetComponent(entity, out value);
        public bool TryGetSlaveLane(Entity entity, out NetSlaveLane value) => m_SlaveLane.TryGetComponent(entity, out value);
        public bool TryGetZoneOverride(Entity entity, out BoardingZoneOverride value) =>
            m_ZoneOverride.TryGetComponent(entity, out value);
        public bool TryGetCarNavigationLanes(Entity entity, out DynamicBuffer<CarNavigationLane> value) =>
            m_CarNavigationLanes.TryGetBuffer(entity, out value);
        public bool TryGetConnectedRoutes(Entity entity, out DynamicBuffer<ConnectedRoute> value) =>
            m_ConnectedRoutes.TryGetBuffer(entity, out value);
        public bool TryGetRouteSegments(Entity entity, out DynamicBuffer<RouteSegment> value) =>
            m_RouteSegments.TryGetBuffer(entity, out value);
        public bool TryGetPathElements(Entity entity, out DynamicBuffer<PathElement> value) =>
            m_PathElements.TryGetBuffer(entity, out value);
    }

    /// <summary>
    /// Buses grouped by stop, keeping first-seen order for the stops and for the buses at each stop.
    /// That order is behaviour, not presentation: admission is first come first served and the slot
    /// rotation indexes into the admitted list. It reproduces what the managed
    /// <c>Dictionary&lt;Entity, List&lt;Entity&gt;&gt;</c> it replaces enumerated after a Clear().
    /// </summary>
    internal struct StopGroups : System.IDisposable
    {
        private NativeHashMap<Entity, int> m_StopIndex;
        private NativeList<Entity> m_Stops;
        private NativeList<int> m_First;
        private NativeList<int> m_Last;
        private NativeList<int> m_Next;
        private NativeList<Entity> m_Buses;

        internal StopGroups(Allocator allocator)
        {
            m_StopIndex = new NativeHashMap<Entity, int>(16, allocator);
            m_Stops = new NativeList<Entity>(16, allocator);
            m_First = new NativeList<int>(16, allocator);
            m_Last = new NativeList<int>(16, allocator);
            m_Next = new NativeList<int>(16, allocator);
            m_Buses = new NativeList<Entity>(16, allocator);
        }

        internal int Count => m_Stops.Length;

        internal Entity GetStop(int index) => m_Stops[index];

        internal void Add(Entity stop, Entity bus)
        {
            int entry = m_Buses.Length;
            m_Buses.Add(bus);
            m_Next.Add(-1);
            if (m_StopIndex.TryGetValue(stop, out int index))
            {
                m_Next[m_Last[index]] = entry;
                m_Last[index] = entry;
                return;
            }
            m_StopIndex.Add(stop, m_Stops.Length);
            m_Stops.Add(stop);
            m_First.Add(entry);
            m_Last.Add(entry);
        }

        internal void GetBuses(int index, NativeList<Entity> result)
        {
            result.Clear();
            for (int entry = m_First[index]; entry >= 0; entry = m_Next[entry])
                result.Add(m_Buses[entry]);
        }

        public void Dispose()
        {
            m_StopIndex.Dispose();
            m_Stops.Dispose();
            m_First.Dispose();
            m_Last.Dispose();
            m_Next.Dispose();
            m_Buses.Dispose();
        }
    }
}
