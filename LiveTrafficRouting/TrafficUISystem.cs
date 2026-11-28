using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.Net;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Feeds the in-game statistics panel (UI module in /UI). Values are JSON strings, rebuilt only when the
    /// report store changes.
    /// </summary>
    public partial class TrafficUISystem : UISystemBase
    {
        internal const string Group = "liveTrafficRouting";

        private int m_Version = -1;
        private int m_DataVersion = -1;
        private string m_CurrentJson = "{}";
        private string m_HistoryJson = "[]";
        private string m_RunsJson = "[]";
        private string m_HotspotsJson = "{}";
        private int m_MarksVersion = -1;
        private string m_RoadworksJson = "{\"cleared\":0,\"roads\":[]}";
        private RoadMarkSystem m_RoadMarkSystem;
        private RoadMarkToolSystem m_RoadMarkTool;

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "enabled", () => Mod.Settings == null || Mod.Settings.ShowStatistics));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "current", () => { Refresh(); return m_CurrentJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "history", () => { Refresh(); return m_HistoryJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "runs", () => { Refresh(); return m_RunsJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hotspots", () => { Refresh(); return m_HotspotsJson; }));
            AddBinding(new TriggerBinding(Group, "deleteAll", StatisticsReset.DeleteAll));
            AddBinding(new TriggerBinding(Group, "deleteLive", StatisticsReset.DeleteLive));
            AddBinding(new TriggerBinding<string>(Group, "deleteRun", StatisticsReset.DeleteRun));
            AddBinding(new TriggerBinding<string>(Group, "togglePin", TrafficReportStore.TogglePin));
            AddBinding(new TriggerBinding<string>(Group, "focusPlace", FocusPlace));
            // "x|z": a map point (an origin / destination area), camera only.
            AddBinding(new TriggerBinding<string>(Group, "focusPoint", xz =>
            {
                string[] parts = xz.Split('|');
                if (parts.Length != 2 ||
                    !float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z)) return;
                float y = 0f;
                try
                {
                    var terrain = World.GetOrCreateSystemManaged<Game.Simulation.TerrainSystem>();
                    Game.Simulation.TerrainHeightData heights = terrain.GetHeightData(true);
                    y = Game.Simulation.TerrainUtils.SampleHeight(ref heights, new Unity.Mathematics.float3(x, 0f, z));
                }
                catch (System.Exception) { /* height is cosmetic */ }
                MoveCameraTo(new Unity.Mathematics.float3(x, y, z));
            }));

            // Temporary roadworks (marks are not saved).
            m_RoadMarkSystem = World.GetOrCreateSystemManaged<RoadMarkSystem>();
            m_RoadMarkTool = World.GetOrCreateSystemManaged<RoadMarkToolSystem>();
            AddUpdateBinding(new GetterValueBinding<string>(Group, "roadworks", RoadworksJson));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "roadworksToolOn", () => m_RoadMarkTool.IsActive));
            AddUpdateBinding(new GetterValueBinding<int>(Group, "roadworksMode", () => m_RoadMarkTool.Mode));
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "modActive", () => Mod.Settings == null || Mod.Settings.IsActive));
            AddBinding(new TriggerBinding<int>(Group, "roadworksTool", mode =>
            {
                if (mode <= 0)
                {
                    m_RoadMarkTool.Activate(false);
                    return;
                }
                m_RoadMarkTool.Mode = (byte)mode;
                m_RoadMarkTool.Activate(true);
            }));
            AddBinding(new TriggerBinding<string>(Group, "roadworksRemove", id =>
            {
                if (RoadMarkSystem.TryParseId(id, out Entity edge) && EntityManager.Exists(edge)) m_RoadMarkSystem.SetMark(edge, RoadMarkSystem.kNone);
            }));
            AddBinding(new TriggerBinding<string>(Group, "roadworksFocus", id =>
            {
                if (!RoadMarkSystem.TryParseId(id, out Entity edge) || !EntityManager.Exists(edge)) return;
                MoveCameraTo(edge);
            }));
            AddBinding(new TriggerBinding(Group, "roadworksClear", () => m_RoadMarkSystem.ClearAll()));
            AddBinding(new TriggerBinding(Group, "roadworksDismissNotice", () =>
            {
                RoadMarkSystem.ClearedOnLoad = false;
                RoadMarkSystem.Version++;
            }));

            // Bottleneck finder: the last finished game hour.
            AddUpdateBinding(new GetterValueBinding<string>(Group, "bottlenecks", () => BottleneckSystem.Json));
            // Map overlay of a bottleneck or possible link (id from the "bottlenecks" JSON; "" hides it).
            AddUpdateBinding(new GetterValueBinding<string>(Group, "overlayShown", () => BottleneckOverlaySystem.ShownId));
            AddBinding(new TriggerBinding<string>(Group, "showOverlay", id =>
            {
                if (string.IsNullOrEmpty(id) || id == BottleneckOverlaySystem.ShownId || !BottleneckSystem.Views.TryGetValue(id, out OverlayView view))
                {
                    BottleneckOverlaySystem.Hide();
                    return;
                }
                BottleneckOverlaySystem.Show(id, view);
                // A bottleneck: its junction (the yellow ring) when there is one, else the street.
                Entity focus = view.IsLink ? (view.LinkKind != BottleneckSystem.kNewLink && view.FixAt != Entity.Null ? view.FixAt : view.LinkA)
                    : (view.Junction != Entity.Null && EntityManager.Exists(view.Junction) ? view.Junction : view.Place);
                if (focus != Entity.Null && EntityManager.Exists(focus))
                {
                    Entity target = focus;
                    if (EntityManager.HasBuffer<AggregateElement>(focus))
                    {
                        DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(focus, true);
                        if (edges.Length > 0) target = edges[edges.Length / 2].m_Edge;
                    }

                    MoveCameraTo(target);
                }
            }));
            AddBinding(new TriggerBinding<string>(Group, "focusEntity", id =>
            {
                if (!RoadMarkSystem.TryParseId(id, out Entity entity) || !EntityManager.Exists(entity)) return;
                Entity target = entity;
                if (EntityManager.HasBuffer<AggregateElement>(entity))
                {
                    DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(entity, true);
                    if (edges.Length > 0) target = edges[edges.Length / 2].m_Edge;
                }
                MoveCameraTo(target);
            }));

            // Copy a comparison summary (built by the panel) to the clipboard, for sharing or bug reports.
            AddBinding(new TriggerBinding<string>(Group, "copySummary", text =>
            {
                UnityEngine.GUIUtility.systemCopyBuffer = text ?? "";
                Logger.Info("Comparison summary copied to the clipboard.");
            }));
            Logger.Info("TrafficUISystem created.");
        }

        /// <summary>
        /// Shows a hotspot on the map: selects the street or junction (opens the game's info panel) and lets the
        /// camera follow it, like the game's own focus button. A street is shown at a segment in its middle.
        /// </summary>
        private void FocusPlace(string arg)
        {
            // "name|x|z": the queue's position (v1.1). Several streets can share a name, and entity ids change on load,
            // so the street with this name nearest to where the queue was is the one shown.
            string place = arg ?? "";
            string[] parts = place.Split('|');
            if (parts.Length == 3 &&
                float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            {
                place = parts[0];
                Entity near = FindNear(place, new Unity.Mathematics.float2(x, z));
                if (near != Entity.Null)
                {
                    Show(near);
                    return;
                }
            }

            Entity entity = FindStreetByName(place);
            if (entity == Entity.Null && TrafficReportStore.TryGetPlace(place, out Entity cached) && EntityManager.Exists(cached)) entity = cached;
            if (entity == Entity.Null)
            {
                Logger.Info($"Hotspot '{place}' not found on the map (it may be from an earlier session or was renamed).");
                return;
            }
            Entity target = entity;
            if (EntityManager.HasBuffer<AggregateElement>(entity))
            {
                DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(entity, true);
                if (edges.Length > 0) target = edges[edges.Length / 2].m_Edge;
            }
            MoveCameraTo(target);
        }

        /// <summary>
        /// Moves the camera to a road, junction, street, building or district without selecting it. Selecting and
        /// following a road makes the game's pin jump between the road's label points.
        /// </summary>
        private void MoveCameraTo(Entity entity)
        {
            if (TryPositionOf(entity, out Unity.Mathematics.float3 position)) MoveCameraTo(position);
        }

        private void MoveCameraTo(Unity.Mathematics.float3 position)
        {
            var camera = World.GetOrCreateSystemManaged<Game.Rendering.CameraUpdateSystem>();
            if (camera.gamePlayController == null) return;
            World.GetOrCreateSystemManaged<SelectedInfoUISystem>().SetSelection(Entity.Null);
            if (camera.orbitCameraController != null) camera.orbitCameraController.followedEntity = Entity.Null;
            camera.gamePlayController.pivot = position;
            camera.activeCameraController = camera.gamePlayController;
        }

        private bool TryPositionOf(Entity e, out Unity.Mathematics.float3 position)
        {
            position = default;
            if (e == Entity.Null || !EntityManager.Exists(e)) return false;
            if (EntityManager.HasComponent<Game.Net.Node>(e))
            {
                position = EntityManager.GetComponentData<Game.Net.Node>(e).m_Position;
                return true;
            }
            if (EntityManager.HasBuffer<AggregateElement>(e))
            {
                DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(e, true);
                if (edges.Length == 0) return false;
                e = edges[edges.Length / 2].m_Edge;
            }
            if (EntityManager.HasComponent<Game.Net.Curve>(e))
            {
                position = Colossal.Mathematics.MathUtils.Position(EntityManager.GetComponentData<Game.Net.Curve>(e).m_Bezier, 0.5f);
                return true;
            }
            if (EntityManager.HasComponent<Game.Objects.Transform>(e))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(e).m_Position;
                return true;
            }
            if (EntityManager.HasComponent<Game.Areas.Geometry>(e))
            {
                position = EntityManager.GetComponentData<Game.Areas.Geometry>(e).m_CenterPosition;
                return true;
            }
            return false;
        }

        private void Show(Entity target)
        {
            MoveCameraTo(target);
        }

        /// <summary>
        /// The road segment of a street with this name nearest to a point (the queue's centre), or for a junction the
        /// nearest junction of 3+ roads. Null when nothing fits within 1.5 km.
        /// </summary>
        private Entity FindNear(string place, Unity.Mathematics.float2 at)
        {
            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            Entity best = Entity.Null;
            float bestDist = 1500f;
            EntityQuery streets = GetEntityQuery(ComponentType.ReadOnly<Aggregate>(), ComponentType.ReadOnly<AggregateElement>(), ComponentType.Exclude<Game.Common.Deleted>());
            using (NativeArray<Entity> list = streets.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity street in list)
                {
                    if (TrafficReportSystem.CleanName(names.GetRenderedLabelName(street)) != place) continue;
                    DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(street, true);
                    for (int i = 0; i < edges.Length; i++)
                    {
                        if (!EntityManager.HasComponent<Game.Net.Curve>(edges[i].m_Edge)) continue;
                        Unity.Mathematics.float3 mid = Colossal.Mathematics.MathUtils.Position(EntityManager.GetComponentData<Game.Net.Curve>(edges[i].m_Edge).m_Bezier, 0.5f);
                        float d = Unity.Mathematics.math.distance(mid.xz, at);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = edges[i].m_Edge;
                        }
                    }
                }
            }
            if (best != Entity.Null) return best;

            // A junction ("A / B"): the nearest road node where three or more roads meet.
            bestDist = 300f;
            EntityQuery nodes = GetEntityQuery(ComponentType.ReadOnly<Game.Net.Node>(), ComponentType.ReadOnly<Game.Net.ConnectedEdge>(), ComponentType.Exclude<Game.Common.Deleted>());
            using (NativeArray<Entity> list = nodes.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity node in list)
                {
                    if (EntityManager.GetBuffer<Game.Net.ConnectedEdge>(node, true).Length < 3) continue;
                    float d = Unity.Mathematics.math.distance(EntityManager.GetComponentData<Game.Net.Node>(node).m_Position.xz, at);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = node;
                    }
                }
            }
            return best;
        }

        /// <summary>
        /// For hotspots without a recorded position: look the street up by its name. When several streets share the
        /// name, the one with the most queued vehicles this game hour is taken (the jammed one).
        /// </summary>
        private Entity FindStreetByName(string place)
        {
            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            TrafficReportSystem report = World.GetOrCreateSystemManaged<TrafficReportSystem>();
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<Aggregate>(), ComponentType.Exclude<Game.Common.Deleted>());
            Entity best = Entity.Null;
            float bestQueued = -1f;
            using (NativeArray<Entity> streets = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity street in streets)
                {
                    if (TrafficReportSystem.CleanName(names.GetRenderedLabelName(street)) != place) continue;
                    float queued = report.QueuedThisHour(street);
                    if (queued > bestQueued)
                    {
                        bestQueued = queued;
                        best = street;
                    }
                }
            }
            return best;
        }

        private string RoadworksJson()
        {
            if (m_MarksVersion != RoadMarkSystem.Version)
            {
                m_MarksVersion = RoadMarkSystem.Version;
                m_RoadworksJson = m_RoadMarkSystem.ToJson();
            }
            return m_RoadworksJson;
        }

        private void Refresh()
        {
            if (m_Version == TrafficReportStore.Version) return;
            m_Version = TrafficReportStore.Version;
            m_CurrentJson = TrafficReportStore.Current.ToJson();
            // The heavy parts only when recorded data changed, not on every "right now" update.
            if (m_DataVersion == TrafficReportStore.DataVersion) return;
            m_DataVersion = TrafficReportStore.DataVersion;
            m_HistoryJson = TrafficReportStore.HistoryJson();
            m_RunsJson = TrafficReportStore.RunsJson();
            m_HotspotsJson = TrafficReportStore.HotspotsJson(25);
        }
    }
}
