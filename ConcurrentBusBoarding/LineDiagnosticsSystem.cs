using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;
using PublicTransportFlags = Game.Vehicles.PublicTransportFlags;

namespace ConcurrentBusBoarding
{
    /// <summary>
    /// On-demand per-line report answering why a bus line carries no passengers.
    ///
    /// For each bus route it reports the things that can independently kill ridership: no vehicles
    /// running, stops whose boarding slot is held by a bus that will never release it, cims waiting
    /// with no successful boardings, and the native average waiting time. Nothing here changes
    /// simulation state.
    /// </summary>
    public partial class LineDiagnosticsSystem : GameSystemBase
    {
        private static bool s_ReportRequested;

        private EntityQuery m_Routes;

        internal static void RequestReport()
        {
            s_ReportRequested = true;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Routes = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<RouteWaypoint>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!s_ReportRequested)
                return;
            s_ReportRequested = false;

            using NativeArray<Entity> routes = m_Routes.ToEntityArray(Allocator.Temp);
            Mod.Log.Info($"Line report: {routes.Length} transport lines.");

            foreach (Entity route in routes)
            {
                if (!IsBusLine(route))
                    continue;
                ReportRoute(route);
            }
            Mod.Log.Info("Line report complete.");
        }

        private bool IsBusLine(Entity route)
        {
            if (!EntityManager.HasComponent<PrefabRef>(route))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(route).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<TransportLineData>(prefab))
                return false;
            return EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType ==
                TransportType.Bus;
        }

        private void ReportRoute(Entity route)
        {
            int vehicles = 0;
            int aboard = 0;
            int capacity = 0;
            int full = 0;
            if (EntityManager.HasBuffer<RouteVehicle>(route))
            {
                DynamicBuffer<RouteVehicle> routeVehicles =
                    EntityManager.GetBuffer<RouteVehicle>(route, true);
                vehicles = routeVehicles.Length;
                foreach (RouteVehicle routeVehicle in routeVehicles)
                {
                    Entity vehicle = routeVehicle.m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                        continue;
                    if (EntityManager.HasBuffer<Game.Vehicles.Passenger>(vehicle))
                        aboard += EntityManager.GetBuffer<Game.Vehicles.Passenger>(vehicle, true).Length;
                    if (EntityManager.HasComponent<VehiclePublicTransport>(vehicle) &&
                        (EntityManager.GetComponentData<VehiclePublicTransport>(vehicle).m_State &
                            PublicTransportFlags.Full) != 0)
                        full++;
                    if (!EntityManager.HasComponent<PrefabRef>(vehicle))
                        continue;
                    Entity vehiclePrefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
                    if (vehiclePrefab != Entity.Null && EntityManager.Exists(vehiclePrefab) &&
                        EntityManager.HasComponent<PublicTransportVehicleData>(vehiclePrefab))
                    {
                        capacity += EntityManager.GetComponentData<PublicTransportVehicleData>(
                            vehiclePrefab).m_PassengerCapacity;
                    }
                }
            }

            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            int stops = 0;
            int occupiedSlots = 0;
            int blockedSlots = 0;
            int waiting = 0;
            int concluded = 0;
            int success = 0;
            int waitingTimeSamples = 0;
            int waitingTimeTotal = 0;

            foreach (RouteWaypoint entry in waypoints)
            {
                Entity waypoint = entry.m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                    continue;

                if (EntityManager.HasComponent<WaitingPassengers>(waypoint))
                {
                    WaitingPassengers passengers =
                        EntityManager.GetComponentData<WaitingPassengers>(waypoint);
                    waiting += passengers.m_Count;
                    concluded += passengers.m_ConcludedAccumulation;
                    success += passengers.m_SuccessAccumulation;
                    if (passengers.m_AverageWaitingTime > 0)
                    {
                        waitingTimeTotal += passengers.m_AverageWaitingTime;
                        waitingTimeSamples++;
                    }
                }

                if (!EntityManager.HasComponent<Connected>(waypoint))
                    continue;
                Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
                if (stop == Entity.Null || !EntityManager.Exists(stop) ||
                    !EntityManager.HasComponent<BoardingVehicle>(stop))
                    continue;

                stops++;
                Entity holder = EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle;
                if (holder == Entity.Null)
                    continue;
                occupiedSlots++;

                // A holder that is no longer targeting this stop is a stale pointer, and it blocks the
                // stop permanently: installed IL shows BeginBoarding aborts when the slot is held by
                // another vehicle in the Boarding state, which a departed bus will be at its next
                // stop. A holder still targeting this stop is simply boarding, which is normal.
                bool holderPresent =
                    BoardingHelpers.TryGetStop(EntityManager, holder, out Entity holderStop) &&
                    holderStop == stop;
                if (!holderPresent &&
                    EntityManager.Exists(holder) &&
                    EntityManager.HasComponent<VehiclePublicTransport>(holder) &&
                    (EntityManager.GetComponentData<VehiclePublicTransport>(holder).m_State &
                        PublicTransportFlags.Boarding) != 0)
                    blockedSlots++;
            }

            // m_ConcludedAccumulation accumulates waiting time (Int32, written both when a cim
            // boards and when it gives up), while m_SuccessAccumulation is a small UInt16 count.
            // They are not two counts, so their ratio means nothing - report them raw and rely on
            // the game's own m_AverageWaitingTime for comparison between lines.
            int averageWait = waitingTimeSamples > 0 ? waitingTimeTotal / waitingTimeSamples : -1;

            Mod.Log.Info(
                $"  line {route.Index}: vehicles={vehicles} full={full} aboard={aboard}/{capacity} " +
                $"stops={stops} waiting={waiting} avgWait={averageWait} " +
                $"boardedRaw={success} waitTimeRaw={concluded} " +
                $"slotsOccupied={occupiedSlots} slotsStaleBlocked={blockedSlots}");
        }
    }
}
