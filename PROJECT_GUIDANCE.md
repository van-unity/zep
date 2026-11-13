# Project Guidance

Package-specific facts for `com.van-unity.zep`. These are deliberate
decisions that a newcomer cannot derive from the code fast enough to avoid
breaking them.

## Operating contracts

- `coding-standards.md` — how to structure and write code. Read it before
  non-trivial design or implementation changes.
- `styling-standards.md` — how the window is styled (USS/UI Toolkit). Read it
  BEFORE touching any `.uss` file, `styleSheets.Add`, or `style.*` write. The
  window uses exactly three stylesheets attached at the panel root; do not add
  sheets, do not add color literals, do not add static inline styles.

## Sidebar scroll ownership (deliberate, do not "fix")

The sidebar has exactly ONE scrollable element — the `zep-sidebar-scroll`
ScrollView in `SidebarWidget`. No sidebar child widget (folder trees, tags)
may scroll itself or show its own scroller: each sizes to its full content, and a
nested ScrollView (e.g. inside a child's ListView) must stay non-scrolling —
hidden scroller + `NestedInteractionKind.ForwardScrolling`, see the
`FolderTreeWidget` ctor. Anything that scrolls the sidebar (wheel, reveal-scroll,
drag auto-scroll) targets that one outer ScrollView.

The asset-area list/grid views are different: they DO own their scrolling.

The price of this contract is that the folder tree writes its own height each
frame so the one owning ScrollView sees the full content while the ListView gets
a viewport slice. That cost is known and accepted; it is not a defect to fix.
