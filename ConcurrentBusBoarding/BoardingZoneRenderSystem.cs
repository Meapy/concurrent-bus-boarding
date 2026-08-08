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
        private static readonly UnityColor DefaultOverlayColor = new UnityColor(0.15f, 0.55f, 0.95f, 0.18f);
        private static readonly UnityColor HandleColor = new UnityColor(0.1f, 0.85f, 1f, 0.95f);

        private EntityQuery m_Buses;
        private OverlayRenderSystem m_Overlay;
        private BoardingZoneToolSystem m_ZoneTool;
        private SelectedInfoUISystem m_SelectedInfo;
        // A cached physical observation is deliberately kept after its stop is deselected, so reselecting
        // a stop with no bus present still shows the real driven lane rather than inferred geometry. That
        // makes the cache grow with every stop ever visited, so it is capped; an evicted stop is simply
        // re-learned the next time a bus is there.
        private const int MaxCachedZones = 512;

        private readonly Dictionary<Entity, BoardingZone> m_Zones = new Dictionary<Entity, BoardingZone>();
        private readonly Dictionary<Entity, BoardingZone> m_Observed = new Dictionary<Entity, BoardingZone>();
        private readonly Dictionary<Entity, UnityColor> m_OverlayColors = new Dictionary<Entity, UnityColor>();
        // Resolution failure is not cached by m_Zones, so without this a stop that cannot yet be
        // resolved - the "Waiting for a bus to approach" state - re-ran the full route walk on every
        // frame it stayed selected. Cleared by the periodic refresh, so a stop still recovers within
        // the same one second the rest of the overlay may lag by.
        private readonly HashSet<Entity> m_Unresolved = new HashSet<Entity>();
        private readonly List<Entity> m_StaleZones = new List<Entity>();
        private int m_RefreshIn;
        private Entity m_LastSelected;
        private Entity m_LastEditing;

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

            Entity selected = GetSelectedStop();
            Entity editing = m_ZoneTool.EditingStop;
            bool showSelectedOnly = Mod.Settings != null && Mod.Settings.OnlyShowSelectedStop;

            // Rebuilding zone geometry walks every bus's route segments and path elements, so it is
            // only worth doing when something will actually be drawn. In the default selected-only
            // mode with no stop selected or being edited, nothing will be.
            bool anythingToDraw = !showSelectedOnly || selected != Entity.Null || editing != Entity.Null;

            // A new selection has to be observed now rather than up to a second later, and its cached
            // colour may belong to a different stop. Previously nothing forced this, and the refresh
            // that appeared to happen on click was really the periodic one falling due: the countdown
            // stops while nothing is drawn, so it was almost always at zero by the time a stop was
            // picked. That made an unrestricted whole-city scan land on the selection frame.
            if (selected != m_LastSelected || editing != m_LastEditing)
            {
                m_LastSelected = selected;
                m_LastEditing = editing;
                m_OverlayColors.Clear();
                m_Unresolved.Clear();
                m_RefreshIn = 0;
            }

            // ponytail: refresh periodically instead of tracking every network edit; the overlay may lag by at most one second.
            if (anythingToDraw && m_RefreshIn-- <= 0)
            {
                m_OverlayColors.Clear();
                m_Unresolved.Clear();
                PruneZones(selected, editing);

                // In selected-only mode nothing but these two stops can be drawn, so resolving any
                // other bus's geometry produces a value that is cached and never read.
                BoardingHelpers.FindObservedZones(EntityManager, m_Buses, m_Observed,
                    showSelectedOnly ? selected : Entity.Null,
                    showSelectedOnly ? editing : Entity.Null);
                foreach (KeyValuePair<Entity, BoardingZone> entry in m_Observed)
                {
                    if (!m_Zones.TryGetValue(entry.Key, out BoardingZone existing) ||
                        BoardingPolicy.PreferZoneCandidate(existing.StopDistance, existing.IsPullIn, existing.IsPhysical,
                            entry.Value.StopDistance, entry.Value.IsPullIn, entry.Value.IsPhysical))
                        m_Zones[entry.Key] = entry.Value;
                }
                m_RefreshIn = 60;
            }

            // On-demand resolution only. When the zone is already cached, DrawZone validates and applies
            // the override itself, so calling this unconditionally walked every piece of the selected
            // zone twice per frame.
            if (selected != Entity.Null && !m_Zones.ContainsKey(selected))
                TryGetObservedZone(selected, out _);
            if (showSelectedOnly)
            {
                DrawZone(buffer, selected);
                if (editing != selected)
                    DrawZone(buffer, editing);
                return;
            }
            foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
                DrawZone(buffer, entry.Key);
        }

        private void PruneZones(Entity selected, Entity editing)
        {
            m_StaleZones.Clear();
            foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
            {
                if (!BoardingHelpers.IsRenderableZone(EntityManager, entry.Key, entry.Value))
                    m_StaleZones.Add(entry.Key);
            }
            foreach (Entity stop in m_StaleZones)
                m_Zones.Remove(stop);

            if (m_Zones.Count <= MaxCachedZones)
                return;

            // Over the cap, drop cached observations for stops that are not on screen. Which ones go is
            // arbitrary, and that is acceptable: the cost of an eviction is one re-resolution the next
            // time a bus stops there, whereas an unbounded cache is walked in full by the prune above
            // and grows for the whole session.
            m_StaleZones.Clear();
            int excess = m_Zones.Count - MaxCachedZones;
            foreach (KeyValuePair<Entity, BoardingZone> entry in m_Zones)
            {
                if (excess <= 0)
                    break;
                if (entry.Key == selected || entry.Key == editing)
                    continue;
                m_StaleZones.Add(entry.Key);
                excess--;
            }
            foreach (Entity stop in m_StaleZones)
                m_Zones.Remove(stop);
        }

        private void DrawZone(OverlayRenderSystem.Buffer buffer, Entity stop)
        {
            if (stop == Entity.Null || !m_Zones.TryGetValue(stop, out BoardingZone zone) ||
                !BoardingHelpers.IsRenderableZone(EntityManager, stop, zone))
                return;
            BoardingHelpers.ApplyOverride(EntityManager, stop, ref zone);

            UnityColor color = GetOverlayColor(stop);
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

        // Resolving a stop's colour walks its ConnectedRoute buffer up to twice. This is called once per
        // drawn zone per frame plus once per UI binding write, and the answer only changes when a colour
        // setting changes or the periodic refresh falls due, so it is memoized between those points.
        internal UnityColor GetOverlayColor(Entity stop)
        {
            if (m_OverlayColors.TryGetValue(stop, out UnityColor cached))
                return cached;
            UnityColor resolved = ResolveOverlayColor(stop);
            m_OverlayColors[stop] = resolved;
            return resolved;
        }

        internal void InvalidateColors() => m_OverlayColors.Clear();

        private UnityColor ResolveOverlayColor(Entity stop)
        {
            UnityColor global = Mod.Settings?.GetGlobalOverlayColor() ?? DefaultOverlayColor;
            if (!math.all(math.isfinite(new float4(global.r, global.g, global.b, global.a))))
                global = DefaultOverlayColor;
            global.r = math.saturate(global.r);
            global.g = math.saturate(global.g);
            global.b = math.saturate(global.b);
            global.a = math.saturate(global.a);

            if (EntityManager.HasComponent<BoardingZoneCustomColor>(stop))
                return EntityManager.GetComponentData<BoardingZoneCustomColor>(stop).ToColor(global.a);

            if (EntityManager.HasComponent<BoardingZoneColorOverride>(stop))
            {
                if (!EntityManager.GetComponentData<BoardingZoneColorOverride>(stop).m_UseLineColor)
                    return global;
            }
            else if (TryGetCustomRouteColor(stop, global.a, out UnityColor routeColor))
                return routeColor;

            if (!TryGetFirstRoute(stop, out Entity nativeRoute) ||
                !EntityManager.HasComponent<Game.Routes.Color>(nativeRoute))
                return global;
            UnityColor nativeLine = EntityManager.GetComponentData<Game.Routes.Color>(nativeRoute).m_Color;
            nativeLine.a = global.a;
            return nativeLine;
        }

        internal UnityColor GetRouteOverlayColor(Entity route, UnityColor global)
        {
            if (EntityManager.HasComponent<BoardingZoneCustomColor>(route))
                return EntityManager.GetComponentData<BoardingZoneCustomColor>(route).ToColor(global.a);
            if (EntityManager.HasComponent<Game.Routes.Color>(route))
            {
                UnityColor nativeLine = EntityManager.GetComponentData<Game.Routes.Color>(route).m_Color;
                nativeLine.a = global.a;
                return nativeLine;
            }
            return global;
        }

        internal bool TryGetFirstRoute(Entity stop, out Entity route)
        {
            route = Entity.Null;
            if (!EntityManager.HasBuffer<ConnectedRoute>(stop))
                return false;
            DynamicBuffer<ConnectedRoute> routes = EntityManager.GetBuffer<ConnectedRoute>(stop, true);
            foreach (ConnectedRoute connected in routes)
            {
                Entity waypoint = connected.m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint) ||
                    !EntityManager.HasComponent<Owner>(waypoint))
                    continue;
                route = EntityManager.GetComponentData<Owner>(waypoint).m_Owner;
                if (route == Entity.Null || !EntityManager.Exists(route) ||
                    EntityManager.HasComponent<Deleted>(route) ||
                    EntityManager.HasComponent<Game.Tools.Temp>(route))
                    continue;
                return true;
            }
            route = Entity.Null;
            return false;
        }

        private bool TryGetCustomRouteColor(Entity stop, float alpha, out UnityColor color)
        {
            color = default;
            if (!EntityManager.HasBuffer<ConnectedRoute>(stop))
                return false;
            DynamicBuffer<ConnectedRoute> routes = EntityManager.GetBuffer<ConnectedRoute>(stop, true);
            foreach (ConnectedRoute connected in routes)
            {
                Entity waypoint = connected.m_Waypoint;
                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint) ||
                    !EntityManager.HasComponent<Owner>(waypoint))
                    continue;
                Entity route = EntityManager.GetComponentData<Owner>(waypoint).m_Owner;
                if (route == Entity.Null || !EntityManager.Exists(route) ||
                    EntityManager.HasComponent<Deleted>(route) ||
                    EntityManager.HasComponent<Game.Tools.Temp>(route) ||
                    !EntityManager.HasComponent<BoardingZoneCustomColor>(route))
                    continue;
                color = EntityManager.GetComponentData<BoardingZoneCustomColor>(route).ToColor(alpha);
                return true;
            }
            return false;
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
                if (m_Unresolved.Contains(stop))
                    return false;
                if (!BoardingHelpers.TryGetStopZone(EntityManager, stop, out zone) ||
                    !BoardingHelpers.IsRenderableZone(EntityManager, stop, zone))
                {
                    m_Unresolved.Add(stop);
                    return false;
                }
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
