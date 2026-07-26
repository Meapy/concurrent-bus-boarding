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
        private readonly HashSet<Entity> m_BusPathfindPrefabs = new();
        private EntityQuery m_LinePrefabs;
        private EntityQuery m_RouteElements;
        private int m_AppliedAttractiveness;
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
            if (m_Initialized && attractiveness == m_AppliedAttractiveness)
                return;

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

            if (!m_Initialized && attractiveness == 100 && m_BusPathfindPrefabs.Count == 0)
            {
                m_Initialized = true;
                m_AppliedAttractiveness = attractiveness;
                Mod.Log.Info($"Public transport attractiveness ready for {m_OriginalCosts.Count} passenger pathfind prefabs at 100%.");
                return;
            }

            Apply(attractiveness);
        }

        protected override void OnDestroy()
        {
            if (m_Initialized &&
                (m_AppliedAttractiveness != 100 || m_BusPathfindPrefabs.Count != 0))
            {
                foreach (KeyValuePair<Entity, PathfindTransportData> entry in m_OriginalCosts)
                {
                    if (EntityManager.Exists(entry.Key) &&
                        EntityManager.HasComponent<PathfindTransportData>(entry.Key))
                        EntityManager.SetComponentData(entry.Key, entry.Value);
                }
                RefreshRoutes();
            }
            m_OriginalCosts.Clear();
            m_BusPathfindPrefabs.Clear();
            base.OnDestroy();
        }

        private void CaptureOriginalCosts()
        {
            using NativeArray<Entity> linePrefabs = m_LinePrefabs.ToEntityArray(Allocator.Temp);
            foreach (Entity linePrefab in linePrefabs)
            {
                TransportLineData line = EntityManager.GetComponentData<TransportLineData>(linePrefab);
                Entity pathfindPrefab = line.m_PathfindPrefab;
                if (!line.m_PassengerTransport || pathfindPrefab == Entity.Null ||
                    !EntityManager.Exists(pathfindPrefab) ||
                    !EntityManager.HasComponent<PathfindTransportData>(pathfindPrefab))
                    continue;

                if (line.m_TransportType == TransportType.Bus)
                    m_BusPathfindPrefabs.Add(pathfindPrefab);
                if (m_OriginalCosts.ContainsKey(pathfindPrefab))
                    continue;

                m_OriginalCosts.Add(pathfindPrefab,
                    EntityManager.GetComponentData<PathfindTransportData>(pathfindPrefab));
            }
        }

        private void Apply(int attractiveness)
        {
            float multiplier = BoardingPolicy.TransitCostMultiplier(attractiveness);
            foreach (KeyValuePair<Entity, PathfindTransportData> entry in m_OriginalCosts)
            {
                if (!EntityManager.Exists(entry.Key) ||
                    !EntityManager.HasComponent<PathfindTransportData>(entry.Key))
                    continue;

                PathfindTransportData adjusted = entry.Value;
                float busMultiplier = m_BusPathfindPrefabs.Contains(entry.Key)
                    ? BoardingPolicy.BusTransitCostMultiplier()
                    : 1f;
                adjusted.m_StartingCost.m_Value = entry.Value.m_StartingCost.m_Value * multiplier * busMultiplier;
                EntityManager.SetComponentData(entry.Key, adjusted);
            }

            RefreshRoutes();
            m_Initialized = true;
            m_AppliedAttractiveness = attractiveness;
            Mod.Log.Info($"Public transport attractiveness set to {attractiveness}% for {m_OriginalCosts.Count} passenger pathfind prefabs, including {m_BusPathfindPrefabs.Count} bus profile(s) at {BoardingPolicy.BusAttractiveness:0.##}x.");
        }

        private void RefreshRoutes()
        {
            // ponytail: refresh every active route edge only when the slider changes; filter by owner if this ever profiles poorly.
            EntityManager.AddComponent<PathfindUpdated>(m_RouteElements);
        }
    }
}
