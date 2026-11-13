using System;
using Zep.Core.Domain;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.State
{
    // Navigation-history + create-target behavior owned by ZepState.
    // Folder-existence validation lives in the controller's ApplyProjection; the
    // history test stands in for it with a Changed subscriber, exercising the
    // state's own reaction to a redirect.
    public sealed class ZepStateNavigationTests
    {
        private static ZepState BuildHistory(params string[] folders)
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;
            foreach (var folder in folders)
            {
                pane.TrySetFolderSelection(folder);
                // The shell records history on each refresh tick; do it explicitly.
                pane.SyncNavigationHistory();
            }

            return state;
        }

        // A domain reload hands the window a brand-new pane; the window re-applies
        // the folder AND the history it captured, so Back/Forward still work on the
        // folders browsed before the reload instead of starting empty.
        [Test]
        public void RestoreNavigationHistory_ThenNavigateBack_LandsOnThePreviousFolder()
        {
            var recorded = BuildHistory("Assets", "Assets/Textures", "Assets/Prefabs").PrimaryPane;
            var entries = recorded.NavigationHistory;
            var index = recorded.NavigationHistoryIndex;
            Assert.That(entries.Count, Is.EqualTo(3), "precondition: three folders were recorded");
            Assert.That(index, Is.EqualTo(2), "precondition: the cursor sits on the newest one");

            var restored = new ZepState().PrimaryPane;
            restored.TrySetFolderSelection("Assets/Prefabs");
            restored.RestoreNavigationHistory(entries, index);

            Assert.That(restored.CanNavigateBack, Is.True);
            Assert.That(restored.CanNavigateForward, Is.False);
            restored.NavigateBack();
            Assert.That(restored.CurrentFolderPath.Value, Is.EqualTo("Assets/Textures"));
            Assert.That(restored.CanNavigateForward, Is.True, "Assets/Prefabs is ahead again");
            restored.NavigateBack();
            Assert.That(restored.CurrentFolderPath.Value, Is.EqualTo("Assets"));
            Assert.That(restored.CanNavigateBack, Is.False, "the restored history starts where the recorded one did");
        }

        [Test]
        public void NavigateBack_OntoDeletedFolder_RewritesSlotToShownFolderAndKeepsNavButtonsCoherent()
        {
            const string deadFolder = "Assets/Gone";
            var state = BuildHistory("Assets", deadFolder, "Assets/Keep");
            var pane = state.PrimaryPane;

            // Stand in for the controller's ApplyProjection: a folder that no longer
            // exists gets redirected to Assets on the next Changed notification.
            var redirecting = false;
            state.PaneChanged += change =>
            {
                if (!ReferenceEquals(change.NewPane, pane)) return;
                if (redirecting) return;
                if (string.Equals(pane.CurrentFolderPath.Value, deadFolder, StringComparison.OrdinalIgnoreCase))
                {
                    redirecting = true;
                    try { pane.TrySetCurrentFolderPath(AssetPath.From("Assets")); }
                    finally { redirecting = false; }
                }
            };

            // Back from Assets/Keep targets the now-deleted Assets/Gone.
            pane.NavigateBack();

            // We land on Assets (the redirect), and both the history slot and the
            // nav-button state track that instead of the phantom Assets/Gone.
            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets"));
            Assert.That(pane.CurrentEntry.FolderPath, Is.EqualTo("Assets"));
            Assert.That(pane.CanNavigateBack, Is.True, "The original Assets slot still sits behind us.");
            Assert.That(pane.CanNavigateForward, Is.True, "Assets/Keep still sits ahead of us.");
        }

        [Test]
        public void NavigateBack_OntoLiveFolder_LandsThereNormally()
        {
            var state = BuildHistory("Assets", "Assets/A", "Assets/B");
            var pane = state.PrimaryPane;

            pane.NavigateBack();

            Assert.That(pane.CurrentFolderPath.Value, Is.EqualTo("Assets/A"));
            Assert.That(pane.CurrentEntry.FolderPath, Is.EqualTo("Assets/A"));
            Assert.That(pane.CanNavigateBack, Is.True);
            Assert.That(pane.CanNavigateForward, Is.True);
        }

        [Test]
        public void ResolveCreateTargetFolderPath_PrefersSelectedFolderOverBrowsedFolder()
        {
            // The toolbar +, Cmd+N, and right-click "New X" all resolve their target
            // through this method: a folder selected-but-not-opened must win
            // over the folder currently browsed.
            var state = new ZepState();
            var pane = state.PrimaryPane;
            var subfolder = new ZepAssetEntry("Sub", "Assets/Parent/Sub", "Folder");
            pane.VisibleAssetsProvider = () => new[] { subfolder };
            pane.TrySetCurrentFolderPath(AssetPath.From("Assets/Parent"));
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Parent/Sub") });

            Assert.That(
                pane.ResolveCreateTargetFolderPath(state.IsFolderAssetEntry),
                Is.EqualTo("Assets/Parent/Sub"));
        }

        // A background right-click targets the browsed folder, as Unity's
        // two-column Project window does; the shell feeds the result to the
        // Unity Assets-menu validators, so it must be a folder Unity can load.
        [Test]
        public void ResolveBackgroundContextFolderPath_ReturnsTheBrowsedFolder()
        {
            var pane = new ZepState().PrimaryPane;
            pane.TrySetCurrentFolderPath(AssetPath.From("Assets/Parent"));

            Assert.That(
                pane.ResolveBackgroundContextFolderPath(_ => true),
                Is.EqualTo("Assets/Parent"));
        }

        [Test]
        public void ResolveBackgroundContextFolderPath_YieldsNothingWithoutAnIndexedFolder()
        {
            var pane = new ZepState().PrimaryPane;
            pane.TrySetCurrentFolderPath(AssetPath.From("Assets/Parent"));

            Assert.That(pane.ResolveBackgroundContextFolderPath(_ => false), Is.Null);
        }

        [TestCase("Packages")]
        public void ResolveBackgroundContextFolderPath_SkipsViewsWithoutABrowsedFolderAsset(string browsePath)
        {
            var pane = new ZepState().PrimaryPane;
            Assume.That(pane.TrySetCurrentFolderPath(AssetPath.From(browsePath)), Is.True);

            Assert.That(pane.ResolveBackgroundContextFolderPath(_ => true), Is.Null);
        }

        // IsFolderAssetEntry is asked once per selected asset by Enter, the
        // context-menu build, and create-target resolution, against the whole
        // project's folder catalog. It answers from a membership index now, so
        // these two cases pin what the index must preserve: the comparison is
        // case-insensitive (a file entry whose path matches a folder's path in
        // a different case is still a folder), and the index is invalidated
        // when the folder list is replaced — a stale index would keep
        // reporting a removed folder, which is the failure mode a derived set
        // introduces and the scan it replaces could not have.
        [Test]
        public void IsFolderAssetEntry_MatchesAFolderPathRegardlessOfCase()
        {
            var state = new ZepState();
            var folders = new ZepFolderEntry[10000];
            for (var i = 0; i < folders.Length; i++)
            {
                folders[i] = new ZepFolderEntry("Folder" + i, "Assets/Folder" + i, 1, false);
            }

            state.SetFolders(folders);

            var differingCase = new ZepAssetEntry(
                "folder7777", "assets/FOLDER7777", "TextAsset");
            Assert.That(
                state.IsFolderAssetEntry(differingCase),
                Is.True,
                "path matching must stay OrdinalIgnoreCase");
            Assert.That(
                state.IsFolderAssetEntry(
                    new ZepAssetEntry("Loose", "Assets/Folder7777.txt", "TextAsset")),
                Is.False,
                "a path that merely starts with a folder path is not that folder");
            Assert.That(
                state.IsFolderAssetEntry(
                    new ZepAssetEntry("Sub", "Assets/Folder7777/Child", "Folder")),
                Is.True,
                "an entry that declares itself a folder needs no catalog lookup");
        }

        [Test]
        public void IsFolderAssetEntry_AfterTheFolderListIsReplaced_ForgetsTheRemovedPath()
        {
            var state = new ZepState();
            state.SetFolders(new[]
            {
                new ZepFolderEntry("Kept", "Assets/Kept", 1, false),
                new ZepFolderEntry("Removed", "Assets/Removed", 1, false),
            });

            var removed = new ZepAssetEntry("Removed", "Assets/Removed", "TextAsset");
            Assert.That(state.IsFolderAssetEntry(removed), Is.True);

            state.SetFolders(new[]
            {
                new ZepFolderEntry("Kept", "Assets/Kept", 1, false),
            });

            Assert.That(
                state.IsFolderAssetEntry(removed),
                Is.False,
                "the folder index must be rebuilt when the folder list changes");
            Assert.That(
                state.IsFolderAssetEntry(
                    new ZepAssetEntry("Kept", "Assets/Kept", "TextAsset")),
                Is.True,
                "surviving folders must still be found after the rebuild");
        }
    }
}
