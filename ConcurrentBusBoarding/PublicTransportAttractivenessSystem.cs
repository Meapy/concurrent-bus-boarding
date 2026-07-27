using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace ConcurrentBusBoarding
{
    internal sealed partial class PublicTransportAttractivenessSystem : GameSystemBase
    {
        private readonly Dictionary<Entity, PathfindTransportData> m_OriginalCosts = new();
        // Pathfind prefabs used only by bus lines. A prefab shared with any other passenger mode is
        // excluded, so the bus multiplier can never quietly discount trams, trains or ferries.
        private readonly HashSet<Entity> m_BusOnlyCosts = new();
        private EntityQuery m_LinePrefabs;
        private EntityQuery m_RouteElements;
        private int m_AppliedAttractiveness;
        private int m_AppliedBusAttractiveness;
        private bool m_Initialized;
        private bool m_LoggedWaitingForCosts;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_LinePrefabs = GetEntityQuery(ComponentType.ReadOnly<TransportLineData>());
            m_RouteElements = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<AccessLane>(),
                    ComponentType.ReadOnly<RouteLane>(),
                    ComponentType.ReadOnly<Segment>(),
                    ComponentType.ReadOnly<Game.Objects.SpawnLocation>()
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Created>(),
                    ComponentType.ReadOnly<Updated>(),
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<PathfindUpdated>(),
                    ComponentType.ReadOnly<Game.Routes.MailBox>(),
                    ComponentType.ReadOnly<LivePath>(),
                    ComponentType.ReadOnly<VerifiedPath>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>()
                }
            });
            RequireForUpdate(m_LinePrefabs);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            int attractiveness = Mod.Settings?.PublicTransportAttractiveness ?? 100;
            int busAttractiveness = Mod.Settings?.BusAttractiveness ?? 100;
            if (m_Initialized && attractiveness == m_AppliedAttractiveness &&
                busAttractiveness == m_AppliedBusAttractiveness)
                return;

            // Restore first, so a prefab that registered after an earlier scaling pass is never
            // captured at its already-scaled value and recorded as the baseline.
            RestoreOriginalCosts();
            CaptureOriginalCosts();
            if (m_OriginalCosts.Count == 0)
            {
                if (!m_LoggedWaitingForCosts)
                {
                    m_LoggedWaitingForCosts = true;
                    Mod.Log.Warn("Public transport attractiveness found no passenger pathfind costs; retrying.");
                }
                return;
            }

            if (!m_Initialized && attractiveness == 100 && busAttractiveness == 100)
            {
                m_Initialized = true;
                m_AppliedAttractiveness = attractiveness;
                m_AppliedBusAttractiveness = busAttractiveness;
                Mod.Log.Info($"Public transport attractiveness ready for {m_OriginalCosts.Count} passenger pathfind prefabs at 100%.");
                return;
            }

            Apply(attractiveness, busAttractiveness);
        }

        protected override void OnDestroy()
        {
            if (m_Initialized && (m_AppliedAttractiveness != 100 || m_AppliedBusAttractiveness != 100))
            {
                RestoreOriginalCosts();
                RefreshRoutes();
            }
            m_OriginalCosts.Clear();
            m_BusOnlyCosts.Clear();
            base.OnDestroy();
        }

        private void RestoreOriginalCosts()
        {
            if (m_AppliedAttractiveness == 100 && m_AppliedBusAttractiveness == 100)
                return;
            foreach (KeyValuePair<Entity, PathfindTransportData> entry in m_OriginalCosts)
            {
                if (EntityManager.Exists(entry.Key) &&
                    EntityManager.HasComponent<PathfindTransportData>(entry.Key))
                    EntityManager.SetComponentData(entry.Key, entry.Value);
            }
        }

        private void CaptureOriginalCosts()
        {
            var busOnly = new Dictionary<Entity, bool>();
            using NativeArray<Entity> linePrefabs = m_LinePrefabs.ToEntityArray(Allocator.Temp);
            foreach (Entity linePrefab in linePrefabs)
            {
                TransportLineData line = EntityManager.GetComponentData<TransportLineData>(linePrefab);
                Entity pathfindPrefab = line.m_PathfindPrefab;
                if (!line.m_PassengerTransport || pathfindPrefab == Entity.Null ||
                    !EntityManager.Exists(pathfindPrefab) ||
                    !EntityManager.HasComponent<PathfindTransportData>(pathfindPrefab))
                    continue;

                // One prefab can back several line types. It only counts as a bus cost if every
                // passenger line referencing it is a bus line.
                bool isBus = line.m_TransportType == TransportType.Bus;
                busOnly[pathfindPrefab] = busOnly.TryGetValue(pathfindPrefab, out bool existing)
                    ? existing && isBus
                    : isBus;

                if (!m_OriginalCosts.ContainsKey(pathfindPrefab))
                {
                    m_OriginalCosts.Add(pathfindPrefab,
                        EntityManager.GetComponentData<PathfindTransportData>(pathfindPrefab));
                }
            }

            m_BusOnlyCosts.Clear();
            foreach (KeyValuePair<Entity, bool> entry in busOnly)
            {
                if (entry.Value)
                    m_BusOnlyCosts.Add(entry.Key);
            }
        }

        private void Apply(int attractiveness, int busAttractiveness)
        {
            float multiplier = BoardingPolicy.TransitCostMultiplier(attractiveness);
            float busMultiplier = BoardingPolicy.TransitCostMultiplier(busAttractiveness);
            foreach (KeyValuePair<Entity, PathfindTransportData> entry in m_OriginalCosts)
            {
                if (!EntityManager.Exists(entry.Key) ||
                    !EntityManager.HasComponent<PathfindTransportData>(entry.Key))
                    continue;

                // Both multipliers recompute from the captured vanilla baseline, so they compose
                // without either drifting when the other changes.
                float combined = m_BusOnlyCosts.Contains(entry.Key)
                    ? multiplier * busMultiplier
                    : multiplier;
                PathfindTransportData adjusted = entry.Value;
                adjusted.m_StartingCost.m_Value = entry.Value.m_StartingCost.m_Value * combined;
                EntityManager.SetComponentData(entry.Key, adjusted);
            }

            RefreshRoutes();
            m_Initialized = true;
            m_AppliedAttractiveness = attractiveness;
            m_AppliedBusAttractiveness = busAttractiveness;
            Mod.Log.Info(
                $"Public transport attractiveness set to {attractiveness}% for {m_OriginalCosts.Count} " +
                $"passenger pathfind prefabs; bus attractiveness {busAttractiveness}% applies to " +
                $"{m_BusOnlyCosts.Count} of them.");
        }

        private void RefreshRoutes()
        {
            // ponytail: refresh every active route edge only when the slider changes; filter by owner if this ever profiles poorly.
            EntityManager.AddComponent<PathfindUpdated>(m_RouteElements);
        }
    }
}
