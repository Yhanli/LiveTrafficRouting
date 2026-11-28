using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Colossal.Mathematics;
using Game.UI;
using Unity.Entities;
using Unity.Mathematics;
using Aggregated = Game.Net.Aggregated;
using AggregateElement = Game.Net.AggregateElement;
using CarLane = Game.Net.CarLane;
using CarLaneFlags = Game.Net.CarLaneFlags;
using ConnectedEdge = Game.Net.ConnectedEdge;
using Curve = Game.Net.Curve;
using EdgeLane = Game.Net.EdgeLane;
using NetLane = Game.Net.Lane;
using NetNode = Game.Net.Node;
using Owner = Game.Common.Owner;
using SubLane = Game.Net.SubLane;
using MasterLane = Game.Net.MasterLane;

namespace LiveTrafficRouting
{
    /// <summary>One turn through a junction (a junction lane) and the share of the queue that wants it.</summary>
    internal struct TurnView
    {
        public Entity Lane;
        public int Move;      // LaneDiagnosis.kLeft .. kUTurn
        public float Share;   // of the queued cars on the main approach; 0 = allowed but not used by the queue
    }

    /// <summary>What the map overlay shows for a bottleneck or a possible link. Built once per game hour.</summary>
    internal sealed class OverlayView
    {
        public string Title = "";
        public Entity Place;
        public float3 Position;
        // Road lanes the queue stands on, with their share of the queue (0..1, the busiest lane = 1).
        public List<KeyValuePair<Entity, float>> QueueLanes = new List<KeyValuePair<Entity, float>>();
        public List<TurnView> Turns = new List<TurnView>();
        public bool IsLink;
        public Entity LinkA, LinkB;
        public List<float3> Route;
        public List<Entity> RouteLanes;   // the detour's lanes, in driving order (drawn as their own curves)
        public int LinkKind;              // BottleneckSystem.kNewLink / kTurnAround / kMissingTurn
        public Entity FixAt;              // the junction where drivers leave the road (turn problems)
        public Entity LinkEnd;            // where they wanted to get to (end of the detour)
        public Entity Junction;           // junction to ring (the place, or where a street queue ends)
        // Where the queued drivers come from and go to: map position and share (0..1).
        public List<AreaPoint> Origins = new List<AreaPoint>();
        public List<AreaPoint> Destinations = new List<AreaPoint>();
    }

    /// <summary>An origin or destination area on the map: centre, share of the drivers (0..1), size; outside = map edge.</summary>
    internal struct AreaPoint
    {
        public float3 Position;
        public float Share;
        public float Radius;
        public bool Outside;
    }

    /// <summary>One area (street, district, building, junction) with its share of a bottleneck's trips.</summary>
    internal sealed class AreaShare
    {
        public string Name = "";
        public Entity Entity;
        public double Share;      // 0..1
        public bool HasPosition;
        public float3 Position;
        // Areas (v1.2): size on the map, the streets inside with their share of all drivers, outside the city.
        public float Radius;
        public bool Outside;
        public List<KeyValuePair<string, double>> Streets;
        // Naming: street names to name the area after, busiest first (empty = the name is fixed: building, district,
        // outside), and how many named places it has.
        public List<string> NameCandidates = new List<string>();
        public int PlaceCount;
    }

    /// <summary>
    /// Lanes and turns at a bottleneck junction: which way the queued cars want to go, how many lanes of their road
    /// allow that turn, and how evenly the lanes are used. Gives advice in plain words. Main thread (EntityManager).
    /// </summary>
    internal sealed class LaneDiagnosis
    {
        internal const int kLeft = 0, kStraight = 1, kRight = 2, kUTurn = 3;
        private static readonly string[] kMoveNames = { "left", "straight", "right", "U-turn" };
        // "turn left", "go straight", ... for sentences.
        private static readonly string[] kMoveVerbs = { "turn left", "go straight", "turn right", "make a U-turn" };

        public float3 Position;
        public string MovesText = "";
        public string LanesText = "";
        public string FlowText = "";
        public string Debug = "";
        public readonly List<string> Advice = new List<string>();
        public readonly List<KeyValuePair<Entity, float>> QueueLanes = new List<KeyValuePair<Entity, float>>();
        public readonly List<TurnView> Turns = new List<TurnView>();
        public Entity Junction;   // the junction diagnosed (the place, or where a street queue ends)

        internal static int MoveOf(CarLaneFlags flags)
        {
            if ((flags & (CarLaneFlags.UTurnLeft | CarLaneFlags.UTurnRight)) != 0) return kUTurn;
            if ((flags & (CarLaneFlags.TurnLeft | CarLaneFlags.GentleTurnLeft)) != 0) return kLeft;
            if ((flags & (CarLaneFlags.TurnRight | CarLaneFlags.GentleTurnRight)) != 0) return kRight;
            return kStraight;
        }

        /// <summary>
        /// Turn of a junction lane from its shape: the angle between where it starts and where it ends. Left is
        /// counter-clockwise seen from above (x east, z north). Falls back to the lane's turn flags.
        /// </summary>
        internal static int MoveOf(EntityManager em, Entity lane)
        {
            if (em.HasComponent<Curve>(lane))
            {
                Bezier4x3 c = em.GetComponentData<Curve>(lane).m_Bezier;
                float2 t0 = math.normalizesafe((c.b - c.a).xz);
                float2 t1 = math.normalizesafe((c.d - c.c).xz);
                if (math.lengthsq(t0) < 0.5f) t0 = math.normalizesafe((c.d - c.a).xz);
                if (math.lengthsq(t1) < 0.5f) t1 = math.normalizesafe((c.d - c.a).xz);
                if (math.lengthsq(t0) > 0.5f && math.lengthsq(t1) > 0.5f)
                {
                    float dot = math.dot(t0, t1);
                    float cross = t0.x * t1.y - t0.y * t1.x;
                    if (dot < -0.7f) return kUTurn;
                    if (dot > 0.85f) return kStraight;
                    return cross > 0 ? kLeft : kRight;
                }
            }
            return em.HasComponent<CarLane>(lane) ? MoveOf(em.GetComponentData<CarLane>(lane).m_Flags) : kStraight;
        }

        /// <summary>
        /// The road piece a junction lane leads onto, with how many lanes of it go the same way and how many points they
        /// end at (fewer ends = lanes merge in it), and its length.
        /// </summary>
        private static Entity ExitEdgeOf(EntityManager em, Entity turnLane, out int lanes, out int ends, out float length)
        {
            lanes = 0;
            ends = 0;
            length = 0f;
            NetLane turn = em.GetComponentData<NetLane>(turnLane);
            Entity node = em.GetComponentData<Owner>(turnLane).m_Owner;
            if (!em.HasBuffer<ConnectedEdge>(node)) return Entity.Null;
            DynamicBuffer<ConnectedEdge> edges = em.GetBuffer<ConnectedEdge>(node, true);
            for (int e = 0; e < edges.Length; e++)
            {
                Entity edge = edges[e].m_Edge;
                if (!em.HasBuffer<SubLane>(edge)) continue;
                DynamicBuffer<SubLane> sub = em.GetBuffer<SubLane>(edge, true);
                NetLane start = default;
                bool found = false;
                for (int i = 0; i < sub.Length && !found; i++)
                {
                    Entity l = sub[i].m_SubLane;
                    if (!em.HasComponent<CarLane>(l) || !em.HasComponent<EdgeLane>(l) || em.HasComponent<MasterLane>(l)) continue;
                    NetLane p = em.GetComponentData<NetLane>(l);
                    if (p.m_StartNode.Equals(turn.m_EndNode))
                    {
                        start = p;
                        found = true;
                    }
                }
                if (!found) continue;
                // Lanes of this road that leave the junction the same way: they start at this junction's end of the road.
                var endNodes = new HashSet<Game.Pathfind.PathNode>();
                for (int i = 0; i < sub.Length; i++)
                {
                    Entity l = sub[i].m_SubLane;
                    if (!em.HasComponent<CarLane>(l) || !em.HasComponent<EdgeLane>(l) || em.HasComponent<MasterLane>(l)) continue;
                    NetLane p = em.GetComponentData<NetLane>(l);
                    // Same direction: starts at the same end of the road piece as the lane the turn leads into.
                    if (!SameEnd(em, l, sub, start)) continue;
                    lanes++;
                    endNodes.Add(p.m_EndNode);
                }
                ends = endNodes.Count;
                length = em.HasComponent<Curve>(edge) ? em.GetComponentData<Curve>(edge).m_Length : 0f;
                return edge;
            }
            return Entity.Null;
        }

        /// <summary>Whether a road lane runs the same way as the lane starting at <paramref name="start"/>: its start
        /// lies near that lane's start (both begin at the same end of the road piece).</summary>
        private static bool SameEnd(EntityManager em, Entity lane, DynamicBuffer<SubLane> sub, NetLane start)
        {
            if (!em.HasComponent<Curve>(lane)) return false;
            float3 a = em.GetComponentData<Curve>(lane).m_Bezier.a;
            for (int i = 0; i < sub.Length; i++)
            {
                Entity l = sub[i].m_SubLane;
                if (!em.HasComponent<NetLane>(l) || !em.HasComponent<Curve>(l)) continue;
                if (!em.GetComponentData<NetLane>(l).m_StartNode.Equals(start.m_StartNode)) continue;
                return math.distance(a, em.GetComponentData<Curve>(l).m_Bezier.a) < 15f;
            }
            return false;
        }

        /// <summary>A real lane for a group ("master") lane: the first lane of its group; other lanes as they are.</summary>
        internal static Entity RealLane(EntityManager em, Entity lane)
        {
            if (!em.HasComponent<MasterLane>(lane) || !em.HasComponent<Owner>(lane)) return lane;
            Entity owner = em.GetComponentData<Owner>(lane).m_Owner;
            if (!em.HasBuffer<SubLane>(owner)) return lane;
            DynamicBuffer<SubLane> sub = em.GetBuffer<SubLane>(owner, true);
            int i = em.GetComponentData<MasterLane>(lane).m_MinIndex;
            return i < sub.Length ? sub[i].m_SubLane : lane;
        }

        /// <summary>How far left a road lane's end lies, seen in its driving direction (larger = further left).</summary>
        private static float LeftOffset(EntityManager em, Entity lane)
        {
            if (!em.HasComponent<Curve>(lane)) return 0f;
            Bezier4x3 c = em.GetComponentData<Curve>(lane).m_Bezier;
            float2 dir = math.normalizesafe((c.d - c.c).xz);
            if (math.lengthsq(dir) < 0.5f) dir = math.normalizesafe((c.d - c.a).xz);
            // Left is counter-clockwise seen from above (x east, z north), as in MoveOf.
            float2 left = new float2(-dir.y, dir.x);
            return math.dot(c.d.xz, left);
        }

        private static string Pct(double v) => v.ToString("0", CultureInfo.InvariantCulture) + "%";

        /// <summary>What the queue job measured about this queue, for advice that isn't about lanes.</summary>
        internal sealed class QueueFacts
        {
            public double[] CauseShares;
            public double TruckPct;
            public string TruckFrom;
            public double BoxBlock;
            public string BoxName;
        }

        private QueueFacts m_Facts;
        // Causes a specific piece of advice already covers (their general advice is left out).
        private readonly HashSet<int> m_Covered = new HashSet<int>();
        private const int kCauseOncoming = 1;

        internal static LaneDiagnosis Build(EntityManager em, NameSystem names, Entity place, Entity junctionNode, int topCause,
            Dictionary<BottleneckSystem.PlaceLaneKey, int> queueLanes, Dictionary<BottleneckSystem.PlaceLaneKey, int> moves, double samples,
            Dictionary<Entity, float> driven, QueueFacts facts = null)
        {
            var d = new LaneDiagnosis { m_Facts = facts };
            d.Position = PositionOf(em, place);

            // Where the queue stands (all lanes, also on other streets: shows how far it spills back).
            var standing = queueLanes.Where(kv => kv.Key.Place == place && em.Exists(kv.Key.Lane)).ToList();
            float most = standing.Count > 0 ? standing.Max(kv => kv.Value) : 1;
            foreach (var kv in standing) d.QueueLanes.Add(new KeyValuePair<Entity, float>(kv.Key.Lane, kv.Value / most));

            // A junction place, or the junction a street queue runs into: its lanes and turns.
            bool junction = junctionNode != Entity.Null && em.Exists(junctionNode) && em.HasComponent<NetNode>(junctionNode) && em.HasBuffer<SubLane>(junctionNode);
            if (junction)
            {
                d.Junction = junctionNode;
                d.DiagnoseJunction(em, names, junctionNode, place, standing, moves, driven);
            }
            if (!d.m_Covered.Contains(topCause)) d.AddCauseAdvice(topCause);
            if (facts != null)
            {
                // The queue reaches back into the junction before it: it blocks that junction too (how gridlock starts).
                if (facts.BoxBlock >= 2 && !string.IsNullOrEmpty(facts.BoxName))
                {
                    d.Advice.Insert(0, $"The queue reaches back into the junction at {facts.BoxName}: about {facts.BoxBlock:0} cars wait inside it, blocking traffic there too. Queues that block junctions spread and can end in gridlock (and the game removing stuck cars). Try: fix this bottleneck first, or leave more road between the two junctions.");
                }
                // Mostly trucks: slow to start and turn, so fewer get through each gap or green.
                if (facts.TruckPct >= 50)
                {
                    string from = string.IsNullOrEmpty(facts.TruckFrom) ? "" : $", most from {facts.TruckFrom}";
                    d.Advice.Add($"Most vehicles in this queue ({facts.TruckPct:0}%) are trucks{from}. Trucks start and turn slowly, so fewer get through each gap or green light. Try: a separate truck route or access road{(string.IsNullOrEmpty(facts.TruckFrom) ? "" : " for " + facts.TruckFrom)}, or give the trucks their own lane for this turn.");
                }
            }
            while (d.Advice.Count > 4) d.Advice.RemoveAt(d.Advice.Count - 1);
            return d;
        }

        private struct JLane
        {
            public Entity Lane;
            public NetLane Path;
            public int Move;
        }

        private void DiagnoseJunction(EntityManager em, NameSystem names, Entity node, Entity keyPlace,
            List<KeyValuePair<BottleneckSystem.PlaceLaneKey, int>> standing, Dictionary<BottleneckSystem.PlaceLaneKey, int> moves,
            Dictionary<Entity, float> driven)
        {
            // The junction's car lanes (turns).
            var jlanes = new List<JLane>();
            DynamicBuffer<SubLane> nodeLanes = em.GetBuffer<SubLane>(node, true);
            for (int i = 0; i < nodeLanes.Length; i++)
            {
                Entity lane = nodeLanes[i].m_SubLane;
                // Group ("master") lanes stand for several real lanes a car can change between: skip them, the real
                // (slave) lanes carry the turns.
                if (!em.HasComponent<CarLane>(lane) || em.HasComponent<EdgeLane>(lane) || !em.HasComponent<NetLane>(lane) || em.HasComponent<MasterLane>(lane)) continue;
                jlanes.Add(new JLane { Lane = lane, Path = em.GetComponentData<NetLane>(lane), Move = MoveOf(em, lane) });
            }

            // The road lanes that lead into the junction, per road.
            var approach = new Dictionary<Entity, List<Entity>>();   // road (edge) -> its lanes into the junction
            var laneEdge = new Dictionary<Entity, Entity>();         // road lane -> road
            var allowed = new Dictionary<Entity, int>();             // road lane -> bit mask of turns allowed
            if (em.HasBuffer<ConnectedEdge>(node))
            {
                DynamicBuffer<ConnectedEdge> edges = em.GetBuffer<ConnectedEdge>(node, true);
                for (int e = 0; e < edges.Length; e++)
                {
                    Entity edge = edges[e].m_Edge;
                    if (!em.HasBuffer<SubLane>(edge)) continue;
                    DynamicBuffer<SubLane> edgeLanes = em.GetBuffer<SubLane>(edge, true);
                    for (int i = 0; i < edgeLanes.Length; i++)
                    {
                        Entity lane = edgeLanes[i].m_SubLane;
                        if (!em.HasComponent<CarLane>(lane) || !em.HasComponent<EdgeLane>(lane) || !em.HasComponent<NetLane>(lane) || em.HasComponent<MasterLane>(lane)) continue;
                        NetLane path = em.GetComponentData<NetLane>(lane);
                        int mask = 0;
                        foreach (JLane j in jlanes)
                        {
                            if (j.Path.m_StartNode.Equals(path.m_EndNode)) mask |= 1 << j.Move;
                        }
                        if (mask == 0) continue;
                        allowed[lane] = mask;
                        laneEdge[lane] = edge;
                        if (!approach.TryGetValue(edge, out List<Entity> list)) approach[edge] = list = new List<Entity>();
                        list.Add(lane);
                    }
                }
            }

            // Which road lane each turn starts from, and the queue's wanted turns per road.
            var turnFrom = new Dictionary<Entity, Entity>();
            foreach (JLane j in jlanes)
            {
                foreach (KeyValuePair<Entity, int> a in allowed)
                {
                    NetLane path = em.GetComponentData<NetLane>(a.Key);
                    if (j.Path.m_StartNode.Equals(path.m_EndNode))
                    {
                        turnFrom[j.Lane] = a.Key;
                        break;
                    }
                }
            }
            var perRoad = new Dictionary<Entity, double[]>();
            int counted = 0, matched = 0;
            foreach (KeyValuePair<BottleneckSystem.PlaceLaneKey, int> kv in moves)
            {
                if (kv.Key.Place != keyPlace) continue;
                counted += kv.Value;
                // Routes may name a group lane: use its first real lane.
                Entity wantedLane = RealLane(em, kv.Key.Lane);
                if (!turnFrom.TryGetValue(wantedLane, out Entity from) || !laneEdge.TryGetValue(from, out Entity roadOf)) continue;
                matched += kv.Value;
                int move = MoveOf(em, wantedLane);
                if (!perRoad.TryGetValue(roadOf, out double[] m)) perRoad[roadOf] = m = new double[4];
                m[move] += kv.Value;
            }
            Debug = $"{jlanes.Count} turn lanes, {allowed.Count} lanes into the junction on {approach.Count} roads, {matched} of {counted} wanted turns matched";
            if (perRoad.Count == 0) return;

            // The road the queue mostly comes from.
            Entity main = perRoad.OrderByDescending(kv => kv.Value.Sum()).First().Key;
            double[] want = perRoad[main];
            double total = want.Sum();
            string road = StreetName(em, names, main);
            MovesText = "Queued cars on " + road + " want to " + string.Join(", ", Enumerable.Range(0, 4).Where(m => want[m] > 0)
                .OrderByDescending(m => want[m]).Select(m => kMoveVerbs[m] + " (" + Pct(want[m] / total * 100) + ")")) +
                " (left and right as the driver sees it; the arrows on the red queue show which way it drives).";

            List<Entity> roadLanes = approach.TryGetValue(main, out List<Entity> l) ? l : new List<Entity>();
            // Lanes as they reach the junction: road lanes that merge before it end at the same point (same path node)
            // and are one lane there. Ordered from the left as the driver sees it.
            var atJunction = roadLanes.GroupBy(lane => em.GetComponentData<NetLane>(lane).m_EndNode)
                .Select(g => g.ToList())
                .OrderByDescending(g => LeftOffset(em, g[0]))
                .ToList();
            int n = atJunction.Count;
            var laneMask = atJunction.Select(g => g.Aggregate(0, (acc, lane) => acc | allowed[lane])).ToList();
            var lanesFor = new int[4];
            foreach (int mask in laneMask)
            {
                for (int m = 0; m < 4; m++) if ((mask & (1 << m)) != 0) lanesFor[m]++;
            }
            string merged = roadLanes.Count > n ? $" ({roadLanes.Count} lanes on the road merge into {n} before it)" : "";
            // Lanes merging right before the junction: queued cars have to change lanes in the queue, at the spot where
            // they also need the right lane for their turn.
            // Turning cars that wait for a gap in oncoming traffic, in a lane that cars going straight also use: everyone
            // going straight waits behind them.
            double oncoming = m_Facts?.CauseShares != null && m_Facts.CauseShares.Length > kCauseOncoming ? m_Facts.CauseShares[kCauseOncoming] : 0;
            if (oncoming >= 0.2 && total >= 5 && want[kStraight] / total >= 0.3)
            {
                int shared = Enumerable.Range(0, n).FirstOrDefault(i => (laneMask[i] & (1 << kStraight)) != 0 &&
                    (laneMask[i] & ((1 << kLeft) | (1 << kRight))) != 0) ;
                bool hasShared = n > 0 && (laneMask[shared] & (1 << kStraight)) != 0 && (laneMask[shared] & ((1 << kLeft) | (1 << kRight))) != 0;
                if (hasShared)
                {
                    string turn = (laneMask[shared] & (1 << kLeft)) != 0 ? "turn left" : "turn right";
                    Advice.Add($"Cars that {turn} wait for a gap in oncoming traffic in lane {shared + 1}, which cars going straight also use: {Pct(want[kStraight] / total * 100)} of the queue want to go straight and wait behind them. Try: a separate {turn.Substring(5)}-turn lane (Traffic mod lane connector: make lane {shared + 1} turn only, if another lane goes straight), or traffic lights with a turn arrow.");
                    m_Covered.Add(kCauseOncoming);
                }
            }

            int mergeTop = -1;
            if (roadLanes.Count > n && total >= 5)
            {
                int top = Enumerable.Range(0, 4).OrderByDescending(m => want[m]).First();
                if (want[top] / total >= 0.5) mergeTop = top;
                int canTop = laneMask.Count(mask => (mask & (1 << top)) != 0);
                Advice.Add($"{roadLanes.Count} lanes of {road} merge into {n} right before the junction, so queued cars must change lanes while they wait" +
                    (want[top] / total >= 0.5 ? $", and only {canTop} of the {n} lanes there can {kMoveVerbs[top]} ({Pct(want[top] / total * 100)} of them want to)" : "") +
                    $". Try: keep all {roadLanes.Count} lanes up to the junction and let the extra lane {kMoveVerbs[top]} too (Traffic mod lane connector), or end the lane well before the junction.");
            }
            LanesText = n + (n == 1 ? " lane reaches" : " lanes reach") + " the junction" + merged + ". From the left as the driver sees it: " +
                string.Join("; ", Enumerable.Range(0, n).Select(i => $"lane {i + 1} can " + string.Join(" or ",
                    Enumerable.Range(0, 4).Where(m => (laneMask[i] & (1 << m)) != 0).Select(m => kMoveVerbs[m])))) + ".";

            // Turns from the main road, for the map: wanted ones by share, the others as allowed but unused.
            var mainTurns = new Dictionary<Entity, double>();
            foreach (KeyValuePair<BottleneckSystem.PlaceLaneKey, int> kv in moves)
            {
                Entity wantedLane = RealLane(em, kv.Key.Lane);
                if (kv.Key.Place == keyPlace && turnFrom.TryGetValue(wantedLane, out Entity from) && laneEdge.TryGetValue(from, out Entity r) && r == main)
                {
                    mainTurns.TryGetValue(wantedLane, out double before);
                    mainTurns[wantedLane] = before + kv.Value;
                }
            }
            foreach (JLane j in jlanes)
            {
                if (!turnFrom.TryGetValue(j.Lane, out Entity from) || !laneEdge.TryGetValue(from, out Entity r) || r != main) continue;
                mainTurns.TryGetValue(j.Lane, out double c);
                Turns.Add(new TurnView { Lane = j.Lane, Move = j.Move, Share = (float)(c / total) });
            }

            // Advice: the turn most of the queue makes runs into fewer lanes after the junction (they merge inside the
            // junction), or the road after it loses a lane soon: cars back up into the junction.
            if (total >= 5)
            {
                int top = Enumerable.Range(0, 4).OrderByDescending(m => want[m]).First();
                var topTurns = jlanes.Where(j => j.Move == top && turnFrom.TryGetValue(j.Lane, out Entity f) && laneEdge.TryGetValue(f, out Entity rd) && rd == main).ToList();
                int starts = topTurns.Select(j => j.Path.m_StartNode).Distinct().Count();
                int ends = topTurns.Select(j => j.Path.m_EndNode).Distinct().Count();
                if (want[top] / total >= 0.5 && starts > ends)
                {
                    Advice.Add($"The {starts} lanes that {kMoveVerbs[top]} go into {ends} lane{(ends > 1 ? "s" : "")} after the junction, so they merge inside it and wait there. Try: give each of them its own lane after the junction (a wider road), or let fewer lanes {kMoveVerbs[top]}.");
                }
                else if (want[top] / total >= 0.5 && topTurns.Count > 0)
                {
                    // The road the turn leads onto: does it lose a lane soon?
                    Entity exitEdge = ExitEdgeOf(em, topTurns[0].Lane, out int exitLanes, out int exitEnds, out float exitLength);
                    if (exitEdge != Entity.Null && exitEnds < exitLanes && exitLength < 200f)
                    {
                        Advice.Add($"Right after the junction, {StreetName(em, names, exitEdge)} goes from {exitLanes} to {exitEnds} lane{(exitEnds > 1 ? "s" : "")} within {exitLength:0} m, so cars that {kMoveVerbs[top]} back up into the junction. Try: keep the lanes for longer after the junction, or move the merge further away.");
                    }
                }
            }

            // How many cars got through each movement from the main road in the last game hour (measured on the
            // junction lanes; only for junctions that were already on the list an hour ago).
            var passed = new double[4];
            int measured = 0;
            foreach (JLane j in jlanes)
            {
                if (!turnFrom.TryGetValue(j.Lane, out Entity from) || !laneEdge.TryGetValue(from, out Entity r) || r != main) continue;
                // The game adds distance / lane length per car, so a car that drives the whole turn adds about 1.
                if (driven == null || !driven.TryGetValue(j.Lane, out float cars)) continue;
                passed[j.Move] += cars;
                measured++;
            }
            var perLane = new double[4];
            for (int m = 0; m < 4; m++) perLane[m] = lanesFor[m] > 0 ? passed[m] / lanesFor[m] : 0;
            if (measured > 0 && passed.Sum() >= 1)
            {
                FlowText = "Last game hour, per lane: " + string.Join(", ", Enumerable.Range(0, 4).Where(m => lanesFor[m] > 0)
                    .Select(m => $"{perLane[m]:0} cars could {kMoveVerbs[m]}")) + ".";
            }
            Debug += $", {measured} turn lanes measured";

            // Advice: turning is much slower than going straight here, and most of the queue wants to turn.
            if (measured > 0 && total >= 5 && perLane[kStraight] >= 10)
            {
                for (int m = 0; m < 4; m++)
                {
                    if (m == kStraight || want[m] / total < 0.5 || perLane[m] <= 0 || perLane[kStraight] < 1.5 * perLane[m]) continue;
                    Advice.Add($"Turning is slow here: about {perLane[m]:0} cars per lane {kMoveVerbs[m]} each game hour, against {perLane[kStraight]:0} going straight. Try: let a second lane {kMoveVerbs[m]}, make the turn wider, or give it a free-flowing slip lane.");
                }
            }

            // Advice: a turn most of the queue wants, but few lanes allow.
            if (total >= 5 && n >= 2)
            {
                for (int m = 0; m < 4; m++)
                {
                    double share = want[m] / total;
                    if (share < 0.5 || lanesFor[m] == 0 || lanesFor[m] * 2 > n || m == mergeTop) continue;
                    string verb = kMoveVerbs[m];
                    string fix = m == kStraight ? "let another lane go straight (Traffic mod lane connector), or widen the road"
                        : "let another lane " + verb + " (Traffic mod lane connector), or add a turn lane";
                    Advice.Add($"Most queued cars on {road} ({Pct(share * 100)}) want to {verb}, but only {lanesFor[m]} of {n} lanes can. Try: {fix}.");
                }

                // Advice: the turn lane is too short. Nearly everyone wants one turn, but many queued cars near the junction
                // stand in lanes that can't make it: the turners overflow into the other lanes and block them.
                int topMove = Enumerable.Range(0, 4).OrderByDescending(m => want[m]).First();
                if (want[topMove] / total >= 0.8 && topMove != mergeTop && lanesFor[topMove] < n)
                {
                    double near = 0, wrong = 0;
                    foreach (Entity lane in roadLanes)
                    {
                        double c = standing.Where(s => s.Key.Lane == lane).Sum(s => s.Value);
                        near += c;
                        if ((allowed[lane] & (1 << topMove)) == 0) wrong += c;
                    }
                    if (near >= 5 && wrong / near >= 0.3)
                    {
                        Advice.Add($"{Pct(want[topMove] / total * 100)} of the queued cars want to {kMoveVerbs[topMove]}, but {Pct(wrong / near * 100)} of those near the junction stand in lanes that can't: the {kMoveVerbs[topMove]} lane is too short or starts too late, so the others are blocked. Try: a longer {kMoveVerbs[topMove]} lane that starts earlier, or let another lane {kMoveVerbs[topMove]}.");
                    }
                }

                // Advice: one lane holds most of the queue although others allow the same turn.
                var counts = atJunction.Select(g => standing.Where(s => g.Contains(s.Key.Lane)).Sum(s => s.Value)).ToList();
                double laneTotal = counts.Sum();
                if (laneTotal >= 5)
                {
                    int busiest = counts.IndexOf(counts.Max());
                    double busiestShare = counts[busiest] / laneTotal;
                    int sameTurnLanes = laneMask.Count(mask => (mask & laneMask[busiest]) != 0);
                    if (busiestShare >= 0.7 && sameTurnLanes >= 2)
                    {
                        Advice.Add($"Most queued cars on {road} ({Pct(busiestShare * 100)}) wait in one lane, even though other lanes can make the same turn. Try: a longer road before the junction so drivers change lanes earlier, or let other lanes make the turn (Traffic mod).");
                    }
                }
            }
        }

        private void AddCauseAdvice(int cause)
        {
            switch (cause)
            {
                case 0: Advice.Add("Cars wait at traffic lights. Try: a roundabout, removing the lights, or a longer green for this road (Traffic mod)."); break;
                case 1: Advice.Add("Turning cars wait for a gap in oncoming traffic. Try: traffic lights with a turn arrow, a roundabout, or a separate turn lane."); break;
                case 2: Advice.Add("Cars wait for other cars crossing their path. Try: traffic lights, a roundabout, or an interchange for the main road."); break;
                case 3: Advice.Add("Cars wait at a give-way or stop sign. Try: give this road priority (Traffic mod), traffic lights, or a roundabout."); break;
                case 4: Advice.Add("Lanes merge or the road gets narrower here, so cars wait for a gap to change lanes. Try: keep the same number of lanes through this point, or move the merge further from junctions."); break;
                case 5: Advice.Add("Many cars cross each other at this junction. Try: a roundabout, or an interchange for the main road."); break;
                case 6: Advice.Add("Cars coming out of buildings or parking slow the road. Try: move the entrances to a side street."); break;
                case 8: Advice.Add("Cars parking or pulling out block a lane. Try: more parking lots or garages, or a road without street parking."); break;
                case 11: Advice.Add("Buses or trams stop in the traffic lane here, and the cars behind them wait. Try: a road with bus bays, or move the stop around the corner onto a quieter street."); break;
                case 12: Advice.Add("Service vehicles (garbage trucks, post vans, maintenance, hearses) stop in the traffic lane here. Try: give the buildings here access from a side street, or a road with parking lanes they can stop in."); break;
                case 13: Advice.Add("Cars from a ramp wait for a gap to merge onto the highway. Try: a longer merge (keep the ramp's lane going for longer), one more highway lane after the ramp, or move the ramps further apart."); break;
                case 9: Advice.Add("Cars stop for people on the crosswalk. Try: traffic lights (people then cross in groups), or a footbridge or tunnel on the people's direct line; if one is already there, people avoid it because it is a detour."); break;
                case 10: Advice.Add("Cars stop for people crossing where there is no crosswalk. Try: a proper crossing or traffic lights where they cross, or a median or fence that sends them to the nearest crossing or footbridge."); break;
            }
        }

        private static float3 PositionOf(EntityManager em, Entity place)
        {
            if (em.HasComponent<NetNode>(place)) return em.GetComponentData<NetNode>(place).m_Position;
            if (em.HasBuffer<AggregateElement>(place))
            {
                DynamicBuffer<AggregateElement> edges = em.GetBuffer<AggregateElement>(place, true);
                if (edges.Length > 0 && em.HasComponent<Curve>(edges[edges.Length / 2].m_Edge))
                {
                    return MathUtils.Position(em.GetComponentData<Curve>(edges[edges.Length / 2].m_Edge).m_Bezier, 0.5f);
                }
            }
            return default;
        }

        private static string StreetName(EntityManager em, NameSystem names, Entity edge)
        {
            if (em.HasComponent<Aggregated>(edge))
            {
                Entity agg = em.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (agg != Entity.Null)
                {
                    string n = TrafficReportSystem.CleanName(names.GetRenderedLabelName(agg));
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            return "this road";
        }
    }
}
