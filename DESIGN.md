---
name: Daxis
description: Supabase-inspired desktop design language. Quietly technical, near-monochrome, one emerald accent.
source: https://github.com/VoltAgent/awesome-design-md/tree/main/design-md/supabase (adapted from marketing site to a dense desktop tool)
colors:
  brand: "#3ecf8e"          # the only chromatic event
  brand-hover: "#24b47e"
  on-brand: "#171717"       # near-black text on green, never white
  dark:
    bg0: "#171717"          # chrome: top bar, sidebar, status bar, table headers
    bg1: "#1c1c1c"          # work surface, editors
    bg2: "#232323"          # controls, cards
    bg3: "#2a2a2a"          # hover, active row
    border: "#2e2e2e"
    border-strong: "#3e3e3e"
    fg: "#ededed"
    fg-muted: "#a0a0a0"
    fg-faint: "#707070"
    danger: "#f87171"
  light:
    bg0: "#f8f8f8"
    bg1: "#ffffff"
    bg2: "#ffffff"
    bg3: "#efefef"
    border: "#dfdfdf"
    border-strong: "#c7c7c7"
    fg: "#171717"
    fg-muted: "#707070"
    fg-faint: "#9a9a9a"
    danger: "#dc2626"
typography:
  ui: "Inter (Circular substitute), embedded"
  mono: "JetBrains Mono, embedded"
  scale: { h1: 22/500/-0.4, title: 20/500/-0.3, h2: 15/500, body: 13/400, caption: 12/400, label: 11/500/+0.6 uppercase }
rounded: { control: 6, card: 8, pill: 999 }
spacing: [2, 4, 8, 12, 16, 20, 24, 32]
---

# Daxis design system

The tokens live in `src/Daxis.App/Theme/Tokens.axaml` and the component styles in `Theme/Styles.axaml`. This file explains the rules behind them. If the code and this file disagree, fix one of them. Don't create a third version.

## Principles
- **The product is the decoration.** No gradients, no illustrations, no glassmorphism. Code, trees and tables carry the visual weight.
- **One green per view.** `brand` fills only the primary action, which is usually *Save changes*. Everywhere else it appears as small accents: the dirty dot, focus ring, run icon, kind pill and signed-in dot.
- **Near-black on green.** Primary buttons use `on-brand` text (`#171717`), never white.
- **Hairlines, not shadows.** Separate regions with 1px borders. Keep elevation for popups only.
- **Dark first, light equal.** Both themes are complete. Views never hardcode hex. Use `{DynamicResource Token}`.

## Layout
```
┌ top bar 48 ─ logo / workspace ▾ / item ─────────────── theme · avatar · sign out ┐
├ sidebar 264 ─┬ tab strip 38 ──────────────────────────────────────────────────────┤
│ search       │ item view (model · notebook · lakehouse)                          │
│ SECTION      │                                                                   │
│  rows 28     │                                                                   │
└ status bar 26 ─ ● state · message ─────────────────────────────────── progress ─┘
```
- The breadcrumb in the top bar (`daxis / workspace / item`) is the navigation. The workspace switcher lives in it, like Supabase's org/project switcher.
- Every item view shares the same header: a kind pill plus context in the first row, a 20px title, and actions right-aligned with the primary action last.
- Section labels are 11px uppercase with +0.6 tracking in `fg-faint`.

## Components
| Component | Rule |
|---|---|
| Button (default) | `bg2` fill, `border-strong` hairline, 6px radius, 13/500, 30px min height |
| Button.primary | `brand` fill, `on-brand` text, hover `brand-hover`. Only one per view |
| Button.ghost | Transparent icon button with `fg-muted` icon and `bg3` on hover |
| Button.nav | Sidebar row: 28px, `fg-muted`, `bg3` when active or hovered |
| TextBox | `bg2`, `border` hairline, `brand` border on focus |
| Pill | 999 radius, 11/500; `.brand` variant has a soft green fill with green text |
| Card | `bg2`, `border`, 8px radius; used for connection details |
| Banner | `danger` at 12% with a bottom hairline; always offers Retry or dismiss |
| Code editor | `bg1`, JetBrains Mono 13, faint gutter, green-tinted selection |
| Data grid | Mono 12 cells, `bg0` headers, hairline grid |
| Graph canvas | `bg1` with a `border-strong` dot grid. Cards are `bg2` with an 8px radius and hairline; edges are `border-strong` beziers. Tracing dims everything else to 14% and draws the path in `brand-text` with `brand` particles moving in the flow direction. Zoomed out, detail fades and titles grow |
| Floating toolbar | `bg2`, hairline, 8px radius, no shadow; top-left over a canvas, hints bottom-left |
| Usage bar | `bg3` track, `brand` for the primary share over `brand-border` for the secondary; grows from zero |
| Stepper | One row per step of a long operation: hollow ring (pending), pulsing brand ring (running, with a 3px determinate bar when the service reports a percentage), brand check (done), danger cross (failed). Sits in a card over a scrim; the result shows as a callout and the card closes only when nothing is running |
| Skeleton | `bg3` bar that breathes (opacity 0.35↔1) only while its row is loading |
| Callout | `bg2` card with hairline for explanations; `.danger` (DangerSoft + Danger) for blocks, `.warn` (Warning hairline) for warnings that need acknowledgement |
| Status dot | 8px: brand = fresh, `Warning` = stale, `Danger` = needs attention, `fg-faint` = unknown |
| Icons | Hand-drawn 24×24 stroke geometry at a 1.6px stroke (`Icon.cs`). No emoji, no icon fonts |

## Motion
- Ease out, never bounce. Canvas motion is exponential easing (frame-rate independent); page switches fade in over 280ms.
- Animate to explain: entrances stagger left to right in flow order, and particles show direction. Nothing loops while the view is idle.
- Request frames only while something is moving. The only looping animations (skeleton, running step) exist only while work is in flight.

## Syntax colours
The same roles apply to DAX and Python:
- keyword → brand
- function / builtin → blue
- string → amber
- number → coral
- measure ref / decorator → violet
- comment → faint italic

Light variants are the same hues darkened for AA contrast on white. Files: `Highlighting/*-{Dark,Light}.xshd`.

## Don't
- Add accent colours as system colours. Purple, amber and coral stay inside code.
- Use pill-shaped buttons, weights of 600 or more for headings, or drop shadows on cards.
- Put two green buttons in one view.
