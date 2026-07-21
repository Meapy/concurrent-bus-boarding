using Colossal.UI.Binding;
using Game;
using Game.Routes;
using Game.Tools;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace ConcurrentBusBoarding
{
    public partial class BoardingZoneEditorUISystem : UISystemBase
    {
        private const string BindingGroup = "ConcurrentBusBoarding";
        private const int UpdateEveryFrames = 10;

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
            m_SelectedInfo = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_RenderSystem = World.GetOrCreateSystemManaged<BoardingZoneRenderSystem>();
            m_ZoneTool = World.GetOrCreateSystemManaged<BoardingZoneToolSystem>();
            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_DefaultTool = World.GetOrCreateSystemManaged<DefaultToolSystem>();
            AddUpdateBinding(new RawValueBinding(BindingGroup, "zoneEditor", WriteEditor));
            AddBinding(new TriggerBinding<float, float>(BindingGroup, "setZone", SetZone,
                ValueReaders.Create<float>(), ValueReaders.Create<float>()));
            AddBinding(new TriggerBinding(BindingGroup, "resetZone", ResetZone));
            AddBinding(new TriggerBinding(BindingGroup, "toggleZoneEditing", ToggleZoneEditing));
        }

        [Preserve]
        protected override void OnUpdate()
        {
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
            float offset = 0f;
            float length = BoardingPolicy.OrdinaryZoneLength;

            if (available)
            {
                if (customized)
                {
                    BoardingZoneOverride custom = EntityManager.GetComponentData<BoardingZoneOverride>(stop);
                    offset = custom.m_Offset;
                    length = custom.m_Length;
                }
                else
                {
                    float2 bounds = BoardingHelpers.GetZoneBounds(zone);
                    float center = (bounds.x + bounds.y) * 0.5f;
                    offset = (center - zone.CurvePosition) * zone.Curve.m_Length * zone.Direction;
                    length = (bounds.y - bounds.x) * zone.Curve.m_Length;
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
            writer.Write(offset);
            writer.PropertyName("length");
            writer.Write(length);
            writer.TypeEnd();
        }

        private void SetZone(float offset, float length)
        {
            if (!TryGetSelectedStop(out Entity stop) || !m_RenderSystem.TryGetObservedZone(stop, out _))
                return;

            var custom = new BoardingZoneOverride(
                math.clamp(offset, -BoardingPolicy.MaximumCustomZoneOffset, BoardingPolicy.MaximumCustomZoneOffset),
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
