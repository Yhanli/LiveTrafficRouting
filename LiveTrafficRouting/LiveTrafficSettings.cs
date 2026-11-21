using System.Globalization;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace LiveTrafficRouting
{
    /// <summary>How strongly drivers react to live traffic. Shown as a dropdown.</summary>
    public enum TrafficAwareness
    {
        Off,
        Relaxed,
        Balanced,
        Eager,
        Custom,
    }

    /// <summary>
    /// Picking a preset fills in the sliders. Changing a slider by hand switches the preset to Custom
    /// (unless the values happen to match the preset). The sliders are only greyed out when the mod is Off.
    /// </summary>
    [FileLocation(nameof(LiveTrafficRouting))]
    [SettingsUIGroupOrder(TrafficGroup, StatsGroup, DebugGroup)]
    [SettingsUIShowGroupName(StatsGroup)]
    internal sealed class LiveTrafficSettings : ModSetting
    {
        internal const string GeneralSection = "General";
        internal const string TrafficGroup = "Traffic";
        internal const string DebugGroup = "Debug";
        internal const string StatsGroup = "Statistics";

        private struct Tuning
        {
            public float LiveTrust;          // %
            public float ReactionSpeed;      // %
            public float PenaltyStrength;    // %
            public float AvoidanceLimit;     // times slower
            public float RerouteShare;       // %
            public float RerouteThreshold;   // %

            public Tuning(float liveTrust, float reactionSpeed, float penaltyStrength, float avoidanceLimit, float rerouteShare, float rerouteThreshold)
            {
                LiveTrust = liveTrust;
                ReactionSpeed = reactionSpeed;
                PenaltyStrength = penaltyStrength;
                AvoidanceLimit = avoidanceLimit;
                RerouteShare = rerouteShare;
                RerouteThreshold = rerouteThreshold;
            }
        }

        private static readonly Tuning kRelaxed = new Tuning(50f, 30f, 75f, 4f, 15f, 50f);
        private static readonly Tuning kBalanced = new Tuning(75f, 50f, 100f, 10f, 30f, 30f);
        private static readonly Tuning kEager = new Tuning(100f, 75f, 150f, 20f, 60f, 20f);

        private bool m_IsLoadingDefaults;
        private TrafficAwareness m_Awareness;
        private Tuning m_Tuning;
        private bool m_LogStatistics;

        internal LiveTrafficSettings(IMod mod) : base(mod)
        {
            SetDefaults();
        }

        [SettingsUISection(GeneralSection, TrafficGroup)]
        public TrafficAwareness Awareness
        {
            get => m_Awareness;
            set
            {
                if (m_Awareness == value) return;
                m_Awareness = value;
                if (TryGetPreset(value, out Tuning preset)) m_Tuning = preset;
                Save();
            }
        }

        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float LiveTrust
        {
            get => m_Tuning.LiveTrust;
            set => SetSlider(ref m_Tuning.LiveTrust, value);
        }

        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 10f, max = 100f, step = 5f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float ReactionSpeed
        {
            get => m_Tuning.ReactionSpeed;
            set => SetSlider(ref m_Tuning.ReactionSpeed, value);
        }

        // Multiplies the extra time a jam adds (100% = as measured).
        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 25f, max = 300f, step = 25f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float PenaltyStrength
        {
            get => m_Tuning.PenaltyStrength;
            set => SetSlider(ref m_Tuning.PenaltyStrength, value);
        }

        // How many times slower than normal a jammed road may look (2x-20x).
        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 2f, max = 20f, step = 1f)]
        [SettingsUICustomFormat(fractionDigits = 0, maxValueWithFraction = 0f, separateThousands = false)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float AvoidanceLimit
        {
            get => m_Tuning.AvoidanceLimit;
            set => SetSlider(ref m_Tuning.AvoidanceLimit, value);
        }

        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float RerouteShare
        {
            get => m_Tuning.RerouteShare;
            set => SetSlider(ref m_Tuning.RerouteShare, value);
        }

        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 10f, max = 100f, step = 5f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float RerouteThreshold
        {
            get => m_Tuning.RerouteThreshold;
            set => SetSlider(ref m_Tuning.RerouteThreshold, value);
        }

        // ---- Statistics (read-only, filled by TrafficReportSystem; measured whether the mod is on or off) ----

        [SettingsUISection(GeneralSection, StatsGroup)]
        [Colossal.Json.Exclude]
        public string StatNow
        {
            get
            {
                TrafficSnapshot c = TrafficReportStore.Current;
                if (string.IsNullOrEmpty(c.Mode)) return "Waiting for a city to run...";
                return Inv("{0} | {1} vehicles | real speed {2:0}% of limit | {3:0.0}% queued | {4} jam warnings",
                    ModeLabel(c.Mode, c.Preset), c.Vehicles, c.AvgSpeedPct, c.QueuedPct, c.JamWarnings);
            }
        }

        [SettingsUISection(GeneralSection, StatsGroup)]
        [Colossal.Json.Exclude]
        public string StatNavigation
        {
            get
            {
                TrafficSnapshot c = TrafficReportStore.Current;
                if (string.IsNullOrEmpty(c.Mode)) return "-";
                return Inv("with navigation ({0:0}%): {1:0.0} km/h | others: {2:0.0} km/h (this hour)",
                    c.NavSharePct, c.NavProgressKmh, c.OtherProgressKmh);
            }
        }

        [SettingsUISection(GeneralSection, StatsGroup)]
        [Colossal.Json.Exclude]
        public string StatLastHour
        {
            get
            {
                TrafficCapture l = TrafficReportStore.Last;
                if (l == null || l.City != TrafficReportStore.CurrentCity) return "No full game hour recorded yet.";
                string rr = l.Mode == "Native"
                    ? "no re-routing (mod off)"
                    : Inv("{0} re-routes ({1} cars, {2} trucks, {3} taxis), {4:0}% found a faster route",
                        l.Reroutes, l.RerouteCars, l.RerouteTrucks, l.RerouteTaxis,
                        l.RerouteBetter + l.RerouteNoBetter > 0 ? 100f * l.RerouteBetter / (l.RerouteBetter + l.RerouteNoBetter) : 0f);
                return Inv("{0} [{1}]: speed {2:0}%, {3:0.0} jam warnings, flow {4:0}%, {5}",
                    l.GameTime, ModeLabel(l.Mode, l.Preset), l.AvgSpeedPct, l.JamWarnings, l.FlowPct, rr);
            }
        }

        [SettingsUISection(GeneralSection, StatsGroup)]
        [Colossal.Json.Exclude]
        public string StatCompare
        {
            get
            {
                TrafficReportStore.ModeAverage n = TrafficReportStore.Average("Native");
                TrafficReportStore.ModeAverage l = TrafficReportStore.Average("Live");
                if (n.Hours == 0 && l.Hours == 0) return "No hours recorded for this city yet.";
                return Inv("Native ({0} h): speed {1:0}%, {2:0.0}% queued, {3:0.0} jams, flow {4:0}%  ||  " +
                           "Live ({5} h): speed {6:0}%, {7:0.0}% queued, {8:0.0} jams, flow {9:0}%",
                    n.Hours, n.Speed, n.Queued, n.Jams, n.Flow, l.Hours, l.Speed, l.Queued, l.Jams, l.Flow);
            }
        }

        [SettingsUISection(GeneralSection, StatsGroup)]
        [Colossal.Json.Exclude]
        public string StatModCost
        {
            get
            {
                float ms = TrafficReportStore.Current.ModMs;
                return ms < 0 ? "not measured yet (measured once per game hour)" : Inv("{0:0.00} ms per run (one run about every second of game time)", ms);
            }
        }

        [SettingsUISection(GeneralSection, StatsGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool ResetStatistics
        {
            set => StatisticsReset.ResetCurrentCity();
        }

        private static string ModeLabel(string mode, string preset) =>
            mode == "Live" ? "Live traffic (" + preset + ")" : mode == "Native" ? "Native (mod off)" : "Mixed";

        private static string Inv(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);

        [SettingsUISection(GeneralSection, DebugGroup)]
        public bool LogStatistics
        {
            get => m_LogStatistics;
            set
            {
                if (m_LogStatistics == value) return;
                m_LogStatistics = value;
                Save();
            }
        }

        [SettingsUISection(GeneralSection, DebugGroup)]
        [SettingsUIButton]
        public bool ResetSettings
        {
            set
            {
                SetDefaults();
                ApplyAndSave();
            }
        }

        public bool IsOff() => m_Awareness == TrafficAwareness.Off;

        internal bool IsActive => m_Awareness != TrafficAwareness.Off;

        /// <summary>Route-cost tuning used by the traffic system.</summary>
        internal void GetTuning(out float liveTrust, out float reactionSpeed, out float maxSlowdownFactor, out float penaltyStrength)
        {
            penaltyStrength = m_Tuning.PenaltyStrength / 100f;
            liveTrust = m_Tuning.LiveTrust / 100f;
            reactionSpeed = m_Tuning.ReactionSpeed / 100f;
            maxSlowdownFactor = m_Tuning.AvoidanceLimit;
        }

        /// <summary>Mid-trip re-routing: share of drivers with live navigation, and how much slower the way ahead must get.</summary>
        internal void GetRerouteTuning(out float share, out float threshold)
        {
            share = m_Tuning.RerouteShare / 100f;
            threshold = m_Tuning.RerouteThreshold / 100f;
        }

        public override void SetDefaults()
        {
            m_IsLoadingDefaults = true;
            m_Awareness = TrafficAwareness.Balanced;
            m_Tuning = kBalanced;
            m_LogStatistics = false;
            m_IsLoadingDefaults = false;
        }

        private static bool TryGetPreset(TrafficAwareness awareness, out Tuning tuning)
        {
            switch (awareness)
            {
                case TrafficAwareness.Relaxed: tuning = kRelaxed; return true;
                case TrafficAwareness.Balanced: tuning = kBalanced; return true;
                case TrafficAwareness.Eager: tuning = kEager; return true;
                default: tuning = default; return false;
            }
        }

        private void SetSlider(ref float field, float value)
        {
            if (field == value) return;
            field = value;
            // Hand-tuned values no longer match the preset: show Custom. (Matching values keep the preset,
            // which also makes loading a saved file safe whatever order the properties are read in.)
            if (TryGetPreset(m_Awareness, out Tuning preset) && !Matches(m_Tuning, preset))
            {
                m_Awareness = TrafficAwareness.Custom;
            }
            Save();
        }

        private static bool Matches(Tuning a, Tuning b) =>
            a.LiveTrust == b.LiveTrust && a.ReactionSpeed == b.ReactionSpeed && a.PenaltyStrength == b.PenaltyStrength &&
            a.AvoidanceLimit == b.AvoidanceLimit &&
            a.RerouteShare == b.RerouteShare && a.RerouteThreshold == b.RerouteThreshold;

        private void Save()
        {
            if (!m_IsLoadingDefaults) ApplyAndSave();
        }
    }
}
