using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LiveTrafficRouting
{
    /// <summary>
    /// The "Auto" preset: tunes itself to the city with a switchback experiment.
    ///
    /// Game hours alternate between the current setup (variant 0) and the same setup with one value changed by one step
    /// (variant 1). Each variant-1 hour is compared with the average of the variant-0 hours on both sides, which cancels
    /// out time of day and a slowly growing city. After <see cref="kPairsPerDecision"/> such comparisons the step is kept
    /// if it clearly reduced queues without pushing more traffic onto local streets; otherwise the other direction or the
    /// next value is tried. Values: jam penalty strength, spread over detours, keep out of neighbourhoods.
    ///
    /// Main thread only. The learned setup is kept per city in ModsData/LiveTrafficRouting/auto-tuning.txt (not in the save).
    /// </summary>
    internal static class AutoTuner
    {
        private const int kPairsPerDecision = 24;
        // Paired t value needed to act on a difference (about "likely" in the panel's Real? check).
        private const double kTValue = 2.0;
        // A step may raise local-street traffic by at most this share.
        private const double kMaxLocalRise = 0.10;

        private static readonly string[] kNames = { "jam penalty", "spread", "neighbourhoods" };
        private static readonly float[] kStep = { 50f, 10f, 10f };
        private static readonly float[] kMin = { 25f, 0f, 0f };
        private static readonly float[] kMax = { 1000f, 100f, 100f };

        private struct HourResult
        {
            public int Variant;
            public float Queued;
            public float Local;
            public bool Valid;
        }

        private static Tuning s_Base = LiveTrafficSettings.kBalanced;
        private static int s_Param;
        private static int s_Direction = 1;
        private static bool s_TriedOtherDirection;
        private static int s_Variant;
        private static readonly List<HourResult> s_Hours = new List<HourResult>();
        private static string s_City;
        private static string s_LastDecision = "";
        private static int s_Pairs;

        /// <summary>The values to use this game hour.</summary>
        internal static Tuning Current => s_Variant == 1 ? Stepped() : s_Base;

        /// <summary>For the statistics: this hour's values, e.g. "penalty 250%, spread 50%, neighbourhoods 50% (testing spread +10%)".</summary>
        internal static string VariantLabel()
        {
            Tuning t = Current;
            return string.Format(CultureInfo.InvariantCulture, "penalty {0:0}%, spread {1:0}%, neighbourhoods {2:0}%{3}",
                t.PenaltyStrength, t.Spread, t.Neighbourhoods, s_Variant == 1 ? " (test hour)" : "");
        }

        /// <summary>One line for the panel.</summary>
        internal static string Status()
        {
            string testing = string.Format(CultureInfo.InvariantCulture, "testing {0} {1}{2:0} ({3}/{4} hour pairs)",
                kNames[s_Param], s_Direction > 0 ? "+" : "-", kStep[s_Param], s_Pairs, kPairsPerDecision);
            string now = string.Format(CultureInfo.InvariantCulture, "Auto: penalty {0:0}%, spread {1:0}%, neighbourhoods {2:0}%; {3}.",
                s_Base.PenaltyStrength, s_Base.Spread, s_Base.Neighbourhoods, testing);
            return string.IsNullOrEmpty(s_LastDecision) ? now : now + " Last: " + s_LastDecision;
        }

        /// <summary>Loads the city's learned setup when the city changes.</summary>
        internal static void SetCity(string city)
        {
            if (city == s_City) return;
            s_City = city;
            s_Base = LiveTrafficSettings.kBalanced;
            s_Param = 0;
            s_Direction = 1;
            s_TriedOtherDirection = false;
            s_LastDecision = "";
            s_Hours.Clear();
            s_Pairs = 0;
            Load();
            Logger.Info($"Auto tuning for '{city}': {Status()}");
        }

        /// <summary>A new game hour starts: alternate between the current setup and the test step.</summary>
        internal static void OnHourStart()
        {
            s_Variant = s_Hours.Count % 2;
            Logger.Detail($"Auto tuning hour: {VariantLabel()}.");
        }

        /// <summary>A game hour was not recorded (loading, paused setup change): comparisons don't reach across it.</summary>
        internal static void OnHourSkipped()
        {
            s_Hours.Add(new HourResult { Variant = s_Variant, Valid = false });
        }

        internal static void OnHourFinished(float queuedPct, float localPct)
        {
            s_Hours.Add(new HourResult { Variant = s_Variant, Queued = queuedPct, Local = localPct, Valid = true });
            Evaluate();
        }

        private static void Evaluate()
        {
            var dq = new List<double>();
            var dl = new List<double>();
            double baseLocal = 0;
            for (int i = 1; i + 1 < s_Hours.Count; i++)
            {
                HourResult h = s_Hours[i], a = s_Hours[i - 1], b = s_Hours[i + 1];
                if (h.Variant != 1 || !h.Valid || !a.Valid || !b.Valid || a.Variant != 0 || b.Variant != 0) continue;
                dq.Add(h.Queued - (a.Queued + b.Queued) / 2.0);
                dl.Add(h.Local - (a.Local + b.Local) / 2.0);
                baseLocal += (a.Local + b.Local) / 2.0;
            }
            s_Pairs = dq.Count;
            if (dq.Count < kPairsPerDecision) return;

            double mean = dq.Average();
            double sd = Math.Sqrt(dq.Sum(d => (d - mean) * (d - mean)) / (dq.Count - 1));
            double t = sd > 1e-9 ? mean / (sd / Math.Sqrt(dq.Count)) : 0;
            double localRise = baseLocal > 0 ? dl.Average() / (baseLocal / dq.Count) : 0;
            string step = string.Format(CultureInfo.InvariantCulture, "{0} {1}{2:0}", kNames[s_Param], s_Direction > 0 ? "+" : "-", kStep[s_Param]);

            if (t <= -kTValue && localRise <= kMaxLocalRise)
            {
                s_Base = Stepped();
                s_LastDecision = string.Format(CultureInfo.InvariantCulture, "kept {0} (queues {1:+0.0;-0.0} points).", step, mean);
                // Keep going the same way while it helps.
                s_TriedOtherDirection = false;
            }
            else if (t >= kTValue || localRise > kMaxLocalRise)
            {
                s_LastDecision = string.Format(CultureInfo.InvariantCulture, "dropped {0} ({1}).", step,
                    localRise > kMaxLocalRise ? "more traffic on local streets" : "more queues");
                NextDirectionOrParam();
            }
            else
            {
                s_LastDecision = string.Format(CultureInfo.InvariantCulture, "{0}: no clear difference.", step);
                NextParam();
            }
            Logger.Info("Auto tuning: " + s_LastDecision + " " + Status());
            s_Hours.Clear();
            s_Pairs = 0;
            FixRange();
            Save();
        }

        private static void NextDirectionOrParam()
        {
            if (!s_TriedOtherDirection)
            {
                s_Direction = -s_Direction;
                s_TriedOtherDirection = true;
            }
            else
            {
                NextParam();
            }
        }

        private static void NextParam()
        {
            s_Param = (s_Param + 1) % kNames.Length;
            s_Direction = 1;
            s_TriedOtherDirection = false;
        }

        /// <summary>Makes sure the test step stays within the slider ranges.</summary>
        private static void FixRange()
        {
            for (int tries = 0; tries < 6; tries++)
            {
                float v = Get(s_Base, s_Param) + s_Direction * kStep[s_Param];
                if (v >= kMin[s_Param] && v <= kMax[s_Param]) return;
                NextDirectionOrParam();
            }
        }

        private static Tuning Stepped()
        {
            Tuning t = s_Base;
            float v = Get(t, s_Param) + s_Direction * kStep[s_Param];
            v = Math.Max(kMin[s_Param], Math.Min(kMax[s_Param], v));
            switch (s_Param)
            {
                case 0: t.PenaltyStrength = v; break;
                case 1: t.Spread = v; break;
                default: t.Neighbourhoods = v; break;
            }
            return t;
        }

        private static float Get(Tuning t, int param)
        {
            switch (param)
            {
                case 0: return t.PenaltyStrength;
                case 1: return t.Spread;
                default: return t.Neighbourhoods;
            }
        }

        // ---------------- File: one line per city ----------------

        private static string FilePath => Path.Combine(TrafficReportStore.Directory_, "auto-tuning.txt");

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    List<string> f = TrafficCapture.SplitCsv(line);
                    if (f.Count < 6 || f[0] != s_City) continue;
                    s_Base.PenaltyStrength = P(f[1], s_Base.PenaltyStrength);
                    s_Base.Spread = P(f[2], s_Base.Spread);
                    s_Base.Neighbourhoods = P(f[3], s_Base.Neighbourhoods);
                    s_Param = Math.Max(0, Math.Min(kNames.Length - 1, (int)P(f[4], 0)));
                    s_Direction = P(f[5], 1) < 0 ? -1 : 1;
                    if (f.Count > 6) s_LastDecision = f[6];
                }
                FixRange();
            }
            catch (Exception e)
            {
                Logger.Warn($"Auto tuning: could not read {FilePath}: {e.Message}");
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(TrafficReportStore.Directory_);
                var lines = File.Exists(FilePath)
                    ? File.ReadAllLines(FilePath).Where(l => TrafficCapture.SplitCsv(l)[0] != s_City).ToList()
                    : new List<string>();
                lines.Add(string.Join(",", new[]
                {
                    Quote(s_City),
                    s_Base.PenaltyStrength.ToString("0.##", CultureInfo.InvariantCulture),
                    s_Base.Spread.ToString("0.##", CultureInfo.InvariantCulture),
                    s_Base.Neighbourhoods.ToString("0.##", CultureInfo.InvariantCulture),
                    s_Param.ToString(CultureInfo.InvariantCulture),
                    s_Direction.ToString(CultureInfo.InvariantCulture),
                    Quote(s_LastDecision),
                }));
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception e)
            {
                Logger.Warn($"Auto tuning: could not write {FilePath}: {e.Message}");
            }
        }

        private static string Quote(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

        private static float P(string s, float fallback) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;
    }
}
