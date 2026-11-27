using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using Curve = Game.Net.Curve;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Forecast of where vehicles are about to drive. Every vehicle carries its planned route; this system counts, per
    /// lane, how many vehicles plan to drive it within the next <see cref="kHorizonSeconds"/> of drive time.
    /// <see cref="LiveTrafficFlowSystem"/> turns that planned demand into a cost (a capacity curve), so a road that
    /// many drivers are heading for looks busier before it jams, and later route requests spread over other roads.
    ///
    /// Vehicles are split into 16 groups (like the other systems); one group per run, so a full count takes 1024
    /// frames. The count is double-buffered: readers always see the last complete count of all vehicles.
    /// All state is in memory only.
    /// </summary>
    public partial class DemandForecastSystem : GameSystemBase
    {
        private const int kUpdateInterval = 64;
        private const int kGroupCount = 16;
        // Count lanes a vehicle reaches within this much drive time (simulation seconds, congested speed).
        internal const float kHorizonSeconds = 120f;
        private const int kMaxElementsAhead = 128;
        private const float kFramesPerSecond = 60f;

        // [0] vehicles counted, [1] lane visits counted (count in progress); [2] vehicles, [3] lanes (last complete count)
        private const int kStatCount = 4;

        /// <summary>Starts a new count: remembers the size of the one just published, then clears the map to fill.</summary>
        [BurstCompile]
        private struct StartCountJob : IJob
        {
            public NativeParallelHashMap<Entity, float> m_Map;
            [ReadOnly] public NativeParallelHashMap<Entity, float> m_Published;
            public NativeArray<float> m_Stats;
            public bool m_Publish;

            public void Execute()
            {
                if (m_Publish)
                {
                    m_Stats[2] = m_Stats[0];
                    m_Stats[3] = m_Published.Count();
                }
                m_Stats[0] = 0f;
                m_Stats[1] = 0f;
                m_Map.Clear();
            }
        }

        [BurstCompile]
        private struct ForecastJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<PathOwner> m_PathOwnerType;
            [ReadOnly] public ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
            [ReadOnly] public ComponentTypeHandle<Controller> m_ControllerType;
            [ReadOnly] public BufferTypeHandle<CarNavigationLane> m_NavigationLaneType;
            [ReadOnly] public BufferTypeHandle<PathElement> m_PathElementType;
            [ReadOnly] public ComponentLookup<CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;

            public NativeParallelHashMap<Entity, float> m_Building;
            public NativeArray<float> m_Stats;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<PathOwner> pathOwners = chunk.GetNativeArray(ref m_PathOwnerType);
                NativeArray<CarCurrentLane> currentLanes = chunk.GetNativeArray(ref m_CurrentLaneType);
                NativeArray<Controller> controllers = chunk.GetNativeArray(ref m_ControllerType);
                BufferAccessor<CarNavigationLane> navigationLanes = chunk.GetBufferAccessor(ref m_NavigationLaneType);
                BufferAccessor<PathElement> pathElements = chunk.GetBufferAccessor(ref m_PathElementType);

                for (int i = 0; i < entities.Length; i++)
                {
                    // Trailers follow the lead vehicle; count each vehicle once.
                    if (controllers.Length != 0 && controllers[i].m_Controller != entities[i] && controllers[i].m_Controller != Entity.Null) continue;
                    PathOwner owner = pathOwners[i];
                    if ((owner.m_State & (PathFlags.Pending | PathFlags.Obsolete | PathFlags.Failed)) != 0) continue;

                    m_Stats[0] += 1f;
                    float t = 0f;

                    // The lane it is on, then the lanes already handed to navigation, then the rest of the path.
                    CarCurrentLane current = currentLanes[i];
                    if (Count(current.m_Lane, new float2(current.m_CurvePosition.x, current.m_CurvePosition.z), ref t)) continue;

                    if (navigationLanes.Length != 0)
                    {
                        DynamicBuffer<CarNavigationLane> nav = navigationLanes[i];
                        bool done = false;
                        for (int k = 0; k < nav.Length && !done; k++)
                        {
                            done = Count(nav[k].m_Lane, nav[k].m_CurvePosition, ref t);
                        }
                        if (done) continue;
                    }

                    if (pathElements.Length == 0) continue;
                    DynamicBuffer<PathElement> path = pathElements[i];
                    int end = math.min(path.Length, owner.m_ElementIndex + kMaxElementsAhead);
                    for (int k = math.max(0, owner.m_ElementIndex); k < end; k++)
                    {
                        if (Count(path[k].m_Target, path[k].m_TargetDelta, ref t)) break;
                    }
                }
            }

            /// <summary>Adds the lane to the count and the drive time to t. True once past the horizon.</summary>
            private bool Count(Entity lane, float2 delta, ref float t)
            {
                if (lane == Entity.Null) return false;
                var element = new PathElement { m_Target = lane, m_TargetDelta = delta };
                if (!RouteEstimate.ElementSeconds(element, ref m_CarLaneData, ref m_CurveData, out float seconds, out _)) return false;
                m_Building.TryGetValue(lane, out float n);
                m_Building[lane] = n + 1f;
                m_Stats[1] += 1f;
                t += seconds;
                return t >= kHorizonSeconds;
            }
        }

        private SimulationSystem m_SimulationSystem;
        private EntityQuery m_VehicleQuery;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<PathOwner> m_PathOwnerType;
        private ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
        private ComponentTypeHandle<Controller> m_ControllerType;
        private BufferTypeHandle<CarNavigationLane> m_NavigationLaneType;
        private BufferTypeHandle<PathElement> m_PathElementType;
        private ComponentLookup<CarLane> m_CarLaneData;
        private ComponentLookup<Curve> m_CurveData;

        private NativeParallelHashMap<Entity, float> m_Building;
        private NativeParallelHashMap<Entity, float> m_Published;
        private NativeArray<float> m_Stats;
        private JobHandle m_LastJob;
        private JobHandle m_Readers;
        private bool m_Running;
        private bool m_RotationStarted;
        private int m_Publishes;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kUpdateInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_VehicleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(),
                    ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<PathOwner>(),
                    ComponentType.ReadOnly<PathElement>(),
                    ComponentType.ReadOnly<UpdateFrame>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<ParkedCar>(),
                    ComponentType.ReadOnly<Unspawned>(), ComponentType.ReadOnly<Bicycle>(),
                },
            });

            m_EntityType = GetEntityTypeHandle();
            m_PathOwnerType = GetComponentTypeHandle<PathOwner>(true);
            m_CurrentLaneType = GetComponentTypeHandle<CarCurrentLane>(true);
            m_ControllerType = GetComponentTypeHandle<Controller>(true);
            m_NavigationLaneType = GetBufferTypeHandle<CarNavigationLane>(true);
            m_PathElementType = GetBufferTypeHandle<PathElement>(true);
            m_CarLaneData = GetComponentLookup<CarLane>(true);
            m_CurveData = GetComponentLookup<Curve>(true);

            m_Building = new NativeParallelHashMap<Entity, float>(32768, Allocator.Persistent);
            m_Published = new NativeParallelHashMap<Entity, float>(32768, Allocator.Persistent);
            m_Stats = new NativeArray<float>(kStatCount, Allocator.Persistent);
            Logger.Info("DemandForecastSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Readers.Complete();
            m_Building.Dispose();
            m_Published.Dispose();
            m_Stats.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            ClearAll();
        }

        private void ClearAll()
        {
            m_LastJob.Complete();
            m_Readers.Complete();
            m_Building.Clear();
            m_Published.Clear();
            m_RotationStarted = false;
        }

        /// <summary>
        /// The last complete count: vehicles planning to drive each lane within the horizon. Keys are the lanes in the
        /// routes (master lanes for grouped lanes, the actual lanes for the next few lanes ahead). Readers must call
        /// <see cref="AddDemandReader"/> with their job handle.
        /// </summary>
        internal NativeParallelHashMap<Entity, float> GetDemand(out JobHandle dependencies)
        {
            dependencies = m_LastJob;
            return m_Published;
        }

        internal void AddDemandReader(JobHandle handle)
        {
            m_Readers = JobHandle.CombineDependencies(m_Readers, handle);
        }

        protected override void OnUpdate()
        {
            LiveTrafficSettings settings = Mod.Settings;
            float spread = 0.5f, neighbourhoods = 0.5f;
            bool active = settings == null || settings.IsActive;
            settings?.GetForecastTuning(out spread, out neighbourhoods);
            bool wanted = active && (spread > 0f || neighbourhoods > 0f);
            if (!wanted)
            {
                if (m_Running)
                {
                    ClearAll();
                    m_Running = false;
                    Logger.Info("Forecast OFF.");
                }
                return;
            }
            if (!m_Running)
            {
                m_Running = true;
                Logger.Info("Forecast ON.");
            }

            uint frame = m_SimulationSystem.frameIndex;
            uint group = (frame / kUpdateInterval) % kGroupCount;

            JobHandle dependency = JobHandle.CombineDependencies(Dependency, m_LastJob, m_Readers);
            System.Diagnostics.Stopwatch timer = null;
            if (ModTiming.ForecastRequested)
            {
                dependency.Complete();
                timer = System.Diagnostics.Stopwatch.StartNew();
            }

            if (group == 0)
            {
                bool publish = m_RotationStarted;
                if (publish && Logger.DetailOn && (++m_Publishes % 16) == 1)
                {
                    // m_Stats[0..1] hold the count that is about to be published.
                    dependency.Complete();
                    Logger.Info(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Forecast: {0:0} vehicles counted, {1:0} lane visits, {2} lanes with planned traffic (horizon {3:0} s).",
                        m_Stats[0], m_Stats[1], m_Building.Count(), kHorizonSeconds));
                }
                if (publish)
                {
                    // A full count is ready: publish it and start a new one. Readers of the old published map are part
                    // of the dependency, so the map being cleared is no longer read.
                    NativeParallelHashMap<Entity, float> done = m_Building;
                    m_Building = m_Published;
                    m_Published = done;
                    m_Readers = default;
                }
                dependency = new StartCountJob { m_Map = m_Building, m_Published = m_Published, m_Stats = m_Stats, m_Publish = publish }
                    .Schedule(dependency);
                m_RotationStarted = true;
            }
            if (!m_RotationStarted)
            {
                return;
            }

            m_EntityType.Update(this);
            m_PathOwnerType.Update(this);
            m_CurrentLaneType.Update(this);
            m_ControllerType.Update(this);
            m_NavigationLaneType.Update(this);
            m_PathElementType.Update(this);
            m_CarLaneData.Update(this);
            m_CurveData.Update(this);

            m_VehicleQuery.ResetFilter();
            m_VehicleQuery.SetSharedComponentFilter(new UpdateFrame(group));
            dependency = new ForecastJob
            {
                m_EntityType = m_EntityType,
                m_PathOwnerType = m_PathOwnerType,
                m_CurrentLaneType = m_CurrentLaneType,
                m_ControllerType = m_ControllerType,
                m_NavigationLaneType = m_NavigationLaneType,
                m_PathElementType = m_PathElementType,
                m_CarLaneData = m_CarLaneData,
                m_CurveData = m_CurveData,
                m_Building = m_Building,
                m_Stats = m_Stats,
            }.Schedule(m_VehicleQuery, dependency);

            if (timer != null)
            {
                dependency.Complete();
                ModTiming.ForecastMs = timer.Elapsed.TotalMilliseconds;
                ModTiming.ForecastRequested = false;
            }
            m_LastJob = dependency;
            Dependency = dependency;
        }

        /// <summary>Last complete count: vehicles counted and lanes with planned traffic.</summary>
        internal void GetCounts(out float vehicles, out int lanes)
        {
            m_LastJob.Complete();
            vehicles = m_Stats[2];
            lanes = (int)m_Stats[3];
        }
    }
}
