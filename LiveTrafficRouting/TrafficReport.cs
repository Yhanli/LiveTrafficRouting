using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LiveTrafficRouting
{
    /// <summary>One game hour of traffic statistics, belonging to one run.</summary>
    internal sealed class TrafficCapture
    {
        public DateTime RealTime;
        public string GameTime = "";       // "yyyy-MM-dd HH:00" in game time
        public int GameHour;
        public string City = "";
        public string RunId = "";          // a run = one continuous recording with the same mode and settings
        public string Mode = "";           // "Native" (mod off) or "Live"
        public string Preset = "";
        public string Settings = "";       // full settings of the run, e.g. "Eager: live 100%, ..."

        public int Samples;
        public float Vehicles;             // average vehicles on the road
        public float AvgSpeedPct;          // real driving speed vs speed limit, averaged over vehicles
        public float QueuedPct;            // vehicles waiting behind another stopped vehicle
        public float LocalStreetPct;       // vehicles on roads with a limit of 50 km/h or less (not highways)
        public float JamWarnings;          // average number of jam warning signs on the map
        public int JamWarningsMax;
        public float FlowPct;              // the game's own Traffic Flow % (the info view chart)

        public float NavProgressKmh;       // how fast vehicles with navigation get closer to their destination
        public float OtherProgressKmh;     // same for the other vehicles
        public float NavSharePct;          // which share counted as "with navigation"

        public int Reroutes, RerouteCars, RerouteTrucks, RerouteTaxis;
        public int RerouteBetter, RerouteNoBetter;
        public float RerouteSavedPct;      // avg estimated time saved by re-routes that found a faster route
        public int RerouteHeldBack;        // re-routes postponed by the per-run limit

        public float PlannerSpeedPct;      // what the route planner "sees" (with penalties); Live only
        public float PlannerJammedPct;

        public float ModMs;                // our own jobs, one timed run (flow + re-route)

        // Why personal cars are on the road / in queues: % by trip purpose of the passengers.
        public float CarCommutePct, CarHomePct, CarShopPct, CarLeisurePct, CarOtherPct;
        public float QueuedCommutePct, QueuedHomePct, QueuedShopPct, QueuedLeisurePct, QueuedOtherPct;
        // Shopping / leisure trips started this hour (all travel modes): how many, to how many different places,
        // % that went to the 5 most visited places, average straight-line distance from home (km).
        public int ShopTrips, ShopPlaces, LeisureTrips, LeisurePlaces;
        public float ShopTop5Pct, ShopKm, LeisureTop5Pct, LeisureKm;

        // Performance (-1 = not measured, e.g. the speed setting changed during the hour).
        public float SpeedSetting;   // game speed setting (1, 2, 3)
        public float SimFps;         // simulation frames per real second
        public float RenderFps;      // rendered frames per second
        public float ModCpuPct;      // the mod's own work, % of one CPU core
        public float ModMemMb;       // what the mod keeps in memory (estimate)
        public float GameMemMb;      // the whole game's memory, for context

        // ---- CSV: columns are matched by header name, so files from older versions still load ----

        private delegate string Getter(TrafficCapture c);
        private delegate void Setter(TrafficCapture c, string v);

        private struct Column
        {
            public string Name;
            public Getter Get;
            public Setter Set;
            public Column(string name, Getter get, Setter set) { Name = name; Get = get; Set = set; }
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static readonly Column[] Columns =
        {
            new Column("real_time", c => c.RealTime.ToString("yyyy-MM-dd HH:mm:ss", Inv), (c, v) => DateTime.TryParse(v, Inv, DateTimeStyles.None, out c.RealTime)),
            new Column("game_time", c => c.GameTime, (c, v) => c.GameTime = v),
            new Column("game_hour", c => c.GameHour.ToString(Inv), (c, v) => c.GameHour = I(v)),
            new Column("city", c => c.City, (c, v) => c.City = v),
            new Column("run_id", c => c.RunId, (c, v) => c.RunId = v),
            new Column("mode", c => c.Mode, (c, v) => c.Mode = v),
            new Column("preset", c => c.Preset, (c, v) => c.Preset = v),
            new Column("settings", c => c.Settings, (c, v) => c.Settings = v),
            new Column("samples", c => c.Samples.ToString(Inv), (c, v) => c.Samples = I(v)),
            new Column("vehicles", c => F(c.Vehicles), (c, v) => c.Vehicles = P(v)),
            new Column("avg_speed_pct", c => F(c.AvgSpeedPct), (c, v) => c.AvgSpeedPct = P(v)),
            new Column("queued_pct", c => F(c.QueuedPct), (c, v) => c.QueuedPct = P(v)),
            new Column("local_street_pct", c => F(c.LocalStreetPct), (c, v) => c.LocalStreetPct = P(v)),
            new Column("jam_warnings", c => F(c.JamWarnings), (c, v) => c.JamWarnings = P(v)),
            new Column("jam_warnings_max", c => c.JamWarningsMax.ToString(Inv), (c, v) => c.JamWarningsMax = I(v)),
            new Column("flow_pct", c => F(c.FlowPct), (c, v) => c.FlowPct = P(v)),
            new Column("nav_progress_kmh", c => F(c.NavProgressKmh), (c, v) => c.NavProgressKmh = P(v)),
            new Column("other_progress_kmh", c => F(c.OtherProgressKmh), (c, v) => c.OtherProgressKmh = P(v)),
            new Column("nav_share_pct", c => F(c.NavSharePct), (c, v) => c.NavSharePct = P(v)),
            new Column("reroutes", c => c.Reroutes.ToString(Inv), (c, v) => c.Reroutes = I(v)),
            new Column("reroute_cars", c => c.RerouteCars.ToString(Inv), (c, v) => c.RerouteCars = I(v)),
            new Column("reroute_trucks", c => c.RerouteTrucks.ToString(Inv), (c, v) => c.RerouteTrucks = I(v)),
            new Column("reroute_taxis", c => c.RerouteTaxis.ToString(Inv), (c, v) => c.RerouteTaxis = I(v)),
            new Column("reroute_better", c => c.RerouteBetter.ToString(Inv), (c, v) => c.RerouteBetter = I(v)),
            new Column("reroute_no_better", c => c.RerouteNoBetter.ToString(Inv), (c, v) => c.RerouteNoBetter = I(v)),
            new Column("reroute_saved_pct", c => F(c.RerouteSavedPct), (c, v) => c.RerouteSavedPct = P(v)),
            new Column("reroute_held_back", c => c.RerouteHeldBack.ToString(Inv), (c, v) => c.RerouteHeldBack = I(v)),
            new Column("planner_speed_pct", c => F(c.PlannerSpeedPct), (c, v) => c.PlannerSpeedPct = P(v)),
            new Column("planner_jammed_pct", c => F(c.PlannerJammedPct), (c, v) => c.PlannerJammedPct = P(v)),
            new Column("mod_ms", c => F(c.ModMs), (c, v) => c.ModMs = P(v)),
            new Column("car_commute_pct", c => F(c.CarCommutePct), (c, v) => c.CarCommutePct = P(v)),
            new Column("car_home_pct", c => F(c.CarHomePct), (c, v) => c.CarHomePct = P(v)),
            new Column("car_shop_pct", c => F(c.CarShopPct), (c, v) => c.CarShopPct = P(v)),
            new Column("car_leisure_pct", c => F(c.CarLeisurePct), (c, v) => c.CarLeisurePct = P(v)),
            new Column("car_other_pct", c => F(c.CarOtherPct), (c, v) => c.CarOtherPct = P(v)),
            new Column("queued_commute_pct", c => F(c.QueuedCommutePct), (c, v) => c.QueuedCommutePct = P(v)),
            new Column("queued_home_pct", c => F(c.QueuedHomePct), (c, v) => c.QueuedHomePct = P(v)),
            new Column("queued_shop_pct", c => F(c.QueuedShopPct), (c, v) => c.QueuedShopPct = P(v)),
            new Column("queued_leisure_pct", c => F(c.QueuedLeisurePct), (c, v) => c.QueuedLeisurePct = P(v)),
            new Column("queued_other_pct", c => F(c.QueuedOtherPct), (c, v) => c.QueuedOtherPct = P(v)),
            new Column("shop_trips", c => c.ShopTrips.ToString(Inv), (c, v) => c.ShopTrips = I(v)),
            new Column("shop_places", c => c.ShopPlaces.ToString(Inv), (c, v) => c.ShopPlaces = I(v)),
            new Column("shop_top5_pct", c => F(c.ShopTop5Pct), (c, v) => c.ShopTop5Pct = P(v)),
            new Column("shop_km", c => F(c.ShopKm), (c, v) => c.ShopKm = P(v)),
            new Column("leisure_trips", c => c.LeisureTrips.ToString(Inv), (c, v) => c.LeisureTrips = I(v)),
            new Column("leisure_places", c => c.LeisurePlaces.ToString(Inv), (c, v) => c.LeisurePlaces = I(v)),
            new Column("leisure_top5_pct", c => F(c.LeisureTop5Pct), (c, v) => c.LeisureTop5Pct = P(v)),
            new Column("leisure_km", c => F(c.LeisureKm), (c, v) => c.LeisureKm = P(v)),
            new Column("speed_setting", c => F(c.SpeedSetting), (c, v) => c.SpeedSetting = P(v)),
            new Column("sim_fps", c => F(c.SimFps), (c, v) => c.SimFps = P(v)),
            new Column("render_fps", c => F(c.RenderFps), (c, v) => c.RenderFps = P(v)),
            new Column("mod_cpu_pct", c => F(c.ModCpuPct), (c, v) => c.ModCpuPct = P(v)),
            new Column("mod_mem_mb", c => F(c.ModMemMb), (c, v) => c.ModMemMb = P(v)),
            new Column("game_mem_mb", c => F(c.GameMemMb), (c, v) => c.GameMemMb = P(v)),
        };

        internal static string CsvHeaderLine => string.Join(",", Columns.Select(col => col.Name));

        internal string ToCsv()
        {
            var parts = new string[Columns.Length];
            for (int i = 0; i < Columns.Length; i++) parts[i] = Escape(Columns[i].Get(this));
            return string.Join(",", parts);
        }

        /// <summary>Maps a file's header line to our columns (index in the file for each of our columns, or -1).</summary>
        internal static int[] MapHeader(string headerLine)
        {
            List<string> names = SplitCsv(headerLine);
            var map = new int[Columns.Length];
            for (int i = 0; i < Columns.Length; i++) map[i] = names.IndexOf(Columns[i].Name);
            return map;
        }

        internal static TrafficCapture FromCsv(string line, int[] map)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            List<string> f = SplitCsv(line);
            var r = new TrafficCapture();
            for (int i = 0; i < Columns.Length; i++)
            {
                int at = map[i];
                if (at >= 0 && at < f.Count) Columns[i].Set(r, f[at]);
            }
            return string.IsNullOrEmpty(r.GameTime) ? null : r;
        }

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append('{');
            Str(sb, "t", GameTime); Str(sb, "run", RunId); Str(sb, "mode", Mode); Str(sb, "preset", Preset);
            Num(sb, "h", GameHour); Num(sb, "n", Samples); Num(sb, "veh", Vehicles); Num(sb, "speed", AvgSpeedPct);
            Num(sb, "queued", QueuedPct); Num(sb, "local", LocalStreetPct); Num(sb, "jams", JamWarnings);
            Num(sb, "jamsMax", JamWarningsMax); Num(sb, "flow", FlowPct); Num(sb, "navKmh", NavProgressKmh);
            Num(sb, "otherKmh", OtherProgressKmh); Num(sb, "navShare", NavSharePct); Num(sb, "rr", Reroutes);
            Num(sb, "rrCars", RerouteCars); Num(sb, "rrTrucks", RerouteTrucks); Num(sb, "rrTaxis", RerouteTaxis);
            Num(sb, "rrBetter", RerouteBetter); Num(sb, "rrNoBetter", RerouteNoBetter); Num(sb, "rrSaved", RerouteSavedPct);
            Num(sb, "rrHeld", RerouteHeldBack); Num(sb, "planSpeed", PlannerSpeedPct); Num(sb, "planJammed", PlannerJammedPct);
            Num(sb, "ms", ModMs);
            // Destinations: -1 marks hours recorded before these were measured.
            bool d = ShopTrips + LeisureTrips + CarCommutePct + CarHomePct + CarShopPct + CarLeisurePct + CarOtherPct > 0;
            Num(sb, "cCom", d ? CarCommutePct : -1); Num(sb, "cHome", d ? CarHomePct : -1); Num(sb, "cShop", d ? CarShopPct : -1);
            Num(sb, "cLeis", d ? CarLeisurePct : -1); Num(sb, "cOth", d ? CarOtherPct : -1);
            Num(sb, "qCom", d ? QueuedCommutePct : -1); Num(sb, "qHome", d ? QueuedHomePct : -1); Num(sb, "qShop", d ? QueuedShopPct : -1);
            Num(sb, "qLeis", d ? QueuedLeisurePct : -1); Num(sb, "qOth", d ? QueuedOtherPct : -1);
            Num(sb, "sTrips", d ? ShopTrips : -1); Num(sb, "sPlaces", d ? ShopPlaces : -1);
            Num(sb, "sTop5", d ? ShopTop5Pct : -1); Num(sb, "sKm", d ? ShopKm : -1);
            Num(sb, "lTrips", d ? LeisureTrips : -1); Num(sb, "lPlaces", d ? LeisurePlaces : -1);
            Num(sb, "lTop5", d ? LeisureTop5Pct : -1); Num(sb, "lKm", d ? LeisureKm : -1);
            // Performance: 0 means an older row without these numbers.
            Num(sb, "spd", SpeedSetting > 0 ? SpeedSetting : -1); Num(sb, "simFps", SimFps > 0 ? SimFps : -1);
            Num(sb, "fps", RenderFps > 0 ? RenderFps : -1); Num(sb, "cpu", ModCpuPct > 0 ? ModCpuPct : -1);
            Num(sb, "modMb", ModMemMb > 0 ? ModMemMb : -1); Num(sb, "gameMb", GameMemMb > 0 ? GameMemMb : -1, last: true);
            sb.Append('}');
        }

        internal string ToLogLine()
        {
            var sb = new StringBuilder();
            sb.Append(string.Format(Inv, "Traffic report {0} [{1}] run {2}: ", GameTime, Settings, RunId));
            sb.Append(string.Format(Inv, "{0:0} vehicles, real avg speed {1:0.0}% of limit, {2:0.0}% queued, {3:0.0}% on local streets, ",
                Vehicles, AvgSpeedPct, QueuedPct, LocalStreetPct));
            sb.Append(string.Format(Inv, "{0:0.0} jam warnings (max {1}), game traffic flow {2:0}%. ", JamWarnings, JamWarningsMax, FlowPct));
            sb.Append(string.Format(Inv, "Progress to destination: with navigation ({0:0}%) {1:0.0} km/h, others {2:0.0} km/h. ",
                NavSharePct, NavProgressKmh, OtherProgressKmh));
            if (Mode == "Live")
            {
                sb.Append(string.Format(Inv, "Re-routes {0} ({1} cars, {2} trucks, {3} taxis, {4} held back): {5} faster (avg {6:0}% saved), {7} no better. ",
                    Reroutes, RerouteCars, RerouteTrucks, RerouteTaxis, RerouteHeldBack, RerouteBetter, RerouteSavedPct, RerouteNoBetter));
                sb.Append(string.Format(Inv, "Planner view: {0:0}% speed, {1:0.0}% jammed. ", PlannerSpeedPct, PlannerJammedPct));
            }
            sb.Append(string.Format(Inv, "Queued cars by trip: {0:0}% commute, {1:0}% home, {2:0}% shopping, {3:0}% leisure, {4:0}% other. ",
                QueuedCommutePct, QueuedHomePct, QueuedShopPct, QueuedLeisurePct, QueuedOtherPct));
            sb.Append(string.Format(Inv, "Shopping: {0} trips to {1} places (top 5: {2:0}%), {3:0.0} km from home. Leisure: {4} trips to {5} places (top 5: {6:0}%), {7:0.0} km. ",
                ShopTrips, ShopPlaces, ShopTop5Pct, ShopKm, LeisureTrips, LeisurePlaces, LeisureTop5Pct, LeisureKm));
            sb.Append(ModMs >= 0 ? string.Format(Inv, "Mod cost {0:0.00} ms per run. ", ModMs) : "Mod cost not measured. ");
            sb.Append(string.Format(Inv, "Performance: speed setting {0}, {1:0} sim frames/s, {2:0} FPS, mod {3:0.00}% of one core, mod memory {4:0.0} MB, game memory {5:0} MB.",
                SpeedSetting > 0 ? SpeedSetting.ToString("0.#", Inv) : "mixed", SimFps, RenderFps, ModCpuPct, ModMemMb, GameMemMb));
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("0.###", Inv);
        private static float P(string s) => float.TryParse(s, NumberStyles.Float, Inv, out float v) ? v : 0f;
        private static int I(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out int v) ? v : 0;

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        internal static List<string> SplitCsv(string line)
        {
            var result = new List<string>();
            var cur = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else if (ch == '"') quoted = false;
                    else cur.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { result.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(ch);
            }
            result.Add(cur.ToString());
            return result;
        }

        internal static void Str(StringBuilder sb, string key, string value, bool last = false)
        {
            sb.Append('"').Append(key).Append("\":\"");
            foreach (char ch in value ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            sb.Append('"');
            if (!last) sb.Append(',');
        }

        internal static void Num(StringBuilder sb, string key, double value, bool last = false)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###", Inv));
            if (!last) sb.Append(',');
        }
    }

    /// <summary>One busy place (street or junction) in one game hour of one run.</summary>
    internal sealed class HotspotRow
    {
        public string RunId = "";
        public string City = "";
        public string GameTime = "";
        public string Place = "";
        public string Kind = "";       // "street" or "junction"
        public float Queued;           // average vehicles waiting in a queue there
        public float Jams;             // average jam warnings there

        internal const string Header = "run_id,city,game_time,place,kind,queued,jams";

        internal string ToCsv() => string.Join(",", new[]
        {
            Esc(RunId), Esc(City), Esc(GameTime), Esc(Place), Kind,
            Queued.ToString("0.###", CultureInfo.InvariantCulture), Jams.ToString("0.###", CultureInfo.InvariantCulture),
        });

        internal static HotspotRow FromCsv(string line, List<string> header)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            List<string> f = TrafficCapture.SplitCsv(line);
            string Get(string name) { int i = header.IndexOf(name); return i >= 0 && i < f.Count ? f[i] : ""; }
            float.TryParse(Get("queued"), NumberStyles.Float, CultureInfo.InvariantCulture, out float q);
            float.TryParse(Get("jams"), NumberStyles.Float, CultureInfo.InvariantCulture, out float j);
            var r = new HotspotRow { RunId = Get("run_id"), City = Get("city"), GameTime = Get("game_time"), Place = TrafficReportSystem.CleanName(Get("place")), Kind = Get("kind"), Queued = q, Jams = j };
            return string.IsNullOrEmpty(r.Place) ? null : r;
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }

    /// <summary>The live numbers for "right now".</summary>
    internal sealed class TrafficSnapshot
    {
        public string Mode = "";
        public string Preset = "";
        public string Settings = "";
        public string RunId = "";
        public string GameTime = "";
        public int Vehicles;
        public float AvgSpeedPct;
        public float QueuedPct;
        public float LocalStreetPct;
        public int JamWarnings;
        public float NavProgressKmh;
        public float OtherProgressKmh;
        public float NavSharePct;
        public float ModMs = -1;

        internal string ToJson()
        {
            var sb = new StringBuilder("{");
            TrafficCapture.Str(sb, "mode", Mode); TrafficCapture.Str(sb, "preset", Preset);
            TrafficCapture.Str(sb, "settings", Settings); TrafficCapture.Str(sb, "run", RunId); TrafficCapture.Str(sb, "t", GameTime);
            TrafficCapture.Num(sb, "veh", Vehicles); TrafficCapture.Num(sb, "speed", AvgSpeedPct);
            TrafficCapture.Num(sb, "queued", QueuedPct); TrafficCapture.Num(sb, "local", LocalStreetPct);
            TrafficCapture.Num(sb, "jams", JamWarnings); TrafficCapture.Num(sb, "navKmh", NavProgressKmh);
            TrafficCapture.Num(sb, "otherKmh", OtherProgressKmh); TrafficCapture.Num(sb, "navShare", NavSharePct);
            TrafficCapture.Num(sb, "ms", ModMs, last: true);
            sb.Append('}');
            return sb.ToString();
        }
    }

    /// <summary>
    /// All captures, in memory and in <c>ModsData/LiveTrafficRouting/captures.csv</c> (one row per game hour, all cities).
    /// Main thread only.
    /// </summary>
    internal static class TrafficReportStore
    {
        /// <summary>
        /// How many game hours of statistics to keep, all cities together ("Keep statistics for"). Above it, the oldest
        /// whole runs move to archive files. The newest Native run of each city, pinned runs and the run being recorded
        /// are never moved.
        /// </summary>
        public static int KeepHours { get; set; } = 365 * 24;
        /// <summary>The run being recorded right now (never trimmed).</summary>
        public static string CurrentRunId { get; set; } = "";
        // How many backup / archive files to keep (oldest deleted first).
        private const int kMaxBackups = 5;
        // Captures of the current city sent to the panel (newest).
        private const int kMaxUiCaptures = 24 * 60;

        private static readonly List<TrafficCapture> s_Captures = new List<TrafficCapture>();
        private static readonly List<HotspotRow> s_Hotspots = new List<HotspotRow>();
        private static readonly HashSet<string> s_Pinned = new HashSet<string>();
        // Place name -> the street (aggregate) or junction (node) entity last seen with that name, this session only
        // (entity ids are not stable across loads). Used to show a hotspot on the map.
        private static readonly Dictionary<string, Unity.Entities.Entity> s_PlaceEntities = new Dictionary<string, Unity.Entities.Entity>();

        public static void RememberPlace(string place, Unity.Entities.Entity entity) => s_PlaceEntities[place] = entity;

        public static bool TryGetPlace(string place, out Unity.Entities.Entity entity) => s_PlaceEntities.TryGetValue(place, out entity);
        private static bool s_Loaded;

        public static TrafficSnapshot Current { get; private set; } = new TrafficSnapshot();
        private static string s_CurrentCity = "";
        /// <summary>The city being played. Changing it refreshes the panel's data (it only shows this city).</summary>
        public static string CurrentCity
        {
            get => s_CurrentCity;
            set
            {
                value = value ?? "";
                if (value == s_CurrentCity) return;
                s_CurrentCity = value;
                DataChanged();
            }
        }
        /// <summary>Changes whenever there is something new for the UI.</summary>
        public static int Version { get; private set; }
        /// <summary>Changes only when recorded data changes (about once per game hour): history, runs, hotspots, pins.</summary>
        public static int DataVersion { get; private set; }

        private static void DataChanged()
        {
            Version++;
            DataVersion++;
        }

        public static string Directory_ => Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", nameof(LiveTrafficRouting));
        public static string FilePath => Path.Combine(Directory_, "captures.csv");
        public static string HotspotPath => Path.Combine(Directory_, "hotspots.csv");
        public static string PinnedPath => Path.Combine(Directory_, "pinned-runs.txt");

        public static int CaptureCount => s_Captures.Count;
        public static int HotspotCount => s_Hotspots.Count;

        public static TrafficCapture Last => s_Captures.Count > 0 ? s_Captures[s_Captures.Count - 1] : null;

        public static void SetCurrent(TrafficSnapshot snapshot)
        {
            Current = snapshot;
            Version++;
        }

        public static void Add(TrafficCapture capture)
        {
            EnsureLoaded();
            s_Captures.Add(capture);
            DataChanged();
            try
            {
                Directory.CreateDirectory(Directory_);
                bool writeHeader = !File.Exists(FilePath);
                using (var w = new StreamWriter(FilePath, append: true, Encoding.UTF8))
                {
                    if (writeHeader) w.WriteLine(TrafficCapture.CsvHeaderLine);
                    w.WriteLine(capture.ToCsv());
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not write traffic capture: {e.Message}");
            }
            TrimToBudget();
        }

        public static void AddHotspots(List<HotspotRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            EnsureLoaded();
            s_Hotspots.AddRange(rows);
            DataChanged();
            try
            {
                Directory.CreateDirectory(Directory_);
                bool writeHeader = !File.Exists(HotspotPath);
                using (var w = new StreamWriter(HotspotPath, append: true, Encoding.UTF8))
                {
                    if (writeHeader) w.WriteLine(HotspotRow.Header);
                    foreach (HotspotRow r in rows) w.WriteLine(r.ToCsv());
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not write traffic hotspots: {e.Message}");
            }
        }

        /// <summary>
        /// Busiest places per run of the current city: {"runId":[{p,k,q,j,h}]}. Averages over all game hours of the
        /// run (an hour where a place wasn't in the top list counts as 0 there).
        /// </summary>
        public static string HotspotsJson(int perRun)
        {
            EnsureLoaded();
            var hours = new Dictionary<string, int>();
            foreach (TrafficCapture c in s_Captures)
            {
                if (c.City != CurrentCity) continue;
                hours.TryGetValue(c.RunId, out int n);
                hours[c.RunId] = n + 1;
            }
            var perRunPlaces = new Dictionary<string, Dictionary<string, float[]>>();
            var kinds = new Dictionary<string, string>();
            foreach (HotspotRow r in s_Hotspots)
            {
                if (r.City != CurrentCity || !hours.ContainsKey(r.RunId)) continue;
                if (!perRunPlaces.TryGetValue(r.RunId, out Dictionary<string, float[]> places))
                {
                    places = new Dictionary<string, float[]>();
                    perRunPlaces[r.RunId] = places;
                }
                if (!places.TryGetValue(r.Place, out float[] v)) { v = new float[3]; places[r.Place] = v; }
                v[0] += r.Queued; v[1] += r.Jams; v[2] += 1;
                kinds[r.Place] = r.Kind;
            }
            var sb = new StringBuilder("{");
            bool firstRun = true;
            foreach (KeyValuePair<string, Dictionary<string, float[]>> run in perRunPlaces)
            {
                float runHours = Math.Max(1, hours[run.Key]);
                if (!firstRun) sb.Append(',');
                firstRun = false;
                sb.Append('"');
                foreach (char ch in run.Key) if (ch != '"' && ch != '\\') sb.Append(ch);
                sb.Append("\":[");
                bool firstPlace = true;
                foreach (KeyValuePair<string, float[]> p in run.Value.OrderByDescending(x => x.Value[0] + 5f * x.Value[1]).Take(perRun))
                {
                    if (!firstPlace) sb.Append(',');
                    firstPlace = false;
                    sb.Append('{');
                    TrafficCapture.Str(sb, "p", p.Key);
                    TrafficCapture.Str(sb, "k", kinds.TryGetValue(p.Key, out string k) ? k : "");
                    TrafficCapture.Num(sb, "q", p.Value[0] / runHours);
                    TrafficCapture.Num(sb, "j", p.Value[1] / runHours);
                    TrafficCapture.Num(sb, "h", p.Value[2], last: true);
                    sb.Append('}');
                }
                sb.Append(']');
            }
            sb.Append('}');
            return sb.ToString();
        }

        // ---------------- Runs ----------------

        internal sealed class RunSummary
        {
            public string Id, Mode, Preset, Settings, First, Last;
            public int Hours;
            public bool Pinned;
        }

        /// <summary>Runs of the current city, oldest first.</summary>
        public static List<RunSummary> Runs()
        {
            EnsureLoaded();
            var runs = new List<RunSummary>();
            var byId = new Dictionary<string, RunSummary>();
            foreach (TrafficCapture c in s_Captures)
            {
                if (c.City != CurrentCity) continue;
                if (!byId.TryGetValue(c.RunId, out RunSummary r))
                {
                    r = new RunSummary { Id = c.RunId, Mode = c.Mode, Preset = c.Preset, Settings = c.Settings, First = c.GameTime, Last = c.GameTime };
                    byId[c.RunId] = r;
                    runs.Add(r);
                }
                r.Hours++;
                r.Pinned = s_Pinned.Contains(c.RunId);
                if (string.CompareOrdinal(c.GameTime, r.First) < 0) r.First = c.GameTime;
                if (string.CompareOrdinal(c.GameTime, r.Last) > 0) r.Last = c.GameTime;
            }
            return runs;
        }

        public static string RunsJson()
        {
            var sb = new StringBuilder("[");
            List<RunSummary> runs = Runs();
            HashSet<string> baselines = BaselineRuns();
            for (int i = 0; i < runs.Count; i++)
            {
                RunSummary r = runs[i];
                sb.Append('{');
                TrafficCapture.Str(sb, "id", r.Id); TrafficCapture.Str(sb, "mode", r.Mode); TrafficCapture.Str(sb, "preset", r.Preset);
                TrafficCapture.Str(sb, "settings", r.Settings); TrafficCapture.Str(sb, "first", r.First); TrafficCapture.Str(sb, "last", r.Last);
                TrafficCapture.Num(sb, "hours", r.Hours);
                TrafficCapture.Num(sb, "pinned", r.Pinned ? 1 : 0);
                TrafficCapture.Num(sb, "baseline", baselines.Contains(r.Id) ? 1 : 0, last: true);
                sb.Append('}');
                if (i < runs.Count - 1) sb.Append(',');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Captures of the current city, newest last, as a JSON array for the UI.</summary>
        public static string HistoryJson()
        {
            EnsureLoaded();
            // The newest game hours of the city, plus all hours of its baseline and pinned runs (so an older reference
            // run is always complete in the comparisons).
            HashSet<string> always = BaselineRuns();
            always.UnionWith(s_Pinned);
            var picked = new List<TrafficCapture>();
            int recent = 0;
            for (int i = s_Captures.Count - 1; i >= 0; i--)
            {
                TrafficCapture c = s_Captures[i];
                if (c.City != CurrentCity) continue;
                if (recent < kMaxUiCaptures) { picked.Add(c); recent++; }
                else if (always.Contains(c.RunId)) picked.Add(c);
            }
            var sb = new StringBuilder("[");
            for (int i = picked.Count - 1; i >= 0; i--)
            {
                picked[i].AppendJson(sb);
                if (i > 0) sb.Append(',');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // ---------------- Deleting ----------------

        public static int DeleteCurrentCity() => Delete(c => c.City == CurrentCity, "all statistics of this city");
        public static int DeleteLiveRuns() => Delete(c => c.City == CurrentCity && c.Mode == "Live", "all live runs of this city");
        public static int DeleteRun(string runId) => Delete(c => c.City == CurrentCity && c.RunId == runId, "run " + runId);

        private static void DeleteHotspotsOfRuns(HashSet<string> runIds, string city)
        {
            int removed = s_Hotspots.RemoveAll(h => h.City == city && runIds.Contains(h.RunId));
            if (removed == 0) return;
            CopyAside(HotspotPath, "hotspots-backup-");
            RewriteHotspots();
        }

        /// <summary>Removes matching captures from memory and the file. A backup of the file is kept first.</summary>
        private static int Delete(Predicate<TrafficCapture> match, string what)
        {
            EnsureLoaded();
            var runIds = new HashSet<string>(s_Captures.Where(c => match(c)).Select(c => c.RunId));
            string city = CurrentCity;
            int removed = s_Captures.RemoveAll(match);
            DataChanged();
            if (removed == 0) return 0;
            try
            {
                string backup = CopyAside(FilePath, "captures-backup-");
                RewriteFile();
                DeleteHotspotsOfRuns(runIds, city);
                Logger.Info($"Deleted {what}: {removed} game hours. Backup: {backup}");
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not rewrite traffic captures: {e.Message}");
            }
            return removed;
        }

        // ---------------- File handling ----------------

        private static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                string[] lines = File.ReadAllLines(FilePath, Encoding.UTF8);
                if (lines.Length == 0) return;
                int[] map = TrafficCapture.MapHeader(lines[0]);
                for (int i = 1; i < lines.Length; i++)
                {
                    TrafficCapture c = TrafficCapture.FromCsv(lines[i], map);
                    if (c != null) s_Captures.Add(c);
                }
                bool upgraded = AssignLegacyRuns();
                Logger.Info($"Loaded {s_Captures.Count} traffic captures from {FilePath}");

                // Older format (different columns): rewrite once in the current format, keeping a backup.
                if (upgraded || lines[0] != TrafficCapture.CsvHeaderLine)
                {
                    string backup = CopyAside(FilePath, "captures-backup-");
                    RewriteFile();
                    Logger.Info($"Upgraded the statistics file to the current format. Backup: {backup}");
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read traffic captures: {e.Message}");
            }
            try
            {
                if (!File.Exists(HotspotPath)) return;
                string[] lines = File.ReadAllLines(HotspotPath, Encoding.UTF8);
                if (lines.Length == 0) return;
                List<string> header = TrafficCapture.SplitCsv(lines[0]);
                for (int i = 1; i < lines.Length; i++)
                {
                    HotspotRow r = HotspotRow.FromCsv(lines[i], header);
                    if (r != null) s_Hotspots.Add(r);
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read traffic hotspots: {e.Message}");
            }
            try
            {
                if (File.Exists(PinnedPath))
                {
                    foreach (string line in File.ReadAllLines(PinnedPath, Encoding.UTF8))
                    {
                        if (!string.IsNullOrWhiteSpace(line)) s_Pinned.Add(line.Trim());
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read pinned runs: {e.Message}");
            }
        }

        /// <summary>
        /// Rows from before runs existed: a new run starts whenever the city, mode or preset changes, or game time
        /// goes back (a save was reloaded).
        /// </summary>
        private static bool AssignLegacyRuns()
        {
            bool changed = false;
            TrafficCapture prev = null;
            string run = null;
            int n = 0;
            foreach (TrafficCapture c in s_Captures)
            {
                if (!string.IsNullOrEmpty(c.RunId)) { prev = c; run = null; continue; }
                bool newRun = prev == null || run == null || prev.City != c.City || prev.Mode != c.Mode || prev.Preset != c.Preset ||
                              string.CompareOrdinal(c.GameTime, prev.GameTime) <= 0;
                if (newRun) run = "old-" + (++n).ToString(CultureInfo.InvariantCulture);
                c.RunId = run;
                if (string.IsNullOrEmpty(c.Settings)) c.Settings = c.Mode == "Live" ? c.Preset + " (settings not recorded)" : "Native (mod off)";
                if (c.Mode == "Mixed") c.Mode = "Live";
                changed = true;
                prev = c;
            }
            return changed;
        }

        private static void RewriteFile()
        {
            Directory.CreateDirectory(Directory_);
            string tmp = FilePath + ".tmp";
            using (var w = new StreamWriter(tmp, append: false, Encoding.UTF8))
            {
                w.WriteLine(TrafficCapture.CsvHeaderLine);
                foreach (TrafficCapture c in s_Captures) w.WriteLine(c.ToCsv());
            }
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        // ---------------- Keeping and trimming ----------------

        public static void TogglePin(string runId)
        {
            EnsureLoaded();
            if (!s_Pinned.Remove(runId)) s_Pinned.Add(runId);
            DataChanged();
            try
            {
                Directory.CreateDirectory(Directory_);
                File.WriteAllLines(PinnedPath, s_Pinned.ToArray(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not save pinned runs: {e.Message}");
            }
        }

        /// <summary>The newest Native run of each city: the baseline for comparisons.</summary>
        private static HashSet<string> BaselineRuns()
        {
            var newest = new Dictionary<string, TrafficCapture>();
            foreach (TrafficCapture c in s_Captures)
            {
                if (c.Mode != "Native") continue;
                if (!newest.TryGetValue(c.City, out TrafficCapture n) || c.RealTime >= n.RealTime) newest[c.City] = c;
            }
            return new HashSet<string>(newest.Values.Select(c => c.RunId));
        }

        /// <summary>Applies "Keep statistics for": moves the oldest whole runs to archive files until under the limit.</summary>
        public static void TrimToBudget()
        {
            EnsureLoaded();
            if (s_Captures.Count <= KeepHours) return;
            try
            {
                HashSet<string> keep = BaselineRuns();
                keep.UnionWith(s_Pinned);
                if (!string.IsNullOrEmpty(CurrentRunId)) keep.Add(CurrentRunId);

                // Oldest first, by when the run started (real time).
                var order = s_Captures.GroupBy(c => c.RunId)
                    .Select(g => new { Id = g.Key, Start = g.Min(c => c.RealTime), Count = g.Count() })
                    .OrderBy(r => r.Start)
                    .ToList();
                var drop = new HashSet<string>();
                int remaining = s_Captures.Count;
                foreach (var r in order)
                {
                    if (remaining <= KeepHours) break;
                    if (keep.Contains(r.Id)) continue;
                    drop.Add(r.Id);
                    remaining -= r.Count;
                }
                if (drop.Count == 0) return;

                string stamp = Stamp();
                ArchiveRows(Path.Combine(Directory_, "captures-archive-" + stamp + ".csv"), TrafficCapture.CsvHeaderLine,
                    s_Captures.Where(c => drop.Contains(c.RunId)).Select(c => c.ToCsv()));
                ArchiveRows(Path.Combine(Directory_, "hotspots-archive-" + stamp + ".csv"), HotspotRow.Header,
                    s_Hotspots.Where(h => drop.Contains(h.RunId)).Select(h => h.ToCsv()));
                int removed = s_Captures.RemoveAll(c => drop.Contains(c.RunId));
                s_Hotspots.RemoveAll(h => drop.Contains(h.RunId));
                RewriteFile();
                RewriteHotspots();
                Prune("captures-archive-");
                Prune("hotspots-archive-");
                DataChanged();
                Logger.Info($"Keep statistics for {KeepHours / 24} game days: moved {drop.Count} oldest runs ({removed} game hours) to archive files.");
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not trim statistics: {e.Message}");
            }
        }

        private static void ArchiveRows(string path, string header, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return;
            using (var w = new StreamWriter(path, append: false, Encoding.UTF8))
            {
                w.WriteLine(header);
                foreach (string l in list) w.WriteLine(l);
            }
        }

        private static void RewriteHotspots()
        {
            Directory.CreateDirectory(Directory_);
            string tmp = HotspotPath + ".tmp";
            using (var w = new StreamWriter(tmp, append: false, Encoding.UTF8))
            {
                w.WriteLine(HotspotRow.Header);
                foreach (HotspotRow r in s_Hotspots) w.WriteLine(r.ToCsv());
            }
            if (File.Exists(HotspotPath)) File.Delete(HotspotPath);
            File.Move(tmp, HotspotPath);
        }

        private static string CopyAside(string file, string prefix)
        {
            if (!File.Exists(file)) return "(no file)";
            string path = Path.Combine(Directory_, prefix + Stamp() + ".csv");
            File.Copy(file, path, overwrite: true);
            Prune(prefix);
            return path;
        }

        /// <summary>Keeps only the newest kMaxBackups files with this prefix.</summary>
        private static void Prune(string prefix)
        {
            try
            {
                string[] files = Directory.GetFiles(Directory_, prefix + "*.csv").OrderBy(f => f, StringComparer.Ordinal).ToArray();
                for (int i = 0; i < files.Length - kMaxBackups; i++) File.Delete(files[i]);
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not remove old statistics backups: {e.Message}");
            }
        }

        private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }
}
