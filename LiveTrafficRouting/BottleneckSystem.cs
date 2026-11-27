using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Pathfind;
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
using Aggregate = Game.Net.Aggregate;
using Aggregated = Game.Net.Aggregated;
using BorderDistrict = Game.Areas.BorderDistrict;
using Building = Game.Buildings.Building;
using CarLane = Game.Net.CarLane;
using CarLaneFlags = Game.Net.CarLaneFlags;
using ConnectedEdge = Game.Net.ConnectedEdge;
using CurrentDistrict = Game.Areas.CurrentDistrict;
using Curve = Game.Net.Curve;
using Edge = Game.Net.Edge;
using EdgeLane = Game.Net.EdgeLane;
using Moving = Game.Objects.Moving;
using NetNode = Game.Net.Node;
using Owner = Game.Common.Owner;
using PropertyRenter = Game.Buildings.PropertyRenter;
using Transform = Game.Objects.Transform;
using Unspawned = Game.Objects.Unspawned;
using Creature = Game.Creatures.Creature;
using HumanCurrentLane = Game.Creatures.HumanCurrentLane;
using PedestrianLane = Game.Net.PedestrianLane;
using PedestrianLaneFlags = Game.Net.PedestrianLaneFlags;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Bottleneck finder (statistics panel only; changes no routing). Every 1024 frames it looks at every queued car
    /// and follows the chain of cars ahead of it to the <b>head of its queue</b>: the place where the queue starts,
    /// which is where a fix helps, not where most cars stand. The head car tells why the queue is there (traffic lights,
    /// turning across oncoming traffic, give way, junction, road narrows...). For covered vehicles (personal cars,
    /// delivery trucks, taxis) it also records where they are going and where they were first seen, and walks the route
    /// ahead looking for <b>detours</b>: two points close together on the map that the route takes a long way to connect.
    /// Pairs many queued routes share are suggested as possible new links.
    ///
    /// One summary per game hour: shown in the panel's Bottlenecks tab, appended to bottlenecks.csv and links.csv,
    /// and written to the log in detail while "Write traffic report to log" is on. All state is in memory only.
    /// </summary>
    public partial class BottleneckSystem : GameSystemBase
    {
        private const int kSampleInterval = 1024;
        private const int kMaxChain = 256;
        // A queue that crawls is one queue: follow the cars ahead while they move slower than this (m/s, ~11 km/h).
        private const float kCrawlSpeed = 3f;
        private const float kCongestedShare = 0.4f;
        // A detour is suggested only when this many queued routes per game hour take it.
        private const int kLinkMinRoutes = 10;
        // A detour counts for a queue only when the long way round passes the queue's head within this distance (m).
        private const float kBypassDistance = 80f;
        // A car queued in this many samples in a row (~17 game seconds apart) waited longer than a light cycle.
        private const int kLongWaitSamples = 3;
        // Fewer long-waiting cars than this per sample at the top place: no real bottleneck this hour.
        private const float kMinLongWait = 3f;
        // Playable area: +-7168 m. A trip first seen within this margin of the edge came from outside the city.
        private const float kMapHalfSize = 7168f;
        private const float kBorderMargin = 600f;
        // Marker area for "outside the city" (no entity has this index).
        private const int kOutsideIndex = int.MaxValue - 7;
        private const int kMaxRoutePoints = 60;
        private const float kMaxRouteLength = 6000f;
        // A detour: two route points at most this far apart on the map...
        private const float kLinkMaxGap = 400f;
        // ...that the route takes at least this long, and this many times the straight distance, to connect.
        private const float kLinkMinRoute = 1000f;
        private const float kLinkMinRatio = 3f;
        // ...at about the same height (not a highway passing over a street).
        private const float kLinkMaxHeight = 6f;
        // Routes walked for detours per sample (about 10 samples per game hour).
        private const int kLinkRoutesPerSample = 200;
        // Origin and destination both farther than this from the bottleneck: through traffic.
        private const float kThroughDistance = 1000f;
        private const uint kOriginForgetFrames = kSampleInterval * 3;
        private const int kRowsPerHour = 5;
        // Kept per game hour for the ranking over time (more than shown, so a place's hours add up).
        private const int kTrackRows = 15;
        private const int kTrackLinks = 15;
        // Ranking window: the last this many recorded game hours.
        private const int kWindowHours = 24;
        private const int kLinksPerHour = 3;

        internal static readonly string[] kCauseNames =
        {
            "traffic lights", "turning across oncoming traffic", "cars crossing their path at the junction", "give way or stop sign",
            "lanes merge or the road narrows", "busy junction", "cars entering the road", "slow traffic",
            "cars parking or leaving parking", "people crossing at a crosswalk", "people crossing where there is no crosswalk",
            "buses or trams stopping in the lane", "service vehicles stopping in the lane", "cars merging onto the highway",
        };
        private const int kCauseCount = 14;
        internal const int kCausePeopleCrosswalk = 9;
        internal const int kCausePeopleJaywalk = 10;

        // Stat slots, per sample: [0] cars on roads, [1] queued, [2] chain length sum, [3] longest chain,
        // [4] chains cut at kMaxChain, [5] distinct queue heads, [6] heads without a place, [7] queued covered vehicles,
        // [8] destination area known, [9] origin area known, [10] origin unknown (car was already driving when counting
        // started), [11] routes walked for detours, [12] routes with a detour, [13] through traffic, [14] both ends known,
        // [15] queued cars standing on another place than their head, [16] queued cars that waited kLongWaitSamples+ samples,
        // [17] origins outside the city, [18] destinations outside the city
        private const int kStatCount = 19;

        private struct HeadLink
        {
            public Entity Head;
            public int Chain;
        }

        private struct HeadInfo
        {
            public Entity Place;
            public float3 Position;
            public int Cause;
            // For a queue on a street: the first junction (3+ roads) ahead of its head car, where the queue ends.
            public Entity Downstream;
        }

        private struct PlaceStats
        {
            public int Queued, LongWait, Spill, Through, OdKnown, ChainMax;
            public int C0, C1, C2, C3, C4, C5, C6, C7, C8, C9, C10, C11, C12, C13;
            // Queued trucks (delivery and cargo); queued cars standing inside another junction (the queue reaches back
            // into it), and that junction.
            public int Trucks, BoxBlock;
            public Entity BoxNode;
            public Entity ExampleHead;
            public float3 Position;

            public void AddCause(int cause)
            {
                switch (cause)
                {
                    case 0: C0++; break;
                    case 1: C1++; break;
                    case 2: C2++; break;
                    case 3: C3++; break;
                    case 4: C4++; break;
                    case 5: C5++; break;
                    case 6: C6++; break;
                    case 8: C8++; break;
                    case 9: C9++; break;
                    case 10: C10++; break;
                    case 11: C11++; break;
                    case 12: C12++; break;
                    case 13: C13++; break;
                    default: C7++; break;
                }
            }

            public int Cause(int i)
            {
                switch (i)
                {
                    case 0: return C0;
                    case 1: return C1;
                    case 2: return C2;
                    case 3: return C3;
                    case 4: return C4;
                    case 5: return C5;
                    case 6: return C6;
                    case 8: return C8;
                    case 9: return C9;
                    case 10: return C10;
                    case 11: return C11;
                    case 12: return C12;
                    case 13: return C13;
                    default: return C7;
                }
            }
        }

        private struct OdKey : IEquatable<OdKey>
        {
            public Entity Place;
            public Entity Area;
            public int Kind; // 0 destination, 1 origin

            public bool Equals(OdKey o) => Place == o.Place && Area == o.Area && Kind == o.Kind;
            public override int GetHashCode() => (Place.GetHashCode() * 397 ^ Area.GetHashCode()) * 31 + Kind;
        }

        private struct LinkKey : IEquatable<LinkKey>
        {
            public Entity A;
            public Entity B;
            // The bottleneck the drivers were stuck in: its junction, or the street when the queue runs into none.
            public Entity Source;

            public bool Equals(LinkKey o) => A == o.A && B == o.B && Source == o.Source;
            public override int GetHashCode() => (A.GetHashCode() * 397 ^ B.GetHashCode()) * 31 + Source.GetHashCode();
        }

        private struct LinkStats
        {
            public int Routes;
            public float Saving;
            public float Gap;
            // One example route between the two ends (for the map): points in m_LinkPoints, lanes in m_LinkLanes.
            public int PointStart, PointCount;
            public bool StartIsA;
        }

        /// <summary>A lane counted for a bottleneck place.</summary>
        internal struct PlaceLaneKey : IEquatable<PlaceLaneKey>
        {
            public Entity Place;
            public Entity Lane;

            public bool Equals(PlaceLaneKey o) => Place == o.Place && Lane == o.Lane;
            public override int GetHashCode() => Place.GetHashCode() * 397 ^ Lane.GetHashCode();
        }

        private struct Wait
        {
            public int Count;
            public uint LastFrame;
        }

        private static Entity Outside => new Entity { Index = kOutsideIndex, Version = 1 };

        private struct Origin
        {
            public Entity Target;
            public Entity Area;
            public float3 Position;
            public uint LastFrame;
            public bool Valid;
        }

        [BurstCompile]
        private struct HeadJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<Moving> m_MovingType;
            [ReadOnly] public ComponentTypeHandle<CarCurrentLane> m_CurrentLaneType;
            [ReadOnly] public ComponentTypeHandle<Transform> m_TransformType;
            [ReadOnly] public ComponentTypeHandle<Blocker> m_BlockerType;
            [ReadOnly] public ComponentTypeHandle<Target> m_TargetType;
            [ReadOnly] public ComponentTypeHandle<PathOwner> m_PathOwnerType;
            [ReadOnly] public BufferTypeHandle<PathElement> m_PathElementType;
            [ReadOnly] public BufferTypeHandle<CarNavigationLane> m_NavigationLaneType;
            [ReadOnly] public ComponentTypeHandle<PersonalCar> m_PersonalCarType;
            [ReadOnly] public ComponentTypeHandle<DeliveryTruck> m_DeliveryTruckType;
            [ReadOnly] public ComponentTypeHandle<Taxi> m_TaxiType;

            [ReadOnly] public ComponentLookup<Blocker> m_BlockerData;
            [ReadOnly] public ComponentLookup<Moving> m_MovingData;
            [ReadOnly] public ComponentLookup<CarCurrentLane> m_CurrentLaneData;
            [ReadOnly] public BufferLookup<CarNavigationLane> m_NavigationLaneData;
            [ReadOnly] public ComponentLookup<Transform> m_TransformData;
            [ReadOnly] public ComponentLookup<EdgeLane> m_EdgeLaneData;
            [ReadOnly] public ComponentLookup<CarLane> m_CarLaneData;
            [ReadOnly] public ComponentLookup<Owner> m_OwnerData;
            [ReadOnly] public ComponentLookup<Aggregated> m_AggregatedData;
            [ReadOnly] public BufferLookup<ConnectedEdge> m_ConnectedEdgeData;
            [ReadOnly] public BufferLookup<Game.Net.SubLane> m_SubLaneData;
            [ReadOnly] public ComponentLookup<PathOwner> m_PathOwnerData;
            [ReadOnly] public BufferLookup<PathElement> m_PathElementData;
            public NativeParallelHashMap<PlaceLaneKey, int> m_Downstream;
            [ReadOnly] public ComponentLookup<PropertyRenter> m_PropertyRenterData;
            [ReadOnly] public ComponentLookup<CurrentDistrict> m_CurrentDistrictData;
            [ReadOnly] public ComponentLookup<BorderDistrict> m_BorderDistrictData;
            [ReadOnly] public ComponentLookup<Building> m_BuildingData;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            [ReadOnly] public ComponentLookup<Edge> m_EdgeData;
            [ReadOnly] public ComponentLookup<NetNode> m_NodeData;
            [ReadOnly] public ComponentLookup<ParkedCar> m_ParkedCarData;
            [ReadOnly] public ComponentLookup<Creature> m_CreatureData;
            [ReadOnly] public ComponentLookup<HumanCurrentLane> m_HumanLaneData;
            [ReadOnly] public ComponentLookup<PedestrianLane> m_PedestrianLaneData;
            [ReadOnly] public ComponentLookup<Game.Net.Lane> m_NetLaneData;
            [ReadOnly] public ComponentLookup<Game.Net.MasterLane> m_MasterLaneData;
            [ReadOnly] public ComponentLookup<PublicTransport> m_PublicTransportData;
            [ReadOnly] public ComponentLookup<GarbageTruck> m_GarbageTruckData;
            [ReadOnly] public ComponentLookup<PostVan> m_PostVanData;
            [ReadOnly] public ComponentLookup<MaintenanceVehicle> m_MaintenanceData;
            [ReadOnly] public ComponentLookup<Hearse> m_HearseData;
            public NativeParallelHashMap<Entity, Wait> m_Waits;
            // Queued cars per (queue head place, road lane they stand on), and per (junction, junction lane they want).
            public NativeParallelHashMap<PlaceLaneKey, int> m_QueueLanes;
            public NativeParallelHashMap<PlaceLaneKey, int> m_JunctionMoves;
            public NativeList<float3> m_LinkPoints;
            public NativeList<Entity> m_LinkLanes;

            public NativeParallelHashMap<Entity, HeadInfo> m_Heads;
            // Each car's queue head and distance to it, filled while walking: a later car that reaches a known car stops
            // there, so a sample walks every queue once (was once per queued car: up to cars x 256 steps in gridlock).
            public NativeParallelHashMap<Entity, HeadLink> m_HeadOf;
            public NativeParallelHashMap<Entity, PlaceStats> m_Places;
            public NativeParallelHashMap<OdKey, int> m_Od;
            public NativeParallelHashMap<LinkKey, LinkStats> m_Links;
            public NativeParallelHashMap<Entity, Origin> m_Origins;
            public NativeArray<double> m_Stats;
            public uint m_Frame;
            public bool m_FirstSample;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<Moving> movings = chunk.GetNativeArray(ref m_MovingType);
                NativeArray<CarCurrentLane> lanes = chunk.GetNativeArray(ref m_CurrentLaneType);
                NativeArray<Transform> transforms = chunk.GetNativeArray(ref m_TransformType);
                NativeArray<Blocker> blockers = chunk.GetNativeArray(ref m_BlockerType);
                NativeArray<Target> targets = chunk.GetNativeArray(ref m_TargetType);
                NativeArray<PathOwner> pathOwners = chunk.GetNativeArray(ref m_PathOwnerType);
                BufferAccessor<PathElement> paths = chunk.GetBufferAccessor(ref m_PathElementType);
                BufferAccessor<CarNavigationLane> navs = chunk.GetBufferAccessor(ref m_NavigationLaneType);
                bool covered = chunk.Has(ref m_PersonalCarType) || chunk.Has(ref m_DeliveryTruckType) || chunk.Has(ref m_TaxiType);
                bool truckChunk = chunk.Has(ref m_DeliveryTruckType);
                var walked = new NativeList<Entity>(64, Allocator.Temp);

                for (int i = 0; i < entities.Length; i++)
                {
                    if (!m_CarLaneData.HasComponent(lanes[i].m_Lane)) continue; // not on a road
                    Entity vehicle = entities[i];
                    float3 position = transforms[i].m_Position;
                    m_Stats[0] += 1;

                    // Where was this trip first seen? Close to where it started, as cars are seen every 1024 frames.
                    bool hasOrigin = false;
                    Origin origin = default;
                    Entity target = Entity.Null;
                    if (covered && targets.Length != 0)
                    {
                        target = targets[i].m_Target;
                        if (m_Origins.TryGetValue(vehicle, out origin) && origin.Target == target)
                        {
                            origin.LastFrame = m_Frame;
                            // Trips start in parking lots and driveways (unnamed paths): the origin is the first named road.
                            if (origin.Area == Entity.Null)
                            {
                                origin.Area = AreaOfLane(lanes[i].m_Lane);
                                origin.Position = position;
                            }
                        }
                        else
                        {
                            bool fromOutside = NearMapEdge(position);
                            origin = new Origin
                            {
                                Target = target,
                                Area = fromOutside ? Outside : AreaOfLane(lanes[i].m_Lane),
                                Position = position,
                                LastFrame = m_Frame,
                                // A car seen in the first sample was already driving: its start is unknown.
                                Valid = !m_FirstSample,
                            };
                        }
                        m_Origins[vehicle] = origin;
                        hasOrigin = true;
                    }

                    Blocker blocker = blockers[i];
                    bool queued = blocker.m_Type == BlockerType.Continuing && blocker.m_Blocker != Entity.Null &&
                                  math.length(movings[i].m_Velocity) < 1f;
                    if (!queued) continue;
                    m_Stats[1] += 1;

                    // How long has it been queued? Samples in a row (a red light usually clears within one).
                    int waited = 1;
                    if (m_Waits.TryGetValue(vehicle, out Wait wait) && m_Frame - wait.LastFrame <= kSampleInterval * 3 / 2) waited = wait.Count + 1;
                    m_Waits[vehicle] = new Wait { Count = waited, LastFrame = m_Frame };
                    bool longWait = waited >= kLongWaitSamples;
                    if (longWait) m_Stats[16] += 1;

                    // Follow the cars ahead to the first one that isn't held up by another stopped or crawling car.
                    Entity head = vehicle;
                    int chain = 0;
                    walked.Clear();
                    while (chain < kMaxChain)
                    {
                        if (m_HeadOf.TryGetValue(head, out HeadLink known))
                        {
                            chain += known.Chain;
                            head = known.Head;
                            break;
                        }
                        walked.Add(head);
                        if (!m_BlockerData.TryGetComponent(head, out Blocker b)) break;
                        if (b.m_Type != BlockerType.Continuing || b.m_Blocker == Entity.Null) break;
                        if (!m_MovingData.TryGetComponent(b.m_Blocker, out Moving ahead) || !m_BlockerData.HasComponent(b.m_Blocker)) break;
                        // Stop-and-go traffic is one queue: the car ahead still counts while it moves well below its
                        // lane's limit.
                        float crawl = kCrawlSpeed;
                        if (m_CurrentLaneData.TryGetComponent(b.m_Blocker, out CarCurrentLane aheadLane) &&
                            m_CarLaneData.TryGetComponent(aheadLane.m_Lane, out CarLane aheadCarLane))
                        {
                            crawl = math.max(kCrawlSpeed, aheadCarLane.m_SpeedLimit * kCongestedShare);
                        }
                        if (math.length(ahead.m_Velocity) >= crawl) break;
                        head = b.m_Blocker;
                        chain++;
                    }
                    // Remember the head for every car passed on the way (its distance = total - its step).
                    for (int w = 0; w < walked.Length; w++)
                    {
                        if (!m_HeadOf.ContainsKey(walked[w])) m_HeadOf[walked[w]] = new HeadLink { Head = head, Chain = math.max(0, chain - w) };
                    }
                    chain = math.min(chain, kMaxChain);
                    if (chain >= kMaxChain) m_Stats[4] += 1;
                    m_Stats[2] += chain;
                    m_Stats[3] = math.max(m_Stats[3], chain);

                    if (!m_Heads.TryGetValue(head, out HeadInfo info))
                    {
                        info = Classify(head);
                        m_Heads[head] = info;
                        m_Stats[5] += 1;
                    }
                    if (info.Place == Entity.Null)
                    {
                        m_Stats[6] += 1;
                        continue;
                    }

                    m_Places.TryGetValue(info.Place, out PlaceStats place);
                    place.Queued++;
                    if (longWait) place.LongWait++;
                    place.AddCause(info.Cause);
                    place.ChainMax = math.max(place.ChainMax, chain);
                    place.Position = info.Position;
                    if (chain >= place.ChainMax) place.ExampleHead = head;
                    if (PlaceOfLane(lanes[i].m_Lane) != info.Place)
                    {
                        place.Spill++;
                        m_Stats[15] += 1;
                    }
                    if (truckChunk) place.Trucks++;
                    // Standing inside a junction that is not the one the queue runs into: the queue blocks it.
                    Entity inJunction = JunctionOf(lanes[i].m_Lane);
                    if (inJunction != Entity.Null && inJunction != info.Place && inJunction != info.Downstream)
                    {
                        place.BoxBlock++;
                        place.BoxNode = inJunction;
                    }

                    if (covered)
                    {
                        m_Stats[7] += 1;
                        bool destKnown = Destination(target, out Entity destArea, out float3 destPos);
                        if (destKnown && destArea != Entity.Null)
                        {
                            Add(new OdKey { Place = info.Place, Area = destArea, Kind = 0 });
                            m_Stats[8] += 1;
                        }
                        if (hasOrigin && !origin.Valid) m_Stats[10] += 1;
                        if (hasOrigin && origin.Valid && origin.Area != Entity.Null)
                        {
                            Add(new OdKey { Place = info.Place, Area = origin.Area, Kind = 1 });
                            m_Stats[9] += 1;
                            if (origin.Area.Index == kOutsideIndex) m_Stats[17] += 1;
                        }
                        if (destKnown && hasOrigin && origin.Valid)
                        {
                            place.OdKnown++;
                            m_Stats[14] += 1;
                            if (math.distance(destPos.xz, info.Position.xz) > kThroughDistance &&
                                math.distance(origin.Position.xz, info.Position.xz) > kThroughDistance)
                            {
                                place.Through++;
                                m_Stats[13] += 1;
                            }
                        }

                        if (m_Stats[11] < kLinkRoutesPerSample && pathOwners.Length != 0 && paths.Length != 0)
                        {
                            m_Stats[11] += 1;
                            DynamicBuffer<CarNavigationLane> nav = navs.Length != 0 ? navs[i] : default;
                            Entity source = m_NodeData.HasComponent(info.Place) ? info.Place : info.Downstream != Entity.Null ? info.Downstream : info.Place;
                            if (FindDetour(lanes[i].m_Lane, navs.Length != 0, nav, paths[i], pathOwners[i].m_ElementIndex, info.Position, source)) m_Stats[12] += 1;
                        }
                    }
                    m_Places[info.Place] = place;

                    // Lanes and turns: where the queue stands, and which way through the junction its cars want to go.
                    if (m_EdgeLaneData.HasComponent(lanes[i].m_Lane)) Count(m_QueueLanes, info.Place, lanes[i].m_Lane);
                    // The junction whose turns matter: the place itself, or for a street queue the junction it runs into.
                    Entity junctionNode = m_NodeData.HasComponent(info.Place) ? info.Place : info.Downstream;
                    if (junctionNode != Entity.Null)
                    {
                        if (junctionNode != info.Place) Count(m_Downstream, info.Place, junctionNode);
                        Entity wanted = WantedJunctionLane(lanes[i].m_Lane, navs.Length != 0 ? navs[i] : default, navs.Length != 0,
                            paths.Length != 0 ? paths[i] : default, paths.Length != 0, pathOwners.Length != 0 ? pathOwners[i].m_ElementIndex : 0, junctionNode);
                        if (wanted != Entity.Null) Count(m_JunctionMoves, info.Place, wanted);
                    }
                }
            }

            private static void Count(NativeParallelHashMap<PlaceLaneKey, int> map, Entity place, Entity lane)
            {
                var key = new PlaceLaneKey { Place = place, Lane = lane };
                map.TryGetValue(key, out int n);
                map[key] = n + 1;
            }

            /// <summary>The first lane inside the given junction on the car's way: the turn it is waiting to make.</summary>
            private Entity WantedJunctionLane(Entity current, DynamicBuffer<CarNavigationLane> nav, bool hasNav,
                DynamicBuffer<PathElement> path, bool hasPath, int elementIndex, Entity junction)
            {
                if (IsLaneOf(current, junction)) return current;
                if (hasNav)
                {
                    for (int k = 0; k < nav.Length && k < 8; k++)
                    {
                        if (IsLaneOf(nav[k].m_Lane, junction)) return nav[k].m_Lane;
                    }
                }
                if (hasPath)
                {
                    int end = math.min(path.Length, math.max(0, elementIndex) + 12);
                    for (int k = math.max(0, elementIndex); k < end; k++)
                    {
                        if (IsLaneOf(path[k].m_Target, junction)) return path[k].m_Target;
                    }
                }
                return Entity.Null;
            }

            private bool IsLaneOf(Entity lane, Entity junction) =>
                lane != Entity.Null && !m_EdgeLaneData.HasComponent(lane) && m_CarLaneData.HasComponent(lane) &&
                m_OwnerData.TryGetComponent(lane, out Owner owner) && owner.m_Owner == junction;

            private Entity PlaceOfLane(Entity lane)
            {
                if (!m_OwnerData.TryGetComponent(lane, out Owner owner)) return Entity.Null;
                if (m_AggregatedData.TryGetComponent(owner.m_Owner, out Aggregated aggregated) && aggregated.m_Aggregate != Entity.Null)
                {
                    return aggregated.m_Aggregate;
                }
                // A node where only two road pieces meet is a joint in the street, not a junction.
                if (m_ConnectedEdgeData.TryGetBuffer(owner.m_Owner, out DynamicBuffer<ConnectedEdge> edges) && edges.Length > 0 && edges.Length <= 2 &&
                    m_AggregatedData.TryGetComponent(edges[0].m_Edge, out Aggregated street) && street.m_Aggregate != Entity.Null)
                {
                    return street.m_Aggregate;
                }
                return owner.m_Owner;
            }

            private int CarLaneCount(Entity edge)
            {
                if (!m_SubLaneData.TryGetBuffer(edge, out DynamicBuffer<Game.Net.SubLane> lanes)) return 0;
                int n = 0;
                // Real lanes only: a group ("master") lane stands for several of them.
                for (int i = 0; i < lanes.Length; i++) if (m_CarLaneData.HasComponent(lanes[i].m_SubLane) && !m_MasterLaneData.HasComponent(lanes[i].m_SubLane)) n++;
                return n;
            }

            private void Add(OdKey key)
            {
                m_Od.TryGetValue(key, out int n);
                m_Od[key] = n + 1;
            }

            /// <summary>Where the queue starts and why. Junction lanes (no EdgeLane) are their junction's place.</summary>
            /// <summary>The first junction of 3+ roads on the car's way (its lane, its next lanes, then its path).</summary>
            private Entity DownstreamJunction(Entity car, Entity lane)
            {
                Entity n = JunctionOf(lane);
                if (n != Entity.Null) return n;
                if (m_NavigationLaneData.TryGetBuffer(car, out DynamicBuffer<CarNavigationLane> nav))
                {
                    for (int k = 0; k < nav.Length; k++)
                    {
                        n = JunctionOf(nav[k].m_Lane);
                        if (n != Entity.Null) return n;
                    }
                }
                if (m_PathOwnerData.TryGetComponent(car, out PathOwner owner) && m_PathElementData.TryGetBuffer(car, out DynamicBuffer<PathElement> path))
                {
                    int end = math.min(path.Length, math.max(0, owner.m_ElementIndex) + 24);
                    for (int k = math.max(0, owner.m_ElementIndex); k < end; k++)
                    {
                        n = JunctionOf(path[k].m_Target);
                        if (n != Entity.Null) return n;
                    }
                }
                return Entity.Null;
            }

            private Entity JunctionOf(Entity lane)
            {
                if (lane == Entity.Null || m_EdgeLaneData.HasComponent(lane) || !m_CarLaneData.HasComponent(lane)) return Entity.Null;
                if (!m_OwnerData.TryGetComponent(lane, out Owner owner)) return Entity.Null;
                return m_ConnectedEdgeData.TryGetBuffer(owner.m_Owner, out DynamicBuffer<ConnectedEdge> edges) && edges.Length >= 3 ? owner.m_Owner : Entity.Null;
            }

            private HeadInfo Classify(Entity head)
            {
                var info = new HeadInfo { Cause = 7 };
                if (m_TransformData.TryGetComponent(head, out Transform t)) info.Position = t.m_Position;
                if (!m_CurrentLaneData.TryGetComponent(head, out CarCurrentLane current)) return info;
                Entity lane = current.m_Lane;
                Entity next = Entity.Null;
                if (m_NavigationLaneData.TryGetBuffer(head, out DynamicBuffer<CarNavigationLane> nav) && nav.Length != 0) next = nav[0].m_Lane;

                // The junction lane it is on, or about to enter at the end of a road.
                Entity junctionLane = Entity.Null;
                if (!m_EdgeLaneData.HasComponent(lane)) junctionLane = lane;
                else if (next != Entity.Null && !m_EdgeLaneData.HasComponent(next) && m_CarLaneData.HasComponent(next)) junctionLane = next;
                info.Place = PlaceOfLane(junctionLane != Entity.Null ? junctionLane : lane);
                info.Downstream = DownstreamJunction(head, lane);

                BlockerType type = m_BlockerData.TryGetComponent(head, out Blocker b) ? b.m_Type : BlockerType.None;
                // Held up by a person: the game makes the pedestrian the car's blocker (type Temporary, sometimes
                // Crossing). On a crosswalk lane = a proper crossing; on an "unsafe" pedestrian lane, or on the road
                // itself = crossing where there is no crosswalk (jaywalking).
                if (type != BlockerType.Signal && b.m_Blocker != Entity.Null && m_CreatureData.HasComponent(b.m_Blocker))
                {
                    info.Cause = kCausePeopleJaywalk;
                    if (m_HumanLaneData.TryGetComponent(b.m_Blocker, out HumanCurrentLane walk) &&
                        m_PedestrianLaneData.TryGetComponent(walk.m_Lane, out PedestrianLane ped) &&
                        (ped.m_Flags & PedestrianLaneFlags.Unsafe) == 0)
                    {
                        info.Cause = kCausePeopleCrosswalk;
                    }
                    return info;
                }
                switch (type)
                {
                    case BlockerType.Signal: info.Cause = 0; return info;
                    case BlockerType.Oncoming: info.Cause = 1; return info;
                    case BlockerType.Crossing: info.Cause = 2; return info;
                    case BlockerType.Spawn: info.Cause = 6; return info;
                }
                // A bus or tram at a stop, or a service vehicle stopped in the lane: the queue's head itself (the chain
                // follows stopped vehicles) or what holds the head up.
                int stopping = StoppingKind(head);
                if (stopping == 0 && b.m_Blocker != Entity.Null) stopping = StoppingKind(b.m_Blocker);
                if (stopping != 0)
                {
                    info.Cause = stopping == 1 ? 11 : 12;
                    return info;
                }
                // Parking in or pulling out of a roadside space, or held up by a parked car.
                if ((current.m_LaneFlags & Game.Vehicles.CarLaneFlags.ParkingSpace) != 0 ||
                    (b.m_Blocker != Entity.Null && m_ParkedCarData.HasComponent(b.m_Blocker)))
                {
                    info.Cause = 8;
                    return info;
                }
                // Waiting to merge: the head is on a road lane that ends where another lane ends (lanes merge, often just
                // before a junction). Cars must find a gap in the next lane, which stalls the queue.
                if (m_EdgeLaneData.HasComponent(lane) && IsMerging(lane))
                {
                    // On a highway: cars from a ramp merging in.
                    info.Cause = m_CarLaneData.TryGetComponent(lane, out CarLane ml) && (ml.m_Flags & CarLaneFlags.Highway) != 0 ? 13 : 4;
                    return info;
                }
                if (junctionLane != Entity.Null)
                {
                    CarLaneFlags flags = m_CarLaneData.TryGetComponent(junctionLane, out CarLane carLane) ? carLane.m_Flags : 0;
                    if ((flags & (CarLaneFlags.Yield | CarLaneFlags.Stop)) != 0)
                    {
                        info.Cause = 3;
                        return info;
                    }
                    int roads = 0;
                    bool narrows = false;
                    if (m_OwnerData.TryGetComponent(junctionLane, out Owner node) &&
                        m_ConnectedEdgeData.TryGetBuffer(node.m_Owner, out DynamicBuffer<ConnectedEdge> edges))
                    {
                        roads = edges.Length;
                        // A joint between two road pieces: only a cause when the road has fewer lanes after it.
                        if (roads == 2) narrows = CarLaneCount(edges[0].m_Edge) != CarLaneCount(edges[1].m_Edge);
                    }
                    info.Cause = roads >= 3 ? 5 : narrows ? 4 : 7;
                    return info;
                }
                info.Cause = 7;
                return info;
            }

            /// <summary>1 = a bus or tram (public transport), 2 = a service vehicle that stops at buildings; 0 = neither,
            /// or it is moving.</summary>
            private int StoppingKind(Entity e)
            {
                if (!m_MovingData.TryGetComponent(e, out Moving mv) || math.length(mv.m_Velocity) >= 1f) return 0;
                if (m_PublicTransportData.HasComponent(e)) return 1;
                if (m_GarbageTruckData.HasComponent(e) || m_PostVanData.HasComponent(e) || m_MaintenanceData.HasComponent(e) || m_HearseData.HasComponent(e)) return 2;
                return 0;
            }

            /// <summary>Another car lane of the same road piece ends at the same point as this one: they merge.</summary>
            private bool IsMerging(Entity lane)
            {
                if (!m_NetLaneData.TryGetComponent(lane, out Game.Net.Lane me) || !m_OwnerData.TryGetComponent(lane, out Owner owner)) return false;
                if (!m_SubLaneData.TryGetBuffer(owner.m_Owner, out DynamicBuffer<Game.Net.SubLane> lanes)) return false;
                for (int i = 0; i < lanes.Length; i++)
                {
                    Entity other = lanes[i].m_SubLane;
                    if (other == lane || !m_CarLaneData.HasComponent(other) || !m_EdgeLaneData.HasComponent(other) || m_MasterLaneData.HasComponent(other)) continue;
                    if (m_NetLaneData.TryGetComponent(other, out Game.Net.Lane o) && o.m_EndNode.Equals(me.m_EndNode)) return true;
                }
                return false;
            }

            private static bool NearMapEdge(float3 p) => math.cmax(math.abs(p.xz)) > kMapHalfSize - kBorderMargin;

            /// <summary>District of a named road, else the road's street. Null off named roads (parking lots, driveways).</summary>
            private Entity AreaOfLane(Entity lane)
            {
                if (!m_OwnerData.TryGetComponent(lane, out Owner owner)) return Entity.Null;
                // Paths inside a building's grounds (cargo station, parking lot) belong to the building: name it.
                if (m_OwnerData.TryGetComponent(owner.m_Owner, out Owner site) && site.m_Owner != Entity.Null)
                {
                    Entity top = site.m_Owner;
                    for (int k = 0; k < 4 && m_OwnerData.TryGetComponent(top, out Owner up) && up.m_Owner != Entity.Null; k++) top = up.m_Owner;
                    return top;
                }
                if (!m_AggregatedData.TryGetComponent(owner.m_Owner, out Aggregated aggregated) || aggregated.m_Aggregate == Entity.Null) return Entity.Null;
                if (m_BorderDistrictData.TryGetComponent(owner.m_Owner, out BorderDistrict d))
                {
                    if (d.m_Left != Entity.Null) return d.m_Left;
                    if (d.m_Right != Entity.Null) return d.m_Right;
                }
                return aggregated.m_Aggregate;
            }

            /// <summary>The destination's district, or else the street its building stands on.</summary>
            private bool Destination(Entity target, out Entity area, out float3 position)
            {
                area = Entity.Null;
                position = default;
                if (target == Entity.Null) return false;
                Entity building = target;
                if (m_PropertyRenterData.TryGetComponent(target, out PropertyRenter renter)) building = renter.m_Property;
                if (!m_TransformData.TryGetComponent(building, out Transform t)) return false;
                position = t.m_Position;
                if (NearMapEdge(position))
                {
                    area = Outside;
                    m_Stats[18] += 1;
                    return true;
                }
                if (m_CurrentDistrictData.TryGetComponent(building, out CurrentDistrict district) && district.m_District != Entity.Null)
                {
                    area = district.m_District;
                }
                else if (m_BuildingData.TryGetComponent(building, out Building b) &&
                         m_AggregatedData.TryGetComponent(b.m_RoadEdge, out Aggregated aggregated))
                {
                    area = aggregated.m_Aggregate;
                }
                return true;
            }

            /// <summary>
            /// Walks the route ahead (end point of each lane) and keeps the biggest detour: two points at most
            /// kLinkMaxGap apart that the route takes at least kLinkMinRoute and kLinkMinRatio times that to connect.
            /// </summary>
            /// <summary>
            /// A detour on this queued car's route that goes round its own queue: the long way between two close points
            /// must pass the head of the queue (within kBypassDistance). A new road there takes the driver out of the
            /// queue; detours elsewhere on the route (a winding road further on) don't help this bottleneck.
            /// </summary>
            private bool FindDetour(Entity current, bool hasNav, DynamicBuffer<CarNavigationLane> nav, DynamicBuffer<PathElement> path, int elementIndex,
                float3 headPos, Entity source)
            {
                var points = new NativeArray<float3>(kMaxRoutePoints, Allocator.Temp);
                var along = new NativeArray<float>(kMaxRoutePoints, Allocator.Temp);
                var pointLanes = new NativeArray<Entity>(kMaxRoutePoints, Allocator.Temp);
                int n = 0;
                float length = 0f;
                AddPoint(current, ref n, ref length, points, along, pointLanes);
                if (hasNav)
                {
                    for (int k = 0; k < nav.Length && n < kMaxRoutePoints && length < kMaxRouteLength; k++) AddPoint(nav[k].m_Lane, ref n, ref length, points, along, pointLanes);
                }
                for (int k = math.max(0, elementIndex); k < path.Length && n < kMaxRoutePoints && length < kMaxRouteLength; k++)
                {
                    AddPoint(path[k].m_Target, ref n, ref length, points, along, pointLanes);
                }

                // Where the route passes the head of the queue (the first route point near it).
                int headAt = -1;
                for (int k = 0; k < n; k++)
                {
                    if (math.distance(points[k].xz, headPos.xz) <= kBypassDistance)
                    {
                        headAt = k;
                        break;
                    }
                }
                float best = 0f, bestGap = 0f;
                int bi = -1, bj = -1;
                for (int a = 0; a <= headAt; a++)
                {
                    for (int c = math.max(a + 2, headAt + 1); c < n; c++)
                    {
                        float route = along[c] - along[a];
                        if (route < kLinkMinRoute) continue;
                        float gap = math.distance(points[a].xz, points[c].xz);
                        if (gap > kLinkMaxGap || route < kLinkMinRatio * gap) continue;
                        // A bridge or tunnel over the other road: they can't simply be joined.
                        if (math.abs(points[a].y - points[c].y) > kLinkMaxHeight) continue;
                        if (route - gap > best)
                        {
                            best = route - gap;
                            bestGap = gap;
                            bi = a;
                            bj = c;
                        }
                    }
                }
                bool found = false;
                if (bi >= 0)
                {
                    Entity na = NodeNear(pointLanes[bi], points[bi]);
                    Entity nb = NodeNear(pointLanes[bj], points[bj]);
                    if (na != Entity.Null && nb != Entity.Null && na != nb)
                    {
                        var key = na.Index < nb.Index ? new LinkKey { A = na, B = nb, Source = source } : new LinkKey { A = nb, B = na, Source = source };
                        if (!m_Links.TryGetValue(key, out LinkStats s))
                        {
                            // Keep the first route of this sample as the example of the detour.
                            s.PointStart = m_LinkPoints.Length;
                            for (int k = bi; k <= bj; k++)
                            {
                                m_LinkPoints.Add(points[k]);
                                m_LinkLanes.Add(pointLanes[k]);
                            }
                            s.PointCount = bj - bi + 1;
                            s.StartIsA = key.A == na;
                        }
                        s.Routes++;
                        s.Saving += best;
                        s.Gap += bestGap;
                        m_Links[key] = s;
                        found = true;
                    }
                }
                points.Dispose();
                along.Dispose();
                pointLanes.Dispose();
                return found;
            }

            private void AddPoint(Entity lane, ref int n, ref float length, NativeArray<float3> points, NativeArray<float> along, NativeArray<Entity> pointLanes)
            {
                if (n >= kMaxRoutePoints || !m_CurveData.TryGetComponent(lane, out Curve curve)) return;
                length += curve.m_Length;
                points[n] = curve.m_Bezier.d;
                along[n] = length;
                pointLanes[n] = lane;
                n++;
            }

            /// <summary>The road node nearest to a point at the end of a lane: its junction, or the closer end of its road.</summary>
            private Entity NodeNear(Entity lane, float3 point)
            {
                if (!m_OwnerData.TryGetComponent(lane, out Owner owner)) return Entity.Null;
                if (m_EdgeData.TryGetComponent(owner.m_Owner, out Edge edge))
                {
                    if (!m_NodeData.TryGetComponent(edge.m_Start, out NetNode s) || !m_NodeData.TryGetComponent(edge.m_End, out NetNode e)) return Entity.Null;
                    return math.distancesq(s.m_Position, point) <= math.distancesq(e.m_Position, point) ? edge.m_Start : edge.m_End;
                }
                return m_NodeData.HasComponent(owner.m_Owner) ? owner.m_Owner : Entity.Null;
            }
        }

        [BurstCompile]
        private struct PruneOriginsJob : IJob
        {
            public NativeParallelHashMap<Entity, Origin> m_Origins;
            public NativeParallelHashMap<Entity, Wait> m_Waits;
            public uint m_Frame;

            public void Execute()
            {
                NativeArray<Entity> waitKeys = m_Waits.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < waitKeys.Length; i++)
                {
                    if (m_Frame - m_Waits[waitKeys[i]].LastFrame > kSampleInterval * 2) m_Waits.Remove(waitKeys[i]);
                }
                waitKeys.Dispose();
                NativeArray<Entity> keys = m_Origins.GetKeyArray(Allocator.Temp);
                for (int i = 0; i < keys.Length; i++)
                {
                    if (m_Frame - m_Origins[keys[i]].LastFrame > kOriginForgetFrames) m_Origins.Remove(keys[i]);
                }
                keys.Dispose();
            }
        }

        // ---------------- Hour totals (main thread) ----------------

        private sealed class PlaceHour
        {
            public double Queued, LongWait, Spill, Through, OdKnown, Trucks, BoxBlock;
            public Entity BoxNode;
            public int ChainMax;
            public readonly double[] Causes = new double[kCauseCount];
            public Entity ExampleHead;
            public float3 Position;
        }

        private sealed class LinkHour
        {
            public int Routes;
            public double Saving, Gap;
            public List<float3> Points;
            public List<Entity> Lanes;
            public bool StartIsA;
        }

        private sealed class Hour
        {
            public int Samples;
            public double Ms;
            public readonly double[] Stats = new double[kStatCount];
            public int ChainMax;
            public readonly Dictionary<Entity, PlaceHour> Places = new Dictionary<Entity, PlaceHour>();
            public readonly Dictionary<OdKey, int> Od = new Dictionary<OdKey, int>();
            public readonly Dictionary<LinkKey, LinkHour> Links = new Dictionary<LinkKey, LinkHour>();
            public readonly Dictionary<PlaceLaneKey, int> QueueLanes = new Dictionary<PlaceLaneKey, int>();
            public readonly Dictionary<PlaceLaneKey, int> JunctionMoves = new Dictionary<PlaceLaneKey, int>();
            public readonly Dictionary<PlaceLaneKey, int> Downstream = new Dictionary<PlaceLaneKey, int>();
        }

        private SimulationSystem m_SimulationSystem;
        private TimeSystem m_TimeSystem;
        private NameSystem m_NameSystem;
        private TurnFlowSystem m_TurnFlow;
        private DespawnCounterSystem m_DespawnCounter;
        private EntityQuery m_VehicleQuery;

        private NativeParallelHashMap<Entity, HeadInfo> m_Heads;
        private NativeParallelHashMap<Entity, HeadLink> m_HeadOf;
        private NativeParallelHashMap<Entity, PlaceStats> m_Places;
        private NativeParallelHashMap<OdKey, int> m_Od;
        private NativeParallelHashMap<LinkKey, LinkStats> m_Links;
        private NativeParallelHashMap<Entity, Origin> m_Origins;
        private NativeParallelHashMap<Entity, Wait> m_Waits;
        private NativeParallelHashMap<PlaceLaneKey, int> m_QueueLanes;
        private NativeParallelHashMap<PlaceLaneKey, int> m_JunctionMoves;
        private NativeParallelHashMap<PlaceLaneKey, int> m_Downstream;
        private NativeList<float3> m_LinkPoints;
        private NativeList<Entity> m_LinkLanes;
        private NativeArray<double> m_Stats;
        private JobHandle m_LastJob;
        private bool m_Pending;
        private bool m_Running;
        private bool m_FirstSample = true;
        private DateTime m_HourKey = DateTime.MinValue;
        private Hour m_Hour = new Hour();
        private double m_PendingMs;

        /// <summary>The last finished hour for the panel (JSON), and a counter that changes with it.</summary>
        internal static string Json = "{\"time\":\"\",\"rows\":[],\"links\":[]}";
        internal static int Version;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => kSampleInterval;

        protected override void OnCreate()
        {
            base.OnCreate();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();
            m_NameSystem = World.GetOrCreateSystemManaged<NameSystem>();
            m_TurnFlow = World.GetOrCreateSystemManaged<TurnFlowSystem>();
            m_DespawnCounter = World.GetOrCreateSystemManaged<DespawnCounterSystem>();
            m_VehicleQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Car>(), ComponentType.ReadOnly<Moving>(), ComponentType.ReadOnly<CarCurrentLane>(),
                    ComponentType.ReadOnly<Transform>(), ComponentType.ReadOnly<Blocker>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(), ComponentType.ReadOnly<Temp>(), ComponentType.ReadOnly<Unspawned>(),
                    ComponentType.ReadOnly<Bicycle>(), ComponentType.ReadOnly<ParkedCar>(),
                },
            });
            m_Heads = new NativeParallelHashMap<Entity, HeadInfo>(4096, Allocator.Persistent);
            m_HeadOf = new NativeParallelHashMap<Entity, HeadLink>(16384, Allocator.Persistent);
            m_Places = new NativeParallelHashMap<Entity, PlaceStats>(1024, Allocator.Persistent);
            m_Od = new NativeParallelHashMap<OdKey, int>(4096, Allocator.Persistent);
            m_Links = new NativeParallelHashMap<LinkKey, LinkStats>(512, Allocator.Persistent);
            m_Origins = new NativeParallelHashMap<Entity, Origin>(16384, Allocator.Persistent);
            m_Waits = new NativeParallelHashMap<Entity, Wait>(4096, Allocator.Persistent);
            m_QueueLanes = new NativeParallelHashMap<PlaceLaneKey, int>(2048, Allocator.Persistent);
            m_JunctionMoves = new NativeParallelHashMap<PlaceLaneKey, int>(1024, Allocator.Persistent);
            m_Downstream = new NativeParallelHashMap<PlaceLaneKey, int>(256, Allocator.Persistent);
            m_LinkPoints = new NativeList<float3>(4096, Allocator.Persistent);
            m_LinkLanes = new NativeList<Entity>(4096, Allocator.Persistent);
            m_Stats = new NativeArray<double>(kStatCount, Allocator.Persistent);
            Logger.Info("BottleneckSystem created.");
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Heads.Dispose();
            m_HeadOf.Dispose();
            m_Places.Dispose();
            m_Od.Dispose();
            m_Links.Dispose();
            m_Origins.Dispose();
            m_Waits.Dispose();
            m_QueueLanes.Dispose();
            m_JunctionMoves.Dispose();
            m_Downstream.Dispose();
            m_LinkPoints.Dispose();
            m_LinkLanes.Dispose();
            m_Stats.Dispose();
            base.OnDestroy();
        }

        protected override void OnGamePreload(Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);
            Reset();
            Json = "{\"time\":\"\",\"rows\":[],\"links\":[]}";
            Version++;
        }

        private void Reset()
        {
            m_LastJob.Complete();
            m_Pending = false;
            m_Origins.Clear();
            m_Waits.Clear();
            ClearSample();
            m_Hour = new Hour();
            m_HourKey = DateTime.MinValue;
            m_FirstSample = true;
            m_Window.Clear();
        }

        private void ClearSample()
        {
            m_Heads.Clear();
            m_HeadOf.Clear();
            m_Places.Clear();
            m_Od.Clear();
            m_Links.Clear();
            m_QueueLanes.Clear();
            m_JunctionMoves.Clear();
            m_Downstream.Clear();
            m_LinkPoints.Clear();
            m_LinkLanes.Clear();
            for (int i = 0; i < kStatCount; i++) m_Stats[i] = 0;
        }

        protected override void OnUpdate()
        {
            LiveTrafficSettings settings = Mod.Settings;
            bool wanted = settings != null && settings.ShowStatistics;
            if (!wanted)
            {
                if (m_Running)
                {
                    Reset();
                    m_Running = false;
                    Logger.Info("Bottleneck finder OFF (statistics panel off).");
                }
                return;
            }
            if (!m_Running)
            {
                m_Running = true;
                Logger.Info("Bottleneck finder ON.");
            }

            // 1. Fold in last sample.
            m_LastJob.Complete();
            if (m_Pending)
            {
                FoldSample();
                m_Pending = false;
            }

            // 2. New game hour: publish the finished one (the first one started before we counted: dropped).
            DateTime now = m_TimeSystem.GetCurrentDateTime();
            var hourKey = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
            if (hourKey != m_HourKey)
            {
                // Cars the game removed (stuck or no route) per place this hour; dropped with an hour that isn't published.
                Dictionary<Entity, int> despawns = m_DespawnCounter.HarvestPlaces();
                if (m_HourKey != DateTime.MinValue && m_Hour.Samples >= 3)
                {
                    try
                    {
                        FinishHour(m_HourKey, despawns);
                    }
                    catch (Exception e)
                    {
                        Logger.Warn($"Bottleneck finder: could not finish the hour: {e}");
                    }
                }
                m_Hour = new Hour();
                m_HourKey = hourKey;
            }

            // 3. Next sample.
            uint frame = m_SimulationSystem.frameIndex;
            ClearSample();
            var job = new HeadJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_MovingType = GetComponentTypeHandle<Moving>(true),
                m_CurrentLaneType = GetComponentTypeHandle<CarCurrentLane>(true),
                m_TransformType = GetComponentTypeHandle<Transform>(true),
                m_BlockerType = GetComponentTypeHandle<Blocker>(true),
                m_TargetType = GetComponentTypeHandle<Target>(true),
                m_PathOwnerType = GetComponentTypeHandle<PathOwner>(true),
                m_PathElementType = GetBufferTypeHandle<PathElement>(true),
                m_NavigationLaneType = GetBufferTypeHandle<CarNavigationLane>(true),
                m_PersonalCarType = GetComponentTypeHandle<PersonalCar>(true),
                m_DeliveryTruckType = GetComponentTypeHandle<DeliveryTruck>(true),
                m_TaxiType = GetComponentTypeHandle<Taxi>(true),
                m_BlockerData = GetComponentLookup<Blocker>(true),
                m_MovingData = GetComponentLookup<Moving>(true),
                m_CurrentLaneData = GetComponentLookup<CarCurrentLane>(true),
                m_NavigationLaneData = GetBufferLookup<CarNavigationLane>(true),
                m_TransformData = GetComponentLookup<Transform>(true),
                m_EdgeLaneData = GetComponentLookup<EdgeLane>(true),
                m_CarLaneData = GetComponentLookup<CarLane>(true),
                m_OwnerData = GetComponentLookup<Owner>(true),
                m_AggregatedData = GetComponentLookup<Aggregated>(true),
                m_ConnectedEdgeData = GetBufferLookup<ConnectedEdge>(true),
                m_SubLaneData = GetBufferLookup<Game.Net.SubLane>(true),
                m_PathOwnerData = GetComponentLookup<PathOwner>(true),
                m_PathElementData = GetBufferLookup<PathElement>(true),
                m_Downstream = m_Downstream,
                m_PropertyRenterData = GetComponentLookup<PropertyRenter>(true),
                m_CurrentDistrictData = GetComponentLookup<CurrentDistrict>(true),
                m_BorderDistrictData = GetComponentLookup<BorderDistrict>(true),
                m_BuildingData = GetComponentLookup<Building>(true),
                m_CurveData = GetComponentLookup<Curve>(true),
                m_EdgeData = GetComponentLookup<Edge>(true),
                m_NodeData = GetComponentLookup<NetNode>(true),
                m_ParkedCarData = GetComponentLookup<ParkedCar>(true),
                m_CreatureData = GetComponentLookup<Creature>(true),
                m_HumanLaneData = GetComponentLookup<HumanCurrentLane>(true),
                m_PedestrianLaneData = GetComponentLookup<PedestrianLane>(true),
                m_NetLaneData = GetComponentLookup<Game.Net.Lane>(true),
                m_MasterLaneData = GetComponentLookup<Game.Net.MasterLane>(true),
                m_PublicTransportData = GetComponentLookup<PublicTransport>(true),
                m_GarbageTruckData = GetComponentLookup<GarbageTruck>(true),
                m_PostVanData = GetComponentLookup<PostVan>(true),
                m_MaintenanceData = GetComponentLookup<MaintenanceVehicle>(true),
                m_HearseData = GetComponentLookup<Hearse>(true),
                m_Waits = m_Waits,
                m_QueueLanes = m_QueueLanes,
                m_JunctionMoves = m_JunctionMoves,
                m_LinkPoints = m_LinkPoints,
                m_LinkLanes = m_LinkLanes,
                m_Heads = m_Heads,
                m_HeadOf = m_HeadOf,
                m_Places = m_Places,
                m_Od = m_Od,
                m_Links = m_Links,
                m_Origins = m_Origins,
                m_Stats = m_Stats,
                m_Frame = frame,
                m_FirstSample = m_FirstSample,
            };
            System.Diagnostics.Stopwatch timer = null;
            JobHandle dependency = Dependency;
            if (Logger.DetailOn)
            {
                // Measure the cost for the log: run it to completion now (only while the detailed log is on).
                dependency.Complete();
                timer = System.Diagnostics.Stopwatch.StartNew();
            }
            JobHandle handle = job.Schedule(m_VehicleQuery, dependency);
            handle = new PruneOriginsJob { m_Origins = m_Origins, m_Waits = m_Waits, m_Frame = frame }.Schedule(handle);
            if (timer != null)
            {
                handle.Complete();
                m_PendingMs = timer.Elapsed.TotalMilliseconds;
            }
            else m_PendingMs = -1;
            m_FirstSample = false;
            m_Pending = true;
            m_LastJob = handle;
            Dependency = handle;
        }

        private void FoldSample()
        {
            Hour h = m_Hour;
            h.Samples++;
            if (m_PendingMs >= 0) h.Ms += m_PendingMs;
            for (int i = 0; i < kStatCount; i++) h.Stats[i] += m_Stats[i];
            h.ChainMax = Math.Max(h.ChainMax, (int)m_Stats[3]);
            using (NativeKeyValueArrays<Entity, PlaceStats> kv = m_Places.GetKeyValueArrays(Allocator.Temp))
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    PlaceStats s = kv.Values[i];
                    if (!h.Places.TryGetValue(kv.Keys[i], out PlaceHour p)) h.Places[kv.Keys[i]] = p = new PlaceHour();
                    p.Queued += s.Queued;
                    p.LongWait += s.LongWait;
                    p.Spill += s.Spill;
                    p.Through += s.Through;
                    p.OdKnown += s.OdKnown;
                    p.Trucks += s.Trucks;
                    p.BoxBlock += s.BoxBlock;
                    if (s.BoxNode != Entity.Null) p.BoxNode = s.BoxNode;
                    for (int c = 0; c < kCauseCount; c++) p.Causes[c] += s.Cause(c);
                    if (s.ChainMax >= p.ChainMax)
                    {
                        p.ChainMax = s.ChainMax;
                        p.ExampleHead = s.ExampleHead;
                    }
                    p.Position = s.Position;
                }
            }
            using (NativeKeyValueArrays<OdKey, int> kv = m_Od.GetKeyValueArrays(Allocator.Temp))
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    h.Od.TryGetValue(kv.Keys[i], out int n);
                    h.Od[kv.Keys[i]] = n + kv.Values[i];
                }
            }
            using (NativeKeyValueArrays<LinkKey, LinkStats> kv = m_Links.GetKeyValueArrays(Allocator.Temp))
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    if (!h.Links.TryGetValue(kv.Keys[i], out LinkHour l)) h.Links[kv.Keys[i]] = l = new LinkHour();
                    l.Routes += kv.Values[i].Routes;
                    l.Saving += kv.Values[i].Saving;
                    l.Gap += kv.Values[i].Gap;
                    LinkStats ls = kv.Values[i];
                    if (l.Points == null && ls.PointCount > 1 && ls.PointStart + ls.PointCount <= m_LinkPoints.Length)
                    {
                        l.Points = new List<float3>(ls.PointCount);
                        l.Lanes = new List<Entity>(ls.PointCount);
                        for (int k = 0; k < ls.PointCount; k++)
                        {
                            l.Points.Add(m_LinkPoints[ls.PointStart + k]);
                            l.Lanes.Add(m_LinkLanes[ls.PointStart + k]);
                        }
                        l.StartIsA = ls.StartIsA;
                    }
                }
            }
            AddCounts(m_QueueLanes, h.QueueLanes);
            AddCounts(m_JunctionMoves, h.JunctionMoves);
            AddCounts(m_Downstream, h.Downstream);
        }

        private static void AddCounts(NativeParallelHashMap<PlaceLaneKey, int> from, Dictionary<PlaceLaneKey, int> into)
        {
            using (NativeKeyValueArrays<PlaceLaneKey, int> kv = from.GetKeyValueArrays(Allocator.Temp))
            {
                for (int i = 0; i < kv.Length; i++)
                {
                    into.TryGetValue(kv.Keys[i], out int n);
                    into[kv.Keys[i]] = n + kv.Values[i];
                }
            }
        }

        // ---------------- Hour summary: names, panel, files, log ----------------

        private sealed class Row
        {
            public Entity Place;
            public string Name, Kind, Cause, To, From;
            public double Queued, LongWait, SpillPct, ThroughPct, CausePct, OdKnown;
            public int ChainMax;
            public Entity ExampleHead;
            public string CauseMix;
            public int TopCause;
            public LaneDiagnosis Lanes;
            public Entity EndsAt;          // street queue: the junction it runs into
            public string EndsAtName;
            public List<AreaShare> ToList, FromList;
            public int HoursIn;            // ranking: game hours of the window it was on the list
            public double AvgWait;         // ranking: stuck cars per hour over the whole window
            public double Despawns;        // cars the game removed here (stuck or no route): in the hour, or per hour in the ranking
            public double[] CauseShares;   // share of queued cars per cause (kCauseNames order)
            public double TruckPct;        // trucks among the queued vehicles
            public double BoxBlock;        // queued cars standing inside another junction, per sample
            public Entity BoxNode;         // that junction
            // Ranking: is it still there? HoursSince = game hours since it was last on the hourly list; RecentWait = stuck
            // cars per hour over the last kRecentHours; Status = "cleared" (gone for kClearedHours, maybe fixed),
            // "better" or "worse" (recent hours against the window average), or "".
            public int HoursSince;
            public double RecentWait;
            public string Status = "";
            public List<KeyValuePair<string, Entity>> QueuesOn = new List<KeyValuePair<string, Entity>>();

            public Row Copy() => (Row)MemberwiseClone();
        }

        // Shown in the ranking only above these: a little queuing is normal city traffic.
        private const double kMinAvgWait = 5;
        private const double kMinLinkDrivers = 15;
        private const int kRecentHours = 3;
        private const int kClearedHours = 3;

        private sealed class LinkRow
        {
            public Entity A, B;
            public string NameA, NameB;
            public double Gap, Saving;
            public int Routes;
            public bool SameStreet;
            public List<float3> Points;
            public List<Entity> Lanes;
            public int Kind;          // kNewLink, kTurnAround, kMissingTurn
            public bool NeedsRamp;    // a new link to a highway: needs a ramp or interchange
            public Entity FixAt;      // where drivers leave the road they wanted to stay on (turn problems)
            public string FixName;
            public Entity End;              // where the detour rejoins (driving order)
            public string FromName, ToName, Via;
            public Entity FromEntity, ToEntity;
            public List<KeyValuePair<string, Entity>> ViaList = new List<KeyValuePair<string, Entity>>();
            public int HoursIn, TotalRoutes;
            public Entity Source;           // the bottleneck the drivers were stuck in
            public string ForId, ForName;   // the listed bottleneck it belongs to (set when shown)

            public LinkRow Copy() => (LinkRow)MemberwiseClone();
        }

        internal const int kNewLink = 0, kTurnAround = 1, kMissingTurn = 2;

        private void FinishHour(DateTime hourKey, Dictionary<Entity, int> despawns)
        {
            Hour h = m_Hour;
            double samples = Math.Max(1, h.Samples);
            string time = hourKey.ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);

            var rows = new List<Row>();
            // Ranked by cars that waited longer than a light cycle: a red light queue is normal, a queue that stays is not.
            foreach (KeyValuePair<Entity, PlaceHour> kv in h.Places.OrderByDescending(p => p.Value.LongWait).ThenByDescending(p => p.Value.Queued))
            {
                if (rows.Count >= kTrackRows) break;
                if (kv.Value.LongWait <= 0) break;
                if (!EntityManager.Exists(kv.Key)) continue;
                string kind = EntityManager.HasComponent<Aggregate>(kv.Key) ? "street" : EntityManager.HasComponent<NetNode>(kv.Key) ? "junction" : null;
                if (kind == null) continue; // parking lots, buildings
                PlaceHour p = kv.Value;
                double causeTotal = Math.Max(1, p.Causes.Sum());
                int top = Array.IndexOf(p.Causes, p.Causes.Max());
                var mix = new List<string>();
                for (int c = 0; c < kCauseCount; c++)
                {
                    if (p.Causes[c] / causeTotal >= 0.1) mix.Add($"{kCauseNames[c]} {p.Causes[c] / causeTotal * 100:0}%");
                }
                rows.Add(new Row
                {
                    Place = kv.Key,
                    Name = PlaceName(kv.Key),
                    Kind = kind,
                    Cause = kCauseNames[top],
                    CausePct = p.Causes[top] / causeTotal * 100,
                    CauseMix = string.Join(", ", mix),
                    Queued = p.Queued / samples,
                    LongWait = p.LongWait / samples,
                    SpillPct = p.Queued > 0 ? p.Spill / p.Queued * 100 : 0,
                    ThroughPct = p.OdKnown > 0 ? p.Through / p.OdKnown * 100 : -1,
                    OdKnown = p.OdKnown,
                    ToList = TopAreaList(h, kv.Key, 0),
                    FromList = TopAreaList(h, kv.Key, 1),
                    ChainMax = p.ChainMax,
                    ExampleHead = p.ExampleHead,
                    TopCause = top,
                    CauseShares = p.Causes.Select(c => c / causeTotal).ToArray(),
                    TruckPct = p.Queued > 0 ? p.Trucks / p.Queued * 100 : 0,
                    BoxBlock = p.BoxBlock / samples,
                    BoxNode = p.BoxNode,
                });
            }
            foreach (Row r in rows)
            {
                r.Despawns = despawns.TryGetValue(r.Place, out int removed) ? removed : 0;
                r.To = AreasText(r.ToList);
                r.From = AreasText(r.FromList);
                // A street queue: the junction it runs into (where the head cars wait to go through).
                if (!EntityManager.HasComponent<NetNode>(r.Place))
                {
                    var ends = h.Downstream.Where(x => x.Key.Place == r.Place && EntityManager.Exists(x.Key.Lane)).OrderByDescending(x => x.Value).FirstOrDefault();
                    if (ends.Value > 0)
                    {
                        r.EndsAt = ends.Key.Lane;
                        r.EndsAtName = PlaceName(r.EndsAt);
                    }
                }
            }
            // Cars that drove through each watched turn this hour; then watch this hour's junctions for the next one.
            Dictionary<Entity, float> driven = m_TurnFlow.Harvest();
            var watch = new List<Entity>();
            foreach (Row r in rows)
            {
                Entity jn = EntityManager.HasComponent<NetNode>(r.Place) ? r.Place : r.EndsAt;
                if (jn == Entity.Null || !EntityManager.HasBuffer<Game.Net.SubLane>(jn)) continue;
                DynamicBuffer<Game.Net.SubLane> nodeLanes = EntityManager.GetBuffer<Game.Net.SubLane>(jn, true);
                for (int i = 0; i < nodeLanes.Length; i++)
                {
                    Entity lane = nodeLanes[i].m_SubLane;
                    if (EntityManager.HasComponent<CarLane>(lane) && !EntityManager.HasComponent<EdgeLane>(lane) &&
                        !EntityManager.HasComponent<Game.Net.MasterLane>(lane)) watch.Add(lane);
                }
            }
            m_TurnFlow.SetLanes(watch);
            foreach (Row r in rows)
            {
                try
                {
                    Entity junctionNode = EntityManager.HasComponent<NetNode>(r.Place) ? r.Place : r.EndsAt;
                    var facts = new LaneDiagnosis.QueueFacts
                    {
                        CauseShares = r.CauseShares,
                        TruckPct = r.TruckPct,
                        TruckFrom = r.FromList != null && r.FromList.Count > 0 ? r.FromList[0].Name : null,
                        BoxBlock = r.BoxBlock,
                        BoxName = r.BoxNode != Entity.Null && EntityManager.Exists(r.BoxNode) ? PlaceName(r.BoxNode) : null,
                    };
                    r.Lanes = LaneDiagnosis.Build(EntityManager, m_NameSystem, r.Place, junctionNode, r.TopCause, h.QueueLanes, h.JunctionMoves, samples, driven, facts);
                }
                catch (Exception e)
                {
                    Logger.Warn($"Bottleneck finder: lane diagnosis failed for {r.Name}: {e.Message}");
                }
            }

            // The same detour is found with slightly different ends (each route snaps to the nearest node): merge pairs
            // whose ends are within kLinkMergeDistance of each other, adding up their routes, before picking the top ones.
            var groups = new List<LinkGroup>();
            int connected = 0;
            foreach (KeyValuePair<LinkKey, LinkHour> kv in h.Links.OrderByDescending(l => l.Value.Routes))
            {
                if (!TryNodePos(kv.Key.A, out float3 pa) || !TryNodePos(kv.Key.B, out float3 pb)) continue;
                LinkGroup g = groups.FirstOrDefault(x => x.Key.Source == kv.Key.Source && (
                    (math.distance(x.PosA.xz, pa.xz) < kLinkMergeDistance && math.distance(x.PosB.xz, pb.xz) < kLinkMergeDistance) ||
                    (math.distance(x.PosA.xz, pb.xz) < kLinkMergeDistance && math.distance(x.PosB.xz, pa.xz) < kLinkMergeDistance)));
                if (g != null)
                {
                    g.Routes += kv.Value.Routes;
                    g.Saving += kv.Value.Saving;
                    g.Gap += kv.Value.Gap;
                    continue;
                }
                groups.Add(new LinkGroup { Key = kv.Key, First = kv.Value, PosA = pa, PosB = pb, Routes = kv.Value.Routes, Saving = kv.Value.Saving, Gap = kv.Value.Gap });
            }

            var links = new List<LinkRow>();
            foreach (LinkGroup g in groups.OrderByDescending(x => x.Routes))
            {
                if (links.Count >= kTrackLinks) break;
                if (g.Routes < kLinkMinRoutes) break;
                if (Connected(g.Key.A, g.Key.B))
                {
                    connected++;
                    continue;
                }
                Entity start = g.First.StartIsA ? g.Key.A : g.Key.B;
                Entity end = g.First.StartIsA ? g.Key.B : g.Key.A;
                int kind = Classify(g.First.Lanes, start, end);
                links.Add(new LinkRow
                {
                    Lanes = g.First.Lanes,
                    Kind = kind,
                    FixAt = FirstChoice(g.First.Lanes, start),
                    FixName = PlaceName(FirstChoice(g.First.Lanes, start)),
                    End = end,
                    FromName = StreetAlong(g.First.Lanes, true) ?? PlaceName(start),
                    ToName = StreetAlong(g.First.Lanes, false) ?? PlaceName(end),
                    Via = ViaStreets(g.First.Lanes),
                    ViaList = ViaStreetList(g.First.Lanes),
                    FromEntity = StreetEntityAlong(g.First.Lanes, true),
                    ToEntity = StreetEntityAlong(g.First.Lanes, false),
                    NeedsRamp = kind == kNewLink && (IsHighwayNode(g.Key.A) || IsHighwayNode(g.Key.B)),
                    A = g.Key.A,
                    B = g.Key.B,
                    Source = g.Key.Source,
                    NameA = PlaceName(g.Key.A),
                    NameB = PlaceName(g.Key.B),
                    SameStreet = SharesStreetName(PlaceName(g.Key.A), PlaceName(g.Key.B)),
                    Points = g.First.Points,
                    Routes = g.Routes,
                    Gap = g.Gap / g.Routes,
                    Saving = g.Saving / g.Routes,
                });
            }

            // Ranking over time: what keeps coming back in the last kWindowHours game hours, not a one-hour flash.
            m_Window.Add(new HourSummary { Rows = rows, Links = links, Despawns = despawns });
            while (m_Window.Count > kWindowHours) m_Window.RemoveAt(0);
            int windowHours = m_Window.Count;
            List<Row> ongoingRows = RankRows(windowHours);
            int minHours = windowHours < 4 ? int.MaxValue : Math.Max(2, (int)Math.Ceiling(windowHours / 4.0));
            List<LinkRow> ongoingLinks = RankLinks(minHours);

            List<Row> rowsTop = rows.Take(kRowsPerHour).ToList();
            // Fixes only for bottlenecks on the list (and not cleared): a detour that takes no one out of a listed queue
            // is not worth building.
            List<LinkRow> linksTop = AttachLinks(rowsTop, links);
            ongoingLinks = AttachLinks(ongoingRows.Where(r => r.Status != "cleared").ToList(), ongoingLinks);
            bool quiet = rows.Count == 0 || rows[0].LongWait < kMinLongWait;
            PublishViews(rowsTop.Concat(ongoingRows), linksTop.Concat(ongoingLinks));
            Json = ToJson(time, h, rowsTop, linksTop, quiet, ongoingRows, ongoingLinks, windowHours, minHours);
            Version++;
            Save(time, rowsTop, linksTop);
            if (Logger.DetailOn)
            {
                Log(time, h, rowsTop, linksTop, connected, quiet);
                if (despawns.Count > 0)
                {
                    // Streets and junctions by name; parking lots, building lots and paths (cars on their access lanes)
                    // only as a total, they are not roads to fix.
                    bool IsRoad(Entity e) => EntityManager.Exists(e) && (EntityManager.HasComponent<Aggregate>(e) || EntityManager.HasComponent<NetNode>(e)) &&
                        !PlaceName(e).StartsWith("Invisible", StringComparison.Ordinal);
                    int offRoad = despawns.Where(d => !IsRoad(d.Key)).Sum(d => d.Value);
                    Logger.Info("  Cars removed by the game (stuck or no route), by road: " + string.Join(", ",
                        despawns.Where(d => IsRoad(d.Key)).OrderByDescending(d => d.Value).Take(8).Select(d => $"{PlaceName(d.Key)} {d.Value}")) +
                        (offRoad > 0 ? $"; in parking or building lots: {offRoad}" : ""));
                }
                Logger.Info($"  Ongoing ({windowHours} game hours): " + string.Join("; ", ongoingRows.Select(r => $"{r.Name} {r.AvgWait:0.0} stuck/h in {r.HoursIn} h, last {kRecentHours} h {r.RecentWait:0.0}/h{(r.Status != "" ? " (" + r.Status + ")" : "")}")) +
                    " | fixes: " + string.Join("; ", ongoingLinks.Select(l => $"{LinkTypeName(l.Kind)} {l.FixName ?? l.NameA} {l.TotalRoutes} drivers in {l.HoursIn} h")));
            }
        }

        /// <summary>
        /// The fixes for these bottlenecks: a link belongs to the row whose junction, street or queue streets include the
        /// bottleneck its drivers were stuck in. At most kLinksPerRow per row; links that belong to no row are dropped.
        /// </summary>
        private List<LinkRow> AttachLinks(List<Row> rows, List<LinkRow> links)
        {
            var result = new List<LinkRow>();
            foreach (Row r in rows)
            {
                var members = new HashSet<Entity> { r.Place };
                if (r.EndsAt != Entity.Null) members.Add(r.EndsAt);
                foreach (KeyValuePair<string, Entity> q in r.QueuesOn) members.Add(q.Value);
                foreach (LinkRow l in links.Where(x => members.Contains(x.Source) && !result.Any(y => y.A == x.A && y.B == x.B)).Take(kLinksPerRow))
                {
                    LinkRow c = l.Copy();
                    c.ForId = Id(r.Place);
                    c.ForName = r.Name;
                    result.Add(c);
                }
            }
            return result;
        }

        private const int kLinksPerRow = 2;

        private sealed class HourSummary
        {
            public List<Row> Rows;
            public List<LinkRow> Links;
            public Dictionary<Entity, int> Despawns;
        }

        private readonly List<HourSummary> m_Window = new List<HourSummary>();

        /// <summary>Places by stuck cars added up over the window; the latest hour's details are shown.</summary>
        /// <summary>
        /// Problems by stuck cars added up over the window. Queues that end at the same junction (on several streets)
        /// are one problem, named after the junction. Shown only when big enough and coming back: at least kMinAvgWait
        /// stuck cars per hour on average, in at least a quarter of the hours.
        /// </summary>
        private List<Row> RankRows(int windowHours)
        {
            var sum = new Dictionary<string, double>();
            var hours = new Dictionary<string, HashSet<int>>();
            var memberSum = new Dictionary<string, Dictionary<Entity, double>>();
            var latest = new Dictionary<Entity, Row>();
            var junctionOf = new Dictionary<string, Entity>();
            var recent = new Dictionary<string, double>();
            int hourIndex = 0;
            foreach (HourSummary hs in m_Window)
            {
                foreach (Row r in hs.Rows)
                {
                    Entity junction = EntityManager.HasComponent<NetNode>(r.Place) ? r.Place : r.EndsAt;
                    string key = junction != Entity.Null ? "J" + Id(junction) : "P" + Id(r.Place);
                    junctionOf[key] = junction;
                    sum.TryGetValue(key, out double s);
                    sum[key] = s + r.LongWait;
                    if (hourIndex >= windowHours - kRecentHours)
                    {
                        recent.TryGetValue(key, out double rs);
                        recent[key] = rs + r.LongWait;
                    }
                    if (!hours.TryGetValue(key, out HashSet<int> hs2)) hours[key] = hs2 = new HashSet<int>();
                    hs2.Add(hourIndex);
                    if (!memberSum.TryGetValue(key, out Dictionary<Entity, double> m)) memberSum[key] = m = new Dictionary<Entity, double>();
                    m.TryGetValue(r.Place, out double ms);
                    m[r.Place] = ms + r.LongWait;
                    latest[r.Place] = r;
                }
                hourIndex++;
            }
            int minHours = Math.Max(1, (int)Math.Ceiling(windowHours / 4.0));
            var list = new List<Row>();
            foreach (KeyValuePair<string, double> kv in sum.OrderByDescending(x => x.Value))
            {
                if (list.Count >= kRowsPerHour) break;
                double avg = kv.Value / Math.Max(1, windowHours);
                if (avg < kMinAvgWait || hours[kv.Key].Count < minHours) continue;
                // The street (or junction) with the most stuck cars stands for the group: its details and map view.
                Entity main = memberSum[kv.Key].OrderByDescending(x => x.Value).First().Key;
                if (!EntityManager.Exists(main)) continue;
                Row r = latest[main].Copy();
                r.HoursIn = hours[kv.Key].Count;
                r.AvgWait = avg;
                // Still there? A fix shows as "cleared" within kClearedHours, long before its old hours leave the window.
                r.HoursSince = windowHours - 1 - hours[kv.Key].Max();
                r.RecentWait = (recent.TryGetValue(kv.Key, out double rw) ? rw : 0) / Math.Min(kRecentHours, windowHours);
                r.Status = windowHours < kRecentHours * 2 ? ""
                    : r.HoursSince >= kClearedHours ? "cleared"
                    : r.RecentWait < avg * 0.5 ? "better"
                    : r.RecentWait > avg * 1.5 ? "worse" : "";
                Entity junction = junctionOf[kv.Key];
                var removedAt = new HashSet<Entity>(memberSum[kv.Key].Keys);
                if (junction != Entity.Null) removedAt.Add(junction);
                double removed = 0;
                foreach (HourSummary hs in m_Window)
                {
                    if (hs.Despawns == null) continue;
                    foreach (Entity e in removedAt) removed += hs.Despawns.TryGetValue(e, out int n) ? n : 0;
                }
                r.Despawns = removed / Math.Max(1, windowHours);
                r.QueuesOn = memberSum[kv.Key].Keys.Where(e => EntityManager.Exists(e) && !EntityManager.HasComponent<NetNode>(e))
                    .Select(e => new KeyValuePair<string, Entity>(latest[e].Name, e)).ToList();
                if (junction != Entity.Null && EntityManager.Exists(junction))
                {
                    r.Name = PlaceName(junction);
                    r.Kind = "junction";
                    r.EndsAt = junction;
                    r.EndsAtName = r.Name;
                }
                list.Add(r);
            }
            // Cleared problems go to the end: still listed (so a fix can be seen working), but not as a current problem.
            return list.Where(r => r.Status != "cleared").Concat(list.Where(r => r.Status == "cleared")).ToList();
        }

        private string LinkKeyText(LinkRow l) =>
            // A new road joins two places either way round: "A to B" and "B to A" are one idea.
            Id(l.Source) + "|" + (l.Kind == kNewLink ? "link|" + (string.CompareOrdinal(l.NameA, l.NameB) <= 0 ? l.NameA + "|" + l.NameB : l.NameB + "|" + l.NameA)
                : l.Kind + "|" + l.FixName + "|" + l.FromName + "|" + l.ToName);

        private static string LinkTypeName(int kind) => kind == kTurnAround ? "turn-around" : kind == kMissingTurn ? "missing turn" : "new road";

        /// <summary>Detours seen in at least minHours of the window, by drivers added up; the latest hour's details.</summary>
        private List<LinkRow> RankLinks(int minHours)
        {
            var sum = new Dictionary<string, int>();
            var hours = new Dictionary<string, int>();
            var latest = new Dictionary<string, LinkRow>();
            foreach (HourSummary hs in m_Window)
            {
                foreach (LinkRow l in hs.Links)
                {
                    string key = LinkKeyText(l);
                    sum.TryGetValue(key, out int s);
                    sum[key] = s + l.Routes;
                    hours.TryGetValue(key, out int n);
                    hours[key] = n + 1;
                    latest[key] = l;
                }
            }
            var list = new List<LinkRow>();
            int windowHours = Math.Max(1, m_Window.Count);
            foreach (KeyValuePair<string, int> kv in sum.Where(x => hours[x.Key] >= minHours && (double)x.Value / windowHours >= kMinLinkDrivers)
                .OrderByDescending(x => x.Value).Take(kTrackLinks))
            {
                LinkRow l = latest[kv.Key].Copy();
                if (!EntityManager.Exists(l.A) || !EntityManager.Exists(l.B)) continue;
                l.HoursIn = hours[kv.Key];
                l.TotalRoutes = kv.Value;
                list.Add(l);
            }
            return list;
        }

        /// <summary>Map views of the bottlenecks and links on show, for <see cref="BottleneckOverlaySystem"/>. Main thread.</summary>
        internal static readonly Dictionary<string, OverlayView> Views = new Dictionary<string, OverlayView>();

        private void PublishViews(IEnumerable<Row> rows, IEnumerable<LinkRow> links)
        {
            Views.Clear();
            foreach (Row r in rows)
            {
                var view = new OverlayView { Title = r.Name, Place = r.Place, Position = r.Lanes != null ? r.Lanes.Position : default };
                if (r.Lanes != null)
                {
                    view.QueueLanes = r.Lanes.QueueLanes;
                    view.Turns = r.Lanes.Turns;
                    view.Junction = r.Lanes.Junction;
                }
                if (r.FromList != null) foreach (AreaShare a in r.FromList) if (a.HasPosition) view.Origins.Add(new KeyValuePair<float3, float>(a.Position, (float)a.Share));
                if (r.ToList != null) foreach (AreaShare a in r.ToList) if (a.HasPosition) view.Destinations.Add(new KeyValuePair<float3, float>(a.Position, (float)a.Share));
                Views[Id(r.Place)] = view;
            }
            foreach (LinkRow l in links)
            {
                var view = new OverlayView
                {
                    Title = l.NameA + " - " + l.NameB, IsLink = true, LinkA = l.A, LinkB = l.B, Route = l.Points, RouteLanes = l.Lanes,
                    LinkKind = l.Kind, FixAt = l.FixAt, LinkEnd = l.End,
                };
                Views[Id(l.A) + "-" + Id(l.B)] = view;
            }
        }

        /// <summary>
        /// The first junction (3+ roads) on the detour: where drivers had to go the wrong way. The start of the detour
        /// can be a plain point on a road, so that is not the place to fix.
        /// </summary>
        private Entity FirstChoice(List<Entity> lanes, Entity fallback)
        {
            if (lanes == null) return fallback;
            foreach (Entity lane in lanes)
            {
                if (!EntityManager.Exists(lane) || EntityManager.HasComponent<Game.Net.EdgeLane>(lane)) continue;
                if (!EntityManager.HasComponent<Owner>(lane)) continue;
                Entity node = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                if (EntityManager.HasBuffer<ConnectedEdge>(node) && EntityManager.GetBuffer<ConnectedEdge>(node, true).Length >= 3) return node;
            }
            return fallback;
        }

        private string StreetOfLane(Entity lane)
        {
            if (!EntityManager.Exists(lane) || !EntityManager.HasComponent<Owner>(lane)) return null;
            Entity edge = EntityManager.GetComponentData<Owner>(lane).m_Owner;
            if (!EntityManager.HasComponent<Aggregated>(edge)) return null;
            Entity agg = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
            if (agg == Entity.Null) return null;
            string n = TrafficReportSystem.CleanName(m_NameSystem.GetRenderedLabelName(agg));
            return string.IsNullOrEmpty(n) ? null : n;
        }

        /// <summary>The street the detour starts on (first) or ends on (last).</summary>
        private string StreetAlong(List<Entity> lanes, bool first)
        {
            if (lanes == null) return null;
            for (int i = 0; i < lanes.Count; i++)
            {
                string n = StreetOfLane(lanes[first ? i : lanes.Count - 1 - i]);
                if (n != null) return n;
            }
            return null;
        }

        private Entity StreetEntityAlong(List<Entity> lanes, bool first)
        {
            if (lanes == null) return Entity.Null;
            for (int i = 0; i < lanes.Count; i++)
            {
                Entity lane = lanes[first ? i : lanes.Count - 1 - i];
                if (!EntityManager.Exists(lane) || !EntityManager.HasComponent<Owner>(lane)) continue;
                Entity edge = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                if (EntityManager.HasComponent<Aggregated>(edge) && EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate != Entity.Null)
                {
                    return EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                }
            }
            return Entity.Null;
        }

        private List<KeyValuePair<string, Entity>> ViaStreetList(List<Entity> lanes)
        {
            var result = new List<KeyValuePair<string, Entity>>();
            if (lanes == null) return result;
            string from = StreetAlong(lanes, true), to = StreetAlong(lanes, false);
            foreach (Entity lane in lanes)
            {
                string n = StreetOfLane(lane);
                if (n == null || n == from || n == to || result.Any(x => x.Key == n)) continue;
                Entity edge = EntityManager.GetComponentData<Owner>(lane).m_Owner;
                result.Add(new KeyValuePair<string, Entity>(n, EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate));
                if (result.Count == 2) break;
            }
            return result;
        }

        /// <summary>Up to two other streets the detour uses, for the text ("via Wood Street and Dunhaven Street").</summary>
        private string ViaStreets(List<Entity> lanes)
        {
            if (lanes == null) return "";
            string from = StreetAlong(lanes, true), to = StreetAlong(lanes, false);
            var seen = new List<string>();
            foreach (Entity lane in lanes)
            {
                string n = StreetOfLane(lane);
                if (n == null || n == from || n == to || seen.Contains(n)) continue;
                seen.Add(n);
                if (seen.Count == 2) break;
            }
            return string.Join(" and ", seen);
        }

        private sealed class LinkGroup
        {
            public LinkKey Key;
            public LinkHour First;
            public float3 PosA, PosB;
            public int Routes;
            public double Saving, Gap;
        }

        private const float kLinkMergeDistance = 200f;

        private bool TryNodePos(Entity node, out float3 position)
        {
            position = default;
            if (!EntityManager.Exists(node) || !EntityManager.HasComponent<NetNode>(node)) return false;
            position = EntityManager.GetComponentData<NetNode>(node).m_Position;
            return true;
        }

        /// <summary>
        /// What kind of fix a detour needs. Turn-around: the route comes back the opposite way (it wanted to turn
        /// round). Missing turn: the two ends are already joined by a short stretch of road (2-3 segments), so the
        /// drivers could not make the turn onto it. New link: the ends are not joined nearby.
        /// </summary>
        private int Classify(List<Entity> lanes, Entity start, Entity end)
        {
            // A turn-around: the route comes back the opposite way on the street it started on. A highway ramp that loops
            // round also ends up facing back, so routes over highways never count as turn-arounds.
            if (lanes != null && lanes.Count >= 2 && TryHeading(lanes[0], true, out float2 h0) && TryHeading(lanes[lanes.Count - 1], false, out float2 h1))
            {
                string from = StreetAlong(lanes, true), to = StreetAlong(lanes, false);
                if (math.dot(h0, h1) < -0.7f && from != null && from == to && !UsesHighway(lanes)) return kTurnAround;
            }
            return WithinSegments(start, end, 3) ? kMissingTurn : kNewLink;
        }

        private bool UsesHighway(List<Entity> lanes)
        {
            foreach (Entity lane in lanes)
            {
                if (EntityManager.Exists(lane) && EntityManager.HasComponent<CarLane>(lane) &&
                    (EntityManager.GetComponentData<CarLane>(lane).m_Flags & CarLaneFlags.Highway) != 0) return true;
            }
            return false;
        }

        private bool TryHeading(Entity lane, bool atStart, out float2 heading)
        {
            heading = default;
            if (!EntityManager.Exists(lane) || !EntityManager.HasComponent<Curve>(lane)) return false;
            Colossal.Mathematics.Bezier4x3 c = EntityManager.GetComponentData<Curve>(lane).m_Bezier;
            float3 d = atStart ? c.b - c.a : c.d - c.c;
            if (math.lengthsq(d.xz) < 1e-4f) d = c.d - c.a;
            heading = math.normalizesafe(d.xz);
            return math.lengthsq(heading) > 0.5f;
        }

        /// <summary>Whether two road nodes are joined by at most this many road segments.</summary>
        private bool WithinSegments(Entity from, Entity to, int maxSegments)
        {
            var frontier = new List<Entity> { from };
            var seen = new HashSet<Entity> { from };
            for (int depth = 0; depth < maxSegments && frontier.Count > 0; depth++)
            {
                var next = new List<Entity>();
                foreach (Entity node in frontier)
                {
                    if (!EntityManager.HasBuffer<ConnectedEdge>(node)) continue;
                    DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
                    for (int i = 0; i < edges.Length; i++)
                    {
                        if (!EntityManager.HasComponent<Edge>(edges[i].m_Edge)) continue;
                        Edge e = EntityManager.GetComponentData<Edge>(edges[i].m_Edge);
                        Entity other = e.m_Start == node ? e.m_End : e.m_Start;
                        if (other == to) return true;
                        if (seen.Add(other)) next.Add(other);
                    }
                }
                frontier = next;
            }
            return false;
        }

        /// <summary>A node on a highway (its road's car lanes are highway lanes).</summary>
        private bool IsHighwayNode(Entity node)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(node)) return false;
            DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(node, true);
            for (int i = 0; i < edges.Length; i++)
            {
                if (!EntityManager.HasBuffer<Game.Net.SubLane>(edges[i].m_Edge)) continue;
                DynamicBuffer<Game.Net.SubLane> lanes = EntityManager.GetBuffer<Game.Net.SubLane>(edges[i].m_Edge, true);
                for (int k = 0; k < lanes.Length; k++)
                {
                    if (EntityManager.HasComponent<CarLane>(lanes[k].m_SubLane) &&
                        (EntityManager.GetComponentData<CarLane>(lanes[k].m_SubLane).m_Flags & CarLaneFlags.Highway) != 0) return true;
                }
            }
            return false;
        }

        /// <summary>Both ends on a street with the same name: likely the two sides of a divided road (a U-turn).</summary>
        private static bool SharesStreetName(string a, string b)
        {
            var left = new HashSet<string>(a.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries));
            return b.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries).Any(left.Contains);
        }

        private static bool SharesEdge(DynamicBuffer<ConnectedEdge> edges, Entity other, EntityManager em)
        {
            for (int i = 0; i < edges.Length; i++)
            {
                if (!em.HasComponent<Edge>(edges[i].m_Edge)) continue;
                Edge e = em.GetComponentData<Edge>(edges[i].m_Edge);
                if (e.m_Start == other || e.m_End == other) return true;
            }
            return false;
        }

        /// <summary>Already joined by a road segment (then there is nothing to build).</summary>
        private bool Connected(Entity a, Entity b)
        {
            if (!EntityManager.HasBuffer<ConnectedEdge>(a)) return false;
            return SharesEdge(EntityManager.GetBuffer<ConnectedEdge>(a, true), b, EntityManager);
        }

        /// <summary>Top 3 areas (destinations or origins) of a place, merged by name, with an entity and position each.</summary>
        private List<AreaShare> TopAreaList(Hour h, Entity place, int kind)
        {
            var list = h.Od.Where(o => o.Key.Place == place && o.Key.Kind == kind).ToList();
            double total = list.Sum(o => o.Value);
            var byName = new Dictionary<string, AreaShare>();
            var best = new Dictionary<string, int>();
            if (total <= 0) return new List<AreaShare>();
            foreach (KeyValuePair<OdKey, int> o in list)
            {
                if (o.Key.Area.Index != kOutsideIndex && !EntityManager.Exists(o.Key.Area)) continue;
                string name = PlaceName(o.Key.Area);
                if (!byName.TryGetValue(name, out AreaShare a))
                {
                    byName[name] = a = new AreaShare { Name = name, Entity = o.Key.Area };
                    best[name] = 0;
                }
                a.Share += o.Value / total;
                if (o.Value > best[name])
                {
                    best[name] = o.Value;
                    a.Entity = o.Key.Area;
                }
            }
            List<AreaShare> top = byName.Values.OrderByDescending(a => a.Share).Take(3).ToList();
            foreach (AreaShare a in top) a.HasPosition = TryAreaPosition(a.Entity, out a.Position);
            return top;
        }

        private static string AreasText(List<AreaShare> list) =>
            list == null ? "" : string.Join(", ", list.Select(a => $"{a.Name} {a.Share * 100:0}%"));

        /// <summary>A point for an area on the map: junction, street (middle), building, district (centre).</summary>
        private bool TryAreaPosition(Entity e, out float3 position)
        {
            position = default;
            if (e == Entity.Null || e.Index == kOutsideIndex || !EntityManager.Exists(e)) return false;
            if (EntityManager.HasComponent<NetNode>(e))
            {
                position = EntityManager.GetComponentData<NetNode>(e).m_Position;
                return true;
            }
            if (EntityManager.HasBuffer<Game.Net.AggregateElement>(e))
            {
                DynamicBuffer<Game.Net.AggregateElement> edges = EntityManager.GetBuffer<Game.Net.AggregateElement>(e, true);
                if (edges.Length == 0 || !EntityManager.HasComponent<Curve>(edges[edges.Length / 2].m_Edge)) return false;
                position = Colossal.Mathematics.MathUtils.Position(EntityManager.GetComponentData<Curve>(edges[edges.Length / 2].m_Edge).m_Bezier, 0.5f);
                return true;
            }
            if (EntityManager.HasComponent<Transform>(e))
            {
                position = EntityManager.GetComponentData<Transform>(e).m_Position;
                return true;
            }
            if (EntityManager.HasComponent<Game.Areas.Geometry>(e))
            {
                position = EntityManager.GetComponentData<Game.Areas.Geometry>(e).m_CenterPosition;
                return true;
            }
            return false;
        }

        private string TopAreas(Hour h, Entity place, int kind)
        {
            var list = h.Od.Where(o => o.Key.Place == place && o.Key.Kind == kind).ToList();
            double total = list.Sum(o => o.Value);
            if (total <= 0) return "";
            // Areas with the same name (a street in several pieces) are counted together.
            var byName = new Dictionary<string, double>();
            foreach (KeyValuePair<OdKey, int> o in list)
            {
                if (o.Key.Area.Index != kOutsideIndex && !EntityManager.Exists(o.Key.Area)) continue;
                string name = PlaceName(o.Key.Area);
                byName.TryGetValue(name, out double n);
                byName[name] = n + o.Value;
            }
            return string.Join(", ", byName.OrderByDescending(x => x.Value).Take(3).Select(x => $"{x.Key} {x.Value / total * 100:0}%"));
        }

        /// <summary>Name of a street, district or junction.</summary>
        private string PlaceName(Entity e)
        {
            if (e.Index == kOutsideIndex) return "outside the city";
            if (EntityManager.HasBuffer<ConnectedEdge>(e) && EntityManager.HasComponent<NetNode>(e))
            {
                DynamicBuffer<ConnectedEdge> edges = EntityManager.GetBuffer<ConnectedEdge>(e, true);
                var names = new List<string>();
                for (int i = 0; i < edges.Length && names.Count < 3; i++)
                {
                    Entity edge = edges[i].m_Edge;
                    if (!EntityManager.HasComponent<Aggregated>(edge)) continue;
                    Entity agg = EntityManager.GetComponentData<Aggregated>(edge).m_Aggregate;
                    if (agg == Entity.Null) continue;
                    string n = TrafficReportSystem.CleanName(m_NameSystem.GetRenderedLabelName(agg));
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                return names.Count > 0 ? string.Join(" / ", names) : "Junction";
            }
            string name = TrafficReportSystem.CleanName(m_NameSystem.GetRenderedLabelName(e));
            return string.IsNullOrEmpty(name) ? "(unnamed)" : name;
        }

        private static string Id(Entity e) => e.Index + ":" + e.Version;

        private static string ToJson(string time, Hour h, List<Row> rows, List<LinkRow> links, bool quiet,
            List<Row> ongoingRows, List<LinkRow> ongoingLinks, int windowHours, int minHours)
        {
            var sb = new StringBuilder("{");
            TrafficCapture.Str(sb, "time", time);
            TrafficCapture.Num(sb, "samples", h.Samples);
            TrafficCapture.Num(sb, "quiet", quiet ? 1 : 0);
            TrafficCapture.Num(sb, "window", windowHours);
            TrafficCapture.Num(sb, "minHours", minHours == int.MaxValue ? -1 : minHours);
            AppendRows(sb, "ongoing", ongoingRows);
            sb.Append(',');
            AppendLinks(sb, "ongoingLinks", ongoingLinks);
            sb.Append(',');
            AppendRows(sb, "rows", rows);
            sb.Append(',');
            AppendLinks(sb, "links", links);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendAreas(StringBuilder sb, string key, List<AreaShare> list)
        {
            sb.Append('"').Append(key).Append("\":[");
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('{');
                    TrafficCapture.Str(sb, "n", list[i].Name);
                    TrafficCapture.Str(sb, "id", list[i].Entity == Entity.Null || list[i].Entity.Index == kOutsideIndex ? "" : Id(list[i].Entity));
                    TrafficCapture.Num(sb, "p", list[i].Share < 0 ? -1 : Math.Round(list[i].Share * 100), last: true);
                    sb.Append('}');
                }
            }
            sb.Append("],");
        }

        private static void AppendStreets(StringBuilder sb, LinkRow l)
        {
            var streets = new List<KeyValuePair<string, Entity>>
            {
                new KeyValuePair<string, Entity>(l.FromName ?? "", l.FromEntity),
                new KeyValuePair<string, Entity>(l.FixName ?? "", l.FixAt),
            };
            streets.AddRange(l.ViaList ?? new List<KeyValuePair<string, Entity>>());
            streets.Add(new KeyValuePair<string, Entity>(l.ToName ?? "", l.ToEntity));
            if (l.Kind == kNewLink)
            {
                streets.Add(new KeyValuePair<string, Entity>(l.NameA ?? "", l.A));
                streets.Add(new KeyValuePair<string, Entity>(l.NameB ?? "", l.B));
            }
            sb.Append("\"streets\":[");
            bool first = true;
            var seen = new HashSet<string>();
            foreach (KeyValuePair<string, Entity> st in streets)
            {
                if (string.IsNullOrEmpty(st.Key) || st.Value == Entity.Null || !seen.Add(st.Key)) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                TrafficCapture.Str(sb, "n", st.Key);
                TrafficCapture.Str(sb, "id", Id(st.Value), last: true);
                sb.Append('}');
            }
            sb.Append("],");
        }

        private static void AppendRows(StringBuilder sb, string key, List<Row> rows)
        {
            sb.Append('"').Append(key).Append("\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                TrafficCapture.Num(sb, "hoursIn", r.HoursIn);
                TrafficCapture.Num(sb, "avgWait", Math.Round(r.AvgWait, 1));
                TrafficCapture.Num(sb, "despawns", Math.Round(r.Despawns, 1));
                TrafficCapture.Num(sb, "trucks", Math.Round(r.TruckPct));
                TrafficCapture.Str(sb, "status", r.Status ?? "");
                TrafficCapture.Num(sb, "since", r.HoursSince);
                TrafficCapture.Num(sb, "recent", Math.Round(r.RecentWait, 1));
                TrafficCapture.Str(sb, "endsAt", r.EndsAtName ?? "");
                TrafficCapture.Str(sb, "endsAtId", r.EndsAt != Entity.Null ? Id(r.EndsAt) : "");
                AppendAreas(sb, "queuesOn", r.QueuesOn.Select(q => new AreaShare { Name = q.Key, Entity = q.Value, Share = -1 }).ToList());
                AppendAreas(sb, "toList", r.ToList);
                AppendAreas(sb, "fromList", r.FromList);
                TrafficCapture.Str(sb, "id", Id(r.Place));
                TrafficCapture.Str(sb, "place", r.Name);
                TrafficCapture.Str(sb, "kind", r.Kind);
                TrafficCapture.Str(sb, "cause", r.Cause);
                TrafficCapture.Str(sb, "mix", r.CauseMix);
                TrafficCapture.Num(sb, "queued", Math.Round(r.Queued, 1));
                TrafficCapture.Num(sb, "wait", Math.Round(r.LongWait, 1));
                TrafficCapture.Num(sb, "spill", Math.Round(r.SpillPct));
                TrafficCapture.Num(sb, "through", Math.Round(r.ThroughPct));
                TrafficCapture.Str(sb, "moves", r.Lanes?.MovesText ?? "");
                TrafficCapture.Str(sb, "lanes", r.Lanes?.LanesText ?? "");
                TrafficCapture.Str(sb, "flow", r.Lanes?.FlowText ?? "");
                TrafficCapture.Str(sb, "advice", r.Lanes != null ? string.Join("||", r.Lanes.Advice) : "");
                TrafficCapture.Str(sb, "to", r.To);
                TrafficCapture.Str(sb, "from", r.From, last: true);
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static void AppendLinks(StringBuilder sb, string key, List<LinkRow> links)
        {
            sb.Append('"').Append(key).Append("\":[");
            for (int i = 0; i < links.Count; i++)
            {
                LinkRow l = links[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                TrafficCapture.Num(sb, "hoursIn", l.HoursIn);
                TrafficCapture.Num(sb, "total", l.TotalRoutes);
                AppendStreets(sb, l);
                TrafficCapture.Str(sb, "a", l.NameA);
                TrafficCapture.Str(sb, "b", l.NameB);
                TrafficCapture.Str(sb, "aId", Id(l.A));
                TrafficCapture.Str(sb, "bId", Id(l.B));
                TrafficCapture.Str(sb, "key", Id(l.A) + "-" + Id(l.B));
                TrafficCapture.Str(sb, "forId", l.ForId ?? "");
                TrafficCapture.Str(sb, "forName", l.ForName ?? "");
                TrafficCapture.Num(sb, "gap", Math.Round(l.Gap));
                TrafficCapture.Num(sb, "saving", Math.Round(l.Saving));
                TrafficCapture.Num(sb, "same", l.SameStreet ? 1 : 0);
                TrafficCapture.Str(sb, "type", l.Kind == kTurnAround ? "uturn" : l.Kind == kMissingTurn ? "turn" : "link");
                TrafficCapture.Str(sb, "at", l.FixName ?? "");
                TrafficCapture.Str(sb, "from", l.FromName ?? "");
                TrafficCapture.Str(sb, "to", l.ToName ?? "");
                TrafficCapture.Str(sb, "via", l.Via ?? "");
                TrafficCapture.Num(sb, "ramp", l.NeedsRamp ? 1 : 0);
                TrafficCapture.Num(sb, "routes", l.Routes, last: true);
                sb.Append('}');
            }
            sb.Append(']');
        }

        private static string Pct(double v) => v < 0 ? "?" : v.ToString("0", CultureInfo.InvariantCulture) + "%";

        private void Log(string time, Hour h, List<Row> rows, List<LinkRow> links, int connected, bool quiet)
        {
            double[] s = h.Stats;
            double samples = Math.Max(1, h.Samples);
            double queued = Math.Max(1, s[1]);
            double covered = Math.Max(1, s[7]);
            Logger.Info(string.Format(CultureInfo.InvariantCulture,
                "Bottlenecks {0}: {1} samples, {2:0} cars on roads and {3:0} queued per sample; queue chains avg {4:0.0} cars, longest {5}, " +
                "{6:0} cut at {7}; {8:0} queue heads per sample, {9:0.0}% of queued cars without a head place; {10:0}% of queued cars stand on " +
                "another street or junction than their queue's head. Covered queued vehicles: destination known {11:0}%, origin known {12:0}% " +
                "(unknown because already driving when counting started {13:0}%), both known {14:0}%, through traffic {15:0}% of those. " +
                "Detours: {16:0} routes walked, {17:0} with a detour, {18} pairs, {19} top pairs dropped as already connected. Cost {20}. " +
                "Waited {21}+ samples: {22:0}% of queued cars. From outside the city {23:0}%, to outside {24:0}% of covered queued vehicles.{25}",
                time, h.Samples, s[0] / samples, s[1] / samples, s[2] / queued, h.ChainMax,
                s[4], kMaxChain, s[5] / samples, s[6] / queued * 100, s[15] / queued * 100,
                s[8] / covered * 100, s[9] / covered * 100, s[10] / covered * 100, s[14] / covered * 100,
                s[14] > 0 ? s[13] / s[14] * 100 : 0,
                s[11], s[12], h.Links.Count, connected,
                h.Ms > 0 ? (h.Ms / samples).ToString("0.00", CultureInfo.InvariantCulture) + " ms per sample" : "not measured",
                kLongWaitSamples, s[16] / queued * 100, s[17] / covered * 100, s[18] / covered * 100,
                quiet ? " No real bottleneck this hour (queues clear within a light cycle)." : ""));
            for (int i = 0; i < rows.Count; i++)
            {
                Row r = rows[i];
                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "  #{0} {1} ({2} {3}): {4:0.0} cars queued behind it ({13:0.0} waited long); cause {5}; {6:0}% of the queue stands on other places; " +
                    "longest chain {7} (example head car {8}); to: {9}; from: {10}; through traffic {11} of {12:0} with both ends known.",
                    i + 1, r.Name, r.Kind, Id(r.Place), r.Queued, string.IsNullOrEmpty(r.CauseMix) ? r.Cause : r.CauseMix, r.SpillPct,
                    r.ChainMax, Id(r.ExampleHead), Empty(r.To), Empty(r.From), Pct(r.ThroughPct), r.OdKnown, r.LongWait));
                if (r.Lanes != null)
                {
                    Logger.Info($"     Lanes: {Empty(r.Lanes.MovesText)}; {Empty(r.Lanes.LanesText)}; {Empty(r.Lanes.FlowText)}; {r.Lanes.Debug}");
                    foreach (string a in r.Lanes.Advice) Logger.Info("     Advice: " + a);
                }
            }
            foreach (LinkRow l in links)
            {
                Logger.Info(string.Format(CultureInfo.InvariantCulture,
                    "  Possible {7}: {0} ({1}) <-> {2} ({3}): {4:0} m apart, {5} queued routes go round, ~{6:0} m longer each.",
                    l.NameA, Id(l.A), l.NameB, Id(l.B), l.Gap, l.Routes, l.Saving, l.Kind == kTurnAround ? "turn-around" : l.Kind == kMissingTurn ? "missing turn at " + l.FixName : l.NeedsRamp ? "link (needs a ramp)" : "link"));
            }
        }

        private static string Empty(string s) => string.IsNullOrEmpty(s) ? "unknown" : s;

        // ---------------- Files (append-only, one block per game hour) ----------------

        private static string BottleneckPath => Path.Combine(TrafficReportStore.Directory_, "bottlenecks.csv");
        private static string LinkPath => Path.Combine(TrafficReportStore.Directory_, "links.csv");
        private const long kMaxFileBytes = 5L * 1024 * 1024;

        private static void Save(string time, List<Row> rows, List<LinkRow> links)
        {
            string run = TrafficReportStore.CurrentRunId;
            string city = TrafficReportStore.CurrentCity;
            Append(BottleneckPath, "run_id,city,game_time,rank,place,kind,cause,cause_mix,queued_behind,spill_pct,through_pct,to,from,long_wait,despawns",
                rows.Select((r, i) => string.Join(",", Csv(run), Csv(city), Csv(time), (i + 1).ToString(CultureInfo.InvariantCulture), Csv(r.Name), r.Kind,
                    Csv(r.Cause), Csv(r.CauseMix), r.Queued.ToString("0.0", CultureInfo.InvariantCulture), r.SpillPct.ToString("0", CultureInfo.InvariantCulture),
                    r.ThroughPct.ToString("0", CultureInfo.InvariantCulture), Csv(r.To), Csv(r.From), r.LongWait.ToString("0.0", CultureInfo.InvariantCulture),
                    r.Despawns.ToString("0", CultureInfo.InvariantCulture))));
            Append(LinkPath, "run_id,city,game_time,a,b,gap_m,routes,longer_m,same_street,bottleneck",
                links.Select(l => string.Join(",", Csv(run), Csv(city), Csv(time), Csv(l.NameA), Csv(l.NameB),
                    l.Gap.ToString("0", CultureInfo.InvariantCulture), l.Routes.ToString(CultureInfo.InvariantCulture), l.Saving.ToString("0", CultureInfo.InvariantCulture), l.SameStreet ? "1" : "0",
                    Csv(l.ForName))));
        }

        private static void Append(string path, string header, IEnumerable<string> lines)
        {
            List<string> list = lines.ToList();
            if (list.Count == 0) return;
            try
            {
                Directory.CreateDirectory(TrafficReportStore.Directory_);
                bool otherHeader = File.Exists(path) && File.ReadLines(path).FirstOrDefault() != header;
                if (File.Exists(path) && (otherHeader || new FileInfo(path).Length > kMaxFileBytes))
                {
                    // Keep one older file; these are for looking back, the panel shows the last hour.
                    string old = Path.ChangeExtension(path, ".old.csv");
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
                bool writeHeader = !File.Exists(path);
                using (var w = new StreamWriter(path, append: true, Encoding.UTF8))
                {
                    if (writeHeader) w.WriteLine(header);
                    foreach (string line in list) w.WriteLine(line);
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Bottleneck finder: could not write {path}: {e.Message}");
            }
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
