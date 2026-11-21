using Unity.Entities;

namespace LiveTrafficRouting
{
    internal static class StatisticsReset
    {
        /// <summary>Clears this city's recorded statistics (with a file backup) and restarts the current hour.</summary>
        public static void ResetCurrentCity()
        {
            int removed = TrafficReportStore.ClearCurrentCity();
            World world = World.DefaultGameObjectInjectionWorld;
            world?.GetExistingSystemManaged<TrafficReportSystem>()?.RestartHour();
            Logger.Info($"Statistics reset: {removed} game hours removed.");
        }
    }
}
