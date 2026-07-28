using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;
using PublicTransportFlags = Game.Vehicles.PublicTransportFlags;

namespace ConcurrentBusBoarding
{
    /// <summary>
    /// Per-stop diagnosis of why a bus line stops carrying passengers.
    ///
    /// The mod's other counters only describe its own boarding sessions, which cannot explain a line
    /// losing riders. This tracks every bus stop across reports and identifies stalled stops: ones
    /// with cims waiting where nobody has boarded since the previous report. For each it dumps the
    /// exact state needed to tell the possible causes apart - no bus ever arriving, a bus arriving
    /// but not boarding, the stop slot held by something, or the mod holding a session there.
    ///
    /// Reports automatically so a time series exists, and on demand from the settings page.
    /// </summary>
    public partial class LineDiagnosticsSystem : GameSystemBase
    {
        private const uint ReportIntervalFrames = 4096u;
        private const int MaxStalledStopsLogged = 8;

        private struct StopSample
        {
            internal int Boarded;
            internal uint LastBoardedFrame;
            internal uint FirstSeenFrame;
        }

        private static bool s_ReportRequested;

        private struct TrackedCim
        {
            internal Entity Stop;
            internal uint FirstSeenFrame;
        }

        private EntityQuery m_Routes;
        private EntityQuery m_Residents;
        private SimulationSystem m_SimulationSystem;
        private readonly Dictionary<Entity, TrackedCim> m_WaitingCims = new();
        private readonly List<Entity> m_Resolved = new();
        private readonly Dictionary<Entity, StopSample> m_Samples = new();
        private uint m_LastReportFrame;
        private int m_LastRiders = -1;
        private int m_LastWaiting = -1;

        internal static void RequestReport()
        {
            s_ReportRequested = true;
        }

        // Nothing here needs frame accuracy, and OnUpdate otherwise runs every frame only to compare
        // two integers.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Routes = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<RouteWaypoint>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_Residents = GetEntityQuery(
                ComponentType.ReadOnly<Game.Creatures.Resident>(),
                ComponentType.ReadOnly<Game.Creatures.HumanCurrentLane>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        [Preserve]
        protected override void OnUpdate()
        {
            uint frame = m_SimulationSystem.frameIndex;
            bool manual = s_ReportRequested;
            if (!manual && frame - m_LastReportFrame < ReportIntervalFrames)
                return;
            s_ReportRequested = false;
            m_LastReportFrame = frame;

            using NativeArray<Entity> routes = m_Routes.ToEntityArray(Allocator.Temp);
            var stalled = new List<string>();
            int busLines = 0;
            int totalStops = 0;
            int stalledStops = 0;
            int stopsWithDemand = 0;
            int totalWaiting = 0;
            int totalAboard = 0;
            int totalCapacity = 0;

            foreach (Entity route in routes)
            {
                if (!IsBusLine(route))
                    continue;
                busLines++;
                ReportRoute(route, frame, stalled, ref totalStops, ref stalledStops,
                    ref stopsWithDemand, ref totalWaiting);
                CountRiders(route, ref totalAboard, ref totalCapacity);
            }

            // Riders is the measure that matters. A falling queue means opposite things depending on
            // it: if riders hold while waiting falls, buses are clearing the queues; if both fall,
            // cims are giving up on buses. Queue length alone cannot tell those apart.
            int ridersDelta = m_LastRiders < 0 ? 0 : totalAboard - m_LastRiders;
            int waitingDelta = m_LastWaiting < 0 ? 0 : totalWaiting - m_LastWaiting;
            m_LastRiders = totalAboard;
            m_LastWaiting = totalWaiting;

            Mod.Log.Info(
                $"Bus ridership: riders={totalAboard}/{totalCapacity} ({ridersDelta:+#;-#;0}) " +
                $"waiting={totalWaiting} ({waitingDelta:+#;-#;0}) across {busLines} lines.");
            Mod.Log.Info(
                $"Stop diagnosis: {totalStops} stops, {stopsWithDemand} with cims waiting, " +
                $"{stalledStops} stalled (waiting cims but nobody boarded since the last report).");

            // Only when the player asks for a report. This walks every resident in the city, which
            // is far too expensive to repeat on a timer.
            if (manual)
                ReportCims(frame);

            int logged = 0;
            foreach (string line in stalled)
            {
                if (logged++ >= MaxStalledStopsLogged)
                {
                    Mod.Log.Info($"  ... and {stalled.Count - MaxStalledStopsLogged} more stalled stops.");
                    break;
                }
                Mod.Log.Info(line);
            }
        }

        private bool IsBusLine(Entity route)
        {
            if (!EntityManager.HasComponent<PrefabRef>(route))
                return false;
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(route).m_Prefab;
            return prefab != Entity.Null && EntityManager.Exists(prefab) &&
                EntityManager.HasComponent<TransportLineData>(prefab) &&
                EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType ==
                    TransportType.Bus;
        }

        /// <summary>
        /// Follows individual cims waiting at bus stops between reports and records how each one
        /// stopped waiting. This separates the two explanations for emptier stops that every other
        /// metric conflates: fewer cims arriving means trip planning is routing them away from
        /// buses, whereas cims arriving and then giving up means the wait itself is driving them off.
        /// </summary>
        private void ReportCims(uint frame)
        {
            var waitingNow = new Dictionary<Entity, Entity>();
            using (NativeArray<Entity> residents = m_Residents.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity resident in residents)
                {
                    if ((EntityManager.GetComponentData<Game.Creatures.Resident>(resident).m_Flags &
                            Game.Creatures.ResidentFlags.WaitingTransport) == 0)
                        continue;
                    Entity stop = EntityManager.GetComponentData<Game.Creatures.HumanCurrentLane>(
                        resident).m_QueueEntity;
                    if (BoardingHelpers.IsPassengerBusStop(EntityManager, stop))
                        waitingNow[resident] = stop;
                }
            }

            int boarded = 0;
            int gaveUp = 0;
            int vanished = 0;
            long boardedWait = 0;
            long gaveUpWait = 0;

            m_Resolved.Clear();
            foreach (KeyValuePair<Entity, TrackedCim> entry in m_WaitingCims)
            {
                Entity cim = entry.Key;
                if (waitingNow.ContainsKey(cim))
                    continue;

                m_Resolved.Add(cim);
                uint waited = frame - entry.Value.FirstSeenFrame;
                if (!EntityManager.Exists(cim))
                {
                    vanished++;
                }
                else if (EntityManager.HasComponent<Game.Creatures.CurrentVehicle>(cim))
                {
                    boarded++;
                    boardedWait += waited;
                }
                else
                {
                    // Stopped waiting without ever getting into a vehicle.
                    gaveUp++;
                    gaveUpWait += waited;
                }
            }
            foreach (Entity cim in m_Resolved)
                m_WaitingCims.Remove(cim);

            int arrived = 0;
            foreach (KeyValuePair<Entity, Entity> entry in waitingNow)
            {
                if (m_WaitingCims.ContainsKey(entry.Key))
                    continue;
                arrived++;
                m_WaitingCims[entry.Key] = new TrackedCim
                {
                    Stop = entry.Value,
                    FirstSeenFrame = frame
                };
            }

            int resolved = boarded + gaveUp;
            int boardedShare = resolved > 0 ? boarded * 100 / resolved : -1;
            Mod.Log.Info(
                $"Cim outcomes: arrived={arrived} boarded={boarded} gaveUp={gaveUp} " +
                $"({boardedShare}% boarded) stillWaiting={m_WaitingCims.Count} vanished={vanished}; " +
                $"avg wait boarded={Average(boardedWait, boarded)}f gaveUp={Average(gaveUpWait, gaveUp)}f.");
        }

        private static string Average(long total, int count)
        {
            return count > 0 ? (total / count).ToString() : "-";
        }

        private void CountRiders(Entity route, ref int aboard, ref int capacity)
        {
            if (!EntityManager.HasBuffer<RouteVehicle>(route))
                return;
            foreach (RouteVehicle routeVehicle in EntityManager.GetBuffer<RouteVehicle>(route, true))
            {
                Entity vehicle = routeVehicle.m_Vehicle;
                if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
                    continue;
                if (EntityManager.HasBuffer<Game.Vehicles.Passenger>(vehicle))
                    aboard += EntityManager.GetBuffer<Game.Vehicles.Passenger>(vehicle, true).Length;
                if (!EntityManager.HasComponent<PrefabRef>(vehicle))
                    continue;
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;
                if (prefab != Entity.Null && EntityManager.Exists(prefab) &&
                    EntityManager.HasComponent<PublicTransportVehicleData>(prefab))
                {
                    capacity += EntityManager.GetComponentData<PublicTransportVehicleData>(prefab)
                        .m_PassengerCapacity;
                }
            }
        }

        private void ReportRoute(Entity route, uint frame, List<string> stalled,
            ref int totalStops, ref int stalledStops, ref int stopsWithDemand, ref int totalWaiting)
        {
            DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
            int vehicles = EntityManager.HasBuffer<RouteVehicle>(route)
                ? EntityManager.GetBuffer<RouteVehicle>(route, true).Length
                : 0;

            foreach (RouteWaypoint entry in waypoints)
            {
                Entity waypoint = entry.m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint) ||
                    !EntityManager.HasComponent<WaitingPassengers>(waypoint))
                    continue;

                WaitingPassengers passengers =
                    EntityManager.GetComponentData<WaitingPassengers>(waypoint);
                totalStops++;
                totalWaiting += passengers.m_Count;
                if (passengers.m_Count > 0)
                    stopsWithDemand++;

                // Track boarding progress per waypoint. m_SuccessAccumulation only rises when a cim
                // actually boards here, so a stop with waiting cims and no rise is not being served.
                if (!m_Samples.TryGetValue(waypoint, out StopSample sample))
                {
                    m_Samples[waypoint] = new StopSample
                    {
                        Boarded = passengers.m_SuccessAccumulation,
                        LastBoardedFrame = frame,
                        FirstSeenFrame = frame
                    };
                    continue;
                }

                bool boardedSince = passengers.m_SuccessAccumulation != sample.Boarded;
                if (boardedSince)
                    sample.LastBoardedFrame = frame;
                sample.Boarded = passengers.m_SuccessAccumulation;
                m_Samples[waypoint] = sample;

                if (boardedSince || passengers.m_Count == 0)
                    continue;

                stalledStops++;
                if (stalled.Count < MaxStalledStopsLogged * 2)
                    stalled.Add(DescribeStalledStop(route, waypoint, passengers, sample, frame, vehicles));
            }
        }

        private string DescribeStalledStop(Entity route, Entity waypoint, WaitingPassengers passengers,
            StopSample sample, uint frame, int vehicles)
        {
            Entity stop = EntityManager.HasComponent<Connected>(waypoint)
                ? EntityManager.GetComponentData<Connected>(waypoint).m_Connected
                : Entity.Null;

            string slotText = "stop=none";
            if (stop != Entity.Null && EntityManager.Exists(stop) &&
                EntityManager.HasComponent<BoardingVehicle>(stop))
            {
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
                slotText = $"stop={stop.Index} slot={DescribeVehicle(slot.m_Vehicle, stop)} " +
                    $"testing={DescribeVehicle(slot.m_Testing, stop)}";
            }

            // How many of this line's buses are anywhere near this stop, and what they are doing.
            int enRouteHere = 0;
            int boardingHere = 0;
            int managedHere = 0;
            if (EntityManager.HasBuffer<RouteVehicle>(route))
            {
                foreach (RouteVehicle routeVehicle in EntityManager.GetBuffer<RouteVehicle>(route, true))
                {
                    Entity vehicle = routeVehicle.m_Vehicle;
                    if (vehicle == Entity.Null || !EntityManager.Exists(vehicle) ||
                        !EntityManager.HasComponent<Game.Common.Target>(vehicle))
                        continue;
                    if (EntityManager.GetComponentData<Game.Common.Target>(vehicle).m_Target != waypoint)
                        continue;
                    enRouteHere++;
                    if (EntityManager.HasComponent<VehiclePublicTransport>(vehicle) &&
                        (EntityManager.GetComponentData<VehiclePublicTransport>(vehicle).m_State &
                            PublicTransportFlags.Boarding) != 0)
                        boardingHere++;
                    if (EntityManager.HasComponent<ConcurrentBoardingActive>(vehicle))
                        managedHere++;
                }
            }

            uint quietFor = frame - sample.LastBoardedFrame;
            return $"  stalled line={route.Index} waypoint={waypoint.Index} {slotText} " +
                $"waiting={passengers.m_Count} avgWait={passengers.m_AverageWaitingTime} " +
                $"quietFor={quietFor}f lineVehicles={vehicles} targetingHere={enRouteHere} " +
                $"boardingHere={boardingHere} managedHere={managedHere}";
        }

        private string DescribeVehicle(Entity vehicle, Entity stop)
        {
            if (vehicle == Entity.Null)
                return "none";
            if (!EntityManager.Exists(vehicle))
                return $"{vehicle.Index}:GONE";
            if (!EntityManager.HasComponent<VehiclePublicTransport>(vehicle))
                return $"{vehicle.Index}:notTransit";

            VehiclePublicTransport transport =
                EntityManager.GetComponentData<VehiclePublicTransport>(vehicle);
            bool here = BoardingHelpers.TryGetStop(EntityManager, vehicle, out Entity current) &&
                current == stop;
            bool managed = EntityManager.HasComponent<ConcurrentBoardingActive>(vehicle);
            return $"{vehicle.Index}:{(here ? "here" : "elsewhere")}" +
                $"{(managed ? ":managed" : string.Empty)}" +
                $":state={(uint)transport.m_State}" +
                $":maxBoard={FormatDistance(transport.m_MaxBoardingDistance)}" +
                $":depart={(int)(transport.m_DepartureFrame - (long)m_SimulationSystem.frameIndex)}f";
        }

        private static string FormatDistance(float value)
        {
            if (value == float.MaxValue)
                return "open";
            return math.isfinite(value) ? ((int)value).ToString() : "invalid";
        }
    }
}
