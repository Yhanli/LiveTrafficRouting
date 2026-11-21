using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace LiveTrafficRouting
{
    public class Mod : IMod
    {
        internal static LiveTrafficSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Logger.Info("Live Traffic Routing loading.");

            Settings = new LiveTrafficSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(LiveTrafficRouting), Settings, new LiveTrafficSettings(this));
            GameManager.instance.localizationManager.AddSource("en-US", new LiveTrafficLocale(Settings));

            // Same phase as the vanilla TrafficFlowSystem it replaces.
            updateSystem.UpdateAt<LiveTrafficFlowSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RerouteSystem>(SystemUpdatePhase.GameSimulation);
            // Statistics: measured whether the mod is on or off, shown in Options and in the in-game panel.
            updateSystem.UpdateAt<TrafficReportSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<TrafficUISystem>(SystemUpdatePhase.UIUpdate);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Logger.Info($"Live Traffic Routing loaded from {asset.path}");
            }
        }

        public void OnDispose()
        {
            Settings?.UnregisterInOptionsUI();
            Settings = null;
            Logger.Info("Live Traffic Routing disposed.");
        }
    }
}
