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

            Vector2 pointer = mouse.position.ReadValue();
            if (mouse.leftButton.wasPressedThisFrame)
                BeginDrag(camera, pointer, zone);

            if (m_DragHandle != 0 && mouse.leftButton.isPressed &&
                TryGetPointerWorld(camera, pointer, out float3 world) &&
                BoardingHelpers.TryGetDistanceFromFront(zone, world, out float length))
                UpdateZone(length);

            if (mouse.leftButton.wasReleasedThisFrame)
                m_DragHandle = 0;
            return inputDeps;
        }

        private void BeginDrag(Camera camera, Vector2 pointer, BoardingZone zone)
        {
            if (!BoardingHelpers.TryGetRearEdge(zone, out BoardingZonePiece piece, out float2 bounds))
                return;
            float rear = piece.Direction >= 0 ? bounds.x : bounds.y;
            if (EndHandleDistance(camera, pointer, piece, rear) > HandleRadiusPixels)
                return;

            m_DragHandle = 1;
            m_DragPlaneHeight = MathUtils.Position(piece.Curve.m_Bezier, rear).y;
        }

        private void UpdateZone(float length)
        {
            if (!math.isfinite(length))
                return;
            BoardingZoneOverride custom = new BoardingZoneOverride(0f,
                math.clamp(length, BoardingPolicy.MinimumCustomZoneLength, BoardingPolicy.MaximumCustomZoneLength));
            if (EntityManager.HasComponent<BoardingZoneOverride>(m_EditingStop))
                EntityManager.SetComponentData(m_EditingStop, custom);
            else
                EntityManager.AddComponentData(m_EditingStop, custom);
        }

        private bool TryGetPointerWorld(Camera camera, Vector2 pointer, out float3 world)
        {
            Ray ray = camera.ScreenPointToRay(pointer);
            Plane plane = new Plane(Vector3.up, new Vector3(0f, m_DragPlaneHeight, 0f));
            if (!plane.Raycast(ray, out float distance))
            {
                world = default;
                return false;
            }
            world = (float3)ray.GetPoint(distance);
            return math.all(math.isfinite(world));
        }

        private static float EndHandleDistance(Camera camera, Vector2 pointer, BoardingZonePiece piece, float curvePosition)
        {
            float3 position = MathUtils.Position(piece.Curve.m_Bezier, curvePosition);
            float nearby = curvePosition < 0.99f ? curvePosition + 0.01f : curvePosition - 0.01f;
            float3 tangent = math.normalizesafe(MathUtils.Position(piece.Curve.m_Bezier, nearby) - position, new float3(0f, 0f, 1f));
            float3 side = math.normalizesafe(math.cross(new float3(0f, 1f, 0f), tangent), new float3(1f, 0f, 0f));
            float halfWidth = math.max(1.5f, piece.Width * 0.5f);
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
