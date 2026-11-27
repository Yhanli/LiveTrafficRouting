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
                    "Relaxed: a light touch. Drivers avoid the worst jams and stay on main roads; about 1 in 3 re-routes mid-trip. Fewer jam warnings, little else changes.\n" +
                    "Balanced: recommended. Drivers react quickly to jams and 3 in 4 re-route mid-trip. In testing: about a quarter fewer vehicles waiting in queues, far fewer jam warnings, and only slightly more traffic on local streets.\n" +
                    "Eager: the strongest. A jammed road is treated almost like a closed one and every driver re-routes. Queues drop the most, but noticeably more traffic cuts through neighbourhoods.\n" +
                    "Custom: your own mix. Picking a preset fills in the sliders below; changing any slider switches to Custom.\n" +
                    "Auto: tunes itself to your city. It starts from Balanced and keeps testing small changes (jam penalty, spreading, neighbourhoods) one game hour at a time, " +
                    "keeping a change only when it clearly cuts queues without sending more traffic through local streets. It needs a couple of game days per step. " +
                    "The sliders are not used while on Auto. What it learned is kept per city in ModsData/LiveTrafficRouting/auto-tuning.txt (not in the save)." },
                { s.GetEnumValueLocaleID(TrafficAwareness.Off), "Off (game default)" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Relaxed), "Relaxed" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Balanced), "Balanced (recommended)" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Eager), "Eager" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Custom), "Custom" },
                { s.GetEnumValueLocaleID(TrafficAwareness.Auto), "Auto (tunes itself to your city)" },

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

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.Spread)), "Spread drivers over detours" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.Spread)),
                    "Drivers also look at where everyone else is about to drive, not only at where traffic is slow right now. " +
                    "A road that many drivers are heading for looks busier before it jams, so the next drivers pick another way, " +
                    "and traffic spreads over several detours instead of all piling onto the same one. " +
                    "0% turns this off. Higher spreads traffic more, but may also send more of it along smaller roads." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.Neighbourhoods)), "Keep through traffic out of neighbourhoods" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.Neighbourhoods)),
                    "Local streets (50 km/h or less) that get much more traffic than they usually do look slower to drivers passing through, " +
                    "so they stay on main roads. People who live or work there still use them. " +
                    "It learns what is usual for each street while you play (it starts over after loading a save). 0% turns this off." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.RerouteShare)), "Drivers who re-route while driving" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.RerouteShare)),
                    "The share of drivers with live navigation (cars, delivery trucks and taxis, including traffic passing through the city). " +
                    "They can switch to a faster way mid-trip when the road ahead jams up, at the next junction where there is a choice. " +
                    "Ambulances, fire engines and police cars on an emergency run always do, whatever this is set to. " +
                    "Buses and other public transport always keep to their line. " +
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

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.KeepRunningOnPopups)), "Don't pause for milestone popups while recording" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.KeepRunningOnPopups)),
                    "Milestone (city upgrade) and signature building popups pause the game, which stops a test run. " +
                    "While the statistics panel is on, this turns off the game's blocking popups so the city keeps running; " +
                    "you still get the unlocks. Your own setting is put back when you turn this or the statistics panel off." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.KeepStatisticsFor)), "Keep statistics for" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.KeepStatisticsFor)),
                    "How much game time of statistics to keep, for all cities together. A game day takes roughly 10 to 60 minutes " +
                    "of real play, depending on game speed. Above the limit, the oldest whole runs move to archive files in " +
                    "ModsData/LiveTrafficRouting (a run is never cut in half). The newest Native run of each city (your baseline), " +
                    "runs you pinned in the panel and the run being recorded are always kept.\n\n" +
                    "Warning for 10 years and Unlimited: the files grow to about 100 MB per 10 game years (Unlimited: without end). " +
                    "They are read when the game starts, which can add several seconds to loading and use more memory. " +
                    "Check the size of ModsData/LiveTrafficRouting now and then." },
                { s.GetEnumValueLocaleID(KeepStatistics.OneMonth), "1 game month (30 days, about 1 MB)" },
                { s.GetEnumValueLocaleID(KeepStatistics.ThreeMonths), "3 game months (90 days, about 3 MB)" },
                { s.GetEnumValueLocaleID(KeepStatistics.OneYear), "1 game year (365 days, about 11 MB)" },
                { s.GetEnumValueLocaleID(KeepStatistics.ThreeYears), "3 game years (1,095 days, about 33 MB)" },
                { s.GetEnumValueLocaleID(KeepStatistics.TenYears), "10 game years (3,650 days, about 105 MB) - large" },
                { s.GetEnumValueLocaleID(KeepStatistics.Unlimited), "Unlimited (never cleaned up) - grows without limit" },
                { s.GetOptionWarningLocaleID(nameof(LiveTrafficSettings.KeepStatisticsFor)),
                    "Large statistics files can slow down loading the game. See the description." },

                { s.GetOptionLabelLocaleID(nameof(LiveTrafficSettings.LogStatistics)), "Write traffic report to log" },
                { s.GetOptionDescLocaleID(nameof(LiveTrafficSettings.LogStatistics)),
                    "Write extra detail to Logs/LiveTrafficRouting.log for testing and bug reports: every game hour the statistics " +
                    "(needs the statistics panel on), the busiest planned streets, forecast counts and Auto tuning steps. " +
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
