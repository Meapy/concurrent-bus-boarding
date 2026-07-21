using Colossal.Mathematics;
using Game.Prefabs;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace ConcurrentBusBoarding
{
    public partial class BoardingZoneToolSystem : ToolBaseSystem
    {
        private const float HandleRadiusPixels = 30f;

        private Entity m_EditingStop;
        private int m_DragHandle;
        private float2 m_InitialBounds;
        private float m_InitialMousePosition;
        private float m_DragPlaneHeight;

        public override string toolID => "Concurrent Bus Boarding Zone Tool";
        internal Entity EditingStop => m_EditingStop;

        public override PrefabBase GetPrefab() => null;
        public override bool TrySetPrefab(PrefabBase prefab) => false;

        protected override void OnCreate()
        {
            base.OnCreate();
            Enabled = false;
        }

        internal void Begin(Entity stop)
        {
            m_EditingStop = stop;
            m_DragHandle = 0;
        }

        internal void End()
        {
            m_EditingStop = Entity.Null;
            m_DragHandle = 0;
        }

        protected override void OnStopRunning()
        {
            base.OnStopRunning();
            End();
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            Mouse mouse = Mouse.current;
            Camera camera = Camera.main;
            BoardingZoneRenderSystem renderSystem = World.GetExistingSystemManaged<BoardingZoneRenderSystem>();
            if (m_EditingStop == Entity.Null || mouse == null || camera == null || renderSystem == null ||
                !renderSystem.TryGetObservedZone(m_EditingStop, out BoardingZone zone))
                return inputDeps;

            if (mouse.rightButton.wasPressedThisFrame ||
                (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame))
            {
                m_ToolSystem.activeTool = m_DefaultToolSystem;
                return inputDeps;
            }

            float2 bounds = BoardingHelpers.GetZoneBounds(zone);
            Vector2 pointer = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
                BeginDrag(camera, pointer, zone, bounds);

            if (m_DragHandle != 0 && mouse.leftButton.isPressed && TryGetCurvePosition(camera, pointer, zone, out float position))
                UpdateZone(zone, position);

            if (mouse.leftButton.wasReleasedThisFrame)
                m_DragHandle = 0;
            return inputDeps;
        }

        private void BeginDrag(Camera camera, Vector2 pointer, BoardingZone zone, float2 bounds)
        {
            float startDistance = EndHandleDistance(camera, pointer, zone, bounds.x);
            float endDistance = EndHandleDistance(camera, pointer, zone, bounds.y);
            float centerPosition = math.csum(bounds) * 0.5f;
            float centerDistance = ScreenDistance(camera, pointer, MathUtils.Position(zone.Curve.m_Bezier, centerPosition));
            float nearest = math.min(centerDistance, math.min(startDistance, endDistance));
            if (nearest > HandleRadiusPixels)
                return;

            m_DragHandle = nearest == centerDistance ? 3 : nearest == startDistance ? 1 : 2;
            m_InitialBounds = bounds;
            m_DragPlaneHeight = MathUtils.Position(zone.Curve.m_Bezier,
                m_DragHandle == 1 ? bounds.x : m_DragHandle == 2 ? bounds.y : centerPosition).y;
            TryGetCurvePosition(camera, pointer, zone, out m_InitialMousePosition);
        }

        private void UpdateZone(BoardingZone zone, float mousePosition)
        {
            float minimumRange = BoardingPolicy.MinimumCustomZoneLength / math.max(1f, zone.Curve.m_Length);
            float2 bounds = m_InitialBounds;
            if (m_DragHandle == 1)
                bounds.x = math.clamp(mousePosition, 0f, bounds.y - minimumRange);
            else if (m_DragHandle == 2)
                bounds.y = math.clamp(mousePosition, bounds.x + minimumRange, 1f);
            else
            {
                float range = bounds.y - bounds.x;
                float start = math.clamp(bounds.x + mousePosition - m_InitialMousePosition, 0f, 1f - range);
                bounds = new float2(start, start + range);
            }

            float center = math.csum(bounds) * 0.5f;
            float offset = (center - zone.CurvePosition) * zone.Curve.m_Length * zone.Direction;
            float length = (bounds.y - bounds.x) * zone.Curve.m_Length;
            BoardingZoneOverride custom = new BoardingZoneOverride(
                math.clamp(offset, -BoardingPolicy.MaximumCustomZoneOffset, BoardingPolicy.MaximumCustomZoneOffset),
                math.clamp(length, BoardingPolicy.MinimumCustomZoneLength, BoardingPolicy.MaximumCustomZoneLength));
            if (EntityManager.HasComponent<BoardingZoneOverride>(m_EditingStop))
                EntityManager.SetComponentData(m_EditingStop, custom);
            else
                EntityManager.AddComponentData(m_EditingStop, custom);
        }

        private bool TryGetCurvePosition(Camera camera, Vector2 pointer, BoardingZone zone, out float curvePosition)
        {
            Ray ray = camera.ScreenPointToRay(pointer);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, m_DragPlaneHeight, 0f));
            if (!plane.Raycast(ray, out float distance))
            {
                curvePosition = 0f;
                return false;
            }
            MathUtils.Distance(zone.Curve.m_Bezier, (float3)ray.GetPoint(distance), out curvePosition);
            return true;
        }

        private static float EndHandleDistance(Camera camera, Vector2 pointer, BoardingZone zone, float curvePosition)
        {
            float3 position = MathUtils.Position(zone.Curve.m_Bezier, curvePosition);
            float nearby = curvePosition < 0.99f ? curvePosition + 0.01f : curvePosition - 0.01f;
            float3 tangent = math.normalizesafe(MathUtils.Position(zone.Curve.m_Bezier, nearby) - position, new float3(0f, 0f, 1f));
            float3 side = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tangent), new float3(1f, 0f, 0f));
            float halfWidth = math.max(1.5f, zone.Width * 0.5f);
            return math.min(ScreenDistance(camera, pointer, position + side * halfWidth),
                ScreenDistance(camera, pointer, position - side * halfWidth));
        }

        private static float ScreenDistance(Camera camera, Vector2 pointer, float3 position)
        {
            Vector3 screen = camera.WorldToScreenPoint((Vector3)position);
            return screen.z <= 0f ? float.MaxValue : Vector2.Distance(pointer, new Vector2(screen.x, screen.y));
        }
    }
}
