import { ReactNode, useMemo, useState } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { FloatingButton, Panel, Tooltip } from "cs2/ui";
import styles from "./LiveTrafficPanel.module.scss";

// ---------------------------------------------------------------------------------------------
// Bindings from TrafficUISystem.cs
// ---------------------------------------------------------------------------------------------
const GROUP = "liveTrafficRouting";
const enabled$ = bindValue<boolean>(GROUP, "enabled", false);
const current$ = bindValue<string>(GROUP, "current", "{}");
const history$ = bindValue<string>(GROUP, "history", "[]");
const runs$ = bindValue<string>(GROUP, "runs", "[]");
const hotspots$ = bindValue<string>(GROUP, "hotspots", "{}");

const ICON = "Media/Game/Icons/Traffic.svg";

interface Current {
    mode?: string; preset?: string; settings?: string; run?: string; t?: string; veh?: number; speed?: number;
    queued?: number; local?: number; jams?: number; navKmh?: number; otherKmh?: number; navShare?: number; ms?: number;
}
interface Capture {
    t: string; run: string; h: number; mode: string; preset: string; n: number; veh: number; speed: number; queued: number;
    local: number; jams: number; jamsMax: number; flow: number; navKmh: number; otherKmh: number; navShare: number;
    rr: number; rrCars: number; rrTrucks: number; rrTaxis: number; rrBetter: number; rrNoBetter: number;
    rrSaved: number; rrHeld: number; planSpeed: number; planJammed: number; ms: number;
}
interface Hotspot { p: string; k: string; q: number; j: number; h: number; }
interface Run { id: string; mode: string; preset: string; settings: string; first: string; last: string; hours: number; }

/** Averages over a set of game hours. */
interface Avg {
    hours: number; speed: number; queued: number; jams: number; local: number; flow: number;
    navKmh: number; otherKmh: number; rr: number; rrTrucks: number; rrBetter: number; rrNoBetter: number;
    rrSaved: number; rrHeld: number;
}
type Row = Avg | Capture;

const AVG_KEYS: (keyof Avg)[] = ["speed", "queued", "jams", "local", "flow", "navKmh", "otherKmh", "rr", "rrTrucks", "rrBetter", "rrNoBetter", "rrSaved", "rrHeld"];
const EMPTY: Avg = { hours: 0, speed: 0, queued: 0, jams: 0, local: 0, flow: 0, navKmh: 0, otherKmh: 0, rr: 0, rrTrucks: 0, rrBetter: 0, rrNoBetter: 0, rrSaved: 0, rrHeld: 0 };

function average(list: Row[]): Avg {
    const r: Avg = { ...EMPTY };
    for (const c of list) {
        const w = "hours" in c ? c.hours : 1;
        r.hours += w;
        for (const k of AVG_KEYS) (r[k] as number) += (c as any)[k] ?? 0;
    }
    const n = Math.max(1, list.length);
    for (const k of AVG_KEYS) (r[k] as number) /= n;
    return r;
}

// ---------------------------------------------------------------------------------------------
// Metrics
// ---------------------------------------------------------------------------------------------
interface Metric {
    key: string;
    label: string;
    unit: string;
    digits: number;
    higherIsBetter: boolean;
    value: (r: Row) => number | null;
}

const TRAFFIC_METRICS: Metric[] = [
    { key: "speed", label: "Real speed", unit: "%", digits: 1, higherIsBetter: true, value: r => r.speed },
    { key: "queued", label: "Queued", unit: "%", digits: 1, higherIsBetter: false, value: r => r.queued },
    { key: "jams", label: "Jam warnings", unit: "", digits: 1, higherIsBetter: false, value: r => r.jams },
    { key: "flow", label: "Traffic flow", unit: "%", digits: 0, higherIsBetter: true, value: r => r.flow },
    { key: "local", label: "Local streets", unit: "%", digits: 1, higherIsBetter: false, value: r => r.local },
    {
        key: "navGain", label: "Nav advantage", unit: " km/h", digits: 1, higherIsBetter: true,
        // Only meaningful when both groups exist (navigation share between 0% and 100%).
        value: r => (r.otherKmh > 0 && r.navKmh > 0 ? r.navKmh - r.otherKmh : null),
    },
];

const REROUTE_METRICS: Metric[] = [
    { key: "rr", label: "Re-routes", unit: "", digits: 0, higherIsBetter: true, value: r => r.rr },
    {
        key: "faster", label: "Found faster", unit: "%", digits: 0, higherIsBetter: true,
        value: r => (r.rrBetter + r.rrNoBetter > 0 ? (100 * r.rrBetter) / (r.rrBetter + r.rrNoBetter) : null),
    },
    { key: "saved", label: "Time saved", unit: "%", digits: 0, higherIsBetter: true, value: r => (r.rrBetter > 0 ? r.rrSaved : null) },
    { key: "trucks", label: "Trucks", unit: "", digits: 0, higherIsBetter: true, value: r => r.rrTrucks },
    { key: "held", label: "Held back", unit: "", digits: 0, higherIsBetter: false, value: r => r.rrHeld },
];

// ---------------------------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------------------------
function parse<T>(json: string, fallback: T): T {
    try {
        return JSON.parse(json) as T;
    } catch {
        return fallback;
    }
}

function fmt(v: number | null | undefined, digits = 0, unit = ""): string {
    if (v === undefined || v === null || isNaN(v)) return "-";
    return v.toFixed(digits) + unit;
}

function fmtDiff(a: number | null, b: number | null, m: Metric): { text: string; cls: string } {
    if (a === null || b === null) return { text: "-", cls: "" };
    const d = b - a;
    if (Math.abs(d) < Math.pow(10, -m.digits) / 2) return { text: "0", cls: "" };
    const good = m.higherIsBetter ? d > 0 : d < 0;
    const rel = Math.abs(a) > 1e-6 ? ` (${d > 0 ? "+" : ""}${((100 * d) / Math.abs(a)).toFixed(0)}%)` : "";
    return { text: `${d > 0 ? "+" : ""}${d.toFixed(m.digits)}${m.unit}${rel}`, cls: good ? styles.good : styles.bad };
}

const pad2 = (h: number) => String(h).padStart(2, "0");

function runName(r: Run | undefined, runs: Run[]): string {
    if (!r) return "-";
    const n = runs.indexOf(r) + 1;
    return `#${n} ${r.mode === "Native" ? "Native" : r.preset}`;
}

function runRange(r: Run): string {
    return `${r.first.substring(5)} to ${r.last.substring(5)}, ${r.hours} h`;
}

/** Latest capture per game time of a run. */
function byTime(list: Capture[]): Map<string, Capture> {
    const m = new Map<string, Capture>();
    for (const c of list) m.set(c.t, c);
    return m;
}

/** Per hour of the day (0-23) averages. */
function byHourOfDay(list: Capture[]): (Avg | null)[] {
    const buckets: Capture[][] = Array.from({ length: 24 }, () => []);
    for (const c of list) if (c.h >= 0 && c.h < 24) buckets[c.h].push(c);
    return buckets.map(b => (b.length > 0 ? average(b) : null));
}

type Scope = "Paired" | "SameHours" | "AllHours";

function compare(a: Capture[], b: Capture[], scope: Scope): { a: Avg; b: Avg; note: string } {
    if (scope === "AllHours") return { a: average(a), b: average(b), note: "All recorded hours of each run." };
    if (scope === "SameHours") {
        const ha = byHourOfDay(a);
        const hb = byHourOfDay(b);
        const pa: Avg[] = [];
        const pb: Avg[] = [];
        for (let h = 0; h < 24; h++) {
            if (ha[h] && hb[h]) { pa.push(ha[h]!); pb.push(hb[h]!); }
        }
        const r = { a: average(pa), b: average(pb) };
        r.a.hours = pa.reduce((s, x) => s + x.hours, 0);
        r.b.hours = pb.reduce((s, x) => s + x.hours, 0);
        return { ...r, note: pa.length > 0 ? `The ${pa.length} hours of the day both runs have, each weighted equally.` : "The runs share no hour of the day yet." };
    }
    const ma = byTime(a);
    const mb = byTime(b);
    const pa: Capture[] = [];
    const pb: Capture[] = [];
    ma.forEach((c, t) => {
        const d = mb.get(t);
        if (d) { pa.push(c); pb.push(d); }
    });
    return {
        a: average(pa), b: average(pb),
        note: pa.length > 0
            ? `${pa.length} game hours that both runs recorded (same save played twice), compared hour by hour.`
            : "The runs share no game hour. Play the same save in both runs to compare hour by hour.",
    };
}

// ---------------------------------------------------------------------------------------------
// Significance: is the difference between A and B bigger than the simulation's own randomness?
// ---------------------------------------------------------------------------------------------
type Verdict = "clear" | "likely" | "noise" | "n/a";
interface Significance { verdict: Verdict; t: number; range: number; n: number; nEff: number; paired: boolean; }

function meanSd(xs: number[]): { mean: number; sd: number } {
    const n = xs.length;
    const mean = xs.reduce((a, b) => a + b, 0) / Math.max(1, n);
    const v = n > 1 ? xs.reduce((a, x) => a + (x - mean) * (x - mean), 0) / (n - 1) : 0;
    return { mean, sd: Math.sqrt(v) };
}

/**
 * Effective sample size for a series of consecutive game hours: neighbouring hours are related (a jam at 08:00 is
 * still there at 09:00), so they count as fewer independent observations. Uses the lag-1 autocorrelation.
 */
function effectiveN(xs: number[]): number {
    const n = xs.length;
    if (n < 4) return n;
    const { mean } = meanSd(xs);
    let num = 0;
    let den = 0;
    for (let i = 0; i < n; i++) {
        den += (xs[i] - mean) * (xs[i] - mean);
        if (i > 0) num += (xs[i] - mean) * (xs[i - 1] - mean);
    }
    const r = den > 0 ? Math.max(0, Math.min(0.95, num / den)) : 0;
    return Math.max(2, Math.min(n, (n * (1 - r)) / (1 + r)));
}

function verdictOf(t: number, nEff: number): Verdict {
    if (nEff < 3) return "n/a";
    const at = Math.abs(t);
    return at >= 3 ? "clear" : at >= 2 ? "likely" : "noise";
}

/** Values of one metric for runs A and B, in the same way the comparison scope pairs them. */
function seriesFor(a: Capture[], b: Capture[], scope: Scope, m: Metric): { xs: number[]; ys: number[]; paired: boolean; consecutive: boolean } {
    const xs: number[] = [];
    const ys: number[] = [];
    if (scope === "Paired") {
        const ma = byTime(a);
        const mb = byTime(b);
        Array.from(ma.keys()).sort().forEach(t => {
            const ca = ma.get(t)!;
            const cb = mb.get(t);
            if (!cb) return;
            const va = m.value(ca);
            const vb = m.value(cb);
            if (va !== null && vb !== null) { xs.push(va); ys.push(vb); }
        });
        return { xs, ys, paired: true, consecutive: true };
    }
    if (scope === "SameHours") {
        const ha = byHourOfDay(a);
        const hb = byHourOfDay(b);
        for (let h = 0; h < 24; h++) {
            const va = ha[h] ? m.value(ha[h]!) : null;
            const vb = hb[h] ? m.value(hb[h]!) : null;
            if (va !== null && vb !== null) { xs.push(va); ys.push(vb); }
        }
        return { xs, ys, paired: true, consecutive: false };
    }
    for (const c of a) { const v = m.value(c); if (v !== null) xs.push(v); }
    for (const c of b) { const v = m.value(c); if (v !== null) ys.push(v); }
    return { xs, ys, paired: false, consecutive: true };
}

/** Paired t-test on the differences, or Welch's t-test for two independent series. */
function significance(a: Capture[], b: Capture[], scope: Scope, m: Metric): Significance {
    const { xs, ys, paired, consecutive } = seriesFor(a, b, scope, m);
    if (paired) {
        const d = xs.map((x, i) => ys[i] - x);
        const n = d.length;
        const nEff = consecutive ? effectiveN(d) : n;
        const { mean, sd } = meanSd(d);
        const se = sd / Math.sqrt(Math.max(1, nEff));
        const t = se > 0 ? mean / se : mean === 0 ? 0 : 99;
        return { verdict: n < 3 ? "n/a" : verdictOf(t, nEff), t, range: 2 * se, n, nEff, paired };
    }
    const A = meanSd(xs);
    const B = meanSd(ys);
    const na = consecutive ? effectiveN(xs) : xs.length;
    const nb = consecutive ? effectiveN(ys) : ys.length;
    const se = Math.sqrt((A.sd * A.sd) / Math.max(1, na) + (B.sd * B.sd) / Math.max(1, nb));
    const diff = B.mean - A.mean;
    const t = se > 0 ? diff / se : diff === 0 ? 0 : 99;
    const nEff = Math.min(na, nb);
    return { verdict: xs.length < 3 || ys.length < 3 ? "n/a" : verdictOf(t, nEff), t, range: 2 * se, n: Math.min(xs.length, ys.length), nEff, paired };
}

const VERDICT_TEXT: Record<Verdict, string> = { clear: "clear", likely: "likely", noise: "noise", "n/a": "" };

// ---------------------------------------------------------------------------------------------
// Small components
// ---------------------------------------------------------------------------------------------
const InfoRow = ({ label, value, hint }: { label: string; value: string; hint?: string }) => {
    const row = (
        <div className={styles.row}>
            <div className={styles.rowLabel}><div>{label}</div>{hint ? <div className={styles.hintMark}>?</div> : null}</div>
            <div className={styles.rowValue}>{value}</div>
        </div>
    );
    return hint ? <Tooltip tooltip={<div className={styles.tip}>{hint}</div>}>{row}</Tooltip> : row;
};

const Toggle = ({ label, selected, onClick, className }: { label: ReactNode; selected: boolean; onClick: () => void; className?: string }) => (
    <div className={`${styles.toggle} ${selected ? styles.toggleOn : ""} ${className ?? ""}`} onClick={onClick}>
        {label}
    </div>
);

const TipLine = ({ label, value }: { label: string; value: string }) => (
    <div className={styles.tipLine}><span>{label}</span><span>{value}</span></div>
);

/** Two-click button for deleting things. */
const DangerButton = ({ id, label, confirmLabel, pending, setPending, onConfirm }: {
    id: string; label: string; confirmLabel: string; pending: string; setPending: (id: string) => void; onConfirm: () => void;
}) => (
    <div className={pending === id ? `${styles.resetButton} ${styles.resetConfirm}` : styles.resetButton}
         onClick={() => {
             if (pending === id) { onConfirm(); setPending(""); }
             else setPending(id);
         }}>
        {pending === id ? confirmLabel : label}
    </div>
);

const RunPicker = ({ label, runs, selected, onSelect, color }: {
    label: string; runs: Run[]; selected: string; onSelect: (id: string) => void; color: string;
}) => (
    <div className={styles.picker}>
        <div className={`${styles.pickerLabel} ${color}`}>{label}</div>
        <div className={styles.pickerChips}>
            {runs.slice(-8).map(r => (
                <Tooltip key={r.id} tooltip={<div className={styles.tip}><div className={styles.tipTitle}>{runName(r, runs)}</div><div>{r.settings}</div><div>{runRange(r)}</div></div>}>
                    <div className={`${styles.toggle} ${selected === r.id ? styles.toggleOn : ""}`} onClick={() => onSelect(r.id)}>
                        {runName(r, runs)} <span className={styles.chipSub}>{r.first.substring(5, 10)}</span>
                    </div>
                </Tooltip>
            ))}
        </div>
    </div>
);

interface Slot { key: string; label: string; values: (number | null)[]; tip: ReactNode; detail: string; }

/** Bar chart with up to two series (run A, run B) per slot. */
const Chart = ({ slots, metric, hover, setHover, idle }: {
    slots: Slot[]; metric: Metric; hover: number; setHover: (i: number) => void; idle: string;
}) => {
    const all = slots.flatMap(s => s.values.filter((v): v is number => v !== null));
    if (all.length === 0) return <div className={styles.empty}>Nothing recorded for this yet.</div>;
    const maxAbs = Math.max(1e-6, ...all.map(v => Math.abs(v)));
    const better = metric.higherIsBetter ? "higher is better" : "lower is better";
    return (
        <>
            <div className={styles.detail}>{hover >= 0 && hover < slots.length ? slots[hover].detail : idle}</div>
            <div className={styles.chart} onMouseLeave={() => setHover(-1)}>
                {slots.map((s, i) => (
                    <Tooltip key={s.key} tooltip={s.tip}>
                        <div className={`${styles.slot} ${hover === i ? styles.barSlotHover : ""}`} onMouseEnter={() => setHover(i)}>
                            {s.values.map((v, k) => (
                                <div key={k} className={k === 0 ? styles.barA : styles.barB}
                                     style={{ height: v === null ? "0%" : `${Math.max(2, (Math.abs(v) / maxAbs) * 100)}%`, opacity: v !== null && v < 0 ? 0.45 : 1 }} />
                            ))}
                        </div>
                    </Tooltip>
                ))}
            </div>
            <div className={styles.barLabels}>
                {slots.map(s => <div className={styles.barLabel} key={s.key}>{s.label}</div>)}
            </div>
            <div className={styles.chartFoot}>
                <span>{metric.label}, max {fmt(maxAbs, metric.digits, metric.unit)}</span>
                <span>{better}{metric.key === "navGain" ? " (faded bar = navigation slower)" : ""}</span>
            </div>
        </>
    );
};

function modeLabel(mode?: string, preset?: string): string {
    if (mode === "Live") return `Live traffic (${preset ?? ""})`;
    if (mode === "Native") return "Native (mod off)";
    return "Waiting for data";
}

// ---------------------------------------------------------------------------------------------
// Panel
// ---------------------------------------------------------------------------------------------
type Tab = "Now" | "Compare" | "History" | "Hotspots" | "Rerouting" | "Runs";
type View = "Timeline" | "TimeOfDay";
const MAX_SLOTS = 48;

export const LiveTrafficPanel = () => {
    const enabled = useValue(enabled$);
    const [expanded, setExpanded] = useState(false);
    const [tab, setTab] = useState<Tab>("Now");
    const [scope, setScope] = useState<Scope>("Paired");
    const [view, setView] = useState<View>("TimeOfDay");
    const [rrView, setRrView] = useState<View>("TimeOfDay");
    const [metricKey, setMetricKey] = useState("speed");
    const [rrMetricKey, setRrMetricKey] = useState("rr");
    const [pickA, setPickA] = useState("");
    const [pickB, setPickB] = useState("");
    const [hover, setHover] = useState(-1);
    const [pending, setPending] = useState("");

    const current = parse<Current>(useValue(current$), {});
    const historyJson = useValue(history$);
    const runsJson = useValue(runs$);
    const hotspotsJson = useValue(hotspots$);
    const hotspots = useMemo(() => parse<Record<string, Hotspot[]>>(hotspotsJson, {}), [hotspotsJson]);
    const history = useMemo(() => parse<Capture[]>(historyJson, []), [historyJson]);
    const runs = useMemo(() => parse<Run[]>(runsJson, []), [runsJson]);

    // Default comparison: latest Native run vs latest Live run.
    const latest = (mode: string) => [...runs].reverse().find(r => r.mode === mode)?.id ?? "";
    const runA = runs.find(r => r.id === pickA) ?? runs.find(r => r.id === latest("Native")) ?? runs[0];
    const runB = runs.find(r => r.id === pickB) ?? runs.find(r => r.id === latest("Live") && r !== runA) ?? [...runs].reverse().find(r => r !== runA);
    const capsA = useMemo(() => history.filter(c => runA && c.run === runA.id), [history, runA]);
    const capsB = useMemo(() => history.filter(c => runB && c.run === runB.id), [history, runB]);

    if (!enabled) return null;

    const switchTab = (t: Tab) => { setTab(t); setHover(-1); setPending(""); };
    const nameA = runName(runA, runs);
    const nameB = runName(runB, runs);

    // ---------- Compare ----------
    const cmp = compare(capsA, capsB, scope);
    const compareRows: [string, Metric][] = [
        ...TRAFFIC_METRICS.map(m => [m.label, m] as [string, Metric]),
        ["Re-routes per hour", REROUTE_METRICS[0]],
        ["Re-routes that found faster", REROUTE_METRICS[1]],
    ];

    // ---------- Charts ----------
    const metric = TRAFFIC_METRICS.find(m => m.key === metricKey) ?? TRAFFIC_METRICS[0];
    const rrMetric = REROUTE_METRICS.find(m => m.key === rrMetricKey) ?? REROUTE_METRICS[0];

    const timelineSlots = (m: Metric, a: Capture[], b: Capture[]): Slot[] => {
        const ma = byTime(a);
        const mb = byTime(b);
        const times = Array.from(new Set([...ma.keys(), ...mb.keys()])).sort().slice(-MAX_SLOTS);
        return times.map(t => {
            const ca = ma.get(t);
            const cb = mb.get(t);
            const va = ca ? m.value(ca) : null;
            const vb = cb ? m.value(cb) : null;
            const h = (ca ?? cb)!.h;
            const d = fmtDiff(va, vb, m);
            return {
                key: t,
                label: h === 0 ? t.substring(5, 10) : h % 3 === 0 ? pad2(h) : "",
                values: [va, vb],
                detail: `${t} - ${m.label}: ${nameA} ${fmt(va, m.digits, m.unit)}, ${nameB} ${fmt(vb, m.digits, m.unit)}${d.text !== "-" ? `, difference ${d.text}` : ""}`,
                tip: (
                    <div className={styles.tip}>
                        <div className={styles.tipTitle}>{t} - {m.label}</div>
                        <TipLine label={nameA} value={fmt(va, m.digits, m.unit)} />
                        <TipLine label={nameB} value={fmt(vb, m.digits, m.unit)} />
                        {cb && cb.mode === "Live" && <TipLine label={`${nameB}: re-routes (faster)`} value={`${cb.rr} (${cb.rrBetter})`} />}
                    </div>
                ),
            };
        });
    };

    const hourSlots = (m: Metric, a: Capture[], b: Capture[]): Slot[] => {
        const ha = byHourOfDay(a);
        const hb = byHourOfDay(b);
        return Array.from({ length: 24 }, (_, h) => {
            const va = ha[h] ? m.value(ha[h]!) : null;
            const vb = hb[h] ? m.value(hb[h]!) : null;
            const d = fmtDiff(va, vb, m);
            return {
                key: String(h),
                label: h % 3 === 0 ? pad2(h) : "",
                values: [va, vb],
                detail: `${pad2(h)}:00 - ${m.label}: ${nameA} ${fmt(va, m.digits, m.unit)} (${ha[h]?.hours ?? 0} h), ${nameB} ${fmt(vb, m.digits, m.unit)} (${hb[h]?.hours ?? 0} h)${d.text !== "-" ? `, difference ${d.text}` : ""}`,
                tip: (
                    <div className={styles.tip}>
                        <div className={styles.tipTitle}>{pad2(h)}:00 to {pad2((h + 1) % 24)}:00 - {m.label}</div>
                        <TipLine label={`${nameA} (${ha[h]?.hours ?? 0} h)`} value={fmt(va, m.digits, m.unit)} />
                        <TipLine label={`${nameB} (${hb[h]?.hours ?? 0} h)`} value={fmt(vb, m.digits, m.unit)} />
                    </div>
                ),
            };
        });
    };

    // Re-routing charts: the runs that are Live, A and/or B.
    const rrA = runA?.mode === "Live" ? capsA : [];
    const rrB = runB?.mode === "Live" ? capsB : [];
    const lastLive = [...history].reverse().find(c => c.mode === "Live");
    const rrTotals = rrB.length > 0 ? average(rrB) : rrA.length > 0 ? average(rrA) : null;
    const rrTotalsName = rrB.length > 0 ? nameB : nameA;

    const legend = (
        <div className={styles.legend}>
            <span className={styles.legendA}>{nameA}</span>
            <span className={styles.legendB}>{nameB}</span>
        </div>
    );

    const pickers = (
        <>
            <RunPicker label="A" runs={runs} selected={runA?.id ?? ""} onSelect={id => { setPickA(id); setHover(-1); }} color={styles.colorA} />
            <RunPicker label="B" runs={runs} selected={runB?.id ?? ""} onSelect={id => { setPickB(id); setHover(-1); }} color={styles.colorB} />
        </>
    );

    return (
        <div className={styles.root}>
            <FloatingButton src={ICON} selected={expanded}
                            tooltipLabel={expanded ? "Hide Live Traffic statistics" : "Live Traffic statistics"}
                            onSelect={() => setExpanded(!expanded)} />
            {expanded && (
                <Panel header="Live Traffic" className={styles.panel} onClose={() => setExpanded(false)}>
                    <div className={styles.body}>
                        <div className={styles.tabs}>
                            {(["Now", "Compare", "History", "Hotspots", "Rerouting", "Runs"] as Tab[]).map(t => (
                                <div key={t} className={tab === t ? `${styles.tab} ${styles.tabOn}` : styles.tab} onClick={() => switchTab(t)}>
                                    {t === "Rerouting" ? "Re-routing" : t}
                                </div>
                            ))}
                        </div>

                        {tab === "Now" && (<>
                            <div className={styles.sectionTitle}>
                                Right now <span className={current.mode === "Live" ? styles.badgeLive : styles.badgeNative}>{modeLabel(current.mode, current.preset)}</span>
                            </div>
                            {current.settings && <div className={styles.note}>{current.settings}</div>}
                            <InfoRow label="Vehicles on the road" value={fmt(current.veh)} />
                            <InfoRow label="Real speed vs limit" value={fmt(current.speed, 0, "%")} hint="Average real driving speed of all road vehicles compared with the speed limit, including vehicles waiting at lights and in queues." />
                            <InfoRow label="Waiting in queues" value={fmt(current.queued, 1, "%")} hint="Vehicles stopped behind another stopped vehicle." />
                            <InfoRow label="Jam warnings on the map" value={fmt(current.jams)} />
                            <InfoRow label="On local streets" value={fmt(current.local, 0, "%")} hint="Vehicles on roads with a limit of 50 km/h or less." />
                            <InfoRow label={`With navigation (${fmt(current.navShare, 0, "%")}) vs others`}
                                     value={current.navShare !== undefined && current.navShare >= 100 ? "no comparison: everyone has navigation"
                                         : current.navShare !== undefined && current.navShare <= 0 ? "no comparison: nobody has navigation"
                                         : `${fmt(current.navKmh, 1)} vs ${fmt(current.otherKmh, 1)} km/h`}
                                     hint="How fast vehicles get closer to their destination (straight line), this game hour. The share is 'Drivers who re-route while driving'. With the mod off both groups should be about equal." />
                            <InfoRow label="Mod processing time"
                                     value={current.mode === "Native" ? "mod is off (only statistics run)"
                                         : current.ms !== undefined && current.ms >= 0 ? `${fmt(current.ms, 2)} ms per run` : "measured once per game hour"} />
                        </>)}

                        {tab === "Compare" && (<>
                            {runs.length < 2 ? (
                                <div className={styles.empty}>Record at least two runs, for example one with the mod Off and one Live, from the same save. A new run starts when you load a save or change the mod's settings.</div>
                            ) : (<>
                                {pickers}
                                <div className={styles.toggles}>
                                    <Toggle label="Same game time" selected={scope === "Paired"} onClick={() => setScope("Paired")} />
                                    <Toggle label="Same times of day" selected={scope === "SameHours"} onClick={() => setScope("SameHours")} />
                                    <Toggle label="All hours" selected={scope === "AllHours"} onClick={() => setScope("AllHours")} />
                                </div>
                                <div className={styles.note}>{cmp.note}</div>
                                <div className={styles.table}>
                                    <div className={styles.tableRow}>
                                        <div className={styles.tableLabel}></div>
                                        <div className={`${styles.tableHead} ${styles.colorA}`}>{nameA}</div>
                                        <div className={`${styles.tableHead} ${styles.colorB}`}>{nameB}</div>
                                        <div className={styles.tableHeadWide}>B vs A</div>
                                        <div className={styles.tableHeadSig}>Real?</div>
                                    </div>
                                    <div className={styles.tableRow}>
                                        <div className={styles.tableLabel}>Game hours</div>
                                        <div className={styles.tableCell}>{fmt(cmp.a.hours)}</div>
                                        <div className={styles.tableCell}>{fmt(cmp.b.hours)}</div>
                                        <div className={styles.tableCellWide}></div>
                                        <div className={styles.tableCellSig}></div>
                                    </div>
                                    {compareRows.map(([label, m]) => {
                                        const va = cmp.a.hours > 0 ? m.value(cmp.a) : null;
                                        const vb = cmp.b.hours > 0 ? m.value(cmp.b) : null;
                                        const d = fmtDiff(va, vb, m);
                                        const sig = significance(capsA, capsB, scope, m);
                                        const sigTip = (
                                            <div className={styles.tip}>
                                                <div className={styles.tipTitle}>{label}: {d.text} {sig.verdict !== "n/a" ? `(${VERDICT_TEXT[sig.verdict]})` : ""}</div>
                                                {sig.verdict === "n/a" ? <div>Not enough game hours to tell yet.</div> : (<>
                                                    <div>95% range of the difference: {d.text !== "-" ? `${fmt((vb ?? 0) - (va ?? 0) - sig.range, m.digits)} to ${fmt((vb ?? 0) - (va ?? 0) + sig.range, m.digits)}${m.unit}` : "-"}</div>
                                                    <div>Based on {sig.n} {sig.paired ? "paired " : ""}hours{sig.nEff < sig.n - 0.5 ? `, counted as about ${sig.nEff.toFixed(0)} independent ones (neighbouring hours are related)` : ""}. t = {Math.abs(sig.t) >= 99 ? "very large" : sig.t.toFixed(1)}.</div>
                                                    <div>Clear: |t| 3 or more, very unlikely to be chance. Likely: |t| 2 to 3. Noise: smaller than the simulation's own randomness.</div>
                                                </>)}
                                            </div>
                                        );
                                        return (
                                            <div className={styles.tableRow} key={label}>
                                                <div className={styles.tableLabel}>{label}</div>
                                                <div className={styles.tableCell}>{fmt(va, m.digits, m.unit)}</div>
                                                <div className={styles.tableCell}>{fmt(vb, m.digits, m.unit)}</div>
                                                <div className={`${styles.tableCellWide} ${sig.verdict === "noise" ? "" : d.cls}`}>{d.text}</div>
                                                <Tooltip tooltip={sigTip}>
                                                    <div className={styles.tableCellSig}>
                                                        {sig.verdict !== "n/a" && <span className={styles[`sig_${sig.verdict}`]}>{VERDICT_TEXT[sig.verdict]}</span>}
                                                    </div>
                                                </Tooltip>
                                            </div>
                                        );
                                    })}
                                </div>
                                <div className={styles.note}>Real?: is the difference bigger than the simulation's own randomness? Hover it for the details. Differences marked noise are not coloured.</div>
                                <div className={styles.note}>A: {runA?.settings}<br />B: {runB?.settings}</div>
                            </>)}
                        </>)}

                        {tab === "History" && (<>
                            {runs.length === 0 ? <div className={styles.empty}>No game hours recorded yet.</div> : (<>
                                {pickers}
                                <div className={styles.toggles}>
                                    <Toggle label="By time of day" selected={view === "TimeOfDay"} onClick={() => { setView("TimeOfDay"); setHover(-1); }} />
                                    <Toggle label="Timeline" selected={view === "Timeline"} onClick={() => { setView("Timeline"); setHover(-1); }} />
                                </div>
                                <div className={styles.toggles}>
                                    {TRAFFIC_METRICS.map(m => <Toggle key={m.key} label={m.label} selected={metricKey === m.key} onClick={() => setMetricKey(m.key)} />)}
                                </div>
                                <Chart metric={metric} hover={hover} setHover={setHover}
                                       slots={view === "Timeline" ? timelineSlots(metric, capsA, capsB) : hourSlots(metric, capsA, capsB)}
                                       idle={view === "Timeline" ? "Game hours of both runs side by side. Hover a bar for details." : "Average per hour of the day. Hover an hour to compare."} />
                                {legend}
                            </>)}
                        </>)}

                        {tab === "Hotspots" && (<>
                            {runs.length === 0 ? <div className={styles.empty}>No game hours recorded yet.</div> : (<>
                                {pickers}
                                {(() => {
                                    const ha = (runA && hotspots[runA.id]) || [];
                                    const hb = (runB && hotspots[runB.id]) || [];
                                    const places = new Map<string, { k: string; a?: Hotspot; b?: Hotspot }>();
                                    for (const x of ha) places.set(x.p, { k: x.k, a: x });
                                    for (const x of hb) { const e = places.get(x.p); if (e) e.b = x; else places.set(x.p, { k: x.k, b: x }); }
                                    const list = Array.from(places.entries())
                                        .sort((x, y) => Math.max(y[1].a?.q ?? 0, y[1].b?.q ?? 0) - Math.max(x[1].a?.q ?? 0, x[1].b?.q ?? 0))
                                        .slice(0, 14);
                                    const qm: Metric = { key: "q", label: "Queued", unit: "", digits: 1, higherIsBetter: false, value: () => 0 };
                                    if (list.length === 0) return <div className={styles.empty}>No queues recorded for these runs yet (hotspots are recorded from this version on).</div>;
                                    return (<>
                                        <div className={styles.note}>Streets and junctions with the most vehicles waiting in queues, on average per game hour of each run. Jam warnings in brackets.</div>
                                        <div className={styles.table}>
                                            <div className={styles.tableRow}>
                                                <div className={styles.tableLabel}>Place</div>
                                                <div className={`${styles.tableHead} ${styles.colorA}`}>{nameA}</div>
                                                <div className={`${styles.tableHead} ${styles.colorB}`}>{nameB}</div>
                                                <div className={styles.tableHeadWide}>B vs A</div>
                                            </div>
                                            {list.map(([place, e]) => {
                                                const va = e.a ? e.a.q : 0;
                                                const vb = e.b ? e.b.q : 0;
                                                const d = fmtDiff(va, vb, qm);
                                                const jam = (x?: Hotspot) => (x && x.j >= 0.05 ? ` (${fmt(x.j, 1)})` : "");
                                                return (
                                                    <div className={styles.tableRow} key={place}>
                                                        <div className={styles.tableLabel}>
                                                            <span className={styles.placeKind}>{e.k === "junction" ? "J" : "S"}</span> {place}
                                                        </div>
                                                        <div className={styles.tableCell}>{fmt(va, 1)}{jam(e.a)}</div>
                                                        <div className={styles.tableCell}>{fmt(vb, 1)}{jam(e.b)}</div>
                                                        <div className={`${styles.tableCellWide} ${d.cls}`}>{d.text}</div>
                                                    </div>
                                                );
                                            })}
                                        </div>
                                        <div className={styles.note}>S = street, J = junction. A place that wasn't among the busiest in an hour counts as 0 for that hour. Streets with the same name are counted together.</div>
                                    </>);
                                })()}
                            </>)}
                        </>)}

                        {tab === "Rerouting" && (<>
                            {runs.length > 0 && pickers}
                            {rrTotals ? (<>
                                <div className={styles.sectionTitle}>Run {rrTotalsName}: average per game hour</div>
                                <InfoRow label="Re-routes" value={`${fmt(rrTotals.rr)} (${fmt(rrTotals.rrTrucks)} by trucks)`} />
                                <InfoRow label="Found a faster route"
                                         value={rrTotals.rrBetter + rrTotals.rrNoBetter > 0 ? `${fmt((100 * rrTotals.rrBetter) / (rrTotals.rrBetter + rrTotals.rrNoBetter), 0, "%")} (avg ${fmt(rrTotals.rrSaved, 0, "%")} of the rest of the trip saved, estimate)` : "-"} />
                                <InfoRow label="Held back by the limit" value={fmt(rrTotals.rrHeld)} hint="Re-routes postponed because the per-update limit (64) was reached. Often high means the limit is holding the mod back." />
                                <div className={styles.toggles}>
                                    <Toggle label="By time of day" selected={rrView === "TimeOfDay"} onClick={() => { setRrView("TimeOfDay"); setHover(-1); }} />
                                    <Toggle label="Timeline" selected={rrView === "Timeline"} onClick={() => { setRrView("Timeline"); setHover(-1); }} />
                                </div>
                                <div className={styles.toggles}>
                                    {REROUTE_METRICS.map(m => <Toggle key={m.key} label={m.label} selected={rrMetricKey === m.key} onClick={() => setRrMetricKey(m.key)} />)}
                                </div>
                                <Chart metric={rrMetric} hover={hover} setHover={setHover}
                                       slots={rrView === "Timeline" ? timelineSlots(rrMetric, rrA, rrB) : hourSlots(rrMetric, rrA, rrB)}
                                       idle="Live runs only (a Native run has no re-routing). Hover for details." />
                                {legend}
                            </>) : (
                                <div className={styles.empty}>
                                    {lastLive ? "Pick a Live run as A or B to see its re-routing." : "No live game hour recorded yet."}
                                </div>
                            )}
                        </>)}

                        {tab === "Runs" && (<>
                            <div className={styles.note}>A run is one continuous recording with the same mode and settings. A new run starts when you load a save, reset, or change the mod's settings (the hour in progress is then not counted).</div>
                            {runs.length === 0 && <div className={styles.empty}>No runs recorded for this city yet.</div>}
                            <div className={styles.runList}>
                                {[...runs].reverse().slice(0, 12).map(r => (
                                    <div className={styles.runItem} key={r.id}>
                                        <div className={styles.runText}>
                                            <div className={styles.runTitle}>
                                                <span className={r.mode === "Live" ? styles.badgeLive : styles.badgeNative}>{runName(r, runs)}</span>
                                                <span className={styles.chipSub}>{runRange(r)}</span>
                                            </div>
                                            <div className={styles.runSettings}>{r.settings}</div>
                                        </div>
                                        <DangerButton id={"run:" + r.id} label="Delete" confirmLabel="Sure?" pending={pending} setPending={setPending}
                                                      onConfirm={() => trigger(GROUP, "deleteRun", r.id)} />
                                    </div>
                                ))}
                            </div>
                            <div className={styles.resetRow}>
                                <DangerButton id="live" label="Delete all Live runs (keep Native baseline)" confirmLabel="Click again to delete all Live runs"
                                              pending={pending} setPending={setPending} onConfirm={() => trigger(GROUP, "deleteLive")} />
                            </div>
                            <div className={styles.resetRow}>
                                <DangerButton id="all" label="Delete all statistics of this city" confirmLabel="Click again to delete everything for this city"
                                              pending={pending} setPending={setPending} onConfirm={() => trigger(GROUP, "deleteAll")} />
                                {pending !== "" && <div className={styles.toggle} onClick={() => setPending("")}>Cancel</div>}
                            </div>
                            <div className={styles.note}>Every delete keeps a backup of the statistics file in ModsData/LiveTrafficRouting (newest 5).</div>
                        </>)}
                    </div>
                </Panel>
            )}
        </div>
    );
};
