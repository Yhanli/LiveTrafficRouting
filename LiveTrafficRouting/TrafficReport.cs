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
            Num(sb, "ms", ModMs, last: true);
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
            sb.Append(ModMs >= 0 ? string.Format(Inv, "Mod cost {0:0.00} ms per run.", ModMs) : "Mod cost not measured.");
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
        // Keep the file (and the startup load) small: above kMaxFileRows the oldest rows move to an archive file,
        // leaving kKeepRows. ~20k rows is ~2.5 years of game time for one city, about 3-4 MB.
        private const int kMaxFileRows = 20000;
        private const int kKeepRows = 12000;
        // How many backup / archive files to keep (oldest deleted first).
        private const int kMaxBackups = 5;
        // Captures of the current city sent to the panel (newest).
        private const int kMaxUiCaptures = 24 * 60;

        private static readonly List<TrafficCapture> s_Captures = new List<TrafficCapture>();
        private static bool s_Loaded;

        public static TrafficSnapshot Current { get; private set; } = new TrafficSnapshot();
        public static string CurrentCity { get; set; } = "";
        /// <summary>Changes whenever there is something new for the UI.</summary>
        public static int Version { get; private set; }

        public static string Directory_ => Path.Combine(UnityEngine.Application.persistentDataPath, "ModsData", nameof(LiveTrafficRouting));
        public static string FilePath => Path.Combine(Directory_, "captures.csv");

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
            Version++;
            try
            {
                Directory.CreateDirectory(Directory_);
                bool writeHeader = !File.Exists(FilePath);
                using (var w = new StreamWriter(FilePath, append: true, Encoding.UTF8))
                {
                    if (writeHeader) w.WriteLine(TrafficCapture.CsvHeaderLine);
                    w.WriteLine(capture.ToCsv());
                }
                if (s_Captures.Count > kMaxFileRows) Trim();
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not write traffic capture: {e.Message}");
            }
        }

        // ---------------- Runs ----------------

        internal sealed class RunSummary
        {
            public string Id, Mode, Preset, Settings, First, Last;
            public int Hours;
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
                if (string.CompareOrdinal(c.GameTime, r.First) < 0) r.First = c.GameTime;
                if (string.CompareOrdinal(c.GameTime, r.Last) > 0) r.Last = c.GameTime;
            }
            return runs;
        }

        public static string RunsJson()
        {
            var sb = new StringBuilder("[");
            List<RunSummary> runs = Runs();
            for (int i = 0; i < runs.Count; i++)
            {
                RunSummary r = runs[i];
                sb.Append('{');
                TrafficCapture.Str(sb, "id", r.Id); TrafficCapture.Str(sb, "mode", r.Mode); TrafficCapture.Str(sb, "preset", r.Preset);
                TrafficCapture.Str(sb, "settings", r.Settings); TrafficCapture.Str(sb, "first", r.First); TrafficCapture.Str(sb, "last", r.Last);
                TrafficCapture.Num(sb, "hours", r.Hours, last: true);
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
            var picked = new List<TrafficCapture>();
            for (int i = s_Captures.Count - 1; i >= 0 && picked.Count < kMaxUiCaptures; i--)
            {
                if (s_Captures[i].City == CurrentCity) picked.Add(s_Captures[i]);
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

        /// <summary>Removes matching captures from memory and the file. A backup of the file is kept first.</summary>
        private static int Delete(Predicate<TrafficCapture> match, string what)
        {
            EnsureLoaded();
            int removed = s_Captures.RemoveAll(match);
            Version++;
            if (removed == 0) return 0;
            try
            {
                string backup = CopyAside("captures-backup-");
                RewriteFile();
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
                    string backup = CopyAside("captures-backup-");
                    RewriteFile();
                    Logger.Info($"Upgraded the statistics file to the current format. Backup: {backup}");
                }
                if (s_Captures.Count > kMaxFileRows) Trim();
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not read traffic captures: {e.Message}");
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

        /// <summary>Moves the oldest rows to an archive file so the main file stays small.</summary>
        private static void Trim()
        {
            int drop = s_Captures.Count - kKeepRows;
            if (drop <= 0) return;
            string archive = Path.Combine(Directory_, "captures-archive-" + Stamp() + ".csv");
            using (var w = new StreamWriter(archive, append: false, Encoding.UTF8))
            {
                w.WriteLine(TrafficCapture.CsvHeaderLine);
                for (int i = 0; i < drop; i++) w.WriteLine(s_Captures[i].ToCsv());
            }
            s_Captures.RemoveRange(0, drop);
            RewriteFile();
            Prune("captures-archive-");
            Version++;
            Logger.Info($"Statistics file trimmed: {drop} oldest game hours moved to {archive}");
        }

        private static string CopyAside(string prefix)
        {
            if (!File.Exists(FilePath)) return "(no file)";
            string path = Path.Combine(Directory_, prefix + Stamp() + ".csv");
            File.Copy(FilePath, path, overwrite: true);
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
