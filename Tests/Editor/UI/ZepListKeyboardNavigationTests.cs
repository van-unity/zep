using System.Collections;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // Finder's list-view keyboard contract, driven through the pane view (the
    // owner of the expansion keys) over a real controller: ↓/↑ walk every
    // visible row including expanded children, → expands the selected folder
    // then steps into it, ← collapses then walks out to the parent row.
    public sealed class ZepListKeyboardNavigationTests
    {
        private const string SubAssetFixturePath = "Assets/ZepListNavFixture.anim";

        [Test]
        public void RightOnANestedChildFolder_ExpandsIt()
        {
            using var rig = BuildRig();
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                Select(pane, "Assets/A");

                Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.True);
                Assert.That(pane.IsAssetFolderExpanded(AssetPath.From("Assets/A")), Is.True);

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(
                    pane.SelectedAssetPaths.Single().Value,
                    Is.EqualTo("Assets/A/Sub"),
                    "↓ from an expanded folder lands on its first child row");

                Assert.That(
                    view.MoveSelection(AssetSelectionMove.Right, extend: false),
                    Is.True,
                    "→ on a child folder row must expand it, exactly as on a top-level row");
                Assert.That(pane.IsAssetFolderExpanded(AssetPath.From("Assets/A/Sub")), Is.True);
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void DownWalksIntoEveryExpandedLevel()
        {
            using var rig = BuildRig();
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                Select(pane, "Assets/A");
                pane.SetAssetFolderExpanded(AssetPath.From("Assets/A"), true);
                pane.SetAssetFolderExpanded(AssetPath.From("Assets/A/Sub"), true);

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A/Sub"));

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A/Sub/Deep"));

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A/Sub/Child.asset"));
            }
            finally
            {
                view.Teardown();
            }
        }

        [Test]
        public void LeftCollapsesThenWalksOutToTheParentRow()
        {
            using var rig = BuildRig();
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                pane.SetAssetFolderExpanded(AssetPath.From("Assets/A"), true);
                pane.SetAssetFolderExpanded(AssetPath.From("Assets/A/Sub"), true);
                Select(pane, "Assets/A/Sub/Child.asset");

                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A/Sub"));

                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
                Assert.That(pane.IsAssetFolderExpanded(AssetPath.From("Assets/A/Sub")), Is.False);

                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A"));
            }
            finally
            {
                view.Teardown();
            }
        }

        // The reported "↓ does nothing on the main asset": a main row whose
        // sub-assets are open must step onto the first sub row, through the
        // pane view (where folder expansion is live) and not just the bare
        // substrate.
        [UnityTest]
        public IEnumerator DownFromAMainAssetWithOpenSubAssets_StepsOntoTheFirstSubRow()
        {
            CreateSubAssetFixture();
            using var rig = BuildRig(SubAssetFixturePath);
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                yield return ResolveFixtureSubAssets();
                var subs = SubAssetCatalog.GetResolved(SubAssetFixturePath);
                Assume.That(subs, Is.Not.Null.And.Length.GreaterThan(1));

                view.Q<ListAssetWidget>().ExpandSubAssets(SubAssetFixturePath);
                Select(pane, SubAssetFixturePath);

                SubAssetDescriptor committed = null;
                view.AssetsArea.SubAssetSelected += sub => committed = sub;

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(committed?.Key, Is.EqualTo(subs[0].Key));

                Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assert.That(
                    committed?.Key,
                    Is.EqualTo(subs[1].Key),
                    "a second ↓ must keep walking the open expansion");
            }
            finally
            {
                view.Teardown();
                AssetDatabase.DeleteAsset(SubAssetFixturePath);
            }
        }

        // A committed sub-asset holds no path, so the row selection is empty
        // by design and the widget highlight is the only thing painting it. A
        // re-published projection must not wipe that: a filtered folder builds
        // fresh entries on every publish, and the commit itself provokes one by
        // clearing the pane selection — which used to leave ↓ landing on a sub
        // row that nothing then painted, so the selection simply vanished.
        [UnityTest]
        public IEnumerator DownOntoASubRow_SurvivesAProjectionRepublish()
        {
            CreateSubAssetFixture();
            using var rig = BuildRig(SubAssetFixturePath);
            var pane = rig.State.PrimaryPane;
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<ListNavTestWindow>();
            window.position = new Rect(60f, 60f, 1000f, 700f);
            window.Show();
            window.rootVisualElement.Add(shell);
            try
            {
                for (var i = 0; i < 4; i++) yield return null;
                pane.TrySetFolderSelection("Assets");
                shell.Q<ZepPaneView>().ApplyPresentation(AssetViewMode.List, 64);
                // Any filter makes the browse projection publish a fresh list
                // instance every time, which is what exposed the wipe.
                pane.SetSelectedTypeFilter("ScriptableObject");
                for (var i = 0; i < 4; i++) yield return null;
                yield return ResolveFixtureSubAssets();

                var subs = SubAssetCatalog.GetResolved(SubAssetFixturePath);
                Assume.That(subs, Is.Not.Null.And.Length.GreaterThan(0));

                var list = shell.Q<ListAssetWidget>();
                list.ExpandSubAssets(SubAssetFixturePath);
                pane.SetSelectedAssetPaths(new[] { AssetPath.From(SubAssetFixturePath) });
                for (var i = 0; i < 3; i++) yield return null;

                SendKey(list.Q<ListView>(), KeyCode.DownArrow);
                for (var i = 0; i < 3; i++) yield return null;

                Assert.That(
                    shell.Q<ZepPaneView>().CommittedSubAsset?.Key,
                    Is.EqualTo(subs[0].Key),
                    "↓ steps onto the first sub row");
                Assert.That(
                    PaintedRowLabels(list),
                    Is.EqualTo(new[] { subs[0].Name }),
                    "and that row must still be the one painted as selected");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(SubAssetFixturePath);
            }
        }

        private static string[] PaintedRowLabels(ListAssetWidget list)
        {
            return list
                .Query<VisualElement>(className: ZepUiClasses.AssetRow)
                .ToList()
                .Where(row => row.ClassListContains(ZepUiClasses.RowSelected))
                .Select(row => row.Q<Label>()?.text)
                .ToArray();
        }

        // A row carries one chevron for two different expansions, so the
        // disclosure keys must read the same on a sub-asset row as on a folder
        // row: → opens a closed disclosure, → again steps into its first child.
        [UnityTest]
        public IEnumerator RightOpensSubAssetsThenStepsIntoTheFirstSubRow()
        {
            CreateSubAssetFixture();
            using var rig = BuildRig(SubAssetFixturePath);
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                yield return ResolveFixtureSubAssets();
                var subs = SubAssetCatalog.GetResolved(SubAssetFixturePath);
                Assume.That(subs, Is.Not.Null.And.Length.GreaterThan(1));

                var list = view.Q<ListAssetWidget>();
                Select(pane, SubAssetFixturePath);
                Assume.That(list.IsSubAssetExpanded(SubAssetFixturePath), Is.False);

                Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.True);
                Assert.That(
                    list.IsSubAssetExpanded(SubAssetFixturePath),
                    Is.True,
                    "→ opens a closed sub-asset disclosure");
                Assert.That(
                    pane.SelectedAssetPaths.Single().Value,
                    Is.EqualTo(SubAssetFixturePath),
                    "opening keeps the selection on the row, exactly as a folder does");

                SubAssetDescriptor committed = null;
                view.AssetsArea.SubAssetSelected += sub => committed = sub;
                Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.True);
                Assert.That(
                    committed?.Key,
                    Is.EqualTo(subs[0].Key),
                    "→ on an already-open disclosure steps onto its first child");
            }
            finally
            {
                view.Teardown();
                AssetDatabase.DeleteAsset(SubAssetFixturePath);
            }
        }

        // ← collapses an open disclosure, and only once it is closed does the
        // key fall through to the pane view's walk-to-the-parent-folder row.
        [UnityTest]
        public IEnumerator LeftStepsOutOfASubRowThenClosesTheDisclosure()
        {
            CreateSubAssetFixture();
            using var rig = BuildRig(SubAssetFixturePath);
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                yield return ResolveFixtureSubAssets();
                var list = view.Q<ListAssetWidget>();
                // Through the substrate: it owns the open set the disclosure
                // keys read and write (audit §6.1a).
                list.GetFirstAncestorOfType<AssetCollectionView>()
                    .SetSubAssetExpanded(SubAssetFixturePath, true);
                Select(pane, SubAssetFixturePath);

                Assume.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
                Assume.That(view.CommittedSubAsset, Is.Not.Null);

                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
                Assert.That(
                    pane.SelectedAssetPaths.Single().Value,
                    Is.EqualTo(SubAssetFixturePath),
                    "← on a sub row steps out to the row that owns it");
                Assert.That(
                    list.IsSubAssetExpanded(SubAssetFixturePath),
                    Is.True,
                    "stepping out of a child must not also close the disclosure");

                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.True);
                Assert.That(
                    list.IsSubAssetExpanded(SubAssetFixturePath),
                    Is.False,
                    "← on the owning row closes the disclosure");
                Assert.That(
                    pane.SelectedAssetPaths.Single().Value,
                    Is.EqualTo(SubAssetFixturePath),
                    "closing keeps the selection on the row it closed");
            }
            finally
            {
                view.Teardown();
                AssetDatabase.DeleteAsset(SubAssetFixturePath);
            }
        }

        // The substrate must decline every key it does not own, or the pane
        // view's folder expansion never gets its turn.
        [Test]
        public void DisclosureKeysOnARowWithNoSubAssets_StayUnconsumed()
        {
            using var rig = BuildRig();
            var pane = rig.State.PrimaryPane;
            var view = CreateListView(rig);
            try
            {
                Select(pane, "Assets/Z.asset");

                Assert.That(view.MoveSelection(AssetSelectionMove.Right, extend: false), Is.False);
                Assert.That(view.MoveSelection(AssetSelectionMove.Left, extend: false), Is.False);
                Assert.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/Z.asset"));
            }
            finally
            {
                view.Teardown();
            }
        }

        // A press on a row's disclosure chevron must not leave the collection's
        // deferred-selection window open. The chevron is a Button, so Unity's
        // Clickable captures the pointer and the matching release is routed
        // straight to it, never trickling back down to the collection that
        // opened the window. A wedged window buffers and drops every later
        // upward SelectionChanged: the rows keep repainting, but pane state,
        // Unity's Selection and the Inspector stop following the keyboard.
        [UnityTest]
        public IEnumerator ChevronClick_LeavesTheKeyboardDrivingPaneState()
        {
            using var rig = BuildRig();
            var pane = rig.State.PrimaryPane;
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<ListNavTestWindow>();
            window.position = new Rect(60f, 60f, 1000f, 700f);
            window.Show();
            window.rootVisualElement.Add(shell);
            try
            {
                for (var i = 0; i < 4; i++) yield return null;
                pane.TrySetFolderSelection("Assets");
                shell.Q<ZepPaneView>().ApplyPresentation(AssetViewMode.List, 64);
                for (var i = 0; i < 4; i++) yield return null;

                var list = shell.Q<ListAssetWidget>();
                var folderRow = FindRow(list, "A");
                Assume.That(folderRow, Is.Not.Null, "the Assets/A row must be realized");
                Click(folderRow);
                yield return null;
                Assume.That(pane.SelectedAssetPaths.Single().Value, Is.EqualTo("Assets/A"));

                var chevron = folderRow.Q(className: ZepUiClasses.AssetRowDisclosureButton);
                Assume.That(chevron, Is.Not.Null);
                Click(chevron);
                for (var i = 0; i < 3; i++) yield return null;
                Assert.That(
                    pane.IsAssetFolderExpanded(AssetPath.From("Assets/A")),
                    Is.True,
                    "the chevron still owns its click");

                var listView = list.Q<ListView>();
                SendKey(listView, KeyCode.DownArrow);
                yield return null;
                Assert.That(
                    pane.SelectedAssetPaths.Select(p => p.Value),
                    Is.EqualTo(new[] { "Assets/A/Sub" }),
                    "the keyboard move must still reach pane state after a chevron press");

                SendKey(listView, KeyCode.RightArrow);
                yield return null;
                Assert.That(
                    pane.IsAssetFolderExpanded(AssetPath.From("Assets/A/Sub")),
                    Is.True,
                    "→ reads the pane selection, so a swallowed move leaves it with nothing to expand");
            }
            finally
            {
                window.Close();
            }
        }

        // The sub-asset half of the same wedge: the chevron that opens an
        // expansion is the very press that used to break every key after it.
        [UnityTest]
        public IEnumerator ChevronClick_ThenLeftFromASubRow_StepsOutToTheParentRow()
        {
            CreateSubAssetFixture();
            using var rig = BuildRig(SubAssetFixturePath);
            var pane = rig.State.PrimaryPane;
            var shell = rig.BuildShell();
            var window = ScriptableObject.CreateInstance<ListNavTestWindow>();
            window.Show();
            window.position = new Rect(60f, 60f, 1000f, 700f);
            window.Focus();
            window.rootVisualElement.Add(shell);
            try
            {
                for (var i = 0; i < 4; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                pane.TrySetFolderSelection("Assets");
                shell.Q<ZepPaneView>().ApplyPresentation(AssetViewMode.List, 64);
                for (var i = 0; i < 4; i++)
                {
                    window.Repaint();
                    yield return null;
                }
                window.Repaint();
                yield return ResolveFixtureSubAssets();

                var list = shell.Q<ListAssetWidget>();
                var row = FindRow(list, "ZepListNavFixture");
                Assume.That(row, Is.Not.Null);
                Click(row);
                window.Repaint();
                yield return null;
                Click(row.Q(className: ZepUiClasses.AssetRowDisclosureButton));
                for (var i = 0; i < 3; i++)
                {
                    window.Repaint();
                    yield return null;
                }

                var paneView = shell.Q<ZepPaneView>();
                var listView = list.Q<ListView>();
                SendKey(listView, KeyCode.DownArrow);
                window.Repaint();
                yield return null;
                Assert.That(
                    paneView.CommittedSubAsset,
                    Is.Not.Null,
                    "↓ steps onto the first sub row of the open expansion");

                SendKey(listView, KeyCode.LeftArrow);
                window.Repaint();
                yield return null;
                Assert.That(
                    pane.SelectedAssetPaths.Select(p => p.Value),
                    Is.EqualTo(new[] { SubAssetFixturePath }),
                    "← steps out to the owning row, and pane state must hear about it");
                Assert.That(
                    paneView.CommittedSubAsset,
                    Is.Null,
                    "a main commit displaces the committed sub-asset");
            }
            finally
            {
                window.Close();
                AssetDatabase.DeleteAsset(SubAssetFixturePath);
            }
        }

        private static VisualElement FindRow(ListAssetWidget list, string label)
        {
            return list
                .Query<VisualElement>(className: ZepUiClasses.AssetRow)
                .ToList()
                .FirstOrDefault(row => row.Q<Label>() != null && row.Q<Label>().text == label);
        }

        // Unity's own order: PointerDown, MouseDown, PointerUp, MouseUp. The
        // pointer pair is what drives the deferred-emission window.
        private static void Click(VisualElement target)
        {
            var position = target.worldBound.center;
            Dispatch(target, PointerDownEvent.GetPooled(MouseEvent(position, EventType.MouseDown)));
            Dispatch(target, MouseDownEvent.GetPooled(MouseEvent(position, EventType.MouseDown)));
            Dispatch(target, PointerUpEvent.GetPooled(MouseEvent(position, EventType.MouseUp)));
            Dispatch(target, MouseUpEvent.GetPooled(MouseEvent(position, EventType.MouseUp)));
        }

        private static Event MouseEvent(Vector2 position, EventType type)
            => new Event { type = type, mousePosition = position, button = 0, clickCount = 1 };

        private static void Dispatch(VisualElement target, EventBase evt)
        {
            using (evt)
            {
                if (target.panel == null) return;
                evt.target = target;
                target.SendEvent(evt);
            }
        }

        private static void SendKey(VisualElement target, KeyCode keyCode)
        {
            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = keyCode,
                modifiers = EventModifiers.None
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private sealed class ListNavTestWindow : EditorWindow
        {
        }

        private static void Select(Core.State.ZepPaneState pane, string path)
            => pane.SetSelectedAssetPaths(new[] { AssetPath.From(path) });

        private static ZepPaneView CreateListView(ZepUiControllerRig rig)
        {
            var pane = rig.State.PrimaryPane;
            pane.TrySetFolderSelection("Assets");
            var view = new ZepPaneView(
                rig.State,
                pane,
                new UnityZepIconRepository(),
                rig.Controller);
            view.ApplyPresentation(AssetViewMode.List, 64);
            view.RefreshFromPane();
            return view;
        }

        private static ZepUiControllerRig BuildRig(string extraAssetPath = null)
        {
            var records = new[]
            {
                new AssetRecord("top-guid", "Assets/A/Top.asset", "Top", "ScriptableObject"),
                new AssetRecord("child-guid", "Assets/A/Sub/Child.asset", "Child", "ScriptableObject"),
                new AssetRecord("leaf-guid", "Assets/A/Sub/Deep/Leaf.asset", "Leaf", "ScriptableObject"),
                new AssetRecord("z-guid", "Assets/Z.asset", "Z", "ScriptableObject")
            };

            if (!string.IsNullOrEmpty(extraAssetPath))
            {
                records = records.Concat(new[]
                {
                    new AssetRecord(
                        "fixture-guid",
                        extraAssetPath,
                        System.IO.Path.GetFileNameWithoutExtension(extraAssetPath),
                        "ScriptableObject")
                }).ToArray();
            }

            return ZepUiControllerRig.Build(
                records,
                new[] { "Assets", "Assets/A", "Assets/A/Sub", "Assets/A/Sub/Deep", "Packages" });
        }

        private static void CreateSubAssetFixture()
        {
            Assert.That(SubAssetCatalog.ResolveOverrideForTests == null, Is.True,
                "the persistent fixture must use the real AssetDatabase resolver");
            SubAssetCatalog.ResetForTests();
            var main = new AnimationClip();
            AssetDatabase.CreateAsset(main, SubAssetFixturePath);
            for (var i = 0; i < 3; i++)
            {
                var sub = new AnimationClip();
                sub.name = "Sub" + i;
                AssetDatabase.AddObjectToAsset(sub, main);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(SubAssetFixturePath, ImportAssetOptions.ForceUpdate);
        }

        private static IEnumerator ResolveFixtureSubAssets()
        {
            SubAssetCatalog.GetOrRequest(SubAssetFixturePath);
            for (var frame = 0;
                 frame < 60 && SubAssetCatalog.GetResolved(SubAssetFixturePath) == null;
                 frame++)
            {
                yield return null;
            }

            Assert.That(
                AssetDatabase.LoadAllAssetRepresentationsAtPath(SubAssetFixturePath)
                    .Count(asset => asset != null),
                Is.EqualTo(3),
                "the persistent fixture must expose three usable sub-assets");
            Assert.That(SubAssetCatalog.GetResolved(SubAssetFixturePath)?.Length, Is.EqualTo(3),
                "the catalog must resolve all three persistent fixture sub-assets");
        }
    }
}
