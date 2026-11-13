# Zep overview

Open the panel from **Window > Zep**. It is an editor-only alternative project browser and can be docked beside Unity's built-in Project window.

## Layout

The sidebar contains project folders plus Tags. The main area contains the search and type-filter controls, list/grid view controls, the current asset collection, and a thumbnail-size control.

Search defaults to names and asset metadata. Type-filter counts describe the whole indexed project; displayed results still respect the current folder or search scope.

Browsing is independent of global search readiness. The panel observes the selected folder directly and lists it immediately, then reconciles authoritative Unity metadata and the global search index in the background; a folder observed that way becomes part of the panel's own index, so browsing into a folder that exists on disk but that Unity has not imported yet keeps you in it. Ordinary create, import, rename, move, tag, and delete flows update only their affected catalog paths. When a batch of external changes contains a path the panel cannot resolve, it applies the part it can and reconciles the rest in the background rather than waiting. A full walk of the project happens in exactly three cases — a first open with no usable cache, a cache the panel cannot reuse, and an explicit **Refresh** — and each of them announces itself with the scan overlay. Use the explicit **Refresh** command when an external tool leaves the project in an unknown state or recovery is required.

After a domain reload — the pause that follows a script edit — the panel reloads its index from its own cache instead of re-reading the project. It is usable as soon as that cache is in memory: the folder you were browsing and the selection you had are restored and stay restored. A compile can change only the *type* of an asset the cache already holds, never its path or its folder, so nothing is re-listed and the project is not re-scanned; the types that did change are rebuilt a few per frame over the following ticks and appear row by row as they land. Folder icons follow the same rule from the other side: a folder shows the empty-folder icon only once the panel knows it has no children, so a folder it has not read yet looks ordinary rather than empty. Directories Unity imports as a single asset — `.bundle`, `.framework`, `.xcframework`, and `.plugin` plugin bundles — are listed as one asset each and are not enterable, in the panel as in Unity.

## Search

Press **Shift twice** (the default shortcut) while Zep has focus, or click **Search** to open Quick Finder. Typing and changing filters affect only the finder. Results identify the asset with a thumbnail or icon, name, parent path, and type. All matching results remain reachable, with name relevance determining order.

Choose common type buttons or **Types…**, including without typing a name. Type choices combine with OR, while scope and other restrictions narrow those choices. The current scope and inherited browser filters stay visible. Click a filter chip to remove it or **Clear filters** to keep the name query and remove restrictions without changing the browser.

A single click or **↑/↓** highlights a result. **Double-click** closes Search and selects the asset in the main window; a folder is highlighted in its parent. Enter does not activate results. After selecting a result with **↑/↓** or a click, **Space** toggles the asset browser's shared Quick Look window. Use **↑/↓** inside Quick Look to browse search results without changing the browser's selection; closing it returns focus to the result. Space still types normally while editing the query. **Escape** dismisses a picker or Quick Look before closing the finder. Clicking outside both windows closes them, and its query is kept for reopening. The empty field offers the six most recent activated queries, stored per user and project; **Clear history** deletes them.

Drag the bottom-right grip to resize Quick Finder, or drag its heading to move it. Results use the full available area at every size. Filters and keyboard hints stay reachable, and resizing keeps the selected result in view. Quick Look remains a separate, independently resizable window. **Types…** opens a drawer from the right with searchable Common and Project type rows and clear checkmarks. Selections apply immediately. **Clear** removes only type restrictions; **Done**, Escape, or the dimmed backdrop closes the drawer. It uses the available width in compact windows.

Plain terms are combined with AND and match an asset's name, type, tags, and Unity labels; an unqualified term matches a GUID only as a prefix of at least four characters, so use `guid:` to search inside one. Repeated `t:`/`type:` filters form an OR group, repeated `l:`/`label:` filters form another OR group, and the type and label groups are ANDed with each other and with plain terms.

In **Settings → Shortcuts**, choose an available modifier/key combination and click **Use shortcut** to replace double-Shift. **Reset** restores double-Shift. The preference is saved per user, and only the selected shortcut opens Search. Choices exclude bindings used by Unity’s active shortcut profile or Zep. If a later Unity rebind creates a conflict, the custom shortcut stops firing and Settings identifies the conflict until you choose another binding or reset.

| Query | Behavior |
| --- | --- |
| `hero controller` | Matches `hero` AND `controller`. |
| `t:prefab t:model` | Matches prefabs OR models. |
| `hero t:prefab t:model l:boss l:art` | Matches `hero` AND either type AND either Unity label. |
| `t:EnemyConfig` | Matches a concrete custom type; `t:ScriptableObject` matches its real base type. |

Search supports:

- `t:` or `type:` for concrete indexed types, their real base classes, and common kinds including `prefab`, `model`, `script`, `folder`, `file`, and `object`; complete kind aliases match exactly while incomplete text remains a partial search;
- `l:` or `label:` for real Unity labels on files and folders;
- `tag:` for Zep project tags, which are intentionally distinct from Unity labels;
- `name:` for the filename without its extension, `path:` for the project-relative path, and `dir:` for an ancestor directory;
- `ext:` for a file extension;
- `a:` or `area:` with `assets`, `packages`, or `all`;
- `guid:` for file or folder GUIDs, matched against the first eight characters of a GUID — a shorter fragment may appear anywhere inside them, while a longer one must be a prefix, so a full GUID still matches. `glob:` takes project-relative glob patterns (`**/` matches zero or more folder levels). A glob pattern needs at least one run of three or more literal characters between its wildcards — a pattern such as `glob:"**/*a*"` is too broad to answer from the index and matches nothing.

Double quotes keep a phrase together. Prefix a term or field with `-` to exclude it. Prefix a value with `!`, or use `field=value`, for an exact match.

```text
"player hud" -old
!Bootstrap
-t:scene name=Bootstrap ext:cs a:assets
guid:2f3a glob:"Assets/**/Editor/*.cs"
```

While you type a single plain term, the field offers up to eight name completions beneath it. The list is ranked by closeness and tolerates small typos, so `plyaer` still suggests `Player`. Down and Up move through it, Enter or Tab accepts the selected completion, and Escape dismisses the list without clearing the field. Suggestions follow the pane's folder scope and type filter, and an accepted completion is quoted when the plain name would otherwise parse as a different kind of query. Suggestions are offered for a single unqualified term only — not for `t:`, `l:`, `tag:`, exclusions, or exact matches.

Scope controls appear whenever search text or a tag filter is active and further restrict the results to the options available in that pane: All, Assets, Packages, or the current folder. A folder split remains clamped to its own root even when a broader scope is selected.

The grammar intentionally follows Unity's Project-window search model rather than the full Unity Search QueryEngine. `AND`, `OR`, and parentheses are not Boolean operators, and serialized-property, reference, and size queries are not supported.

The search index is built in the background at editor startup only in projects where the panel's search has already been used. In a project that has never been searched, the first search builds the index when you run it.

## Split View

Drag a folder from either the Assets or Packages tree, or an individual tag, into the asset workspace to open it beside the primary browser. Dragging a source that is already open focuses and briefly highlights its pane. The current preview supports one auxiliary pane; dropping another source replaces the targeted auxiliary rather than opening a third pane. This limit is fixed for the current release and is not a Project Setting.

Each visible pane has its own search, scope, type filter, selection, navigation history, list/grid mode, and thumbnail size. Click or focus inside a pane to make it active. Shared Back, Forward, search, filter, Quick Look, create, rename, delete, tag, and other panel commands then act on that active pane. Ordinary sidebar clicks and external Unity selection, ping, or reveal requests continue to target the unrestricted primary pane.

A folder split is pinned to the folder it came from. Its dedicated Back/Forward controls and breadcrumb may enter descendants but cannot travel above that root. Folder panes backed by writable Assets folders remain valid move, import, create, and scene-object drop destinations; package folders keep Unity's existing read-only restrictions. Tag splits are live smart collections, not snapshots, and are read-only destinations. Opening a folder result from a Tag split navigates and activates the primary pane without changing the smart collection.

Drag the divider to resize adjacent panes. The divider can also receive keyboard focus: use the arrow keys for incremental resizing, or double-click it to equalize the panes. Each auxiliary pane's navigation row ends with a keyboard-focusable close button; the permanent primary pane has no close control. The active pane carries an accent edge along its top, and a tag pane's leading crumb carries the tag's colour dot. Split source, order, active pane, presentation, and divider weights are restored after domain reload and window reattachment when their sources are still valid.

## Common actions

- Click or use the arrow keys to navigate and select assets.
- Click the chevron on a folder row to expand it inline in list view, and the disclosure control on an asset that has sub-assets to show them. In list view sub-assets appear as indented child rows carrying a **Sub-asset** chip; in grid view they flow into the tile stream directly after their parent, and the parent and its sub-assets sit together in one outlined group container that wraps onto the following rows with a notch where it continues.
- Type while the asset collection is focused to jump to a matching name.
- Press **Space** to toggle Quick Look.
- Press **F2** to rename the current selection.
- Use **Cmd/Ctrl+A** to select all visible assets.
- Press **Shift twice**, or your configured shortcut from **Settings → Shortcuts**, to open Search.
- Use **Cmd/Ctrl+B** to toggle the sidebar.
- Use **Cmd/Ctrl+N** or **Cmd/Ctrl+Shift+N** to create a folder.
- Use **Cmd/Ctrl+D** to duplicate.
- Use the platform-standard Undo and Redo shortcuts for supported file commands.

Delete removes eligible project assets after confirmation and can be undone with Unity’s Undo command. File history privately retains the exact asset contents and `.meta` files needed for Undo/Redo, including edits made to created or duplicated assets before undoing them. It releases payloads when their history entries are discarded. Clearing `Library` or losing the editor session can make old file operations unavailable.

Undo refuses to overwrite a different item that now occupies the original path. If an external restore already put back the identical contents and metadata, Undo completes without replacing them.

## Tags and themes

Open Settings from the panel toolbar to create or edit tags and themes. Tags can be applied from context menus and used as filters. Project tag data is stored under `ProjectSettings` so it can be committed with the consuming project.

Built-in themes use package stylesheets. User themes are generated at runtime or stored as project assets, depending on the selected save mode. Theme changes apply to the main panel and Quick Look.

## Preview status

This package is pre-1.0. The editor window is the supported product surface; the C# type surface is not yet a stable API. Read the repository [README](../README.md) for installation, the current test checklist, supported versions, and known limitations.
