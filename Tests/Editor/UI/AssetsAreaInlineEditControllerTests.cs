using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.AssetsBrowser;
using Zep.Editor.UI.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // Lifetime of an inline-edit session against row recycling. The rename row
    // is a pooled list/grid element: scrolling it out of view, or a refresh that
    // drops its path, rebinds the same physical TextField to another entry.
    // Hiding the edit container does not blur it, so the session has to end at
    // that bind or the panel keyboard stays trapped in an invisible input.
    public sealed class AssetsAreaInlineEditControllerTests
    {
        private const string EditedPath = "Assets/Edited.png";
        private const string OtherPath = "Assets/Other.png";

        [UnityTest]
        public IEnumerator BindRow_RebindingTheEditRowToAnotherPath_EndsTheSessionOnTheNextTick()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            controller.Aborted += () => abortCount++;
            var row = new PooledRow();
            BeginEdit(controller, row, EditedPath);

            Bind(controller, row, OtherPath);

            // Deliberately NOT an immediate cancel: Unity 6.5's ListView
            // releases and re-binds every active row inside a single refresh
            // pass, so the session must survive the recycle long enough for a
            // re-claiming bind to keep it. Without one, the deferred leg
            // cancels on the next editor tick.
            Assert.That(controller.IsActiveForPath(EditedPath), Is.True,
                "the session must stay pending through the refresh pass that recycled the row");

            yield return null;
            yield return null;

            Assert.That(controller.Descriptor.IsActive, Is.False,
                "with no re-claiming bind, recycling the edit row must end the session");
            Assert.That(abortCount, Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator BindRow_ResettingTheEditRowToAnEmptyEntry_EndsTheSessionOnTheNextTick()
        {
            var controller = new AssetsAreaInlineEditController();
            var row = new PooledRow();
            BeginEdit(controller, row, EditedPath);

            // The widgets' null-entry reset path binds the pooled row with an
            // empty path — the edited asset left the data set entirely.
            Bind(controller, row, string.Empty);

            yield return null;
            yield return null;

            Assert.That(controller.Descriptor.IsActive, Is.False);
        }

        [Test]
        public void BindRow_RebindingTheEditRowToItsOwnPath_KeepsTheSession()
        {
            var controller = new AssetsAreaInlineEditController();
            var row = new PooledRow();
            BeginEdit(controller, row, EditedPath);
            controller.HandleRowValueChanged("half-typed", EditedPath);

            Bind(controller, row, EditedPath);

            Assert.That(controller.IsActiveForPath(EditedPath), Is.True);
            Assert.That(row.Field.value, Is.EqualTo("half-typed"),
                "an ordinary rebind of the editing row must not discard what the user has typed");
        }

        [Test]
        public void BindRow_RecyclingADifferentRow_LeavesTheActiveEditAlone()
        {
            var controller = new AssetsAreaInlineEditController();
            var editRow = new PooledRow();
            var neighbourRow = new PooledRow();
            BeginEdit(controller, editRow, EditedPath);

            Bind(controller, neighbourRow, OtherPath);

            Assert.That(controller.IsActiveForPath(EditedPath), Is.True);
        }

        [UnityTest]
        public IEnumerator RecycledEditRow_AbortsTheSessionExactlyOnce()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            var commitCount = 0;
            controller.Aborted += () => abortCount++;
            controller.Confirmed += _ => commitCount++;

            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                yield return null;

                Bind(controller, row, OtherPath);
                yield return null;
                yield return null;

                Assert.That(abortCount, Is.EqualTo(1));
                Assert.That(commitCount, Is.EqualTo(0),
                    "a recycled row must cancel the edit — a half-typed name is not a rename the user asked for");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator RecycledEditRow_WhenANewEditOpensFirst_DoesNotAbortIt()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            controller.Aborted += () => abortCount++;

            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                yield return null;

                Bind(controller, row, OtherPath);
                controller.SetDescriptor(
                    new AssetsAreaInlineEditDescriptor(OtherPath, "Other", ".png", isGhost: false));
                yield return null;
                yield return null;

                Assert.That(abortCount, Is.EqualTo(0));
                Assert.That(controller.IsActiveForPath(OtherPath), Is.True);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ReleasedEditRow_WithoutARebind_AbortsTheSessionExactlyOnce()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            var commitCount = 0;
            controller.Aborted += () => abortCount++;
            controller.Confirmed += _ => commitCount++;

            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                yield return null;

                // The source shrank below this row's index: ListView releases
                // the pooled row via unbindItem and never rebinds it. Detach
                // like the pool does, then release — no BindRow follows, so
                // only HandleRowReleased stands between the user and a
                // keyboard trapped in a detached field.
                row.Root.RemoveFromHierarchy();
                controller.HandleRowReleased(row.Field);
                yield return null;
                yield return null;

                Assert.That(controller.Descriptor.IsActive, Is.False,
                    "a released edit row must end the session, not leave it active on a detached field");
                Assert.That(abortCount, Is.EqualTo(1),
                    "release aborts exactly once even though the deferred leg rides a one-shot editor update");
                Assert.That(commitCount, Is.EqualTo(0),
                    "release is a cancel — a half-typed name must not become a rename");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void HandleRowReleased_ForANonEditRowOrNull_LeavesTheActiveEditAlone()
        {
            var controller = new AssetsAreaInlineEditController();
            var editRow = new PooledRow();
            var neighbourRow = new PooledRow();
            BeginEdit(controller, editRow, EditedPath);

            controller.HandleRowReleased(neighbourRow.Field);
            controller.HandleRowReleased(null);

            Assert.That(controller.IsActiveForPath(EditedPath), Is.True,
                "releasing a row that does not host the edit must not end the session");
        }

        [Test]
        public void HandleRowEscapeKeyDown_OnlyAbortsForEscapeOnTheActiveEditRow()
        {
            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            var commitCount = 0;
            controller.Aborted += () => abortCount++;
            controller.Confirmed += _ => commitCount++;
            var row = new PooledRow();
            BeginEdit(controller, row, EditedPath);

            using (var otherRowEscape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
            {
                controller.HandleRowEscapeKeyDown(otherRowEscape, OtherPath);
            }
            using (var enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
            {
                controller.HandleRowEscapeKeyDown(enter, EditedPath);
            }

            Assert.That(abortCount, Is.Zero,
                "neither Escape for another row nor a non-Escape key may abort the active edit");

            using (var escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
            {
                controller.HandleRowEscapeKeyDown(escape, EditedPath);
            }

            Assert.That(abortCount, Is.EqualTo(1));
            Assert.That(commitCount, Is.Zero, "Escape is a cancel, never a commit");
        }

        // The regression this guards: the field's inner text element consumes a
        // bubble-phase Escape (restores the text, blurs), and the blur's
        // FocusOut committed the edit — Escape CREATED the ghost asset under
        // its default name instead of cancelling. The wiring below mirrors the
        // widgets' EnsureRow/TileEditField exactly; without the trickle-phase
        // interception this test fails with commitCount == 1.
        [UnityTest]
        public IEnumerator EscapeOnTheFocusedEditField_CancelsWithoutCommitting()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for real focus and text-engine key processing.");

            var controller = new AssetsAreaInlineEditController();
            var abortCount = 0;
            var commitCount = 0;
            // Mirror AssetsBrowserFeature: ending the session clears the
            // descriptor (which releases field focus), so the FocusOut that
            // follows an abort must find the guard already closed.
            controller.Aborted += () =>
            {
                abortCount++;
                controller.SetDescriptor(AssetsAreaInlineEditDescriptor.None);
            };
            controller.Confirmed += _ =>
            {
                commitCount++;
                controller.SetDescriptor(AssetsAreaInlineEditDescriptor.None);
            };

            var row = new PooledRow();
            row.Field.RegisterCallback<KeyDownEvent>(
                evt => controller.HandleRowEscapeKeyDown(evt, EditedPath),
                TrickleDown.TrickleDown);
            row.Field.RegisterCallback<KeyDownEvent>(
                evt => controller.HandleRowKeyDown(evt, EditedPath, row.Field));
            row.Field.RegisterCallback<FocusOutEvent>(
                evt => controller.HandleRowFocusOut(EditedPath, row.Field));

            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                yield return null;
                Assume.That(FieldOwnsFocus(row), Is.True,
                    "the edit field must own focus so Escape targets its inner text element");

                var target = row.Field.panel.focusController.focusedElement as VisualElement ?? row.Field;
                using (var escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                {
                    escape.target = target;
                    target.SendEvent(escape);
                }

                yield return null;
                yield return null;

                Assert.That(abortCount, Is.EqualTo(1), "Escape must cancel the edit session");
                Assert.That(commitCount, Is.Zero,
                    "Escape must never commit — commit here is the restore+blur path creating the ghost asset");
                Assert.That(controller.Descriptor.IsActive, Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator RecycledEditRow_ReleasesTheFieldFocus()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required to prove real focus release.");

            var controller = new AssetsAreaInlineEditController();
            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                yield return null;

                Assume.That(FieldOwnsFocus(row), Is.True, "the edit field must take focus before it can be released");

                Bind(controller, row, OtherPath);
                yield return null;

                Assert.That(FieldOwnsFocus(row), Is.False);
            }
            finally
            {
                window.Close();
            }
        }

        // A refresh while a rename is open (a background import finishing, a
        // catalog delta, a second refresh pass) rebinds the live edit row.
        // That bind re-ran focus AND select-all, so the caret jumped to a full
        // selection mid-typing and the next keystroke wiped what the user had
        // written. Focus must still be re-taken on every bind — 6.5 releases
        // the row before rebinding it — but the selection must not move.
        //
        // Only the plain rebind is asserted here. The 6.5 release-then-rebind
        // shape needs focus to come BACK after a blur; the caret the controller
        // stashes for that path is verified in the editor.
        [UnityTest]
        public IEnumerator RebindingTheLiveEditRow_KeepsTheCaretInsteadOfReselectingTheName()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for real focus and text selection.");

            var controller = new AssetsAreaInlineEditController();
            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                var deadline = EditorApplication.timeSinceStartup + SchedulerTimeoutSeconds;
                while (!FieldOwnsFocus(row) && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                Assume.That(FieldOwnsFocus(row), Is.True,
                    "the edit field must own focus before its selection state means anything");
                Assert.That(SelectionSpan(row), Is.EqualTo((0, row.Field.value.Length)),
                    "opening an edit selects the whole name so typing replaces it");

                // The user clicked into the name: a collapsed caret, no selection.
                row.Field.SelectRange(3, 3);
                Assume.That(SelectionSpan(row), Is.EqualTo((3, 3)));

                // Nothing observable changes when the rebind's focus leg behaves,
                // so the caret cannot simply be read a few frames later: on a tick
                // the leg never got, the assertion below would pass vacuously.
                // Chase it with a scheduled item of our own, registered after it on
                // the same element and due at the same time, so it cannot run first.
                Bind(controller, row, EditedPath);
                var rebindLegRan = false;
                row.Field.schedule.Execute(() => rebindLegRan = true).ExecuteLater(0);
                deadline = EditorApplication.timeSinceStartup + SchedulerTimeoutSeconds;
                while (!rebindLegRan && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                Assume.That(rebindLegRan, Is.True,
                    "the panel scheduler must tick before the rebind's focus leg can be judged");
                yield return null;
                window.Repaint();
                yield return null;

                Assert.That(SelectionSpan(row), Is.EqualTo((3, 3)),
                    "an ordinary rebind of the live edit row must leave the caret where the user put it");
                Assert.That(FieldOwnsFocus(row), Is.True,
                    "the rebind must keep the field focused — 6.5 releases the row before rebinding it");
                Assert.That(controller.IsActiveForPath(EditedPath), Is.True,
                    "the rebind keeps the session alive");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ANewEditSessionInTheSameRow_SelectsTheWholeNameAgain()
        {
            Assume.That(
                SystemInfo.graphicsDeviceType,
                Is.Not.EqualTo(GraphicsDeviceType.Null),
                "A graphics-backed Editor panel is required for real focus and text selection.");

            var controller = new AssetsAreaInlineEditController();
            var row = new PooledRow();
            var window = ShowWindowHosting(row);
            try
            {
                yield return null;
                BeginEdit(controller, row, EditedPath);
                var deadline = EditorApplication.timeSinceStartup + SchedulerTimeoutSeconds;
                while (!FieldOwnsFocus(row) && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                Assume.That(FieldOwnsFocus(row), Is.True,
                    "the edit field must own focus before its selection state means anything");
                row.Field.SelectRange(3, 3);

                // A second rename, on a different asset, in the same pooled row.
                BeginEdit(controller, row, OtherPath);
                Assert.That(FieldOwnsFocus(row), Is.False,
                    "opening a session on another path releases the old field focus first — "
                    + "the wait below must be waiting for the new bind's re-focus, not reading a stale one");
                deadline = EditorApplication.timeSinceStartup + SchedulerTimeoutSeconds;
                while (!FieldOwnsFocus(row) && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                // Focus is taken and the name selected in the same scheduled leg,
                // so the selection has already been decided once focus is back.
                Assume.That(FieldOwnsFocus(row), Is.True,
                    "the re-focused edit field must own focus before its selection state means anything");
                Assert.That(SelectionSpan(row), Is.EqualTo((0, row.Field.value.Length)),
                    "a new edit session selects the whole name — once per session is not once per row");
            }
            finally
            {
                window.Close();
            }
        }

        // The panel scheduler ticks on repaint, and a batchmode editor repaints
        // only when asked — and only as fast as the rest of the suite lets it.
        // A fixed frame budget therefore raced the bind's focus leg: eight
        // frames was enough for this fixture alone but landed on frames 3-5+ in
        // a full run, so the tests below wait on the condition itself under a
        // generous deadline instead of counting frames.
        private const double SchedulerTimeoutSeconds = 5d;

        // Direction-independent: select-all leaves the caret at one end and the
        // anchor at the other, and which end is the text engine's business.
        private static (int, int) SelectionSpan(PooledRow row)
        {
            return (Mathf.Min(row.Field.cursorIndex, row.Field.selectIndex),
                Mathf.Max(row.Field.cursorIndex, row.Field.selectIndex));
        }

        private static bool FieldOwnsFocus(PooledRow row)
        {
            var focused = row.Field.panel?.focusController?.focusedElement as VisualElement;
            return focused != null && (focused == row.Field || row.Field.Contains(focused));
        }

        private static void BeginEdit(AssetsAreaInlineEditController controller, PooledRow row, string path)
        {
            controller.SetDescriptor(
                new AssetsAreaInlineEditDescriptor(path, Path.GetFileNameWithoutExtension(path), ".png", isGhost: false));
            Bind(controller, row, path);
            Assert.That(controller.IsActiveForPath(path), Is.True, "precondition: the row hosts the active edit");
        }

        private static void Bind(AssetsAreaInlineEditController controller, PooledRow row, string entryPath)
        {
            controller.BindRow(
                entryPath: entryPath,
                nameLabel: row.Name,
                editContainer: row.EditContainer,
                nameField: row.Field,
                extensionLabel: row.Extension,
                rowRoot: row.Root,
                editingUssClass: ZepUiClasses.RowEditing);
        }

        private static EditorWindow ShowWindowHosting(PooledRow row)
        {
            var window = ScriptableObject.CreateInstance<InlineEditTestWindow>();
            window.position = new Rect(100f, 100f, 400f, 200f);
            window.Show();
            window.rootVisualElement.Add(row.Root);
            return window;
        }

        // Mirrors what both asset widgets pool per row: a name label plus an
        // on-demand edit container holding the TextField and its extension label.
        private sealed class PooledRow
        {
            public readonly VisualElement Root = new VisualElement();
            public readonly Label Name = new Label();
            public readonly VisualElement EditContainer = new VisualElement();
            public readonly TextField Field = new TextField();
            public readonly Label Extension = new Label();

            public PooledRow()
            {
                Root.Add(Name);
                EditContainer.Add(Field);
                EditContainer.Add(Extension);
                Root.Add(EditContainer);
            }
        }

        private sealed class InlineEditTestWindow : EditorWindow
        {
        }
    }

    // What an inline rename does when the name is already taken in the folder.
    // It used to look like nothing happened: the rename reached
    // AssetDatabase.MoveAsset, failed there, and left Unity's raw error in a
    // status bar that deduped every identical repeat away — so a second attempt
    // at the same name said nothing at all, and the edit was already gone.
    //
    // The commit is driven through the feature's own inline-edit controller
    // (the Enter leg the rows register), so these run the real chain:
    // Confirmed -> AssetsBrowserFeature.OnInlineEditConfirmed (which closes the
    // session first) -> the request's OnCommit -> ZepCreateOrchestrator
    // -> ZepController -> ProjectFileCommandService -> the rig gateway.
    public sealed class InlineRenameConflictTests
    {
        private const string Folder = "Assets/Art";
        private const string Renamed = "Assets/Art/Bar.mat";
        private const string Occupant = "Assets/Art/Foo.mat";

        private readonly List<string> _errors = new List<string>();
        private ZepUiControllerRig _rig;
        private AssetsBrowserFeature _feature;
        private ZepCreateOrchestrator _orchestrator;

        [SetUp]
        public void SetUp()
        {
            _errors.Clear();
            _rig = ZepUiControllerRig.Build(
                new[]
                {
                    new AssetRecord("occupant-guid", Occupant, "Foo", "Material"),
                    new AssetRecord("renamed-guid", Renamed, "Bar", "Material")
                },
                new[] { "Assets", Folder });
            _feature = new AssetsBrowserFeature(
                new UnityZepIconRepository(),
                AssetViewMode.List,
                AssetsAreaConstants.DefaultAssetGridItemSize);
            _orchestrator = new ZepCreateOrchestrator(
                _rig.State,
                _rig.Controller,
                pane => ReferenceEquals(pane, _rig.State.PrimaryPane) ? _feature : null,
                null,
                null,
                showError: message => _errors.Add(message));
        }

        [TearDown]
        public void TearDown()
        {
            _feature?.CancelInlineEdit();
            _feature = null;
            _orchestrator = null;
            _rig?.Dispose();
            _rig = null;
        }

        [Test]
        public void ACommitOntoATakenName_KeepsTheEditorOpenWithWhatWasTyped_AndSaysWhy()
        {
            BeginRename();

            Commit("Foo");

            Assert.That(Exists(Renamed), Is.True, "the item keeps its name");
            Assert.That(Exists(Occupant), Is.True, "and the item already there is untouched");
            Assert.That(
                _rig.Controller.StatusMessage,
                Is.EqualTo("'Foo.mat' already exists in Assets/Art."),
                "the status line carries the panel's own reason, not Unity's raw error");
            Assert.That(_errors, Is.EqualTo(new[] { "'Foo.mat' already exists in Assets/Art." }),
                "and the toast carries it too — the status bar dedupes repeats away");
            Assert.That(_feature.HasActiveInlineEdit, Is.True,
                "a refused name leaves the user in the edit, the way Finder does");
            Assert.That(InlineController().Descriptor.Path, Is.EqualTo(Renamed));
            Assert.That(InlineController().Descriptor.InitialBasename, Is.EqualTo("Foo"),
                "the field opens with what was typed, not with the name on disk");
        }

        [Test]
        public void TwoAttemptsAtTheSameTakenName_SayItTwice()
        {
            BeginRename();

            Commit("Foo");
            Commit("Foo");

            Assert.That(_errors.Count, Is.EqualTo(2),
                "the second attempt at the same name must not be silent");
            Assert.That(_feature.HasActiveInlineEdit, Is.True);
        }

        [Test]
        public void EscapeAfterARefusedCommit_CancelsTheReopenedSession()
        {
            BeginRename();
            Commit("Foo");
            Assume.That(_feature.HasActiveInlineEdit, Is.True);

            using (var escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
            {
                InlineController().HandleRowEscapeKeyDown(escape, Renamed);
            }

            Assert.That(_feature.HasActiveInlineEdit, Is.False, "Escape still cancels");
            Assert.That(InlineController().Descriptor.IsActive, Is.False);
            Assert.That(Exists(Renamed), Is.True, "and cancelling renames nothing");
            Assert.That(_errors.Count, Is.EqualTo(1), "the cancel is not a second failure");
        }

        [Test]
        public void ACommitOntoAFreeName_StillRenamesAndClosesTheEditor()
        {
            BeginRename();

            Commit("Baz");

            Assert.That(Exists("Assets/Art/Baz.mat"), Is.True);
            Assert.That(Exists(Renamed), Is.False);
            Assert.That(Exists(Occupant), Is.True);
            Assert.That(_feature.HasActiveInlineEdit, Is.False);
            Assert.That(_errors, Is.Empty);
        }

        private void BeginRename()
        {
            _orchestrator.BeginRename(_rig.State.PrimaryPane, Renamed, isFolder: false);
            Assume.That(_feature.HasActiveInlineEdit, Is.True,
                "precondition: the inline edit session is open on the renamed item");
        }

        // The Enter leg both widgets register on the edit row.
        private void Commit(string typed)
        {
            var controller = InlineController();
            controller.HandleRowValueChanged(typed, Renamed);
            using (var enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
            {
                controller.HandleRowKeyDown(enter, Renamed, null);
            }
        }

        private AssetsAreaInlineEditController InlineController()
        {
            var field = typeof(AssetsBrowserFeature).GetField(
                "_inlineEditController",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "the feature owns the inline-edit controller");
            return (AssetsAreaInlineEditController)field.GetValue(_feature);
        }

        private bool Exists(string assetPath)
        {
            return _rig.Gateway.Exists(AssetPath.From(assetPath));
        }
    }
}
