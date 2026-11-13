# Styling Standards — Zep

The source of truth for how this window is styled. Written for humans and AI agents. AI agents must treat it as an operating contract: apply these gates before editing any `.uss` file or any `style.*` write in C#.

The styling system was consolidated in July 2026 after a full audit (dead rules, cascade collisions, and inline-style bugs all traced back to sheet sprawl). Do not re-introduce the old shape. When this document and habit disagree, this document wins.

---

## 1. Architecture: exactly three stylesheets

All styling attaches at the panel root, in this order:

1. `Editor/UI/Styles/Zep.uss` — the single structural sheet. Layout, geometry, text, states, and color *bindings* (`var(--zep-*)`) for every widget in the window. Loaded by `ZepShellView.LoadStyleSheet()`.
2. `Editor/UI/Styles/ZepTheme_Defaults.uss` — the fallback variable table (all 39 `--zep-*` tokens on `.zep-root`). Loaded by `ZepShellView.LoadStyleSheet()`.
3. `Editor/UI/Styles/ZepTheme_<Id>.uss` — the active theme's variable table, scoped to `.zep-root.zep-theme-<Id>`. Added/swapped only by `ZepThemeSwitcher`.

The cascade story is one sentence: **three sheets on the root; specificity and in-file order decide winners.**

Hard rules:

- Do NOT call `styleSheets.Add` anywhere else. No per-widget sheets. The only exception is `ThemePreviewCard`'s runtime-built preview sheet (it previews a theme that is not active).
- Do NOT create new `.uss` files. New widget styles go into the matching `REGION` section of `Zep.uss`.
- Widgets style themselves exclusively through classes (`AddToClassList` with constants from `ZepUiClasses`).
- If a surface ever becomes its own `EditorWindow` (separate panel), only then does it justify its own sheet — attached once, at that window's root.

## 2. Rules inside Zep.uss

- The file is ordered: shell → shared primitives → top bar → sidebar → assets area → settings/overlays. Order encodes precedence. Add rules to the region they belong to; add same-specificity overrides *after* the rule they override.
- One rule per selector-group. Never two rules with the same selector in the file. Structural properties and `var()` color bindings live together in that one rule.
- Property order inside a rule: layout → box → text → paint → transitions.
- Pseudo-state rules (`:hover`, `:active`, `:checked`, `:focus`) sit immediately after their base rule.
- Do not restate UI Toolkit defaults (`flex-direction: column`, `position: relative`, `flex-shrink: 1`, `align-items: stretch`).
- Size a text input from **its own vertical padding**, never by forcing a `height` on the field root. Unity lays an *empty* single-line input's caret out at the top of the inner text element and only honours `-unity-text-align: middle-left` once the string is non-empty, so a content box taller than the line box shows the caret high and then jerks it down on the first keystroke. The prompt field forced `height: 32px` and gave the input no vertical padding — a 29px box around a 15px line — and put the caret 7.5px above centre. `.zep-settings-tag-name-field` and `.zep-creator-name-field` already size from padding; match them. `ThemeSystemTests.PromptTextField_WhenEmpty_KeepsTheCaretOnTheSameLineAsTypedText` pins it.
- Use the shared primitives before writing a new rule. `zep-flat-button` is every chrome-less `Button` (icon buttons, nav rail, breadcrumb segments, disclosure chevrons, segmented controls): it owns the margin/padding/border reset and the themed hover/press fill, so a consumer states only its geometry, text and any resting paint. `zep-sidebar-row` is every sidebar item row (the folder-tree rows, Favorites, Tags): one 24px pitch, one inset, one rounded state box; section headers are `SidebarSectionHeader` and carry `zep-sidebar-section-header` instead. The sidebar's selection is three focus states keyed on classes SidebarWidget/FolderTreeWidget toggle (`-pane-focused`, `-pane-keyboard`, `-tree-focused`), never on `:focus` of a composite. `zep-drop-target` is the one drag-over state class for every surface (asset list, grid, folder tree, Favorites). `zep-breadcrumb*` has a base rule; a host applies deltas only.
- The editor default theme paints `.unity-button:hover:enabled`, `:active:enabled`, `:focus:enabled` and `.unity-collection-view__item:hover:enabled` at specificity **0-3-0**. A class-only pseudo rule (`.x:hover`, 0-2-0) therefore never owns a button's or list item's hover/press fill, whatever the sheet order — it silently loses to the skin. Any rule that must win a hover/press/focus fill on a Unity `Button` or collection item is written as `.zep-root .x:hover:enabled` (0-3-0, ties the skin, wins by sheet order). A companion for a selected/active surface follows the same rule: `<subject>, .zep-root <subject>:hover:enabled`. Measured on 6000.3.10f1 by dumping `DefaultCommonDark_inter.uss`.
- A selected/active surface rule carries its own `:hover` companion in the same group (`<subject>, <subject>:hover { … }`), because the sibling `:hover` rule on the same subject has one more simple selector and would repaint the selected surface under the pointer. USS has no `:not()`, so this is the only shape that works; do not restate the reason at each site.
- ID selectors are allowed only to out-rank Unity's built-in editor theme (e.g. suppressing ListView item chrome) or where the theme system already keys on an id. Every ID selector carries a `/* WHY */` comment. Same for any `.zep-root` prefix used purely as a specificity boost — it defeats Unity's `.unity-button:hover` defaults; do not "simplify" it away.
- Keep `/* WHY */` comments when moving rules. Delete a WHY comment only when you delete its rule.

## 3. Colors and theming

- Every color is `var(--zep-*)`. The only literal allowed is fully transparent `rgba(0,0,0,0)` (and friends with alpha 0). No hex, no chromatic rgba, no `Color` literals in C# for element styling. There are exactly three documented exceptions, each marked with a WHY comment at its source — two in `Zep.uss`, one in C#:
  1. the theme-creator R/G/B slider draggers (channel-identity colors, intentionally unthemed);
  2. the toast card's translucent dark borders;
  3. `ZepTagColorPalette`'s eight `Color32` tag swatches (`Editor/UI/ZepTagColorPalette.cs`), applied to elements at 8 sites. Tag color is user-picked identity, not a palette role: it must not shift when the theme does, so it is deliberately outside the token vocabulary. Adding a fourth exception needs the same bar — an identity the theme may not vary — plus an entry here.
- The 39-token vocabulary is defined by `Editor/Core/Theming/ZepThemeSchema.cs` (authoritative, test-enforced) and documented in `Editor/UI/Styles/THEME_SCHEMA.md`.
- Adding a token is a schema change: update `ZepThemeSchema.cs`, `ZepTheme_Defaults.uss`, every `ZepTheme_*.uss` file (same order as the schema), the derivation in `ThemeDerivation.cs` (for runtime user themes), and `THEME_SCHEMA.md`. The parity tests will fail until all of them agree. Do not enumerate themes by class (`.zep-theme-SomeLight …`) as a substitute for a variable — that pattern rotted once already (a deleted theme left a phantom selector behind).
- Never put a color property in `transition-property` — a theme swap animates from the stale color and flashes. `ThemeSystemTests.AllThemeUss_NoColorDrivenTransitions_ExceptDocumentedPulse` enforces this. The single documented exception is the sidebar tags-row drop-pulse (`background-color`, 0.6s).
- Folder icons tint with `--zep-icon-tint` like every other monochrome icon (`.zep-themed-folder-icon` binds it). Do not add a folder-specific tint token; the one that existed was contractually forced to equal `--zep-icon-tint` and was removed on 2026-09-03.
- A token with no `var()` consumer in `Zep.uss` is deleted, not "reserved". Six reserved tokens sat unbound in 17 tables for two months before they were removed.
- Selection is ONE token pair, `--zep-bg-item-selected-accent` / `--zep-border-item-selected-accent`, for every selectable surface: asset list rows, asset grid tiles, sidebar folder tree, tags, and the drag-over drop target. Do not add a per-surface selection token — the grid had one (`--zep-*-grid-item-selected`) and it drifted until the same selection read differently in list and grid mode; it was deleted from the schema on 2026-07-29.
- That background is a *translucent* accent tint, and its alpha is a property of the design, not of the theme: every theme sheet declares it as `rgba(r, g, b, 0.3)`, and `ThemeDerivation` derives runtime user themes at the same `ThemeDerivation.SelectedAccentAlpha`. Hue is per-theme; opacity is not. Themes once ranged from an opaque solid to alpha 0.14, so a selection shouted in one theme and was invisible in another. `ThemeSystemTests.EveryThemeUss_DeclaresTheSameSelectionAccentAlpha` enforces it.

### 3.1 Unity's built-in controls must be re-bound sub-element by sub-element

A `TextField`, `PopupField`, `Toggle` or `Slider` is not one element — it is a small tree, and Unity's editor default theme paints most of that tree **from the editor skin**. Those declarations are real rules on the child, so they *block inheritance*: putting `color` on the control, or on its `__input`, does not reach a child Unity already coloured.

**Any sub-element whose paint you do not re-bind renders in the editor skin's colour and ignores the panel theme completely.** Read that failure mode carefully, because it is what makes this class of bug so easy to ship:

- it does **not** look like "one theme is wrong". The element resolves to the *same* skin colour in all 17 themes, so switching themes never reveals it;
- it only looks broken when the panel theme's polarity disagrees with the editor skin — a light panel theme under Unity's dark skin, or the reverse — which is exactly the combination the author is least likely to be sitting in;
- the offending rule looks finished. It has a `background-color` and a `border-color` bound to tokens. It is simply missing one property.

This shipped twice. The New Tag prompt's `PopupField` resolved its label to `rgb(196,196,196)` and its value text to `rgb(228,228,228)` in **every** theme — near-white text on a white card — because `.zep-prompt-field-label` declared no `color` and nothing targeted `.unity-base-popup-field__text` at all. The asset-grid zoom slider kept the skin's `rgb(153,153,153)` dragger on a `rgb(94,94,94)` tracker in every theme, because the whole scrollbar block is scoped to `.unity-scroller` and a standalone `SliderInt` is not inside one.

Sub-elements known to carry built-in paint — re-bind each one you render, or leave a `/* WHY */` saying the skin value is deliberate:

| Control | Sub-element | Bind |
| --- | --- | --- |
| any `BaseField` | `.unity-base-field__label` | `color` |
| `TextField` | `.unity-base-text-field__input` | `color`, `background-color`, `border-color` |
| `TextField` | `.unity-text-element--inner-input-field-component` | `color`, when the input's own `color` does not reach it |
| `TextField` | `.unity-base-text-field__input--placeholder` | `color` |
| `PopupField` / `EnumField` | `__input`, `__text`, `__arrow` | `color` on the first two; the arrow is a **skin image** — substitute the package's own chevron and tint it, do not tint the built-in one (a tint multiplies whatever grey the skin supplied, so it inverts wrongly on one of the two skins) |
| `Toggle` | `.unity-toggle__checkmark` | `background-color`, `border-color`, `-unity-background-image-tint-color` |
| `Slider` / `SliderInt` | `.unity-base-slider__dragger`, `__tracker` | `background-color`. The `.unity-scroller` rules do **not** apply — those are scoped to scrollbars |
| `Button` | — | already covered by `.zep-action-button`; a bare `Button` with no panel class is a skin button |

`ThemeSystemTests.EveryTheme_PaintsBuiltInControlSubElements_FromTokensNotTheEditorSkin` mounts these controls under every built-in theme and asserts the resolved colour equals the token its rule binds. Grepping the sheet cannot catch an omitted property, so the test resolves real styles instead. **Add a leg to `SkinLeakProbes` whenever a Unity composite control gains a themed surface.**

### 3.2 Legibility floors

A theme is a table of raw colours, and nothing in the token vocabulary stops a pair inside it from being unreadable. On 2026-07-29 sixteen of the seventeen tables had at least one such pair. Every theme now clears these WCAG 2.x contrast floors, and `ThemeSystemTests.EveryBuiltInTheme_MeetsTheLegibilityFloors` plus `…DerivedUserThemes_MeetTheLegibilityFloors_AcrossTheHueCircle` hold the line for the hand-written tables and for anything `ThemeDerivation` builds from a user's accent.

| Pair | Floor |
| --- | --- |
| `--zep-text-main` on pane / root / prompt-card | 4.5 |
| `--zep-text-input` on `--zep-bg-input` | 4.5 |
| `--zep-text-subtle` on pane, `--zep-icon-tint` on pane | 3.0 |
| button label on its base, `:hover` **and** `:active` fill | 3.0 |
| `--zep-text-input-placeholder` on `--zep-bg-input` | 2.5 |
| `--zep-bg-prompt-card` vs the scrimmed backdrop | 1.20 |
| `--zep-border-prompt-card` vs that backdrop / vs the card fill | 1.30 / 1.12 |

Two things that are easy to get wrong here:

- **Measure a translucent token composited over what it is actually painted on**, not in isolation. `--zep-bg-button-tonal` is an accent at alpha 0.16 over the pane; judging its label against the raw accent flatters it by a stop or more.
- **A modal card is never on the bare pane — the scrim is between them.** Separation is therefore card-vs-(scrim over pane). Comparing the card to the pane directly reports a failure on almost every theme (the two are *meant* to be close) and hides the real one: `ThemeDerivation` painted a *light* scrim at alpha 0.17 over a near-white pane, so derived light themes had a dialog with no edge at every hue.
- The `:hover` fill is the state that invites the click, so it must not be the least readable one. Six themes lightened a filled button on hover until the white label fell under 3.0.

When a floor fails, move the **smallest** amount of brightness on one token and keep its hue and saturation — the theme's identity lives in those. `ThemeColorMath.RelativeLuminance` linearises sRGB properly; do not reintroduce a raw-channel average (it inflates mid-tones enough to flip `ContrastingForeground` to the *worse* label).

## 4. Inline styles (`element.style.*`) in C#

Inline styles beat every USS rule. That power is the reason they are restricted.

Allowed (the value genuinely cannot live in USS):

- runtime-computed geometry: splitter positions, marquee rects, drag-ghost placement, measured widths;
- `display`/`visibility` toggles driven by data or interaction;
- animation writes (`opacity`, `translate`, `scale`) around USS transitions;
- dynamic images (`backgroundImage` from a loaded `Texture2D`).

Forbidden:

- static values set once (fixed paddings, colors, fonts, `alignItems`, `flexGrow` known at authoring time) — they belong in a class;
- any inline write to a property that a *class-conditional* USS rule also targets. The inline value silently wins forever (this shipped four real bugs: edit-mode "hide label" rules that never fired). If a state class must restyle a property, either drive the property by class everywhere, or clear the inline value (`style.<prop> = StyleKeyword.Null`) when the state class takes over — never leave both fighting.

## 5. Naming

- Component classes: `zep-<block>[-<element>][-<state>]`, declared as constants in `ZepUiClasses.cs`. No inline string literals for class names in widget code.
- `zep-` prefix is reserved for theme machinery (`zep-theme-<Id>`, `zep-themed-icon`), shared utilities (`zep-absolute-fill`, `zep-toast`), and the theme-creator (`zep-creator-*`).
- State modifiers are suffixes on the block (`-selected`, `-editing`, `-visible`), toggled with `EnableInClassList`.
- Element `name`s (`#id`) are for queries and the documented specificity cases, not general styling.

## 6. Gates before merging a styling change

1. Every class you added in USS has an `AddToClassList`/`EnableInClassList` consumer, and vice versa. Dead rules and dead constants are deleted together. A rule that would contain only UI Toolkit defaults is not written, and the class it would have served is not added.
1a. A new chrome-less `Button` carries `zep-flat-button`; a new drag-over surface toggles `zep-drop-target`; a new sidebar item row carries `zep-sidebar-row`, a new sidebar section is a `SidebarSectionHeader` plus a body. A hover/press fill on a Unity `Button` or collection item is a root-prefixed `:enabled` leg (§2).
2. `grep` the file for your selector: still exactly one rule per selector.
3. No new color literals; no color in `transition-property`.
4. Every rule you added that targets a `.unity-*` sub-element declares the paint it needs (§3.1), or carries a `/* WHY */` saying the skin value is deliberate. If you styled a Unity composite control, its probe leg exists in `ThemeSystemTests.SkinLeakProbes`.
5. No new `styleSheets.Add`, no new `.uss` file, no static inline styles.
6. `ThemeSystemTests` and `ZepShellViewTests` compile and pass (in-editor Test Runner).
7. `.meta` files: never hand-write them. New files get their `.meta` from the Unity editor.

Priority when rules conflict, same as `coding-standards.md`:

> Correctness > Simplicity > Clarity > Performance where it matters > Brevity > Consistency
