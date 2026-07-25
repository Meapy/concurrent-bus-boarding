using System.Globalization;
using Colossal.UI.Binding;
using Game;
using Game.Common;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using UnityColor = UnityEngine.Color;

namespace ConcurrentBusBoarding
{
    public partial class BoardingZoneEditorUISystem : UISystemBase
    {
        private const string BindingGroup = "ConcurrentBusBoarding";
        private const int UpdateEveryFrames = 10;
        private static volatile bool s_ResetAllRequested;
        private static volatile bool s_ResetAllColorsRequested;

        private EntityQuery m_ZoneOverrides;
        private EntityQuery m_ColorOverrides;
        private EntityQuery m_CustomColors;
        private SelectedInfoUISystem m_SelectedInfo;
        private BoardingZoneRenderSystem m_RenderSystem;
        private BoardingZoneToolSystem m_ZoneTool;
        private ToolSystem m_ToolSystem;
        private DefaultToolSystem m_DefaultTool;
        private int m_Frame;

        [Preserve]
        protected override void OnCreate()
        {
            base.OnCreate();
            m_ZoneOverrides = GetEntityQuery(
                ComponentType.ReadOnly<BoardingZoneOverride>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_ColorOverrides = GetEntityQuery(
                ComponentType.ReadOnly<BoardingZoneColorOverride>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_CustomColors = GetEntityQuery(
                ComponentType.ReadOnly<BoardingZoneCustomColor>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_RenderSystem = World.GetOrCreateSystemManaged<BoardingZoneRenderSystem>();
            m_ZoneTool = World.GetOrCreateSystemManaged<BoardingZoneToolSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            AddUpdateBinding(new RawValueBinding(BindingGroup, "zoneEditor", WriteEditor));
            AddBinding(new TriggerBinding<float, float>(BindingGroup, "setZone", SetZone,
                ValueReaders.Create<float>(), ValueReaders.Create<float>()));
            AddBinding(new TriggerBinding<bool>(BindingGroup, "setLineColor", SetLineColor,
                ValueReaders.Create<bool>()));
            AddBinding(new TriggerBinding<string>(BindingGroup, "setGlobalOverlayColor", SetGlobalOverlayColor,
                ValueReaders.Create<string>()));
            AddBinding(new TriggerBinding<string, bool>(BindingGroup, "setStopOverlayColor", SetStopOverlayColor,
                ValueReaders.Create<string>(), ValueReaders.Create<bool>()));
            AddBinding(new TriggerBinding(BindingGroup, "resetZone", ResetZone));
            AddBinding(new TriggerBinding(BindingGroup, "toggleZoneEditing", ToggleZoneEditing));
        }

        [Preserve]
        protected override void OnUpdate()
        {
            if (s_ResetAllRequested)
            {
                s_ResetAllRequested = false;
                ResetAllZones();
            }
            if (s_ResetAllColorsRequested)
            {
                s_ResetAllColorsRequested = false;
                ResetAllZoneColors();
            }
            if (m_ZoneTool.EditingStop != Entity.Null && TryGetSelectedStop(out Entity selectedStop) &&
                selectedStop != m_ZoneTool.EditingStop)
                StopEditing();
            if (++m_Frame >= UpdateEveryFrames)
            {
                m_Frame = 0;
                base.OnUpdate();
            }
        }

        private void WriteEditor(IJsonWriter writer)
        {
            bool visible = TryGetSelectedStop(out Entity stop);
            BoardingZone zone = default;
            bool available = visible && m_RenderSystem.TryGetObservedZone(stop, out zone);
            bool customized = visible && EntityManager.HasComponent<BoardingZoneOverride>(stop);
            float length = BoardingPolicy.OrdinaryZoneLength;

            if (available)
            {
                if (customized)
                {
                    BoardingZoneOverride custom = EntityManager.GetComponentData<BoardingZoneOverride>(stop);
                    length = custom.m_Length;
                }
                else
                {
                    length = BoardingHelpers.GetZoneLength(zone);
                }
            }

            writer.TypeBegin("ConcurrentBusBoarding.ZoneEditor");
            writer.PropertyName("visible");
            writer.Write(visible);
            writer.PropertyName("available");
            writer.Write(available);
            writer.PropertyName("customized");
            writer.Write(customized);
            writer.PropertyName("lineColor");
            writer.Write(visible && EntityManager.HasComponent<BoardingZoneColorOverride>(stop) &&
                EntityManager.GetComponentData<BoardingZoneColorOverride>(stop).m_UseLineColor);
            writer.PropertyName("forceGlobal");
            writer.Write(visible && EntityManager.HasComponent<BoardingZoneColorOverride>(stop) &&
                !EntityManager.GetComponentData<BoardingZoneColorOverride>(stop).m_UseLineColor);
            bool customStopColor = visible && EntityManager.HasComponent<BoardingZoneCustomColor>(stop);
            Entity route = Entity.Null;
            bool hasRoute = visible && m_RenderSystem.TryGetFirstRoute(stop, out route);
            bool customLineColor = hasRoute && EntityManager.HasComponent<BoardingZoneCustomColor>(route);
            writer.PropertyName("customStopColor");
            writer.Write(customStopColor);
            writer.PropertyName("customLineColor");
            writer.Write(customLineColor);
            writer.PropertyName("hasLine");
            writer.Write(hasRoute);
            UnityColor globalColor = Mod.Settings?.GetGlobalOverlayColor() ??
                new UnityColor(0.15f, 0.55f, 0.95f, 0.18f);
            WriteColor(writer, "globalColor", globalColor);
            WriteColor(writer, "stopColor", visible ? m_RenderSystem.GetOverlayColor(stop) : globalColor);
            WriteColor(writer, "routeColor",
                hasRoute ? m_RenderSystem.GetRouteOverlayColor(route, globalColor) : globalColor);
            writer.PropertyName("editing");
            writer.Write(visible && m_ZoneTool.EditingStop == stop);
            writer.PropertyName("offset");
            writer.Write(0f);
            writer.PropertyName("length");
            writer.Write(length);
            writer.TypeEnd();
        }

        private void SetZone(float ignoredOffset, float length)
        {
            if (!TryGetSelectedStop(out Entity stop) || !m_RenderSystem.TryGetObservedZone(stop, out _))
                return;

            var custom = new BoardingZoneOverride(0f,
                math.clamp(length, BoardingPolicy.MinimumCustomZoneLength, BoardingPolicy.MaximumCustomZoneLength));
            if (EntityManager.HasComponent<BoardingZoneOverride>(stop))
                EntityManager.SetComponentData(stop, custom);
            else
                EntityManager.AddComponentData(stop, custom);
            Refresh();
        }

        private void ResetZone()
        {
            if (!TryGetSelectedStop(out Entity stop))
                return;
            if (EntityManager.HasComponent<BoardingZoneOverride>(stop))
                EntityManager.RemoveComponent<BoardingZoneOverride>(stop);
            Refresh();
        }

        private void SetLineColor(bool useLineColor)
        {
            if (!TryGetSelectedStop(out Entity stop))
                return;
            var color = new BoardingZoneColorOverride(useLineColor);
            if (EntityManager.HasComponent<BoardingZoneColorOverride>(stop))
                EntityManager.SetComponentData(stop, color);
            else
                EntityManager.AddComponentData(stop, color);
            if (EntityManager.HasComponent<BoardingZoneCustomColor>(stop))
                EntityManager.RemoveComponent<BoardingZoneCustomColor>(stop);
            Refresh();
        }

        private void SetGlobalOverlayColor(string rgba)
        {
            if (Mod.Settings != null && Mod.Settings.SetGlobalOverlayColor(rgba))
                Refresh();
        }

        private void SetStopOverlayColor(string rgb, bool wholeLine)
        {
            if (!TryGetSelectedStop(out Entity stop) || rgb == null || rgb.Length != 6 ||
                !int.TryParse(rgb, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
                return;
            Entity target = stop;
            if (wholeLine && !m_RenderSystem.TryGetFirstRoute(stop, out target))
                return;
            var color = new BoardingZoneCustomColor(packed);
            if (EntityManager.HasComponent<BoardingZoneCustomColor>(target))
                EntityManager.SetComponentData(target, color);
            else
                EntityManager.AddComponentData(target, color);
            if (!wholeLine && EntityManager.HasComponent<BoardingZoneColorOverride>(stop))
                EntityManager.RemoveComponent<BoardingZoneColorOverride>(stop);
            Refresh();
        }

        internal static void RequestResetAllZones() => s_ResetAllRequested = true;
        internal static void RequestResetAllZoneColors() => s_ResetAllColorsRequested = true;

        private void ResetAllZones()
        {
            int count = m_ZoneOverrides.CalculateEntityCount();
            if (count != 0)
            {
                EntityManager.RemoveComponent<BoardingZoneOverride>(m_ZoneOverrides);
                Mod.Log.Info($"Reset {count} customized boarding zone(s)");
            }
            Refresh();
        }

        private void ResetAllZoneColors()
        {
            int sourceCount = m_ColorOverrides.CalculateEntityCount();
            int customCount = m_CustomColors.CalculateEntityCount();
            if (sourceCount != 0)
                EntityManager.RemoveComponent<BoardingZoneColorOverride>(m_ColorOverrides);
            if (customCount != 0)
                EntityManager.RemoveComponent<BoardingZoneCustomColor>(m_CustomColors);
            if (sourceCount + customCount != 0)
            {
                Mod.Log.Info($"Reset {sourceCount + customCount} boarding-zone colour override(s)");
            }
            Refresh();
        }

        private static void WriteColor(IJsonWriter writer, string property, UnityColor color)
        {
            writer.PropertyName(property);
            writer.TypeBegin("ConcurrentBusBoarding.Color");
            writer.PropertyName("r");
            writer.Write(color.r);
            writer.PropertyName("g");
            writer.Write(color.g);
            writer.PropertyName("b");
            writer.Write(color.b);
            writer.PropertyName("a");
            writer.Write(color.a);
            writer.TypeEnd();
        }

        private void ToggleZoneEditing()
        {
            if (TryGetSelectedStop(out Entity stop) && m_RenderSystem.TryGetObservedZone(stop, out _))
            {
                if (m_ZoneTool.EditingStop == stop)
                    StopEditing();
                else
                {
                    m_SelectedInfo.Focus(stop);
                    m_ZoneTool.Begin(stop);
                    m_ToolSystem.activeTool = m_ZoneTool;
                }
            }
            Refresh();
        }

        private void StopEditing()
        {
            if (m_ToolSystem.activeTool == m_ZoneTool)
                m_ToolSystem.activeTool = m_DefaultTool;
            else
                m_ZoneTool.End();
        }

        private bool TryGetSelectedStop(out Entity stop)
        {
            stop = m_SelectedInfo.selectedEntity;
            if (BoardingHelpers.IsPassengerBusStop(EntityManager, stop))
                return true;
            if (stop != Entity.Null && EntityManager.HasComponent<Connected>(stop))
            {
                stop = EntityManager.GetComponentData<Connected>(stop).m_Connected;
                if (BoardingHelpers.IsPassengerBusStop(EntityManager, stop))
                    return true;
            }
            stop = Entity.Null;
            return false;
        }

        private void Refresh()
        {
            m_Frame = UpdateEveryFrames;
            m_RenderSystem.Invalidate();
        }
    }
}
