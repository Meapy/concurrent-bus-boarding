using System;
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

        private EntityQuery m_ZoneOverrides;
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
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_RenderSystem = World.GetOrCreateSystemManaged<BoardingZoneRenderSystem>();
            m_ZoneTool = World.GetOrCreateSystemManaged<BoardingZoneToolSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            AddUpdateBinding(new RawValueBinding(BindingGroup, "zoneEditor", WriteEditor));
            AddBinding(new TriggerBinding<float, float>(BindingGroup, "setZone", SetZone,
                ValueReaders.Create<float>(), ValueReaders.Create<float>()));
            AddBinding(new TriggerBinding<float, float, float, float>(BindingGroup, "setZoneColor", SetZoneColor,
                ValueReaders.Create<float>(), ValueReaders.Create<float>(), ValueReaders.Create<float>(), ValueReaders.Create<float>()));
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
            writer.PropertyName("editing");
            writer.Write(visible && m_ZoneTool.EditingStop == stop);
            writer.PropertyName("offset");
            writer.Write(0f);
            writer.PropertyName("length");
            writer.Write(length);
            writer.PropertyName("color");
            writer.Write(GetEditorColor(stop, available, visible));
            writer.TypeEnd();
        }

        private void SetZone(float ignoredOffset, float length)
        {
            if (!TryGetSelectedStop(out Entity stop) || !m_RenderSystem.TryGetObservedZone(stop, out _))
                return;

            BoardingZoneOverride custom = EntityManager.HasComponent<BoardingZoneOverride>(stop)
                ? EntityManager.GetComponentData<BoardingZoneOverride>(stop)
                : new BoardingZoneOverride(0f, BoardingPolicy.OrdinaryZoneLength);
            custom.m_Offset = 0f;
            custom.m_Length = math.clamp(length, BoardingPolicy.MinimumCustomZoneLength,
                BoardingPolicy.MaximumCustomZoneLength);
            if (EntityManager.HasComponent<BoardingZoneOverride>(stop))
                EntityManager.SetComponentData(stop, custom);
            else
                EntityManager.AddComponentData(stop, custom);
            Refresh();
        }

        private void SetZoneColor(float r, float g, float b, float a)
        {
            if (!TryGetSelectedStop(out Entity stop) || !m_RenderSystem.TryGetObservedZone(stop, out _))
                return;

            BoardingZoneOverride custom = EntityManager.HasComponent<BoardingZoneOverride>(stop)
                ? EntityManager.GetComponentData<BoardingZoneOverride>(stop)
                : new BoardingZoneOverride(0f, BoardingPolicy.OrdinaryZoneLength);
            custom.m_Color = new UnityColor(r, g, b, a);
            if (EntityManager.HasComponent<BoardingZoneOverride>(stop))
                EntityManager.SetComponentData(stop, custom);
            else
                EntityManager.AddComponentData(stop, custom);
            Refresh();
        }

        private string GetEditorColor(Entity stop, bool available, bool visible)
        {
            if (visible && EntityManager.HasComponent<BoardingZoneOverride>(stop))
            {
                BoardingZoneOverride custom = EntityManager.GetComponentData<BoardingZoneOverride>(stop);
                return ToHexColor(custom.m_Color);
            }
            if (Mod.Settings?.UseDefaultOverlayColor == true)
                return ToHexColor(ParseHexColor(Mod.Settings.DefaultOverlayColorHex));
            return available ? ToHexColor(new UnityColor(0.15f, 0.55f, 0.95f, 0.28f)) : "#2f8fe8";
        }

        private static string ToHexColor(UnityColor color)
        {
            int r = math.clamp((int)Math.Round(color.r * 255f), 0, 255);
            int g = math.clamp((int)Math.Round(color.g * 255f), 0, 255);
            int b = math.clamp((int)Math.Round(color.b * 255f), 0, 255);
            return $"#{r:X2}{g:X2}{b:X2}";
        }

        private static UnityColor ParseHexColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                value = "#2f8fe8";
            string hex = value.Trim();
            if (hex.StartsWith("#"))
                hex = hex.Substring(1);
            if (hex.Length == 3)
            {
                char[] expanded = new char[6];
                for (int i = 0; i < 3; i++)
                {
                    expanded[i * 2] = hex[i];
                    expanded[i * 2 + 1] = hex[i];
                }
                hex = new string(expanded);
            }
            if (hex.Length == 6 && int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int r) &&
                int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out int g) &&
                int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
                return new UnityColor(r / 255f, g / 255f, b / 255f, 0.28f);
            return new UnityColor(0.15f, 0.55f, 0.95f, 0.28f);
        }

        private void ResetZone()
        {
            if (!TryGetSelectedStop(out Entity stop))
                return;
            if (EntityManager.HasComponent<BoardingZoneOverride>(stop))
                EntityManager.RemoveComponent<BoardingZoneOverride>(stop);
            Refresh();
        }

        internal static void RequestResetAllZones() => s_ResetAllRequested = true;

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
