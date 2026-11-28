using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Curve = Game.Net.Curve;
using NetNode = Game.Net.Node;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Draws the selected bottleneck or possible link from the Causes tab on the map, until the player hides it:
    /// the lanes the queue stands on (red, wider = more cars), the turns the queued cars want through the junction
    /// (coloured by direction, wider = more of the queue; dashed = allowed but not used), and for a link the detour
    /// route (orange) with the suggested connection (dashed green).
    ///
    /// Colours and widths come from ModsData/LiveTrafficRouting/overlay-style.txt, re-read while the game runs, so the
    /// look can be tuned without a restart. Each time a view is shown, its shapes are written to overlay-preview.json
    /// (for a top-down preview outside the game). Nothing is saved with the city.
    /// </summary>
    public partial class BottleneckOverlaySystem : GameSystemBase
    {
        private static OverlayView s_View;
        private static string s_ViewId = "";
        private static bool s_ExportPending;

        internal static string ShownId => s_ViewId;

        internal static void Show(string id, OverlayView view)
        {
            s_View = view;
            s_ViewId = view != null ? id : "";
            s_ExportPending = view != null;
            Logger.Info(view != null ? $"Map overlay: {view.Title} ({id})." : "Map overlay hidden.");
        }

        internal static void Hide() => Show("", null);

        // ---------------- Style (hot reload) ----------------

        private sealed class Style
        {
            public Color Queue = new Color(0.95f, 0.2f, 0.15f, 0.85f);
            public Color Left = new Color(0.25f, 0.6f, 1f, 0.9f);
            public Color Straight = new Color(0.95f, 0.95f, 0.95f, 0.9f);
            public Color Right = new Color(0.75f, 0.4f, 1f, 0.9f);
            public Color UTurn = new Color(1f, 0.85f, 0.2f, 0.9f);
            public Color Unused = new Color(0.7f, 0.7f, 0.7f, 0.5f);
            public Color Junction = new Color(1f, 0.85f, 0.2f, 0.9f);
            public Color Route = new Color(1f, 0.55f, 0.1f, 0.85f);
            public Color Link = new Color(0.3f, 0.95f, 0.4f, 0.95f);
            public Color Marker = new Color(1f, 0.85f, 0.2f, 0.95f);
            // Where the queued drivers come from (cyan) and go to (pink): arcs to and from the bottleneck.
            public Color From = new Color(0.3f, 0.85f, 1f, 0.75f);
            public Color To = new Color(1f, 0.45f, 0.85f, 0.75f);
            public float TripMin = 0.8f, TripMax = 2f;
            // Direction arrows for where drivers come from / go to: from this far out to this close to the junction.
            public float StubFar = 170f, StubNear = 45f;
            // Dark edge around every line, so lines that cross or overlap stay apart.
            public Color OutlineColor = new Color(0.05f, 0.05f, 0.05f, 0.55f);
            public float OutlineShare = 0.3f;
            // Widths are for a camera about zoom_reference metres away; closer = thinner, further = thicker.
            public float ZoomReference = 400f, ZoomMin = 0.12f, ZoomMax = 12f;
            public float QueueWidth = 2f;
            public float ChevronSpacing = 120f;
            // Lanes are about 3.5 m apart: wider lines would merge neighbouring lanes.
            public float QueueMin = 1f, QueueMax = 3f;
            public float TurnMin = 0.6f, TurnMax = 1.8f;
            public float UnusedWidth = 0.6f;
            public float ArrowLength = 2.5f;
            public float JunctionSize = 18f, JunctionOutline = 0.8f;
            public float RouteWidth = 2.2f, LinkWidth = 2.5f, EndSize = 8f;
            public float Lift = 0.5f;
        }

        private static Style s_Style = new Style();
        private static DateTime s_StyleTime;
        private double m_NextStyleCheck;
        private static string StylePath => Path.Combine(TrafficReportStore.Directory_, "overlay-style.txt");
        private static string PreviewPath => Path.Combine(TrafficReportStore.Directory_, "overlay-preview.json");

        private static readonly string[] kStyleHelp =
        {
            "# Live Traffic Routing: map overlay style. Edit and save while the game runs; it is re-read within a second.",
            "# Colours: r, g, b, a (0-1). Widths and sizes: metres.",
        };

        // Bump when the defaults change a lot: an older file is set aside (overlay-style.old.txt) and rewritten.
        private const int kStyleVersion = 7;

        private static void WriteDefaultStyle()
        {
            Style d = new Style();
            var lines = new List<string>(kStyleHelp)
            {
                "style_version = " + kStyleVersion,
                "queue = " + C(d.Queue), "left = " + C(d.Left), "straight = " + C(d.Straight), "right = " + C(d.Right),
                "uturn = " + C(d.UTurn), "unused = " + C(d.Unused), "junction = " + C(d.Junction), "route = " + C(d.Route),
                "link = " + C(d.Link), "marker = " + C(d.Marker), "from = " + C(d.From), "to = " + C(d.To),
                "trip_min = " + F(d.TripMin), "trip_max = " + F(d.TripMax), "stub_far = " + F(d.StubFar), "stub_near = " + F(d.StubNear), "outline_color = " + C(d.OutlineColor),
                "outline_share = " + F(d.OutlineShare), "zoom_reference = " + F(d.ZoomReference), "zoom_min = " + F(d.ZoomMin),
                "zoom_max = " + F(d.ZoomMax), "queue_width = " + F(d.QueueWidth), "chevron_spacing = " + F(d.ChevronSpacing),
                "queue_min = " + F(d.QueueMin), "queue_max = " + F(d.QueueMax), "turn_min = " + F(d.TurnMin), "turn_max = " + F(d.TurnMax),
                "unused_width = " + F(d.UnusedWidth), "arrow_length = " + F(d.ArrowLength), "junction_size = " + F(d.JunctionSize),
                "junction_outline = " + F(d.JunctionOutline), "route_width = " + F(d.RouteWidth),
                "link_width = " + F(d.LinkWidth), "end_size = " + F(d.EndSize), "lift = " + F(d.Lift),
            };
            Directory.CreateDirectory(TrafficReportStore.Directory_);
            File.WriteAllLines(StylePath, lines);
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static string C(Color c) => string.Join(", ", F(c.r), F(c.g), F(c.b), F(c.a));

        private static void ReloadStyle()
        {
            try
            {
                if (!File.Exists(StylePath)) WriteDefaultStyle();
                else if (!File.ReadAllText(StylePath).Contains("style_version = " + kStyleVersion))
                {
                    string old = Path.Combine(TrafficReportStore.Directory_, "overlay-style.old.txt");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(StylePath, old);
                    WriteDefaultStyle();
                    Logger.Info("Map overlay: new default style written (the previous file is overlay-style.old.txt).");
                }
                DateTime time = File.GetLastWriteTimeUtc(StylePath);
                if (time == s_StyleTime) return;
                s_StyleTime = time;
                var st = new Style();
                foreach (string raw in File.ReadAllLines(StylePath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string[] v = line.Substring(eq + 1).Split(',');
                    float[] n = new float[v.Length];
                    for (int i = 0; i < v.Length; i++) float.TryParse(v[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]);
                    Color col = n.Length >= 4 ? new Color(n[0], n[1], n[2], n[3]) : Color.white;
                    switch (key)
                    {
                        case "queue": st.Queue = col; break;
                        case "left": st.Left = col; break;
                        case "straight": st.Straight = col; break;
                        case "right": st.Right = col; break;
                        case "uturn": st.UTurn = col; break;
                        case "unused": st.Unused = col; break;
                        case "junction": st.Junction = col; break;
                        case "route": st.Route = col; break;
                        case "link": st.Link = col; break;
                        case "marker": st.Marker = col; break;
                        case "from": st.From = col; break;
                        case "to": st.To = col; break;
                        case "trip_min": st.TripMin = n[0]; break;
                        case "trip_max": st.TripMax = n[0]; break;
                        case "stub_far": st.StubFar = n[0]; break;
                        case "stub_near": st.StubNear = n[0]; break;
                        case "outline_color": st.OutlineColor = col; break;
                        case "outline_share": st.OutlineShare = n[0]; break;
                        case "zoom_reference": st.ZoomReference = n[0]; break;
                        case "zoom_min": st.ZoomMin = n[0]; break;
                        case "zoom_max": st.ZoomMax = n[0]; break;
                        case "queue_width": st.QueueWidth = n[0]; break;
                        case "chevron_spacing": st.ChevronSpacing = n[0]; break;
                        case "queue_min": st.QueueMin = n[0]; break;
                        case "queue_max": st.QueueMax = n[0]; break;
                        case "turn_min": st.TurnMin = n[0]; break;
                        case "turn_max": st.TurnMax = n[0]; break;
                        case "unused_width": st.UnusedWidth = n[0]; break;
                        case "junction_size": st.JunctionSize = n[0]; break;
                        case "junction_outline": st.JunctionOutline = n[0]; break;
                        case "arrow_length": st.ArrowLength = n[0]; break;
                        case "route_width": st.RouteWidth = n[0]; break;
                        case "link_width": st.LinkWidth = n[0]; break;
                        case "end_size": st.EndSize = n[0]; break;
                        case "lift": st.Lift = n[0]; break;
                    }
                }
                s_Style = st;
                s_ExportPending = s_View != null;
                Logger.Info("Map overlay style loaded.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Map overlay: could not read {StylePath}: {e.Message}");
                s_StyleTime = DateTime.UtcNow;
            }
        }

        // ---------------- Drawing ----------------

        private OverlayRenderSystem m_OverlayRenderSystem;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            s_View = null;
            s_ViewId = "";
        }

        private struct Shape
        {
            public Bezier4x3 Curve;
            public Color Color;
            public float Width;
            public bool Dashed;
            public bool Circle;
            public float Outline; // circle drawn as a ring of this width (0 = filled)
            public bool Plain;    // no dark edge (queue lanes: side by side, an edge would show seams between road pieces)
            public bool Soft;     // a long trip arc or its arrowhead (no longer drawn differently)
            public bool WorldSize; // circle: Width is its real diameter in metres (an area), not scaled with the zoom
        }

        private readonly List<Shape> m_Shapes = new List<Shape>();
        private OverlayView m_BuiltView;
        private Style m_BuiltStyle;
        private double m_NextRebuild;

        protected override void OnUpdate()
        {
            OverlayView view = s_View;
            if (view == null) return;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now >= m_NextStyleCheck)
            {
                m_NextStyleCheck = now + 1.0;
                ReloadStyle();
            }

            Style st = s_Style;
            float zoom = ZoomFactor(view, st);
            // Rebuild the shapes only when something changed (the view, the style, the zoom by 3%+) or every half second
            // (roads can change while it is shown): building reads many components on the main thread.
            if (view != m_BuiltView || st != m_BuiltStyle || math.abs(zoom - m_Zoom) > m_Zoom * 0.03f || now >= m_NextRebuild)
            {
                m_Zoom = zoom;
                BuildShapes(view, st);
                m_BuiltView = view;
                m_BuiltStyle = st;
                m_NextRebuild = now + 0.5;
            }
            zoom = m_Zoom;
            OverlayRenderSystem.Buffer buffer = m_OverlayRenderSystem.GetBuffer(out JobHandle deps);
            deps.Complete();
            var round = new float2(1f, 1f);
            // Solid outlined lines in two passes: every dark outline first, then every fill on top. Drawn one by one,
            // each piece's outlined end lay on the previous piece's fill: dark dots at every joint of a route, and
            // outlines cutting across overlapping turn arrows.
            foreach (Shape sh in m_Shapes)
            {
                if (sh.Circle || sh.Dashed || sh.Plain) continue;
                buffer.DrawCurve(st.OutlineColor, sh.Curve, sh.Width * zoom, round);
            }
            foreach (Shape sh in m_Shapes)
            {
                // Zoomed out, every line grows with the camera distance so it keeps the same size on screen (sqrt made the
                // arcs fade away from far out).
                float w = sh.Width * zoom;
                if (sh.Circle && sh.WorldSize)
                {
                    Color fill = sh.Color;
                    fill.a *= 0.08f;
                    buffer.DrawCircle(sh.Color, fill, sh.Outline * zoom, 0, new float2(0f, 1f), sh.Curve.a, sh.Width);
                }
                else if (sh.Circle && sh.Outline > 0f)
                {
                    Color fill = sh.Color;
                    fill.a = 0f;
                    buffer.DrawCircle(sh.Color, fill, sh.Outline * zoom, 0, new float2(0f, 1f), sh.Curve.a, sh.Width * math.max(1f, zoom * 0.6f));
                }
                else if (sh.Circle) buffer.DrawCircle(st.OutlineColor, sh.Color, w * st.OutlineShare, 0, new float2(0f, 1f), sh.Curve.a, w);
                else if (sh.Dashed) buffer.DrawDashedCurve(st.OutlineColor, sh.Color, w * st.OutlineShare, 0, sh.Curve, w, w * 3f, w * 2f);
                else if (sh.Plain) buffer.DrawCurve(sh.Color, sh.Curve, w, round);
                // Same look as before: in game the outline showed as about outline_share of the width, split over both sides.
                else buffer.DrawCurve(sh.Color, sh.Curve, w * (1f - st.OutlineShare), round);
            }
            m_OverlayRenderSystem.AddBufferWriter(default);

            if (s_ExportPending)
            {
                s_ExportPending = false;
                Export(view);
            }
        }

        /// <summary>Line widths follow the camera: thinner up close (lanes stay apart), thicker far away (still visible).</summary>
        private float ZoomFactor(OverlayView view, Style st)
        {
            Camera cam = Camera.main;
            if (cam == null || st.ZoomReference <= 0f) return 1f;
            float3 focus = view.Position;
            if (view.IsLink && TryNode(view.LinkA, out float3 a) && TryNode(view.LinkB, out float3 b)) focus = (a + b) * 0.5f;
            float dist = math.distance((float3)cam.transform.position, focus);
            return math.clamp(dist / st.ZoomReference, st.ZoomMin, st.ZoomMax);
        }

        private float m_Zoom = 1f;

        private void BuildShapes(OverlayView view, Style st)
        {
            m_Shapes.Clear();
            float3 up = new float3(0f, st.Lift, 0f);
            if (view.IsLink)
            {
                // The detour drivers take now, along the lane curves, with small arrows showing the way round.
                if (view.RouteLanes != null && view.RouteLanes.Count > 1)
                {
                    float sinceArrow = 0f;
                    for (int i = 1; i < view.RouteLanes.Count; i++)
                    {
                        if (!TryCurve(view.RouteLanes[i], out Bezier4x3 c)) continue;
                        Lift(ref c, up);
                        m_Shapes.Add(new Shape { Curve = c, Color = st.Route, Width = st.RouteWidth });
                        sinceArrow += EntityManager.GetComponentData<Curve>(view.RouteLanes[i]).m_Length;
                        if (sinceArrow >= st.ChevronSpacing)
                        {
                            sinceArrow = 0f;
                            AddArrowHead(c, st.Route, st.RouteWidth * 0.8f, st.ArrowLength * 1.5f);
                        }
                    }
                }
                else if (view.Route != null)
                {
                    for (int i = 0; i + 1 < view.Route.Count; i++) AddLine(view.Route[i] + up, view.Route[i + 1] + up, st.Route, st.RouteWidth, false);
                }

                if (view.LinkKind != BottleneckSystem.kNewLink)
                {
                    // A turn problem: ring the junction that blocks the way, and a dashed green arrow from there to where
                    // the drivers wanted to go (green = the way they want, orange = the way they have to drive).
                    if (TryNode(view.FixAt, out float3 at))
                    {
                        m_Shapes.Add(new Shape { Curve = new Bezier4x3(at + up, at + up, at + up, at + up), Color = st.Marker, Width = st.JunctionSize, Circle = true, Outline = st.JunctionOutline });
                        if (view.LinkKind == BottleneckSystem.kMissingTurn && TryNode(view.LinkEnd, out float3 want) && math.distance(at.xz, want.xz) > 20f)
                        {
                            Bezier4x3 wanted = NetUtilsLine(at + up * 2f, want + up * 2f);
                            m_Shapes.Add(new Shape { Curve = wanted, Color = st.Link, Width = st.LinkWidth, Dashed = true });
                            AddArrowHead(wanted, st.Link, st.LinkWidth, st.ArrowLength * 1.5f);
                        }
                    }
                    return;
                }
                if (TryNode(view.LinkA, out float3 la) && TryNode(view.LinkB, out float3 lb))
                {
                    AddLine(la + up * 2f, lb + up * 2f, st.Link, st.LinkWidth, true);
                    AddCircle(la + up * 2f, st.Link, st.EndSize);
                    AddCircle(lb + up * 2f, st.Link, st.EndSize);
                }
                return;
            }

            // Ring the junction: the place itself, or the junction a street queue runs into.
            Entity ring = EntityManager.Exists(view.Place) && EntityManager.HasComponent<NetNode>(view.Place) ? view.Place : view.Junction;
            // The ring goes where the queue enters the junction (the start of the turns it makes), not the junction's
            // centre point: on a wide junction that can be a lane or more away from where the problem is.
            bool hasRing = TryNode(ring, out float3 nodeAt);
            float3 centre = hasRing ? QueueEntry(view, nodeAt) : view.Position;
            if (hasRing)
            {
                m_Shapes.Add(new Shape { Curve = new Bezier4x3(centre + up, centre + up, centre + up, centre + up),
                    Color = st.Junction, Width = st.JunctionSize, Circle = true, Outline = st.JunctionOutline });
            }
            // Where the queued drivers come from (cyan) and go to (pink): long arcs between those places and the junction.
            // They stop at the edge of the ring (not through its middle, where the turns are drawn).
            // End right at the yellow ring (drawn bigger when zoomed out, so the end moves with it).
            float ringRadius = st.JunctionSize * 0.5f * math.max(1f, m_Zoom * 0.6f) + 2f * math.sqrt(m_Zoom);
            // Each area as a faint circle of its real size; the arc runs between the circle's edge and the ring.
            foreach (AreaPoint o in view.Origins)
            {
                float3 at = AreaPointAt(o, centre);
                AddAreaCircle(at, o, st.From, up);
                AddTripArc(o.Outside ? at : Edge(at, centre, o.Radius), Edge(centre, at, ringRadius), st.From, math.lerp(st.TripMin, st.TripMax, o.Share), st, up, atRing: 1);
            }
            foreach (AreaPoint d in view.Destinations)
            {
                float3 at = AreaPointAt(d, centre);
                AddAreaCircle(at, d, st.To, up);
                AddTripArc(Edge(centre, at, ringRadius), d.Outside ? at : Edge(at, centre, d.Radius), st.To, math.lerp(st.TripMin, st.TripMax, d.Share), st, up, atRing: 0);
            }
            foreach (KeyValuePair<Entity, float> q in view.QueueLanes)
            {
                if (!TryCurve(q.Key, out Bezier4x3 c)) continue;
                // Same width everywhere (no steps between road pieces); how busy a lane is shows in how strong the red is.
                Color col = st.Queue;
                col.a *= 0.3f + 0.7f * q.Value;
                Lift(ref c, up);
                m_Shapes.Add(new Shape { Curve = c, Color = col, Width = st.QueueWidth, Plain = true });
            }
            AddQueueArrows(view, st, up, centre);
            foreach (TurnView t in view.Turns)
            {
                // Only turns the queue uses (unused ones made busy junctions hard to read), at the ringed junction.
                if (t.Share <= 0.02f || !TryCurve(t.Lane, out Bezier4x3 c)) continue;
                if (TryNode(ring, out float3 rp) && math.distance(c.a.xz, rp.xz) > 120f) continue;
                Lift(ref c, up * 2f);
                Color col = t.Move == LaneDiagnosis.kLeft ? st.Left : t.Move == LaneDiagnosis.kRight ? st.Right : t.Move == LaneDiagnosis.kUTurn ? st.UTurn : st.Straight;
                float w = math.lerp(st.TurnMin, st.TurnMax, math.saturate(t.Share));
                m_Shapes.Add(new Shape { Curve = c, Color = col, Width = w });
                AddArrowHead(c, col, w, st.ArrowLength);
            }
        }

        /// <summary>
        /// Where the queue enters the junction: the start of the turn lanes the queued cars use (weighted by how many use
        /// each), on the ground. The junction's centre point when there are none near it.
        /// </summary>
        private float3 QueueEntry(OverlayView view, float3 node)
        {
            float3 sum = float3.zero;
            float weight = 0f;
            foreach (TurnView t in view.Turns)
            {
                if (t.Share <= 0.02f || !TryCurve(t.Lane, out Bezier4x3 c) || math.distance(c.a.xz, node.xz) > 120f) continue;
                sum += c.a * t.Share;
                weight += t.Share;
            }
            return weight > 0f ? sum / weight : node;
        }

        /// <summary>
        /// Arrows showing which way the queue drives (so "turn right" can be checked on the map): on one lane per road
        /// piece (the busiest), every ChevronSpacing metres of distance from the front of the queue (more apart when zoomed
        /// out, so the same few show on screen), plus one at the front. One per lane piece made a red fringe far out.
        /// </summary>
        private void AddQueueArrows(OverlayView view, Style st, float3 up, float3 head)
        {
            var best = new Dictionary<Entity, KeyValuePair<Entity, float>>();
            foreach (KeyValuePair<Entity, float> q in view.QueueLanes)
            {
                if (!EntityManager.HasComponent<Game.Common.Owner>(q.Key)) continue;
                Entity piece = EntityManager.GetComponentData<Game.Common.Owner>(q.Key).m_Owner;
                if (!best.TryGetValue(piece, out var b) || q.Value > b.Value) best[piece] = q;
            }
            Color arrow = st.Queue;
            arrow.a = math.max(arrow.a, 0.9f);
            float len = st.ArrowLength * m_Zoom;
            float spacing = st.ChevronSpacing * math.max(1f, m_Zoom);
            int drawn = 0;
            Bezier4x3 front = default;
            float frontDist = float.MaxValue;
            foreach (var b in best.Values)
            {
                if (!TryCurve(b.Key, out Bezier4x3 c)) continue;
                Lift(ref c, up);
                float dA = math.distance(c.a.xz, head.xz), dD = math.distance(c.d.xz, head.xz);
                if (dD < frontDist)
                {
                    frontDist = dD;
                    front = c;
                }
                // The multiples of the spacing that fall on this piece: an arrow at each.
                int k0 = (int)math.ceil(math.min(dA, dD) / spacing), k1 = (int)math.floor(math.max(dA, dD) / spacing);
                for (int k = math.max(1, k0); k <= k1 && drawn < 24; k++)
                {
                    float target = k * spacing, bestT = 1f, bestErr = float.MaxValue;
                    for (int i = 1; i <= 16; i++)
                    {
                        float t = i / 16f;
                        float err = math.abs(math.distance(Colossal.Mathematics.MathUtils.Position(c, t).xz, head.xz) - target);
                        if (err < bestErr)
                        {
                            bestErr = err;
                            bestT = t;
                        }
                    }
                    AddArrowHead(Colossal.Mathematics.MathUtils.Cut(c, new float2(0f, bestT)), arrow, st.QueueWidth * 0.8f, len);
                    drawn++;
                }
            }
            if (frontDist < float.MaxValue) AddArrowHead(front, arrow, st.QueueWidth * 0.8f, len);
        }

        /// <summary>An area's centre at the junction's height (areas are recorded without a reliable height).</summary>
        private static float3 AreaPointAt(AreaPoint a, float3 centre)
        {
            float3 p = a.Position;
            p.y = centre.y;
            return p;
        }

        private void AddAreaCircle(float3 at, AreaPoint a, Color color, float3 up)
        {
            if (a.Outside || a.Radius <= 0f) return;
            Color c = color;
            c.a *= 0.6f;
            float3 p = at + up;
            m_Shapes.Add(new Shape { Curve = new Bezier4x3(p, p, p, p), Color = c, Width = a.Radius * 2f, Circle = true, Outline = 1.2f, WorldSize = true });
        }

        /// <summary>The point at this distance from the centre towards another point (the edge of the junction).</summary>
        private static float3 Edge(float3 centre, float3 toward, float distance)
        {
            float2 d = toward.xz - centre.xz;
            float len = math.length(d);
            return len < distance * 2f ? centre : centre + new float3(d.x / len * distance, 0f, d.y / len * distance);
        }

        private void AddDirectionStub(float3 centre, float3 toward, bool inward, Color color, float width, Style st, float3 up)
        {
            float2 d = toward.xz - centre.xz;
            float len = math.length(d);
            if (len < st.StubNear * 2f) return;
            float2 dir = d / len;
            float3 far = centre + new float3(dir.x, 0f, dir.y) * st.StubFar + up * 3f;
            float3 near = centre + new float3(dir.x, 0f, dir.y) * st.StubNear + up * 3f;
            Bezier4x3 c = inward ? NetUtilsLine(far, near) : NetUtilsLine(near, far);
            m_Shapes.Add(new Shape { Curve = c, Color = color, Width = width });
            AddArrowHead(c, color, width, st.ArrowLength * 2f);
        }

        /// <summary>A dashed arc from one point to another (bent to the side, so arcs to and from the same place differ).</summary>
        /// <param name="atRing">Which end is at the ring (0 = from, 1 = to): that end sits at the ring's height. Lifted
        /// higher, it looked several metres away from the ring when the camera looks at an angle.</param>
        private void AddTripArc(float3 from, float3 to, Color color, float width, Style st, float3 up, int atRing)
        {
            float2 d = to.xz - from.xz;
            float len = math.length(d);
            if (len < 60f) return;
            // Bow out a little to the side (so arcs to and from the same place differ), at most 120 m.
            float2 side = new float2(-d.y, d.x) / len * math.min(len * 0.1f, 120f);
            float3 lift = up * 3f;
            float3 fromLift = atRing == 0 ? up : lift, toLift = atRing == 1 ? up : lift;
            var c = new Bezier4x3(from + fromLift, from + new float3(d.x / 3f + side.x, 0f, d.y / 3f + side.y) + lift,
                from + new float3(d.x * 2f / 3f + side.x, 0f, d.y * 2f / 3f + side.y) + lift, to + toLift);
            m_Shapes.Add(new Shape { Curve = c, Color = color, Width = width, Dashed = true, Soft = true });
            AddArrowHead(c, color, width, st.ArrowLength * 0.8f * m_Zoom, soft: true);
        }

        /// <summary>Two short strokes at the end of a turn, pointing the way it goes.</summary>
        private void AddArrowHead(Bezier4x3 c, Color color, float width, float length, bool soft = false)
        {
            float3 tip = c.d;
            float3 back = c.d - c.c;
            if (math.lengthsq(back.xz) < 1e-4f) back = c.d - c.a;
            float2 dir = math.normalizesafe(back.xz);
            if (math.lengthsq(dir) < 0.5f) return;
            float2 side = new float2(-dir.y, dir.x);
            float len = length + width * 0.3f;
            float3 w1 = tip - new float3(dir.x - side.x * 0.6f, 0f, dir.y - side.y * 0.6f) * len;
            float3 w2 = tip - new float3(dir.x + side.x * 0.6f, 0f, dir.y + side.y * 0.6f) * len;
            m_Shapes.Add(new Shape { Curve = NetUtilsLine(tip, w1), Color = color, Width = width * 0.6f, Soft = soft });
            m_Shapes.Add(new Shape { Curve = NetUtilsLine(tip, w2), Color = color, Width = width * 0.6f, Soft = soft });
        }

        private static void Lift(ref Bezier4x3 c, float3 up)
        {
            c.a += up;
            c.b += up;
            c.c += up;
            c.d += up;
        }

        private bool TryCurve(Entity lane, out Bezier4x3 curve)
        {
            curve = default;
            if (!EntityManager.Exists(lane) || !EntityManager.HasComponent<Curve>(lane)) return false;
            curve = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
            return true;
        }

        private bool TryNode(Entity node, out float3 position)
        {
            position = default;
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<NetNode>(node)) return false;
            position = EntityManager.GetComponentData<NetNode>(node).m_Position;
            return true;
        }

        private void AddLine(float3 a, float3 b, Color color, float width, bool dashed)
        {
            m_Shapes.Add(new Shape { Curve = NetUtilsLine(a, b), Color = color, Width = width, Dashed = dashed });
        }

        private void AddCircle(float3 p, Color color, float size)
        {
            m_Shapes.Add(new Shape { Curve = new Bezier4x3(p, p, p, p), Color = color, Width = size, Circle = true });
        }

        private static Bezier4x3 NetUtilsLine(float3 a, float3 b) =>
            new Bezier4x3(a, math.lerp(a, b, 1f / 3f), math.lerp(a, b, 2f / 3f), b);

        /// <summary>The shapes on screen, for a top-down preview outside the game.</summary>
        private void Export(OverlayView view)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"title\":\"").Append(view.Title.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\",\"shapes\":[");
                for (int i = 0; i < m_Shapes.Count; i++)
                {
                    Shape s = m_Shapes[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"p\":[").Append(P(s.Curve.a)).Append(',').Append(P(s.Curve.b)).Append(',').Append(P(s.Curve.c)).Append(',').Append(P(s.Curve.d))
                      .Append("],\"color\":[").Append(C(s.Color)).Append("],\"w\":").Append(F(s.Width))
                      .Append(",\"dashed\":").Append(s.Dashed ? "true" : "false").Append(",\"circle\":").Append(s.Circle ? "true" : "false")
                      .Append(",\"outline\":").Append(F(s.Outline)).Append(",\"plain\":").Append(s.Plain ? "true" : "false").Append('}');
                }
                sb.Append("]}");
                Directory.CreateDirectory(TrafficReportStore.Directory_);
                File.WriteAllText(PreviewPath, sb.ToString());
            }
            catch (Exception e)
            {
                Logger.Warn($"Map overlay: could not write the preview file: {e.Message}");
            }
        }

        private static string P(float3 p) => "[" + F(p.x) + "," + F(p.y) + "," + F(p.z) + "]";
    }
}
