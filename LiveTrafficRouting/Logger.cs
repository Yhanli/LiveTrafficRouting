using System;
using System.IO;
using Colossal.Logging;
using UnityEngine;

namespace LiveTrafficRouting
{
    internal static class Logger
    {
        // Logging must never break the simulation update.
        private static readonly ILog Log = KeepPreviousLog() ? null : LogManager.GetLogger(nameof(LiveTrafficRouting)).SetShowsErrorsInUI(false);

        /// <summary>
        /// The game starts every log file afresh, so a test session's log was lost as soon as the game was started again.
        /// Copy it to LiveTrafficRouting-prev.log first. Always returns false (it only runs before the logger exists).
        /// </summary>
        private static bool KeepPreviousLog()
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "Logs");
                string log = Path.Combine(dir, nameof(LiveTrafficRouting) + ".log");
                if (File.Exists(log) && new FileInfo(log).Length > 0)
                {
                    File.Copy(log, Path.Combine(dir, nameof(LiveTrafficRouting) + "-prev.log"), true);
                }
            }
            catch { /* the copy is a convenience only */ }
            return false;
        }

        internal static void Info(string message) => SafeWrite(log => log.Info(message));
        internal static void Warn(string message) => SafeWrite(log => log.Warn(message));
        internal static void Error(string message) => SafeWrite(log => log.Error(message));

        /// <summary>Test and bug-report detail: written only while "Write traffic report to log" is on.</summary>
        internal static bool DetailOn => Mod.Settings != null && Mod.Settings.LogStatistics;

        internal static void Detail(string message)
        {
            if (DetailOn) Info(message);
        }

        private static void SafeWrite(Action<ILog> write)
        {
            try { write?.Invoke(Log); }
            catch { /* ignore logging failures */ }
        }
    }
}
