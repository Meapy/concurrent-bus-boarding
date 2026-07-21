using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.UI.InGame;
using Game.Vehicles;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using UnityColor = UnityEngine.Color;
using VehiclePublicTransport = Game.Vehicles.PublicTransport;

namespace ConcurrentBusBoarding
{
    public partial class BoardingZoneRenderSystem : GameSystemBase
    {
        private static readonly UnityColor PullInColor = new UnityColor(0.05f, 0.55f, 1f, 0.42f);
        private static readonly UnityColor OrdinaryColor = new UnityColor(0.15f, 0.55f, 0.95f, 0.28f);
        private static readonly UnityColor HandleColor = new UnityColor(0.1f, 0.85f, 1f, 0.95f);
        private static readonly UnityColor MoveHandleColor = new UnityColor(1f, 1f, 1f, 0.95f);

        private EntityQuery m_Buses;
        private OverlayRenderSystem m_Overlay;
        private BoardingZoneToolSystem m_ZoneTool;
        private SelectedInfoUISystem m_SelectedInfo;
        private Dictionary<Entity, BoardingZone> m_Zones = new Dictionary<Entity, BoardingZone>();
        private int m_RefreshIn;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_Buses = GetEntityQuery(
                ComponentType.ReadOnly<VehiclePublicTransport>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.ReadOnly<Transform>());
            m_Overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_ZoneTool = World.GetOrCreateSystemManaged<BoardingZoneToolSystem>();
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            RequireForUpdate(m_Buses);
        }

        [Preserve]
        protected override void OnUpdate()
        {
            Dependency.Complete();
            OverlayRenderSystem.Buffer buffer = m_Overlay.GetBuffer(out var bufferDependencies);
            bufferDependencies.Complete();

            // ponytail: refresh periodically instead of tracking every network edit; the overlay may lag by at most one second.
            if (m_RefreshIn-- <= 0)
            {
                Dictionary<Entity, BoardingZone> observed = BoardingHelpers.FindObservedZones(EntityManager, m_Buses);
                foreach (KeyValuePair<Entity, BoardingZone> entry in observed)
                {
                    if (!m_Zones.TryGetValue(entry.Key, out BoardingZone existing) ||
                        !EntityManager.Exists(existing.Lane) ||
                        BoardingPolicy.PreferZoneCandidate(existing.StopDistance, existing.IsPullIn, existing.IsPhysical,
                            entry.Value.StopDistance, entry.Value.IsPullIn, entry.Value.IsPhysical))
                        m_Zones[entry.Key] = entry.Value;
                }
                m_RefreshIn = 60;
            }

            Entity selectedStop = GetSelectedStop();
            if (selectedStop != Entity.Null)
                TryGetObservedZone(selectedStop, out _);
            bool selectedOnly = Mod.Settings != null && Mod.Settings.OnlyShowSelectedStop;
            foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
            {
                BoardingZone zone = entry.Value;
                if (!BoardingPolicy.ShouldDrawZone(selectedOnly, entry.Key == selectedStop,
                    entry.Key == m_ZoneTool.EditingStop))
                    continue;
                if (!EntityManager.Exists(entry.Key) || !EntityManager.HasComponent<Game.Routes.BoardingVehicle>(entry.Key) ||
                    !EntityManager.Exists(zone.Lane))
                    continue;
                BoardingHelpers.ApplyOverride(EntityManager, entry.Key, ref zone);

                float2 bounds = BoardingHelpers.GetZoneBounds(zone);
                Bezier4x3 visibleZone = MathUtils.Cut(zone.Curve.m_Bezier, bounds);
                buffer.DrawCurve(zone.IsPullIn && !zone.IsCustom ? PullInColor : OrdinaryColor, visibleZone, zone.Width);
                if (m_ZoneTool.EditingStop == entry.Key)
                    DrawHandles(buffer, zone, bounds);
            }
        }

        private static void DrawHandles(OverlayRenderSystem.Buffer buffer, BoardingZone zone, float2 bounds)
        {
            DrawEndHandles(buffer, zone, bounds.x);
            DrawEndHandles(buffer, zone, bounds.y);
            buffer.DrawCircle(MoveHandleColor, MathUtils.Position(zone.Curve.m_Bezier, math.csum(bounds) * 0.5f), 2.2f);
        }

        private static void DrawEndHandles(OverlayRenderSystem.Buffer buffer, BoardingZone zone, float curvePosition)
        {
            float3 position = MathUtils.Position(zone.Curve.m_Bezier, curvePosition);
            float nearby = curvePosition < 0.99f ? curvePosition + 0.01f : curvePosition - 0.01f;
            float3 tangent = math.normalizesafe(MathUtils.Position(zone.Curve.m_Bezier, nearby) - position, new float3(0f, 0f, 1f));
            float3 side = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tangent), new float3(1f, 0f, 0f));
            float halfWidth = math.max(1.5f, zone.Width * 0.5f);
            buffer.DrawCircle(HandleColor, position + side * halfWidth, 1.8f);
            buffer.DrawCircle(HandleColor, position - side * halfWidth, 1.8f);
        }

        internal bool TryGetObservedZone(Entity stop, out BoardingZone zone)
        {
            if (!m_Zones.TryGetValue(stop, out zone))
            {
                if (!BoardingHelpers.TryGetStopZone(EntityManager, stop, out zone))
                    return false;
                m_Zones[stop] = zone;
            }
            BoardingHelpers.ApplyOverride(EntityManager, stop, ref zone);
            return true;
        }

        internal void Invalidate() => m_RefreshIn = 0;

        private Entity GetSelectedStop()
        {
            Entity selected = m_SelectedInfo.selectedEntity;
            if (BoardingHelpers.IsPassengerBusStop(EntityManager, selected))
                return selected;
            if (selected != Entity.Null && EntityManager.HasComponent<Connected>(selected))
            {
                Entity stop = EntityManager.GetComponentData<Connected>(selected).m_Connected;
                if (BoardingHelpers.IsPassengerBusStop(EntityManager, stop))
                    return stop;
            }
            return Entity.Null;
        }
    }
}
