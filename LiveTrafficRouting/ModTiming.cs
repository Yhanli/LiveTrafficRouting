namespace LiveTrafficRouting
{
    /// <summary>
    /// Once per game hour the report asks each system to time one of its runs. The system then waits for its
    /// inputs, runs its jobs to completion on the spot and records the time. This briefly stalls one frame per
    /// game hour; the rest of the time the jobs run in the background as usual.
    /// </summary>
    internal static class ModTiming
    {
        public static bool FlowRequested;
        public static bool RerouteRequested;
        public static bool ReportRequested;
        public static double ReportMs = -1;
        public static double FlowMs = -1;
        public static double RerouteMs = -1;

        public static void RequestAll()
        {
            FlowRequested = true;
            RerouteRequested = true;
            ReportRequested = true;
            ReportMs = -1;
            // Only count what was measured this hour (a system that is off this hour reports nothing).
            FlowMs = -1;
            RerouteMs = -1;
        }
    }
}
