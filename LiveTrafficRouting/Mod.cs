using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace LiveTrafficRouting
{
    public class Mod : IMod
    {
        /// <summary>Keep equal to ModVersion in Properties/PublishConfiguration.xml (tools\check.ps1 checks it).</summary>
        internal const string Version = "1.1.0";

        internal static LiveTrafficSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Logger.Info($"Live Traffic Routing {Version} loading.");

            Settings = new LiveTrafficSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings(nameof(LiveTrafficRouting), Settings, new LiveTrafficSettings(this));
            Settings.ReapplyPickedPreset();
            Logger.Info($"Settings: {Settings.RunLabel()}; statistics panel {(Settings.ShowStatistics ? "on" : "off")}, detailed log {(Settings.LogStatistics ? "on" : "off")}.");
            GameManager.instance.localizationManager.AddSource("en-US", new LiveTrafficLocale(Settings));

            // The forecast and the temporary road marks feed the live traffic system, so they come first.
            updateSystem.UpdateAt<DemandForecastSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RoadMarkSystem>(SystemUpdatePhase.GameSimulation);
            // Same phase as the vanilla TrafficFlowSystem it replaces.
            updateSystem.UpdateAt<LiveTrafficFlowSystem>(SystemUpdatePhase.GameSimulation);
            // The temporary roadworks tool (opened from the Live Traffic panel).
            updateSystem.UpdateAt<RoadMarkToolSystem>(SystemUpdatePhase.ToolUpdate);
            // Map overlay for a bottleneck or possible link picked in the panel (drawn every frame while shown).
            updateSystem.UpdateAt<BottleneckOverlaySystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<RerouteSystem>(SystemUpdatePhase.GameSimulation);
            // Statistics: measured whether the mod is on or off, shown in Options and in the in-game panel.
            updateSystem.UpdateAt<TrafficReportSystem>(SystemUpdatePhase.GameSimulation);
            // Bottleneck finder: statistics panel only, changes no routing.
            updateSystem.UpdateAt<BottleneckSystem>(SystemUpdatePhase.GameSimulation);
            // Cars per hour through the turns of the bottleneck junctions (lanes chosen by BottleneckSystem).
            updateSystem.UpdateAt<TurnFlowSystem>(SystemUpdatePhase.GameSimulation);
            // Vehicles the game removes (stuck, failed route, trip done): Modification1 runs every frame and sees each
            // deleted vehicle once.
            updateSystem.UpdateAt<DespawnCounterSystem>(SystemUpdatePhase.Modification1);
            updateSystem.UpdateAt<TrafficUISystem>(SystemUpdatePhase.UIUpdate);

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
            {
                Logger.Info($"Live Traffic Routing loaded from {asset.path}");
            }
        }

        public void OnDispose()
        {
            PopupGuard.Restore();
            Settings?.UnregisterInOptionsUI();
            Settings = null;
            Logger.Info("Live Traffic Routing disposed.");
        }
    }
}
