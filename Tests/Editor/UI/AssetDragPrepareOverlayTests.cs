using System;
using System.Collections;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // The themed drag ghost follows the cursor for the whole drag. It used to
    // do that by writing style.left/top on every DragUpdatedEvent, which dirties
    // layout under the panel root each time and defeats the overlay's own
    // DynamicTransform usage hint (that hint only accelerates
    // translate/scale/rotate). The box is anchored once at drag start and every
    // later move is a delta on style.translate instead.
    public sealed class AssetDragPrepareOverlayTests
    {
        [UnityTest]
        public IEnumerator FolderListDrag_WithinPane_ShowsNamedContentGhost() =>
            AssertContentGhost(AssetViewMode.List, true);

        [UnityTest]
        public IEnumerator FolderGridDrag_WithinPane_ShowsNamedContentGhost() =>
            AssertContentGhost(AssetViewMode.Grid, true);

        [UnityTest]
        public IEnumerator TextAssetListPreview_ShowsNamedGhostAndFollowsDragEvents() =>
            AssertContentGhost(AssetViewMode.List, false);

        [UnityTest]
        public IEnumerator TextAssetGridPreview_ShowsNamedGhostAndFollowsDragEvents() =>
            AssertContentGhost(AssetViewMode.Grid, false);

        private static IEnumerator AssertContentGhost(AssetViewMode mode, bool folder)
        {
            using var assets = new TempAssetScope();
            var name = folder ? "Dragged Folder" : "Dragged.txt";
            var path = assets.FolderPath + "/" + name;
            if (folder) AssetDatabase.CreateFolder(assets.FolderPath, name);
            else assets.CreateTextAsset(name, "drag ghost fixture");
            var sharedState = ZepControllerHost.SharedViewModel;
            var previousActive = sharedState.ActivePane;
            ZepControllerHost.Shared.Core.Refresh();
            ZepWindowDocking.Clear();
            DragAndDrop.PrepareStartDrag();
            var window = ZepPaneWindow.Create(
                ZepPaneSource.RootedFolder(AssetPath.From(assets.FolderPath)), "Ghost Fixture");
            try
            {
                window.position = new Rect(100f, 100f, 640f, 480f);
                window.Show();
                var deadline = EditorApplication.timeSinceStartup + 5;
                while (window.Pane == null && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.That(window.Pane, Is.Not.Null);
                var root = window.rootVisualElement;
                var view = root.Q<ZepPaneView>();
                view.ApplyPresentation(mode, 100);
                for (var frame = 0; frame < 4; frame++) yield return null;
                var collection = root.Q<AssetCollectionView>();
                Assert.That(collection, Is.Not.Null);
                var row = collection.Query<VisualElement>().ToList().FirstOrDefault(element =>
                    collection.TryGetAssetEntryFromTarget(element, out var entry) && entry.Path == path);
                Assert.That(row, Is.Not.Null, "the actual pane must realize the dragged asset");
                var down = new Vector2(row.worldBound.xMin + 30f, row.worldBound.center.y);
                window.Focus();
                SendMouse(window, EventType.MouseUp, down);
                var start = down + new Vector2(10f, 6f);
                if (folder)
                {
                    SendMouse(window, EventType.MouseDown, down);
                    SendMouse(window, EventType.MouseDrag, start);
                    Assert.That(DragAndDrop.paths, Is.EqualTo(new[] { path }),
                        "the captured row gesture must retain its asset payload");
                }
                else
                {
                    // Native OS drags can wait for physical input in batchmode.
                    // Exercise their content-preview boundary with a resolved
                    // asset payload; folder cases above drive the real gesture.
                    Assert.That(AssetDragPrepareInteraction.TryBuildAssetDragPayload(
                        new[] { path }, out var objects, out var paths), Is.True);
                    Assert.That(paths, Is.EqualTo(new[] { path }));
                    Assert.That(objects, Is.EqualTo(new[] { AssetDatabase.LoadMainAssetAtPath(path) }));
                    DragAndDrop.paths = paths;
                    DragAndDrop.objectReferences = objects;
                    _ = new AssetDragPrepareInteraction.ThemedDragSession(
                        root, objects, paths, start, () => { });
                }
                Assert.That(ZepWindowDocking.IsActive, Is.EqualTo(folder));
                Assert.That(Resources.FindObjectsOfTypeAll(
                    typeof(EditorWindow).Assembly.GetType("UnityEditor.PaneDragTab")), Is.Empty,
                    "a content ghost must not also show a native docking preview");
                var overlay = root.Q("zep-drag-overlay");
                Assert.That(overlay, Is.Not.Null,
                    "drag promotion inside package content must create its content ghost");
                yield return null;
                AssertVisibleGhost(overlay, name);
                Assert.That(root.worldBound.Contains(overlay.worldBound.center), Is.True,
                    "the visible ghost must remain inside the package window");
                Assert.That(root.Query<VisualElement>("zep-drag-overlay").ToList(), Has.Count.EqualTo(1));
                var initialPosition = overlay.worldBound.position;
                var next = start + new Vector2(70f, 45f);
                if (folder) SendMouse(window, EventType.MouseDrag, next);
                else SendDragUpdated(collection, next);
                yield return null;
                AssertVisibleGhost(overlay, name);
                Assert.That(overlay.worldBound.position - initialPosition,
                    Is.EqualTo(next - start).Using(Vector2Comparer));

                if (folder)
                    window.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
                else
                {
                    using var ended = DragExitedEvent.GetPooled(new Event { type = EventType.DragExited });
                    ended.target = collection;
                    collection.SendEvent(ended);
                }
                Assert.That(root.Q("zep-drag-overlay"), Is.Null,
                    "completion or cancellation must remove the content ghost");
                Assert.That(ZepWindowDocking.IsActive, Is.False);
            }
            finally
            {
                ZepWindowDocking.Clear();
                SendMouse(window, EventType.MouseUp, Vector2.zero);
                window.Close();
                DragAndDrop.PrepareStartDrag();
                if (sharedState.IsLivePane(previousActive)) sharedState.ActivatePane(previousActive);
            }
        }

        private static void AssertVisibleGhost(VisualElement overlay, string expectedName)
        {
            Assert.That(overlay.panel, Is.Not.Null);
            Assert.That(overlay.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(overlay.resolvedStyle.visibility, Is.EqualTo(Visibility.Visible));
            Assert.That(overlay.worldBound.width, Is.GreaterThan(30f));
            Assert.That(overlay.worldBound.height, Is.GreaterThan(16f));
            var label = overlay.Q<Label>("zep-drag-overlay-label");
            Assert.That(label.text, Is.EqualTo(expectedName));
            Assert.That(label.worldBound.width, Is.GreaterThan(0f));
            Assert.That(label.worldBound.height, Is.GreaterThan(0f));
            Assert.That(label.resolvedStyle.color.a, Is.GreaterThan(0f));
            var icon = overlay.Q<Image>("zep-drag-overlay-icon");
            Assert.That(icon.image, Is.Not.Null);
            Assert.That(icon.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(icon.worldBound.width, Is.EqualTo(16f).Within(0.1f));
            Assert.That(icon.worldBound.height, Is.EqualTo(16f).Within(0.1f));
        }

        [UnityTest]
        public IEnumerator FolderListPress_ClickPublishesSelection_AndFastOutsideDragFloats()
        {
            yield return AssertFolderGesture(AssetViewMode.List);
        }

        [UnityTest]
        public IEnumerator FolderGridPress_ClickPublishesSelection_AndFastOutsideDragFloats()
        {
            yield return AssertFolderGesture(AssetViewMode.Grid);
        }

        private static IEnumerator AssertFolderGesture(AssetViewMode mode)
        {
            using var assets = new TempAssetScope();
            var existing = Resources.FindObjectsOfTypeAll<ZepPaneWindow>();
            var window = CreateWindow();
            SurfaceGestureRouter router = null;
            try
            {
                var surface = MountThemedHost(window);
                var collection = new AssetCollectionView(
                    new UnityZepIconRepository(), new AssetsAreaInlineEditController(),
                    mode, 136, AssetsAreaConstants.MinAssetGridItemSize,
                    AssetsAreaConstants.MaxAssetGridItemSize, presentationLookup: null);
                surface.Add(collection);
                collection.SetAssets(new[]
                {
                    new ZepAssetEntry("Folder", assets.FolderPath, "Folder"),
                });
                router = new SurfaceGestureRouter(surface, collection);
                var emitted = 0;
                collection.SelectionChanged += _ => emitted++;
                yield return null;
                collection.style.height = surface.resolvedStyle.height;
                yield return null;
                yield return null;
                var row = collection.Query<VisualElement>().ToList().FirstOrDefault(element =>
                    collection.TryGetAssetEntryFromTarget(element, out var entry)
                    && entry.Path == assets.FolderPath);
                Assert.That(row, Is.Not.Null, "the real list/grid must realize the folder entry");
                var point = row.worldBound.center;
                // Pointer button state is editor-global, including input sent
                // by other fixtures. Start and finish with the button released.
                SendMouse(window, EventType.MouseUp, point);
                SendMouse(window, EventType.MouseDown, point);
                Assert.That(surface.HasMouseCapture(), Is.True,
                    "the move/up owner must retain the pending folder press");
                Assert.That(emitted, Is.Zero, "selection stays deferred until the press resolves");
                SendMouse(window, EventType.MouseUp, point);
                Assert.That(surface.HasMouseCapture(), Is.False);
                Assert.That(emitted, Is.EqualTo(1),
                    "a captured click must flush selection even though collection PointerUp is bypassed");

                SendMouse(window, EventType.MouseDown, point);
                router.Cancel();
                Assert.That(surface.HasMouseCapture(), Is.False, "cancel releases the pending capture");
                SendMouse(window, EventType.MouseUp, point);

                SendMouse(window, EventType.MouseDown, point);
                var outside = new Vector2(-2000f, -2000f);
                SendMouse(window, EventType.MouseLeaveWindow, outside);
                SendMouse(window, EventType.MouseDrag, outside);
                Assert.That(ZepWindowDocking.IsActive, Is.True,
                    "a held drag must start even when leaving the window cleared Unity's cached button state");
                Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False,
                    "an asset folder retains move semantics in our content");
                Assert.That(DragAndDrop.paths, Is.EqualTo(new[] { assets.FolderPath }));
                SendMouse(window, EventType.MouseEnterWindow, point);
                SendMouse(window, EventType.MouseDrag, point);
                SendMouse(window, EventType.MouseLeaveWindow, outside);
                SendMouse(window, EventType.MouseDrag, outside + new Vector2(20f, 20f));
                SendMouse(window, EventType.MouseDrag, outside);
                Assert.That(ZepWindowDocking.IsActive, Is.True,
                    "multiple window crossings while held cannot cancel the folder gesture");
                SendMouse(window, EventType.MouseUp, outside);
                yield return null;
                var created = Resources.FindObjectsOfTypeAll<ZepPaneWindow>()
                    .Except(existing).ToArray();
                Assert.That(created, Has.Length.EqualTo(1));
                var deadline = EditorApplication.timeSinceStartup + 5;
                while (created[0].Pane == null && EditorApplication.timeSinceStartup < deadline)
                    yield return null;
                Assert.That(created[0].Pane, Is.Not.Null, "the floated window must initialize its shared pane");
                Assert.That(created[0].Pane.Source.RootFolderPath, Is.EqualTo(AssetPath.From(assets.FolderPath)));
                Assert.That(ZepWindowDocking.IsActive, Is.False);
                Assert.That(window.rootVisualElement.panel.GetCapturingElement(PointerId.mousePointerId), Is.Null);
            }
            finally
            {
                router?.Cancel();
                ZepWindowDocking.Clear();
                SendMouse(window, EventType.MouseUp, Vector2.zero);
                foreach (var pane in Resources.FindObjectsOfTypeAll<ZepPaneWindow>().Except(existing))
                    pane.Close();
                window.Close();
                DragAndDrop.PrepareStartDrag();
            }
        }

        private static void SendMouse(EditorWindow window, EventType type, Vector2 point)
        {
            window.SendEvent(new Event { type = type, button = 0, clickCount = 1, mousePosition = point });
        }

        [UnityTest]
        public IEnumerator DraggingTheGhost_MovesByTranslateAndLeavesItsAnchorBoxAlone()
        {
            var window = CreateWindow();
            try
            {
                var host = MountThemedHost(window);
                yield return null;

                var start = new Vector2(100f, 80f);
                var session = CreateSession(host, start);
                Assert.That(session, Is.Not.Null);

                var overlay = host.Q("zep-drag-overlay");
                Assert.That(overlay, Is.Not.Null, "the session must mount its ghost on the themed host");
                yield return null;

                var anchorLeft = overlay.resolvedStyle.left;
                var anchorTop = overlay.resolvedStyle.top;

                var first = new Vector2(160f, 130f);
                SendDragUpdated(host, first);
                yield return null;

                Assert.That(Translation(overlay),
                    Is.EqualTo(first - start).Using(Vector2Comparer),
                    "the first move must ride translate by the pointer delta from the anchor");

                var second = new Vector2(245f, 310f);
                SendDragUpdated(host, second);
                yield return null;

                Assert.That(Translation(overlay),
                    Is.EqualTo(second - start).Using(Vector2Comparer),
                    "each move re-writes translate as the delta from the anchor, not from the last point");
                Assert.That(overlay.resolvedStyle.left, Is.EqualTo(anchorLeft).Within(0.01f),
                    "moving the ghost must not touch left — that is the layout write §5.18 removed");
                Assert.That(overlay.resolvedStyle.top, Is.EqualTo(anchorTop).Within(0.01f),
                    "moving the ghost must not touch top — that is the layout write §5.18 removed");
            }
            finally
            {
                window.Close();
            }
        }

        private static Vector2 Translation(VisualElement overlay)
        {
            var translate = overlay.style.translate.value;
            return new Vector2(translate.x.value, translate.y.value);
        }

        private static readonly System.Collections.Generic.IComparer<Vector2> Vector2Comparer =
            new ApproximateVector2Comparer();

        private static void SendDragUpdated(VisualElement target, Vector2 position)
        {
            using var evt = DragUpdatedEvent.GetPooled(new Event
            {
                type = EventType.DragUpdated,
                mousePosition = position
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        // This focused positioning check uses an offset host independently of
        // the production drag-start tests above.
        private static AssetDragPrepareInteraction.ThemedDragSession CreateSession(
            VisualElement host, Vector2 startMousePosition)
        {
            return new AssetDragPrepareInteraction.ThemedDragSession(
                host,
                Array.Empty<UnityEngine.Object>(),
                new[] { "Assets/DragGhostFixture.asset" },
                startMousePosition,
                () => { });
        }

        // The overlay is positioned in the host's local space, so the host is
        // deliberately offset from the window origin: a correct anchor has to
        // subtract that origin.
        private static VisualElement MountThemedHost(EditorWindow window)
        {
            var host = new VisualElement { name = "zep-drag-ghost-test-host" };
            host.AddToClassList(ZepUiClasses.Root);
            host.style.position = Position.Absolute;
            host.style.left = 24f;
            host.style.top = 36f;
            host.style.width = 500f;
            host.style.height = 400f;
            window.rootVisualElement.Add(host);
            return host;
        }

        private static DragGhostTestWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<DragGhostTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 480f);
            window.Show();
            return window;
        }

        private sealed class ApproximateVector2Comparer : System.Collections.Generic.IComparer<Vector2>
        {
            public int Compare(Vector2 x, Vector2 y)
            {
                return Mathf.Abs(x.x - y.x) < 0.01f && Mathf.Abs(x.y - y.y) < 0.01f ? 0 : 1;
            }
        }

        private sealed class DragGhostTestWindow : EditorWindow
        {
        }
    }
}
