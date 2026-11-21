using System.Globalization;
using System.Text;
using Colossal.UI.Binding;
using Game.Net;
using Game.UI;
using Game.UI.InGame;
using Unity.Collections;
using Unity.Entities;

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
            AddBinding(new TriggerBinding<string>(Group, "focusPlace", FocusPlace));
            Logger.Info("TrafficUISystem created.");
        }

        /// <summary>
        /// Shows a hotspot on the map: selects the street or junction (opens the game's info panel) and lets the
        /// camera follow it, like the game's own focus button. A street is shown at a segment in its middle.
        /// </summary>
        private void FocusPlace(string place)
        {
            Entity entity = Entity.Null;
            if (!TrafficReportStore.TryGetPlace(place, out entity) || !EntityManager.Exists(entity))
            {
                entity = FindStreetByName(place);
            }
            if (entity == Entity.Null)
            {
                Logger.Info($"Hotspot '{place}' not found on the map (it may be from an earlier session or was renamed).");
                return;
            }
            Entity target = entity;
            if (EntityManager.HasBuffer<AggregateElement>(entity))
            {
                DynamicBuffer<AggregateElement> edges = EntityManager.GetBuffer<AggregateElement>(entity, true);
                if (edges.Length > 0) target = edges[edges.Length / 2].m_Edge;
            }
            SelectedInfoUISystem selected = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            selected.SetSelection(target);
            selected.Focus(target);
        }

        /// <summary>For hotspots recorded in an earlier session: look the street up by its name.</summary>
        private Entity FindStreetByName(string place)
        {
            NameSystem names = World.GetOrCreateSystemManaged<NameSystem>();
            EntityQuery query = GetEntityQuery(ComponentType.ReadOnly<Aggregate>(), ComponentType.Exclude<Game.Common.Deleted>());
            using (NativeArray<Entity> streets = query.ToEntityArray(Allocator.Temp))
            {
                foreach (Entity street in streets)
                {
                    if (TrafficReportSystem.CleanName(names.GetRenderedLabelName(street)) == place)
                    {
                        TrafficReportStore.RememberPlace(place, street);
                        return street;
                    }
                }
            }
            return Entity.Null;
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
