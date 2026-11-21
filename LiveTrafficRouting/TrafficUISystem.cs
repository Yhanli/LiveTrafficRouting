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
        private const int kHistoryHours = 24 * 7;

        private int m_Version = -1;
        private string m_CurrentJson = "{}";
        private string m_HistoryJson = "[]";
        private string m_AveragesJson = "{}";
        private string m_ByHourJson = "{}";

        protected override void OnCreate()
        {
            base.OnCreate();
            AddUpdateBinding(new GetterValueBinding<string>(Group, "current", () => { Refresh(); return m_CurrentJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "history", () => { Refresh(); return m_HistoryJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "averages", () => { Refresh(); return m_AveragesJson; }));
            AddUpdateBinding(new GetterValueBinding<string>(Group, "byHour", () => { Refresh(); return m_ByHourJson; }));
            AddBinding(new TriggerBinding(Group, "resetStats", StatisticsReset.ResetCurrentCity));
            Logger.Info("TrafficUISystem created.");
        }

        private void Refresh()
        {
            if (m_Version == TrafficReportStore.Version) return;
            m_Version = TrafficReportStore.Version;
            m_CurrentJson = TrafficReportStore.Current.ToJson();
            m_HistoryJson = TrafficReportStore.HistoryJson(kHistoryHours);

            var sb = new StringBuilder("{");
            AppendAverage(sb, "native", TrafficReportStore.Average("Native"));
            sb.Append(',');
            AppendAverage(sb, "live", TrafficReportStore.Average("Live"));
            sb.Append('}');
            m_AveragesJson = sb.ToString();
            m_ByHourJson = TrafficReportStore.ByHourJson();
        }

        private static void AppendAverage(StringBuilder sb, string key, TrafficReportStore.ModeAverage a)
        {
            sb.Append('"').Append(key).Append("\":{");
            TrafficCapture.Num(sb, "hours", a.Hours);
            TrafficCapture.Num(sb, "speed", a.Speed);
            TrafficCapture.Num(sb, "queued", a.Queued);
            TrafficCapture.Num(sb, "jams", a.Jams);
            TrafficCapture.Num(sb, "local", a.Local);
            TrafficCapture.Num(sb, "flow", a.Flow);
            TrafficCapture.Num(sb, "navKmh", a.NavKmh);
            TrafficCapture.Num(sb, "otherKmh", a.OtherKmh, last: true);
            sb.Append('}');
        }
    }
}
