# Case Study: Finding a UI Perf Bug

A real write-up from using this tool on a UE 5.7 game's HUD. Names are removed, but the numbers and method are real. This is the kind of result this tool is built to help you get.

## The question

The HUD felt expensive to render each frame. Where was the time going? And did wrapping widgets in `SInvalidationPanel` (a box that caches how a widget paints) actually help?

## The steps

1. Captured a `.utrace` while playing normally with the HUD open.
2. Ran `export.sh --trace campaign_hud.utrace --out ./out/base`.
3. Made one change: wrapped two widget groups in `SInvalidationPanel`.
4. Captured a new trace, exported it to `./out/box`.
5. Compared `timerstats.csv` between the two runs, using the exclusive average column.

## What we found

Wrapping the two widget groups cut the paint scope's cost from **2.70 to 0.63 ms per frame, a 77% drop**. A separate layout scope did not change at all. This confirmed: the box only caches paint, not layout. The win was real, but only for paint.

## Two guesses that turned out wrong

- **Removing per-frame ticking** looked like an easy win. It only saved about 0.06 ms per frame. Basically nothing. The CSV made this clear once counts were compared, instead of guessing that ticking was the problem.
- **Forcing a widget to always repaint (`ForceVolatile`) on animating progress bars** looked flat across 6 test runs (2.60 to 2.96 ms per frame, no real pattern). Without CSVs to compare, this might have shipped as a "fix" based on a hunch, when it did nothing.

## Finding one bad frame

The same trace had one 144 ms hitch. `frame-drill.sh map` found the frame using the `Slate::Prepass` scope. The real problem was not that frame, but a large gap right before it. `frame-drill.sh window` on that gap showed the exact call chain. It turned out to be an editor cost (the Content Browser), not a bug in the game itself.

## Why this matters for AI-driven perf work

None of this needed the Insights GUI. Every step was: export, get a CSV, read plain text and think about it. The wrong guesses were caught because the data could be compared side by side, not just glanced at once in a flame graph.
