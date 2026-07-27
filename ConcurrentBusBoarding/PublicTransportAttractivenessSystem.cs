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

            if (!m_Initialized && attractiveness == 100)
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
            if (m_Initialized && m_AppliedAttractiveness != 100)
            {
                RestoreOriginalCosts();
                RefreshRoutes();
            }
            m_OriginalCosts.Clear();
            base.OnDestroy();
        }

        private void RestoreOriginalCosts()
        {
            if (m_AppliedAttractiveness == 100)
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
            using NativeArray<Entity> linePrefabs = m_LinePrefabs.ToEntityArray(Allocator.Temp);
            foreach (Entity linePrefab in linePrefabs)
            {
                TransportLineData line = EntityManager.GetComponentData<TransportLineData>(linePrefab);
                Entity pathfindPrefab = line.m_PathfindPrefab;
                if (!line.m_PassengerTransport || pathfindPrefab == Entity.Null ||
                    m_OriginalCosts.ContainsKey(pathfindPrefab) ||
                    !EntityManager.Exists(pathfindPrefab) ||
                    !EntityManager.HasComponent<PathfindTransportData>(pathfindPrefab))
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
                adjusted.m_StartingCost.m_Value = entry.Value.m_StartingCost.m_Value * multiplier;
                EntityManager.SetComponentData(entry.Key, adjusted);
            }

            RefreshRoutes();
            m_Initialized = true;
            m_AppliedAttractiveness = attractiveness;
            Mod.Log.Info($"Public transport attractiveness set to {attractiveness}% for {m_OriginalCosts.Count} passenger pathfind prefabs.");
        }

        private void RefreshRoutes()
        {
            // ponytail: refresh every active route edge only when the slider changes; filter by owner if this ever profiles poorly.
            EntityManager.AddComponent<PathfindUpdated>(m_RouteElements);
        }
    }
}
