using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Rendering;
using Game.Tools;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using Edge = Game.Net.Edge;
using Curve = Game.Net.Curve;

namespace LiveTrafficRouting
{
    /// <summary>
    /// The "Temporary roadworks" tool: click a road to give it the selected mark (closed, avoid, prefer); click it again
    /// with the same mode, or right-click, to remove the mark. Marks are temporary (see <see cref="RoadMarkSystem"/>).
    /// Opened and closed from the Live Traffic panel.
    /// </summary>
    public partial class RoadMarkToolSystem : ToolBaseSystem
    {
        private RoadMarkSystem m_RoadMarkSystem;
        private OverlayRenderSystem m_OverlayRenderSystem;
        private Entity m_Hovered;

        /// <summary>Mark set by a click (RoadMarkSystem.kClosed / kAvoid / kPrefer, or kNone to remove).</summary>
        internal byte Mode = RoadMarkSystem.kClosed;

        public override string toolID => "Live Traffic Temporary Roadworks";

        protected override void OnCreate()
        {
            base.OnCreate();
            m_RoadMarkSystem = World.GetOrCreateSystemManaged<RoadMarkSystem>();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            Enabled = false;
        }

        public override PrefabBase GetPrefab() => null;

        public override bool TrySetPrefab(PrefabBase prefab) => false;

        public override void InitializeRaycast()
        {
            base.InitializeRaycast();
            m_ToolRaycastSystem.typeMask = TypeMask.Net;
            m_ToolRaycastSystem.netLayerMask = Layer.Road | Layer.PublicTransportRoad;
        }

        internal bool IsActive => m_ToolSystem.activeTool == this;

        internal void Activate(bool on)
        {
            Logger.Info(on ? $"Temporary roadworks tool on ({RoadMarkSystem.MarkName(Mode)})." : "Temporary roadworks tool off.");
            if (on) m_ToolSystem.activeTool = this;
            else if (IsActive) m_ToolSystem.activeTool = m_DefaultToolSystem;
        }

        protected override void OnStartRunning()
        {
            base.OnStartRunning();
            m_Hovered = Entity.Null;
        }

        protected override JobHandle OnUpdate(JobHandle inputDeps)
        {
            applyAction.shouldBeEnabled = true;
            cancelAction.shouldBeEnabled = true;

            m_Hovered = Entity.Null;
            if (GetRaycastResult(out Entity entity, out RaycastHit _) && EntityManager.HasComponent<Edge>(entity) &&
                EntityManager.HasComponent<Road>(entity))
            {
                m_Hovered = entity;
            }

            if (m_Hovered != Entity.Null)
            {
                if (applyAction.WasPressedThisFrame())
                {
                    byte current = m_RoadMarkSystem.GetMark(m_Hovered);
                    byte next = current == Mode ? RoadMarkSystem.kNone : Mode;
                    m_RoadMarkSystem.SetMark(m_Hovered, next);
                }
                else if (cancelAction.WasPressedThisFrame())
                {
                    m_RoadMarkSystem.SetMark(m_Hovered, RoadMarkSystem.kNone);
                }
            }

            DrawOverlay();
            return inputDeps;
        }

        private void DrawOverlay()
        {
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle deps);
            deps.Complete();
            foreach (Entity edge in m_RoadMarkSystem.Edges())
            {
                if (!EntityManager.Exists(edge) || !EntityManager.HasComponent<Curve>(edge)) continue;
                Curve curve = EntityManager.GetComponentData<Curve>(edge);
                buffer.DrawCurve(ColorOf(m_RoadMarkSystem.GetMark(edge)), curve.m_Bezier, 4f);
            }
            if (m_Hovered != Entity.Null && EntityManager.HasComponent<Curve>(m_Hovered))
            {
                Color hover = ColorOf(Mode);
                hover.a = 0.5f;
                buffer.DrawCurve(hover, EntityManager.GetComponentData<Curve>(m_Hovered).m_Bezier, 6f);
            }
            m_OverlayRenderSystem.AddBufferWriter(default);
        }

        private static Color ColorOf(byte mark)
        {
            switch (mark)
            {
                case RoadMarkSystem.kClosed: return new Color(0.9f, 0.15f, 0.1f, 0.8f);
                case RoadMarkSystem.kAvoid: return new Color(1f, 0.6f, 0.1f, 0.8f);
                case RoadMarkSystem.kPrefer: return new Color(0.2f, 0.8f, 0.3f, 0.8f);
                default: return new Color(0.8f, 0.8f, 0.8f, 0.6f);
            }
        }
    }
}
