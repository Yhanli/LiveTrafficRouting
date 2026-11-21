using Unity.Entities;

namespace LiveTrafficRouting
{
    /// <summary>Deleting recorded statistics (always with a backup of the file). Main thread.</summary>
    internal static class StatisticsReset
    {
        public static void DeleteAll() => After(TrafficReportStore.DeleteCurrentCity(), restart: true);

        /// <summary>Keeps the Native runs as a baseline.</summary>
        public static void DeleteLive() => After(TrafficReportStore.DeleteLiveRuns(), restart: false);

        public static void DeleteRun(string runId) => After(TrafficReportStore.DeleteRun(runId), restart: false);

        private static void After(int removed, bool restart)
        {
            if (restart)
            {
                World world = World.DefaultGameObjectInjectionWorld;
                world?.GetExistingSystemManaged<TrafficReportSystem>()?.RestartHour();
            }
            Logger.Info($"Statistics deleted: {removed} game hours.");
        }
    }
}
