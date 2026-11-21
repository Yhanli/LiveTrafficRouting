using System;
using Game.Settings;

namespace LiveTrafficRouting
{
    /// <summary>
    /// While statistics are recording (and the option is on), turns off the game's "blocking popups"
    /// (InterfaceSettings.blockingPopupsEnabled). Those are the milestone ("city upgrade") and signature building
    /// popups, which pause the game and so stop a test run. The player's own value is remembered and put back as soon
    /// as the option or the statistics panel is turned off, or the mod is unloaded.
    /// </summary>
    internal static class PopupGuard
    {
        private static bool s_Active;
        private static bool s_Original;

        public static void Update(LiveTrafficSettings settings)
        {
            bool want = settings != null && settings.ShowStatistics && settings.KeepRunningOnPopups;
            if (want == s_Active) return;
            try
            {
                InterfaceSettings ui = SharedSettings.instance?.userInterface;
                if (ui == null) return;
                if (want)
                {
                    s_Original = ui.blockingPopupsEnabled;
                    ui.blockingPopupsEnabled = false;
                    s_Active = true;
                    Logger.Info($"Blocking popups off while recording statistics (was {(s_Original ? "on" : "off")}).");
                }
                else
                {
                    Restore();
                }
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not change the blocking popups setting: {e.Message}");
            }
        }

        public static void Restore()
        {
            if (!s_Active) return;
            s_Active = false;
            try
            {
                InterfaceSettings ui = SharedSettings.instance?.userInterface;
                if (ui == null) return;
                ui.blockingPopupsEnabled = s_Original;
                Logger.Info($"Blocking popups restored ({(s_Original ? "on" : "off")}).");
            }
            catch (Exception e)
            {
                Logger.Warn($"Could not restore the blocking popups setting: {e.Message}");
            }
        }
    }
}
