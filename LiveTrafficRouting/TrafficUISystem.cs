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
        private string m_CurrentJson = "{}";
        private string m_HistoryJson = "[]";
        private string m_RunsJson = "[]";

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<bool>(Group, "enabled", () => Mod.Settings == null || Mod.Settings.ShowStatistics));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "current", () => { Refresh(); return m_CurrentJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "history", () => { Refresh(); return m_HistoryJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "runs", () => { Refresh(); return m_RunsJson; }));
            AddBinding(new TriggerBinding(Group, "deleteAll", StatisticsReset.DeleteAll));
            AddBinding(new TriggerBinding(Group, "deleteLive", StatisticsReset.DeleteLive));
            AddBinding(new TriggerBinding<string>(Group, "deleteRun", StatisticsReset.DeleteRun));
            Logger.Info("TrafficUISystem created.");
        }

        private void Refresh()
        {
            if (m_Version == TrafficReportStore.Version) return;
            m_Version = TrafficReportStore.Version;
            m_CurrentJson = TrafficReportStore.Current.ToJson();
            m_HistoryJson = TrafficReportStore.HistoryJson();
            m_RunsJson = TrafficReportStore.RunsJson();
        }
    }
}
