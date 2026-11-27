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
const roadworks$ = bindValue<string>(GROUP, "roadworks", '{"cleared":0,"roads":[]}');
const roadworksToolOn$ = bindValue<boolean>(GROUP, "roadworksToolOn", false);
const roadworksMode$ = bindValue<number>(GROUP, "roadworksMode", 1);
const modActive$ = bindValue<boolean>(GROUP, "modActive", true);
const bottlenecks$ = bindValue<string>(GROUP, "bottlenecks", '{"time":"","rows":[],"links":[]}');
const overlayShown$ = bindValue<string>(GROUP, "overlayShown", "");

const ICON = "Media/Game/Icons/Traffic.svg";

interface Current {
    mode?: string; preset?: string; settings?: string; run?: string; t?: string; veh?: number; speed?: number;
    queued?: number; local?: number; jams?: number; navKmh?: number; otherKmh?: number; navShare?: number; ms?: number;
    news?: string; auto?: string;
}
interface Capture {
    t: string; run: string; h: number; mode: string; preset: string; n: number; veh: number; speed: number; queued: number;
    local: number; jams: number; jamsMax: number; flow: number; navKmh: number; otherKmh: number; navShare: number;
    rr: number; rrCars: number; rrTrucks: number; rrTaxis: number; rrBetter: number; rrNoBetter: number;
    rrSaved: number; rrHeld: number; planSpeed: number; planJammed: number; ms: number;
    // Destinations (-1 = hour recorded before these were measured)
    cCom: number; cHome: number; cShop: number; cLeis: number; cOth: number;
    qCom: number; qHome: number; qShop: number; qLeis: number; qOth: number;
    sTrips: number; sPlaces: number; sTop5: number; sKm: number;
    lTrips: number; lPlaces: number; lTop5: number; lKm: number;
    // Performance (-1 = not measured)
    spd: number; simFps: number; fps: number; cpu: number; modMb: number; gameMb: number;
    // v1.1 (-1 = hour recorded before these were measured)
    planFc: number; planLocal: number; fcMaxVc: number; fcVeh: number;
    rrEmerg: number; rrDefer: number; rrBudget: number; carPax: number; transitPax: number; transit: number; auto: string;
    // v1.1 (-1 = not measured): vehicles the game removed (stuck, route failed, trip done), failed / lost re-routes
    dsStuck?: number; dsFailed?: number; rmDone?: number; rrFailed?: number; rrGone?: number;
    // people cycling, walking, waiting for a route; share of everyone on the move riding in cars
    bikePax?: number; walking?: number; waitTransit?: number; waitRoute?: number; carShare?: number;
}
interface MarkedRoad { id: string; name: string; mark: number; }
interface Roadworks { cleared: number; roads: MarkedRoad[]; }
// x/z: where the queue was (game metres, v1.1), to find the right one of several streets with the same name.
interface Hotspot { p: string; k: string; q: number; j: number; h: number; x?: number; z?: number; }
// Bottleneck finder (BottleneckSystem.cs): the last finished game hour.
// A street, district, building or junction with an entity id ("" = not on the map, e.g. outside the city) and a share.
interface Area { n: string; id: string; p?: number; }
interface BottleneckRow {
    id: string; place: string; kind: string; cause: string; mix: string; queued: number; wait?: number; spill: number; through: number;
    to: string; from: string; moves?: string; lanes?: string; flow?: string; advice?: string;
    hoursIn?: number; avgWait?: number; endsAt?: string; endsAtId?: string; toList?: Area[]; fromList?: Area[]; queuesOn?: Area[]; despawns?: number; trucks?: number;
    // ranking: "cleared" (no queue for a few game hours: fixed?), "better", "worse" or ""; hours since last seen; recent stuck/h
    status?: string; since?: number; recent?: number;
}
// type: "link" = a new road could join the two points; "uturn" = drivers can't turn around and loop back;
// "turn" = the two points are already joined nearby, so a turn is missing at junction "at".
interface LinkHint {
    a: string; b: string; aId: string; bId: string; key?: string; gap: number; saving: number; routes: number; same?: number;
    type?: string; at?: string; ramp?: number; from?: string; to?: string; via?: string;
    hoursIn?: number; total?: number; streets?: Area[];
    // the listed bottleneck whose queued drivers this would help (row id and name)
    forId?: string; forName?: string;
}

/** One line per detour suggestion, in words that say what to fix. */
function linkText(l: LinkHint): string {
    if (l.type === "uturn") return `Can't turn around at ${l.at || l.a} (${l.routes} drivers per hour).`;
    if (l.type === "turn") return `Missing turn at ${l.at || l.a} (${l.routes} drivers per hour).`;
    // Both ends on the same street: drivers drive round a bend or loop of it.
    if (l.a === l.b) return `Possible shortcut across a bend of ${l.a} (${l.routes} drivers per hour).`;
    return `Possible new road: ${l.a} to ${l.b}${l.ramp ? " (needs a ramp)" : ""} (${l.routes} drivers per hour).`;
}

/** The longer explanation, with street names, for the detail box. */
function linkStory(l: LinkHint): string {
    const via = l.via ? ` via ${l.via}` : "";
    if (l.type === "uturn") return `Drivers on ${l.from || l.a} want to go back the other way, but can't turn around at ${l.at || l.a}. They drive ${fmt(l.saving, 0)} m round${via} instead.`;
    if (l.type === "turn") return `Drivers on ${l.from || l.a} want to reach ${l.to || l.b} (${fmt(l.gap, 0)} m away), but can't make the turn at ${l.at || l.a}. They drive ${fmt(l.saving, 0)} m round${via} instead.`;
    if (l.a === l.b) return `Drivers on ${l.a} follow a long bend or loop of it: ${fmt(l.saving, 0)} m round${via}, while its two ends are only about ${fmt(l.gap, 0)} m apart. A short road across would save that.`;
    return `Drivers from ${l.from || l.a} to ${l.to || l.b} drive ${fmt(l.saving, 0)} m round${via}. A new road of about ${fmt(l.gap, 0)} m between ${l.a} and ${l.b} would save that${l.ramp ? " (it needs a ramp onto the highway)" : ""}.`;
}

function linkLegend(l: LinkHint): string {
    if (l.type === "uturn") return "Try: allow a U-turn there, or build a small roundabout.";
    if (l.type === "turn") return "Try: allow that turn at the junction (turn rules or lane connections in the Traffic mod), or change the junction.";
    return "Try: build the new road (with a ramp if it meets a highway). The mod can't see rivers, hills or buildings in the way: check the map first.";
}
interface Bottlenecks {
    time: string; samples?: number; quiet?: number; rows: BottleneckRow[]; links: LinkHint[];
    // Ranking over the last `window` game hours: places by stuck cars, fixes seen in at least `minHours` of them (-1 = not yet).
    window?: number; minHours?: number; ongoing?: BottleneckRow[]; ongoingLinks?: LinkHint[];
}

/** Map legend: a colour swatch per line, matching what is drawn. */
type LegendItem = { color: string; text: string; ring?: boolean; dashed?: boolean };
const PLACE_LEGEND: LegendItem[] = [
    { color: "rgb(242,51,38)", text: "queue (arrows: its direction)" },
    { color: "rgb(255,217,51)", text: "where it starts", ring: true },
    { color: "rgb(64,153,255)", text: "turn left" },
    { color: "rgb(242,242,242)", text: "straight" },
    { color: "rgb(191,102,255)", text: "turn right" },
    { color: "rgb(255,217,51)", text: "U-turn" },
    { color: "rgb(77,217,255)", text: "coming from" },
    { color: "rgb(255,115,217)", text: "going to" },
];
function linkLegendItems(l: LinkHint): LegendItem[] {
    const items: LegendItem[] = [{ color: "rgb(255,140,26)", text: "the way they drive now" }];
    if (l.type === "uturn") items.push({ color: "rgb(255,217,51)", text: "where they want to turn around", ring: true });
    if (l.type === "turn") {
        items.push({ color: "rgb(77,242,102)", text: "the way they want", dashed: true });
        items.push({ color: "rgb(255,217,51)", text: "junction that blocks it", ring: true });
    }
    if (!l.type || l.type === "link") items.push({ color: "rgb(77,242,102)", text: "new road", dashed: true });
    return items;
}
const Legend = ({ items }: { items: LegendItem[] }) => (
    <div className={styles.legendBox}>
        <div className={styles.legendRow}>
            <div className={styles.legendText}>{"Map:"}</div>
            {items.map(i => (
                <div key={i.text} className={styles.legendItem}>
                    <div className={i.ring ? styles.swatchRing : i.dashed ? styles.swatchDashed : styles.swatch}
                         style={i.ring || i.dashed ? { borderColor: i.color } : { backgroundColor: i.color }} />
                    <div className={styles.legendText}>{i.text}</div>
                </div>
            ))}
        </div>
    </div>
);

/** Clickable names: each one moves the camera there. Names off the map (outside the city) are plain. */
const Chips = ({ label, items }: { label: string; items: Area[] }) => (
    <div className={styles.chipRow}>
        <div className={styles.chipLabel}>{label}</div>
        {items.length === 0 ? <div className={styles.chipPlain}>{"unknown"}</div> : items.map(a => (
            a.id
                ? <div key={a.n} className={styles.chip} onClick={() => trigger(GROUP, "focusEntity", a.id)}>{a.p !== undefined && a.p >= 0 ? `${a.n} ${fmt(a.p, 0)}%` : a.n}</div>
                : <div key={a.n} className={styles.chipPlain}>{a.p !== undefined && a.p >= 0 ? `${a.n} ${fmt(a.p, 0)}%` : a.n}</div>
        ))}
    </div>
);
interface Run { id: string; mode: string; preset: string; settings: string; first: string; last: string; hours: number; pinned?: number; baseline?: number; }

/** Averages over a set of game hours. */
interface Avg {
    hours: number; speed: number; queued: number; jams: number; local: number; flow: number;
    veh: number; navShare: number;
    navKmh: number; otherKmh: number; rr: number; rrTrucks: number; rrBetter: number; rrNoBetter: number;
    rrSaved: number; rrHeld: number;
    // v1.1: averaged only over hours that have them; -1 when none does.
    transit: number; rrEmerg: number; rrDefer: number; rrBudget: number; planFc: number; planLocal: number;
    dsStuck: number; dsFailed: number; rrFailed: number; carPax: number; transitPax: number;
    bikePax: number; walking: number; waitRoute: number; carShare: number;
}
type Row = Avg | Capture;

const AVG_KEYS: (keyof Avg)[] = ["speed", "queued", "jams", "local", "flow", "veh", "navShare", "navKmh", "otherKmh", "rr", "rrTrucks", "rrBetter", "rrNoBetter", "rrSaved", "rrHeld"];
const OPTIONAL_KEYS: (keyof Avg)[] = ["transit", "rrEmerg", "rrDefer", "rrBudget", "planFc", "planLocal", "dsStuck", "dsFailed", "rrFailed", "carPax", "transitPax", "bikePax", "walking", "waitRoute", "carShare"];
const EMPTY: Avg = {
    hours: 0, speed: 0, queued: 0, jams: 0, local: 0, flow: 0, veh: 0, navShare: 0, navKmh: 0, otherKmh: 0, rr: 0, rrTrucks: 0, rrBetter: 0, rrNoBetter: 0, rrSaved: 0, rrHeld: 0,
    transit: -1, rrEmerg: -1, rrDefer: -1, rrBudget: -1, planFc: -1, planLocal: -1,
    dsStuck: -1, dsFailed: -1, rrFailed: -1, carPax: -1, transitPax: -1,
    bikePax: -1, walking: -1, waitRoute: -1, carShare: -1,
};

function average(list: Row[]): Avg {
    const r: Avg = { ...EMPTY };
    for (const c of list) {
        const w = "hours" in c ? c.hours : 1;
        r.hours += w;
        for (const k of AVG_KEYS) (r[k] as number) += (c as any)[k] ?? 0;
    }
    const n = Math.max(1, list.length);
    for (const k of AVG_KEYS) (r[k] as number) /= n;
    for (const k of OPTIONAL_KEYS) {
        const v = list.map(c => (c as any)[k]).filter((x): x is number => typeof x === "number" && x >= 0);
        (r[k] as number) = v.length > 0 ? v.reduce((a, b) => a + b, 0) / v.length : -1;
    }
    return r;
}

const opt = (v: number) => (v >= 0 ? v : null);

/** "name|x|z" at the bigger of the two queues when a position was recorded, else just the name. */
function focusKey(place: string, a?: Hotspot, b?: Hotspot): string {
    const withPos = [a, b].filter((h): h is Hotspot => !!h && h.x !== undefined && h.z !== undefined).sort((p, q) => q.q - p.q);
    return withPos.length > 0 ? `${place}|${withPos[0].x}|${withPos[0].z}` : place;
}

/** Progress towards destinations of all cars, trucks and taxis: the two navigation groups weighted by their share. */
function tripSpeed(r: Row): number | null {
    const share = Math.min(100, Math.max(0, r.navShare ?? 0)) / 100;
    if (r.navKmh > 0 && r.otherKmh > 0) return r.navKmh * share + r.otherKmh * (1 - share);
    if (r.navKmh > 0 && share > 0.99) return r.navKmh;
    if (r.otherKmh > 0 && share < 0.01) return r.otherKmh;
    return null;
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
    // A side effect to watch, not a goal: differences are not coloured good or bad.
    neutral?: boolean;
    note?: string;
    value: (r: Row) => number | null;
}

const TRAFFIC_METRICS: Metric[] = [
    { key: "speed", label: "Real speed", unit: "%", digits: 1, higherIsBetter: true, value: r => r.speed },
    {
        key: "tripSpeed", label: "Trip speed", unit: " km/h", digits: 1, higherIsBetter: true, value: tripSpeed,
        note: "How fast cars, trucks and taxis get closer to their destination (straight-line distance per hour of driving). The most direct measure of trips getting faster.",
    },
    { key: "queued", label: "Queued", unit: "%", digits: 1, higherIsBetter: false, value: r => r.queued },
    { key: "jams", label: "Jam warnings", unit: "", digits: 1, higherIsBetter: false, value: r => r.jams },
    { key: "flow", label: "Traffic flow", unit: "%", digits: 0, higherIsBetter: true, value: r => r.flow },
    {
        key: "local", label: "Local streets", unit: "%", digits: 1, higherIsBetter: false, neutral: true,
        note: "Share of cars on local streets (50 km/h or less). A side effect to watch, not a goal: some traffic through neighbourhoods is fine when queues drop more. Spreading over other main roads does not count here.",
        value: r => r.local,
    },
    {
        key: "veh", label: "Vehicles on roads", unit: "", digits: 0, higherIsBetter: false, neutral: true, value: r => r.veh,
        note: "Road vehicles driving at the same moment. It drops when trips get faster, and also when people leave the car (the route planner also picks how people travel): check Car share of travellers and People walking before reading a drop as better traffic.",
    },
    {
        key: "navGain", label: "Nav advantage", unit: " km/h", digits: 1, higherIsBetter: true,
        // Only meaningful when both groups exist (navigation share between 0% and 100%).
        value: r => (r.otherKmh > 0 && r.navKmh > 0 ? r.navKmh - r.otherKmh : null),
    },
    {
        key: "transit", label: "Public transport share", unit: "%", digits: 1, higherIsBetter: true, neutral: true, value: r => opt(r.transit),
        note: "Share of people riding a car or public transport right now who ride public transport. It also rises when fewer people sit in cars, so look at the counts below: only a rise in people on public transport means people switched to it.",
    },
    {
        key: "carShare", label: "Car share of travellers", unit: "%", digits: 1, higherIsBetter: false, neutral: true, value: r => opt(r.carShare ?? -1),
        note: "Out of everyone on the move right now (car, public transport, bicycle, on foot), the share riding in cars and taxis. A big drop means the setting makes people leave the car, not only that traffic flows better.",
    },
    {
        key: "carPax", label: "People in cars", unit: "", digits: 0, higherIsBetter: false, neutral: true, value: r => opt(r.carPax),
        note: "People riding in cars and taxis at the same moment (not cyclists). Somewhat fewer when car trips get faster; much fewer means people chose another way to travel.",
    },
    {
        key: "transitPax", label: "People on public transport", unit: "", digits: 0, higherIsBetter: true, neutral: true, value: r => opt(r.transitPax),
        note: "People riding public transport at the same moment. A rise here (not only in the share) means people switched to public transport.",
    },
    {
        key: "walking", label: "People walking", unit: "", digits: 0, higherIsBetter: false, neutral: true, value: r => opt(r.walking ?? -1),
        note: "People walking outside at the same moment (to a stop, a parked car, or the whole way). A rise with fewer people in cars means people walk instead of driving.",
    },
    { key: "bikePax", label: "People cycling", unit: "", digits: 0, higherIsBetter: true, neutral: true, value: r => opt(r.bikePax ?? -1) },
    {
        key: "waitRoute", label: "Waiting for a route", unit: "", digits: 0, higherIsBetter: false, value: r => opt(r.waitRoute ?? -1),
        note: "People waiting for the game's route planner before they can set off. A rise means the route planner is busy (re-routes share it).",
    },
    { key: "dsStuck", label: "Despawned: stuck", unit: "", digits: 1, higherIsBetter: false, value: r => opt(r.dsStuck ?? -1) },
    { key: "dsFailed", label: "Despawned: no route", unit: "", digits: 1, higherIsBetter: false, value: r => opt(r.dsFailed ?? -1) },
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
    { key: "emerg", label: "Emergency", unit: "", digits: 0, higherIsBetter: true, value: r => opt(r.rrEmerg) },
    { key: "defer", label: "Waited for junction", unit: "", digits: 0, higherIsBetter: false, value: r => opt(r.rrDefer) },
    { key: "budget", label: "Budget", unit: "", digits: 0, higherIsBetter: true, value: r => opt(r.rrBudget) },
    { key: "rrFailed", label: "Failed re-routes", unit: "", digits: 1, higherIsBetter: false, value: r => opt(r.rrFailed ?? -1) },
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
    const cls = m.neutral ? "" : good ? styles.good : styles.bad;
    return { text: `${d > 0 ? "+" : ""}${d.toFixed(m.digits)}${m.unit}${rel}`, cls };
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
    return significanceOf(seriesFor(a, b, scope, m));
}

function significanceOf(series: { xs: number[]; ys: number[]; paired: boolean; consecutive: boolean }): Significance {
    const { xs, ys, paired, consecutive } = series;
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
const SLOW_TEXT = "Can't tell yet: this number changes slowly from hour to hour, so its game hours are not independent enough to test. A longer run helps.";

/** Real? badge. "?" when there are game hours but too few independent ones to test (slowly changing numbers). */
const SigBadge = ({ sig }: { sig: Significance }) =>
    sig.verdict !== "n/a" ? <span className={styles[`sig_${sig.verdict}`]}>{VERDICT_TEXT[sig.verdict]}</span>
        : sig.n >= 3 ? <span className={styles.sig_noise}>{"?"}</span> : null;

// ---------------------------------------------------------------------------------------------
// Small components
// ---------------------------------------------------------------------------------------------
/** tone colours the value: a key signal (good / bad / worth a look) shouldn't read like any other number. */
type Tone = "good" | "bad" | "warn" | undefined;
const toneClass = (t: Tone) => (t === "good" ? styles.good : t === "bad" ? styles.bad : t === "warn" ? styles.warnText : "");
const InfoRow = ({ label, value, hint, tone }: { label: string; value: string; hint?: string; tone?: Tone }) => {
    const row = (
        <div className={styles.row}>
            <div className={styles.rowLabel}><div>{label}</div>{hint ? <div className={styles.hintMark}>?</div> : null}</div>
            <div className={`${styles.rowValue} ${toneClass(tone)}`}>{value}</div>
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
    const better = metric.neutral ? "side effect: watch it" : metric.higherIsBetter ? "higher is better" : "lower is better";
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
                <span>{`${metric.label}, max ${fmt(maxAbs, metric.digits, metric.unit)}`}</span>
                <span>{`${better}${metric.key === "navGain" ? " (faded bar = navigation slower)" : ""}`}</span>
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
type Tab = "Now" | "Compare" | "History" | "Hotspots" | "Bottlenecks" | "Destinations" | "Rerouting" | "Cost" | "Roadworks" | "Runs";
const TAB_LABEL: Record<Tab, string> = {
    Now: "Now", Compare: "Compare", History: "History", Hotspots: "Hotspots", Bottlenecks: "Causes", Destinations: "Trips",
    Rerouting: "Re-route", Cost: "Cost", Roadworks: "Roadworks", Runs: "Runs",
};

// Temporary roadworks: marks (RoadMarkSystem.cs). Never saved; every text says so.
const TEMPORARY = "Temporary: marks are not saved and are cleared when you load a save or quit.";
const MARKS: { mark: number; label: string; tip: string }[] = [
    { mark: 1, label: "Close road", tip: "Drivers plan around the road as if it were closed (the strongest the route planner allows). It is not a barrier: vehicles already on it drive on, and drivers with live navigation leave it at the next junction." },
    { mark: 2, label: "Avoid", tip: "The road looks at least 3 times as slow to drivers, so they use it only when there is no reasonable other way." },
    { mark: 3, label: "Prefer", tip: "Drivers ignore half of the road's congestion penalty, so they keep using it longer when it gets busy. It can't make a road look faster than an empty one." },
];
const markLabel = (m: number) => MARKS.find(x => x.mark === m)?.label ?? "-";

/** Comparison as a markdown table, for sharing (Reddit, GitHub issues). */
function summaryText(nameA: string, nameB: string, runA: Run | undefined, runB: Run | undefined, note: string,
                     rows: { label: string; a: string; b: string; diff: string; sig: string }[], hoursA: number, hoursB: number): string {
    const lines = [
        "**Live Traffic Routing: comparison**",
        "",
        `| | A: ${nameA} | B: ${nameB} | B vs A | Real? |`,
        "|---|---|---|---|---|",
        `| Game hours | ${hoursA.toFixed(0)} | ${hoursB.toFixed(0)} | | |`,
        ...rows.map(r => `| ${r.label} | ${r.a} | ${r.b} | ${r.diff} | ${r.sig} |`),
        "",
        note,
        `A: ${runA?.settings ?? "-"}`,
        `B: ${runB?.settings ?? "-"}`,
    ];
    return lines.join("\n");
}



/** A destination metric: value of one game hour, or null when that hour has no such data. */
interface DestMetric { label: string; unit: string; digits: number; higherIsBetter: boolean; get: (c: Capture) => number | null; }
const field = (k: keyof Capture) => (c: Capture) => (typeof c[k] === "number" && (c[k] as number) >= 0 ? (c[k] as number) : null);
const QUEUED_PURPOSE: DestMetric[] = [
    { label: "Commuting", unit: "%", digits: 0, higherIsBetter: false, get: field("qCom") },
    { label: "Going home", unit: "%", digits: 0, higherIsBetter: false, get: field("qHome") },
    { label: "Shopping", unit: "%", digits: 0, higherIsBetter: false, get: field("qShop") },
    { label: "Leisure", unit: "%", digits: 0, higherIsBetter: false, get: field("qLeis") },
    { label: "Other", unit: "%", digits: 0, higherIsBetter: false, get: field("qOth") },
];
const SHOP_METRICS: DestMetric[] = [
    { label: "Trips per hour", unit: "", digits: 0, higherIsBetter: true, get: field("sTrips") },
    { label: "Different shops used", unit: "", digits: 0, higherIsBetter: true, get: field("sPlaces") },
    { label: "Trips to the 5 busiest", unit: "%", digits: 0, higherIsBetter: false, get: field("sTop5") },
    { label: "Distance from home", unit: " km", digits: 2, higherIsBetter: false, get: field("sKm") },
];
const LEISURE_METRICS: DestMetric[] = [
    { label: "Trips per hour", unit: "", digits: 0, higherIsBetter: true, get: field("lTrips") },
    { label: "Different places used", unit: "", digits: 0, higherIsBetter: true, get: field("lPlaces") },
    { label: "Trips to the 5 busiest", unit: "%", digits: 0, higherIsBetter: false, get: field("lTop5") },
    { label: "Distance from home", unit: " km", digits: 2, higherIsBetter: false, get: field("lKm") },
];

/** Simulation speed as game minutes per real second (a game hour is 262144 / 24 frames). */
const GAME_FRAMES_PER_MINUTE = 262144 / 24 / 60;
const COST_METRICS: DestMetric[] = [
    { label: "Simulation speed (game min / s)", unit: "", digits: 1, higherIsBetter: true,
      get: c => (typeof c.simFps === "number" && c.simFps > 0 ? c.simFps / GAME_FRAMES_PER_MINUTE : null) },
    { label: "Frames per second", unit: "", digits: 0, higherIsBetter: true, get: field("fps") },
    { label: "Mod CPU (% of one core)", unit: "%", digits: 2, higherIsBetter: false, get: field("cpu") },
    { label: "Mod memory", unit: " MB", digits: 1, higherIsBetter: false, get: field("modMb") },
    { label: "Game memory", unit: " MB", digits: 0, higherIsBetter: false, get: field("gameMb") },
    { label: "Re-routes per hour", unit: "", digits: 0, higherIsBetter: true, get: c => (c.mode === "Live" ? c.rr : null) },
];

/** Like seriesFor, for destination metrics (which skip hours without data). */
function destSeries(a: Capture[], b: Capture[], scope: Scope, m: DestMetric) {
    const xs: number[] = [];
    const ys: number[] = [];
    if (scope === "Paired") {
        const ma = byTime(a);
        const mb = byTime(b);
        Array.from(ma.keys()).sort().forEach(t => {
            const cb = mb.get(t);
            if (!cb) return;
            const va = m.get(ma.get(t)!);
            const vb = m.get(cb);
            if (va !== null && vb !== null) { xs.push(va); ys.push(vb); }
        });
        return { xs, ys, paired: true, consecutive: true };
    }
    if (scope === "SameHours") {
        const hourMean = (list: Capture[], h: number) => {
            const v = list.filter(c => c.h === h).map(m.get).filter((x): x is number => x !== null);
            return v.length > 0 ? v.reduce((p, q) => p + q, 0) / v.length : null;
        };
        for (let h = 0; h < 24; h++) {
            const va = hourMean(a, h);
            const vb = hourMean(b, h);
            if (va !== null && vb !== null) { xs.push(va); ys.push(vb); }
        }
        return { xs, ys, paired: true, consecutive: false };
    }
    for (const c of a) { const v = m.get(c); if (v !== null) xs.push(v); }
    for (const c of b) { const v = m.get(c); if (v !== null) ys.push(v); }
    return { xs, ys, paired: false, consecutive: true };
}
type View = "Timeline" | "TimeOfDay";
const WINDOW_SIZES = [24, 48, 96];

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
    // Timeline window: how many game hours, and where it ends (index into all game hours; -1 = follow run B).
    const [tlSize, setTlSize] = useState(48);
    const [tlEnd, setTlEnd] = useState(-1);
    const [pending, setPending] = useState("");
    const [copied, setCopied] = useState(false);
    // Causes tab: problems that keep coming back (default) or just the last game hour.
    const [causesView, setCausesView] = useState<"ongoing" | "hour">("ongoing");
    const roadworksJson = useValue(roadworks$);
    const bottlenecksJson = useValue(bottlenecks$);
    const bottlenecks = useMemo(() => parse<Bottlenecks>(bottlenecksJson, { time: "", rows: [], links: [] }), [bottlenecksJson]);
    const overlayShown = useValue(overlayShown$);
    const roadworks = useMemo(() => parse<Roadworks>(roadworksJson, { cleared: 0, roads: [] }), [roadworksJson]);
    const toolOn = useValue(roadworksToolOn$);
    const toolMode = useValue(roadworksMode$);
    const modActive = useValue(modActive$);

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

    const switchTab = (t: Tab) => { setTab(t); setHover(-1); setPending(""); setTlEnd(-1); setCopied(false); };
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

    /** All game hours of both runs (sorted) and the visible window of them. */
    const timelineWindow = (a: Capture[], b: Capture[]) => {
        const all = Array.from(new Set([...a.map(c => c.t), ...b.map(c => c.t)])).sort();
        // By default the window ends at run B's newest hour (a run in progress stays in view), else A's.
        const lastOf = (list: Capture[]) => list.reduce((x, c) => (c.t > x ? c.t : x), "");
        const anchor = lastOf(b) || lastOf(a);
        const autoEnd = Math.max(0, all.indexOf(anchor));
        const end = tlEnd < 0 ? autoEnd : Math.min(tlEnd, all.length - 1);
        const start = Math.max(0, end - tlSize + 1);
        return { all, start, end, times: all.slice(start, end + 1) };
    };

    /** Earlier / later / window size buttons under the timeline. */
    const windowControls = (a: Capture[], b: Capture[]) => {
        const w = timelineWindow(a, b);
        if (w.all.length === 0) return null;
        const step = Math.max(1, Math.floor(tlSize / 2));
        const go = (end: number) => { setTlEnd(Math.max(0, Math.min(w.all.length - 1, end))); setHover(-1); };
        return (
            <div className={styles.toggles}>
                <Toggle label="|<" selected={false} onClick={() => go(Math.min(w.all.length - 1, tlSize - 1))} />
                <Toggle label="< Earlier" selected={false} onClick={() => go(w.end - step)} />
                <Toggle label="Later >" selected={false} onClick={() => go(w.end + step)} />
                <Toggle label=">|" selected={false} onClick={() => go(w.all.length - 1)} />
                <Toggle label="Follow B" selected={tlEnd < 0} onClick={() => { setTlEnd(-1); setHover(-1); }} />
                <div className={styles.spacer} />
                {WINDOW_SIZES.map(n => (
                    <Toggle key={n} label={`${n} h`} selected={tlSize === n} onClick={() => { setTlSize(n); setHover(-1); }} />
                ))}
                <div className={styles.windowInfo}>{`${w.all[w.start]?.substring(5) ?? ""} to ${w.all[w.end]?.substring(5) ?? ""} (${w.start + 1}-${w.end + 1} of ${w.all.length} h)`}</div>
            </div>
        );
    };

    const timelineSlots = (m: Metric, a: Capture[], b: Capture[]): Slot[] => {
        const ma = byTime(a);
        const mb = byTime(b);
        const times = timelineWindow(a, b).times;
        return times.map(t => {
            const ca = ma.get(t);
            const cb = mb.get(t);
            const va = ca ? m.value(ca) : null;
            const vb = cb ? m.value(cb) : null;
            const h = (ca ?? cb)!.h;
            const d = fmtDiff(va, vb, m);
            return {
                key: t,
                label: h === 0 ? t.substring(5, 10) : h % (tlSize > 48 ? 6 : 3) === 0 ? pad2(h) : "",
                values: [va, vb],
                detail: `${t} - ${m.label}: ${nameA} ${fmt(va, m.digits, m.unit)}, ${nameB} ${fmt(vb, m.digits, m.unit)}${d.text !== "-" ? `, difference ${d.text}` : ""}`,
                tip: (
                    <div className={styles.tip}>
                        <div className={styles.tipTitle}>{`${t} - ${m.label}`}</div>
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
                        <div className={styles.tipTitle}>{`${pad2(h)}:00 to ${pad2((h + 1) % 24)}:00 - ${m.label}`}</div>
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
            <RunPicker label="A" runs={runs} selected={runA?.id ?? ""} onSelect={id => { setPickA(id); setHover(-1); setTlEnd(-1); }} color={styles.colorA} />
            <RunPicker label="B" runs={runs} selected={runB?.id ?? ""} onSelect={id => { setPickB(id); setHover(-1); setTlEnd(-1); }} color={styles.colorB} />
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
                            {(["Now", "Compare", "History", "Hotspots", "Bottlenecks", "Destinations", "Rerouting", "Cost", "Roadworks", "Runs"] as Tab[]).map(t => (
                                <div key={t} className={tab === t ? `${styles.tab} ${styles.tabOn}` : styles.tab} onClick={() => switchTab(t)}>
                                    {TAB_LABEL[t]}
                                </div>
                            ))}
                        </div>

                        {tab === "Now" && (<>
                            <div className={styles.sectionTitle}>
                                Right now <span className={current.mode === "Live" ? styles.badgeLive : styles.badgeNative}>{modeLabel(current.mode, current.preset)}</span>
                            </div>
                            {current.settings && <div className={styles.note}>{current.settings}</div>}
                            {current.auto ? <div className={styles.note}>{current.auto}</div> : null}
                            {current.news ? <div className={styles.news}>{current.news}</div> : null}
                            {roadworks.cleared ? (
                                <div className={styles.temporary} onClick={() => trigger(GROUP, "roadworksDismissNotice")}>
                                    {"Temporary roadworks were cleared (not saved). Click to hide."}
                                </div>
                            ) : roadworks.roads.length > 0 ? (
                                <div className={styles.temporary}>{`${roadworks.roads.length} temporary road marks active (not saved). See Roadworks.`}</div>
                            ) : null}
                            <InfoRow label="Vehicles on the road" value={fmt(current.veh)} />
                            <InfoRow label="Real speed vs limit" value={fmt(current.speed, 0, "%")} hint="Average real driving speed of all road vehicles compared with the speed limit, including vehicles waiting at lights and in queues." />
                            <InfoRow label="Waiting in queues" value={fmt(current.queued, 1, "%")} hint="Vehicles stopped behind another stopped vehicle." />
                            <InfoRow label="Jam warnings on the map" value={fmt(current.jams)} />
                            <InfoRow label="On local streets" value={fmt(current.local, 0, "%")} hint="Vehicles on roads with a limit of 50 km/h or less." />
                            <InfoRow label={`With navigation (${fmt(current.navShare, 0, "%")}) vs others`}
                                     value={current.navShare !== undefined && current.navShare >= 100 ? "no comparison: everyone has navigation"
                                         : current.navShare !== undefined && current.navShare <= 0 ? "no comparison: nobody has navigation"
                                         : `${fmt(current.navKmh, 1)} vs ${fmt(current.otherKmh, 1)} km/h`}
                                     tone={current.navShare !== undefined && current.navShare > 0 && current.navShare < 100 && current.navKmh !== undefined && current.otherKmh !== undefined
                                         ? (current.navKmh - current.otherKmh >= 0.5 ? "good" : current.otherKmh - current.navKmh >= 0.5 ? "bad" : undefined) : undefined}
                                     hint="How fast vehicles get closer to their destination (straight line), this game hour. The share is 'Drivers who re-route while driving'. With the mod off both groups should be about equal." />
                            <InfoRow label="Mod processing time"
                                     value={current.mode === "Native" ? "mod is off (only statistics run)"
                                         : current.ms !== undefined && current.ms >= 0 ? `${fmt(current.ms, 2)} ms per run` : "measured once per game hour"} />
                            {(() => {
                                const last = [...history].reverse().find(c => c.run === current.run && typeof c.cpu === "number" && c.cpu > 0);
                                if (!last) return <InfoRow label="Mod cost" value="after the first full game hour" />;
                                const sim = last.simFps > 0 ? `, simulation ${fmt(last.simFps / GAME_FRAMES_PER_MINUTE, 1)} game min/s at speed ${fmt(last.spd, 0)}` : "";
                                return <InfoRow label="Mod cost (last game hour)" value={`${fmt(last.cpu, 2)}% of one CPU core, ${fmt(last.modMb, 1)} MB${sim}`}
                                                hint="The mod's own work (traffic, re-routing, statistics) as a share of one CPU core, and the memory it keeps. Re-routing also makes the game calculate extra routes; the Cost tab compares simulation speed with the mod off to show the full effect." />;
                            })()}
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
                                                <div className={styles.tipTitle}>{`${label}: ${d.text}`}</div>
                                                {m.note ? <div>{m.note}</div> : null}
                                                {sig.verdict === "n/a" ? <div>{sig.n >= 3 ? SLOW_TEXT : "Not enough game hours to tell yet."}</div> : (<>
                                                    <TipLine label="Verdict" value={sig.verdict === "clear" ? "clear: very unlikely to be chance" : sig.verdict === "likely" ? "likely real" : "noise: could be chance"} />
                                                    <TipLine label="95% range of the difference"
                                                             value={d.text !== "-" ? `${fmt((vb ?? 0) - (va ?? 0) - sig.range, m.digits)} to ${fmt((vb ?? 0) - (va ?? 0) + sig.range, m.digits)}${m.unit}` : "-"} />
                                                    <TipLine label={sig.paired ? "Paired hours compared" : "Hours compared"}
                                                             value={sig.nEff < sig.n - 0.5 ? `${sig.n} (about ${sig.nEff.toFixed(0)} independent)` : `${sig.n}`} />
                                                    <TipLine label="Strength (t)" value={Math.abs(sig.t) >= 99 ? "very large" : sig.t.toFixed(1)} />
                                                    <div className={styles.tipNote}>{"Neighbouring game hours are related (a jam at 08:00 is often still there at 09:00), so they count as fewer independent hours. Clear: t of 3 or more. Likely: t between 2 and 3. Noise: smaller than the simulation's own randomness."}</div>
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
                                                        <SigBadge sig={sig} />
                                                    </div>
                                                </Tooltip>
                                            </div>
                                        );
                                    })}
                                </div>
                                <div className={styles.note}>Real?: is the difference bigger than the simulation's own randomness? Hover it for the details. Differences marked noise are not coloured.</div>
                                <div className={styles.toggles}>
                                    <Tooltip tooltip={<div className={styles.tip}>{"Copies this comparison as a table (markdown) to the clipboard, to paste into a post or a bug report."}</div>}>
                                        <div className={`${styles.toggle} ${copied ? styles.toggleOn : ""}`} onClick={() => {
                                            const rows = compareRows.map(([label, m]) => {
                                                const va = cmp.a.hours > 0 ? m.value(cmp.a) : null;
                                                const vb = cmp.b.hours > 0 ? m.value(cmp.b) : null;
                                                const sig = significance(capsA, capsB, scope, m);
                                                return { label, a: fmt(va, m.digits, m.unit), b: fmt(vb, m.digits, m.unit), diff: fmtDiff(va, vb, m).text, sig: VERDICT_TEXT[sig.verdict] };
                                            });
                                            trigger(GROUP, "copySummary", summaryText(nameA, nameB, runA, runB, cmp.note, rows, cmp.a.hours, cmp.b.hours));
                                            setCopied(true);
                                        }}>{copied ? "Copied" : "Copy summary"}</div>
                                    </Tooltip>
                                </div>
                                <div className={styles.note}>{`A: ${runA?.settings ?? "-"}`}</div>
                                <div className={styles.note}>{`B: ${runB?.settings ?? "-"}`}</div>
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
                                {view === "Timeline" && windowControls(capsA, capsB)}
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
                                                // A run recorded before hotspots existed has no data at all: show "-", not 0.
                                                const hasA = ha.length > 0;
                                                const hasB = hb.length > 0;
                                                const va = hasA ? (e.a ? e.a.q : 0) : null;
                                                const vb = hasB ? (e.b ? e.b.q : 0) : null;
                                                const d = fmtDiff(va, vb, qm);
                                                const jam = (x?: Hotspot) => (x && x.j >= 0.05 ? ` (${fmt(x.j, 1)})` : "");
                                                return (
                                                    <Tooltip key={place} tooltip={<div className={styles.tip}>{"Click to show it on the map"}</div>}>
                                                        <div className={`${styles.tableRow} ${styles.clickRow}`} onClick={() => trigger(GROUP, "focusPlace", focusKey(place, e.a, e.b))}>
                                                            <div className={styles.tableLabel}>{e.k === "junction" ? `${place} (junction)` : place}</div>
                                                            <div className={styles.tableCell}>{va === null ? "-" : `${fmt(va, 1)}${jam(e.a)}`}</div>
                                                            <div className={styles.tableCell}>{vb === null ? "-" : `${fmt(vb, 1)}${jam(e.b)}`}</div>
                                                            <div className={`${styles.tableCellWide} ${d.cls}`}>{d.text}</div>
                                                        </div>
                                                    </Tooltip>
                                                );
                                            })}
                                        </div>
                                        <div className={styles.note}>S = street, J = junction. A place that wasn't among the busiest in an hour counts as 0 for that hour. Streets with the same name are counted together.</div>
                                    </>);
                                })()}
                            </>)}
                        </>)}

                        {tab === "Bottlenecks" && (() => {
                            const ongoing = causesView === "ongoing";
                            const placeRows = (ongoing ? bottlenecks.ongoing : bottlenecks.rows) ?? [];
                            const fixRows = (ongoing ? bottlenecks.ongoingLinks : bottlenecks.links) ?? [];
                            const win = bottlenecks.window ?? 0;
                            return (<>
                                <div className={styles.toggles}>
                                    <Toggle label={`Ongoing (last ${win} game hours)`} selected={ongoing} onClick={() => setCausesView("ongoing")} />
                                    <Toggle label="This hour" selected={!ongoing} onClick={() => setCausesView("hour")} />
                                </div>
                                {placeRows.length === 0 ? (
                                    <div className={styles.empty}>{ongoing
                                        ? "No real bottleneck: no place averages 5 or more stuck cars per hour, coming back in at least a quarter of the hours."
                                        : "No bottlenecks yet: the first summary comes after one full game hour with queues."}</div>
                                ) : (<>
                                    <div className={styles.note}>{ongoing
                                        ? `Where queues start most, added up over the last ${win} game hours. Fix the start of a queue, not where the cars stand.`
                                        : `Where queues start, game hour ${bottlenecks.time}. Fix the start of a queue, not where the cars stand.`}</div>
                                    {!ongoing && bottlenecks.quiet ? <div className={styles.note}>{"No real bottleneck this hour: queues clear within about a light cycle."}</div> : null}
                                    <div className={styles.table}>
                                        {placeRows.map(r => (
                                            <Tooltip key={r.id} tooltip={<div className={styles.tip}>
                                                <div className={styles.tipTitle}>{r.place}</div>
                                                <div>{`Causes: ${r.mix || r.cause}`}</div>
                                                {ongoing ? <div>{`On the list in ${r.hoursIn ?? 0} of the last ${win} game hours; ${fmt(r.avgWait ?? 0, 1)} stuck cars per hour on average, ${fmt(r.recent ?? 0, 1)} in the last 3 hours.${r.status === "cleared" ? " No queue for " + (r.since ?? 0) + " game hours: if you fixed it, it worked. It leaves the list once its old hours are out of the window." : ""}${(r.despawns ?? 0) > 0 ? ` The game removed ${fmt(r.despawns ?? 0, 1)} stuck cars per hour here.` : ""}`}</div> : null}
                                                <div>{`Last time: ${fmt(r.queued, 1)} cars queued behind it, ${fmt(r.wait ?? 0, 1)} of them waited over half a minute.`}</div>
                                                <div className={styles.tipNote}>{"Click to show it on the map with its lanes, turns and where the drivers come from and go to. Click again to hide."}</div>
                                            </div>}>
                                                <div className={`${styles.tableRow} ${styles.clickRow} ${overlayShown === r.id ? styles.rowOn : ""} ${ongoing && r.status === "cleared" ? styles.rowCleared : ""}`} onClick={() => trigger(GROUP, "showOverlay", r.id)}>
                                                    <div className={styles.tableLabel}>
                                                        <div>{r.kind === "junction" ? `${r.place} (junction)` : r.place}</div>
                                                        {(() => {
                                                            // One string per text element (Coherent stacks mixed pieces), the status as its own badge.
                                                            const ideas = fixRows.filter(l => l.forId === r.id).length;
                                                            const ideaText = "";
                                                            const removed = r.despawns ?? 0;
                                                            const status = ongoing ? r.status ?? "" : "";
                                                            const text = !ongoing ? `${r.cause}${ideaText}`
                                                                : status === "cleared" ? `no queue for ${r.since ?? 0} game hours · was in ${r.hoursIn ?? 0} of ${win} hours`
                                                                : `${(r.queuesOn ?? []).length > 0 ? "queues on " + (r.queuesOn ?? []).map(q => q.n).join(", ") : r.cause} · in ${r.hoursIn ?? 0} of ${win} hours${ideaText}`;
                                                            const badge = status === "better" ? { cls: styles.statusBetter, label: "getting better" }
                                                                : status === "worse" ? { cls: styles.statusWorse, label: "getting worse" }
                                                                : status === "cleared" ? { cls: styles.statusCleared, label: "cleared (fixed?)" } : null;
                                                            return (<div className={styles.subRow}>
                                                                {badge ? <div className={`${styles.status} ${badge.cls}`}>{badge.label}</div> : null}
                                                                {removed >= 0.5 ? <div className={`${styles.status} ${styles.statusRemoved}`}>{`${fmt(removed, 0)} removed${ongoing ? "/h" : ""}`}</div> : null}
                                                                {ideas > 0 ? <div className={`${styles.status} ${styles.statusIdea}`}>{`${ideas} fix idea${ideas > 1 ? "s" : ""}`}</div> : null}
                                                                <div className={styles.subLine}>{text}</div>
                                                            </div>);
                                                        })()}
                                                    </div>
                                                    <div className={styles.tableCell}>{ongoing ? (r.status === "cleared" ? "cleared" : `${fmt(r.avgWait ?? 0, 0)} stuck/h`) : `${fmt(r.wait ?? r.queued, 0)} stuck`}</div>
                                                </div>
                                            </Tooltip>
                                        ))}
                                    </div>
                                </>)}
                                {ongoing && (bottlenecks.minHours ?? -1) < 0 && placeRows.length > 0
                                    ? <div className={styles.note}>{"Fix ideas (new roads, missing turns) show up in a place's details after 4 game hours, once it is clear which detours keep coming back."}</div>
                                    : null}
                                {(() => {
                                    const r = placeRows.find(x => x.id === overlayShown);
                                    const l = fixRows.find(x => (x.key ?? `${x.aId}-${x.bId}`) === overlayShown);
                                    if (r) {
                                        const advice = (r.advice ?? "").split("||").filter(a => a.length > 0);
                                        const seen = advice.map(a => { const at = a.indexOf(" Try: "); return at > 0 ? a.substring(0, at) : a; });
                                        const tries = advice.map(a => { const at = a.indexOf(" Try: "); return at > 0 ? a.substring(at + 6) : ""; }).filter(t => t.length > 0);
                                        const endsElsewhere = !!r.endsAt && r.endsAt !== r.place && r.endsAtId !== r.id;
                                        const fixes = fixRows.filter(f => f.forId === r.id);
                                        return (<div className={styles.detailBox}>
                                            <div className={styles.detailTitle}>{r.place}</div>
                                            <div className={styles.sectionLabel}>{"What we see"}</div>
                                            {(r.queuesOn ?? []).length > 0 ? <Chips label="Queues on:" items={r.queuesOn ?? []} /> : null}
                                            {endsElsewhere ? <Chips label="They start at the junction:" items={[{ n: r.endsAt ?? "", id: r.endsAtId ?? "" }]} /> : null}
                                            {r.moves ? <div className={styles.note}>{r.moves}</div> : null}
                                            {r.lanes ? <div className={styles.note}>{r.lanes}</div> : null}
                                            {r.flow ? <div className={styles.note}>{r.flow}</div> : null}
                                            <Chips label="Going to:" items={r.toList ?? []} />
                                            <Chips label="Coming from:" items={r.fromList ?? []} />
                                            <div className={styles.note}>{`Passing through (start and end over 1 km away): ${r.through < 0 ? "unknown" : fmt(r.through, 0) + "%"}.${(r.trucks ?? 0) >= 10 ? ` Trucks: ${fmt(r.trucks ?? 0, 0)}% of the queued vehicles.` : ""}`}</div>
                                            {(r.despawns ?? 0) > 0 ? <div className={styles.advice}>{`The game removed ${fmt(r.despawns ?? 0, 1)} stuck cars ${ongoing ? "per hour" : "last hour"} in these queues (cars stuck too long simply vanish).`}</div> : null}
                                            {seen.map(t => <div key={t} className={styles.advice}>{t}</div>)}
                                            {tries.length > 0 || fixes.length > 0 ? (<>
                                                <div className={styles.sectionLabel}>{"What you can do"}</div>
                                                {tries.map(t => <div key={t} className={styles.tryLine}>{`• ${t.charAt(0).toUpperCase()}${t.substring(1)}`}</div>)}
                                                {fixes.map(f => (
                                                    <div key={f.key ?? `${f.aId}-${f.bId}`} className={`${styles.tryLine} ${styles.clickRow}`} onClick={() => trigger(GROUP, "showOverlay", f.key ?? `${f.aId}-${f.bId}`)}>
                                                        {`• ${linkText(f)} It would take these drivers out of this queue${ongoing ? ` (seen in ${f.hoursIn ?? 0} of ${win} hours)` : ""}. Click to see it on the map.`}
                                                    </div>
                                                ))}
                                            </>) : null}
                                            <Legend items={PLACE_LEGEND} />
                                        </div>);
                                    }
                                    if (l) {
                                        const fix = linkLegend(l);
                                        const at = fix.indexOf("Try: ");
                                        return (<div className={styles.detailBox}>
                                            {l.forName ? <div className={`${styles.note} ${styles.clickRow}`} onClick={() => trigger(GROUP, "showOverlay", l.forId ?? "")}>{`< Back to ${l.forName}`}</div> : null}
                                            <div className={styles.sectionLabel}>{"What we see"}</div>
                                            <div className={styles.advice}>{linkStory(l)}</div>
                                            {l.forName ? <div className={styles.note}>{`These drivers are stuck in the queue at ${l.forName}; this would let them skip it.`}</div> : null}
                                            {(l.streets ?? []).length > 0 ? <Chips label="Streets:" items={l.streets ?? []} /> : null}
                                            {at >= 0 ? (<>
                                                <div className={styles.sectionLabel}>{"What you can do"}</div>
                                                <div className={styles.tryLine}>{`• ${fix.substring(at + 5).charAt(0).toUpperCase()}${fix.substring(at + 6)}`}</div>
                                            </>) : null}
                                            <Legend items={linkLegendItems(l)} />
                                        </div>);
                                    }
                                    return <div className={styles.note}>{"Click a place to show it on the map, with what you can do about it."}</div>;
                                })()}
                            </>);
                        })()}

                        {tab === "Destinations" && (<>
                            {runs.length < 2 ? <div className={styles.empty}>{"Record at least two runs (for example Native and Live from the same save) to compare destinations."}</div> : (<>
                                {pickers}
                                <div className={styles.toggles}>
                                    <Toggle label="Same game time" selected={scope === "Paired"} onClick={() => setScope("Paired")} />
                                    <Toggle label="Same times of day" selected={scope === "SameHours"} onClick={() => setScope("SameHours")} />
                                    <Toggle label="All hours" selected={scope === "AllHours"} onClick={() => setScope("AllHours")} />
                                </div>
                                {(() => {
                                    const table = (title: string, metrics: DestMetric[]) => (
                                        <>
                                            <div className={styles.sectionTitle}>{title}</div>
                                            <div className={styles.table}>
                                                <div className={styles.tableRow}>
                                                    <div className={styles.tableLabel}></div>
                                                    <div className={`${styles.tableHead} ${styles.colorA}`}>{nameA}</div>
                                                    <div className={`${styles.tableHead} ${styles.colorB}`}>{nameB}</div>
                                                    <div className={styles.tableHeadWide}>B vs A</div>
                                                    <div className={styles.tableHeadSig}>Real?</div>
                                                </div>
                                                {metrics.map(dm => {
                                                    const ser = destSeries(capsA, capsB, scope, dm);
                                                    const mean = (v: number[]) => (v.length > 0 ? v.reduce((p, q) => p + q, 0) / v.length : null);
                                                    const va = mean(ser.xs);
                                                    const vb = mean(ser.ys);
                                                    const m: Metric = { key: dm.label, label: dm.label, unit: dm.unit, digits: dm.digits, higherIsBetter: dm.higherIsBetter, value: () => 0 };
                                                    const d = fmtDiff(va, vb, m);
                                                    const sig = significanceOf(ser);
                                                    return (
                                                        <div className={styles.tableRow} key={title + dm.label}>
                                                            <div className={styles.tableLabel}>{dm.label}</div>
                                                            <div className={styles.tableCell}>{fmt(va, dm.digits, dm.unit)}</div>
                                                            <div className={styles.tableCell}>{fmt(vb, dm.digits, dm.unit)}</div>
                                                            <div className={`${styles.tableCellWide} ${sig.verdict === "noise" ? "" : d.cls}`}>{d.text}</div>
                                                            <div className={styles.tableCellSig}>
                                                                <SigBadge sig={sig} />
                                                            </div>
                                                        </div>
                                                    );
                                                })}
                                            </div>
                                        </>
                                    );
                                    // The number that decides whether "change destination mid-trip" is worth building.
                                    const shareOf = (caps: Capture[], k: keyof Capture) => {
                                        const v = caps.map(field(k)).filter((x): x is number => x !== null);
                                        return v.length > 0 ? v.reduce((p, q) => p + q, 0) / v.length : null;
                                    };
                                    const live = runB?.mode === "Live" ? capsB : runA?.mode === "Live" ? capsA : capsB;
                                    const leis = shareOf(live, "qLeis");
                                    const shop = shareOf(live, "qShop");
                                    return (<>
                                        <div className={styles.note}>{leis === null
                                            ? "These numbers are recorded from this version on. Record a Native and a Live run from the same save to compare."
                                            : `Of the cars waiting in queues, ${fmt(leis, 0, "%")} are on leisure trips and ${fmt(shop, 0, "%")} on shopping trips. Only leisure trips could switch destination mid-trip (shopping is paid when the trip is planned), so that share is the most such a feature could help.`}</div>
                                        {table("Why queued cars are on the road (% of queued cars)", QUEUED_PURPOSE)}
                                        {table("Shopping trips (all travel modes, per game hour)", SHOP_METRICS)}
                                        {table("Leisure trips (all travel modes, per game hour)", LEISURE_METRICS)}
                                        <div className={styles.note}>{"If live traffic makes people pick other places, a Live run uses more different places, sends fewer trips to the 5 busiest, at a similar distance from home."}</div>
                                    </>);
                                })()}
                            </>)}
                        </>)}

                        {tab === "Cost" && (<>
                            {runs.length < 2 ? <div className={styles.empty}>{"Record a Native and a Live run from the same save, at the same game speed, to compare the cost."}</div> : (<>
                                {pickers}
                                <div className={styles.toggles}>
                                    <Toggle label="Same game time" selected={scope === "Paired"} onClick={() => setScope("Paired")} />
                                    <Toggle label="Same times of day" selected={scope === "SameHours"} onClick={() => setScope("SameHours")} />
                                    <Toggle label="All hours" selected={scope === "AllHours"} onClick={() => setScope("AllHours")} />
                                </div>
                                {(() => {
                                    const speedOf = (caps: Capture[]) => {
                                        const v = caps.map(c => c.spd).filter(x => typeof x === "number" && x > 0);
                                        if (v.length === 0) return null;
                                        const counts = new Map<number, number>();
                                        for (const x of v) counts.set(x, (counts.get(x) ?? 0) + 1);
                                        return Array.from(counts.entries()).sort((p, q) => q[1] - p[1])[0][0];
                                    };
                                    const sa = speedOf(capsA);
                                    const sb = speedOf(capsB);
                                    const speedNote = sa === null || sb === null
                                        ? "Performance is recorded from this version on."
                                        : sa !== sb
                                            ? `Warning: run A mostly ran at speed ${sa}, run B at speed ${sb}. Simulation speed and FPS only compare at the same speed setting.`
                                            : `Both runs mostly at speed ${sa}. At the fastest speed the simulation runs as fast as your CPU allows, so any cost shows up there.`;
                                    return (<>
                                        <div className={styles.note}>{speedNote}</div>
                                        <div className={styles.table}>
                                            <div className={styles.tableRow}>
                                                <div className={styles.tableLabel}></div>
                                                <div className={`${styles.tableHead} ${styles.colorA}`}>{nameA}</div>
                                                <div className={`${styles.tableHead} ${styles.colorB}`}>{nameB}</div>
                                                <div className={styles.tableHeadWide}>B vs A</div>
                                                <div className={styles.tableHeadSig}>Real?</div>
                                            </div>
                                            {COST_METRICS.map(dm => {
                                                const ser = destSeries(capsA, capsB, scope, dm);
                                                const mean = (v: number[]) => (v.length > 0 ? v.reduce((p, q) => p + q, 0) / v.length : null);
                                                const va = mean(ser.xs);
                                                const vb = mean(ser.ys);
                                                // Shown even when only one run has the number (e.g. re-routes, mod CPU of a Native run).
                                                const onlyA = va === null ? mean(capsA.map(dm.get).filter((x): x is number => x !== null)) : va;
                                                const onlyB = vb === null ? mean(capsB.map(dm.get).filter((x): x is number => x !== null)) : vb;
                                                const m: Metric = { key: dm.label, label: dm.label, unit: dm.unit, digits: dm.digits, higherIsBetter: dm.higherIsBetter, value: () => 0 };
                                                const d = fmtDiff(va, vb, m);
                                                const sig = significanceOf(ser);
                                                return (
                                                    <div className={styles.tableRow} key={dm.label}>
                                                        <div className={styles.tableLabel}>{dm.label}</div>
                                                        <div className={styles.tableCell}>{fmt(onlyA, dm.digits, dm.unit)}</div>
                                                        <div className={styles.tableCell}>{fmt(onlyB, dm.digits, dm.unit)}</div>
                                                        <div className={`${styles.tableCellWide} ${sig.verdict === "noise" ? "" : d.cls}`}>{d.text}</div>
                                                        <div className={styles.tableCellSig}>
                                                            <SigBadge sig={sig} />
                                                        </div>
                                                    </div>
                                                );
                                            })}
                                        </div>
                                        <div className={styles.note}>{"Simulation speed and FPS show the full effect, including the extra routes the game calculates for re-routing. Mod CPU is only the mod's own work. Game memory is the whole game, for context: it grows with the city."}</div>
                                    </>);
                                })()}
                            </>)}
                        </>)}

                        {tab === "Rerouting" && (<>
                            {runs.length > 0 && pickers}
                            {rrTotals ? (<>
                                <div className={styles.sectionTitle}>{`Run ${rrTotalsName}: average per game hour`}</div>
                                <InfoRow label="Re-routes" value={`${fmt(rrTotals.rr)} (${fmt(rrTotals.rrTrucks)} by trucks)`} />
                                <InfoRow label="Found a faster route"
                                         tone={rrTotals.rrBetter + rrTotals.rrNoBetter > 0 ? ((100 * rrTotals.rrBetter) / (rrTotals.rrBetter + rrTotals.rrNoBetter) >= 50 ? "good" : (100 * rrTotals.rrBetter) / (rrTotals.rrBetter + rrTotals.rrNoBetter) < 30 ? "warn" : undefined) : undefined}
                                         value={rrTotals.rrBetter + rrTotals.rrNoBetter > 0 ? `${fmt((100 * rrTotals.rrBetter) / (rrTotals.rrBetter + rrTotals.rrNoBetter), 0, "%")} (avg ${fmt(rrTotals.rrSaved, 0, "%")} of the rest of the trip saved, estimate)` : "-"} />
                                <InfoRow label="Held back by the limit" tone={rrTotals.rrHeld >= 1 ? "warn" : undefined} value={`${fmt(rrTotals.rrHeld)}${rrTotals.rrBudget >= 0 ? ` (limit ${fmt(rrTotals.rrBudget)} per update)` : ""}`}
                                         hint="Re-routes postponed because the per-update limit was reached. The limit follows how busy the game's route planner is: from 8 when it is busy up to 128 when it has room." />
                                {rrTotals.rrEmerg >= 0 && <InfoRow label="Emergency vehicles" value={fmt(rrTotals.rrEmerg)} hint="Re-routes by ambulances, fire engines and police cars on an emergency run. They always have live navigation." />}
                                {rrTotals.rrDefer >= 0 && <InfoRow label="Waited for a junction" value={fmt(rrTotals.rrDefer)} hint="Times a driver's way ahead got slower but no junction with a choice was coming up, so they waited for one before looking for another way." />}
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
                                {rrView === "Timeline" && windowControls(rrA, rrB)}
                                {legend}
                            </>) : (
                                <div className={styles.empty}>
                                    {lastLive ? "Pick a Live run as A or B to see its re-routing." : "No live game hour recorded yet."}
                                </div>
                            )}
                        </>)}

                        {tab === "Roadworks" && (<>
                            <div className={styles.temporary}>{TEMPORARY}</div>
                            {roadworks.cleared ? (
                                <div className={styles.note} onClick={() => trigger(GROUP, "roadworksDismissNotice")}>{"Temporary roadworks were cleared (not saved) when the city loaded. Click to hide."}</div>
                            ) : null}
                            {!modActive && <div className={styles.note}>{"The mod is set to Off: road marks do nothing until you turn it on."}</div>}
                            <div className={styles.note}>{"Mark roads to test your network without deleting anything: close a road for traffic, or ask drivers to avoid or prefer it. Marks change how drivers plan their routes; they are not barriers."}</div>
                            <div className={styles.toggles}>
                                {MARKS.map(m => (
                                    <Tooltip key={m.mark} tooltip={<div className={styles.tip}><div className={styles.tipTitle}>{m.label}</div><div>{m.tip}</div><div className={styles.tipNote}>{TEMPORARY}</div></div>}>
                                        <div className={`${styles.toggle} ${toolOn && toolMode === m.mark ? styles.toggleOn : ""}`}
                                             onClick={() => trigger(GROUP, "roadworksTool", toolOn && toolMode === m.mark ? 0 : m.mark)}>
                                            {m.label}
                                        </div>
                                    </Tooltip>
                                ))}
                                {toolOn && <div className={styles.toggle} onClick={() => trigger(GROUP, "roadworksTool", 0)}>{"Done"}</div>}
                            </div>
                            {toolOn && <div className={styles.note}>{`Click a road to mark it "${markLabel(toolMode)}". Click it again, or right-click it, to remove the mark.`}</div>}
                            {roadworks.roads.length === 0 ? <div className={styles.empty}>{"No roads marked."}</div> : (<>
                                <div className={styles.table}>
                                    {roadworks.roads.slice(0, 12).map(r => (
                                        <div className={styles.tableRow} key={r.id}>
                                            <Tooltip tooltip={<div className={styles.tip}>{"Click to show it on the map"}</div>}>
                                                <div className={`${styles.tableLabel} ${styles.clickRow}`} onClick={() => trigger(GROUP, "roadworksFocus", r.id)}>{r.name}</div>
                                            </Tooltip>
                                            <div className={`${styles.tableCell} ${r.mark === 1 ? styles.bad : r.mark === 3 ? styles.good : ""}`}>{markLabel(r.mark)}</div>
                                            <div className={styles.toggle} onClick={() => trigger(GROUP, "roadworksRemove", r.id)}>{"Remove"}</div>
                                        </div>
                                    ))}
                                </div>
                                {roadworks.roads.length > 12 && <div className={styles.note}>{`and ${roadworks.roads.length - 12} more`}</div>}
                                <div className={styles.resetRow}>
                                    <DangerButton id="marks" label="Remove all marks" confirmLabel="Click again to remove all marks"
                                                  pending={pending} setPending={setPending} onConfirm={() => trigger(GROUP, "roadworksClear")} />
                                </div>
                            </>)}
                        </>)}

                        {tab === "Runs" && (<>
                            <div className={styles.note}>Old runs are cleaned up after the time set in Options ("Keep statistics for"), oldest first, whole runs only. The baseline and runs marked Keep stay.</div>
                            <div className={styles.note}>A run is one continuous recording with the same mode and settings. A new run starts when you load a save, reset, or change the mod's settings (the hour in progress is then not counted).</div>
                            {runs.length === 0 && <div className={styles.empty}>No runs recorded for this city yet.</div>}
                            <div className={styles.runList}>
                                {[...runs].reverse().slice(0, 12).map(r => (
                                    <div className={styles.runItem} key={r.id}>
                                        <div className={styles.runText}>
                                            <div className={styles.runTitle}>
                                                <span className={r.mode === "Live" ? styles.badgeLive : styles.badgeNative}>{runName(r, runs)}</span>
                                                {r.baseline ? <span className={styles.keepBadge}>baseline</span> : null}
                                                <span className={styles.chipSub}>{runRange(r)}</span>
                                            </div>
                                            <div className={styles.runSettings}>{r.settings}</div>
                                        </div>
                                        <Tooltip tooltip={<div className={styles.tip}>{r.baseline
                                            ? "The newest Native run of this city is your baseline and is always kept."
                                            : r.pinned ? "Pinned: never removed by 'Keep statistics for'. Click to unpin." : "Pin to keep this run when old statistics are cleaned up ('Keep statistics for')."}</div>}>
                                            <div className={`${styles.toggle} ${r.pinned ? styles.toggleOn : ""}`} onClick={() => trigger(GROUP, "togglePin", r.id)}>
                                                {r.pinned ? "Kept" : "Keep"}
                                            </div>
                                        </Tooltip>
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
