using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Serialization.Entities;
using Game;
using Game.City;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Game.Vehicles;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using CarLane = Game.Net.CarLane;
using NetLaneFlags = Game.Net.CarLaneFlags;
using PersonalCar = Game.Vehicles.PersonalCar;
using DeliveryTruck = Game.Vehicles.DeliveryTruck;
using Taxi = Game.Vehicles.Taxi;
using Transform = Game.Objects.Transform;
using Owner = Game.Common.Owner;
using Aggregated = Game.Net.Aggregated;
using Aggregate = Game.Net.Aggregate;
using NetNode = Game.Net.Node;
using ConnectedEdge = Game.Net.ConnectedEdge;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Measures how traffic is actually doing, whether the mod is on ("Live") or off ("Native"), so the two can be
    /// compared. Samples all road vehicles every 1024 frames and turns each game hour into a <see cref="TrafficCapture"/>.
    /// </summary>
    public partial class TrafficReportSystem : GameSystemBase
    {
        private const int kSampleInterval = 1024;
        // Speed limit (m/s) at or below which a road counts as a local street: 50 km/h.
        private const float kLocalStreetLimit = 13.9f;
        // Progress is only counted between two consecutive samples of the same trip.
        private const uint kMaxSampleGap = kSampleInterval * 3;
        private const float kFramesPerSecond = 60f;

        // Per-sample counters: [0] vehicles, [1] sum speed/limit, [2] queued, [3] on local streets,
        // [4] nav progress (m), [5] nav time (s), [6] other progress (m), [7] other time (s), [8] jam warnings shown
        private const int kSampleStats = 9;

        private struct TripState
        {
            public Entity m_Target;
            public float m_Distance;
            public uint m_Frame;
        }

        /// <summary>
        /// The "place" of a lane, for the hotspot list: the named street (aggregate) of a road segment, or the
        /// junction (node) for lanes inside an intersection. Burst-compatible.
        /// </summary>
        private static Entity PlaceOf(Entity lane, ref ComponentLookup<Owner> ownerData, ref ComponentLookup<Aggregated> aggregatedData)
        {
            if (!ownerData.TryGetComponent(lane, out Owner owner)) return Entity.Null;
            if (aggregatedData.TryGetComponent(owner.m_Owner, out Aggregated aggregated) && aggregated.m_Aggregate != Entity.Null)
            {
                return aggregated.m_Aggregate;
            }
            return owner.m_Owner;
        }

        [BurstCompile]
        private struct SampleVehiclesJob : IJobChunk
        {
            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Aggregated> m_AggregatedData;
            public NativeParallelHashMap<Entity, int> m_PlaceQueued;
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<Moving> m_MovingType;
            [ReadOnly] public ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
            [ReadOnly] public ComponentTypeHandle<Transform> m_TransformType;
            [ReadOnly] public ComponentTypeHandle<Blocker> m_BlockerType;
            [ReadOnly] public ComponentTypeHandle<Target> m_TargetType;
            [ReadOnly] public ComponentTypeHandle<PersonalCar> m_PersonalCarType;
            [ReadOnly] public ComponentTypeHandle<DeliveryTruck> m_DeliveryTruckType;
            [ReadOnly] public ComponentTypeHandle<Taxi> m_TaxiType;
            [ReadOnly] public ComponentLookup<CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;

            public NativeParallelHashMap<Entity, TripState> m_Trips;
            public NativeArray<double> m_Stats;
            public uint m_Frame;
            public uint m_SharePercent;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Moving> movings = chunk.GetNativeArray(ref m_MovingType);
                NativeArray<CarCurrentLane> lanes = chunk.GetNativeArray(ref m_CurrentLaneType);
                NativeArray<Transform> transforms = chunk.GetNativeArray(ref m_TransformType);
                NativeArray<Blocker> blockers = chunk.GetNativeArray(ref m_BlockerType);
                NativeArray<Target> targets = chunk.GetNativeArray(ref m_TargetType);
                bool covered = chunk.Has(ref m_PersonalCarType) || chunk.Has(ref m_DeliveryTruckType) || chunk.Has(ref m_TaxiType);

                for (int i = 0; i < entities.Length; i++)
                {
                    if (!m_CarLaneData.TryGetComponent(lanes[i].m_Lane, out CarLane lane)) continue; // not on a road

                    float speed = math.length(movings[i].m_Velocity);
                    m_Stats[0] += 1;
                    m_Stats[1] += math.saturate(speed / math.max(1f, lane.m_SpeedLimit));
                    if (blockers.Length != 0)
                    {
                        Blocker blocker = blockers[i];
                        if (blocker.m_Type == BlockerType.Continuing && blocker.m_Blocker != Entity.Null && speed < 1f)
                        {
                            m_Stats[2] += 1;
                            Entity place = PlaceOf(lanes[i].m_Lane, ref m_OwnerData, ref m_AggregatedData);
                            if (place != Entity.Null)
                            {
                                m_PlaceQueued.TryGetValue(place, out int n);
                                m_PlaceQueued[place] = n + 1;
                            }
                        }
                    }
                    if (lane.m_SpeedLimit <= kLocalStreetLimit && (lane.m_Flags & NetLaneFlags.Highway) == 0) m_Stats[3] += 1;

                    // Progress towards the destination (straight-line), split by navigation group.
                    if (!covered || targets.Length == 0) continue;
                    Entity target = targets[i].m_Target;
                    if (!m_TransformData.TryGetComponent(target, out Transform targetTransform)) continue;
                    float distance = math.distance(transforms[i].m_Position.xz, targetTransform.m_Position.xz);

                    Entity vehicle = entities[i];
                    if (m_Trips.TryGetValue(vehicle, out TripState previous) && previous.m_Target == target &&
                        m_Frame - previous.m_Frame <= kMaxSampleGap)
                    {
                        double progress = previous.m_Distance - distance;
                        double seconds = (m_Frame - previous.m_Frame) / kFramesPerSecond;
                        int slot = NavigationGroup.Has(vehicle, m_SharePercent) ? 4 : 6;
                        m_Stats[slot] += progress;
                        m_Stats[slot + 1] += seconds;
                    }
                    m_Trips[vehicle] = new TripState { m_Target = target, m_Distance = distance, m_Frame = m_Frame };
                }
            }
        }

        [BurstCompile]
        private struct CountJamsJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<Bottleneck> m_BottleneckType;
            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Aggregated> m_AggregatedData;
            public NativeArray<double> m_Stats;
            public NativeParallelHashMap<Entity, int> m_PlaceJams;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Bottleneck> bottlenecks = chunk.GetNativeArray(ref m_BottleneckType);
                for (int i = 0; i < bottlenecks.Length; i++)
                {
                    // The game shows the jam warning once the timer reaches 20 (see TrafficBottleneckSystem).
                    if (bottlenecks[i].m_Timer < 20) continue;
                    m_Stats[8] += 1;
                    Entity place = PlaceOf(entities[i], ref m_OwnerData, ref m_AggregatedData);
                    if (place != Entity.Null)
                    {
                        m_PlaceJams.TryGetValue(place, out int n);
                        m_PlaceJams[place] = n + 1;
                    }
                }
            }
        }

        [BurstCompile]
        private struct PruneTripsJob : IJob
        {
            public NativeParallelHashMap<Entity, TripState> m_Trips;
            public uint m_Frame;

            public void Execute()
            {
                NativeArray<Entity> keys = m_Trips.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (m_Frame - m_Trips[keys[i]].m_Frame > kMaxSampleGap) m_Trips.Remove(keys[i]);
                }
                keys.Dispose();
            }
        }

        /// <summary>Sums over the current game hour.</summary>
        private sealed class HourAccumulator
        {
            public int Samples;
            public double Vehicles, SpeedRatio, Queued, Local, Jams;
            public int JamsMax;
            public double NavProgress, NavTime, OtherProgress, OtherTime;
            public string Mode = "";
            public string Preset = "";
            public string RunId = "";
            public string Settings = "";
            // Not recorded: the hour started before we were counting (game loaded, settings changed, reset).
            public bool Partial;
            // Per place (street or junction): queued vehicles and jam warnings, summed over the samples.
            public readonly Dictionary<Entity, float2> Places = new Dictionary<Entity, float2>();
            public float NavSharePct;
        }

        private SimulationSystem m_SimulationSystem;
        private TimeSystem m_TimeSystem;
        private CityConfigurationSystem m_CityConfigurationSystem;
        private TrafficFlowSystem m_VanillaFlowSystem;
        private LiveTrafficFlowSystem m_FlowSystem;
        private RerouteSystem m_RerouteSystem;

        private EntityQuery m_VehicleQuery;
        private EntityQuery m_BottleneckQuery;

        private EntityTypeHandle m_EntityType;
        private ComponentTypeHandle<Moving> m_MovingType;
        private ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
        private ComponentTypeHandle<Transform> m_TransformType;
        private ComponentTypeHandle<Blocker> m_BlockerType;
        private ComponentTypeHandle<Target> m_TargetType;
        private ComponentTypeHandle<PersonalCar> m_PersonalCarType;
        private ComponentTypeHandle<DeliveryTruck> m_DeliveryTruckType;
        private ComponentTypeHandle<Taxi> m_TaxiType;
        private ComponentTypeHandle<Bottleneck> m_BottleneckType;
        private ComponentLookup<CarLane> m_CarLaneData;
        private ComponentLookup<Transform> m_TransformData;

        private NativeParallelHashMap<Entity, TripState> m_Trips;
        private NativeParallelHashMap<Entity, int> m_PlaceQueued;
        private NativeParallelHashMap<Entity, int> m_PlaceJams;
        private ComponentLookup<Owner> m_OwnerData;
        private ComponentLookup<Aggregated> m_AggregatedData;
        private NameSystem m_NameSystem;
        private const int kHotspotsPerHour = 15;
        private NativeArray<double> m_Stats;
        private JobHandle m_LastJob;
        private bool m_HasPendingSample;
        private string m_RunId;
        private string m_RunSettings = "";
        private string m_RunMode = "";
        private string m_PendingMode = "";
        private string m_PendingPreset = "";
        private float m_PendingSharePct;

        private HourAccumulator m_Hour = new HourAccumulator();
        private DateTime m_HourKey = DateTime.MinValue;
        private readonly float[] m_FlowStats = new float[5];
        private readonly float[] m_RerouteStats = new float[9];

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kSampleInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_CityConfigurationSystem = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_VanillaFlowSystem = World.GetOrCreateSystemManaged<TrafficFlowSystem>();
            m_FlowSystem = World.GetOrCreateSystemManaged<LiveTrafficFlowSystem>();
            m_RerouteSystem = World.GetOrCreateSystemManaged<RerouteSystem>();

            m_VehicleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Moving>(),
                    ComponentType.ReadOnly<CarCurrentLane>(), ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Unspawned>(), ComponentType.ReadOnly<Bicycle>(),
                },
            });
            m_BottleneckQuery = GetEntityQuery(ComponentType.ReadOnly<Bottleneck>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());

            m_EntityType = GetEntityTypeHandle();
            m_MovingType = GetComponentTypeHandle<Moving>(true);
            m_CurrentLaneType = GetComponentTypeHandle<CarCurrentLane>(true);
            m_TransformType = GetComponentTypeHandle<Transform>(true);
            m_BlockerType = GetComponentTypeHandle<Blocker>(true);
            m_TargetType = GetComponentTypeHandle<Target>(true);
            m_PersonalCarType = GetComponentTypeHandle<PersonalCar>(true);
            m_DeliveryTruckType = GetComponentTypeHandle<DeliveryTruck>(true);
            m_TaxiType = GetComponentTypeHandle<Taxi>(true);
            m_BottleneckType = GetComponentTypeHandle<Bottleneck>(true);
            m_CarLaneData = GetComponentLookup<CarLane>(true);
            m_TransformData = GetComponentLookup<Transform>(true);

            m_Trips = new NativeParallelHashMap<Entity, TripState>(16384, Allocator.Persistent);
            m_PlaceQueued = new NativeParallelHashMap<Entity, int>(1024, Allocator.Persistent);
            m_PlaceJams = new NativeParallelHashMap<Entity, int>(256, Allocator.Persistent);
            m_OwnerData = GetComponentLookup<Owner>(true);
            m_AggregatedData = GetComponentLookup<Aggregated>(true);
            m_Stats = new NativeArray<double>(kSampleStats, Allocator.Persistent);
            Logger.Info("TrafficReportSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Trips.Dispose();
            m_PlaceQueued.Dispose();
            m_PlaceJams.Dispose();
            m_Stats.Dispose();
            base.OnDestroy();
        }

        /// <summary>Starts counting afresh (after the statistics were reset). Main thread.</summary>
        internal void RestartHour()
        {
            m_LastJob.Complete();
            m_HasPendingSample = false;
            m_Hour = new HourAccumulator();
            m_HourKey = DateTime.MinValue;
            m_RunId = null;
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            m_LastJob.Complete();
            m_Trips.Clear();
            m_HasPendingSample = false;
            m_Hour = new HourAccumulator();
            m_HourKey = DateTime.MinValue;
            m_RunId = null;
            TrafficReportStore.SetCurrent(new TrafficSnapshot());
        }

        protected override void OnUpdate()
        {
            LiveTrafficSettings settings = Mod.Settings;
            TrafficReportStore.CurrentCity = m_CityConfigurationSystem.cityName ?? "";
            PopupGuard.Update(settings);

            // Statistics are for traffic geeks: when the panel is off, record nothing at all.
            if (settings != null && !settings.ShowStatistics)
            {
                if (m_RunId != null || m_HasPendingSample)
                {
                    m_LastJob.Complete();
                    m_HasPendingSample = false;
                    m_Hour = new HourAccumulator();
                    m_HourKey = DateTime.MinValue;
                    m_RunId = null;
                    TrafficReportStore.SetCurrent(new TrafficSnapshot());
                }
                return;
            }

            // 1. Fold in the sample scheduled last time (it had a whole interval to finish).
            m_LastJob.Complete();
            if (m_HasPendingSample)
            {
                AddSampleToHour();
                m_HasPendingSample = false;
            }

            // 2. New game hour: finish the previous one.
            DateTime now = m_TimeSystem.GetCurrentDateTime();
            DateTime hourKey = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            if (hourKey != m_HourKey)
            {
                if (m_HourKey != DateTime.MinValue && m_Hour.Samples > 0 && !m_Hour.Partial) FinishHour(settings);
                // The first hour after we start counting began before us, so it isn't a full hour.
                m_Hour = new HourAccumulator { Partial = m_HourKey == DateTime.MinValue };
                m_HourKey = hourKey;
                ModTiming.RequestAll();
            }

            // 3. A new run starts after loading, a reset, or any change of mode or settings. The hour in progress
            // then mixes two setups, so it is not recorded.
            bool live = settings == null || settings.IsActive;
            string mode = live ? "Live" : "Native";
            string runSettings = settings != null ? settings.RunLabel() : "Balanced";
            if (m_RunId == null || runSettings != m_RunSettings || mode != m_RunMode)
            {
                m_RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                m_RunSettings = runSettings;
                m_RunMode = mode;
                bool partial = m_Hour.Partial || m_Hour.Samples > 0;
                m_Hour = new HourAccumulator { Partial = partial };
                Logger.Info($"Statistics: new run {m_RunId} [{runSettings}]");
            }

            // 4. Schedule the next sample.
            float share = 0.3f, threshold;
            settings?.GetRerouteTuning(out share, out threshold);
            m_PendingMode = mode;
            m_PendingPreset = settings != null ? settings.Awareness.ToString() : "Balanced";
            m_PendingSharePct = share * 100f;

            for (int i = 0; i < m_Stats.Length; i++) m_Stats[i] = 0;

            m_EntityType.Update(this);
            m_MovingType.Update(this);
            m_CurrentLaneType.Update(this);
            m_TransformType.Update(this);
            m_BlockerType.Update(this);
            m_TargetType.Update(this);
            m_PersonalCarType.Update(this);
            m_DeliveryTruckType.Update(this);
            m_TaxiType.Update(this);
            m_BottleneckType.Update(this);
            m_CarLaneData.Update(this);
            m_TransformData.Update(this);
            m_OwnerData.Update(this);
            m_AggregatedData.Update(this);
            m_PlaceQueued.Clear();
            m_PlaceJams.Clear();

            uint frame = m_SimulationSystem.frameIndex;
            JobHandle handle = new PruneTripsJob { m_Trips = m_Trips, m_Frame = frame }.Schedule(Dependency);
            handle = new CountJamsJob
            {
                m_EntityType = m_EntityType,
                m_BottleneckType = m_BottleneckType,
                m_OwnerData = m_OwnerData,
                m_AggregatedData = m_AggregatedData,
                m_Stats = m_Stats,
                m_PlaceJams = m_PlaceJams,
            }.Schedule(m_BottleneckQuery, handle);
            handle = new SampleVehiclesJob
            {
                m_EntityType = m_EntityType,
                m_MovingType = m_MovingType,
                m_CurrentLaneType = m_CurrentLaneType,
                m_TransformType = m_TransformType,
                m_BlockerType = m_BlockerType,
                m_TargetType = m_TargetType,
                m_PersonalCarType = m_PersonalCarType,
                m_DeliveryTruckType = m_DeliveryTruckType,
                m_TaxiType = m_TaxiType,
                m_CarLaneData = m_CarLaneData,
                m_TransformData = m_TransformData,
                m_OwnerData = m_OwnerData,
                m_AggregatedData = m_AggregatedData,
                m_PlaceQueued = m_PlaceQueued,
                m_Trips = m_Trips,
                m_Stats = m_Stats,
                m_Frame = frame,
                m_SharePercent = NavigationGroup.SharePercent(share),
            }.Schedule(m_VehicleQuery, handle);

            m_LastJob = handle;
            Dependency = handle;
            m_HasPendingSample = true;
        }

        private void AddSampleToHour()
        {
            AddPlaces(m_PlaceQueued, 0);
            AddPlaces(m_PlaceJams, 1);
            HourAccumulator h = m_Hour;
            double vehicles = m_Stats[0];
            h.Mode = m_PendingMode;
            h.Preset = m_PendingPreset;
            h.RunId = m_RunId ?? "";
            h.Settings = m_RunSettings;
            h.NavSharePct = m_PendingSharePct;
            h.Samples++;
            h.Vehicles += vehicles;
            h.SpeedRatio += m_Stats[1];
            h.Queued += m_Stats[2];
            h.Local += m_Stats[3];
            h.NavProgress += m_Stats[4];
            h.NavTime += m_Stats[5];
            h.OtherProgress += m_Stats[6];
            h.OtherTime += m_Stats[7];
            h.Jams += m_Stats[8];
            h.JamsMax = Math.Max(h.JamsMax, (int)m_Stats[8]);

            // "Right now": this sample for the counts, the hour so far for the (noisier) progress speeds.
            TrafficReportStore.SetCurrent(new TrafficSnapshot
            {
                Mode = m_PendingMode,
                Preset = m_PendingPreset,
                Settings = m_RunSettings,
                RunId = m_RunId ?? "",
                GameTime = m_HourKey == DateTime.MinValue ? "" : m_HourKey.ToString("yyyy-MM-dd HH:00"),
                Vehicles = (int)vehicles,
                AvgSpeedPct = Pct(m_Stats[1], vehicles),
                QueuedPct = Pct(m_Stats[2], vehicles),
                LocalStreetPct = Pct(m_Stats[3], vehicles),
                JamWarnings = (int)m_Stats[8],
                NavProgressKmh = Kmh(h.NavProgress, h.NavTime),
                OtherProgressKmh = Kmh(h.OtherProgress, h.OtherTime),
                NavSharePct = m_PendingSharePct,
                ModMs = LastModMs(),
            });
        }

        private void AddPlaces(NativeParallelHashMap<Entity, int> map, int component)
        {
            if (map.IsEmpty) return;
            NativeKeyValueArrays<Entity, int> kv = map.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < kv.Length; i++)
            {
                m_Hour.Places.TryGetValue(kv.Keys[i], out float2 v);
                v[component] += kv.Values[i];
                m_Hour.Places[kv.Keys[i]] = v;
            }
            kv.Dispose();
        }

        /// <summary>The busiest places of the hour by queued vehicles (jam warnings weigh in too), with names.</summary>
        private List<HotspotRow> TopPlaces(HourAccumulator h, TrafficCapture capture)
        {
            var rows = new List<HotspotRow>();
            if (h.Places.Count == 0) return rows;
            float samples = Math.Max(1, h.Samples);
            foreach (KeyValuePair<Entity, float2> kv in h.Places.OrderByDescending(p => p.Value.x + 5f * p.Value.y).Take(kHotspotsPerHour * 2))
            {
                if (!EntityManager.Exists(kv.Key)) continue;
                string kind;
                string name;
                if (EntityManager.HasComponent<Aggregate>(kv.Key))
                {
                    kind = "street";
                    name = m_NameSystem.GetRenderedLabelName(kv.Key);
                }
                else if (EntityManager.HasComponent<NetNode>(kv.Key))
                {
                    kind = "junction";
                    name = JunctionName(kv.Key);
                }
                else continue; // parking lots, buildings, etc.
                rows.Add(new HotspotRow
                {
                    RunId = capture.RunId,
                    City = capture.City,
                    GameTime = capture.GameTime,
                    Place = string.IsNullOrEmpty(name) ? "(unnamed)" : name,
                    Kind = kind,
                    Queued = kv.Value.x / samples,
                    Jams = kv.Value.y / samples,
                });
                if (rows.Count >= kHotspotsPerHour) break;
            }
            return rows;
        }

        /// <summary>"Street A / Street B" from the streets that meet at a junction.</summary>
        private string JunctionName(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return "Junction";
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            var names = new List<string>();
            for (int i = 0; i < edges.Length && names.Count < 3; i++)
            {
                Entity edge = edges[i].m_Edge;
                if (!EntityManager.HasComponent<Aggregated>(edge)) continue;
                Entity agg = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                if (agg == Entity.Null) continue;
                string n = m_NameSystem.GetRenderedLabelName(agg);
                if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
            }
            return names.Count == 0 ? "Junction" : names.Count == 1 ? names[0] + " (junction)" : string.Join(" / ", names);
        }

        private void FinishHour(LiveTrafficSettings settings)
        {
            HourAccumulator h = m_Hour;

            m_FlowSystem.HarvestStats(m_FlowStats);
            m_RerouteSystem.HarvestStats(m_RerouteStats);
            float flowPct = 0f;
            try
            {
                m_VanillaFlowSystem.RefreshCityTrafficAverages();
                flowPct = m_VanillaFlowSystem.cityAverageTrafficFlow;
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read the game's traffic flow: {e.Message}");
            }

            float reroutes = m_RerouteStats[1];
            float trucks = m_RerouteStats[6];
            float taxis = m_RerouteStats[7];
            float better = m_RerouteStats[3];
            var capture = new TrafficCapture
            {
                RealTime = DateTime.Now,
                GameTime = m_HourKey.ToString("yyyy-MM-dd HH:00"),
                GameHour = m_HourKey.Hour,
                City = TrafficReportStore.CurrentCity,
                Mode = h.Mode,
                Preset = h.Preset,
                RunId = h.RunId,
                Settings = h.Settings,
                RerouteHeldBack = (int)m_RerouteStats[5],
                Samples = h.Samples,
                Vehicles = (float)(h.Vehicles / h.Samples),
                AvgSpeedPct = Pct(h.SpeedRatio, h.Vehicles),
                QueuedPct = Pct(h.Queued, h.Vehicles),
                LocalStreetPct = Pct(h.Local, h.Vehicles),
                JamWarnings = (float)(h.Jams / h.Samples),
                JamWarningsMax = h.JamsMax,
                FlowPct = flowPct,
                NavProgressKmh = Kmh(h.NavProgress, h.NavTime),
                OtherProgressKmh = Kmh(h.OtherProgress, h.OtherTime),
                NavSharePct = h.NavSharePct,
                Reroutes = (int)reroutes,
                RerouteCars = (int)(reroutes - trucks - taxis),
                RerouteTrucks = (int)trucks,
                RerouteTaxis = (int)taxis,
                RerouteBetter = (int)better,
                RerouteNoBetter = (int)(m_RerouteStats[2] - better),
                RerouteSavedPct = better > 0 ? m_RerouteStats[4] / better * 100f : 0f,
                PlannerSpeedPct = m_FlowStats[0] > 0 ? m_FlowStats[2] / m_FlowStats[0] * 100f : 0f,
                PlannerJammedPct = m_FlowStats[0] > 0 ? m_FlowStats[1] / m_FlowStats[0] * 100f : 0f,
                ModMs = LastModMs(),
            };
            TrafficReportStore.Add(capture);
            try
            {
                TrafficReportStore.AddHotspots(TopPlaces(h, capture));
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not record traffic hotspots: {e.Message}");
            }
            if (settings != null && settings.LogStatistics) Logger.Info(capture.ToLogLine());
        }

        private static float LastModMs()
        {
            double total = 0;
            bool any = false;
            if (ModTiming.FlowMs >= 0) { total += ModTiming.FlowMs; any = true; }
            if (ModTiming.RerouteMs >= 0) { total += ModTiming.RerouteMs; any = true; }
            return any ? (float)total : -1f;
        }

        private static float Pct(double part, double whole) => whole > 0 ? (float)(part / whole * 100.0) : 0f;
        private static float Kmh(double meters, double seconds) => seconds > 0 ? (float)(meters / seconds * 3.6) : 0f;
    }
}
