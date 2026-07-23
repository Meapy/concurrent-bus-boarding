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

        private EntityQuery m_Buses;
        private OverlayRenderSystem m_Overlay;
        private BoardingZoneToolSystem m_ZoneTool;
        private SelectedInfoUISystem m_SelectedInfo;
        private Dictionary<Entity, BoardingZone> m_Zones = new Dictionary<Entity, BoardingZone>();
        private readonly List<Entity> m_StaleZones = new List<Entity>();
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
                m_StaleZones.Clear();
                foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
                {
                    if (!BoardingHelpers.IsRenderableZone(EntityManager, entry.Key, entry.Value))
                        m_StaleZones.Add(entry.Key);
                }
                foreach (Entity stop in m_StaleZones)
                    m_Zones.Remove(stop);

                Dictionary<Entity, BoardingZone> observed = BoardingHelpers.FindObservedZones(EntityManager, m_Buses);
                foreach (KeyValuePair<Entity, BoardingZone> entry in observed)
                {
                    if (!m_Zones.TryGetValue(entry.Key, out BoardingZone existing) ||
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
            if (selectedOnly)
            {
                DrawZone(buffer, selectedStop);
                if (m_ZoneTool.EditingStop != selectedStop)
                    DrawZone(buffer, m_ZoneTool.EditingStop);
                return;
            }
            foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
                DrawZone(buffer, entry.Key);
        }

        private void DrawZone(OverlayRenderSystem.Buffer buffer, Entity stop)
        {
            if (stop == Entity.Null || !m_Zones.TryGetValue(stop, out BoardingZone zone) ||
                !BoardingHelpers.IsRenderableZone(EntityManager, stop, zone))
                return;
            BoardingHelpers.ApplyOverride(EntityManager, stop, ref zone);

            UnityColor color = zone.IsPullIn && !zone.IsCustom ? PullInColor : OrdinaryColor;
            float remaining = BoardingHelpers.GetRequestedZoneLength(zone);
            if (zone.Pieces == null)
                return;
            foreach (BoardingZonePiece piece in zone.Pieces)
            {
                float2 bounds = BoardingHelpers.TrimFromFront(piece, remaining);
                float pieceLength = BoardingHelpers.PieceLength(piece);
                if (pieceLength > 0.01f && bounds.y - bounds.x > 0.0001f)
                    buffer.DrawCurve(color, MathUtils.Cut(piece.Curve.m_Bezier, bounds), piece.Width);
                remaining -= pieceLength;
                if (remaining <= 0f)
                    break;
            }
            if (m_ZoneTool.EditingStop == stop &&
                BoardingHelpers.TryGetRearEdge(zone, out BoardingZonePiece rearPiece, out float2 rearBounds))
                DrawEndHandles(buffer, rearPiece, rearPiece.Direction >= 0 ? rearBounds.x : rearBounds.y);
        }

        private static void DrawEndHandles(OverlayRenderSystem.Buffer buffer, BoardingZonePiece piece, float curvePosition)
        {
            float3 position = MathUtils.Position(piece.Curve.m_Bezier, curvePosition);
            float nearby = curvePosition < 0.99f ? curvePosition + 0.01f : curvePosition - 0.01f;
            float3 tangent = math.normalizesafe(MathUtils.Position(piece.Curve.m_Bezier, nearby) - position, new float3(0f, 0f, 1f));
            float3 side = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tangent), new float3(1f, 0f, 0f));
            float halfWidth = math.max(1.5f, piece.Width * 0.5f);
            buffer.DrawCircle(HandleColor, position + side * halfWidth, 1.8f);
            buffer.DrawCircle(HandleColor, position - side * halfWidth, 1.8f);
        }

        internal bool TryGetObservedZone(Entity stop, out BoardingZone zone)
        {
            if (m_Zones.TryGetValue(stop, out zone) &&
                !BoardingHelpers.IsRenderableZone(EntityManager, stop, zone))
            {
                m_Zones.Remove(stop);
                zone = default;
            }
            if (!m_Zones.ContainsKey(stop))
            {
                if (!BoardingHelpers.TryGetStopZone(EntityManager, stop, out zone) ||
                    !BoardingHelpers.IsRenderableZone(EntityManager, stop, zone))
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
