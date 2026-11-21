using System.Collections.Generic;
using Colossal;

namespace LiveTrafficRouting
{
    internal sealed class LiveTrafficLocale : IDictionarySource
    {
        private readonly LiveTrafficSettings m_Settings;

        internal LiveTrafficLocale(LiveTrafficSettings settings)
        {
            m_Settings = settings;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            LiveTrafficSettings s = m_Settings;
            return new Dictionary<string, string>
            {
                { s.GetSettingsLocaleID(), "Live Traffic Routing" },
                { s.GetOptionTabLocaleID(LiveTrafficSettings.GeneralSection), "General" },
                { s.GetOptionGroupLocaleID(LiveTrafficSettings.TrafficGroup), "Traffic" },
                { s.GetOptionGroupLocaleID(LiveTrafficSettings.DebugGroup), "For traffic geeks" },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.Awareness)), "How drivers react to traffic jams" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.Awareness)),
                    "Like a navigation app with live traffic: drivers starting a trip avoid roads that are jammed right now, " +
                    "and some drivers already on the road switch to a faster way when their route jams up. " +
                    "Without this mod, drivers only learn very slowly (over game days) which roads are usually busy, and never change their mind mid-trip.\n\n" +
                    "Off: the game's normal behaviour (the sliders are greyed out).\n" +
                    "Relaxed: drivers only avoid bad jams and mostly stay on main roads. About 1 in 7 drivers re-routes mid-trip.\n" +
                    "Balanced: recommended. New trips avoid jams within about 10-20 game minutes; about 1 in 3 drivers re-routes mid-trip.\n" +
                    "Eager: drivers react fast and strongly; a jammed road is avoided almost like a closed one, and 3 in 4 drivers re-route mid-trip. Can move traffic onto residential streets.\n" +
                    "Custom: your own mix. Picking a preset fills in the sliders below; changing any slider switches to Custom." },
                { s.GetEnumValueLocaleID(TrafficAwareness.Off), "Off (game default)" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Relaxed), "Relaxed" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Balanced), "Balanced (recommended)" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Eager), "Eager" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Custom), "Custom" },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.LiveTrust)), "Right now vs usual traffic" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.LiveTrust)),
                    "What drivers go by when planning a trip. 100%: only what the roads are like right now. " +
                    "0%: only what they are usually like at this time of day, which is the game's normal behaviour." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.ReactionSpeed)), "How quickly jams are noticed" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.ReactionSpeed)),
                    "Higher: drivers notice new jams, and roads that have cleared, sooner. " +
                    "Too high and traffic may swing back and forth between two roads." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.PenaltyStrength)), "Jam penalty strength" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.PenaltyStrength)),
                    "How heavily drivers count the delay of a jammed road when choosing a route. 100%: as measured. " +
                    "200%: a road where traffic takes twice as long looks three times as slow, so drivers take detours sooner. " +
                    "Lower: drivers put up with jams on main roads. Above about 300% even small slowdowns cause detours. The limit below still applies." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.AvoidanceLimit)), "Avoid jams up to (times slower)" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.AvoidanceLimit)),
                    "The worst a jammed road can look to drivers, compared with driving it at the speed limit. " +
                    "At 10, drivers accept a detour up to 10 times as long as the jammed stretch. " +
                    "Lower keeps traffic on main roads even when they are busy; higher lets drivers detour further. Above about 50 a jammed road is treated almost as closed." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.RerouteShare)), "Drivers who re-route while driving" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.RerouteShare)),
                    "The share of drivers with live navigation (cars, delivery trucks and taxis, including traffic passing through the city). " +
                    "They can switch to a faster way mid-trip when the road ahead jams up. Buses and other public transport always keep to their line. " +
                    "The others keep the route they picked when they set off. 0% turns mid-trip re-routing off. " +
                    "Higher values spread traffic better but make the game calculate more routes." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.RerouteThreshold)), "Re-route when the way ahead gets slower by" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.RerouteThreshold)),
                    "How much worse the rest of a driver's route must get, compared with when they planned it, before they look for another way. " +
                    "Lower: drivers react to smaller jams. Higher: only big jams make drivers change their route." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.ShowStatistics)), "Statistics panel" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.ShowStatistics)),
                    "Shows a traffic icon in the top-left of the game. It opens the Live Traffic panel: how traffic is doing right now, " +
                    "comparisons between the mod off (Native) and different settings, charts per hour of the day, and re-routing details. " +
                    "While on, the mod records one row of statistics per game hour (also with the mod off) to " +
                    "ModsData/LiveTrafficRouting/captures.csv. Off: nothing is recorded, and the mod just does its job." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.LogStatistics)), "Write traffic report to log" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.LogStatistics)),
                    "Every game hour, also write the statistics (needs the statistics panel on) to Logs/LiveTrafficRouting.log. " +
                    "Only needed when reporting a problem." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.ResetSettings)), "Reset settings" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.ResetSettings)), "Go back to Balanced and the default values." },
            };
        }

        public void Unload()
        {
        }
    }
}
