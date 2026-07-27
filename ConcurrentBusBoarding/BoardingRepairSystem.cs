using Colossal.Serialization.Entities;
using Game;
using Game.Common;
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
    /// Repairs boarding state that earlier versions of this mod could leave behind in a saved city.
    ///
    /// Two kinds of residue matter, and both are permanent once saved:
    ///
    /// 1. A stop whose <c>BoardingVehicle.m_Vehicle</c> still points at a bus that has moved on or no
    ///    longer exists. Installed IL shows <c>TransportBoardingJob.BeginBoarding</c> aborts when the
    ///    slot is held by a different vehicle in the Boarding state, so a stale holder can stop that
    ///    stop from ever boarding again.
    /// 2. A bus left with <c>m_MaxBoardingDistance = 0</c> - the doors-closed value - or with a
    ///    departure frame far in the future. Such a bus admits no passengers and will not depart.
    ///
    /// Runs once automatically after a city loads, and can be re-run from the settings page.
    /// </summary>
    public partial class BoardingRepairSystem : GameSystemBase
    {
        private static bool s_RepairRequested;

        private EntityQuery m_Stops;
        private EntityQuery m_Vehicles;
        private SimulationSystem m_SimulationSystem;
        private bool m_PendingLoadRepair;

        internal static void RequestRepair()
        {
            s_RepairRequested = true;
        }

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Stops = GetEntityQuery(
                ComponentType.ReadWrite<BoardingVehicle>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_Vehicles = GetEntityQuery(
                ComponentType.ReadWrite<VehiclePublicTransport>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
        }

        protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            // Only a real city can carry the residue; the editor and menu have no live vehicles.
            if (mode == GameMode.Game)
                m_PendingLoadRepair = true;
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (!m_PendingLoadRepair && !s_RepairRequested)
                return;

            bool manual = s_RepairRequested;
            m_PendingLoadRepair = false;
            s_RepairRequested = false;

            int clearedSlots = ClearStaleStopSlots();
            int repairedVehicles = RepairVehicles();

            if (clearedSlots > 0 || repairedVehicles > 0 || manual)
            {
                Mod.Log.Info(
                    $"Boarding repair: cleared {clearedSlots} stale stop slots and reset " +
                    $"{repairedVehicles} buses.");
            }
        }

        private int ClearStaleStopSlots()
        {
            int cleared = 0;
            using NativeArray<Entity> stops = m_Stops.ToEntityArray(Allocator.Temp);
            foreach (Entity stop in stops)
            {
                BoardingVehicle slot = EntityManager.GetComponentData<BoardingVehicle>(stop);
                bool changed = false;

                if (slot.m_Vehicle != Entity.Null && IsStaleHolder(slot.m_Vehicle, stop))
                {
                    slot.m_Vehicle = Entity.Null;
                    changed = true;
                }
                if (slot.m_Testing != Entity.Null && IsStaleHolder(slot.m_Testing, stop))
                {
                    slot.m_Testing = Entity.Null;
                    changed = true;
                }

                if (changed)
                {
                    EntityManager.SetComponentData(stop, slot);
                    cleared++;
                }
            }
            return cleared;
        }

        /// <summary>
        /// A holder is stale unless it still exists, is still a transit vehicle, is still targeting
        /// this stop, and is still boarding. Anything else can never release the slot on its own.
        /// </summary>
        private bool IsStaleHolder(Entity vehicle, Entity stop)
        {
            if (!EntityManager.Exists(vehicle) ||
                EntityManager.HasComponent<Deleted>(vehicle) ||
                EntityManager.HasComponent<Game.Tools.Temp>(vehicle) ||
                !EntityManager.HasComponent<VehiclePublicTransport>(vehicle))
                return true;

            if (!BoardingHelpers.TryGetStop(EntityManager, vehicle, out Entity current) || current != stop)
                return true;

            VehiclePublicTransport transport =
                EntityManager.GetComponentData<VehiclePublicTransport>(vehicle);
            const PublicTransportFlags atStop = PublicTransportFlags.Boarding |
                PublicTransportFlags.Testing | PublicTransportFlags.Arriving |
                PublicTransportFlags.RequireStop;
            return (transport.m_State & atStop) == 0;
        }

        private int RepairVehicles()
        {
            uint frame = m_SimulationSystem.frameIndex;
            uint latestDeparture = frame + BoardingPolicy.ManagedBoardingTimeoutFrames;
            int repaired = 0;

            using NativeArray<Entity> vehicles = m_Vehicles.ToEntityArray(Allocator.Temp);
            foreach (Entity vehicle in vehicles)
            {
                VehiclePublicTransport transport =
                    EntityManager.GetComponentData<VehiclePublicTransport>(vehicle);
                bool changed = false;
                bool boarding = (transport.m_State & PublicTransportFlags.Boarding) != 0;

                // Only a boarding bus can be stuck. For every other bus a zero boarding distance and
                // a stale departure frame are the normal resting values, so touching them would
                // rewrite the whole city's healthy vehicles rather than repair anything.
                if (boarding)
                {
                    if (!BoardingHelpers.TryGetStop(EntityManager, vehicle, out _))
                    {
                        // Boarding with nowhere to board: hand it back to native AI.
                        transport.m_State &= ~PublicTransportFlags.Boarding;
                        transport.m_State |= PublicTransportFlags.EnRoute;
                        changed = true;
                    }
                    else
                    {
                        // Doors left closed by an interrupted managed session: it would admit nobody.
                        if (transport.m_MaxBoardingDistance <= 0f)
                        {
                            transport.m_MaxBoardingDistance = float.MaxValue;
                            changed = true;
                        }
                        // A departure frame beyond any legitimate dwell can never be reached.
                        if (transport.m_DepartureFrame > latestDeparture)
                        {
                            transport.m_DepartureFrame = frame;
                            changed = true;
                        }
                    }
                }

                // Non-finite values are never valid in any state.
                if (!math.isfinite(transport.m_MaxBoardingDistance))
                {
                    transport.m_MaxBoardingDistance = boarding ? float.MaxValue : 0f;
                    changed = true;
                }
                if (!math.isfinite(transport.m_MinWaitingDistance))
                {
                    transport.m_MinWaitingDistance = float.MaxValue;
                    changed = true;
                }

                if (changed)
                {
                    EntityManager.SetComponentData(vehicle, transport);
                    repaired++;
                }

                // Sessions and handoffs are not serialized, but a mid-session hot reload can leave
                // them behind. Removing them hands the vehicle straight back to native AI.
                if (EntityManager.HasComponent<ConcurrentBoardingActive>(vehicle))
                    EntityManager.RemoveComponent<ConcurrentBoardingActive>(vehicle);
                if (EntityManager.HasComponent<ConcurrentRouteHandoff>(vehicle))
                    EntityManager.RemoveComponent<ConcurrentRouteHandoff>(vehicle);
            }
            return repaired;
        }
    }
}
