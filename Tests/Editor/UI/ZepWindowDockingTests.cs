using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.Sidebar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    public sealed class ZepWindowDockingTests
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Parent = typeof(EditorWindow).GetField("m_Parent", Instance);
        private static readonly Type View = typeof(EditorWindow).Assembly.GetType("UnityEditor.View");
        private static readonly Type PreviewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.PaneDragTab");
        private readonly List<EditorWindow> _windows = new List<EditorWindow>();
        private readonly List<ZepShellView> _sidebarShells = new List<ZepShellView>();
        private ZepPaneWindow[] _existingPanes;
        private bool _favoritesExpanded;

        [SetUp]
        public void SetUp()
        {
            _existingPanes = Resources.FindObjectsOfTypeAll<ZepPaneWindow>();
            _favoritesExpanded = SessionState.GetBool(FavoritesWidget.ExpandedSessionKey, true);
            SessionState.SetBool(FavoritesWidget.ExpandedSessionKey, true);
            ZepWindowDocking.Clear();
            DragAndDrop.PrepareStartDrag();
        }

        [TearDown]
        public void TearDown()
        {
            ZepWindowDocking.Clear();
            foreach (var shell in _sidebarShells) shell.Teardown();
            _sidebarShells.Clear();
            foreach (var window in _windows)
                if (window != null && Parent.GetValue(window) != null)
                    window.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(120, 130) });
            foreach (var pane in NewPanes()) Close(pane);
            foreach (var window in _windows) Close(window);
            _windows.Clear();
            SessionState.SetBool(FavoritesWidget.ExpandedSessionKey, _favoritesExpanded);
            DragAndDrop.PrepareStartDrag();
        }

        [Test]
        public void InstalledUnity_ExposesTheNativeDockingBoundary()
        {
            Assert.That(ZepWindowDocking.IsSupported, Is.True);
        }

        [Test]
        public void DetachedSource_ReturnsToTheCallersExistingDragWithoutChangingItsPayload()
        {
            var payload = new ZepSplitDragPayload(ZepPaneSource.FixedTag("blue"), "Blue");
            ZepSplitDragAndDrop.Prepare(payload);
            Assert.That(ZepWindowDocking.Start(payload.Source, payload.DisplayName, new VisualElement()), Is.False);
            Assert.That(ZepSplitDragAndDrop.TryRead(out var actual), Is.True);
            Assert.That(actual, Is.SameAs(payload));
            Assert.That(NewPanes(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator Cancel_KeepsAssetPathsAndObjectsButDestroysTheUnhostedCandidate()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var paths = new[] { "Assets" };
            var objects = new[] { AssetDatabase.LoadMainAssetAtPath("Assets") };
            DragAndDrop.paths = paths;
            DragAndDrop.objectReferences = objects;
            var candidate = Begin(source);
            Assert.That(Parent.GetValue(candidate), Is.Null);
            Assert.That(DragAndDrop.paths, Is.EqualTo(paths));
            Assert.That(DragAndDrop.objectReferences, Is.EqualTo(objects));

            ZepWindowDocking.Clear();

            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(DragAndDrop.paths, Is.EqualTo(paths));
            Assert.That(DragAndDrop.objectReferences, Is.EqualTo(objects));
            Assert.That(source.rootVisualElement.panel.GetCapturingElement(PointerId.mousePointerId), Is.Null);
        }

        [UnityTest]
        public IEnumerator ChangedExternalPayload_CancelsInsteadOfReusingTheRoot()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            DragAndDrop.paths = new[] { "Assets/External" };
            Move(source, new Vector2(1800, 1200));
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Release(source, new Vector2(1800, 1200));
            Assert.That(NewPanes(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator Cancel_ClearsOnlyTheTypedPayloadCapturedByThisGesture()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var first = new ZepSplitDragPayload(ZepPaneSource.FixedTag("blue"), "Blue");
            ZepSplitDragAndDrop.Prepare(first);
            Begin(source);
            ZepWindowDocking.Clear();
            Assert.That(ZepSplitDragAndDrop.TryRead(out _), Is.False);

            ZepSplitDragAndDrop.Prepare(first);
            Begin(source);
            var next = new ZepSplitDragPayload(ZepPaneSource.FixedTag("red"), "Red");
            ZepSplitDragAndDrop.Prepare(next);
            ZepWindowDocking.Clear();
            Assert.That(ZepSplitDragAndDrop.TryRead(out var untouched), Is.True);
            Assert.That(untouched, Is.SameAs(next));
        }

        [UnityTest]
        public IEnumerator NativeGhost_RemainsOneWindowAcrossMovementAndTenStationaryFrames()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var target = OpenTarget(new Rect(700, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            var preview = Resources.FindObjectsOfTypeAll(PreviewType).Single();
            var point = ScreenRect(target).position + new Vector2(160, 12);
            Move(source, point);
            var previewRect = (Rect)View.GetProperty("screenPosition", Instance).GetValue(preview);
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                Assert.That(ZepWindowDocking.IsActive, Is.True);
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.EqualTo(new[] { preview }));
                Assert.That((Rect)View.GetProperty("screenPosition", Instance).GetValue(preview), Is.EqualTo(previewRect));
                Assert.That(Parent.GetValue(candidate), Is.Null, "hovering cannot register the candidate in a native host");
            }
            Move(source, new Vector2(1800, 1200));
            Move(source, point);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.EqualTo(new[] { preview }));
            ZepWindowDocking.Clear();
            Assert.That(preview == null, Is.True);
            Assert.That(candidate == null, Is.True);
        }

        [UnityTest]
        public IEnumerator FolderDragWithinOwnContent_HasNoDockingGhostAndDeliversOneLocalDrop()
        {
            var source = OpenPane(new Rect(100, 100, 500, 450));
            yield return AssertRootedView(source);
            yield return null;
            var paths = new[] { "Assets" };
            var objects = new[] { AssetDatabase.LoadMainAssetAtPath("Assets") };
            DragAndDrop.paths = paths;
            DragAndDrop.objectReferences = objects;
            var received = 0;
            source.rootVisualElement.RegisterCallback<DragPerformEvent>(evt =>
            {
                received++;
                Assert.That(DragAndDrop.paths, Is.EqualTo(paths));
                Assert.That(DragAndDrop.objectReferences, Is.EqualTo(objects));
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            var candidate = Begin(source);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty,
                "crossing the drag threshold inside our content must not create a docking ghost");
            var ghost = AssertLocalGhost(source, "Assets");

            var point = ScreenRect(source).position + source.rootVisualElement.worldBound.center;
            Move(source, point);
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                Assert.That(ZepWindowDocking.IsActive, Is.True,
                    "the local gesture must survive editor updates without a native preview");
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
                Assert.That(AssertLocalGhost(source, "Assets"), Is.SameAs(ghost));
            }
            var ghostPosition = ghost.worldBound.position;
            Move(source, point + new Vector2(30, 0));
            yield return null;
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            Assert.That(AssertLocalGhost(source, "Assets"), Is.SameAs(ghost));
            Assert.That(ghost.worldBound.x, Is.EqualTo(ghostPosition.x + 30).Within(0.5f),
                "the ordinary folder ghost must follow the local pointer");
            Assert.That(ghost.worldBound.y, Is.EqualTo(ghostPosition.y).Within(0.5f));
            Release(source, point + new Vector2(30, 0));

            Assert.That(received, Is.EqualTo(1));
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            Assert.That(NewPanes(), Is.EqualTo(new[] { source }));
        }

        [UnityTest]
        public IEnumerator LeavingAndReenteringPackageContent_ShowsGhostOnlyOutsideAndCanStillDock()
        {
            var source = OpenPane(new Rect(100, 100, 500, 450));
            yield return AssertRootedView(source);
            var otherContent = OpenPane(new Rect(700, 100, 500, 450));
            yield return AssertRootedView(otherContent);
            var target = OpenTarget(new Rect(1300, 100, 400, 450));
            yield return null;
            var payload = new ZepSplitDragPayload(ZepPaneSource.FixedTag("blue"), "Blue");
            ZepSplitDragAndDrop.Prepare(payload);
            var contentUpdates = 0;
            otherContent.rootVisualElement.RegisterCallback<DragUpdatedEvent>(evt =>
            {
                contentUpdates++;
                Assert.That(ZepSplitDragAndDrop.TryRead(out var actual), Is.True);
                Assert.That(actual, Is.SameAs(payload));
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            var candidate = Begin(source, payload.DisplayName);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            AssertLocalGhost(source, "Blue");

            var dockPoint = ScreenRect(target).position + new Vector2(160, 12);
            Move(source, dockPoint);
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            var preview = Resources.FindObjectsOfTypeAll(PreviewType).Single();
            var previewRect = (Rect)View.GetProperty("screenPosition", Instance).GetValue(preview);
            for (var frame = 0; frame < 10; frame++)
            {
                yield return null;
                Assert.That(ZepWindowDocking.IsActive, Is.True);
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.EqualTo(new[] { preview }));
                Assert.That((Rect)View.GetProperty("screenPosition", Instance).GetValue(preview), Is.EqualTo(previewRect));
            }

            Move(source, ScreenRect(source).position + source.rootVisualElement.worldBound.center);
            Assert.That(preview == null, Is.True, "returning to our content must dismiss the docking ghost");
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            Assert.That(ZepWindowDocking.IsActive, Is.True);
            AssertLocalGhost(source, "Blue");
            Move(source, ScreenRect(otherContent).position + otherContent.rootVisualElement.worldBound.center);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty,
                "another package window is also a content drop destination");
            Assert.That(contentUpdates, Is.EqualTo(1));
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            AssertLocalGhost(otherContent, "Blue");

            Move(source, dockPoint);
            Assert.That(otherContent.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType).Length, Is.EqualTo(1));
            Release(source, dockPoint);
            yield return null;
            Assert.That(candidate != null, Is.True);
            Assert.That(Parent.GetValue(candidate), Is.SameAs(Parent.GetValue(target)));
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            Assert.That(otherContent.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            yield return AssertRootedView(candidate);
        }

        [UnityTest]
        public IEnumerator TagDragWithinOwnContent_ShowsNamedTagGhostAndCleansUpOnEscape()
        {
            var source = OpenPane(new Rect(100, 100, 500, 450));
            yield return AssertRootedView(source);
            yield return null;
            var payload = new ZepSplitDragPayload(ZepPaneSource.FixedTag("blue"), "Blue");
            ZepSplitDragAndDrop.Prepare(payload);
            Begin(source, payload.DisplayName, payload.Source);

            var ghost = AssertLocalGhost(source, "Blue");
            var icon = ghost.Q<Image>("zep-drag-overlay-icon").image;
            Assert.That(icon, Is.Not.Null);
            Assert.That(icon, Is.SameAs(EditorGUIUtility.IconContent("FilterByLabel").image));
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            source.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(NewPanes(), Is.EqualTo(new[] { source }));
        }

        [UnityTest]
        public IEnumerator CapturedReleaseOnOtherTab_CreatesAndFocusesOneRootedSibling()
        {
            yield return CompleteNativeDrop(edge: false, sameSource: false);
        }

        [UnityTest]
        public IEnumerator CapturedReleaseOnOwnTab_SurvivesTheSourceRootDetachingDuringAddTab()
        {
            yield return CompleteNativeDrop(edge: false, sameSource: true);
        }

        [UnityTest]
        public IEnumerator NativeAddTab_SurvivesTheRealWorkspaceCallingClearWhileItsRootDetaches()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var shell = new ZepShellView(new ZepState());
            source.rootVisualElement.Add(shell);
            try
            {
                yield return null;
                Assert.That(source.rootVisualElement.Q<ZepSplitWorkspaceView>(), Is.Not.Null);
                var candidate = Begin(source);
                var parent = Parent.GetValue(source);
                var point = ScreenRect(source).position + new Vector2(160, 12);
                Move(source, point);
                Release(source, point);
                yield return null;
                Assert.That(candidate != null, Is.True, "workspace detach during AddTab cannot destroy the unhosted candidate");
                Assert.That(Parent.GetValue(candidate), Is.SameAs(parent));
                Assert.That(ZepWindowDocking.IsActive, Is.False);
                yield return AssertRootedView(candidate);
            }
            finally { shell.Teardown(); }
        }

        [UnityTest]
        public IEnumerator CapturedReleaseOnEdge_CreatesANativeSplitBesideTheTarget()
        {
            yield return CompleteNativeDrop(edge: true, sameSource: false);
        }

        [UnityTest]
        public IEnumerator CapturedReleaseOutsideWindowEdge_UsesTheNativeRootDockingZone()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var target = OpenTarget(new Rect(700, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            target.Focus();
            var rect = ScreenRect(target);
            var point = new Vector2(rect.xMax + 20, rect.center.y);
            Move(source, point);
            Release(source, point);
            yield return null;
            Assert.That(Parent.GetValue(candidate), Is.Not.Null);
            Assert.That(Parent.GetValue(candidate), Is.Not.SameAs(Parent.GetValue(target)));
            Assert.That(NativeWindow(candidate), Is.SameAs(NativeWindow(target)),
                "Unity's root docking zone extends beyond the native window rectangle");
        }

        [UnityTest]
        public IEnumerator CapturedReleaseOutsideDocking_CreatesAndFocusesAFloatingRootedWindow()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            var point = new Vector2(1800, 1200);
            Move(source, point);
            Release(source, point);
            yield return null;
            Assert.That(Parent.GetValue(candidate), Is.Not.Null);
            Assert.That(NativeWindow(candidate), Is.Not.SameAs(NativeWindow(source)));
            AssertSelected(candidate);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
            yield return AssertRootedView(candidate);
        }

        [UnityTest]
        public IEnumerator ReturnFromDockingToOwnContent_RelaysOnceBeforeReentrantClearAndNeverFloats()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var target = OpenPane(new Rect(700, 100, 500, 450));
            yield return null;
            var payload = new ZepSplitDragPayload(ZepPaneSource.FixedTag("blue"), "Blue");
            ZepSplitDragAndDrop.Prepare(payload);
            var received = 0;
            target.rootVisualElement.RegisterCallback<DragPerformEvent>(evt =>
            {
                received++;
                Assert.That(ZepSplitDragAndDrop.TryRead(out var actual), Is.True,
                    "content delivery must be immediate, before completion removes the payload");
                Assert.That(actual, Is.SameAs(payload));
                ZepWindowDocking.Clear();
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            var candidate = Begin(source);
            Move(source, ScreenRect(target).position + new Vector2(160, 12));
            var point = ScreenRect(target).position + target.rootVisualElement.worldBound.center;
            Move(source, point);
            Release(source, point);
            Assert.That(received, Is.EqualTo(1));
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(NewPanes(), Is.EqualTo(new[] { target }));
        }

        [UnityTest]
        public IEnumerator RejectedOwnContentDrop_DestroysCandidateInsteadOfFloating()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var target = OpenPane(new Rect(700, 100, 500, 450));
            yield return null;
            var received = false;
            target.rootVisualElement.RegisterCallback<DragPerformEvent>(evt =>
            {
                received = true;
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            var candidate = Begin(source);
            var point = ScreenRect(target).position + new Vector2(target.rootVisualElement.worldBound.xMin + 2,
                target.rootVisualElement.worldBound.center.y);
            Move(source, point);
            Release(source, point);
            Assert.That(received, Is.True, "our content keeps priority right to its edge");
            Assert.That(candidate == null, Is.True);
            Assert.That(NewPanes(), Is.EqualTo(new[] { target }));
        }

        [UnityTest]
        public IEnumerator CoveredOwnContent_CannotStealTheFrontWindowDockingEdge()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var behind = OpenPane(new Rect(700, 100, 500, 450));
            var target = OpenTarget(new Rect(700, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            target.Focus();
            var point = ScreenRect(target).position + new Vector2(8, 180);
            Move(source, point);
            Release(source, point);
            yield return null;
            Assert.That(Parent.GetValue(candidate), Is.Not.Null);
            Assert.That(NativeWindow(candidate), Is.SameAs(NativeWindow(target)));
            Assert.That(NativeWindow(candidate), Is.Not.SameAs(NativeWindow(behind)));
        }

        [UnityTest]
        public IEnumerator VisibleOwnContent_KeepsPriorityOverNearbyNativeOuterDockingZone()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var content = OpenPane(new Rect(700, 100, 500, 450));
            var nearby = OpenTarget(new Rect(950, 100, 300, 450));
            yield return null;
            var received = false;
            content.rootVisualElement.RegisterCallback<DragPerformEvent>(evt =>
            {
                received = true;
                evt.StopImmediatePropagation();
            }, TrickleDown.TrickleDown);
            var candidate = Begin(source);
            nearby.Focus();
            var nearbyRect = ScreenRect(nearby);
            var point = new Vector2(nearbyRect.xMin - 20, nearbyRect.center.y);
            Assert.That(content.rootVisualElement.worldBound.Contains(point - ScreenRect(content).position), Is.True);
            Move(source, point);
            Release(source, point);
            Assert.That(received, Is.True, "visible asset content retains priority over the overlapping outer dock zone");
            Assert.That(candidate == null, Is.True);
        }

        [UnityTest]
        public IEnumerator Escape_CancelsGhostAndCandidateWithoutCreatingAWindow()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            source.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
        }

        [UnityTest]
        public IEnumerator SourceClose_CancelsGhostAndUnhostedCandidate()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            source.Close();
            yield return null;
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
        }

        [UnityTest]
        public IEnumerator LostCapture_CancelsGhostAndUnhostedCandidate()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            source.rootVisualElement.CapturePointer(PointerId.mousePointerId);
            source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = new Vector2(130, 150) });
            yield return null;
            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RejectedDrop_DestroysTheCandidateWhetherUnityAttachedItOrNot(bool hosted)
        {
            var pane = ZepPaneWindow.Create(RootSource, "Assets");
            if (hosted) pane.Show();
            typeof(ZepWindowDocking).GetMethod("CloseRejectedPane",
                BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { pane });
            Assert.That(pane == null, Is.True);
        }

        private IEnumerator CompleteNativeDrop(bool edge, bool sameSource)
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            var target = sameSource ? source : OpenTarget(new Rect(700, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            var oldTargetParent = Parent.GetValue(target);
            var point = ScreenRect(target).position + (edge ? new Vector2(8, 180) : new Vector2(160, 12));
            Move(source, point);
            Release(source, point);
            yield return null;
            Assert.That(candidate != null, Is.True, "native AddTab must not cancel the in-progress completion");
            Assert.That(Parent.GetValue(candidate), Is.Not.Null);
            if (edge)
            {
                Assert.That(Parent.GetValue(candidate), Is.Not.SameAs(Parent.GetValue(target)));
                Assert.That(NativeWindow(candidate), Is.SameAs(NativeWindow(target)));
            }
            else Assert.That(Parent.GetValue(candidate), Is.SameAs(oldTargetParent));
            Assert.That(target != null && source != null, Is.True);
            AssertSelected(candidate);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(NewPanes(), Is.EqualTo(new[] { candidate }));
            yield return AssertRootedView(candidate);
        }

        [UnityTest]
        public IEnumerator NativeWindowExitAndReentryWhileHeld_PreserveContinuousMovementAndFloatingRelease()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            var preview = Resources.FindObjectsOfTypeAll(PreviewType).Single();
            source.SendEvent(new Event { type = EventType.MouseLeaveWindow, mousePosition = new Vector2(505, 150) });
            Assert.That(ZepWindowDocking.IsActive, Is.True);
            source.SendEvent(new Event { type = EventType.MouseEnterWindow, mousePosition = new Vector2(505, 150) });
            for (var step = 0; step < 3; step++)
            {
                Move(source, new Vector2(1800 + step * 10, 1200));
                Assert.That(ZepWindowDocking.IsActive, Is.True,
                    "native MouseDrag remains held after Unity clears its cached buttons on window exit");
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.EqualTo(new[] { preview }));
                yield return null;
            }
            Release(source, new Vector2(1820, 1200));
            Assert.That(candidate != null, Is.True);
            Assert.That(Parent.GetValue(candidate), Is.Not.Null);
            Assert.That(preview == null, Is.True);
        }

        [UnityTest]
        public IEnumerator MovementWithoutAHeldButtonOrNativeDrag_CancelsTheLostRelease()
        {
            var source = OpenTarget(new Rect(100, 100, 500, 450));
            yield return null;
            var candidate = Begin(source);
            source.SendEvent(new Event { type = EventType.MouseLeaveWindow, mousePosition = new Vector2(505, 150) });
            source.SendEvent(new Event { type = EventType.MouseMove, mousePosition = new Vector2(520, 150) });
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(candidate == null, Is.True);

            candidate = Begin(source);
            source.SendEvent(new Event { type = EventType.MouseLeaveWindow, mousePosition = new Vector2(505, 150) });
            source.SendEvent(new Event { type = EventType.MouseDrag, button = 1, mousePosition = new Vector2(520, 150) });
            Assert.That(ZepWindowDocking.IsActive, Is.False,
                "a different mouse button cannot continue an abandoned left-button gesture");
            Assert.That(candidate == null, Is.True);
        }

        [UnityTest]
        public IEnumerator LeavingSidebarDuringCapturedDrag_StopsScrollingOverContentAndNativeDocking()
        {
            var source = OpenSidebarSource();
            var otherContent = OpenPane(new Rect(1150, 100, 500, 450));
            var nativeTarget = OpenTarget(new Rect(1150, 650, 500, 450));
            yield return PrepareScrollableSidebar(source);
            var sidebar = source.rootVisualElement.Q<SidebarWidget>();
            var scroll = sidebar.Q<ScrollView>("zep-sidebar-scroll");
            var workspace = source.rootVisualElement.Q<ZepSplitWorkspaceView>();
            Begin(source);

            var destinations = new[]
            {
                ScreenRect(source).position + workspace.worldBound.center,
                ScreenRect(otherContent).position + otherContent.rootVisualElement.worldBound.center,
                ScreenRect(nativeTarget).position + new Vector2(160, 12),
            };
            foreach (var destination in destinations)
            {
                yield return HoverSidebarEdgeUntilScrolling(source, scroll);
                Move(source, destination);
                Assert.That(ZepWindowDocking.IsActive, Is.True);
                yield return AssertManualScrollRemainsInPlace(scroll);
            }

            // A favorite refresh can detach the hovered row while its owning
            // scroll view stays mounted. Leaving still ends that owner's hover.
            yield return HoverSidebarEdgeUntilScrolling(source, scroll);
            var edge = new Vector2(scroll.contentViewport.worldBound.center.x, scroll.worldBound.yMin + 2);
            var hovered = source.rootVisualElement.panel.Pick(edge);
            Assert.That(hovered.GetFirstAncestorOfType<FavoritesWidget>(), Is.Not.Null);
            sidebar.SetFavoritePaths(Array.Empty<string>());
            sidebar.Flush();
            Assert.That(hovered.panel, Is.Null, "the old hover leaf must actually be detached");
            sidebar.SetFavoritePaths(Enumerable.Repeat("Assets", 80).ToArray());
            sidebar.Flush();
            Move(source, destinations[0]);
            yield return AssertManualScrollRemainsInPlace(scroll);
        }

        [UnityTest]
        public IEnumerator EscapeWhileSidebarAutoScrolls_LeavesManualScrollingInPlace()
        {
            var source = OpenSidebarSource();
            yield return PrepareScrollableSidebar(source);
            var scroll = source.rootVisualElement.Q<ScrollView>("zep-sidebar-scroll");
            var candidate = Begin(source);
            yield return HoverSidebarEdgeUntilScrolling(source, scroll);
            AssertLocalGhost(source, "Assets");

            source.SendEvent(new Event { type = EventType.KeyDown, keyCode = KeyCode.Escape });

            Assert.That(candidate == null, Is.True);
            Assert.That(ZepWindowDocking.IsActive, Is.False);
            Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
            yield return AssertManualScrollRemainsInPlace(scroll);
        }

        [UnityTest]
        public IEnumerator ReleaseOutsideDirectlyFromSidebarHover_StopsScrollingAfterFloating()
        {
            var source = OpenSidebarSource();
            yield return PrepareScrollableSidebar(source);
            var scroll = source.rootVisualElement.Q<ScrollView>("zep-sidebar-scroll");
            var candidate = Begin(source);
            yield return HoverSidebarEdgeUntilScrolling(source, scroll);

            // The native release updates Unity's pointer hover before the
            // docking handler runs. No preceding move may clean up for it.
            Release(source, new Vector2(1800, 1200));

            Assert.That(ZepWindowDocking.IsActive, Is.False);
            yield return AssertRootedView(candidate);
            yield return AssertManualScrollRemainsInPlace(scroll);
        }

        [UnityTest]
        public IEnumerator RealSidebarSplitThenAnotherDrag_PreservesReleasedInputAndDocksWithoutAReset()
        {
            var source = ScriptableObject.CreateInstance<ZepWindow>();
            source.Show();
            source.position = new Rect(100, 100, 1000, 650);
            _windows.Add(source);
            var deadline = EditorApplication.timeSinceStartup + 8;
            Label label = null;
            while (label == null && EditorApplication.timeSinceStartup < deadline)
            {
                yield return null;
                label = source.rootVisualElement.Query<Label>().ToList().FirstOrDefault(item => item.text == "Assets"
                    && item.GetFirstAncestorOfType<Zep.Editor.UI.Sidebar.FolderTreeWidget>() != null
                    && item.worldBound.width > 0 && source.rootVisualElement.worldBound.Contains(item.worldBound.center));
            }
            Assert.That(label, Is.Not.Null, "the real sidebar must have a visible folder drag source");
            var workspace = source.rootVisualElement.Q<ZepSplitWorkspaceView>();
            var state = ZepControllerHost.SharedViewModel;
            var existingSplits = state.AuxiliaryPanes.ToArray();
            try
            {
                var initialPanes = workspace.PaneCount;
                var down = label.worldBound.center;
                source.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = down });
                source.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = down });
                source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = down + new Vector2(8, 0) });
                Assert.That(ZepWindowDocking.IsActive, Is.True, "the actual sidebar threshold starts capture");
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
                var ghost = AssertLocalGhost(source, "Assets");
                source.SendEvent(new Event { type = EventType.MouseLeaveWindow, mousePosition = down + new Vector2(8, 0) });
                source.SendEvent(new Event { type = EventType.MouseEnterWindow, mousePosition = down + new Vector2(8, 0) });
                for (var step = 0; step < 3; step++)
                {
                    source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = down + new Vector2(12 + step * 10, 0) });
                    Assert.That(ZepWindowDocking.IsActive, Is.True);
                    Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
                    Assert.That(AssertLocalGhost(source, "Assets"), Is.SameAs(ghost));
                    yield return null;
                }
                var drop = new Vector2(workspace.worldBound.xMax - 12, workspace.worldBound.center.y);
                source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = drop });
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty,
                    "the existing split preview must not be obscured by a native docking ghost");
                Assert.That(AssertLocalGhost(source, "Assets"), Is.SameAs(ghost));
                source.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = drop });
                Assert.That(workspace.PaneCount, Is.EqualTo(initialPanes + 1));
                var pressed = (int)typeof(VisualElement).Assembly.GetType("UnityEngine.UIElements.PointerDeviceState")
                    .GetMethod("GetPressedButtons", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { PointerId.mousePointerId });
                Assert.That(pressed & 1, Is.Zero, "synthetic cleanup must not re-press the released physical button");
                Assert.That(ZepWindowDocking.IsActive, Is.False);
                Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
                Assert.That(NewPanes(), Is.Empty);

                // A second real press follows immediately: no helper release,
                // PrepareStartDrag or test-only pointer reset between gestures.
                down = label.worldBound.center;
                source.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = down });
                source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = down + new Vector2(8, 0) });
                Assert.That(ZepWindowDocking.IsActive, Is.True, "the next sidebar press still starts normally");
                Assert.That(Resources.FindObjectsOfTypeAll(PreviewType), Is.Empty);
                AssertLocalGhost(source, "Assets");
                var candidate = NewPanes().Single();
                source.SendEvent(new Event { type = EventType.MouseLeaveWindow, mousePosition = down + new Vector2(8, 0) });
                var point = ScreenRect(source).position + new Vector2(160, 12);
                Move(source, point);
                Assert.That(source.rootVisualElement.Q("zep-drag-overlay"), Is.Null);
                Move(source, point + new Vector2(10, 0));
                Release(source, point + new Vector2(10, 0));
                Assert.That(candidate != null, Is.True);
                Assert.That(Parent.GetValue(candidate), Is.SameAs(Parent.GetValue(source)));
                Assert.That(ZepWindowDocking.IsActive, Is.False);
                yield return AssertRootedView(candidate);
            }
            finally
            {
                foreach (var pane in state.AuxiliaryPanes.Except(existingSplits).ToArray())
                    state.CloseAuxiliary(pane);
            }
        }

        private static ZepPaneSource RootSource => ZepPaneSource.RootedFolder(AssetPath.From("Assets"));

        private ZepPaneWindow OpenSidebarSource()
        {
            return OpenPane(new Rect(100, 100, 1000, 650));
        }

        private IEnumerator PrepareScrollableSidebar(ZepPaneWindow source)
        {
            var deadline = EditorApplication.timeSinceStartup + 5;
            while (source.Pane == null && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(source.rootVisualElement.Q<ZepPaneView>(), Is.Not.Null,
                "wait for the host's initial content before mounting the isolated sidebar");
            // Keep the native Zep host and real sidebar, with local
            // state so shared catalog refreshes cannot reset fixture scrolling.
            source.rootVisualElement.Clear();
            var shell = new ZepShellView(new ZepState());
            _sidebarShells.Add(shell);
            source.rootVisualElement.Add(shell);
            var sidebar = shell.Q<SidebarWidget>();
            Assert.That(sidebar, Is.Not.Null);
            sidebar.SetFavoritePaths(Enumerable.Repeat("Assets", 80).ToArray());
            sidebar.Flush();
            yield return null;
            yield return null;
            yield return null;
            var scroll = sidebar.Q<ScrollView>("zep-sidebar-scroll");
            Assert.That(scroll.verticalScroller.highValue, Is.GreaterThan(600),
                "the real sidebar must overflow enough to expose a stale edge-scroll ticker");
            DragAndDrop.paths = new[] { "Assets" };
            DragAndDrop.objectReferences = new[] { AssetDatabase.LoadMainAssetAtPath("Assets") };
        }

        private static IEnumerator HoverSidebarEdgeUntilScrolling(EditorWindow source, ScrollView scroll)
        {
            scroll.scrollOffset = new Vector2(0, 400);
            yield return null;
            var edge = new Vector2(scroll.contentViewport.worldBound.center.x, scroll.worldBound.yMin + 2);
            Move(source, ScreenRect(source).position + edge);
            var deadline = EditorApplication.timeSinceStartup + 1;
            while (scroll.scrollOffset.y > 380 && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(scroll.scrollOffset.y, Is.LessThanOrEqualTo(380),
                "hovering a folder drag at the sidebar edge must first start real auto-scrolling");
        }

        private static IEnumerator AssertManualScrollRemainsInPlace(ScrollView scroll)
        {
            scroll.scrollOffset = new Vector2(0, 400);
            var settledOffset = scroll.scrollOffset.y;
            Assert.That(settledOffset, Is.EqualTo(400).Within(0.5f));
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(scroll.scrollOffset.y, Is.EqualTo(settledOffset).Within(0.5f),
                "after leaving or ending a drag, the sidebar must not pull manual scrolling back toward its old hover edge");
        }

        private static VisualElement AssertLocalGhost(EditorWindow window, string label)
        {
            var overlays = window.rootVisualElement.Query<VisualElement>("zep-drag-overlay").ToList();
            Assert.That(overlays.Count, Is.EqualTo(1), "the content drag must show exactly one ordinary item ghost");
            var overlay = overlays[0];
            Assert.That(overlay.parent.ClassListContains(ZepUiClasses.Root), Is.True,
                "the ghost must belong to the themed host");
            Assert.That(overlay.Q<Label>("zep-drag-overlay-label").text, Is.EqualTo(label));
            Assert.That(overlay.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            return overlay;
        }

        private static ZepPaneWindow Begin(EditorWindow source, string displayName = "Assets",
            ZepPaneSource? paneSource = null)
        {
            // Earlier UI tests may pool a pressed pointer without its native
            // release. Normalize that process-wide input before this gesture.
            source.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = new Vector2(120, 130) });
            var root = source.rootVisualElement.panel.visualTree;
            var pending = false;
            var began = false;
            void Down(PointerDownEvent evt)
            {
                if (evt.button != 0) return;
                pending = true;
                source.rootVisualElement.CapturePointer(evt.pointerId);
                evt.StopImmediatePropagation();
            }
            void MovePending(PointerMoveEvent evt)
            {
                if (!pending) return;
                pending = false;
                began = ZepWindowDocking.Start(paneSource ?? RootSource, displayName, source.rootVisualElement);
                evt.StopImmediatePropagation();
            }
            root.RegisterCallback<PointerDownEvent>(Down, TrickleDown.TrickleDown);
            source.rootVisualElement.RegisterCallback<PointerMoveEvent>(MovePending, TrickleDown.TrickleDown);
            try
            {
                source.Focus();
                source.SendEvent(new Event { type = EventType.MouseDown, button = 0, clickCount = 1, mousePosition = new Vector2(120, 130) });
                source.SendEvent(new Event { type = EventType.MouseDrag, button = 0, mousePosition = new Vector2(140, 150) });
            }
            finally
            {
                root.UnregisterCallback<PointerDownEvent>(Down, TrickleDown.TrickleDown);
                source.rootVisualElement.UnregisterCallback<PointerMoveEvent>(MovePending, TrickleDown.TrickleDown);
            }
            Assert.That(began, Is.True, "the real source pointer threshold must start the captured window gesture");
            Assert.That(ZepWindowDocking.IsActive, Is.True);
            Assert.That(root.HasPointerCapture(PointerId.mousePointerId), Is.True);
            return (ZepPaneWindow)typeof(ZepWindowDocking).GetField("_candidate",
                BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        private DockingTestWindow OpenTarget(Rect position)
        {
            var window = ScriptableObject.CreateInstance<DockingTestWindow>();
            window.titleContent = new GUIContent("Dock Target");
            window.Show();
            window.position = position;
            _windows.Add(window);
            return window;
        }

        private ZepPaneWindow OpenPane(Rect position)
        {
            var window = ZepPaneWindow.Create(RootSource, "Assets");
            window.Show();
            window.position = position;
            _windows.Add(window);
            return window;
        }

        private static void AssertSelected(EditorWindow candidate)
        {
            var parent = Parent.GetValue(candidate);
            Assert.That(parent.GetType().GetProperty("actualView", Instance).GetValue(parent), Is.SameAs(candidate));
            // Batchmode has no OS keyboard focus, even after EditorWindow.Focus.
            if (!Application.isBatchMode) Assert.That(EditorWindow.focusedWindow, Is.SameAs(candidate));
        }

        private static Rect ScreenRect(EditorWindow window) => (Rect)View.GetProperty("screenPosition", Instance).GetValue(Parent.GetValue(window));
        private static object NativeWindow(EditorWindow window) => View.GetProperty("window", Instance).GetValue(Parent.GetValue(window));
        private static void Move(EditorWindow source, Vector2 screenPoint) => source.SendEvent(new Event
        {
            type = EventType.MouseDrag, button = 0, mousePosition = screenPoint - ScreenRect(source).position,
        });
        private static void Release(EditorWindow source, Vector2 screenPoint) => source.SendEvent(new Event
        {
            type = EventType.MouseUp, button = 0, mousePosition = screenPoint - ScreenRect(source).position,
        });
        private static void Close(EditorWindow window)
        {
            if (window == null) return;
            if (Parent.GetValue(window) == null) UnityEngine.Object.DestroyImmediate(window);
            else window.Close();
        }
        private ZepPaneWindow[] NewPanes() => Resources.FindObjectsOfTypeAll<ZepPaneWindow>()
            .Where(pane => !_existingPanes.Contains(pane)).ToArray();
        private static IEnumerator AssertRootedView(ZepPaneWindow pane)
        {
            var deadline = EditorApplication.timeSinceStartup + 5;
            while (pane != null && pane.Pane == null && EditorApplication.timeSinceStartup < deadline)
                yield return null;
            Assert.That(pane != null, Is.True);
            Assert.That(pane.Pane, Is.Not.Null, "native completion must initialize the rooted pane");
            Assert.That(pane.Pane.Source.RootFolderPath.Value, Is.EqualTo("Assets"));
            Assert.That(pane.rootVisualElement.Q<ZepPaneView>(), Is.Not.Null);
            Assert.That(pane.ViewModel.ActivePane, Is.SameAs(pane.Pane));
        }
        private sealed class DockingTestWindow : EditorWindow { }
    }
}
