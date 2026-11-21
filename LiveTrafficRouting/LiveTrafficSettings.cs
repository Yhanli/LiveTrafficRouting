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
    [SettingsUIGroupOrder(TrafficGroup, DebugGroup)]
    [SettingsUIShowGroupName(DebugGroup)]
    internal sealed class LiveTrafficSettings : ModSetting
    {
        internal const string GeneralSection = "General";
        internal const string TrafficGroup = "Traffic";
        internal const string DebugGroup = "Debug";

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
        // Eager v2: stronger than the first version (penalty 150%, cap 20x, 60% navigation, +20%), which still
        // felt mild in testing.
        private static readonly Tuning kEager = new Tuning(100f, 80f, 250f, 50f, 75f, 15f);

        private bool m_IsLoadingDefaults;
        private TrafficAwareness m_Awareness;
        private Tuning m_Tuning;
        private bool m_LogStatistics;
        private bool m_ShowStatistics;
        // The last preset picked in the dropdown, also while loading. Used to re-apply a preset whose values
        // changed in a mod update (otherwise the old saved slider values would turn it into "Custom").
        private TrafficAwareness m_PickedAwareness;

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
                m_PickedAwareness = value;
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
        [SettingsUISlider(min = 25f, max = 1000f, step = 25f, unit = Game.UI.Unit.kPercentage)]
        [SettingsUIDisableByCondition(typeof(LiveTrafficSettings), nameof(IsOff))]
        public float PenaltyStrength
        {
            get => m_Tuning.PenaltyStrength;
            set => SetSlider(ref m_Tuning.PenaltyStrength, value);
        }

        // How many times slower than normal a jammed road may look (2x-100x). The pathfinder can represent up to
        // 256x (FlowOffset is a byte); beyond ~50x a jammed road is effectively closed to route planning.
        [SettingsUISection(GeneralSection, TrafficGroup)]
        [SettingsUISlider(min = 2f, max = 100f, step = 1f)]
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

        [SettingsUISection(GeneralSection, DebugGroup)]
        public bool ShowStatistics
        {
            get => m_ShowStatistics;
            set
            {
                if (m_ShowStatistics == value) return;
                m_ShowStatistics = value;
                Save();
            }
        }


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

        /// <summary>Describes the active setup, for statistics runs: a run = one setup.</summary>
        internal string RunLabel()
        {
            if (m_Awareness == TrafficAwareness.Off) return "Native (mod off)";
            Tuning t = m_Tuning;
            return string.Format(CultureInfo.InvariantCulture,
                "{0}: live {1:0}%, react {2:0}%, penalty {3:0}%, cap {4:0}x, nav {5:0}%, re-route at +{6:0}%",
                m_Awareness, t.LiveTrust, t.ReactionSpeed, t.PenaltyStrength, t.AvoidanceLimit, t.RerouteShare, t.RerouteThreshold);
        }

        /// <summary>
        /// After loading: if a preset was picked, use its current values. A mod update can change a preset's values;
        /// the saved slider values are the old ones and would otherwise switch the dropdown to Custom.
        /// </summary>
        internal void ReapplyPickedPreset()
        {
            if (!TryGetPreset(m_PickedAwareness, out Tuning preset)) return;
            if (m_Awareness == m_PickedAwareness && Matches(m_Tuning, preset)) return;
            m_Awareness = m_PickedAwareness;
            m_Tuning = preset;
            Save();
        }

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
            m_ShowStatistics = false;
            m_PickedAwareness = TrafficAwareness.Balanced;
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
