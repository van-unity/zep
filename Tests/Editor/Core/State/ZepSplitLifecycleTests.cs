using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Domain;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.State
{
    public sealed class ZepSplitLifecycleTests
    {
        [Test]
        public void WindowPanes_WithTheSameSourceKeepIndependentNavigationAndSelection()
        {
            var state = new ZepState();
            var source = ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art"));
            var split = state.OpenAuxiliary(source).NewPane;
            var first = state.OpenWindowPane(source);
            var second = state.OpenWindowPane(source);

            first.TrySetFolderSelection("Assets/Art/UI");
            first.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            Assert.That(state.WindowPanes, Is.EqualTo(new[] { first, second }));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(new[] { split }));
            Assert.That(state.Panes, Is.EqualTo(new[] { state.PrimaryPane, split, first, second }));
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(state.ActivePane, Is.SameAs(second));
            Assert.That(first.CurrentFolderPath.Value, Is.EqualTo("Assets/Art/UI"));
            Assert.That(second.CurrentFolderPath.Value, Is.EqualTo("Assets/Art"));
            Assert.That(split.CurrentFolderPath.Value, Is.EqualTo("Assets/Art"));
            Assert.That(second.SelectedAssetPaths, Is.Empty);
            Assert.That(split.SelectedAssetPaths, Is.Empty);
            Assert.Throws<ArgumentException>(() => state.OpenWindowPane(ZepPaneSource.Primary));
        }

        [Test]
        public void WindowPane_CloseUnsubscribesAndPreservesOtherWindowsAndSplit()
        {
            var state = new ZepState();
            var split = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var first = state.OpenWindowPane(ZepPaneSource.FixedTag("blue"));
            var second = state.OpenWindowPane(ZepPaneSource.FixedTag("blue"));
            var changes = new List<ZepPaneChange>();
            state.PaneChanged += changes.Add;

            using (state.DeferNotifications())
            {
                first.SetSearchText("hero");
                first.SetSelectedTypeFilter("Texture2D");
            }

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].NewPane, Is.SameAs(first));
            Assert.That(changes[0].Update,
                Is.EqualTo(ZepPaneUpdate.Search | ZepPaneUpdate.Filter));
            Assert.That(state.CloseWindowPane(first), Is.True);
            Assert.That(state.ActivePane, Is.SameAs(second));
            Assert.That(state.WindowPanes, Is.EqualTo(new[] { second }));
            Assert.That(state.IsLivePane(first), Is.False);
            Assert.That(state.IsLivePane(second), Is.True);
            Assert.That(state.CloseWindowPane(split), Is.False);
            Assert.That(state.CloseWindowPane(state.PrimaryPane), Is.False);
            Assert.That(state.CloseAuxiliary(second).Kind, Is.EqualTo(ZepSplitOperationKind.Rejected));
            changes.Clear();
            first.SetSearchText("stale");
            Assert.That(changes, Is.Empty, "a closed window cannot publish into shared state");

            Assert.That(state.CloseWindowPane(second), Is.True);
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(new[] { split }));
        }

        [Test]
        public void OpenAuxiliary_WithAWindowActiveReplacesOnlyTheExistingSplit()
        {
            var state = new ZepState();
            var split = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            var window = state.OpenWindowPane(ZepPaneSource.FixedTag("red"));

            var result = state.OpenAuxiliary(ZepPaneSource.FixedTag("green"));

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(result.OldPane, Is.SameAs(split));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(new[] { result.NewPane }));
            Assert.That(state.WindowPanes, Is.EqualTo(new[] { window }));
            Assert.That(state.IsLivePane(window), Is.True);
        }

        [Test]
        public void OpenAuxiliary_BelowCapacityAppendsAndActivatesPane()
        {
            var state = new ZepState();
            var source = ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art"));

            var result = state.OpenAuxiliary(source);

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Added));
            Assert.That(state.AuxiliaryPanes.Single(), Is.SameAs(result.NewPane));
            Assert.That(state.Panes, Is.EqualTo(new[] { state.PrimaryPane, result.NewPane }));
            Assert.That(state.ActivePane, Is.SameAs(result.NewPane));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }

        [Test]
        public void OpenAuxiliary_SameSourceActivatesExistingWithoutDuplicate()
        {
            var state = new ZepState();
            var source = ZepPaneSource.FixedTag("blue");
            var added = state.OpenAuxiliary(source).NewPane;
            state.ActivatePane(state.PrimaryPane);

            var result = state.OpenAuxiliary(ZepPaneSource.FixedTag("BLUE"));

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Activated));
            Assert.That(result.NewPane, Is.SameAs(added));
            Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(1));
            Assert.That(state.ActivePane, Is.SameAs(added));
        }

        [Test]
        public void OpenAuxiliary_AtCapacityReplacesDeterministicAuxiliary()
        {
            var state = new ZepState();
            state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            for (var i = 1; i < ZepState.MaxSplitViews; i++)
            {
                state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Extra" + i)));
            }

            var replacedPane = state.AuxiliaryPanes.Last();
            state.ActivatePane(state.PrimaryPane);

            var result = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue"));

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(result.OldPane, Is.SameAs(replacedPane));
            Assert.That(
                result.NewPane.Source,
                Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(state.AuxiliaryPanes.Count,
                Is.EqualTo(ZepState.MaxSplitViews));
            Assert.That(state.AuxiliaryPanes.Last(), Is.SameAs(result.NewPane));
            Assert.That(state.IsLivePane(replacedPane), Is.False);
            Assert.That(state.ActivePane, Is.SameAs(result.NewPane));
        }

        [Test]
        public void OpenAuxiliary_AtCapacityRejectsNonAuxiliaryReplacementTarget()
        {
            // The requested source must not already be open: the duplicate-source
            // branch activates and returns before replacement targets are validated.
            var state = new ZepState();
            state.OpenAuxiliary(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            for (var i = 1; i < ZepState.MaxSplitViews; i++)
            {
                state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Extra" + i)));
            }

            var existing = state.AuxiliaryPanes.ToArray();

            var result = state.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue"),
                state.PrimaryPane);

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Rejected));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(existing));
            Assert.That(state.ActivePane, Is.SameAs(existing.Last()));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }

        [Test]
        public void CloseAuxiliary_ActiveAndInactiveBothKeepActiveReferenceLive()
        {
            var activeState = new ZepState();
            var activeAuxiliary = activeState.OpenAuxiliary(
                ZepPaneSource.FixedTag("green")).NewPane;

            var activeClose = activeState.CloseAuxiliary(activeAuxiliary);

            Assert.That(activeClose.Kind, Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(activeState.ActivePane, Is.SameAs(activeState.PrimaryPane));
            Assert.That(activeState.AuxiliaryPanes, Is.Empty);
            Assert.That(activeState.IsLivePane(activeState.ActivePane), Is.True);

            var inactiveState = new ZepState();
            var inactiveAuxiliary = inactiveState.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue")).NewPane;
            inactiveState.ActivatePane(inactiveState.PrimaryPane);

            var inactiveClose = inactiveState.CloseAuxiliary(inactiveAuxiliary);

            Assert.That(inactiveClose.Kind, Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(inactiveState.ActivePane, Is.SameAs(inactiveState.PrimaryPane));
            Assert.That(inactiveState.IsLivePane(inactiveState.ActivePane), Is.True);
        }

        [Test]
        public void Primary_IsOutsideClosableCollectionAndHasNoGenericClosePath()
        {
            var state = new ZepState();

            var result = state.CloseAuxiliary(state.PrimaryPane);

            Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Rejected));
            Assert.That(state.Panes.Single(), Is.SameAs(state.PrimaryPane));
            Assert.That(
                typeof(ZepState).GetMethod("ClosePane"),
                Is.Null,
                "Only the auxiliary-specific closure API may exist.");
        }

        [Test]
        public void RestoreAuxiliaries_EnforcesCapacityDeduplicatesAndPublishesOnce()
        {
            var state = new ZepState();
            var green = new ZepPaneState(ZepPaneSource.FixedTag("green"));
            var duplicateGreen = new ZepPaneState(
                ZepPaneSource.FixedTag("green"));
            var tag = new ZepPaneState(
                ZepPaneSource.FixedTag("blue"));
            var changes = new List<ZepPaneChange>();
            state.PaneChanged += changes.Add;

            var restored = state.RestoreAuxiliaries(
                new[] { green, duplicateGreen, tag },
                duplicateGreen);

            Assert.That(restored, Is.True);
            Assert.That(state.AuxiliaryPanes.Count, Is.EqualTo(ZepState.MaxSplitViews));
            Assert.That(state.AuxiliaryPanes.First(), Is.SameAs(green));
            Assert.That(
                state.AuxiliaryPanes.Select(pane => pane.Source).Distinct().Count(),
                Is.EqualTo(state.AuxiliaryPanes.Count));
            Assert.That(state.ActivePane, Is.SameAs(green));
            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(ZepPaneChangeKind.Restored));
            Assert.That(changes[0].NewPane, Is.SameAs(green));

            Assert.That(
                state.RestoreAuxiliaries(new[] { tag }, tag),
                Is.False,
                "A live collection must never be replaced by a serialized snapshot.");
        }

        [Test]
        public void RestoreAuxiliaries_InvalidActiveIdentityFallsBackToPrimary()
        {
            var state = new ZepState();
            var green = new ZepPaneState(ZepPaneSource.FixedTag("green"));
            var staleActive = new ZepPaneState(
                ZepPaneSource.FixedTag("missing"));

            state.RestoreAuxiliaries(new[] { green }, staleActive);

            Assert.That(state.AuxiliaryPanes.Single(), Is.SameAs(green));
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }

        [Test]
        public void PaneNotifications_BatchUpdatesAndPreservePrimaryNavigationButtons()
        {
            var state = new ZepState();
            var changes = new List<ZepPaneChange>();
            var globalChangeCount = 0;
            var navStates = new List<(bool Back, bool Forward)>();
            state.PaneChanged += changes.Add;
            state.GlobalChanged += () => globalChangeCount++;
            state.PrimaryPane.NavButtonsStateChanged +=
                (back, forward) => navStates.Add((back, forward));

            using (state.DeferNotifications())
            {
                state.PrimaryPane.SetSearchText("icon");
                state.PrimaryPane.SetSelectedTypeFilter("Texture2D");
                state.PrimaryPane.SetSelectedAssetPath(AssetPath.From("Assets/Icon.png"));
                state.SetStatusMessage("Searching");
            }

            Assert.That(changes.Count, Is.EqualTo(1));
            Assert.That(changes[0].Kind, Is.EqualTo(ZepPaneChangeKind.Updated));
            Assert.That(changes[0].NewPane, Is.SameAs(state.PrimaryPane));
            Assert.That(
                changes[0].Update,
                Is.EqualTo(
                    ZepPaneUpdate.Search
                    | ZepPaneUpdate.Filter
                    | ZepPaneUpdate.Selection));
            Assert.That(globalChangeCount, Is.EqualTo(1));

            state.PrimaryPane.TrySetFolderSelection("Assets");
            state.PrimaryPane.SyncNavigationHistory();
            state.PrimaryPane.TrySetFolderSelection("Assets/Art");
            state.PrimaryPane.SyncNavigationHistory();
            state.PrimaryPane.TrySetFolderSelection("Assets/Audio");
            state.PrimaryPane.SyncNavigationHistory();
            state.PrimaryPane.NavigateBack();

            Assert.That(navStates.Last(), Is.EqualTo((true, true)));
            state.PrimaryPane.NavigateForward();
            Assert.That(navStates.Last(), Is.EqualTo((true, false)));
        }

        [Test]
        public void PaneChangeChannel_ReportsAddedUpdatedActivatedReplacedAndClosedDeltas()
        {
            var state = new ZepState();
            var changes = new List<ZepPaneChange>();
            state.PaneChanged += changes.Add;

            var folderSource = ZepPaneSource.RootedFolder(
                AssetPath.From("Assets/Art"));
            var folderPane = state.OpenAuxiliary(folderSource).NewPane;
            folderPane.SetSearchText("icon");
            state.OpenAuxiliary(folderSource);
            for (var i = 1; i < ZepState.MaxSplitViews; i++)
            {
                state.OpenAuxiliary(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Extra" + i)));
            }

            var tagPane = state.OpenAuxiliary(
                ZepPaneSource.FixedTag("blue"),
                folderPane).NewPane;
            state.CloseAuxiliary(tagPane);

            var expectedKinds = new List<ZepPaneChangeKind>
            {
                ZepPaneChangeKind.Added,
                ZepPaneChangeKind.Updated,
                ZepPaneChangeKind.Activated
            };
            for (var i = 1; i < ZepState.MaxSplitViews; i++)
            {
                expectedKinds.Add(ZepPaneChangeKind.Added);
            }

            expectedKinds.Add(ZepPaneChangeKind.Replaced);
            expectedKinds.Add(ZepPaneChangeKind.Closed);
            Assert.That(
                changes.Select(change => change.Kind),
                Is.EqualTo(expectedKinds));
            Assert.That(changes[0].NewSource, Is.EqualTo(folderSource));
            Assert.That(changes[1].NewPane, Is.SameAs(folderPane));
            Assert.That(changes[2].OldPane, Is.SameAs(folderPane));
            Assert.That(changes[2].NewPane, Is.SameAs(folderPane));
            var replaced = changes[changes.Count - 2];
            var closed = changes[changes.Count - 1];
            Assert.That(replaced.OldSource, Is.EqualTo(folderSource));
            Assert.That(replaced.NewSource,
                Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(closed.OldPane, Is.SameAs(tagPane));
            Assert.That(closed.NewPane, Is.SameAs(state.PrimaryPane));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }

        [Test]
        public void ConfiguredCapacity_PreservesOrderedIndependentPanesAndCloseRules()
        {
            var state = new ZepState();
            var panes = new List<ZepPaneState>();
            for (var i = 0; i < ZepState.MaxSplitViews; i++)
            {
                var root = AssetPath.From("Assets/Root" + i);
                var result = state.OpenAuxiliary(
                    ZepPaneSource.RootedFolder(root));
                Assert.That(result.Kind, Is.EqualTo(ZepSplitOperationKind.Added));
                result.NewPane.TrySetFolderSelection(root.Value + "/Child");
                result.NewPane.SetSearchText("query-" + i);
                panes.Add(result.NewPane);
            }

            Assert.That(state.AuxiliaryPanes, Is.EqualTo(panes));
            Assert.That(state.AuxiliaryPanes.Count,
                Is.EqualTo(ZepState.MaxSplitViews));
            for (var i = 0; i < panes.Count; i++)
            {
                Assert.That(panes[i].CurrentFolderPath,
                    Is.EqualTo(AssetPath.From("Assets/Root" + i + "/Child")));
                Assert.That(panes[i].SearchText, Is.EqualTo("query-" + i));
            }

            var duplicate = state.OpenAuxiliary(panes[0].Source);
            Assert.That(duplicate.Kind,
                Is.EqualTo(ZepSplitOperationKind.Activated));
            Assert.That(state.AuxiliaryPanes, Is.EqualTo(panes));

            var explicitTarget = panes[panes.Count - 1];
            var replacement = state.OpenAuxiliary(
                ZepPaneSource.FixedTag("replacement"),
                explicitTarget);
            Assert.That(replacement.Kind,
                Is.EqualTo(ZepSplitOperationKind.Replaced));
            Assert.That(state.AuxiliaryPanes.Last(), Is.SameAs(replacement.NewPane));
            Assert.That(state.AuxiliaryPanes.Count,
                Is.EqualTo(ZepState.MaxSplitViews));

            state.ActivatePane(state.PrimaryPane);
            var inactiveClose = state.CloseAuxiliary(state.AuxiliaryPanes[0]);
            Assert.That(inactiveClose.Kind,
                Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(state.ActivePane, Is.SameAs(state.PrimaryPane));

            var active = state.AuxiliaryPanes.LastOrDefault()
                ?? state.OpenAuxiliary(ZepPaneSource.FixedTag("fallback")).NewPane;
            state.ActivatePane(active);
            var activeClose = state.CloseAuxiliary(active);
            Assert.That(activeClose.Kind,
                Is.EqualTo(ZepSplitOperationKind.Closed));
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }

        [Test]
        public void PaneChangeChannel_DetachedPaneCannotPublishFurtherUpdates()
        {
            var state = new ZepState();
            var changes = new List<ZepPaneChange>();
            state.PaneChanged += changes.Add;
            var pane = state.OpenAuxiliary(ZepPaneSource.FixedTag("blue")).NewPane;
            state.CloseAuxiliary(pane);
            changes.Clear();

            pane.SetSearchText("stale");

            Assert.That(changes, Is.Empty);
            Assert.That(state.IsLivePane(state.ActivePane), Is.True);
        }
    }
}
