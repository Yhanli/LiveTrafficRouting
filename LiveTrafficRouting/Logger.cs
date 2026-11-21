using System;
using Colossal.Logging;

namespace LiveTrafficRouting
{
    internal static class Logger
    {
        // Logging must never break the simulation update.
        private static readonly ILog Log = LogManager.GetLogger(nameof(LiveTrafficRouting)).SetShowsErrorsInUI(false);

        internal static void Info(string message) => SafeWrite(log => log.Info(message));
        internal static void Warn(string message) => SafeWrite(log => log.Warn(message));
        internal static void Error(string message) => SafeWrite(log => log.Error(message));

        private static void SafeWrite(Action<ILog> write)
        {
            try { write?.Invoke(Log); }
            catch { /* ignore logging failures */ }
        }
    }
}
