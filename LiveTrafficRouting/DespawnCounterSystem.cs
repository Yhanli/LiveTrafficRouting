using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Counts the road vehicles the game removes, and why. Vanilla deletes a car when it is stuck
    /// (<c>StuckMovingObjectSystem</c>: a loop or a chain of 100+ blocked vehicles, then <c>PathFlags.Stuck</c>) or when
    /// its route failed (<c>PathFlags.Failed</c>: no path, or the destination is gone). Other removals are normal
    /// (a delivery truck or service vehicle that finished its trip). Measured with the mod on or off, so a Native run
    /// and a Live run can be compared.
    ///
    /// Runs in Modification1, which runs every frame: a vehicle tagged <c>Deleted</c> by the end-of-frame barrier is
    /// seen exactly once there, before the clean-up destroys it. Only runs when such a vehicle exists.
    /// Bicycles are left out (the game builds them as personal cars). For stuck and failed removals it also notes the
    /// place (street or junction) the car stood on, so the panel and log can show which bottleneck makes cars vanish.
    /// </summary>
    public partial class DespawnCounterSystem : GameSystemBase
    {
        // [0] removed while stuck, [1] removed after a failed route, [2] removed otherwise (trip done)
        internal const int kCount = 3;

        [BurstCompile]
        private struct CountJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<PathOwner> m_PathOwnerType;
            [ReadOnly] public ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Aggregated> m_AggregatedData;
            public NativeArray<int> m_Counts;
            public NativeParallelHashMap<Entity, int> m_Places;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<PathOwner> owners = chunk.GetNativeArray(ref m_PathOwnerType);
                NativeArray<CarCurrentLane> lanes = chunk.GetNativeArray(ref m_CurrentLaneType);
                for (int i = 0; i < owners.Length; i++)
                {
                    PathFlags state = owners[i].m_State;
                    bool stuck = (state & PathFlags.Stuck) != 0;
                    bool failed = !stuck && (state & PathFlags.Failed) != 0;
                    if (stuck) m_Counts[0]++;
                    else if (failed) m_Counts[1]++;
                    else m_Counts[2]++;
                    if (!stuck && !failed) continue;
                    Entity place = TrafficReportSystem.PlaceOf(lanes[i].m_Lane, ref m_OwnerData, ref m_AggregatedData);
                    if (place == Entity.Null) continue;
                    m_Places.TryGetValue(place, out int n);
                    m_Places[place] = n + 1;
                }
            }
        }

        private EntityQuery m_DeletedQuery;
        private ComponentTypeHandle<PathOwner> m_PathOwnerType;
        private ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
        private ComponentLookup<Owner> m_OwnerData;
        private ComponentLookup<Aggregated> m_AggregatedData;
        private NativeArray<int> m_Counts;
        private NativeParallelHashMap<Entity, int> m_Places;
        private JobHandle m_LastJob;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_DeletedQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<PathOwner>(), ComponentType.ReadOnly<Deleted>(),
                },
                None = new[] { ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Bicycle>() },
            });
            RequireForUpdate(m_DeletedQuery);
            m_PathOwnerType = GetComponentTypeHandle<PathOwner>(true);
            m_CurrentLaneType = GetComponentTypeHandle<CarCurrentLane>(true);
            m_OwnerData = GetComponentLookup<Owner>(true);
            m_AggregatedData = GetComponentLookup<Aggregated>(true);
            m_Counts = new NativeArray<int>(kCount, Allocator.Persistent);
            m_Places = new NativeParallelHashMap<Entity, int>(256, Allocator.Persistent);
            Logger.Info("DespawnCounterSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Counts.Dispose();
            m_Places.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            m_PathOwnerType.Update(this);
            m_CurrentLaneType.Update(this);
            m_OwnerData.Update(this);
            m_AggregatedData.Update(this);
            m_LastJob = new CountJob
                {
                    m_PathOwnerType = m_PathOwnerType, m_CurrentLaneType = m_CurrentLaneType, m_OwnerData = m_OwnerData,
                    m_AggregatedData = m_AggregatedData, m_Counts = m_Counts, m_Places = m_Places,
                }
                .Schedule(m_DeletedQuery, JobHandle.CombineDependencies(Dependency, m_LastJob));
            Dependency = m_LastJob;
        }

        /// <summary>Counts since the last call (see kCount), then starts again. Main thread.</summary>
        internal void Harvest(int[] into)
        {
            m_LastJob.Complete();
            for (int i = 0; i < kCount; i++)
            {
                into[i] = m_Counts[i];
                m_Counts[i] = 0;
            }
        }

        /// <summary>Stuck and failed removals per place (street or junction) since the last call, then starts again. Main thread.</summary>
        internal Dictionary<Entity, int> HarvestPlaces()
        {
            m_LastJob.Complete();
            var places = new Dictionary<Entity, int>();
            NativeKeyValueArrays<Entity, int> kv = m_Places.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < kv.Length; i++) places[kv.Keys[i]] = kv.Values[i];
            kv.Dispose();
            m_Places.Clear();
            return places;
        }
    }
}
