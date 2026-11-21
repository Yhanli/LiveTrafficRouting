import { useMemo, useState } from "react";
import { bindValue, useValue } from "cs2/api";
import { FloatingButton, Panel, Tooltip } from "cs2/ui";
import styles from "./LiveTrafficPanel.module.scss";

// Bindings from TrafficUISystem.cs (JSON strings).
const GROUP = "liveTrafficRouting";
const current$ = bindValue<string>(GROUP, "current", "{}");
const history$ = bindValue<string>(GROUP, "history", "[]");
const averages$ = bindValue<string>(GROUP, "averages", "{}");
const byHour$ = bindValue<string>(GROUP, "byHour", "{}");

const ICON = "Media/Game/Icons/Traffic.svg";

interface Current {
    mode?: string; preset?: string; t?: string; veh?: number; speed?: number; queued?: number;
    local?: number; jams?: number; navKmh?: number; otherKmh?: number; navShare?: number; ms?: number;
}
interface Capture {
    t: string; h: number; mode: string; preset: string; veh: number; speed: number; queued: number; local: number;
    jams: number; jamsMax: number; flow: number; navKmh: number; otherKmh: number; navShare: number;
    rr: number; rrCars: number; rrTrucks: number; rrTaxis: number; rrBetter: number; rrNoBetter: number;
    rrSaved: number; planSpeed: number; planJammed: number; ms: number;
}
/** Averages over a set of game hours (also used per hour of the day). */
interface Average {
    h?: number; hours: number; speed: number; queued: number; jams: number; local: number; flow: number;
    navKmh: number; otherKmh: number;
}
interface Averages { native?: Average; live?: Average; }
interface ByHour { native?: Average[]; live?: Average[]; }

type Filter = "All" | "Native" | "Live";
type View = "Timeline" | "TimeOfDay";
type TableScope = "AllHours" | "SameHours";
type MetricKey = "speed" | "queued" | "jams" | "flow" | "local" | "navGain";

interface Metric {
    key: MetricKey;
    label: string;
    unit: string;
    higherIsBetter: boolean;
    value: (c: Average | Capture) => number;
}

const METRICS: Metric[] = [
    { key: "speed", label: "Real speed", unit: "%", higherIsBetter: true, value: c => c.speed },
    { key: "queued", label: "Queued", unit: "%", higherIsBetter: false, value: c => c.queued },
    { key: "jams", label: "Jam warnings", unit: "", higherIsBetter: false, value: c => c.jams },
    { key: "flow", label: "Traffic flow", unit: "%", higherIsBetter: true, value: c => c.flow },
    { key: "local", label: "Local streets", unit: "%", higherIsBetter: false, value: c => c.local },
    { key: "navGain", label: "Nav advantage", unit: " km/h", higherIsBetter: true, value: c => c.navKmh - c.otherKmh },
];

const MAX_BARS = 48;
const EMPTY: Average = { hours: 0, speed: 0, queued: 0, jams: 0, local: 0, flow: 0, navKmh: 0, otherKmh: 0 };

function parse<T>(json: string, fallback: T): T {
    try {
        return JSON.parse(json) as T;
    } catch {
        return fallback;
    }
}

function fmt(v: number | undefined, digits = 0, unit = ""): string {
    if (v === undefined || v === null || isNaN(v)) return "-";
    return v.toFixed(digits) + unit;
}

function modeLabel(mode?: string, preset?: string): string {
    if (mode === "Live") return `Live traffic (${preset ?? ""})`;
    if (mode === "Native") return "Native (mod off)";
    if (mode === "Mixed") return "Mixed";
    return "Waiting for data";
}

/** Average over the hours of the day that have data in BOTH modes, each hour of the day weighted equally. */
function sameHours(byHour: ByHour): { native: Average; live: Average; slots: number } {
    const n = byHour.native ?? [];
    const l = byHour.live ?? [];
    const sum = (list: Average[], pick: number[]): Average => {
        const r: Average = { ...EMPTY };
        for (const h of pick) {
            const a = list[h];
            r.hours += a.hours;
            r.speed += a.speed; r.queued += a.queued; r.jams += a.jams; r.local += a.local; r.flow += a.flow;
            r.navKmh += a.navKmh; r.otherKmh += a.otherKmh;
        }
        const k = Math.max(1, pick.length);
        r.speed /= k; r.queued /= k; r.jams /= k; r.local /= k; r.flow /= k; r.navKmh /= k; r.otherKmh /= k;
        return r;
    };
    const slots: number[] = [];
    for (let h = 0; h < 24; h++) {
        if (n[h] && l[h] && n[h].hours > 0 && l[h].hours > 0) slots.push(h);
    }
    return { native: sum(n, slots), live: sum(l, slots), slots: slots.length };
}

const Row = ({ label, value, hint }: { label: string; value: string; hint?: string }) => {
    const row = (
        <div className={styles.row}>
            <div className={styles.rowLabel}>{label}{hint ? <span className={styles.hintMark}> ?</span> : null}</div>
            <div className={styles.rowValue}>{value}</div>
        </div>
    );
    return hint ? <Tooltip tooltip={<div className={styles.tip}>{hint}</div>}>{row}</Tooltip> : row;
};

/** Hour label under a timeline bar: "14", or the date at midnight. */
function barLabel(t: string, h: number): string {
    if (h === 0 && t.length >= 10) return t.substring(5, 10);
    return String(h).padStart(2, "0");
}

const TipLine = ({ label, value }: { label: string; value: string }) => (
    <div className={styles.tipLine}><span>{label}</span><span>{value}</span></div>
);

function captureTip(c: Capture): JSX.Element {
    return (
        <div className={styles.tip}>
            <div className={styles.tipTitle}>{c.t} - {modeLabel(c.mode, c.preset)}</div>
            <TipLine label="Vehicles" value={fmt(c.veh)} />
            <TipLine label="Real speed" value={fmt(c.speed, 1, "%")} />
            <TipLine label="Waiting in queues" value={fmt(c.queued, 1, "%")} />
            <TipLine label="Jam warnings (avg / max)" value={`${fmt(c.jams, 1)} / ${fmt(c.jamsMax)}`} />
            <TipLine label="Traffic flow (game)" value={fmt(c.flow, 0, "%")} />
            <TipLine label="On local streets" value={fmt(c.local, 0, "%")} />
            <TipLine label="Nav vs others" value={c.otherKmh > 0 ? `${fmt(c.navKmh, 1)} / ${fmt(c.otherKmh, 1)} km/h` : "-"} />
            {c.mode !== "Native" && (
                <TipLine label="Re-routes (faster)" value={`${c.rr} (${c.rrBetter})`} />
            )}
        </div>
    );
}

const Toggle = ({ label, selected, onClick }: { label: string; selected: boolean; onClick: () => void }) => (
    <div className={selected ? `${styles.toggle} ${styles.toggleOn}` : styles.toggle} onClick={onClick}>
        {label}
    </div>
);

function navText(navKmh: number | undefined, otherKmh: number | undefined, share: number | undefined): string {
    if (share !== undefined && share >= 100) return "no comparison: everyone has navigation";
    if (share !== undefined && share <= 0) return "no comparison: nobody has navigation";
    return `${fmt(navKmh, 1)} vs ${fmt(otherKmh, 1)} km/h`;
}

export const LiveTrafficPanel = () => {
    const [expanded, setExpanded] = useState(false);
    const [filter, setFilter] = useState<Filter>("All");
    const [view, setView] = useState<View>("Timeline");
    const [scope, setScope] = useState<TableScope>("SameHours");
    const [metricKey, setMetricKey] = useState<MetricKey>("speed");
    const [hover, setHover] = useState<number>(-1);

    const currentJson = useValue(current$);
    const historyJson = useValue(history$);
    const averagesJson = useValue(averages$);
    const byHourJson = useValue(byHour$);

    const current = useMemo(() => parse<Current>(currentJson, {}), [currentJson]);
    const history = useMemo(() => parse<Capture[]>(historyJson, []), [historyJson]);
    const averages = useMemo(() => parse<Averages>(averagesJson, {}), [averagesJson]);
    const byHour = useMemo(() => parse<ByHour>(byHourJson, {}), [byHourJson]);
    const matched = useMemo(() => sameHours(byHour), [byHour]);

    const metric = METRICS.find(m => m.key === metricKey) ?? METRICS[0];
    const lastLive = [...history].reverse().find(c => c.mode === "Live");

    // Comparison table source: all recorded hours, or only the hours of the day both modes have.
    const tableNative = scope === "SameHours" ? matched.native : (averages.native ?? EMPTY);
    const tableLive = scope === "SameHours" ? matched.live : (averages.live ?? EMPTY);
    const cell = (a: Average, f: (a: Average) => string) => (a.hours > 0 ? f(a) : "-");

    // Timeline chart data
    const timeline = useMemo(
        () => history.filter(c => filter === "All" || c.mode === filter).slice(-MAX_BARS),
        [history, filter]);
    const timelineValues = timeline.map(metric.value);

    // Time-of-day chart data
    const showNative = filter !== "Live";
    const showLive = filter !== "Native";
    const nativeHours = byHour.native ?? [];
    const liveHours = byHour.live ?? [];
    const todValues: number[] = [];
    for (let h = 0; h < 24; h++) {
        if (showNative && nativeHours[h]?.hours > 0) todValues.push(metric.value(nativeHours[h]));
        if (showLive && liveHours[h]?.hours > 0) todValues.push(metric.value(liveHours[h]));
    }

    const chartValues = view === "Timeline" ? timelineValues : todValues;
    const maxAbs = Math.max(1e-6, ...chartValues.map(v => Math.abs(v)));
    const barHeight = (v: number) => `${Math.max(2, Math.abs(v) / maxAbs * 100)}%`;
    const better = metric.higherIsBetter ? " (higher is better)" : " (lower is better)";

    return (
        <div className={styles.root}>
            <FloatingButton
                src={ICON}
                selected={expanded}
                tooltipLabel={expanded ? "Hide Live Traffic statistics" : "Live Traffic statistics"}
                onSelect={() => setExpanded(!expanded)}
            />
            {expanded && (
                <Panel header="Live Traffic" className={styles.panel} onClose={() => setExpanded(false)}>
                    <div className={styles.body}>
                        {/* Right now */}
                        <div className={styles.sectionTitle}>
                            Right now <span className={current.mode === "Live" ? styles.badgeLive : styles.badgeNative}>{modeLabel(current.mode, current.preset)}</span>
                        </div>
                        <Row label="Vehicles on the road" value={fmt(current.veh)} />
                        <Row label="Real speed vs limit" value={fmt(current.speed, 0, "%")} hint="Average real driving speed of all road vehicles compared with the speed limit, including vehicles waiting at lights and in queues." />
                        <Row label="Waiting in queues" value={fmt(current.queued, 1, "%")} hint="Vehicles stopped behind another stopped vehicle." />
                        <Row label="Jam warnings on the map" value={fmt(current.jams)} />
                        <Row label="On local streets" value={fmt(current.local, 0, "%")} hint="Vehicles on roads with a limit of 50 km/h or less." />
                        <Row
                            label={`With navigation (${fmt(current.navShare, 0, "%")}) vs others`}
                            value={navText(current.navKmh, current.otherKmh, current.navShare)}
                            hint="How fast vehicles get closer to their destination (straight line), this game hour. The share comes from 'Drivers who re-route while driving'. With the mod off both groups should be about equal." />

                        {/* Native vs Live */}
                        <div className={styles.sectionTitle}>Native vs live traffic (this city)</div>
                        <div className={styles.toggles}>
                            <Toggle label="Same times of day" selected={scope === "SameHours"} onClick={() => setScope("SameHours")} />
                            <Toggle label="All hours" selected={scope === "AllHours"} onClick={() => setScope("AllHours")} />
                        </div>
                        {scope === "SameHours" && (
                            <div className={styles.note}>
                                {matched.slots > 0
                                    ? `Only the ${matched.slots} hours of the day recorded in both modes, each weighted equally.`
                                    : "No hour of the day has been recorded in both modes yet."}
                            </div>
                        )}
                        <div className={styles.table}>
                            <div className={styles.tableRow}>
                                <div className={styles.tableLabel}></div>
                                <div className={styles.tableHead}>Native</div>
                                <div className={styles.tableHead}>Live</div>
                            </div>
                            {([
                                ["Game hours recorded", (a: Average) => fmt(a.hours)],
                                ["Real speed", (a: Average) => fmt(a.speed, 1, "%")],
                                ["Waiting in queues", (a: Average) => fmt(a.queued, 1, "%")],
                                ["Jam warnings", (a: Average) => fmt(a.jams, 1)],
                                ["Traffic flow (game)", (a: Average) => fmt(a.flow, 0, "%")],
                                ["On local streets", (a: Average) => fmt(a.local, 0, "%")],
                                ["Nav vs others (km/h)", (a: Average) => a.otherKmh > 0 ? `${fmt(a.navKmh, 1)} / ${fmt(a.otherKmh, 1)}` : "-"],
                            ] as [string, (a: Average) => string][]).map(([label, f]) => (
                                <div className={styles.tableRow} key={label}>
                                    <div className={styles.tableLabel}>{label}</div>
                                    <div className={styles.tableCell}>{cell(tableNative, f)}</div>
                                    <div className={styles.tableCell}>{cell(tableLive, f)}</div>
                                </div>
                            ))}
                        </div>

                        {/* Charts */}
                        <div className={styles.sectionTitle}>History</div>
                        <div className={styles.toggles}>
                            <Toggle label="Timeline" selected={view === "Timeline"} onClick={() => { setView("Timeline"); setHover(-1); }} />
                            <Toggle label="By time of day" selected={view === "TimeOfDay"} onClick={() => { setView("TimeOfDay"); setHover(-1); }} />
                            <div className={styles.spacer} />
                            {(["All", "Native", "Live"] as Filter[]).map(f => (
                                <Toggle key={f} label={f === "Live" ? "Live traffic" : f} selected={filter === f} onClick={() => setFilter(f)} />
                            ))}
                        </div>
                        <div className={styles.toggles}>
                            {METRICS.map(m => (
                                <Toggle key={m.key} label={m.label} selected={metricKey === m.key} onClick={() => setMetricKey(m.key)} />
                            ))}
                        </div>

                        {view === "Timeline" && (timeline.length === 0 ? (
                            <div className={styles.empty}>No game hours recorded for this filter yet.</div>
                        ) : (
                            <>
                                <div className={styles.detail}>
                                    {(() => {
                                        const i = hover >= 0 && hover < timeline.length ? hover : timeline.length - 1;
                                        const c = timeline[i];
                                        return `${c.t} - ${modeLabel(c.mode, c.preset)} - ${metric.label}: ${fmt(timelineValues[i], 1, metric.unit)}`;
                                    })()}
                                </div>
                                <div className={styles.chart} onMouseLeave={() => setHover(-1)}>
                                    {timeline.map((c, i) => (
                                        <Tooltip key={c.t + i} tooltip={captureTip(c)}>
                                            <div className={hover === i ? `${styles.barSlot} ${styles.barSlotHover}` : styles.barSlot}
                                                 onMouseEnter={() => setHover(i)}>
                                                <div
                                                    className={c.mode === "Live" ? styles.barLive : c.mode === "Native" ? styles.barNative : styles.barMixed}
                                                    style={{ height: barHeight(timelineValues[i]) }}
                                                />
                                            </div>
                                        </Tooltip>
                                    ))}
                                </div>
                                <div className={styles.barLabels}>
                                    {timeline.map((c, i) => {
                                        const every = timeline.length > 24 ? 4 : timeline.length > 12 ? 2 : 1;
                                        const show = i % every === 0 || c.h === 0;
                                        return <div className={styles.barLabel} key={c.t + i}>{show ? barLabel(c.t, c.h) : ""}</div>;
                                    })}
                                </div>
                                <div className={styles.chartFoot}>
                                    <span>Game hours, from {timeline[0].t}</span>
                                    <span>max {fmt(maxAbs, 1, metric.unit)}{better}</span>
                                </div>
                            </>
                        ))}

                        {view === "TimeOfDay" && (todValues.length === 0 ? (
                            <div className={styles.empty}>No game hours recorded for this filter yet.</div>
                        ) : (
                            <>
                                <div className={styles.detail}>
                                    {(() => {
                                        const h = hover >= 0 && hover < 24 ? hover : -1;
                                        if (h < 0) return "Hover an hour to compare Native and Live";
                                        const n = nativeHours[h];
                                        const l = liveHours[h];
                                        const nv = n?.hours ? metric.value(n) : undefined;
                                        const lv = l?.hours ? metric.value(l) : undefined;
                                        const diff = nv !== undefined && lv !== undefined ? `, difference ${lv - nv >= 0 ? "+" : ""}${fmt(lv - nv, 1, metric.unit)}` : "";
                                        return `${String(h).padStart(2, "0")}:00 - ${metric.label}: Native ${fmt(nv, 1, metric.unit)} (${n?.hours ?? 0} h), Live ${fmt(lv, 1, metric.unit)} (${l?.hours ?? 0} h)${diff}`;
                                    })()}
                                </div>
                                <div className={styles.chart} onMouseLeave={() => setHover(-1)}>
                                    {Array.from({ length: 24 }, (_, h) => {
                                        const n = nativeHours[h];
                                        const l = liveHours[h];
                                        const tip = (
                                            <div className={styles.tip}>
                                                <div className={styles.tipTitle}>{String(h).padStart(2, "0")}:00 to {String(h + 1).padStart(2, "0")}:00 - {metric.label}</div>
                                                <TipLine label={`Native (${n?.hours ?? 0} h)`} value={n?.hours ? fmt(metric.value(n), 1, metric.unit) : "-"} />
                                                <TipLine label={`Live traffic (${l?.hours ?? 0} h)`} value={l?.hours ? fmt(metric.value(l), 1, metric.unit) : "-"} />
                                            </div>
                                        );
                                        return (
                                            <Tooltip key={h} tooltip={tip}>
                                                <div className={hover === h ? `${styles.hourSlot} ${styles.barSlotHover}` : styles.hourSlot}
                                                     onMouseEnter={() => setHover(h)}>
                                                    {showNative && (
                                                        <div className={styles.barNative} style={{ height: n?.hours ? barHeight(metric.value(n)) : "0%" }} />
                                                    )}
                                                    {showLive && (
                                                        <div className={styles.barLive} style={{ height: l?.hours ? barHeight(metric.value(l)) : "0%" }} />
                                                    )}
                                                </div>
                                            </Tooltip>
                                        );
                                    })}
                                </div>
                                <div className={styles.barLabels}>
                                    {Array.from({ length: 24 }, (_, h) => (
                                        <div className={styles.barLabel} key={h}>{h % 3 === 0 ? String(h).padStart(2, "0") : ""}</div>
                                    ))}
                                </div>
                                <div className={styles.chartFoot}>
                                    <span>Average per hour of the day</span>
                                    <span>{metric.label}{better}</span>
                                </div>
                            </>
                        ))}
                        <div className={styles.legend}>
                            <span className={styles.legendNative}>Native</span>
                            <span className={styles.legendLive}>Live traffic</span>
                        </div>

                        {/* Re-routing */}
                        <div className={styles.sectionTitle}>Re-routing (last live game hour)</div>
                        {lastLive ? (
                            <>
                                <Row label="Re-routes" value={`${lastLive.rr} (${lastLive.rrCars} cars, ${lastLive.rrTrucks} trucks, ${lastLive.rrTaxis} taxis)`} />
                                <Row
                                    label="Found a faster route"
                                    value={lastLive.rrBetter + lastLive.rrNoBetter > 0
                                        ? `${fmt(100 * lastLive.rrBetter / (lastLive.rrBetter + lastLive.rrNoBetter), 0, "%")} (avg ${fmt(lastLive.rrSaved, 0, "%")} saved, estimate)`
                                        : "-"} />
                                <Row label="Route planner sees" value={`${fmt(lastLive.planSpeed, 0, "%")} speed, ${fmt(lastLive.planJammed, 1, "%")} jammed`} hint="Speeds with the mod's jam penalties applied, as the route planner uses them." />
                            </>
                        ) : (
                            <div className={styles.empty}>No live game hour recorded yet.</div>
                        )}
                        <Row
                            label="Mod processing time"
                            value={current.mode === "Native"
                                ? "mod is off (only statistics run)"
                                : current.ms !== undefined && current.ms >= 0 ? `${fmt(current.ms, 2)} ms per run` : "measured once per game hour"} />
                    </div>
                </Panel>
            )}
        </div>
    );
};
