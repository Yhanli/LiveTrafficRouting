using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
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
using VehicleLaneFlags = Game.Vehicles.CarLaneFlags;
using PersonalCar = Game.Vehicles.PersonalCar;
using DeliveryTruck = Game.Vehicles.DeliveryTruck;
using Taxi = Game.Vehicles.Taxi;
using PedestrianLane = Game.Net.PedestrianLane;
using Curve = Game.Net.Curve;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Milestone B: drivers with "live navigation" re-plan mid-trip when the rest of their route has become
    /// much slower than when they planned it.
    ///
    /// Re-planning uses the game's own mechanism, the same steps <c>CarNavigationSystem</c> takes when a road is
    /// removed under a car: keep the lanes within braking distance, clear the rest of the route and mark the path
    /// obsolete. The vehicle's own AI system (<c>PersonalCarAISystem</c>, <c>DeliveryTruckAISystem</c>, <c>TaxiAISystem</c>)
    /// then requests a new path from the vehicle's position to the same destination.
    ///
    /// Covered: personal cars, delivery trucks and taxis, including through-traffic and vehicles with trailers
    /// (trailers follow the lead vehicle's lanes). Not covered: public transport (fixed lines and stops) and
    /// service vehicles (not reviewed yet).
    ///
    /// All state is in memory only; nothing is written to the save.
    /// </summary>
    public partial class RerouteSystem : GameSystemBase
    {
        // Cars are split into 16 groups by UpdateFrame; one group per run, so each car is checked every 1024 frames.
        private const int kUpdateInterval = 64;
        private const int kGroupCount = 16;

        // A vehicle re-plans at most once per ~22 game minutes. Each time a re-plan finds no better route the wait
        // doubles (up to 8x, ~3 game hours), and it resets once a re-plan finds a faster route.
        private const uint kCooldownFrames = 4096;
        private const int kMaxBackoffLevel = 3;
        // At most this many re-plans per run (~every 21 game seconds), to keep the pathfind queue light.
        private const int kMaxReroutesPerRun = 64;
        // The extra delay must be at least this much drive time (seconds at the speed limit)...
        private const float kMinExtraSeconds = 20f;
        // ...and the rest of the trip at least this long, or re-planning isn't worth it.
        private const float kMinRemainingSeconds = 30f;
        // Look at most this many path elements ahead.
        private const int kMaxElementsAhead = 256;
        // A re-planned route counts as "better" if it is estimated at least this much faster.
        private const float kBetterRouteMargin = 0.01f;
        private const uint kStaleFrames = 32768;

        private struct CarState
        {
            public uint m_PathSignature;
            // Remaining time / remaining free-flow time when we first saw this path.
            public float m_BaselineRatio;
            public uint m_LastReroute;
            public uint m_LastSeen;
            // Cooldown = kCooldownFrames << m_BackoffLevel.
            public int m_BackoffLevel;
        }

        private struct PendingCheck
        {
            public Entity m_Car;
            public float m_OldSeconds;
            public int m_Attempts;
        }

        // [0] cars checked, [1] re-routes, [2] re-routes evaluated, [3] of those found a faster route,
        // [4] sum of time saved (fraction of old remaining time, faster routes only), [5] skipped by the per-run cap,
        // [6] re-routes by trucks, [7] re-routes by taxis
        private const int kStatCount = 8;

        [BurstCompile]
        private struct CheckCarsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<PersonalCar> m_PersonalCarType;
            [ReadOnly] public ComponentTypeHandle<DeliveryTruck> m_DeliveryTruckType;
            [ReadOnly] public ComponentTypeHandle<Taxi> m_TaxiType;
            [ReadOnly] public ComponentTypeHandle<Moving> m_MovingType;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> m_PrefabRefType;
            [ReadOnly] public ComponentTypeHandle<Controller> m_ControllerType;
            [ReadOnly] public BufferTypeHandle<LayoutElement> m_LayoutType;
            public ComponentTypeHandle<PathOwner> m_PathOwnerType;
            public ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
            public BufferTypeHandle<CarNavigationLane> m_NavigationLaneType;
            public BufferTypeHandle<PathElement> m_PathElementType;

            [ReadOnly] public ComponentLookup<CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<PedestrianLane> m_PedestrianLaneData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            [ReadOnly] public ComponentLookup<CarData> m_PrefabCarData;

            public NativeParallelHashMap<Entity, CarState> m_Cars;
            public NativeList<PendingCheck> m_Pending;
            public NativeArray<float> m_Stats;
            public NativeArray<int> m_RunBudget;

            public uint m_Frame;
            public uint m_SharePercent;
            public float m_Threshold;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<PersonalCar> personalCars = chunk.GetNativeArray(ref m_PersonalCarType);
                NativeArray<DeliveryTruck> trucks = chunk.GetNativeArray(ref m_DeliveryTruckType);
                NativeArray<Taxi> taxis = chunk.GetNativeArray(ref m_TaxiType);
                // 0 = personal car, 1 = delivery truck, 2 = taxi
                int kind = personalCars.Length != 0 ? 0 : trucks.Length != 0 ? 1 : 2;
                NativeArray<Moving> movings = chunk.GetNativeArray(ref m_MovingType);
                NativeArray<PrefabRef> prefabRefs = chunk.GetNativeArray(ref m_PrefabRefType);
                NativeArray<Controller> controllers = chunk.GetNativeArray(ref m_ControllerType);
                BufferAccessor<LayoutElement> layouts = chunk.GetBufferAccessor(ref m_LayoutType);
                NativeArray<PathOwner> pathOwners = chunk.GetNativeArray(ref m_PathOwnerType);
                NativeArray<CarCurrentLane> currentLanes = chunk.GetNativeArray(ref m_CurrentLaneType);
                BufferAccessor<CarNavigationLane> navigationLanes = chunk.GetBufferAccessor(ref m_NavigationLaneType);
                BufferAccessor<PathElement> pathElements = chunk.GetBufferAccessor(ref m_PathElementType);

                for (int i = 0; i < entities.Length; i++)
                {
                    Entity car = entities[i];

                    // Only drivers who "have live navigation": a fixed share of cars, decided per car.
                    if (!NavigationGroup.Has(car, m_SharePercent)) continue;

                    if (!IsDriving(kind, personalCars, trucks, taxis, i)) continue;

                    // Only the lead vehicle has a route; trailers follow its lanes.
                    if (controllers.Length != 0 && controllers[i].m_Controller != car && controllers[i].m_Controller != Entity.Null) continue;

                    PathOwner pathOwner = pathOwners[i];
                    const PathFlags busy = PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Scheduled |
                                           PathFlags.Append | PathFlags.Updated | PathFlags.Stuck | PathFlags.Divert |
                                           PathFlags.DivertObsolete | PathFlags.CachedObsolete;
                    if ((pathOwner.m_State & busy) != 0) continue;

                    CarCurrentLane currentLane = currentLanes[i];
                    const VehicleLaneFlags special = VehicleLaneFlags.EndOfPath | VehicleLaneFlags.EndReached | VehicleLaneFlags.ParkingSpace |
                                                     VehicleLaneFlags.Obsolete | VehicleLaneFlags.Waypoint | VehicleLaneFlags.Queue |
                                                     VehicleLaneFlags.QueueReached | VehicleLaneFlags.Connection | VehicleLaneFlags.Area |
                                                     VehicleLaneFlags.ClearedForPathfind | VehicleLaneFlags.TransformTarget;
                    if ((currentLane.m_LaneFlags & special) != 0) continue;
                    if (currentLane.m_ChangeLane != Entity.Null) continue;
                    if (!m_CarLaneData.HasComponent(currentLane.m_Lane)) continue;

                    DynamicBuffer<PathElement> path = pathElements[i];
                    if (path.Length == 0 || pathOwner.m_ElementIndex >= path.Length) continue;

                    m_Stats[0] += 1f;
                    EstimateRemaining(path, pathOwner.m_ElementIndex, out float seconds, out float freeSeconds);
                    if (freeSeconds < kMinRemainingSeconds) continue;
                    float ratio = seconds / freeSeconds;

                    uint signature = PathSignature(path);
                    bool known = m_Cars.TryGetValue(car, out CarState state);
                    if (!known || state.m_PathSignature != signature)
                    {
                        // New (or first seen) route: remember how congested it looked.
                        state.m_PathSignature = signature;
                        state.m_BaselineRatio = ratio;
                        if (!known) state.m_LastReroute = m_Frame - kCooldownFrames;
                        state.m_LastSeen = m_Frame;
                        m_Cars[car] = state;
                        continue;
                    }
                    state.m_LastSeen = m_Frame;

                    bool cooledDown = m_Frame - state.m_LastReroute >= (kCooldownFrames << state.m_BackoffLevel);
                    // (a) The way ahead got much worse since the route was planned...
                    bool gotWorse = ratio >= state.m_BaselineRatio * (1f + m_Threshold) &&
                                    seconds - freeSeconds * state.m_BaselineRatio >= kMinExtraSeconds;
                    // (b) ...or the vehicle is heading through a standing jam: the way ahead is much slower than
                    // free-flowing traffic (twice the threshold), like a navigation app re-checking while you wait.
                    bool inStandingJam = ratio >= 1f + 2f * m_Threshold &&
                                         seconds - freeSeconds >= 2f * kMinExtraSeconds;
                    if (!(cooledDown && (gotWorse || inStandingJam)))
                    {
                        m_Cars[car] = state;
                        continue;
                    }
                    if (m_RunBudget[0] <= 0)
                    {
                        m_Stats[5] += 1f;
                        m_Cars[car] = state;
                        continue;
                    }
                    m_RunBudget[0]--;

                    // The same steps the game takes when a car's path becomes invalid.
                    CarData carData = m_PrefabCarData[prefabRefs[i].m_Prefab];
                    DynamicBuffer<CarNavigationLane> navLanes = navigationLanes[i];
                    VehicleUtils.ClearNavigationForPathfind(movings[i], carData, false, ref currentLane, navLanes,
                        ref m_CarLaneData, ref m_PedestrianLaneData, ref m_CurveData);
                    path.Clear();
                    pathOwner.m_ElementIndex = 0;
                    pathOwner.m_State |= PathFlags.Obsolete;
                    currentLanes[i] = currentLane;
                    pathOwners[i] = pathOwner;

                    state.m_LastReroute = m_Frame;
                    state.m_PathSignature = 0;
                    m_Cars[car] = state;
                    m_Pending.Add(new PendingCheck { m_Car = car, m_OldSeconds = seconds });
                    m_Stats[1] += 1f;
                    if (kind == 1) m_Stats[6] += 1f;
                    if (kind == 2) m_Stats[7] += 1f;
                }
            }

            /// <summary>Whether the vehicle is simply on its way somewhere (not loading, boarding, parking...).</summary>
            private static bool IsDriving(int kind, NativeArray<PersonalCar> personalCars, NativeArray<DeliveryTruck> trucks, NativeArray<Taxi> taxis, int i)
            {
                switch (kind)
                {
                    case 0:
                    {
                        // Citizens being driven somewhere, or through-traffic heading for the edge of the map.
                        PersonalCarFlags flags = personalCars[i].m_State;
                        if ((flags & (PersonalCarFlags.Boarding | PersonalCarFlags.Disembarking)) != 0) return false;
                        return (flags & (PersonalCarFlags.Transporting | PersonalCarFlags.DummyTraffic)) != 0;
                    }
                    case 1:
                    {
                        DeliveryTruckFlags flags = trucks[i].m_State;
                        return (flags & DeliveryTruckFlags.TransactionCancelled) == 0;
                    }
                    default:
                    {
                        TaxiFlags flags = taxis[i].m_State;
                        return (flags & (TaxiFlags.Boarding | TaxiFlags.Disembarking | TaxiFlags.Arriving | TaxiFlags.Disabled)) == 0;
                    }
                }
            }

            private static uint PathSignature(DynamicBuffer<PathElement> path)
            {
                // Changes whenever the car gets a new path (length and both ends).
                return math.hash(new int3(path.Length, path[0].m_Target.Index, path[path.Length - 1].m_Target.Index)) | 1u;
            }

            private void EstimateRemaining(DynamicBuffer<PathElement> path, int start, out float seconds, out float freeSeconds)
            {
                RouteEstimate.Estimate(path, start, kMaxElementsAhead, ref m_CarLaneData, ref m_CurveData, out seconds, out freeSeconds);
            }
        }

        /// <summary>Checks re-planned cars once their new path has arrived and compares the estimated times.</summary>
        [BurstCompile]
        private struct EvaluateJob : IJob
        {
            public NativeParallelHashMap<Entity, CarState> m_Cars;
            [ReadOnly] public ComponentLookup<PathOwner> m_PathOwnerData;
            [ReadOnly] public BufferLookup<PathElement> m_PathElements;
            [ReadOnly] public ComponentLookup<CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            public NativeList<PendingCheck> m_Pending;
            public NativeArray<float> m_Stats;
            public NativeArray<int> m_RunBudget;
            public int m_MaxReroutes;

            public void Execute()
            {
                m_RunBudget[0] = m_MaxReroutes;
                for (int i = m_Pending.Length - 1; i >= 0; i--)
                {
                    PendingCheck check = m_Pending[i];
                    bool done = true;
                    if (m_PathOwnerData.TryGetComponent(check.m_Car, out PathOwner owner) && m_PathElements.TryGetBuffer(check.m_Car, out DynamicBuffer<PathElement> path))
                    {
                        bool waiting = (owner.m_State & (PathFlags.Pending | PathFlags.Obsolete)) != 0 || path.Length == 0;
                        if (waiting && check.m_Attempts < 4)
                        {
                            check.m_Attempts++;
                            m_Pending[i] = check;
                            done = false;
                        }
                        else if (!waiting && (owner.m_State & PathFlags.Failed) == 0)
                        {
                            RouteEstimate.Estimate(path, owner.m_ElementIndex, kMaxElementsAhead, ref m_CarLaneData, ref m_CurveData, out float seconds, out _);
                            float saved = (check.m_OldSeconds - seconds) / math.max(1f, check.m_OldSeconds);
                            m_Stats[2] += 1f;
                            bool better = saved >= kBetterRouteMargin;
                            if (m_Cars.TryGetValue(check.m_Car, out CarState state))
                            {
                                state.m_BackoffLevel = better ? 0 : math.min(state.m_BackoffLevel + 1, kMaxBackoffLevel);
                                m_Cars[check.m_Car] = state;
                            }
                            if (better)
                            {
                                m_Stats[3] += 1f;
                                m_Stats[4] += saved;
                            }
                        }
                    }
                    if (done) m_Pending.RemoveAtSwapBack(i);
                }
            }
        }

        [BurstCompile]
        private struct PruneJob : IJob
        {
            public NativeParallelHashMap<Entity, CarState> m_Cars;
            public uint m_Frame;

            public void Execute()
            {
                NativeArray<Entity> keys = m_Cars.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (m_Frame - m_Cars[keys[i]].m_LastSeen > kStaleFrames) m_Cars.Remove(keys[i]);
                }
                keys.Dispose();
            }
        }

        private SimulationSystem m_SimulationSystem;
        private EntityQuery m_CarQuery;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<PersonalCar> m_PersonalCarType;
        private ComponentTypeHandle<DeliveryTruck> m_DeliveryTruckType;
        private ComponentTypeHandle<Taxi> m_TaxiType;
        private ComponentTypeHandle<Moving> m_MovingType;
        private ComponentTypeHandle<PrefabRef> m_PrefabRefType;
        private ComponentTypeHandle<Controller> m_ControllerType;
        private BufferTypeHandle<LayoutElement> m_LayoutType;
        private ComponentTypeHandle<PathOwner> m_PathOwnerType;
        private ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
        private BufferTypeHandle<CarNavigationLane> m_NavigationLaneType;
        private BufferTypeHandle<PathElement> m_PathElementType;
        private ComponentLookup<CarLane> m_CarLaneData;
        private ComponentLookup<PedestrianLane> m_PedestrianLaneData;
        private ComponentLookup<Curve> m_CurveData;
        private ComponentLookup<CarData> m_PrefabCarData;
        private ComponentLookup<PathOwner> m_PathOwnerData;
        private BufferLookup<PathElement> m_PathElementData;

        private NativeParallelHashMap<Entity, CarState> m_Cars;
        private NativeList<PendingCheck> m_Pending;
        private NativeArray<float> m_Stats;
        private NativeArray<int> m_RunBudget;
        private JobHandle m_LastJob;
        private uint m_NextPruneFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kUpdateInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            m_CarQuery = GetEntityQuery(new EntityQueryDesc
            {
                Any = new[]
                {
                    ComponentType.ReadOnly<PersonalCar>(),
                    ComponentType.ReadOnly<DeliveryTruck>(),
                    ComponentType.ReadOnly<Taxi>(),
                },
                All = new[]
                {
                    ComponentType.ReadOnly<Moving>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<UpdateFrame>(),
                    ComponentType.ReadWrite<PathOwner>(),
                    ComponentType.ReadWrite<CarCurrentLane>(),
                    ComponentType.ReadWrite<CarNavigationLane>(),
                    ComponentType.ReadWrite<PathElement>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<TripSource>(),
                    ComponentType.ReadOnly<OutOfControl>(), ComponentType.ReadOnly<Destroyed>(), ComponentType.ReadOnly<Unspawned>(),
                    ComponentType.ReadOnly<Bicycle>(), ComponentType.ReadOnly<ParkedCar>(),
                },
            });

            m_EntityType = GetEntityTypeHandle();
            m_PersonalCarType = GetComponentTypeHandle<PersonalCar>(true);
            m_DeliveryTruckType = GetComponentTypeHandle<DeliveryTruck>(true);
            m_TaxiType = GetComponentTypeHandle<Taxi>(true);
            m_MovingType = GetComponentTypeHandle<Moving>(true);
            m_PrefabRefType = GetComponentTypeHandle<PrefabRef>(true);
            m_ControllerType = GetComponentTypeHandle<Controller>(true);
            m_LayoutType = GetBufferTypeHandle<LayoutElement>(true);
            m_PathOwnerType = GetComponentTypeHandle<PathOwner>(false);
            m_CurrentLaneType = GetComponentTypeHandle<CarCurrentLane>(false);
            m_NavigationLaneType = GetBufferTypeHandle<CarNavigationLane>(false);
            m_PathElementType = GetBufferTypeHandle<PathElement>(false);
            m_CarLaneData = GetComponentLookup<CarLane>(true);
            m_PedestrianLaneData = GetComponentLookup<PedestrianLane>(true);
            m_CurveData = GetComponentLookup<Curve>(true);
            m_PrefabCarData = GetComponentLookup<CarData>(true);
            m_PathOwnerData = GetComponentLookup<PathOwner>(true);
            m_PathElementData = GetBufferLookup<PathElement>(true);

            m_Cars = new NativeParallelHashMap<Entity, CarState>(8192, Allocator.Persistent);
            m_Pending = new NativeList<PendingCheck>(256, Allocator.Persistent);
            m_Stats = new NativeArray<float>(kStatCount, Allocator.Persistent);
            m_RunBudget = new NativeArray<int>(1, Allocator.Persistent);

            RequireForUpdate(m_CarQuery);
            Logger.Info("RerouteSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Cars.Dispose();
            m_Pending.Dispose();
            m_Stats.Dispose();
            m_RunBudget.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LastJob.Complete();
            m_Cars.Clear();
            m_Pending.Clear();
        }

        protected override void OnUpdate()
        {
            LiveTrafficSettings settings = Mod.Settings;
            float share = 0.3f, threshold = 0.3f;
            if (settings != null)
            {
                if (!settings.IsActive) return;
                settings.GetRerouteTuning(out share, out threshold);
            }

            uint frame = m_SimulationSystem.frameIndex;

            m_EntityType.Update(this);
            m_PersonalCarType.Update(this);
            m_DeliveryTruckType.Update(this);
            m_TaxiType.Update(this);
            m_MovingType.Update(this);
            m_PrefabRefType.Update(this);
            m_ControllerType.Update(this);
            m_LayoutType.Update(this);
            m_PathOwnerType.Update(this);
            m_CurrentLaneType.Update(this);
            m_NavigationLaneType.Update(this);
            m_PathElementType.Update(this);
            m_CarLaneData.Update(this);
            m_PedestrianLaneData.Update(this);
            m_CurveData.Update(this);
            m_PrefabCarData.Update(this);
            m_PathOwnerData.Update(this);
            m_PathElementData.Update(this);

            JobHandle dependency = JobHandle.CombineDependencies(Dependency, m_LastJob);
            System.Diagnostics.Stopwatch timer = null;
            if (ModTiming.RerouteRequested)
            {
                dependency.Complete();
                timer = System.Diagnostics.Stopwatch.StartNew();
            }

            // Results of last run's re-plans first (their paths have had ~64 frames to arrive).
            dependency = new EvaluateJob
            {
                m_PathOwnerData = m_PathOwnerData,
                m_PathElements = m_PathElementData,
                m_CarLaneData = m_CarLaneData,
                m_CurveData = m_CurveData,
                m_Cars = m_Cars,
                m_Pending = m_Pending,
                m_Stats = m_Stats,
                m_RunBudget = m_RunBudget,
                m_MaxReroutes = kMaxReroutesPerRun,
            }.Schedule(dependency);

            if (frame >= m_NextPruneFrame)
            {
                dependency = new PruneJob { m_Cars = m_Cars, m_Frame = frame }.Schedule(dependency);
                m_NextPruneFrame = frame + 32768;
            }

            uint sharePercent = NavigationGroup.SharePercent(share);
            if (sharePercent > 0)
            {
                uint group = (frame / kUpdateInterval) % kGroupCount;
                m_CarQuery.ResetFilter();
                m_CarQuery.SetSharedComponentFilter(new UpdateFrame(group));

                dependency = new CheckCarsJob
                {
                    m_EntityType = m_EntityType,
                    m_PersonalCarType = m_PersonalCarType,
                    m_DeliveryTruckType = m_DeliveryTruckType,
                    m_TaxiType = m_TaxiType,
                    m_MovingType = m_MovingType,
                    m_PrefabRefType = m_PrefabRefType,
                    m_ControllerType = m_ControllerType,
                    m_LayoutType = m_LayoutType,
                    m_PathOwnerType = m_PathOwnerType,
                    m_CurrentLaneType = m_CurrentLaneType,
                    m_NavigationLaneType = m_NavigationLaneType,
                    m_PathElementType = m_PathElementType,
                    m_CarLaneData = m_CarLaneData,
                    m_PedestrianLaneData = m_PedestrianLaneData,
                    m_CurveData = m_CurveData,
                    m_PrefabCarData = m_PrefabCarData,
                    m_Cars = m_Cars,
                    m_Pending = m_Pending,
                    m_Stats = m_Stats,
                    m_RunBudget = m_RunBudget,
                    m_Frame = frame,
                    m_SharePercent = sharePercent,
                    m_Threshold = threshold,
                }.Schedule(m_CarQuery, dependency);
            }

            if (timer != null)
            {
                dependency.Complete();
                ModTiming.RerouteMs = timer.Elapsed.TotalMilliseconds;
                ModTiming.RerouteRequested = false;
            }
            m_LastJob = dependency;
            Dependency = dependency;
        }

        /// <summary>
        /// Re-routing counters since the last call (see kStatCount for the layout) plus [8] vehicles tracked.
        /// Resets the counters.
        /// </summary>
        internal void HarvestStats(float[] into)
        {
            m_LastJob.Complete();
            for (int i = 0; i < kStatCount; i++) into[i] = m_Stats[i];
            into[kStatCount] = m_Cars.IsCreated ? m_Cars.Count() : 0;
            for (int i = 0; i < m_Stats.Length; i++) m_Stats[i] = 0f;
        }
    }

    /// <summary>Estimated remaining drive time of a path with the route planner's current congestion data.</summary>
    internal static class RouteEstimate
    {
        public static void Estimate(DynamicBuffer<PathElement> path, int start, int maxElements,
            ref ComponentLookup<CarLane> carLaneData, ref ComponentLookup<Curve> curveData,
            out float seconds, out float freeSeconds)
        {
            seconds = 0f;
            freeSeconds = 0f;
            int end = math.min(path.Length, start + maxElements);
            for (int i = math.max(0, start); i < end; i++)
            {
                PathElement element = path[i];
                if (!carLaneData.TryGetComponent(element.m_Target, out CarLane lane)) continue;
                if (!curveData.TryGetComponent(element.m_Target, out Curve curve)) continue;
                float length = curve.m_Length * math.abs(element.m_TargetDelta.y - element.m_TargetDelta.x);
                float free = length / math.max(1f, lane.m_SpeedLimit);
                // Same slowdown the pathfinder applies: speed * (1 - FlowOffset / 256).
                float slowdown = 1f - lane.m_FlowOffset / 256f;
                freeSeconds += free;
                seconds += free / math.max(1f / 256f, slowdown);
            }
        }
    }
}
