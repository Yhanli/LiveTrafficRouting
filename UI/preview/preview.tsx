// Browser preview of the Live Traffic panel with sample data (no game needed).
// Build: npm run preview (writes preview/dist), then open preview/index.html.
import { createRoot } from "react-dom/client";
import { LiveTrafficPanel } from "mods/LiveTrafficPanel";

const root = createRoot(document.getElementById("root")!);
root.render(<LiveTrafficPanel />);

// Open the panel and go to the tab named in the address (#Causes by default).
setTimeout(() => {
    (document.querySelector(".preview-button") as HTMLElement | null)?.click();
    setTimeout(() => {
        const want = decodeURIComponent(location.hash.replace("#", "")) || "Causes";
        const tab = Array.from(document.querySelectorAll("div")).find(d => d.textContent === want && d.children.length === 0);
        (tab as HTMLElement | undefined)?.click();
    }, 50);
}, 50);
