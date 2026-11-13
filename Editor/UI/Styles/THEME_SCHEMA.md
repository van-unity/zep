# Zep Theme Schema

The theme system's contract. The authoritative token list is `Editor/Core/Theming/ZepThemeSchema.cs` (`RequiredVariables`, 39 tokens); this document explains it. `ThemeSystemTests` enforces parity between the schema, `ZepTheme_Defaults.uss`, and every `ZepTheme_*.uss`.

## The contract

- Every theme file (`ZepTheme_<Id>.uss`) is a **pure variable table**: one rule scoped to `.zep-root.zep-theme-<Id>`, defining all 39 tokens **in schema order**, and nothing else.
- `ZepTheme_Defaults.uss` defines the same 39 tokens on `.zep-root` as the fallback layer (active when a theme sheet fails to load; the `zep-theme-<Id>` class is still applied).
- Structural rules bind colors with `var(--zep-*)` in `Zep.uss`. Theme files never contain structural properties; structural rules never contain color literals.
- Runtime (user-created) themes are built by `RuntimeThemeStyleSheetBuilder` from the same schema; derived values come from `ThemeDerivation.cs`.

## Adding or removing a token

Touch all of these together, in one change, or the parity tests fail:

1. `ZepThemeSchema.cs` — the ordered list.
2. `ZepTheme_Defaults.uss` — fallback value, same position.
3. All 16 `ZepTheme_*.uss` files — hand-tuned per-theme values, same position.
4. `ThemeDerivation.cs` — derivation for runtime user themes.
5. This document.

Do not enumerate themes by class in structural rules (`.zep-theme-SomeLight .foo { … }`) as a substitute for a token — that pattern left a phantom selector behind once already.

## Tokens (schema order)

Surfaces and text:

| Token | Role |
|---|---|
| `--zep-bg-root` | window root background |
| `--zep-bg-pane` | pane background (sidebar, main, status) |
| `--zep-bg-asset-list-band` | alternating band in the asset list |
| `--zep-border-pane` | pane separators / top-bar border |
| `--zep-text-main` | primary text |
| `--zep-text-subtle` | secondary/dimmed text |

Inputs:

| Token | Role |
|---|---|
| `--zep-bg-input` | text/search field background |
| `--zep-border-input` | field border |
| `--zep-text-input` | field text |
| `--zep-text-input-placeholder` | placeholder text |

Buttons:

| Token | Role |
|---|---|
| `--zep-bg-button-primary` / `-hover` / `-active` | primary action button states |
| `--zep-text-button-primary` | primary button label |
| `--zep-bg-button-secondary` / `-hover` / `-active` | secondary button states |
| `--zep-text-button-secondary` | secondary button label |
| `--zep-bg-button-danger` / `-hover` | destructive button states |
| `--zep-text-button-danger` | danger button label |

Rows, selection, marquee:

| Token | Role |
|---|---|
| `--zep-bg-item-hover` | row/tile hover |
| `--zep-bg-item-selected-accent`, `--zep-border-item-selected-accent` | accented selection — the ONE pair every selectable surface uses (asset list rows, asset grid tiles, sidebar folder tree, tags) and also the drag-over drop target. The background is always `rgba(r, g, b, 0.3)` — same alpha in every theme, hue per theme; see `styling-standards.md` §3 |
| `--zep-bg-marquee`, `--zep-border-marquee` | marquee-selection rectangle |

Grid tiles have no tokens of their own. The tile *base* is deliberately transparent — the tile sits on the pane and paints nothing at rest — so the `--zep-bg-grid-item` / `--zep-border-grid-item` pair that once described it was removed from the schema on 2026-08-29; it had been unbound since the grid rework and every theme was hand-tuning a colour nothing read. Tile *selection* likewise binds `--zep-bg-item-selected-accent` / `--zep-border-item-selected-accent` like every other surface; the grid-specific `--zep-bg-grid-item-selected` / `--zep-border-grid-item-selected` pair was removed on 2026-07-29 — selection strength is one design decision, not a per-surface one.

Splitter, status:

| Token | Role |
|---|---|
| `--zep-bg-splitter-grip` / `-hover` / `-dragging` | pane splitter grip states |
| `--zep-bg-status-pulse` | status-bar activity pulse |

Overlays, chips, tags, toggles, icons:

| Token | Role |
|---|---|
| `--zep-bg-prompt-scrim` | modal scrim (prompt overlay, initial-scan overlay) |
| `--zep-bg-prompt-card`, `--zep-border-prompt-card` | modal/prompt card |
| `--zep-bg-info-chip` | tag/info chip background — bound by `.zep-row-chip` (asset rows, grid tiles) |
| `--zep-bg-tag-favorite-active` | favorite-tag toggle active state |
| `--zep-bg-toggle-group`, `--zep-bg-toggle-active` | view-mode toggle group / active segment |
| `--zep-tag-selected-border` | selected tag outline |
| `--zep-icon-tint` | monochrome icon tint |

Folder icons have no token of their own: `.zep-themed-folder-icon` binds `--zep-icon-tint`. The former `--zep-folder-icon-tint` was contractually required to equal `--zep-icon-tint` in every theme, so it was removed on 2026-09-03 along with the six "reserved" tokens that had no `var()` consumer (`--zep-bg-button-tonal`, `--zep-text-button-tonal`, `--zep-bg-dropdown`, `--zep-border-dropdown`, `--zep-bg-dropdown-item-hover`, `--zep-bg-dropdown-item-selected`) and `--zep-bg-item-selected`, whose consumers were press states (now `--zep-bg-button-secondary-active`) and one selection fill (now the shared accent pair). Saved user themes keep loading: `RuntimeThemeStyleSheetBuilder` emits only schema keys.
