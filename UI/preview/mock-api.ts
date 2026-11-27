// Browser preview only: a stand-in for the game's "cs2/api" (bindings and triggers), fed with sample data.
import { useEffect, useState } from "react";

interface Binding<T> { name: string; fallback: T; }

const store: Record<string, unknown> = (window as any).__previewData ?? {};
const listeners = new Set<() => void>();

export function setPreviewValue(name: string, value: unknown) {
    store[name] = value;
    listeners.forEach(l => l());
}

export function bindValue<T>(_group: string, name: string, fallback: T): Binding<T> {
    return { name, fallback };
}

export function useValue<T>(b: Binding<T>): T {
    const [, tick] = useState(0);
    useEffect(() => {
        const l = () => tick(x => x + 1);
        listeners.add(l);
        return () => { listeners.delete(l); };
    }, []);
    return (b.name in store ? store[b.name] : b.fallback) as T;
}

export function trigger(_group: string, name: string, ...args: unknown[]) {
    // The overlay toggle is the one trigger whose result the panel shows.
    if (name === "showOverlay") {
        const id = String(args[0] ?? "");
        setPreviewValue("overlayShown", store["overlayShown"] === id ? "" : id);
    }
    const log = document.getElementById("triggers");
    if (log) log.textContent = `trigger ${name}(${args.map(a => JSON.stringify(a)).join(", ")})\n` + log.textContent;
}
