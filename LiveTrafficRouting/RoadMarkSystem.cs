using System.Collections.Generic;
using System.Text;
using Colossal.Serialization.Entities;
using Game;
using Game.Net;
using Game.Pathfind;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Temporary road marks from the roadworks tool: roads the player closed for traffic, or asked drivers to avoid or
    /// prefer. Marks change route costs only (drivers avoid a closed road; it is not a barrier). They are kept in
    /// memory only: nothing goes into the save, and loading a city clears them. Every text about them says so.
    /// </summary>
    public partial class RoadMarkSystem : GameSystemBase
    {
        internal const byte kNone = 0;
        internal const byte kClosed = 1;
        internal const byte kAvoid = 2;
        internal const byte kPrefer = 3;
        // "Avoid": the road looks at least 3 times as slow. "Prefer": half of its congestion penalty is ignored.
        internal const float kAvoidFactor = 3f;
        internal const float kPreferKeep = 0.5f;

        private PathfindQueueSystem m_PathfindQueueSystem;
        private NameSystem m_NameSystem;
        private NativeParallelHashMap<Entity, byte> m_Marks;
        private JobHandle m_Readers;

        /// <summary>Changes on every edit, so the UI can rebuild its list.</summary>
        internal static int Version;
        /// <summary>Set when loading a city removed marks; the panel shows "were cleared (not saved)" once.</summary>
        internal static bool ClearedOnLoad;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 262144;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_PathfindQueueSystem = World.GetOrCreateSystemManaged<PathfindQueueSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_Marks = new NativeParallelHashMap<Entity, byte>(64, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            m_Readers.Complete();
            m_Marks.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_Readers.Complete();
            if (!m_Marks.IsEmpty)
            {
                ClearedOnLoad = true;
                Logger.Info($"Temporary roadworks: {m_Marks.Count()} road marks cleared on load (they are not saved).");
            }
            m_Marks.Clear();
            Version++;
        }

        protected override void OnUpdate()
        {
        }

        /// <summary>Marks per road (edge). Only changed on the main thread after the readers are done.</summary>
        internal NativeParallelHashMap<Entity, byte> GetMarks(out JobHandle dependencies)
        {
            dependencies = default;
            return m_Marks;
        }

        internal void AddReader(JobHandle handle)
        {
            m_Readers = JobHandle.CombineDependencies(m_Readers, handle);
        }

        internal int Count => m_Marks.IsCreated ? m_Marks.Count() : 0;

        internal byte GetMark(Entity edge)
        {
            m_Readers.Complete();
            return m_Marks.TryGetValue(edge, out byte mark) ? mark : kNone;
        }

        /// <summary>Sets (or with kNone removes) the mark of a road and applies it to the route planner right away.</summary>
        internal void SetMark(Entity edge, byte mark)
        {
            m_Readers.Complete();
            byte before = m_Marks.TryGetValue(edge, out byte b) ? b : kNone;
            if (mark == kNone) m_Marks.Remove(edge);
            else m_Marks[edge] = mark;
            Version++;
            int lanes = ApplyNow(edge, mark);
            Logger.Info($"Temporary roadworks: {MarkName(before)} -> {MarkName(mark)} on '{NameOf(edge)}' (edge {edge.Index}:{edge.Version}), " +
                $"{lanes} car lanes updated at once, {m_Marks.Count()} marks in total.");
        }

        internal static string MarkName(byte mark)
        {
            switch (mark)
            {
                case kClosed: return "closed";
                case kAvoid: return "avoid";
                case kPrefer: return "prefer";
                default: return "none";
            }
        }

        internal void ClearAll()
        {
            m_Readers.Complete();
            Logger.Info($"Temporary roadworks: all {m_Marks.Count()} marks cleared by the player.");
            m_Marks.Clear();
            Version++;
        }

        internal List<Entity> Edges()
        {
            m_Readers.Complete();
            var list = new List<Entity>();
            if (m_Marks.IsEmpty) return list;
            using (NativeArray<Entity> keys = m_Marks.GetKeyArray(Allocator.Temp))
            {
                foreach (Entity e in keys) list.Add(e);
            }
            return list;
        }

        /// <summary>
        /// The live traffic system applies marks when it next visits the road (within ~1024 frames). Closing or avoiding
        /// is applied at once so drivers react immediately; removing a mark or "prefer" waits for that visit.
        /// </summary>
        private int ApplyNow(Entity edge, byte mark)
        {
            if (mark != kClosed && mark != kAvoid) return 0;
            if (Mod.Settings != null && !Mod.Settings.IsActive) return 0;
            if (!EntityManager.HasBuffer<SubLane>(edge)) return 0;
            byte minOffset = mark == kClosed ? (byte)255 : (byte)math.clamp(256 - (int)math.round(256f / kAvoidFactor), 0, 255);
            var action = new FlowAction(Allocator.Persistent);
            DynamicBuffer<SubLane> lanes = EntityManager.GetBuffer<SubLane>(edge, true);
            var changes = new List<KeyValuePair<Entity, byte>>();
            for (int i = 0; i < lanes.Length; i++)
            {
                Entity lane = lanes[i].m_SubLane;
                if (!EntityManager.HasComponent<CarLane>(lane)) continue;
                CarLane carLane = EntityManager.GetComponentData<CarLane>(lane);
                byte offset = mark == kClosed ? minOffset : (byte)math.max((int)carLane.m_FlowOffset, (int)minOffset);
                if (offset == carLane.m_FlowOffset) continue;
                changes.Add(new KeyValuePair<Entity, byte>(lane, offset));
            }
            foreach (KeyValuePair<Entity, byte> c in changes)
            {
                CarLane carLane = EntityManager.GetComponentData<CarLane>(c.Key);
                carLane.m_FlowOffset = c.Value;
                EntityManager.SetComponentData(c.Key, carLane);
                action.m_FlowData.Enqueue(new FlowActionData { m_Owner = c.Key, m_FlowOffset = c.Value });
            }
            // The pathfind queue takes ownership of the action and disposes it.
            m_PathfindQueueSystem.Enqueue(action, default);
            return changes.Count;
        }

        /// <summary>Street name of a road segment (or "Road" when it has none).</summary>
        internal string NameOf(Entity edge)
        {
            if (EntityManager.HasComponent<Aggregated>(edge))
            {
                Entity agg = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (agg != Entity.Null)
                {
                    string n = TrafficReportSystem.CleanName(m_NameSystem.GetRenderedLabelName(agg));
                    if (!string.IsNullOrEmpty(n)) return n;
                }
            }
            return "Road";
        }

        /// <summary>The marked roads for the panel: [{id, name, mark}], plus the "cleared on load" notice.</summary>
        internal string ToJson()
        {
            var sb = new StringBuilder("{\"cleared\":");
            sb.Append(ClearedOnLoad ? "1" : "0").Append(",\"roads\":[");
            List<Entity> edges = Edges();
            bool first = true;
            foreach (Entity edge in edges)
            {
                if (!EntityManager.Exists(edge)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                TrafficCapture.Str(sb, "id", edge.Index + ":" + edge.Version);
                TrafficCapture.Str(sb, "name", NameOf(edge));
                TrafficCapture.Num(sb, "mark", m_Marks[edge], last: true);
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        internal static bool TryParseId(string id, out Entity entity)
        {
            entity = Entity.Null;
            if (string.IsNullOrEmpty(id)) return false;
            string[] parts = id.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int index) || !int.TryParse(parts[1], out int version)) return false;
            entity = new Entity { Index = index, Version = version };
            return true;
        }
    }
}
