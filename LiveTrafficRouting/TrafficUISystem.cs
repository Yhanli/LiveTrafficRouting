using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.UI;

namespace LiveTrafficRouting
{
    /// <summary>
    /// Feeds the in-game statistics panel (UI module in /UI). Values are JSON strings, rebuilt only when the
    /// report store changes.
    /// </summary>
    public partial class TrafficUISystem : UISystemBase
    {
        internal const string Group = "liveTrafficRouting";

        private int m_Version = -1;
        private int m_DataVersion = -1;
        private string m_CurrentJson = "{}";
        private string m_HistoryJson = "[]";
        private string m_RunsJson = "[]";
        private string m_HotspotsJson = "{}";

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "enabled", () => Mod.Settings == null || Mod.Settings.ShowStatistics));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "current", () => { Refresh(); return m_CurrentJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "history", () => { Refresh(); return m_HistoryJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "runs", () => { Refresh(); return m_RunsJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "hotspots", () => { Refresh(); return m_HotspotsJson; }));
            AddBinding(new TriggerBinding(Group, "deleteAll", StatisticsReset.DeleteAll));
            AddBinding(new TriggerBinding(Group, "deleteLive", StatisticsReset.DeleteLive));
            AddBinding(new TriggerBinding<string>(Group, "deleteRun", StatisticsReset.DeleteRun));
            AddBinding(new TriggerBinding<string>(Group, "togglePin", TrafficReportStore.TogglePin));
            Logger.Info("TrafficUISystem created.");
        }

        private void Refresh()
        {
            if (m_Version == TrafficReportStore.Version) return;
            m_Version = TrafficReportStore.Version;
            m_CurrentJson = TrafficReportStore.Current.ToJson();
            // The heavy parts only when recorded data changed, not on every "right now" update.
            if (m_DataVersion == TrafficReportStore.DataVersion) return;
            m_DataVersion = TrafficReportStore.DataVersion;
            m_HistoryJson = TrafficReportStore.HistoryJson();
            m_RunsJson = TrafficReportStore.RunsJson();
            m_HotspotsJson = TrafficReportStore.HotspotsJson(25);
        }
    }
}
