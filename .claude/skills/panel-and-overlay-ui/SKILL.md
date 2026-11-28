---
name: panel-and-overlay-ui
description: Standards and the user's preferences for the Live Traffic statistics panel (UI/src/mods, React in Coherent Gameface) and the in-game map overlay (BottleneckOverlaySystem). Use before changing any panel text, layout, metric, badge or colour, or any overlay line, arrow, ring or style value.
---

# Panel and map overlay: how we build them

These are agreed with the user over many rounds of in-game screenshots. Follow them without being asked; when the
user corrects something new, add it here.

## Wording

- Plain words a player understands. No internal terms (head, chain, node, aggregate, master lane) in the UI.
- Separate **observation** from **recommendation**: the details box has "What we see" (facts; problems in yellow
  `styles.advice`) and "What you can do" (bullets from the "Try: ..." part of each advice string). Advice strings are
  `"<problem sentence>. Try: <fix>."` so the panel can split them.
- Left and right are **always as the driver sees it**, and say so where it matters. Anything with a direction on the
  map gets arrows.
- Name things the player can find: street and junction names, "lane 1 from the left". Every name in the details is
  a clickable chip that moves the camera (`focusEntity`), never a selection (selection made the pin hop).
- Numbers per game hour say "per hour" / "/h"; a game hour is ~3 minutes of driving, so counts are small.
- When unsure whether the user will understand a text, they usually won't: rewrite it simpler.

## Key signals stand out

- A status or warning must not look like ordinary text. Use a coloured badge (`styles.status` + `statusBetter` green,
  `statusWorse` red, `statusCleared` bright green with the row dimmed, `statusRemoved` red, `statusIdea` blue) or a
  tone on `InfoRow` (`tone="good" | "bad" | "warn"`).
- Colour only what has a clear good/bad reading. Metrics without a baseline (queue %, jam count on the Now tab) stay
  plain; side effects are marked neutral ("side effect: watch it"), e.g. local streets, public transport share.
- Every metric that can mislead gets a `note` (for example: fewer cars can mean people left the car, not faster trips).

## What to show

- Don't show trivial things: thresholds (e.g. 5+ stuck cars per hour, in a quarter of the hours) and a ranking over
  time (24 game hours), not one-hour flashes. Show when a fix worked (cleared / better / worse).
- Suggestions belong to the problem they fix (fix ideas live in their bottleneck's details) and disappear with it.
- Keep tabs short: Coherent clips overflow instead of scrolling.

## Coherent Gameface layout (game UI)

- Every element is a flex box: text pieces mixed with `{values}` in one element stack on separate lines. Build each
  line as **one template string**; put badges in their own element inside a row.
- A row of items side by side needs `display: flex` in its class, not only `flex-direction: row`.
- No HTML `title`: use `Tooltip` from `cs2/ui`. 1rem = 1px.
- Check layout in the browser preview first (`cd UI; npm run preview`, serve `UI/preview`, open
  `index.html#Causes`); add new fields to `UI/preview/sample-data.js` (and `make-sample-history.py` for captures) so
  the preview shows them. The real check is in game.
- Run `npx tsc --noEmit -p .` in `UI/` as well as `npm run build`: the build does not fail on type errors.

## Map overlay (BottleneckOverlaySystem)

- Lines are smooth lane curves with rounded caps, never chains of straight segments. Turns are drawn along the
  game's junction lane curves, only the turns the queue uses (>2%), within 120 m of the ring.
- Long dashed arcs for "coming from" (cyan) and "going to" (pink): the user prefers them to short stubs. Arcs end at the
  ring's edge, at the ring's height. Origins and destinations are areas (~1 km): each is a faint circle of its real
  size (`WorldSize`, not zoom-scaled) and the arc starts / ends at the circle's edge; outside the city has no circle.
- Area chips move the camera to the area centre (`focusPoint`), with a tooltip listing the streets in the area; a list
  that doesn't cover nearly everyone says so ("these cover 74%").
- The yellow ring marks where the queue enters the junction (start of the turns it uses), not the junction centre.
- Widths scale with camera distance (zoom factor clamp(dist/400, 0.12, 12)): thin up close, same on-screen size far
  out. Arrowheads small. Outlines are drawn in a first pass and fills in a second (no dark dots at joints).
- The queue line has direction arrows, but sparse: one lane per road piece, spaced by distance from the queue's
  front (spacing grows with zoom), plus one at the front. One arrow per lane piece looked like red fuzz far out.
- Area names must be unique among the shown areas (a long street can top two areas): next street, else a direction.
- Colours match the panel legend exactly; update the legend when colours change.
- Style values live in `ModsData/LiveTrafficRouting/overlay-style.txt` (hot reload). When changing defaults, bump
  `kStyleVersion` so players get the new file (old one kept as `overlay-style.old.txt`). For quick tuning while the
  game runs, edit the live file instead of rebuilding.
- `overlay.html?file=overlay-preview.json` draws what the game exported, for checking shapes without the game.

## When the user sends a screenshot

Check what it shows against the code and game data before answering (e.g. count the lanes, read the lane connector
view), say plainly whether the mod is right, and fix the cause, not the symptom.
