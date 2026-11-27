using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Net;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Replaces the vanilla <see cref="TrafficFlowSystem"/>.
    ///
    /// Vanilla learns a slow, per-time-of-day "typical traffic" speed for each lane and turns it into
    /// <c>CarLane.m_FlowOffset</c>, which the pathfinder uses to slow that lane down in route costs.
    /// This system keeps that typical-traffic data (and the Road values the traffic info view reads)
    /// exactly as vanilla computes it, and adds a fast "live" speed measured over the last few game
    /// minutes. The FlowOffset sent to the pathfinder is a blend of the two.
    ///
    /// Live state is kept in memory only (nothing is added to the save), and is rebuilt within a few
    /// game minutes after loading.
    /// </summary>
    public partial class LiveTrafficFlowSystem : GameSystemBase
    {
        // Lanes are split into 16 update groups by the game's UpdateFrame. We visit one group every
        // 64 frames, so each lane gets a live update every 1024 frames (~5.6 game minutes).
        private const int kUpdateInterval = 64;
        private const int kGroupCount = 16;
        private const int kUpdatesPerDayForGroups = 262144 / (kUpdateInterval * kGroupCount);

        // Vanilla updates the typical-traffic history once per 8192 frames per lane. We do the
        // history step on every 8th live visit to keep the exact same rate.
        private const int kLiveVisitsPerHistoryStep = 8;

        // Live samples are in "lane traversals at the speed limit": one car driving the whole lane
        // at the limit adds 1.0 to duration and 1.0 to distance. Below this much traffic in the
        // window, the live speed is blended towards free flow (no cars = road is free right now).
        private const float kFullConfidenceTraversals = 1f;

        // Forecast: a lane carries about this many vehicles per second at the speed limit (a first guess, tuned in game).
        internal const float kSaturationFlow = 0.5f;
        // BPR capacity curve: extra time = alpha * (demand / capacity)^4; alpha at "Spread drivers over detours" 100%.
        private const float kMaxForecastAlpha = 0.6f;
        // Local streets: extra time = k * (demand above usual) / capacity; k at "Keep out of neighbourhoods" 100%.
        private const float kMaxNeighbourhoodK = 4f;
        // A local street counts as busier than usual above usual * 1.2 + 2 vehicles (ignores noise on quiet streets).
        private const float kUsualMargin = 1.2f;
        private const float kUsualSlack = 2f;
        // The usual demand of a lane follows the forecast slowly: 1/64 per visit, about 6 game hours.
        private const float kUsualRate = 1f / 64f;
        // Cost dithering: up to +-10% (at "Spread" 100%) on slowed lanes, changing every rotation.
        private const float kMaxDither = 0.1f;
        private const float kDitherAbove = 1.2f;

        // Entries not visited for this long belong to deleted lanes.
        private const uint kStaleFrames = 16384;
        private const uint kPruneEveryFrames = 32768;

        private struct LiveLane
        {
            // LaneFlow.m_Next (+ SecondaryFlow.m_Next) at our previous visit, to get the delta.
            public float2 m_PrevNext;
            // Smoothed (duration, distance) per live window.
            public float2 m_Live;
            public uint m_LastFrame;
        }

        /// <summary>How many vehicles usually plan to drive a lane (slow average of the forecast).</summary>
        private struct UsualDemand
        {
            public float m_Value;
            public uint m_LastFrame;
        }

        [BurstCompile]
        private struct UpdateLanesJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public ComponentTypeHandle<LaneFlow> m_LaneFlowType;
            public ComponentTypeHandle<SecondaryFlow> m_SecondaryFlowType;
            public NativeParallelHashMap<Entity, LiveLane> m_Live;
            public float4 m_HistoryBlend;
            public bool m_UpdateHistory;
            public float m_Responsiveness;
            public uint m_Frame;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<LaneFlow> laneFlows = chunk.GetNativeArray(ref m_LaneFlowType);
                NativeArray<SecondaryFlow> secondaryFlows = chunk.GetNativeArray(ref m_SecondaryFlowType);
                bool hasLaneFlow = laneFlows.Length != 0;
                bool hasSecondary = secondaryFlows.Length != 0;

                for (int i = 0; i < entities.Length; i++)
                {
                    float2 next = default;
                    if (hasLaneFlow) next += laneFlows[i].m_Next;
                    if (hasSecondary) next += secondaryFlows[i].m_Next;

                    Entity lane = entities[i];
                    m_Live.TryGetValue(lane, out LiveLane state);

                    // m_Next only grows between history steps; a drop means it was reset by
                    // something else (e.g. vanilla ran before us), so take it all as new.
                    float2 delta = next - state.m_PrevNext;
                    if (math.any(delta < 0f)) delta = next;

                    state.m_Live = math.lerp(state.m_Live, delta, m_Responsiveness);
                    state.m_PrevNext = next;
                    state.m_LastFrame = m_Frame;

                    if (m_UpdateHistory)
                    {
                        // Same typical-traffic update as vanilla.
                        if (hasLaneFlow)
                        {
                            LaneFlow flow = laneFlows[i];
                            flow.m_Duration = math.lerp(flow.m_Duration, flow.m_Next.x, m_HistoryBlend);
                            flow.m_Distance = math.lerp(flow.m_Distance, flow.m_Next.y, m_HistoryBlend);
                            flow.m_Next = default;
                            laneFlows[i] = flow;
                        }
                        if (hasSecondary)
                        {
                            SecondaryFlow flow = secondaryFlows[i];
                            flow.m_Duration = math.lerp(flow.m_Duration, flow.m_Next.x, m_HistoryBlend);
                            flow.m_Distance = math.lerp(flow.m_Distance, flow.m_Next.y, m_HistoryBlend);
                            flow.m_Next = default;
                            secondaryFlows[i] = flow;
                        }
                        state.m_PrevNext = default;
                    }

                    m_Live[lane] = state;
                }
            }
        }

        [BurstCompile]
        private struct UpdateRoadsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public BufferTypeHandle<SubLane> m_SubLaneType;
            [ReadOnly] public NativeParallelHashMap<Entity, float> m_Demand;
            [ReadOnly] public NativeParallelHashMap<Entity, byte> m_Marks;
            public NativeParallelHashMap<Entity, UsualDemand> m_Usual;
            [ReadOnly] public ComponentLookup<LaneFlow> m_LaneFlowData;
            [ReadOnly] public ComponentLookup<SecondaryFlow> m_SecondaryFlowData;
            [ReadOnly] public ComponentLookup<MasterLane> m_MasterLaneData;
            [ReadOnly] public ComponentLookup<EdgeLane> m_EdgeLaneData;
            [ReadOnly] public NativeParallelHashMap<Entity, LiveLane> m_Live;
            public ComponentTypeHandle<Road> m_RoadType;
            public ComponentLookup<CarLane> m_CarLaneData;
            public NativeQueue<FlowActionData> m_FlowActions;
            // [0] lanes, [1] lanes at half speed or worse, [2] sum of blended speed, [3] lanes with live traffic,
            // [4] lanes priced by the forecast, [5] local streets with the neighbourhood penalty, [6] highest demand/capacity
            public NativeArray<float> m_Stats;
            public float4 m_TimeFactors;
            public float m_LiveWeight;
            public float m_PenaltyStrength;
            public float m_MaxSlowdownFactor;
            public int m_MaxOffset;
            public float m_ForecastAlpha;
            public float m_NeighbourhoodK;
            public float m_Dither;
            public float m_HorizonCapacity;
            public uint m_Rotation;
            public uint m_Frame;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> edges = chunk.GetNativeArray(m_EntityType);
                NativeArray<Road> roads = chunk.GetNativeArray(ref m_RoadType);
                BufferAccessor<SubLane> subLaneBuffers = chunk.GetBufferAccessor(ref m_SubLaneType);

                for (int i = 0; i < roads.Length; i++)
                {
                    Road road = roads[i];
                    m_Marks.TryGetValue(edges[i], out byte mark);
                    DynamicBuffer<SubLane> subLanes = subLaneBuffers[i];
                    road.m_TrafficFlowDuration0 = default;
                    road.m_TrafficFlowDuration1 = default;
                    road.m_TrafficFlowDistance0 = default;
                    road.m_TrafficFlowDistance1 = default;

                    // A master lane has no flow of its own; it gets the sum of the lanes it groups.
                    int masterIndex = -1;
                    MasterLane master = default;
                    float4 masterDuration = default;
                    float4 masterDistance = default;
                    float2 masterLive = default;
                    // Routes name the master lane; the vehicles then spread over its lanes.
                    float masterDemand = 0f;
                    float masterShare = 0f;

                    for (int j = 0; j < subLanes.Length; j++)
                    {
                        Entity lane = subLanes[j].m_SubLane;
                        if (masterIndex != -1 && j > master.m_MaxIndex)
                        {
                            ApplyLane(subLanes[masterIndex].m_SubLane, masterDuration, masterDistance, masterLive, masterDemand, mark, out _);
                            masterIndex = -1;
                        }

                        bool hasLaneFlow = m_LaneFlowData.TryGetComponent(lane, out LaneFlow laneFlow);
                        bool hasSecondary = m_SecondaryFlowData.TryGetComponent(lane, out SecondaryFlow secondary);
                        if (hasLaneFlow || hasSecondary)
                        {
                            float4 duration = laneFlow.m_Duration + secondary.m_Duration;
                            float4 distance = laneFlow.m_Distance + secondary.m_Distance;
                            float2 live = m_Live.TryGetValue(lane, out LiveLane state) ? state.m_Live : default;
                            m_Demand.TryGetValue(lane, out float ownDemand);
                            float demand = ownDemand + (masterIndex != -1 ? masterShare : 0f);
                            ApplyLane(lane, duration, distance, live, demand, mark, out bool isRoundabout);

                            // Info-view data: same weighting as vanilla (lane ends on edges, 1/3 inside roundabouts).
                            float2 weight;
                            if (m_EdgeLaneData.TryGetComponent(lane, out EdgeLane edgeLane))
                            {
                                weight = math.select(0f, 1f, new bool2(math.any(edgeLane.m_EdgeDelta == 0f), math.any(edgeLane.m_EdgeDelta == 1f)));
                            }
                            else
                            {
                                weight = math.select(1f, 1f / 3f, isRoundabout);
                            }
                            road.m_TrafficFlowDuration0 += laneFlow.m_Duration * weight.x;
                            road.m_TrafficFlowDuration1 += laneFlow.m_Duration * weight.y;
                            road.m_TrafficFlowDistance0 += laneFlow.m_Distance * weight.x;
                            road.m_TrafficFlowDistance1 += laneFlow.m_Distance * weight.y;

                            masterDuration += duration;
                            masterDistance += distance;
                            masterLive += live;
                            masterDemand += ownDemand;
                        }
                        else if (m_MasterLaneData.TryGetComponent(lane, out MasterLane masterLane))
                        {
                            masterIndex = j;
                            master = masterLane;
                            masterDuration = default;
                            masterDistance = default;
                            masterLive = default;
                            m_Demand.TryGetValue(lane, out masterDemand);
                            masterShare = masterDemand / math.max(1, master.m_MaxIndex - master.m_MinIndex + 1);
                        }
                    }

                    if (masterIndex != -1)
                    {
                        ApplyLane(subLanes[masterIndex].m_SubLane, masterDuration, masterDistance, masterLive, masterDemand, mark, out _);
                    }

                    roads[i] = road;
                }
            }

            private void ApplyLane(Entity lane, float4 duration, float4 distance, float2 live, float demand, byte mark, out bool isRoundabout)
            {
                isRoundabout = false;
                if (!m_CarLaneData.TryGetComponent(lane, out CarLane carLane))
                {
                    return;
                }
                isRoundabout = (carLane.m_Flags & (CarLaneFlags.Approach | CarLaneFlags.Roundabout)) == CarLaneFlags.Roundabout;

                // 1. Speed: typical traffic for this time of day (actual speed / speed limit, as vanilla), blended with
                // live traffic over the last few game minutes. With little or no live traffic, assume free flow.
                float4 typical = math.select(1f, math.saturate(distance / duration), duration > 0f);
                float typicalSpeed = math.dot(typical, m_TimeFactors);
                float confidence = math.saturate(live.x / kFullConfidenceTraversals);
                float measured = math.select(1f, math.saturate(live.y / live.x), live.x > 1e-4f);
                float liveSpeed = math.lerp(1f, measured, confidence);

                // Blend travel times, not speeds: a jam (speed near 0) must be able to reach the cap even when
                // only part of the weight is live. With weight 0 and strength 1 this equals vanilla.
                const float minSpeed = 1f / 256f;
                float typicalTime = 1f / math.max(typicalSpeed, minSpeed);
                float liveTime = 1f / math.max(liveSpeed, minSpeed);
                float timeFactor = math.lerp(typicalTime, liveTime, m_LiveWeight);
                timeFactor = 1f + (timeFactor - 1f) * m_PenaltyStrength;

                // 2. Forecast: vehicles planning to drive this lane soon, on a capacity curve. A floor under the speed
                // cost, not on top of it: once the lane really jams, the speed signal already shows it.
                float vc = demand / m_HorizonCapacity;
                if (m_ForecastAlpha > 0f)
                {
                    float vc2 = vc * vc;
                    float forecastFactor = 1f + m_ForecastAlpha * vc2 * vc2;
                    m_Stats[4] += math.select(0f, 1f, forecastFactor > timeFactor && forecastFactor > 1.05f);
                    timeFactor = math.max(timeFactor, forecastFactor);
                }
                m_Stats[6] = math.max(m_Stats[6], vc);

                // 3. Local streets: much more planned traffic than usual is through traffic cutting through.
                bool local = carLane.m_SpeedLimit <= TrafficReportSystem.kLocalStreetLimit && (carLane.m_Flags & CarLaneFlags.Highway) == 0;
                if (m_NeighbourhoodK > 0f && local)
                {
                    UsualDemand usual;
                    if (!m_Usual.TryGetValue(lane, out usual)) usual.m_Value = demand;
                    usual.m_Value = math.lerp(usual.m_Value, demand, kUsualRate);
                    usual.m_LastFrame = m_Frame;
                    m_Usual[lane] = usual;
                    float excess = math.max(0f, demand - usual.m_Value * kUsualMargin - kUsualSlack);
                    if (excess > 0f)
                    {
                        timeFactor *= 1f + m_NeighbourhoodK * excess / m_HorizonCapacity;
                        m_Stats[5] += 1f;
                    }
                }

                // 4. The player's temporary road marks (roadworks tool).
                bool closed = mark == RoadMarkSystem.kClosed;
                if (mark == RoadMarkSystem.kAvoid) timeFactor = math.max(timeFactor, RoadMarkSystem.kAvoidFactor);
                else if (mark == RoadMarkSystem.kPrefer) timeFactor = 1f + (timeFactor - 1f) * RoadMarkSystem.kPreferKeep;

                // 5. Dithering: slowed lanes look a little faster or slower each rotation, so route requests made close
                // together don't all pick the very same detour.
                if (m_Dither > 0f && timeFactor > kDitherAbove)
                {
                    float u = (math.hash(new uint2((uint)lane.Index, m_Rotation)) & 0xFFFFu) / 65535f;
                    timeFactor *= 1f + (u * 2f - 1f) * m_Dither;
                }

                timeFactor = math.clamp(timeFactor, 1f, m_MaxSlowdownFactor);
                float speed = 1f / timeFactor;
                // A closed road gets the pathfinder's maximum, past the "Avoid jams up to" limit.
                byte offset = closed
                    ? (byte)255
                    : (byte)math.clamp(256 - (int)math.round(speed * 256f), 0, m_MaxOffset);

                m_Stats[0] += 1f;
                m_Stats[1] += math.select(0f, 1f, speed <= 0.5f);
                m_Stats[2] += speed;
                m_Stats[3] += math.select(0f, 1f, confidence >= 0.5f);

                if (offset != carLane.m_FlowOffset)
                {
                    carLane.m_FlowOffset = offset;
                    m_CarLaneData[lane] = carLane;
                    m_FlowActions.Enqueue(new FlowActionData { m_Owner = lane, m_FlowOffset = offset });
                }
            }
        }

        [BurstCompile]
        private struct PruneJob : IJob
        {
            public NativeParallelHashMap<Entity, LiveLane> m_Live;
            public NativeParallelHashMap<Entity, UsualDemand> m_Usual;
            public uint m_Frame;

            public void Execute()
            {
                NativeArray<Entity> keys = m_Live.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (m_Frame - m_Live[keys[i]].m_LastFrame > kStaleFrames)
                    {
                        m_Live.Remove(keys[i]);
                    }
                }
                keys.Dispose();
                keys = m_Usual.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (m_Frame - m_Usual[keys[i]].m_LastFrame > kStaleFrames)
                    {
                        m_Usual.Remove(keys[i]);
                    }
                }
                keys.Dispose();
            }
        }

        private SimulationSystem m_SimulationSystem;
        private TimeSystem m_TimeSystem;
        private PathfindQueueSystem m_PathfindQueueSystem;
        private TrafficFlowSystem m_VanillaSystem;
        private DemandForecastSystem m_ForecastSystem;
        private RoadMarkSystem m_RoadMarkSystem;

        private EntityQuery m_LaneQuery;
        private EntityQuery m_RoadQuery;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<LaneFlow> m_LaneFlowType;
        private ComponentTypeHandle<SecondaryFlow> m_SecondaryFlowType;
        private ComponentTypeHandle<Road> m_RoadType;
        private BufferTypeHandle<SubLane> m_SubLaneType;
        private ComponentLookup<LaneFlow> m_LaneFlowData;
        private ComponentLookup<SecondaryFlow> m_SecondaryFlowData;
        private ComponentLookup<MasterLane> m_MasterLaneData;
        private ComponentLookup<EdgeLane> m_EdgeLaneData;
        private ComponentLookup<CarLane> m_CarLaneData;

        private NativeParallelHashMap<Entity, LiveLane> m_Live;
        private NativeParallelHashMap<Entity, UsualDemand> m_Usual;
        private NativeArray<float> m_Stats;
        private JobHandle m_LastJob;
        private bool m_Active;
        private string m_LastSettingsLabel;
        private uint m_NextPruneFrame;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kUpdateInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_PathfindQueueSystem = World.GetOrCreateSystemManaged<PathfindQueueSystem>();
            m_VanillaSystem = World.GetOrCreateSystemManaged<TrafficFlowSystem>();
            m_ForecastSystem = World.GetOrCreateSystemManaged<DemandForecastSystem>();
            m_RoadMarkSystem = World.GetOrCreateSystemManaged<RoadMarkSystem>();

            m_LaneQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UpdateFrame>() },
                Any = new[] { ComponentType.ReadWrite<LaneFlow>(), ComponentType.ReadWrite<SecondaryFlow>() },
                None = new[] { ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>() },
            });
            m_RoadQuery = GetEntityQuery(
                ComponentType.ReadWrite<Road>(),
                ComponentType.ReadOnly<SubLane>(),
                ComponentType.ReadOnly<UpdateFrame>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_EntityType = GetEntityTypeHandle();
            m_LaneFlowType = GetComponentTypeHandle<LaneFlow>(false);
            m_SecondaryFlowType = GetComponentTypeHandle<SecondaryFlow>(false);
            m_RoadType = GetComponentTypeHandle<Road>(false);
            m_SubLaneType = GetBufferTypeHandle<SubLane>(true);
            m_LaneFlowData = GetComponentLookup<LaneFlow>(true);
            m_SecondaryFlowData = GetComponentLookup<SecondaryFlow>(true);
            m_MasterLaneData = GetComponentLookup<MasterLane>(true);
            m_EdgeLaneData = GetComponentLookup<EdgeLane>(true);
            m_CarLaneData = GetComponentLookup<CarLane>(false);

            m_Live = new NativeParallelHashMap<Entity, LiveLane>(16384, Allocator.Persistent);
            m_Usual = new NativeParallelHashMap<Entity, UsualDemand>(8192, Allocator.Persistent);
            m_Stats = new NativeArray<float>(7, Allocator.Persistent);

            RequireAnyForUpdate(m_LaneQuery, m_RoadQuery);
            Logger.Info("LiveTrafficFlowSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            if (m_VanillaSystem != null) m_VanillaSystem.Enabled = true;
            m_Live.Dispose();
            m_Usual.Dispose();
            m_Stats.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            // Entity ids from the previous city mean nothing in the next one.
            m_LastJob.Complete();
            m_Live.Clear();
            m_Usual.Clear();
        }

        protected override void OnUpdate()
        {
            LiveTrafficSettings settings = Mod.Settings;
            if (settings != null)
            {
                string label = settings.RunLabel();
                if (label != m_LastSettingsLabel)
                {
                    Logger.Info($"Settings in use: {label}");
                    m_LastSettingsLabel = label;
                }
            }
            bool wantActive = settings == null || settings.IsActive;
            if (!SetActive(wantActive))
            {
                return;
            }

            uint frame = m_SimulationSystem.frameIndex;

            float liveTrust = 0.75f, reactionSpeed = 0.5f, maxSlowdownFactor = 10f, penaltyStrength = 1f;
            settings?.GetTuning(out liveTrust, out reactionSpeed, out maxSlowdownFactor, out penaltyStrength);
            float spread = 0.5f, neighbourhoods = 0.5f;
            settings?.GetForecastTuning(out spread, out neighbourhoods);
            // A lane at speed (1 - offset/256) takes 1/(1 - offset/256) times as long, so
            // "at most F times slower" means offset <= 256 * (1 - 1/F).
            int maxOffset = math.clamp((int)math.floor(256f * (1f - 1f / math.max(1f, maxSlowdownFactor))), 0, 255);

            uint group = SimulationUtils.GetUpdateFrame(frame, kUpdatesPerDayForGroups, kGroupCount);
            // One full rotation over all groups takes kUpdateInterval * kGroupCount frames.
            uint rotation = frame / (kUpdateInterval * kGroupCount);
            bool updateHistory = rotation % kLiveVisitsPerHistoryStep == 0;

            float timeOfDay = m_TimeSystem.normalizedTime * 4f;
            float4 timeFactors = math.saturate(new float4(
                math.max(timeOfDay - 3f, 1f - timeOfDay),
                1f - math.abs(timeOfDay - new float3(1f, 2f, 3f))));

            m_EntityType.Update(this);
            m_LaneFlowType.Update(this);
            m_SecondaryFlowType.Update(this);
            m_RoadType.Update(this);
            m_SubLaneType.Update(this);
            m_LaneFlowData.Update(this);
            m_SecondaryFlowData.Update(this);
            m_MasterLaneData.Update(this);
            m_EdgeLaneData.Update(this);
            m_CarLaneData.Update(this);

            m_LaneQuery.ResetFilter();
            m_LaneQuery.SetSharedComponentFilter(new UpdateFrame(group));
            m_RoadQuery.ResetFilter();
            m_RoadQuery.SetSharedComponentFilter(new UpdateFrame(group));

            JobHandle dependency = JobHandle.CombineDependencies(Dependency, m_LastJob);
            // Once per game hour, measure how long our own jobs take (for the statistics).
            System.Diagnostics.Stopwatch timer = null;
            if (ModTiming.FlowRequested)
            {
                dependency.Complete();
                timer = System.Diagnostics.Stopwatch.StartNew();
            }

            if (frame >= m_NextPruneFrame)
            {
                dependency = new PruneJob { m_Live = m_Live, m_Usual = m_Usual, m_Frame = frame }.Schedule(dependency);
                m_NextPruneFrame = frame + kPruneEveryFrames;
            }

            var lanesJob = new UpdateLanesJob
            {
                m_EntityType = m_EntityType,
                m_LaneFlowType = m_LaneFlowType,
                m_SecondaryFlowType = m_SecondaryFlowType,
                m_Live = m_Live,
                m_HistoryBlend = timeFactors * 0.125f,
                m_UpdateHistory = updateHistory,
                m_Responsiveness = reactionSpeed,
                m_Frame = frame,
            };
            JobHandle lanesHandle = lanesJob.Schedule(m_LaneQuery, dependency);

            NativeParallelHashMap<Entity, float> demand = m_ForecastSystem.GetDemand(out JobHandle demandDeps);
            NativeParallelHashMap<Entity, byte> marks = m_RoadMarkSystem.GetMarks(out JobHandle marksDeps);
            JobHandle roadsDeps = JobHandle.CombineDependencies(lanesHandle, demandDeps, marksDeps);

            FlowAction action = new FlowAction(Allocator.Persistent);
            var roadsJob = new UpdateRoadsJob
            {
                m_EntityType = m_EntityType,
                m_SubLaneType = m_SubLaneType,
                m_Demand = demand,
                m_Marks = marks,
                m_Usual = m_Usual,
                m_LaneFlowData = m_LaneFlowData,
                m_SecondaryFlowData = m_SecondaryFlowData,
                m_MasterLaneData = m_MasterLaneData,
                m_EdgeLaneData = m_EdgeLaneData,
                m_Live = m_Live,
                m_RoadType = m_RoadType,
                m_CarLaneData = m_CarLaneData,
                m_FlowActions = action.m_FlowData,
                m_Stats = m_Stats,
                m_TimeFactors = timeFactors,
                m_LiveWeight = liveTrust,
                m_PenaltyStrength = penaltyStrength,
                m_MaxSlowdownFactor = math.max(1f, maxSlowdownFactor),
                m_MaxOffset = maxOffset,
                m_ForecastAlpha = spread * kMaxForecastAlpha,
                m_NeighbourhoodK = neighbourhoods * kMaxNeighbourhoodK,
                m_Dither = spread * kMaxDither,
                m_HorizonCapacity = DemandForecastSystem.kHorizonSeconds * kSaturationFlow,
                m_Rotation = rotation,
                m_Frame = frame,
            };
            JobHandle roadsHandle = roadsJob.Schedule(m_RoadQuery, roadsDeps);
            m_ForecastSystem.AddDemandReader(roadsHandle);
            m_RoadMarkSystem.AddReader(roadsHandle);
            if (timer != null)
            {
                roadsHandle.Complete();
                ModTiming.FlowMs = timer.Elapsed.TotalMilliseconds;
                ModTiming.FlowRequested = false;
            }

            // The pathfind queue takes ownership of the action and disposes it.
            m_PathfindQueueSystem.Enqueue(action, roadsHandle);
            m_LastJob = roadsHandle;
            Dependency = roadsHandle;
        }

        /// <summary>Switches between our system and vanilla. Returns whether ours should run.</summary>
        private bool SetActive(bool active)
        {
            if (active != m_Active)
            {
                m_LastJob.Complete();
                m_Live.Clear();
                m_Usual.Clear();
                m_Active = active;
                Logger.Info(active
                    ? "Live traffic ON: vanilla TrafficFlowSystem disabled."
                    : "Live traffic OFF: vanilla TrafficFlowSystem restored.");
            }
            // Re-assert every update in case something else toggled it.
            m_VanillaSystem.Enabled = !active;
            return active;
        }

        /// <summary>Length of the array <see cref="HarvestStats"/> fills.</summary>
        internal const int kHarvestCount = 8;

        /// <summary>
        /// Route-planner view since the last call: [0] lane updates, [1] lanes the planner sees at half speed or worse,
        /// [2] sum of planner speed, [3] lanes with live traffic, [4] lanes tracked, [5] lane updates priced by the
        /// forecast, [6] lane updates with the neighbourhood penalty, [7] highest demand/capacity. Resets the counters.
        /// </summary>
        internal void HarvestStats(float[] into)
        {
            m_LastJob.Complete();
            for (int i = 0; i < 4; i++) into[i] = m_Stats[i];
            into[4] = (m_Live.IsCreated ? m_Live.Count() : 0) + (m_Usual.IsCreated ? m_Usual.Count() : 0);
            into[5] = m_Stats[4];
            into[6] = m_Stats[5];
            into[7] = m_Stats[6];
            for (int i = 0; i < m_Stats.Length; i++) m_Stats[i] = 0f;
        }
    }
}
