# Zep — Manual QA Test Plan (Release Readiness)

> Package: `com.van-unity.zep` · Version under test: `0.2.1-preview.1` + `[Unreleased]`
> Baseline commit at authoring: `abf0887`
> Last updated: 2026-08-08

This is the **manual** release gate. The EditMode suite (`Tests/Editor`, ~56 files) covers the
domain, indexing, search grammar, and command layers headlessly. Everything in this document is
something a test **cannot** prove: real Unity import behaviour, UI Toolkit layout and focus, drag
gestures, native menus, domain reloads, editor restarts, cross-platform key handling, and how the
window actually looks.

---

## 0. How to use this document

**Pass/fail convention.** A case passes only if the expected result is observed *and* no exception,
error, or unexpected warning appears in the Console or `Editor.log` during the step. "It worked but
logged a NullReferenceException" is a **fail**.

**Priorities.**

| P | Meaning | Gate |
| --- | --- | --- |
| **P0** | Data loss, corruption, crash, or the feature is unusable. | Blocks release. Zero open P0. |
| **P1** | Core workflow broken or visibly wrong in a common path. | Blocks release unless explicitly waived by the product owner, in writing, in §36. |
| **P2** | Edge case, polish, cosmetic, or degraded-but-working. | Ship with known-issue note. |

**Always-on instrumentation.** Keep these open for the whole pass:

1. Unity **Console** with *Error Pause* off, *Clear on Play* off, all three filters on.
2. `~/Library/Logs/Unity/Editor.log` (macOS) / `%LOCALAPPDATA%\Unity\Editor\Editor.log` (Windows)
   tailing in a terminal.
3. The panel perf log at `$TMPDIR/ZepPerf.log` when running §28 (Performance).

```bash
tail -f ~/Library/Logs/Unity/Editor.log
```

**Bug report template.** Every failure gets:

```
[AREA-ID] one-line summary
Package SHA:      <from Packages/packages-lock.json>
Unity version:    <e.g. 6000.0.64f1>
OS:               <e.g. macOS 15.5 Apple Silicon>
Fixture:          <A / B / C>
Steps:            1. … 2. … 3. …
Expected:         …
Actual:           …
Console/log:      <paste stack trace or "clean">
Reproducible:     always / intermittent (n of m)
Severity:         P0 / P1 / P2
```

**Order of work.** Run §2 (smoke) first on every environment. If smoke fails, stop and report —
the rest of the pass is not meaningful. Then run §3 onward. §35 (Regression watch-list) is the
highest-value section on this release and must be run on **macOS and at least one of Windows/Linux**.

---

## 1. Test environments and fixtures

### 1.1 Environment matrix

Every row must be exercised. Columns marked *full* run the whole document; *smoke+* runs §2, §35,
and the sections marked **[cross-platform]** in their heading.

| # | Unity | OS | Depth | Notes |
| --- | --- | --- | --- | --- |
| E1 | 6000.0.79f1 (LTS) | macOS (Apple Silicon) | **full** | Release CI target; latest local full-suite evidence is 6000.0.64f1. |
| E2 | 6000.3.19f1 (LTS) | macOS (Apple Silicon) | **full** | Release CI target; latest local full-suite evidence is 6000.3.10f1. |
| E3 | 2022.3.76f1 | macOS | **smoke+** | Declared legacy minimum lane; latest local full-suite evidence is 2022.3.62f3. |
| E4 | 6000.0.79f1 (LTS) | Windows 11 | **full** | **Highest residual risk.** Keyboard, delete semantics, path separators, native menus, `ShowPopup` all differ. |
| E5 | 2022.3.76f1 | Windows 11 | **smoke+** | Legacy compatibility lane. |
| E6 | 6000.0.79f1 (LTS) | Linux (Ubuntu 22.04+) | **smoke+** | AltGr, GNOME Files conventions, file-watcher differences. |
| E7 | 6000.0.79f1 (LTS) | macOS (Intel) | **smoke+** | Only if an Intel Mac is available. |

Additionally record for each environment: Apple Silicon vs Intel, display scaling (100% / 150% /
200%), and Unity Editor theme (Personal **and** Professional — see §24 THM-11).

### 1.2 Fixtures you must build before starting

**Fixture A — Clean small project.** New empty 3D project + this package. ~50 assets. Used for
first-run, indexing, and any test that needs a predictable tree.

**Fixture B — Large real project.** A genuine production project, **under source control, on a
throwaway branch**. Target ≥ 20,000 assets and ≥ 2,000 folders, with several resolved registry
packages and at least one embedded package under `Packages/`. Used for performance, indexing,
scroll, and search-at-scale.

**Fixture C — Edge-case project.** Purpose-built to be hostile. Create all of the following:

| Fixture C content | Why |
| --- | --- |
| Names with spaces, `#`, `&`, `%`, `+`, `'`, `,`, `!`, `(` `)` | Path escaping, menu labels, USS selectors. |
| Names with dots: `my.config.data.asset`, `v1.2.3.prefab` | Extension parsing / name truncation. |
| Unicode names: `日本語.png`, `Ünïcødé.mat`, `emoji-🎮.txt` | Encoding, sorting, width measurement. |
| Very long name (200+ chars) and a very deep path (12+ levels) | Layout overflow, path-length limits (Windows). |
| Case-differing siblings: `Foo.png` and `foo.png` (macOS/Windows case-insensitive FS) | Case-only rename, collision. |
| A leading-dot file `.hidden` and a `Backup~` folder | Assets Unity refuses to import. |
| An empty folder, and a folder containing only an empty folder | Empty-state rendering, chevrons. |
| A folder with 5,000 flat files | Virtualization, marquee, select-all. |
| A `.fbx` model with sub-meshes/animations, a multi-sprite `.png` (sliced), an `AnimatorController`, a `SpriteAtlas` | Sub-asset expansion. |
| A prefab **variant** and its base prefab | Quick Look "Original Prefab", context menu. |
| Materials on Built-in, URP, **and** HDRP (whichever pipelines you can install) | Material thumbnails. |
| A `.unitypackage` file **outside** the project | External drop / interactive import. |
| A broken/missing-script `ScriptableObject`, a corrupted `.png` | Error paths. |
| An asset with a Unity **label** and a Zep **tag** on the same file, and a **folder** with both | `l:` vs `tag:` separation. |
| A symlinked folder inside `Assets/` (macOS/Linux) | Enumeration, infinite-loop guard. |
| A read-only file (chmod 444 / Windows read-only attribute) | Write-gate handling. |
| An embedded package with an `Editor/` folder and its own assets | Package browsing, read-only rules. |

**Fixture D — Version-control project.** Fixture A configured with Perforce or Git+Unity VCS
integration active (asset checkout required before edit). Used only for §33.

### 1.3 Before every environment's pass

- [ ] `git status` in the consuming project is clean; note the branch and SHA.
- [ ] Record the package SHA from the project's `Packages/packages-lock.json`.
- [ ] Delete `Library/com.van-unity.zep/` to force a cold index at least once per environment.
- [ ] Confirm `ProjectSettings/ZepSettings.asset`, `ZepTagSettings.asset`, and
      `Zep.TagAssignments.json` are either absent (cold) or in a known state.
- [ ] Confirm `ProjectSettings/Zep.Recents.json` does **not** exist (feature removed
      — see §34 PKG-06).

---

## 2. Smoke pass — run first, on every environment (~15 minutes)

If any P0 here fails, stop the pass and report immediately.

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SMK-01 | P0 | Open **Window > Zep** in Fixture A. | Window opens. No exception. Sidebar tree, asset area, top bar, and bottom bar all render. |
| SMK-02 | P0 | Click through 5 folders in the sidebar. | Asset area updates each time; breadcrumb tracks; no lag > 1 frame. |
| SMK-03 | P0 | Type `t:prefab` in search. | Results appear and are all prefabs. |
| SMK-04 | P0 | Select an asset, press **Space**. | Quick Look opens showing that asset. Press Space again — it closes. |
| SMK-05 | P0 | Create a folder (**Cmd/Ctrl+N**), name it, press Enter. | Folder is created on disk, appears in the view, is selected, and has a `.meta`. |
| SMK-07 | P0 | Press **Cmd/Ctrl+Z**. | The folder is restored to its original path with its original GUID. |
| SMK-08 | P0 | Switch to grid view, drag the thumbnail slider across its full range. | Tiles resize smoothly. No exception, no layout collapse. |
| SMK-09 | P0 | Open Settings (gear), switch the theme, close Settings. | The whole panel repaints in the new theme, including Quick Look if open. |
| SMK-10 | P0 | Force a script recompile (touch any `.cs` in the project). | Panel survives the domain reload: same folder, same selection, same view mode, no full-scan overlay, no exception. |
| SMK-11 | P0 | Close and reopen the window. | Restores to a valid state within 2 s. No exception. |
| SMK-12 | P0 | Quit Unity and relaunch. | Panel reopens (if it was docked), index loads from cache without a full scan, tags and themes intact. |

---

## 3. Install, first run, and indexing — INS

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| INS-01 | P0 | Add the package to a clean project via **Add package from git URL** using the README URL. | Package resolves, compiles with **0 errors and 0 warnings**, appears in Package Manager with the right displayName/version/description. |
| INS-02 | P0 | Add the package via a `Packages/manifest.json` dependency entry instead. | Same result; `packages-lock.json` records a resolvable SHA. |
| INS-03 | P1 | Read the Package Manager detail pane. | Description, author, license, and the three URLs (documentation / changelog / licenses) all render and open the right pages. |
| INS-04 | P0 | First open in Fixture B with `Library/com.van-unity.zep/` deleted. | "Building project index" overlay appears, reports progress, and dismisses when done. Editor stays responsive (you can still click other windows). |
| INS-05 | P1 | While INS-04's scan is running, click folders / type in search. | The panel either shows partial results consistently or blocks input clearly — it must never show a *wrong* result set or throw. |
| INS-06 | P0 | After INS-04, close Unity, relaunch, open the panel. | **No** full-scan overlay. Index hydrates from the persistent cache in well under the cold time. |
| INS-07 | P1 | With the editor closed, add 20 files and delete 10 from `Assets/` on disk. Relaunch. | The cache is invalidated or reconciled; all 20 additions are searchable and all 10 deletions are gone, without a manual refresh. |
| INS-08 | P1 | With the editor closed, edit `Packages/manifest.json` to add a package. Relaunch. | The new package's folder appears in the Packages tree and its assets are searchable. |
| INS-09 | P1 | With the editor closed, hand-edit `ProjectSettings/Zep.TagAssignments.json` (add one assignment). Relaunch. | The edited tag assignment is live in search and the tag filter immediately — the cache must not survive a tag-file change. |
| INS-10 | P1 | Corrupt the persistent cache: truncate `Library/com.van-unity.zep/index.v1.bin` to 100 bytes. Relaunch. | A **console warning**, then a clean automatic rebuild. No exception, no empty panel. |
| INS-11 | P1 | Delete `Library/com.van-unity.zep/` entirely while the editor is running, then trigger a refresh. | Panel recovers; next launch rebuilds cleanly. |
| INS-12 | P2 | Replace a `.meta` file in place so its GUID changes, while the editor is running. | The index swaps the GUID atomically — search returns exactly one result for that asset, neither a duplicate nor a ghost. |
| INS-13 | P2 | Open the panel in a project with `Assets/` containing a symlinked folder (Fixture C). | Enumeration completes; no infinite recursion; no hang. |
| INS-14 | P1 | Fixture C: confirm the leading-dot file and the `Backup~` folder. | They are **not** shown as project assets (Unity does not import them) and do not break enumeration. |
| INS-15 | P2 | Set `UseNativeFileSystemAssetSource = false` in `ProjectSettings/ZepSettings.asset`, restart. | The panel still indexes correctly via the AssetDatabase source (kill switch works). |
| INS-16 | P2 | Set `EnablePersistentIndexCache = false`, restart twice. | Every launch does a fresh scan; no stale results; no cache file written. |

---

## 4. Window lifecycle and docking — WIN

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| WIN-01 | P0 | Dock the panel next to Unity's Project window; use both. | Both work independently. No selection fights, no exception. |
| WIN-02 | P0 | Undock into a floating window, resize from 400×300 up to full screen. | Layout reflows at every size. No clipped controls, no overlapping text, no NaN/negative-size errors. |
| WIN-03 | P0 | Dock it, then **undock, re-dock, undock** three times in a row. | Every action in the panel still works afterwards — specifically: open an asset, switch theme, ping from Inspector. *(Historic bug class: detach nulls a provider that is never restored on re-dock.)* |
| WIN-04 | P0 | Maximize the panel with **Shift+Space** (Unity's maximize), then restore. | Panel renders correctly in both states; theme still applies; no exception. |
| WIN-05 | P1 | Shrink the window until the sidebar and asset area both become very narrow. | Chrome degrades gracefully (compact breadcrumb, no stray dividers). Nothing overlaps the bottom bar. |
| WIN-07 | P1 | Close the window with an asset selected, reopen. | Reopens at a valid location. Selection restore is acceptable either way, but must not throw or land on a deleted asset. |
| WIN-08 | P1 | Open a **second** Zep window (Unity allows duplicate EditorWindows via layout). | Either the second window works independently or the package cleanly refuses. It must not corrupt shared state or throw repeatedly. |
| WIN-09 | P1 | Save a Unity layout with the panel docked, switch layouts, switch back. | Panel restores with its sidebar width, view mode, thumbnail size, and split layout. |
| WIN-10 | P1 | With the panel open, enter and exit Play Mode. | Panel survives; no exception on either transition; no domain-reload assert. |
| WIN-11 | P1 | Toggle the sidebar with **Cmd/Ctrl+B** ten times rapidly. | Sidebar hides/shows each time; final state is consistent; the asset area reclaims the space. |
| WIN-12 | P2 | Drag the sidebar splitter to its minimum and maximum. | Clamps sensibly; the sidebar never becomes unusably narrow or eats the asset area. |
| WIN-13 | P2 | Change the OS display scale (100% → 200%) with the panel open. | Panel re-lays out; icons and text stay crisp; no overlap. |
| WIN-14 | P1 | Close the panel while Quick Look is open. | Quick Look closes with it. No orphan window left on screen. |
| WIN-15 | P1 | Close the panel, then check for leaked `StyleSheet` objects (`Resources.FindObjectsOfTypeAll<StyleSheet>()` count before/after, or Memory Profiler). | The runtime theme sheet is released. *(Session-scoped leak was a known deferred issue — confirm current behaviour and record the number.)* |

---

## 5. Sidebar and folder tree — SID

Contract under test: **the sidebar has exactly one scrollable element** (the outer
`zep-sidebar-scroll` ScrollView). No child widget scrolls itself or shows its own scroller.

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SID-01 | P0 | Expand and collapse folders in the Assets tree with the chevrons. | Children appear/disappear immediately. Chevron direction matches state. |
| SID-04 | P0 | Click a folder. | The folder row paints the **accent selection fill and border** (not just a bold label), matching a selected asset row. |
| SID-06 | P1 | Keyboard-navigate the tree: ↑ ↓ to move, → to expand, ← to collapse/go to parent. | Selection moves as in Finder/Explorer. Freshly expanded branches scroll into view even though they lay out over several frames. |
| SID-07 | P1 | Select a folder far down a long tree, then collapse everything above it. | The reveal-scroll follows the target folder **by identity** — it must not frame whatever row slid into the old position. |
| SID-08 | P1 | Externally (Finder/Explorer) add the first child to a previously-empty folder while the panel is open. | The folder gains an expansion chevron immediately, without a manual refresh. |
| SID-09 | P1 | Externally delete the last child of an expanded folder. | Chevron disappears; folder collapses or shows empty; no ghost rows. |
| SID-10 | P1 | Expand an empty folder, then externally add a child to it. | The new child appears without a manual refresh. |
| SID-11 | P1 | Externally change a **registry package** folder's contents. | The tree refreshes immediately, not on the next unrelated event. |
| SID-12 | P1 | Expand ~20 folders, force a script recompile. | Expansion state survives the domain reload exactly. |
| SID-13 | P1 | Expand ~20 folders, restart Unity. | On a **fresh session** the root is expanded (not treated as a saved collapsed tree); previously saved expansion is honoured where persisted. |
| SID-14 | P2 | Fixture C: browse the folder with the 200-char name and the 12-level-deep path. | Names truncate with ellipsis; the tree does not blow out horizontally; the row stays clickable. |
| SID-15 | P1 | Right-click a sidebar folder. | Folder context menu opens with the correct items enabled/disabled for that folder's writability (see §21). |
| SID-16 | P1 | Select a **package** folder under `Packages/`. | Browsable read-only; create/rename/delete/move are disabled or refuse cleanly. |
| SID-17 | P2 | Collapse the entire tree, then use search and cancel it. | The tree scrolls to and frames the containing folder of the result you picked (§35 REG-04). |

---

## 6. List view — LST

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| LST-01 | P0 | Open a folder with 30 mixed assets in list view. | Each row shows the correct icon, name, and metadata. Alternating **stripes** run edge to edge. |
| LST-02 | P0 | Open an **empty** folder in list view. | The striped list surface still renders across the whole area (no blank slab). The bottom bar reports 0 files. |
| LST-03 | P0 | Open a folder where the type filter excludes everything. | Same striped surface; bottom bar reports the filtered count. No leftover "No files match" banner (that banner was removed). |
| LST-04 | P0 | Fixture C: open the 5,000-file folder; scroll top to bottom with the wheel and by dragging the scrollbar. | Smooth virtualized scrolling. No blank rows, no duplicated rows, no stripe phase shift, no exception. |
| LST-05 | P1 | Scroll to the very bottom of a long list. | The last row is fully visible above the bottom bar — the bottom bar occupies its own row and must not overlap content. |
| LST-06 | P0 | Open a folder with **fewer rows than fit** the viewport. | No scrollbar; no negative-scroll artefacts; stripes fill the remaining height. |
| LST-07 | P0 | Click the inline chevron on a folder row (Finder-style inline expansion). | The folder expands **in place**; children render indented by one level (16 px); the chevron rotates. |
| LST-08 | P0 | Expand three nested folders inline, then select a deeply nested child. | Selection lands on the right item; ↑/↓ walk the flattened visible order, not the unexpanded order. |
| LST-09 | P0 | With an inline-expanded folder, select the folder **and** one of its expanded children, then delete. | Both are deleted. No "identity" error stopping the operation halfway. |
| LST-10 | P1 | Expand folders inline, then navigate away and back. | Expansion state for that pane is preserved (or cleanly reset) — never a half-flattened list. |
| LST-11 | P1 | With an inline expansion open, press **Space** on an expanded child. | Quick Look previews **that child**, not its parent. |
| LST-12 | P1 | With an inline expansion open, select all (**Cmd/Ctrl+A**). | The selection universe is the single visible list — every visible row including expanded children, nothing hidden. |
| LST-13 | P1 | Externally delete a file that is currently visible in an inline expansion. | The row disappears; the expansion stays coherent; selection reconciles to a valid neighbour. |
| LST-14 | P2 | Resize the window narrow enough to clip the metadata columns. | Columns degrade gracefully; the name column keeps priority. |
| LST-15 | P1 | Sort/refresh after an incremental import or rename. | Alphabetical order stays stable and correct; no visible re-sort flash of the whole list. |

---

## 7. Grid view — GRD

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| GRD-01 | P0 | Toggle list → grid → list from the bottom bar. | Both views render the same content and preserve the selection across the switch. |
| GRD-02 | P0 | Drag the thumbnail-size slider through its whole range in a folder of 200 assets. | Tiles reflow continuously. No exception, no flicker of empty tiles, no column-count thrash. |
| GRD-03 | P0 | Select a tile. | It paints the **same selection colour** as a selected list row (shared token, ~0.30 alpha accent). |
| GRD-04 | P0 | Rubber-band (marquee) drag across three columns of a grid. | Exactly the tiles the band touches are selected — verify against a slow, careful visual check at two different thumbnail sizes. |
| GRD-05 | P0 | Start a marquee, drag past the bottom edge of the view. | The grid auto-scrolls while dragging; the selection keeps collecting the tiles it passes over (no holes at the scroll boundary). |
| GRD-06 | P1 | Start a marquee, then press **Escape** before releasing. | The pre-drag selection is restored exactly. |
| GRD-07 | P1 | Marquee starting from inside an existing selection, and with **Shift**/**Cmd** held. | Additive/toggle behaviour matches the list view's modifier semantics. |
| GRD-08 | P0 | Keyboard-navigate the grid with ← ↑ → ↓ at several thumbnail sizes. | Moves by **on-screen** tile geometry. Home/End go to first/last; PageUp/PageDown move by a viewport. |
| GRD-09 | P0 | Expand sub-assets in grid view, then keyboard-navigate. | Movement follows on-screen tiles including sub-asset tiles — no drift to non-adjacent tiles. |
| GRD-10 | P1 | Grid view in the 5,000-file folder; scroll to bottom and back. | Virtualization holds; thumbnails resolve; memory does not climb without bound (§28). |
| GRD-11 | P1 | Grid view of a folder of textures/materials/models. | Real previews render (not just generic icons) once Unity has generated them. Placeholder → preview upgrade happens without a manual refresh. |
| GRD-12 | P2 | Shrink the pane until only one column fits. | Single column renders correctly; no clipped labels; no zero-width tiles. |
| GRD-13 | P1 | Grid view on an empty folder / fully-filtered folder. | Empty area renders cleanly; bottom bar count is right; no banner. |

---

## 8. Sub-assets — SUB

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SUB-01 | P0 | List view: click the chevron on a `.fbx` model with meshes/animations. | Sub-assets expand inline with a **Sub-asset** chip and correct per-sub icons. |
| SUB-02 | P0 | Grid view: expand the same asset. | Sub-asset tiles appear in the shared visible order, with rotated chevron and the Sub-asset chip. |
| SUB-03 | P0 | Select a sub-asset, press **Space**. | Quick Look previews **the sub-asset**, not the parent asset. |
| SUB-04 | P0 | Select a sub-asset. Check Unity's Inspector. | The Inspector shows that sub-asset. |
| SUB-05 | P1 | Select a sub-asset, then ping the parent from elsewhere. | The panel reveals and highlights the correct row. |
| SUB-06 | P1 | Ping a **sub-asset** from the Inspector (e.g. click a mesh reference). | The panel expands the parent, reveals and bloom-highlights the **sub row**, and does not also ping the parent. |
| SUB-07 | P1 | Expand a multi-sprite sliced texture. | All sprites listed with correct names and previews. |
| SUB-08 | P1 | Expand sub-assets, then re-import the parent asset with a different sub-asset set. | Expansion reconciles; stale sub rows disappear; no "sub-asset is no longer inside this asset" state left visible. |
| SUB-09 | P1 | Select a parent **and** one of its expanded sub-assets, then delete. | Behaviour is coherent: the parent is deleted (sub-assets cannot be independently deleted) with a clear result message — never a silent partial failure. |
| SUB-10 | P2 | Expand sub-assets, force a domain reload. | Expansion state is preserved or cleanly reset; never half-rendered. |
| SUB-11 | P1 | With sub-assets expanded, use **Cmd/Ctrl+A** then look at the count in the bottom bar. | Count matches what is visibly selected. |
| SUB-12 | P2 | Drag a sub-asset into a scene / an Inspector object field. | Works like a normal drag of that sub-object. |

---

## 9. Selection and keyboard navigation — SEL · **[cross-platform]**

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SEL-01 | P0 | Click an asset; click another. | Single selection replaces cleanly. Unity's Inspector follows. |
| SEL-02 | P0 | **Shift+click** a row 10 below the first. | A contiguous range of 11 is selected. |
| SEL-03 | P0 | **Cmd/Ctrl+click** individual rows. | Toggles each item in/out without clearing the rest. |
| SEL-04 | P0 | Shift+click, then Cmd+click to punch a hole, then Shift+click again. | Anchor/pivot behaves like Finder/Explorer: the second Shift+click extends from the correct pivot. |
| SEL-05 | P0 | **Cmd/Ctrl+A** with the asset area focused. | Selects every visible asset (respecting the current filter/search/expansion). |
| SEL-06 | P0 | macOS: **Cmd+A** with the panel focused but nothing clicked yet. | Still selects all — macOS routes ⌘A as an IMGUI command event, and the panel must handle both protocols. |
| SEL-07 | P0 | ↑ / ↓ / Home / End / PageUp / PageDown in list view. | Selection moves correctly and scrolls into view. |
| SEL-08 | P1 | Shift+↑ / Shift+↓. | Extends the selection from the pivot. |
| SEL-09 | P0 | With the asset area focused, type `hel` quickly. | Type-ahead jumps to the first asset starting with `hel`. Buffer resets after ~1 s of silence. |
| SEL-10 | P1 | Type-ahead a name containing a space (e.g. `my file`). | The space is treated as **text**, not as Quick Look. |
| SEL-11 | P0 | Press **Space** with no type-ahead active. | Quick Look toggles. |
| SEL-12 | P1 | Press **Shift+Space** / **Cmd+Space** / **Alt+Space**. | Not consumed by the panel — Shift+Space maximizes the Unity window, the others go to the OS. |
| SEL-13 | P1 | Select 5 assets, then change folder and come back. | Selection is restored or cleanly cleared — never a selection of assets not in view. |
| SEL-14 | P1 | Select assets, apply a search that excludes them, cancel the search. | Selection reconciles to something valid and visible; the picked result is scrolled into view. |
| SEL-15 | P1 | Select assets, then delete some of them externally. | Selection prunes to the survivors; no exception on the next keyboard move. |
| SEL-16 | P0 | Click a row, then press and hold the mouse and move slightly without leaving the row. | The selection survives the pointer movement (does not collapse to a single item on a multi-selection). |
| SEL-17 | P1 | Click an already-selected item in a multi-selection **without** modifiers, release without dragging. | Collapses to that single item on **mouse-up** (Finder/Explorer semantics), so a drag of the whole multi-selection is still possible. |
| SEL-18 | P1 | Tab through the panel from the search field. | Focus moves through the interactive elements in a sane order; the focused element shows a visible focus ring. |
| SEL-19 | P1 | Give focus to the sidebar, then to the asset area, using clicks and Tab. | Only one area shows an active focus state; keyboard commands route to the focused area. |
| SEL-20 | P2 | Select an asset, hide the sidebar (**Cmd/Ctrl+B**), show it again. | Selection and focus survive. *(Hidden ≠ blurred: a hidden element must not silently keep keyboard focus.)* |

---

## 10. Search — SCH

Grammar under test (from README/overview): plain terms **AND**; repeated `t:`/`type:` **OR**;
repeated `l:`/`label:` **OR**; those groups **AND**ed. Fields: `name:` `path:` `dir:` `ext:` `a:`/`area:`
`guid:` `glob:` `tag:` `t:`/`type:` `l:`/`label:`. `"phrase"`, `-exclude`, `!exact`, `field=value`.

### 10.1 Grammar

| ID | P | Query | Expected result |
| --- | --- | --- | --- |
| SCH-01 | P0 | `hero` | Matches assets/folders whose **name** contains `hero`. Does **not** match an asset merely because a parent folder is named `hero`. |
| SCH-02 | P0 | `hero controller` | Both terms must match (AND). |
| SCH-03 | P0 | `t:prefab` | Only prefabs. |
| SCH-04 | P0 | `t:prefab t:model` | Prefabs **or** models — and prefabs and models stay distinct even though Unity reports both as `GameObject`. |
| SCH-05 | P0 | `t:script` | C# scripts only — **not** `ScriptableObject` assets (complete kind aliases are exact). |
| SCH-06 | P1 | `t:pref` (incomplete) | Still useful as a partial type search while typing. |
| SCH-07 | P0 | `t:EnemyConfig` (a concrete custom type in Fixture C) | Matches instances of that concrete type. |
| SCH-08 | P0 | `t:ScriptableObject` | Matches real ScriptableObject assets via their base type; does **not** match arbitrary `.asset` files that are not ScriptableObjects. |
| SCH-09 | P0 | `t:folder` / `t:file` / `t:object` | Each returns the right kind. |
| SCH-10 | P0 | `l:gameplay` (a real Unity label) | Matches files **and folders** carrying that Unity label. |
| SCH-11 | P0 | `l:a l:b` | Union of both labels. |
| SCH-12 | P0 | `tag:MyTag` (a Zep tag) | Matches tagged assets. `tag:` and `l:` are independent — a Unity label named `MyTag` must not satisfy `tag:MyTag` and vice versa (Fixture C has both on one file). |
| SCH-13 | P0 | `hero t:prefab t:model l:boss` | `hero` AND (prefab OR model) AND label `boss`. |
| SCH-14 | P0 | `"player hud"` | Quoted phrase kept together. |
| SCH-15 | P0 | `"player hud" -old` | Phrase matches, `old` excluded. |
| SCH-16 | P0 | `!Bootstrap` | Exact-name match only. |
| SCH-17 | P0 | `name=Bootstrap` | Same exact-match semantics via `field=value`. |
| SCH-18 | P0 | `name:boot ext:cs` | Name contains `boot` AND extension is `.cs`. |
| SCH-19 | P1 | `ext:.cs` and `ext:cs` | Both work identically. |
| SCH-20 | P1 | `path:Editor` | Matches on the **project-relative path** (unlike a plain term). |
| SCH-21 | P1 | `dir:Prefabs` | Matches assets under an ancestor directory named `Prefabs`. |
| SCH-22 | P0 | `a:assets` / `a:packages` / `a:all` (and `area:` forms) | Restricts to the right area. |
| SCH-23 | P0 | `guid:<first 4 chars of a real GUID>` | Matches that asset. Also works for a **folder** GUID. |
| SCH-24 | P0 | `glob:"Assets/**/Editor/*.cs"` | Matches editor scripts at any depth. |
| SCH-25 | P0 | `glob:"Assets/**/Foo.cs"` where `Foo.cs` is directly in `Assets/` | `**/` matches **zero** folder levels too. |
| SCH-26 | P1 | `glob:"Assets/Scripts/Exact.cs"` (literal, no wildcards) | Resolves without a full-project scan (watch the perf log — it must not spike). |
| SCH-27 | P0 | `-t:scene name=Bootstrap ext:cs a:assets` | Combined exclusion + exact + extension + area, all applied. |
| SCH-28 | P1 | `t:""` | Does **not** broaden to the whole project. |
| SCH-29 | P1 | `ext:.` | Does **not** match extensionless assets. |
| SCH-30 | P1 | `tag:` with nothing after it | Does not claim a filter / does not return everything. |
| SCH-31 | P2 | `AND`, `OR`, `(a OR b)` typed literally | Treated as plain terms (documented non-support), not as operators, and not as an error. |
| SCH-32 | P2 | Fixture C: search a Unicode name (`日本語`), a name with `#`, and a name with an apostrophe. | All match correctly. |
| SCH-33 | P2 | Search with mixed case (`HERO` vs `hero`). | Case-insensitive by default. |

### 10.2 Search behaviour, scope, and exit

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SCH-40 | P0 | With Zep focused, press **Shift twice** using the default shortcut. Set a custom shortcut in **Settings → Shortcuts**, then try it, double-Shift, and **Cmd/Ctrl+F**, including from a text field. Reset the shortcut. | Only the configured shortcut opens the floating Search window. Cmd/Ctrl+F and native Find commands do not open it. Reset restores double-Shift. |
| SCH-41 | P0 | Type into search in Fixture B, one character at a time, watching the perf log. | Each keystroke is fast (no multi-ms per-keystroke rebuild) and allocates nothing significant. Results update live. |
| SCH-42 | P0 | Type a query, clear it with ✕, then type again and press Escape. | ✕ shows the empty-query state. Escape dismisses search while retaining the query. Neither changes browser navigation or selection. |
| SCH-43 | P0 | Use All / Assets / Packages / current-folder scope, including with an empty name query. | Scope is always reachable in the finder; results respect it. Restrictions inherited from the browser remain visible. |
| SCH-44 | P0 | Set a scope, cancel the search, search again. | The remembered scope is honoured. Visiting a place where that scope does not apply must **not** overwrite the preference. |
| SCH-45 | P0 | Single-click a result, then cancel; reopen and activate it with a double-click. | Single click highlights only. Cancel preserves browsing. Double-click selects and frames the chosen file or folder in its parent. |
| SCH-47 | P0 | Open a **tag view**, search inside it, then cancel the search. | You stay in the tag view with the tag still applied — the exit must not abandon the tag. |
| SCH-48 | P0 | In a **rooted folder split**, search with scope = All. | Results stay clamped inside that pane's root. |
| SCH-49 | P1 | Apply a tag filter with **no** search text. | Scope controls remain visible. |
| SCH-50 | P1 | Reveal a result, reopen the finder, and inspect its scope. | The current folder context is visible and no stale selection is activated. |
| SCH-52 | P1 | Type a query, then immediately reveal an asset via Inspector ping. | The live search ends cleanly and the ping target is revealed. |
| SCH-53 | P1 | Search, then rename a matching asset so it no longer matches. | The result list updates; selection reconciles. |
| SCH-54 | P1 | Search, then edit a tag definition. | Results rebuild correctly — base-type queries stay correct immediately after a tag edit. |
| SCH-55 | P1 | Search while a background index scan is running (kick one off with a mass import). | Results are consistent; late scan results do not clobber imports/deletions that arrived mid-scan. |
| SCH-56 | P1 | The search field's own glyphs (magnifier, ✕). | Render correctly in every theme and at both editor skins; the ✕ appears only when there is text. |
| SCH-57 | P1 | Type in search, then click outside the floating window. | Search dismisses, browsing stays unchanged, and reopening restores the query. |
| SCH-58 | P2 | Open Search from a narrow main panel. | The native finder sizes independently; the input, scope, type drawer and keyboard hints remain reachable. |
| SCH-59 | P2 | Paste a 500-character query. | No hang, no exception; results are whatever the grammar yields. |
| SCH-60 | P1 | Open the finder while using a split workspace. | Typing, filtering and previewing change neither pane. Reveal uses the existing primary-pane navigation behavior. |
| SCH-61 | P0 | Search for a shared name across a folder, prefab, texture, material and script. | Each row has a recognizable icon/thumbnail, readable type, parent path and highlighted name match. Exact names rank first. |
| SCH-62 | P0 | Choose Textures and Materials, type a name, then remove one type chip. Repeat using typed `t:` qualifiers and a quoted path. | Types combine with OR; other restrictions combine with AND. Chips and typed syntax stay synchronized, preserving quotes and unrelated text. |
| SCH-63 | P0 | Open from a browser type/tag filter; clear the finder filters. | The inherited restrictions are visible and individually removable; browser filters and selection do not change. |
| SCH-64 | P0 | Select a result with a click or ↑/↓, then press Space. Navigate between a texture, folder, material and script. Close with Space, Escape and ✕, reopening each time. | The existing asset Quick Look opens in front of Search; ↑/↓ inside it follows search results. The query and browser selection remain intact. Closing returns focus to the result; Space reopens with one press. There is no inline preview or Show/Hide button. |
| SCH-65 | P0 | Open Types…, search for common/custom types, use arrows and Space/Enter to toggle rows, then Tab through controls. Clear types with a name/path/tag query present. Close with Done, Esc and the backdrop; reopen rapidly. | The right drawer groups Common and Project types, shows checkmarks and a selected count, and traps focus while open. Changes apply immediately; Clear retains non-type restrictions. Dismissal restores focus to Types; reopening survives the closing animation. Empty type searches show useful feedback. |
| SCH-66 | P0 | Produce more than 50 results, scroll to the end and navigate with arrows. Import or rename a result while searching. | Every match remains reachable, only visible rows are realized, and a still-present selection is retained by path on refresh. |
| SCH-67 | P1 | Activate queries, reopen with an empty field, reuse a recent query, then clear history. Restart Unity. | Only activated queries are remembered, with at most six per user/project. Clear history persists; other projects are unaffected. |
| SCH-68 | P1 | Search with restrictive filters and a misspelling; then search for something absent. | Similar-name suggestions obey all explicit restrictions. No-match recovery removes filters or broadens an allowed scope. |
| SCH-69 | P1 | Repeat selection, hover, filter selection and preview in every theme, both editor skins and at 100%/200% display scaling. | Selection is obvious; controls use theme colors; text, icons and footer do not clip. |
| SCH-70 | P0 | Highlight a file and folder with one click or arrows, press Enter, then double-click each. Repeat while a new query is pending. | Single click/arrows/Enter do not change main-window selection. Double-click closes Search and selects the asset; a folder is highlighted in its parent. No Open/Reveal controls remain. Pending results cannot activate. |
| SCH-71 | P0 | Drag the search resize grip through 700×620, 400×400, 500×800, 1000×700 and 1000×400, with Quick Look open and closed. | Results use the full available area; filters, keyboard hints and the selected row remain reachable. Quick Look remains independent and fully interactive. |
| SCH-72 | P0 | Select the last result, resize repeatedly with multiple filters and Types… open, release the grip, then move the pointer. Close and reopen Search and resize again. | Query, filters and selection are preserved; the selected result stays visible. Resize stops on release, clamps to 400×400, and never turns into a window move. The drawer stays inside the card, with its own footer visible and choices scrollable. |
| SCH-73 | P0 | Type a multi-word query; select a result and press Space. Return to the query and type more spaces. Also type spaces in Types… and activate a filter button with Space. | Text fields retain normal Space input, filter buttons retain activation, and only Space focused on search results toggles Quick Look. Modified Space shortcuts remain untouched. |
| SCH-75 | P0 | Open Search with Double Shift, then repeat it from the query, Types drawer and Quick Look. Repeat using a configured shortcut. Reopen and press Escape; then reopen and click outside. | The opening shortcut toggles Search closed from each surface and dismisses its Quick Look. Escape still dismisses nested surfaces before Search. Outside clicks dismiss Search without stealing focus; the query is retained. No Esc button is shown. |

---

## 11. Type filter and scope controls — FLT

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| FLT-01 | P0 | Open the type-filter dropdown ("Filter by type"). | Lists the types present with counts. |
| FLT-02 | P0 | Pick a type. | The view filters to it; the bottom-bar count matches; the control shows an **active-filter indicator**. |
| FLT-03 | P0 | Pick "All Types". | Filter clears; indicator clears. |
| FLT-04 | P1 | With a filter active, change folders. | Filter persists per pane (or clears predictably) — document which and confirm it is consistent. |
| FLT-05 | P1 | With a filter active, combine with a search query. | Both apply (AND). |
| FLT-06 | P2 | Read the counts while scoped to one folder. | **Known limitation:** counts are project-wide, results are folder-scoped. Confirm the limitation still matches the README and is not actively misleading (i.e. results are correct even when counts are not folder-local). |
| FLT-07 | P1 | Filter to a type, then delete all assets of that type. | Filter yields an empty (but correctly rendered) view; the type may drop out of the dropdown. |
| FLT-08 | P1 | Filter in one split pane. | Filter is per-pane. |

---

## 12. Tags — TAG · **[cross-platform]**

Tag definitions live in `ProjectSettings/ZepTagSettings.asset`; assignments in
`ProjectSettings/Zep.TagAssignments.json`. Both are meant to be committed.

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| TAG-01 | P0 | Settings → **Finder Tags** → **Add Tag**, name it, pick a colour. | Tag appears in the list, in context menus, and in the sidebar Tags section. Written to `ZepTagSettings.asset`. |
| TAG-02 | P0 | Right-click an asset → tag submenu → apply a tag. | Tag colour dot appears on the asset; assignment is written to the JSON store. |
| TAG-03 | P0 | Apply the same tag to 20 assets in one multi-selection. | All 20 tagged in one operation. |
| TAG-04 | P0 | Apply a tag to a **folder**. | Works; `tag:` search finds the folder. |
| TAG-05 | P0 | Click the tag in the sidebar. | Tag view opens showing exactly the tagged assets (a live smart collection). |
| TAG-06 | P0 | Remove a tag from an asset via the context menu, and via **Remove All Tags**. | Both work; the tag view updates live. |
| TAG-07 | P0 | Rename a tag definition in Settings. | Every assignment follows the rename; `tag:NewName` works immediately; no assignments lost. |
| TAG-08 | P0 | Change a tag's colour. | All dots repaint immediately, in the main panel **and** any open split/Quick Look. |
| TAG-09 | P0 | Delete a tag definition that has 10 assignments. | Confirmation (if any) is clear; assignments are cleaned up; no orphan entries left in the JSON. |
| TAG-10 | P0 | **Reset Defaults** in the Tags section. | Restores the default tag set with a clear, reversible-or-confirmed action. Verify what happens to existing assignments and that it matches the confirmation text. |
| TAG-11 | P1 | Mark a tag as **favorite** (star), unmark it. | Star toggles; favorites are ordered first / exposed to the Ctrl+number shortcuts. The **inactive star** must not paint from the editor skin (it must follow the theme). |
| TAG-12 | P1 | **macOS only:** with assets selected, press **Ctrl+1** … **Ctrl+9**, and **Ctrl+0**. | Applies/toggles the corresponding favorite tag (Finder convention). **Ctrl+0** clears tags. Modifiers must be exactly Ctrl (no Cmd/Alt/Shift). |
| TAG-13 | P1 | **Windows/Linux:** press Ctrl+1…9. | The Finder shortcut is macOS-only — confirm the key is **not** swallowed and does something sane (nothing, or the platform equivalent). Verify no conflict with **AltGr** on a German/Nordic keyboard layout. |
| TAG-14 | P0 | Restart Unity. | All tag definitions, colours, favorites, and assignments survive exactly. |
| TAG-15 | P0 | Hand-corrupt `ZepTagSettings.asset` with a duplicate tag id, restart. | Duplicates/corrupt entries are dropped with a **console warning** — the file must **not** be silently rewritten with new identities. |
| TAG-16 | P1 | Delete `Zep.TagAssignments.json` with the editor closed; relaunch. | Definitions survive, assignments are empty, no exception. |
| TAG-17 | P1 | Two people's tag files merged by git (simulate a conflict resolution producing an unusual but valid file). | Loads cleanly or warns clearly. |
| TAG-18 | P1 | Tag an asset, then move it to another folder. | The tag follows the asset. |
| TAG-20 | P1 | Tag view with an active type filter. | Shows "No assets match this tag and filter." when appropriate; "No assets with this tag." when the tag is genuinely empty. |
| TAG-21 | P1 | Right-click → **Manage Tags…** | Opens the Settings drawer **deep-linked to the Finder Tags section**. |
| TAG-22 | P1 | Create a tag with an empty name / whitespace-only name / a duplicate name. | Rejected with a clear message ("Name is required." / "Name must contain at least one alphanumeric character."). |
| TAG-23 | P2 | Create ~30 tags. | The Settings tag list scrolls inside its own editor scroll; the sidebar Tags section still sizes to content inside the single sidebar scroll (§5). |
| TAG-24 | P1 | Undo (**Cmd/Ctrl+Z**) after a tag **definition** change. | Reverted — tag-definition changes participate in Undo. |

---

## 13. Split view — SPL

One auxiliary pane maximum. Sources: an Assets/Packages folder, or a tag.

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SPL-01 | P0 | Drag a folder from the sidebar into the asset workspace. | While dragging, a clear drop affordance ("Release to open in Split View"). On release, an auxiliary pane opens beside the primary. |
| SPL-02 | P0 | Drag a **tag** from the sidebar into the workspace. | A tag split opens as a live smart collection. |
| SPL-03 | P0 | Drag a source that is **already open** into the workspace. | The existing pane is focused and briefly highlighted; no second pane, no duplicate. |
| SPL-04 | P0 | With one auxiliary open, drop a **different** source onto it. | It **replaces** the auxiliary (still exactly two panes total). |
| SPL-05 | P0 | Click inside each pane. | The clicked pane becomes active; the accent edge along the top of the pane shows which is active. |
| SPL-06 | P0 | With the auxiliary active, use Back/Forward, search, type filter, Quick Look, create, rename, delete. | Every shared command acts on the **active** pane. |
| SPL-07 | P0 | Click a sidebar folder while the auxiliary is active. | Ordinary sidebar clicks target the **primary** pane (unrestricted), not the auxiliary. |
| SPL-08 | P0 | Ping an asset from the Inspector while the auxiliary is active. | External ping/reveal/selection targets the **primary** pane. |
| SPL-09 | P0 | In a folder split, use its dedicated Back/Forward and the breadcrumb. | Navigation may descend into children but **cannot go above the pane's root**. The breadcrumb is root-clamped. |
| SPL-10 | P0 | In a folder split rooted at a writable Assets folder: create, rename, move into it, drop an external file, drag a scene object in. | All valid — the pane is a real destination. |
| SPL-11 | P0 | In a **package** folder split: try to create/rename/delete/move. | Unity's read-only restrictions hold; commands disabled or refused with a clear message. |
| SPL-12 | P0 | In a **tag** split: try to drop an asset into it. | Refused — tag splits are read-only destinations. No move happens. |
| SPL-13 | P0 | In a tag split, open a **folder** result. | It navigates and activates the **primary** pane; the tag smart collection is unchanged. |
| SPL-14 | P0 | Drag the divider left and right. | Both panes resize live; neither collapses to zero; content reflows. |
| SPL-15 | P0 | Focus the divider with **Tab**, then use ← / →. | Incremental keyboard resizing works; the divider shows a focus ring. |
| SPL-16 | P0 | **Double-click** the divider. | Panes equalize. |
| SPL-17 | P0 | Click the auxiliary's ✕ close button. | The auxiliary closes; the primary expands to fill. The **primary has no close control**. |
| SPL-18 | P0 | Tab to the ✕ and press Enter/Space. | Closes — the close control is keyboard-focusable and operable. |
| SPL-19 | P0 | With a split open, force a script recompile. | Split source, order, active pane, per-pane presentation (list/grid, thumbnail size), and divider weights are all restored. |
| SPL-20 | P0 | With a split open, close and reopen the window. | Same restoration as SPL-19. |
| SPL-21 | P0 | With a folder split open, **delete that folder** from the primary pane. | The split closes or shows a clear invalid state — never a pane pointing at a dead path, never an exception. |
| SPL-22 | P1 | With a tag split open, delete that tag definition. | Same as SPL-21. |
| SPL-23 | P0 | Shrink the window until the split cannot fit. | The panel shows "Split View is unavailable." / "Widen the window or hide the sidebar to use Split View." and behaves sanely; widening restores it. |
| SPL-24 | P1 | Try to open a split in a window too narrow for it. | Refused with the same clear message — no half-rendered pane. |
| SPL-25 | P1 | Each pane has independent search, scope, filter, selection, history, view mode, thumbnail size. | Verify **each** of the seven independently. |
| SPL-26 | P1 | Rapidly open / replace / close / focus splits (interrupt the animations mid-flight). | Motion is interruptible; no stuck half-open pane; no exception. |
| SPL-27 | P1 | Drag a folder from the sidebar but release **outside** the workspace. | No split opens; no state change; the sidebar selection is unaffected. |
| SPL-28 | P1 | Start a sidebar drag and press Escape mid-drag. | Drag cancels cleanly. |
| SPL-29 | P1 | A restored layout that named the removed **Recents** source (hand-edit the saved state if needed). | That pane is dropped; other panes restore unchanged; no exception. |
| SPL-30 | P2 | Split + Quick Look + Settings drawer all open at once. | All three coexist; the drawer pushes rather than covering the workspace; Quick Look follows the active pane's selection. |

---

## 14. Create — CRT

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| CRT-01 | P0 | **Cmd/Ctrl+N** and **Cmd/Ctrl+Shift+N**. | Create-folder prompt opens for the current folder. |
| CRT-02 | P0 | Create a folder, type a name, press Enter. | Folder created **inside the currently browsed folder**, `.meta` generated by Unity, folder selected **in its parent** — the view must **not** navigate into the new folder. |
| CRT-03 | P0 | ➕ button → **New Folder / New Script / New Scene / New Material**. | Each creates the right asset type in the right folder with a valid default name. |
| CRT-04 | P0 | Create a **Scene** while the currently open scene has **unsaved changes**. | The open scene's unsaved edits are **NOT** lost. *(This was a P0 data-loss bug — verify explicitly and carefully.)* |
| CRT-05 | P0 | Right-click empty area → **Create** submenu. | Unity's full `Assets/Create/...` menu appears, including third-party `CreateAssetMenu` entries. |
| CRT-06 | P0 | Use a **third-party** `CreateAssetMenu` item (install any asset-store-style package that adds one, or add one to Fixture C). | The correct asset type is created, in the correct folder, with the correct default name. |
| CRT-07 | P0 | Fixture C: a custom create item whose leaf label collides with a built-in (e.g. `MyTool/Material`). | Creates **your** asset, not a Unity Material. |
| CRT-08 | P0 | Create an asset with a dotted name: `my.config.data`. | Full name preserved — not truncated at the first dot. |
| CRT-09 | P0 | Create in a folder that already has an asset of that default name. | Unity's uniquing applies (`New Material 1`), no overwrite. |
| CRT-10 | P1 | Create with an invalid name (empty, whitespace, `/`, reserved Windows name like `CON`). | Rejected with a clear message ("Please enter a valid asset name."), no partial file on disk. |
| CRT-12 | P1 | Undo a create, then **Redo**. | The exact asset is restored — same content, same `.meta`, **same GUID**. |
| CRT-13 | P0 | Create in a **package** folder. | Refused (read-only), with a clear message. |
| CRT-15 | P0 | Create in a folder split rooted at a writable Assets folder. | Created inside that pane's folder. |
| CRT-16 | P1 | Press **Escape** in the create prompt. | Cancels. No file on disk. Keys pressed immediately after are **not** swallowed by the close animation. |
| CRT-17 | P1 | Create a **Prefab Variant** from a prefab (context menu). | Variant created with the right base; "Original Prefab" shows in Quick Look. |
| CRT-18 | P1 | Inspector's **Create Material Preset** (Unity-driven, not our menu). | The new `.mat` is revealed and selected in the panel even though Unity pings it mid-import. |
| CRT-19 | P1 | Create 10 assets rapidly in a row. | All 10 exist, all named uniquely, panel view is consistent. |
| CRT-20 | P2 | Create while a background index scan is running. | The asset is not lost to the scan; it appears in the view and in search. |
| CRT-21 | P1 | Verify each create writes a status/toast message. | Message is accurate ("Zep created '…'"), not a stale or generic one. |

---

## 15. Rename — REN

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| REN-01 | P0 | Select an asset, press **F2**. | Inline edit field opens with the name (extension excluded from the selection). |
| REN-02 | P0 | **macOS:** press **Return** on a selection. | Begins rename (Finder convention). |
| REN-03 | P0 | **Windows/Linux:** press **Enter** on a selection. | **Opens** the asset (Explorer convention). |
| REN-04 | P0 | Type a new name, press Enter. | Asset renamed on disk with its `.meta`; GUID unchanged; references intact. |
| REN-05 | P0 | Press **Escape** during an inline rename. | The edit is **cancelled** — the old name is kept. *(The inner text element used to eat the Escape and commit instead — verify carefully.)* |
| REN-06 | P0 | Click elsewhere (focus out) during an inline rename. | Commits (Finder behaviour) — and it must commit the typed text, not the old text. |
| REN-07 | P0 | Case-only rename: `foo` → `Foo`. | Applied **on disk** (verify in Finder/Explorer, not just in the panel) and recorded as a real undo step. |
| REN-08 | P0 | Rename to an existing sibling's name. | Rejected with a clear message; no overwrite; no data loss. |
| REN-09 | P0 | Rename a **folder** with contents. | Folder and all children move correctly; references intact; sidebar tree updates. |
| REN-10 | P1 | Rename to a name with a dot: `config.v2`. | Full name preserved. |
| REN-11 | P1 | Rename to an invalid name (`/`, `:`, empty, 300 chars). | Rejected with a clear message. |
| REN-12 | P1 | Rename to a Unicode name. | Works; the row re-measures and re-sorts correctly. |
| REN-13 | P0 | Start a rename, then **scroll the row out of view**. | The edit cancels cleanly — the keyboard is never trapped in an invisible field. |
| REN-14 | P0 | Start a rename, then shrink the list (apply a filter/search) so the row is released. | Same as REN-13. |
| REN-15 | P0 | Start a rename, then trigger a refresh that removes the item from the data set. | Same as REN-13. |
| REN-16 | P1 | Rename, then **Cmd/Ctrl+Z**. | Reverted to the old name on disk. |
| REN-17 | P0 | Rename a file in a **package** folder. | Refused. |
| REN-19 | P1 | Rename with a multi-selection active. | Renames the focused/lead item only, with a clear indication of which. |
| REN-20 | P2 | Start a rename in the auxiliary pane while the primary is active. | Rename targets the active pane's selection only. |
| REN-21 | P1 | Rename a **sub-asset**. | Either supported correctly (sub-asset name changes, parent untouched) or clearly disabled — never a silent no-op. |

---

## 16. Duplicate and move — DUP

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| DUP-01 | P0 | Select an asset, **Cmd/Ctrl+D**. | Duplicate created next to it with a uniqued name and a **new GUID**. |
| DUP-02 | P0 | macOS: ⌘D delivered as a command event (menu-bar accelerator). | Still duplicates. |
| DUP-03 | P0 | Duplicate a **folder** with nested contents. | Whole tree duplicated; all `.meta` files new and valid; internal references remapped as Unity does. |
| DUP-04 | P0 | Duplicate a multi-selection of 10 assets. | All 10 duplicated, correctly named. |
| DUP-05 | P1 | Duplicate, then **Cmd/Ctrl+Z**. | Duplicates removed. |
| DUP-06 | P0 | Duplicate in a package folder. | Refused. |
| DUP-07 | P0 | Drag an asset onto a folder in the **asset view**. | Moved into that folder; `.meta` follows; references intact. |
| DUP-08 | P0 | Drag an asset onto a folder in the **sidebar tree**. | Same. |
| DUP-09 | P0 | Drag a multi-selection of 5 onto a folder. | All 5 moved. |
| DUP-10 | P0 | Drag a folder into one of its own descendants. | Refused cleanly (no move, no corruption, no exception). |
| DUP-11 | P0 | Drag an asset onto **its own current folder**. | No-op. The drag cursor must **not** advertise a move for a drop that would do nothing. |
| DUP-12 | P0 | Drag an asset onto **itself**. | No-op, no cursor affordance. |
| DUP-13 | P0 | Drag onto a **package** folder. | Refused (read-only). |
| DUP-14 | P0 | Drag onto a folder where a same-named asset already exists. | Unity's collision handling applies (rename or refuse) — never a silent overwrite. |
| DUP-15 | P1 | Move, then **Cmd/Ctrl+Z**. | Asset returns to its original path. |
| DUP-16 | P1 | Drag 3 assets where 1 will fail (e.g. a read-only file from Fixture C). | One aggregate status: "Moved 2 of 3 — 1 failed". Not three overwriting messages. |
| DUP-17 | P1 | Move a folder that is currently the active pane's location. | The pane follows the folder or shows a clear invalid state. |
| DUP-18 | P1 | Move an asset that is currently open in Quick Look. | Quick Look follows or closes cleanly. |

---

## 17. Asset deletion and Undo

| ID | P | Action | Expected |
| --- | --- | --- | --- |
| DEL-01 | P0 | Select a disposable asset, request Delete, then cancel the confirmation. Repeat and confirm. | Cancellation preserves the asset and selection. Confirmation removes the asset from the project; the dialog explains that deletion can be undone. |
| DEL-02 | P0 | Delete a disposable folder with files and metadata, then Undo and Redo. | Undo restores the exact contents and GUIDs at their original paths. Redo removes the same items again. |
| DEL-03 | P1 | Delete selected adjacent assets in the list and grid. | Deleted rows disappear and the surviving neighboring asset becomes selected. Unrelated rows remain. |
| DEL-14 | P0 | Request deletion of Assets and package roots. | Root targets are refused; their contents remain unchanged. |
| DEL-15 | P0 | Request deletion of a read-only package asset. | The target is refused and its contents remain unchanged. |

---

## 18. Compression (ZIP) — ZIP

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| ZIP-01 | P0 | Right-click one asset → **Compress "name"**. | With `PromptForMetaOnCompress = true`, the `.meta` prompt appears. |
| ZIP-02 | P0 | Choose "include .meta", complete. | A `.zip` is produced; opening it shows the asset **and** its `.meta`. Re-importing into a fresh project preserves the GUID. |
| ZIP-03 | P0 | Choose "exclude .meta", complete. | Archive contains the asset only. |
| ZIP-04 | P0 | Select 3 items → **Compress 3 Items**. | One archive with all three; status message names the archive. |
| ZIP-05 | P0 | Compress a **folder** with nested contents. | Whole tree archived with the correct relative entry paths (no absolute paths, no leading `/`). |
| ZIP-06 | P1 | Settings → Compression → uncheck **Ask about .meta files when compressing**. Compress again. | No prompt; the archive honours the **Include .meta files in archives** toggle. |
| ZIP-07 | P1 | Toggle **Include .meta files in archives** off, compress with the prompt disabled. | Archive has no `.meta` files. |
| ZIP-08 | P1 | Restart Unity, check both compression toggles. | Both persisted in `ProjectSettings/ZepSettings.asset`. *(These toggles were once fully disconnected — verify they are genuinely read AND written.)* |
| ZIP-09 | P1 | Cancel the save-file dialog. | No archive; no error; no partial file. |
| ZIP-10 | P1 | Compress to a path that already has an `Archive.zip`. | Overwrite is confirmed by the OS dialog; result is a valid archive. |
| ZIP-11 | P1 | Fixture C: compress assets with Unicode and `#`/space names. | Entry names are correct; the archive opens in the OS archiver and in `unzip`. |
| ZIP-12 | P1 | Compress a 500 MB folder. | Progress feedback; editor stays responsive enough; archive is valid. |
| ZIP-13 | P2 | Compress from a **package** folder. | Allowed (read is fine) or clearly refused — must be consistent. |
| ZIP-14 | P2 | Verify the archive with `unzip -t` / Windows Explorer. | Valid, no corrupt entries. |

```bash
unzip -l ~/Desktop/Archive.zip && unzip -t ~/Desktop/Archive.zip
```

---

## 19. Undo / Redo — UND

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| UND-01 | P0 | Perform each supported operation once, then **Cmd/Ctrl+Z**. | Each is individually reverted, correctly, on disk. |
| UND-02 | P0 | For each, then **Redo** (Cmd/Ctrl+Shift+Z; also **Ctrl+Y** on Windows/Linux). | Each is reapplied exactly, including GUIDs. |
| UND-03 | P0 | Do 10 mixed operations, then undo all 10 in a row. | The project returns to its exact starting state. Verify with `git status` — clean. |
| UND-04 | P0 | Then redo all 10. | Ends in the exact post-operation state. `git status` matches the pre-undo diff. |
| UND-05 | P0 | Check Unity's **Edit > Undo** menu label after each operation. | Names the right operation (not a generic or stale label). |
| UND-06 | P0 | Undo an operation using Unity's **Edit menu** rather than the keyboard. | Same result — the panel must not depend on its own key handler. |
| UND-07 | P0 | Perform an operation in the panel, then an unrelated operation in the Scene (move a GameObject), then undo twice. | Interleaving is correct: the scene edit undoes first, then the panel operation. |
| UND-08 | P0 | Do an operation, force a **domain reload** (recompile), then undo. | Still undoes correctly — the command history survives the reload via SessionState. |
| UND-09 | P0 | Do an operation, **restart Unity**, then undo. | Either it undoes correctly or the history is cleanly empty ("Nothing to undo") — never a half-applied revert. |
| UND-12 | P1 | Undo a multi-item move where some items failed. | Only the items that actually moved are reverted. |
| UND-13 | P1 | Drop external files including ones Unity refuses to import (dotfiles, `~` folders), then undo. | The undo history is **not erased**; later files in the same drop keep their undo records; the importable files are reverted. |
| UND-14 | P1 | Undo a tag **definition** change. | Reverted. |
| UND-15 | P1 | Press **Cmd/Ctrl+Z** with an empty history. | "Nothing to undo" — no exception. |
| UND-16 | P1 | Do an operation, then modify the same asset externally, then undo. | Either a clean revert or a clear refusal — never a corrupt file. |
| UND-17 | P1 | Undo rapidly (hold Cmd+Z). | No double-application, no exception, no lost step. |
| UND-19 | P2 | Undo across a Play Mode enter/exit boundary. | Behaves predictably; no exception. |

---

## 20. Drag and drop — DND

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| DND-01 | P0 | Drag an asset from the panel into the **Scene** view. | Instantiated as Unity does from the Project window. |
| DND-02 | P0 | Drag an asset from the panel into an **Inspector object field**. | Assigned correctly. |
| DND-03 | P0 | Drag an asset from the panel into Unity's own **Project window**. | Moves as expected. |
| DND-04 | P0 | Drag an asset from Unity's Project window into the panel's folder. | Moves into that folder. |
| DND-05 | P0 | Drag a **GameObject from the Hierarchy** onto a folder in the panel. | A prefab is created **in that folder** (not in the Assets root). |
| DND-06 | P0 | Drag a GameObject onto the asset **area background** of a folder pane. | Prefab created in that pane's current folder. |
| DND-07 | P0 | Drag files from Finder/Explorer onto a folder row. | Copied into that folder and imported. |
| DND-08 | P0 | Drag files from Finder/Explorer onto the asset area background. | Imported into the current folder. |
| DND-09 | P0 | Drag a **directory** from the OS into the panel. | The whole tree is copied in and imported. |
| DND-10 | P0 | Drag a **`.unitypackage`** from the OS into the panel. | Unity's **interactive Import Package** dialog opens (it must not be copied in as a raw file). |
| DND-11 | P0 | Drag a mix: 2 normal files + 1 `.unitypackage`. | Normal files are copied in; the `.unitypackage` opens the import dialog. Both paths complete. |
| DND-12 | P1 | Drag `foo.unitypackage.bak`. | Treated as a **normal file** (copied), not as a package. |
| DND-13 | P0 | Drag OS files that Unity refuses to import (a dotfile, a `~` folder) plus valid files. | Valid ones import; the refused ones are skipped; **the undo history is not erased**; the aggregate status is accurate. |
| DND-14 | P0 | Hover a drag over a folder row in **list view**, **grid view**, and the **sidebar tree**. | The target folder highlights in all three. |
| DND-15 | P0 | Hover over a folder where the drop would be a no-op (source's own folder, or the source itself). | **No** highlight and **no** move cursor. |
| DND-16 | P0 | Drag toward the bottom edge of the asset list. | Edge auto-scroll kicks in; the highlighted drop target is re-picked correctly **after** each scroll step. |
| DND-17 | P0 | Same at the top edge, and in grid view, and in the sidebar. | Auto-scroll works in all four; the sidebar scrolls its single outer ScrollView. |
| DND-18 | P1 | Auto-scroll to the very end of the list, keep dragging. | Stops cleanly at the boundary; no runaway scroll; no negative scroll offset. |
| DND-19 | P1 | Start a **divider/split** drag near an edge. | Split drags are excluded from edge auto-scroll. |
| DND-20 | P1 | Begin a drag then press **Escape**. | Cancelled cleanly; no move; original selection restored. |
| DND-21 | P1 | Begin a drag then release over an invalid target (Console window, empty desktop). | No-op, no exception. |
| DND-22 | P1 | Drag out of the panel into another application (Finder). | Behaves like Unity's Project window (copy-out or no-op) — no crash. |
| DND-23 | P1 | Drop into a **folder split** pane. | Lands in that pane's folder. |
| DND-24 | P1 | Drop external files while the panel is showing a **search result set**. | Clear behaviour: either into the current folder or refused — never into an unexpected folder. |
| DND-25 | P1 | Drop a 500 MB folder from the OS. | Progress feedback; editor recovers; all files imported. |
| DND-26 | P2 | Drop while the editor is compiling. | "Wait for the compile to finish." — refused cleanly, nothing half-imported. |
| DND-27 | P2 | Both drop handlers: verify the UI Toolkit path **and** the `OnGUI` fallback path are each exercised (they are mutually exclusive; force the fallback by dropping onto the region the fallback owns). | Both produce the same result. |

---

## 21. Context menus — CTX

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| CTX-01 | P0 | Right-click a single asset. | Open, Rename, Duplicate, Delete, Reveal in Finder / Show in Explorer, Compress, tag submenu, and Unity's Assets menu items — all present and enabled. |
| CTX-02 | P0 | Right-click a multi-selection. | Commands apply to the whole selection; labels pluralize ("Compress 3 Items", "Delete Items"). |
| CTX-03 | P0 | Right-click a **folder**. | Folder-appropriate items; Create submenu targets that folder. |
| CTX-04 | P0 | Right-click the **empty area** of a folder. | Create submenu targets the browsed folder; asset-specific items are absent or disabled. |
| CTX-05 | P0 | Right-click inside a **package** folder. | Write commands (Create/Rename/Delete/Duplicate-into) are disabled; read commands work. |
| CTX-07 | P0 | **Reveal in Finder** / **Show in Explorer**. | Opens the OS file browser with the asset selected. Correct label per platform. |
| CTX-08 | P0 | Open the **Create** submenu and confirm third-party entries appear. | Unity's real `Assets/Create` tree is reproduced, including hotkey hints and separators, in the right order. |
| CTX-09 | P1 | A Unity menu item that is contextually **disabled** (e.g. Create Prefab Variant on a non-prefab). | Shown disabled, not missing and not throwing. |
| CTX-10 | P1 | The **tag submenu**. | Lists all tags with colour swatches, shows checkmarks for applied tags, includes **New Tag…**, **Remove All Tags**, and **Manage Tags…**. |
| CTX-11 | P1 | **New Tag…** from the submenu. | Creates the tag and applies it to the current selection in one step. |
| CTX-12 | P1 | Right-click in the **auxiliary** pane. | The menu targets the auxiliary pane's selection, and the pane becomes active. |
| CTX-13 | P1 | Right-click a **sub-asset** row. | Sub-asset-appropriate items only; parent-only commands absent or disabled. |
| CTX-14 | P1 | Right-click with **nothing** selected. | A sane menu (Create + paste-like items); no exception. |
| CTX-15 | P2 | On a Unity version where the `UnityEditor.Menu` reflection API is unavailable/changed. | The panel logs a single clear warning and degrades to its built-in items — it must not spam the console or throw. **Test explicitly on 2022.3.** |
| CTX-16 | P2 | Open a context menu, then press Escape / click away. | Closes; the following keystroke is not swallowed. |

---

## 22. Quick Look — QLK

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| QLK-01 | P0 | Select an asset, press **Space**. | A frameless preview window opens, positioned near the panel, themed with the panel's theme. |
| QLK-02 | P0 | Press **Space** again / press **Escape** / click the **✕**. | All three close it. After each, pressing Space **once** reopens it (the toggle must not desync). |
| QLK-03 | P0 | With Quick Look open, arrow through a folder of mixed assets. | The preview follows the selection live, with no flicker and no stale content. |
| QLK-04 | P0 | With Quick Look open, change the selection from **Unity's Project window** or the Hierarchy. | The preview follows the external selection change. |
| QLK-05 | P0 | Preview each type: texture, sprite, material, model/FBX, prefab, prefab variant, scene, script, text asset, audio clip, animation clip, animator controller, ScriptableObject, shader, font, video, `.asmdef`, `StyleSheet`, `VisualTreeAsset`. | Each renders a meaningful preview or a clear "No live preview for this asset type" — never a blank window and never an exception. |
| QLK-06 | P0 | Preview a **folder**. | Shows the folder grid with counts and sizes. |
| QLK-07 | P1 | Preview a folder with 10,000 files. | Truncates with a clear indicator; does not hang. |
| QLK-08 | P0 | Preview an asset with sub-assets; use the **filmstrip**. | Filmstrip lists sub-assets; clicking one previews it. |
| QLK-09 | P0 | Select a **sub-asset** in the panel, press Space. | Previews the **sub-asset** (not the parent). |
| QLK-10 | P1 | Read the metadata panel for a texture / audio / model / material. | Dimensions, Mip Maps, Max Size, Import Type / Duration, Frequency, Channels, Load Type / Triangles, Vertices, Sub-meshes, Total Objects / Render Queue, Keywords — all correct against the Inspector. |
| QLK-11 | P1 | Preview a prefab **variant**. | "Original Prefab" names the correct base. |
| QLK-12 | P1 | Preview an asset with a broken/missing script (Fixture C). | "This asset's inspector failed to load" / "This asset's preview failed to render" — a clear message, no exception. |
| QLK-13 | P1 | Preview a corrupted `.png` (Fixture C). | Graceful failure message. |
| QLK-14 | P1 | Preview with **nothing selected**. | "Select something in the panel to preview it" / "Nothing to preview". |
| QLK-15 | P0 | Resize the Quick Look window; close and reopen. | Size persists (`QuickLook.Width` / `QuickLook.Height`). |
| QLK-16 | P0 | **macOS:** open Quick Look, then click on the main Unity window, other editor windows, and another application. | The frameless `ShowPopup` window behaves correctly: it stays visible when it should and does not get stuck above other applications. **Explicitly check z-order.** |
| QLK-17 | P0 | **Windows/Linux:** same as QLK-16. | Same. |
| QLK-18 | P0 | Open Quick Look, switch the theme in Settings. | The Quick Look window repaints in the new theme immediately. |
| QLK-19 | P1 | Open Quick Look, force a domain reload. | Closes cleanly or survives cleanly — no orphan window, no exception. |
| QLK-20 | P1 | Open Quick Look, then close the panel window. | Quick Look closes with it. |
| QLK-21 | P1 | Open Quick Look, then delete the previewed asset. | Preview clears or closes with a clear state — no exception, no stale render. |
| QLK-22 | P1 | Open Quick Look, then rename the previewed asset. | Title updates. |
| QLK-23 | P1 | With a **split** open, Space in each pane. | Previews the **active** pane's selection. |
| QLK-24 | P1 | Open Quick Look, then open the **Settings drawer** and press Space. | Space does not fight the drawer; the documented "Space closes a Quick Look opened before the overlay" behaviour holds. |
| QLK-25 | P1 | Open Quick Look on a 4K/Retina display and on a 100%-scale display. | Preview is crisp at both; no half-size or double-size render. |
| QLK-26 | P2 | Hold ↓ to scrub 100 assets rapidly with Quick Look open. | No leak, no queue backup, no exception; the final preview matches the final selection. |

---

## 23. Settings drawer — SET

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SET-01 | P0 | Click the **gear** in the toolbar. | The Settings drawer **pushes in from the left** (the browser is pushed, not covered by a scrim); the browser content stays live. |
| SET-02 | P0 | Click the gear again / the ✕. | The drawer slides out; the browser reclaims the width. |
| SET-03 | P0 | Use the two-pane **nav rail**: Theme, Finder Tags, Compression. | Each selects and shows the right section; the selected nav item is visibly active. |
| SET-04 | P0 | With the drawer open, interact with the browser behind it (click a folder, scroll). | Still works — the browser is not blocked. |
| SET-05 | P1 | Open the drawer, then the **Theme Creator** page. | The creator takes over as a page; the browser is correctly guarded while it is active. |
| SET-06 | P1 | Leave the Theme Creator, return to the drawer. | Returns to the previous section; no stale overlay state. |
| SET-07 | P1 | Open the drawer in a **very narrow** window. | Layout degrades gracefully; the drawer does not push the browser to zero width. |
| SET-08 | P1 | Open the drawer, force a domain reload. | Restores or closes cleanly; no half-open drawer; no exception. |
| SET-09 | P1 | Open the drawer with a **split** open. | Both coexist; the workspace resizes; splits are not destroyed. |
| SET-10 | P1 | Right-click → **Manage Tags…**. | Opens the drawer scrolled/switched to the **Finder Tags** section. |
| SET-11 | P1 | Rapidly toggle the gear 10 times. | The width animation is interruptible; final state is consistent. |
| SET-12 | P2 | Tab through the drawer's controls. | All controls (nav items, buttons, toggles, colour swatches, text fields, star buttons, remove buttons) are keyboard reachable with visible focus. |
| SET-13 | P2 | Every popup/dropdown, slider, and toggle in the drawer. | Label, text, and arrow all paint from the **theme**, not the Unity editor skin (§24 THM-10). |

---

## 24. Themes and Theme Creator — THM

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| THM-01 | P0 | Cycle through **all 16 selectable built-in themes** from the Settings dropdown. The Defaults table is a fallback token source, not a seventeenth picker entry. | Each applies immediately to the whole panel. Take a screenshot of each for the record. |
| THM-02 | P0 | For each theme, check **contrast**: row text on row background, selected row text, disabled text, chips, breadcrumb, bottom bar, status bar, search field, and empty-state copy. | All readable. Record any theme that fails as a P1/P2 contrast defect. *(Theme contrast defects were previously identified as open — re-check them all.)* |
| THM-03 | P0 | For each theme, the **selection** colour in list, grid, and sidebar. | One shared accent at the same alpha everywhere; a selected sidebar folder looks like a selected asset row. |
| THM-04 | P0 | Switch themes with a **split**, **Quick Look**, and the **Settings drawer** all open. | All surfaces repaint. |
| THM-05 | P0 | Settings → **Create theme…**. | The Theme Creator opens with default seeds. |
| THM-06 | P0 | Pick a seed colour, adjust, **Save theme**. | The theme is created and applied; it appears in the dropdown; "Theme saved". |
| THM-07 | P0 | Create a theme with save mode **UserPrefs**, and another with **ProjectAsset**. | UserPrefs theme lives in EditorPrefs (`…UserPrefsThemes`) and survives restart; ProjectAsset theme is written as a project asset and is committable. Both load on restart. |
| THM-08 | P0 | **Edit current** on a user theme; change a colour; save. | The live panel updates. |
| THM-09 | P0 | **Delete** the currently active user theme. | Falls back to the default theme for **both** the attached panel and any detached/Quick Look view; no exception; no unstyled flash. |
| THM-10 | P0 | In every theme, inspect Unity **composite sub-elements**: popup field (label + text + arrow), standalone sliders (thumbnail-size slider), the **inactive** ☆ favorite star, and any progress bar. | All paint from the theme, **not** from the Unity editor skin. |
| THM-11 | P0 | Switch the Unity Editor skin (Personal ↔ Professional) with the panel open, in a mid-brightness theme. | The panel does not change appearance (it owns its own colours). No skin leak. |
| THM-12 | P1 | Focus an empty text field (search, rename, tag name) in each theme. | The caret is visible against the field background. |
| THM-13 | P1 | Create a theme, restart Unity. | Theme survives and is still the active theme. |
| THM-14 | P1 | Create a ProjectAsset theme, delete its asset file externally, restart. | Falls back cleanly with a warning; no exception. |
| THM-15 | P1 | Create ~10 user themes. | The dropdown handles them; ids stay unique; no "Theme id must be non-empty." errors. |
| THM-16 | P1 | Create a theme with an empty / duplicate name. | Rejected with a clear message; "Save failed." path is exercised. |
| THM-17 | P1 | Theme creator's live preview card. | Reflects the seed and derived tokens accurately before saving. |
| THM-18 | P1 | Verify the panel uses exactly **three stylesheets** attached at the panel root (inspect via UI Toolkit Debugger). | Three, no more. No inline static styles, no colour literals. |
| THM-19 | P2 | Switch themes 20 times rapidly. | No leaked StyleSheets (check `Resources.FindObjectsOfTypeAll<StyleSheet>()` count), no exception. |
| THM-20 | P2 | Screenshot every theme in both list and grid view at two thumbnail sizes. | Attach to the release record for visual sign-off. |

---

## 25. Ping, reveal, and Unity selection sync — PNG

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| PNG-01 | P0 | Click an object field's **ping** in the Inspector for an asset. | The panel navigates to the asset's folder, selects it, scrolls it into view, and plays the ping highlight. |
| PNG-02 | P0 | Ping while a **search** is active. | The live search ends and the target is revealed. |
| PNG-03 | P0 | Ping while a **tag filter** is active. | The target is revealed (not a silent no-op after a wasted index refresh). |
| PNG-05 | P0 | Ping a **sub-asset** (e.g. a mesh inside an FBX). | The parent expands, the **sub row** is revealed and highlighted, and the parent's own ping is suppressed. |
| PNG-06 | P0 | Ping an asset that was just created and is still importing. | The panel retries and lands on it (does not silently drop the ping because the index has not caught up). |
| PNG-07 | P0 | Ping an asset in a **package** folder. | Revealed in the Packages tree. |
| PNG-08 | P0 | Select an asset in the panel. | Unity's `Selection.activeObject` updates; the Inspector shows it. |
| PNG-09 | P0 | Select an asset in Unity's Project window. | The panel's selection mirrors it (primary pane). |
| PNG-10 | P1 | Ping while a **split** is open. | The **primary** pane is targeted; the auxiliary is unchanged. |
| PNG-11 | P1 | Ping while the Settings drawer is open. | Either revealed behind the drawer or the drawer closes — consistent and clear. |
| PNG-12 | P1 | Select a multi-selection in the panel. | `Selection.objects` matches exactly. |
| PNG-13 | P1 | Ping the same asset twice in a row. | Highlight replays; no exception; no stuck highlight. |
| PNG-14 | P1 | Ping an asset that no longer exists. | No exception; no navigation to a dead path. |
| PNG-15 | P2 | Double-click an asset in the panel. | Opens it the way Unity's Project window does (scene loads, script opens in the IDE, prefab opens in prefab mode). |
| PNG-16 | P2 | **macOS:** ⌘↓ on a selection. | Opens the selection. |
| PNG-17 | P2 | **macOS:** ⌘↑ / **Win/Linux:** Alt+↑. | Navigates to the parent folder. |
| PNG-18 | P2 | Alt+← / Alt+→ everywhere; ⌘[ / ⌘] on macOS. | Back / Forward through the pane's history. |

---

## 26. Material thumbnails — MAT

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| MAT-01 | P0 | Grid view of a folder of materials, Built-in pipeline. | UE-style sphere thumbnails render, correctly lit, at every thumbnail size. |
| MAT-02 | P0 | Same on **URP**. | Renders correctly. |
| MAT-03 | P0 | Same on **HDRP**. | Renders correctly (or degrades to Unity's default preview with no error). |
| MAT-04 | P0 | Scroll a folder of 500 materials up and down repeatedly. | Thumbnails resolve progressively; the LRU keeps memory bounded (§28); no exception. |
| MAT-05 | P0 | Edit a material (change its albedo) in the Inspector. | The thumbnail updates without a manual refresh. |
| MAT-06 | P1 | Restart Unity; scroll the same folder. | Thumbnails load from the disk store fast (no full re-render). |
| MAT-07 | P1 | Delete the material thumbnail disk store, restart. | Rebuilds cleanly with no error. |
| MAT-08 | P1 | A material with a **missing shader** / an error shader (Fixture C). | Falls back to a default icon with no console spam. |
| MAT-09 | P1 | Material thumbnails in the **list** view. | Confirm the documented scope (assets-view only) — behaviour is consistent, not half-applied. |
| MAT-10 | P1 | Enter/exit **Play Mode** with a material folder open. | Render rig does not leak scenes/cameras/render textures. Check the Hierarchy for stray `MaterialThumb*` objects — there must be none in the user's scene. |
| MAT-11 | P1 | Check the scene Hierarchy and the Project for any `MaterialThumbSphere`, `MaterialThumbPlane`, `MaterialThumbEnvironment` leftovers. | None visible to the user, ever. |
| MAT-12 | P2 | Visual look tuning sign-off: compare against Unity's built-in material preview. | Product owner signs off on the look. |

---

## 27. Persistence and recovery — PER

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| PER-01 | P0 | Set up a full state: folder selected, folders expanded, grid view, thumbnail size set, filter active, split open, sidebar hidden. Force a **script recompile**. | Every one of those survives the domain reload. No full-scan overlay. |
| PER-02 | P0 | Same state, **close and reopen** the window. | Restores. |
| PER-03 | P0 | Same state, **quit and relaunch Unity**. | Restores what is meant to persist (document what is session-only). |
| PER-04 | P0 | Recompile **20 times in a row** (touch a script repeatedly). | No cumulative degradation: no growing memory, no duplicated event subscriptions, no double-refresh, no exception. |
| PER-05 | P0 | Recompile while a rename edit, a create prompt, a drag, and a Quick Look are each in progress (four separate runs). | Each aborts cleanly. No trapped keyboard focus, no orphan window, no partial file. |
| PER-06 | P1 | Delete `ProjectSettings/ZepSettings.asset`, restart. | Defaults applied; no exception. |
| PER-07 | P1 | Delete all panel state (settings + tag settings + assignments + Library cache), restart. | Clean first-run experience. |
| PER-08 | P1 | Restore from source control: `git checkout` an older `ProjectSettings/ZepTagSettings.asset`. | Loads correctly. |
| PER-09 | P1 | Two Unity instances open on **different** projects, both with the panel. | No cross-talk of settings, themes (UserPrefs themes are global — verify that is intended), or caches. |
| PER-10 | P2 | Change the project's location on disk (move the folder), reopen. | Panel recovers; paths resolve; no absolute-path leakage in the persisted state. |

---

## 28. Performance and memory — PRF

Run on **Fixture B** (≥ 20,000 assets). Capture numbers, not impressions.

| ID | P | Test case | Target / expected |
| --- | --- | --- | --- |
| PRF-01 | P0 | Cold index build (cache deleted). Record wall time from overlay appear to disappear. | Completes; editor remains interactive. **Record the number.** |
| PRF-02 | P0 | Warm start (cache present). Time from window open to first render. | Well under the cold time; target < 2 s. **Record.** |
| PRF-03 | P0 | Type a 10-character query one key at a time; watch `$TMPDIR/ZepPerf.log`. | No per-keystroke full rebuild. Per-keystroke cost and allocation are near zero. **Record ms and KB per keystroke.** |
| PRF-04 | P0 | Scroll a 5,000-item list from top to bottom continuously for 30 s. | Sustained smooth scrolling; no frame spikes; memory flat. |
| PRF-05 | P0 | Same in grid view with thumbnails. | Same; thumbnail LRU keeps memory bounded. |
| PRF-06 | P0 | Profile the panel **idle** for 60 s (Unity Profiler, Editor mode). | Near-zero per-frame allocation. No repeating GC spikes. **Record GC alloc/frame.** |
| PRF-07 | P0 | Externally add 1,000 files to `Assets/` while the panel is open. | The **incremental** external-change path runs (not a full rebuild). Editor recovers quickly. **Record the time.** |
| PRF-08 | P0 | Externally delete 1,000 files. | Same. |
| PRF-09 | P1 | Rapidly switch folders 50 times. | No cumulative slowdown; memory returns to baseline. |
| PRF-10 | P1 | Open/close the split 20 times. | No leak; memory returns to baseline. |
| PRF-11 | P1 | Open/close Quick Look 50 times over different asset types. | No leak; preview textures released. |
| PRF-12 | P1 | Switch themes 20 times. | No StyleSheet accumulation. |
| PRF-13 | P1 | Run a 30-minute realistic session, then compare Memory Profiler snapshots (start vs end). | No unbounded growth in panel-owned types. **Attach both snapshots to the release record.** |
| PRF-14 | P1 | With the panel open, measure the editor's idle CPU vs with the panel closed. | The delta is small enough to leave the panel docked all day. **Record both.** |
| PRF-15 | P2 | Compare the panel's folder-switch and search latency to Unity's own Project window on the same fixture. | The panel should be at least comparable; record where it is not. |

---

## 29. External change and concurrency — EXT

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| EXT-01 | P0 | With the panel open, add a file in Finder/Explorer, then focus Unity. | Appears in the panel and in search after Unity's refresh, without a manual panel refresh. |
| EXT-02 | P0 | Delete a file externally. | Disappears; selection reconciles; no ghost row. |
| EXT-03 | P0 | Rename a file externally. | Reflected correctly (as one rename, not a delete+add ghost). |
| EXT-04 | P0 | Move a folder externally. | Sidebar tree and asset view both update. |
| EXT-05 | P0 | `git checkout` a branch that changes 500 files while the panel is open. | The panel converges to the correct state; no exception; no stale entries. |
| EXT-06 | P0 | Trigger a mass import (drop 200 files) **while** a background index scan is running. | Neither clobbers the other; the final index has all 200. |
| EXT-07 | P1 | Delete files **while** a background scan runs. | The deletions survive the scan (an in-flight scan must not resurrect them). |
| EXT-08 | P1 | Edit a tag assignment while a scan is running. | Either applied correctly or forced to rebuild next load; never a lost assignment. |
| EXT-09 | P1 | Make an asset unreadable mid-scan (chmod 000 on macOS/Linux). | The incomplete scan is **discarded**, not cached; a warning is logged; next load rebuilds. |
| EXT-10 | P1 | Reimport All (`Assets > Reimport All`) with the panel open. | Panel survives and converges; no exception storm. |
| EXT-11 | P1 | Hand-edit a `.meta` file to add a flow-style `labels: [a, b]` line. | Indexed incrementally, matching what a full rebuild produces (`l:a` finds it). |
| EXT-12 | P1 | Add a Unity label from the Inspector while the panel is open. | `l:` search finds it immediately. |
| EXT-13 | P2 | Run a `.unitypackage` import concurrently with a third-party `CreateAssetMenu` create in the same editor tick. | **Known limitation:** the fallback create can misattribute the asset. Confirm the failure mode is at worst a wrong selection, never data loss, and that the README limitation is still accurate. |

---

## 30. Safety and data integrity — SAF

**Run every case in this section on a project under source control, with a clean `git status` before
each case and a `git status` check after.**

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| SAF-02 | P0 | Move a prefab that is referenced by a scene and by another prefab. Open both. | All references intact (GUID preserved). |
| SAF-03 | P0 | Rename a script that is attached to GameObjects in a scene. | Component references intact after the rename (file rename only; class name unchanged). |
| SAF-09 | P0 | Kill Unity during a tag write. Relaunch. | Tag file is intact (atomic write-temp-then-replace), not truncated. |
| SAF-10 | P0 | Kill Unity during a persistent-cache write. Relaunch. | Cache is either the old valid one or absent — never a truncated file that loads as garbage. |
| SAF-12 | P0 | Attempt every write operation on a **read-only** file (Fixture C). | Clear failure message; no partial state. |
| SAF-13 | P1 | Delete an asset that is currently open in the Scene view / Prefab mode. | Unity's own guards apply; the panel does not force an unsafe delete. |
| SAF-16 | P1 | Confirm the panel never writes into `Packages/` for a resolved registry package. | Verified. |

---

## 31. Platform-specific — PLT

Run the whole section on **each** of macOS, Windows, and Linux.

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| PLT-01 | P0 | Every shortcut in the table below, on this platform. | Each does exactly what the platform convention says. |
| PLT-02 | P0 | **macOS only:** ⌘A, ⌘D, ⌘⌫ delivered as IMGUI **command events** (via the native menu bar). | All three work even though the chord never becomes a `KeyDownEvent`. |
| PLT-03 | P0 | **Windows/Linux:** the same three as plain key events. | All three work. |
| PLT-04 | P0 | **Windows/Linux:** an **AltGr** keyboard layout (German, Polish, Nordic). Type `@`, `{`, `\`, `€` into the search field and into a rename field. | Characters are typed correctly. AltGr (= Ctrl+Alt) must **not** be misread as a Ctrl shortcut and swallow the keystroke. |
| PLT-05 | P0 | Reveal in Finder / Show in Explorer / the Linux equivalent. | Correct label and correct behaviour. |
| PLT-06 | P0 | Path handling with backslashes on Windows: browse, search `path:`, `glob:`, drag-drop, compress. | All correct; no mixed-separator paths shown in the UI or written to disk. |
| PLT-07 | P0 | Windows long paths (> 260 chars, Fixture C). | Handled or refused with a clear message — never a truncated write. |
| PLT-08 | P0 | Windows: a file locked by another process. | Clear failure; no corruption. |
| PLT-09 | P0 | Quick Look frameless window z-order on this platform (§22 QLK-16/17). | Correct. |
| PLT-10 | P1 | The native OS drag-and-drop from the platform's file manager. | Works (Finder / Explorer / Nautilus). |
| PLT-11 | P1 | High-DPI / fractional scaling (Windows 125%/150%, Linux fractional). | No blurry or misaligned chrome. |
| PLT-13 | P2 | Right-to-left or non-Latin system locale. | No layout break; no crash. |

### Shortcut reference under test

| Action | macOS | Windows / Linux |
| --- | --- | --- |
| Focus search | ⌘F (and the Find command) | Ctrl+F |
| Select all | ⌘A (key **and** command event) | Ctrl+A |
| Toggle sidebar | ⌘B | Ctrl+B |
| New folder | ⌘N / ⌘⇧N | Ctrl+N / Ctrl+Shift+N |
| Duplicate | ⌘D (key **and** command event) | Ctrl+D |
| Rename | F2, **Return** | F2 |
| Open selection | ⌘↓, double-click | **Enter**, double-click |
| Go to parent | ⌘↑ | Alt+↑ |
| Back / Forward | Alt+← / Alt+→, ⌘[ / ⌘] | Alt+← / Alt+→ |
| Quick Look | Space (bare only) | Space (bare only) |
| Close Quick Look | Space, Esc, ✕ | Space, Esc, ✕ |
| Undo / Redo | ⌘Z / ⌘⇧Z | Ctrl+Z / Ctrl+Shift+Z, Ctrl+Y |
| Favorite tags | Ctrl+1…9, Ctrl+0 clears | *(macOS only)* |

---

## 32. Accessibility and keyboard-only operation — A11Y

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| A11Y-01 | P1 | Complete this full workflow **without touching the mouse**: open the panel, navigate to a folder, create a folder, rename it, create an asset inside it, tag it, search for it, preview it, delete it, undo. | Every step is reachable by keyboard. |
| A11Y-02 | P1 | Tab through the entire panel from the top-left. | Focus order is logical; every interactive control is reachable; focus is always **visible**. |
| A11Y-03 | P1 | Focus the split divider and the auxiliary ✕ by keyboard. | Both reachable and operable (§13). |
| A11Y-04 | P1 | Confirm no control traps focus (rename field, search field, prompts, drawer, Quick Look). | Escape/Tab always gets you out. |
| A11Y-05 | P2 | Check every theme against a contrast checker for body text on background. | Record any theme below WCAG AA (4.5:1) as a known issue. |
| A11Y-06 | P2 | Use the panel at 200% OS scaling. | Usable; no clipped controls. |
| A11Y-07 | P2 | Verify tooltips exist on icon-only controls (gear, view toggles, close buttons, star, swatch, remove). | Each has a meaningful tooltip. |

---

## 33. Version control integration — VCS (Fixture D)

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| VCS-01 | P1 | With Perforce/Unity VCS active, rename an asset that is not checked out. | Unity's checkout prompt appears and the rename completes, or it fails with a clear message — never a silent local-only change. |
| VCS-02 | P1 | Move an asset that is not checked out. | Same. |
| VCS-04 | P1 | Delete an asset under version control, then Undo. | The VCS records the add/restore correctly. |
| VCS-05 | P2 | Git: after a full session of panel operations, `git status` shows a clean, intentional diff. | Yes (this is SAF-01 repeated at session scale). |

---

## 34. Package hygiene, docs, and release artefacts — PKG

| ID | P | Test case | Expected result |
| --- | --- | --- | --- |
| PKG-01 | P0 | Build the project with the package installed, on each Unity version. | **0 errors, 0 warnings** from package assemblies. |
| PKG-02 | P0 | Add `"testables": ["com.van-unity.zep"]` and run the EditMode suite. | All tests pass on every environment in the matrix. **Record the pass/fail/skip counts.** |
| PKG-03 | P0 | Confirm the package is editor-only. | Assemblies are Editor-platform restricted; a **player build** of Fixture A succeeds and contains none of the package's code. |
| PKG-04 | P0 | Verify every `.cs` has a `.meta` and every folder has a `.meta` after a fresh clone + open. | Unity generates none at open time (i.e. they are all committed). No "meta file missing" warnings. |
| PKG-05 | P0 | Fresh clone into a project, open, and check the Console during import. | Clean import, no warnings. |
| PKG-06 | P0 | Confirm `ProjectSettings/Zep.Recents.json` is never read or written. | Create the file by hand, use the panel for a session, confirm the file's mtime is unchanged and no Recents UI exists anywhere. |
| PKG-07 | P1 | Walk the README end to end and do exactly what it says. | Every instruction is accurate: install URL, menu path, shortcut table, search grammar table, test checklist, data locations, known limitations. |
| PKG-08 | P1 | Walk `Documentation~/overview.md` end to end. | Same. Every documented behaviour matches the build. |
| PKG-09 | P1 | Read `CHANGELOG.md` `[Unreleased]` against the actual build. | Every Added/Changed/Removed/Fixed entry is true of this build. Nothing shipped is undocumented; nothing documented is missing. |
| PKG-10 | P1 | Confirm the version number in `package.json` is bumped for this release and matches the CHANGELOG heading. | Consistent. |
| PKG-11 | P1 | Verify `LICENSE.md` is present and the licensesUrl resolves. | Yes. |
| PKG-12 | P1 | Confirm `Samples~` contains only real, working samples (or is removed). | No advertised-but-empty samples. |
| PKG-13 | P2 | Check `.npmignore` / package contents: no dev-only files (`AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `coding-standards.md`, `styling-standards.md`, `Documentation~/*Fix-Prompt*`) shipped to consumers if that is the intent. | Matches the intended shipping manifest — decide and record. |
| PKG-14 | P2 | Verify the GitHub Actions workflow passes on the release commit. | Green. |
| PKG-15 | P1 | Confirm no `Debug.Log` / perf-log spam in a normal session with perf logging disabled. | Console is clean during a 30-minute session. |

---

## 35. Regression watch-list — REG

**These are the highest-priority manual cases in this document.** Each corresponds to a fix that
landed recently and has *not yet been verified in a running editor*, or to a bug class that has
recurred. Run every one on macOS **and** on Windows or Linux.

| ID | P | What was fixed | How to verify |
| --- | --- | --- | --- |
| REG-01 | P0 | **Sidebar single-scroll ownership.** | §5 SID-02/03. Exactly one sidebar scrollbar, wheel forwards from every child. |
| REG-02 | P0 | **Sub-assets in list + grid.** | §8 SUB-01/02/03/11. Shared visible-order projection; Space resolves the sub. |
| REG-03 | P0 | **List inline folder expansion.** | §6 LST-07…LST-13. Especially: Space on an expanded child (LST-11), Cmd+A universe (LST-12), delete parent+child together (LST-09). |
| REG-04 | P0 | **Search-cancel sidebar framing.** | §10 SCH-45. The tree must *scroll* to the containing folder, not merely select it. |
| REG-06 | P0 | **Inline-edit Escape cancels (did not).** | §15 REN-05. Then REN-13/14/15 (the three release paths). |
| REG-07 | P0 | **Undo/redo review — 8 defects.** | The whole of §19, with special attention to UND-08 (domain reload) and UND-13 (non-importable files in a drop). |
| REG-08 | P0 | **Quick Look toggle desync / frameless window.** | §22 QLK-02 and QLK-16/17 (macOS `ShowPopup` z-order is a known open risk). |
| REG-09 | P0 | **Selection accent unification.** | §5 SID-04, §7 GRD-03, §24 THM-03. Sidebar folder selection paints the accent in **all 16 selectable built-in themes**. |
| REG-10 | P0 | **Theming skin leak.** | §24 THM-10 and THM-11. Popup label/text/arrow, standalone sliders, inactive ☆ — none may paint from the editor skin. Plus THM-12 (empty-field caret). |
| REG-11 | P1 | **Bottom bar de-floated; empty folders keep stripes.** | §6 LST-02, LST-03, LST-05. No 44 px compensation pad, no content under the bar, no removed banner. |
| REG-12 | P1 | **Drop-target highlight + no-op suppression.** | §20 DND-14/15 in all three surfaces. |
| REG-13 | P1 | **Drag-edge auto-scroll.** | §20 DND-16/17/18/19 in list, grid, and sidebar. |
| REG-14 | P1 | **Settings drawer push-in + deep link.** | §23 SET-01/04/10, and SET-05/06 (theme creator guard). |
| REG-15 | P1 | **External `.unitypackage` drop → interactive import.** | §20 DND-10/11/12, both handler paths (DND-27). |
| REG-16 | P1 | **Inspector ping reveal (dynamic framable window) + sub-asset ping.** | §25 PNG-01…PNG-06. Visual confirmation of the highlight, not just the dispatch. |
| REG-17 | P1 | **Create-asset focus + reveal (Unity-driven Create Material Preset).** | §14 CRT-18 and §25 PNG-06. |
| REG-18 | P1 | **Incremental external-change path (no full rebuild).** | §28 PRF-07/08 with the perf log open. |
| REG-19 | P1 | **Presentation-layer perf fixes (ghost-row type sweep per keystroke).** | §28 PRF-03. Per-keystroke allocation must be ~0. |
| REG-20 | P1 | **Search field paints its own glyphs; selections survive the pointer.** | §10 SCH-56 and §9 SEL-16/17. |
| REG-21 | P1 | **Top-bar redesign (SF icons, collapsing search, 24 px bar).** | §10 SCH-58, and a visual check of the whole top bar in every theme. |
| REG-22 | P1 | **Material thumbnails.** | The whole of §26, plus the look-tuning sign-off (MAT-12). |
| REG-23 | P1 | **Marquee index-math + begin-clamp + cancel restore.** | §7 GRD-04/05/06/07. |
| REG-24 | P1 | **Keyboard conventions (mac verified; Win/Linux NOT).** | The whole of §31, especially PLT-04 (**AltGr**) and PLT-02/03. |
| REG-25 | P1 | **USS refactor (29 → 12 sheets, 3 attached at root).** | §24 THM-18 plus a full visual eyeball of every screen in two themes. |
| REG-27 | P1 | **List striping (built-in AlternatingRowBackground) + short-list scroll bounds.** | §6 LST-01/04/06 — especially LST-06, negative `scrollableHeight` in short lists. |
| REG-28 | P1 | **UB fixes: create → select-in-parent, neighbour-select on delete, history realign, pivot/lead.** | §14 CRT-02, §17 DEL-03, §9 SEL-04, §25 PNG-18. |
| REG-29 | P1 | **Recents removal.** | §34 PKG-06 and §13 SPL-29. |
| REG-30 | P2 | **Search grammar corrections (`t:script` exactness, `**/` zero levels, `t:""`, `ext:.`, folder labels/GUIDs).** | §10.1 SCH-05, SCH-10, SCH-23, SCH-25, SCH-28, SCH-29. |

---

## 36. Release sign-off

Do not ship until every line below is checked and signed.

### Gate

- [ ] **Zero open P0** findings across the whole document.
- [ ] Every open **P1** is either fixed or explicitly waived below, with a name and a reason.
- [ ] All **P2** findings are recorded in `CHANGELOG.md` known-issues or in the README's
      "Known preview limitations".

### Coverage

- [ ] §2 smoke pass green on **all seven environments** (E1–E7).
- [ ] Full pass completed on E1, E2, and **E4 (Windows)**.
- [ ] Smoke+ pass completed on E3, E5, E6 (and E7 if hardware available).
- [ ] §35 regression watch-list run in full on macOS **and** on Windows or Linux.
- [ ] §30 (Safety) run in full on a source-controlled project, including all three SAF-08 kill-during-write runs.
- [ ] §28 (Performance) numbers recorded and attached.

### Artefacts to attach to the release record

- [ ] EditMode suite results per environment (pass / fail / skip counts).
- [ ] `PRF-01/02/03/06/07/14` measurements.
- [ ] Memory Profiler snapshots from PRF-13 (start and end).
- [ ] Screenshots of all 16 selectable built-in themes (THM-20), list and grid.
- [ ] The completed bug list with severities and resolutions.

### Documentation

- [ ] README's "Suggested test pass" still matches reality (PKG-07).
- [ ] `Documentation~/overview.md` matches the build (PKG-08).
- [ ] `CHANGELOG.md` `[Unreleased]` is accurate and complete, and the version is bumped (PKG-09/10).
- [ ] "Known preview limitations" updated with anything found here and not fixed.

### Sign-off

| Role | Name | Environments covered | Date | Result |
| --- | --- | --- | --- | --- |
| QA | | | | ☐ pass ☐ pass-with-waivers ☐ fail |
| Engineering | | | | ☐ pass ☐ pass-with-waivers ☐ fail |
| Product owner | | | | ☐ ship ☐ hold |

**P1 waivers** (one line each: ID, why it is acceptable to ship, who approved):

1.
2.
3.
