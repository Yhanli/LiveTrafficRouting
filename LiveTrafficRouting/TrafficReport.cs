using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LiveTrafficRouting
{
    /// <summary>One game hour of traffic statistics.</summary>
    internal sealed class TrafficCapture
    {
        public DateTime RealTime;
        public string GameTime = "";       // "yyyy-MM-dd HH:00" in game time
        public int GameHour;
        public string City = "";
        public string Mode = "";           // "Native", "Live" or "Mixed" (mode changed during the hour)
        public string Preset = "";

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

        public float PlannerSpeedPct;      // what the route planner "sees" (with penalties); Live mode only
        public float PlannerJammedPct;

        public float ModMs;                // our own jobs, one timed run (flow + re-route)

        internal static readonly string[] CsvHeader =
        {
            "real_time", "game_time", "game_hour", "city", "mode", "preset", "samples", "vehicles",
            "avg_speed_pct", "queued_pct", "local_street_pct", "jam_warnings", "jam_warnings_max", "flow_pct",
            "nav_progress_kmh", "other_progress_kmh", "nav_share_pct",
            "reroutes", "reroute_cars", "reroute_trucks", "reroute_taxis", "reroute_better", "reroute_no_better", "reroute_saved_pct",
            "planner_speed_pct", "planner_jammed_pct", "mod_ms",
        };

        internal string ToCsv()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            string[] v =
            {
                RealTime.ToString("yyyy-MM-dd HH:mm:ss", c), GameTime, GameHour.ToString(c), Escape(City), Mode, Preset,
                Samples.ToString(c), F(Vehicles), F(AvgSpeedPct), F(QueuedPct), F(LocalStreetPct), F(JamWarnings),
                JamWarningsMax.ToString(c), F(FlowPct), F(NavProgressKmh), F(OtherProgressKmh), F(NavSharePct),
                Reroutes.ToString(c), RerouteCars.ToString(c), RerouteTrucks.ToString(c), RerouteTaxis.ToString(c),
                RerouteBetter.ToString(c), RerouteNoBetter.ToString(c), F(RerouteSavedPct),
                F(PlannerSpeedPct), F(PlannerJammedPct), F(ModMs),
            };
            return string.Join(",", v);
        }

        internal static TrafficCapture FromCsv(string line)
        {
            List<string> f = SplitCsv(line);
            if (f.Count < CsvHeader.Length) return null;
            CultureInfo c = CultureInfo.InvariantCulture;
            int i = 0;
            var r = new TrafficCapture();
            DateTime.TryParse(f[i++], c, DateTimeStyles.None, out r.RealTime);
            r.GameTime = f[i++];
            r.GameHour = I(f[i++]);
            r.City = f[i++];
            r.Mode = f[i++];
            r.Preset = f[i++];
            r.Samples = I(f[i++]);
            r.Vehicles = P(f[i++]); r.AvgSpeedPct = P(f[i++]); r.QueuedPct = P(f[i++]); r.LocalStreetPct = P(f[i++]);
            r.JamWarnings = P(f[i++]); r.JamWarningsMax = I(f[i++]); r.FlowPct = P(f[i++]);
            r.NavProgressKmh = P(f[i++]); r.OtherProgressKmh = P(f[i++]); r.NavSharePct = P(f[i++]);
            r.Reroutes = I(f[i++]); r.RerouteCars = I(f[i++]); r.RerouteTrucks = I(f[i++]); r.RerouteTaxis = I(f[i++]);
            r.RerouteBetter = I(f[i++]); r.RerouteNoBetter = I(f[i++]); r.RerouteSavedPct = P(f[i++]);
            r.PlannerSpeedPct = P(f[i++]); r.PlannerJammedPct = P(f[i++]); r.ModMs = P(f[i++]);
            return r;
        }

        internal void AppendJson(StringBuilder sb)
        {
            sb.Append('{');
            Str(sb, "t", GameTime); Str(sb, "mode", Mode); Str(sb, "preset", Preset);
            Num(sb, "h", GameHour); Num(sb, "veh", Vehicles); Num(sb, "speed", AvgSpeedPct); Num(sb, "queued", QueuedPct);
            Num(sb, "local", LocalStreetPct); Num(sb, "jams", JamWarnings); Num(sb, "jamsMax", JamWarningsMax);
            Num(sb, "flow", FlowPct); Num(sb, "navKmh", NavProgressKmh); Num(sb, "otherKmh", OtherProgressKmh);
            Num(sb, "navShare", NavSharePct); Num(sb, "rr", Reroutes); Num(sb, "rrCars", RerouteCars);
            Num(sb, "rrTrucks", RerouteTrucks); Num(sb, "rrTaxis", RerouteTaxis); Num(sb, "rrBetter", RerouteBetter);
            Num(sb, "rrNoBetter", RerouteNoBetter); Num(sb, "rrSaved", RerouteSavedPct);
            Num(sb, "planSpeed", PlannerSpeedPct); Num(sb, "planJammed", PlannerJammedPct); Num(sb, "ms", ModMs, last: true);
            sb.Append('}');
        }

        internal string ToLogLine()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(string.Format(c, "Traffic report {0} [{1}{2}]: ", GameTime, Mode, Mode == "Live" ? " " + Preset : ""));
            sb.Append(string.Format(c, "{0:0} vehicles, real avg speed {1:0.0}% of limit, {2:0.0}% queued, {3:0.0}% on local streets, ",
                Vehicles, AvgSpeedPct, QueuedPct, LocalStreetPct));
            sb.Append(string.Format(c, "{0:0.0} jam warnings (max {1}), game traffic flow {2:0}%. ", JamWarnings, JamWarningsMax, FlowPct));
            sb.Append(string.Format(c, "Progress to destination: with navigation ({0:0}%) {1:0.0} km/h, others {2:0.0} km/h. ",
                NavSharePct, NavProgressKmh, OtherProgressKmh));
            if (Mode != "Native")
            {
                sb.Append(string.Format(c, "Re-routes {0} ({1} cars, {2} trucks, {3} taxis): {4} faster (avg {5:0}% saved), {6} no better. ",
                    Reroutes, RerouteCars, RerouteTrucks, RerouteTaxis, RerouteBetter, RerouteSavedPct, RerouteNoBetter));
                sb.Append(string.Format(c, "Planner view: {0:0}% speed, {1:0.0}% jammed. ", PlannerSpeedPct, PlannerJammedPct));
            }
            sb.Append(ModMs >= 0 ? string.Format(c, "Mod cost {0:0.00} ms per run.", ModMs) : "Mod cost not measured.");
            return sb.ToString();
        }

        private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        private static float P(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
        private static int I(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static List<string> SplitCsv(string line)
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

        internal static void Str(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":\"");
            foreach (char ch in value ?? "")
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < ' ') sb.Append(' ');
                else sb.Append(ch);
            }
            sb.Append("\",");
        }

        internal static void Num(StringBuilder sb, string key, double value, bool last = false)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
            sb.Append('"').Append(key).Append("\":").Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            if (!last) sb.Append(',');
        }
    }

    /// <summary>The live numbers for "right now" (since the start of the current game hour).</summary>
    internal sealed class TrafficSnapshot
    {
        public string Mode = "";
        public string Preset = "";
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
            TrafficCapture.Str(sb, "mode", Mode); TrafficCapture.Str(sb, "preset", Preset); TrafficCapture.Str(sb, "t", GameTime);
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
        private const int kMaxInMemory = 24 * 60; // about two game months
        private static readonly List<TrafficCapture> s_Captures = new List<TrafficCapture>();
        private static bool s_Loaded;

        public static TrafficSnapshot Current { get; private set; } = new TrafficSnapshot();
        public static string CurrentCity { get; set; } = "";
        /// <summary>Changes whenever there is something new for the UI.</summary>
        public static int Version { get; private set; }

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", nameof(LiveTrafficRouting));
                return Path.Combine(dir, "captures.csv");
            }
        }

        public static IReadOnlyList<TrafficCapture> Captures
        {
            get { EnsureLoaded(); return s_Captures; }
        }

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
            if (s_Captures.Count > kMaxInMemory) s_Captures.RemoveRange(0, s_Captures.Count - kMaxInMemory);
            Version++;
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool writeHeader = !File.Exists(path);
                using (var w = new StreamWriter(path, append: true, Encoding.UTF8))
                {
                    if (writeHeader) w.WriteLine(string.Join(",", TrafficCapture.CsvHeader));
                    w.WriteLine(capture.ToCsv());
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not write traffic capture: {e.Message}");
            }
        }

        internal struct ModeAverage
        {
            public int Hours;
            public float Speed, Queued, Jams, Local, Flow, NavKmh, OtherKmh;
        }

        /// <summary>Average of all captures of the current city in one mode ("Native" or "Live").</summary>
        public static ModeAverage Average(string mode)
        {
            EnsureLoaded();
            var a = new ModeAverage();
            foreach (TrafficCapture c in s_Captures)
            {
                if (c.City != CurrentCity || c.Mode != mode) continue;
                a.Hours++;
                a.Speed += c.AvgSpeedPct; a.Queued += c.QueuedPct; a.Jams += c.JamWarnings; a.Local += c.LocalStreetPct;
                a.Flow += c.FlowPct; a.NavKmh += c.NavProgressKmh; a.OtherKmh += c.OtherProgressKmh;
            }
            if (a.Hours > 0)
            {
                float n = a.Hours;
                a.Speed /= n; a.Queued /= n; a.Jams /= n; a.Local /= n; a.Flow /= n; a.NavKmh /= n; a.OtherKmh /= n;
            }
            return a;
        }

        /// <summary>
        /// Averages per game hour of the day (0-23) for each mode, for the current city:
        /// {"native":[{h,hours,speed,...} x24],"live":[...]}. Lets the UI compare the same times of day.
        /// </summary>
        public static string ByHourJson()
        {
            EnsureLoaded();
            var sb = new StringBuilder("{");
            string[] modes = { "Native", "Live" };
            for (int m = 0; m < modes.Length; m++)
            {
                var sum = new ModeAverage[24];
                foreach (TrafficCapture c in s_Captures)
                {
                    if (c.City != CurrentCity || c.Mode != modes[m] || c.GameHour < 0 || c.GameHour > 23) continue;
                    ref ModeAverage a = ref sum[c.GameHour];
                    a.Hours++;
                    a.Speed += c.AvgSpeedPct; a.Queued += c.QueuedPct; a.Jams += c.JamWarnings; a.Local += c.LocalStreetPct;
                    a.Flow += c.FlowPct; a.NavKmh += c.NavProgressKmh; a.OtherKmh += c.OtherProgressKmh;
                }
                sb.Append('"').Append(m == 0 ? "native" : "live").Append("\":[");
                for (int h = 0; h < 24; h++)
                {
                    ModeAverage a = sum[h];
                    float n = Math.Max(1, a.Hours);
                    sb.Append('{');
                    TrafficCapture.Num(sb, "h", h);
                    TrafficCapture.Num(sb, "hours", a.Hours);
                    TrafficCapture.Num(sb, "speed", a.Speed / n);
                    TrafficCapture.Num(sb, "queued", a.Queued / n);
                    TrafficCapture.Num(sb, "jams", a.Jams / n);
                    TrafficCapture.Num(sb, "local", a.Local / n);
                    TrafficCapture.Num(sb, "flow", a.Flow / n);
                    TrafficCapture.Num(sb, "navKmh", a.NavKmh / n);
                    TrafficCapture.Num(sb, "otherKmh", a.OtherKmh / n, last: true);
                    sb.Append('}');
                    if (h < 23) sb.Append(',');
                }
                sb.Append(']');
                if (m == 0) sb.Append(',');
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Captures of the current city, newest last, as a JSON array for the UI.</summary>
        public static string HistoryJson(int max)
        {
            EnsureLoaded();
            var sb = new StringBuilder("[");
            int count = 0;
            int start = s_Captures.Count - 1;
            var picked = new List<TrafficCapture>();
            for (int i = start; i >= 0 && count < max; i--)
            {
                if (s_Captures[i].City == CurrentCity) { picked.Add(s_Captures[i]); count++; }
            }
            for (int i = picked.Count - 1; i >= 0; i--)
            {
                picked[i].AppendJson(sb);
                if (i > 0) sb.Append(',');
            }
            sb.Append(']');
            return sb.ToString();
        }

        private static void EnsureLoaded()
        {
            if (s_Loaded) return;
            s_Loaded = true;
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return;
                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                for (int i = 1; i < lines.Length; i++)
                {
                    TrafficCapture c = TrafficCapture.FromCsv(lines[i]);
                    if (c != null) s_Captures.Add(c);
                }
                if (s_Captures.Count > kMaxInMemory) s_Captures.RemoveRange(0, s_Captures.Count - kMaxInMemory);
                Logger.Info($"Loaded {s_Captures.Count} traffic captures from {path}");
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read traffic captures: {e.Message}");
            }
        }
    }
}
