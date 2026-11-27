using System.Collections.Generic;
using Game;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using LaneFlow = Game.Net.LaneFlow;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Counts how many cars get through the turns of the bottleneck junctions: the distance cars drove on each watched
    /// junction lane (vanilla <c>LaneFlow.m_Next.y</c>, which navigation adds to and the traffic flow system resets about
    /// every 1024 frames), read every 16 frames so a reset loses little. The game adds distance / lane length per car, so the sum is the number of cars that drove through.
    /// The lanes to watch are set once per game hour by <see cref="BottleneckSystem"/> (the turns of its top junctions).
    /// Statistics panel only; reads game data, writes nothing to it.
    /// </summary>
    public partial class TurnFlowSystem : GameSystemBase
    {
        private struct Watch
        {
            public float Last;   // m_Next.y at the previous read
            public float Sum;    // cars through the lane since the last harvest (distance / lane length, summed)
        }

        [BurstCompile]
        private struct ReadJob : IJob
        {
            [ReadOnly] public ComponentLookup<LaneFlow> m_LaneFlowData;
            [ReadOnly] public NativeList<Entity> m_Lanes;
            public NativeParallelHashMap<Entity, Watch> m_Watch;

            public void Execute()
            {
                for (int i = 0; i < m_Lanes.Length; i++)
                {
                    Entity lane = m_Lanes[i];
                    if (!m_LaneFlowData.TryGetComponent(lane, out LaneFlow flow)) continue;
                    float now = flow.m_Next.y;
                    m_Watch.TryGetValue(lane, out Watch w);
                    // m_Next only grows between resets; after a reset the new value is all new driving.
                    w.Sum += now >= w.Last ? now - w.Last : now;
                    w.Last = now;
                    m_Watch[lane] = w;
                }
            }
        }

        private NativeList<Entity> m_Lanes;
        private NativeParallelHashMap<Entity, Watch> m_Watch;
        private JobHandle m_LastJob;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_Lanes = new NativeList<Entity>(256, Allocator.Persistent);
            m_Watch = new NativeParallelHashMap<Entity, Watch>(256, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Lanes.Dispose();
            m_Watch.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_Lanes.Length == 0) return;
            m_LastJob = new ReadJob
            {
                m_LaneFlowData = GetComponentLookup<LaneFlow>(true),
                m_Lanes = m_Lanes,
                m_Watch = m_Watch,
            }.Schedule(JobHandle.CombineDependencies(Dependency, m_LastJob));
            Dependency = m_LastJob;
        }

        /// <summary>Distance driven per watched lane since the last call, then starts again. Main thread.</summary>
        internal Dictionary<Entity, float> Harvest()
        {
            m_LastJob.Complete();
            var result = new Dictionary<Entity, float>();
            using (NativeKeyValueArrays<Entity, Watch> kv = m_Watch.GetKeyValueArrays(Allocator.Temp))
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    result[kv.Keys[i]] = kv.Values[i].Sum;
                    Watch w = kv.Values[i];
                    w.Sum = 0f;
                    m_Watch[kv.Keys[i]] = w;
                }
            }
            return result;
        }

        /// <summary>The junction lanes to watch from now on. Main thread.</summary>
        internal void SetLanes(IEnumerable<Entity> lanes)
        {
            m_LastJob.Complete();
            m_Lanes.Clear();
            var keep = new HashSet<Entity>();
            foreach (Entity lane in lanes)
            {
                if (m_Lanes.Length >= 400) break;
                if (keep.Add(lane)) m_Lanes.Add(lane);
            }
            using (NativeArray<Entity> keys = m_Watch.GetKeyArray(Allocator.Temp))
            {
                foreach (Entity k in keys) if (!keep.Contains(k)) m_Watch.Remove(k);
            }
        }
    }
}
