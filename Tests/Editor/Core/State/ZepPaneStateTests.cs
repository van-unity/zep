using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.TestInfrastructure;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.State
{
    public sealed class ZepPaneStateTests
    {
        [Test]
        public void Primary_InitialStateAndNavigationMatchExistingBehavior()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            Assert.That(pane.Source, Is.EqualTo(ZepPaneSource.Primary));
            Assert.That(state.ActivePane, Is.SameAs(pane));
            Assert.That(state.AuxiliaryPanes, Is.Empty);
            Assert.That(pane.SearchText, Is.Empty);
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));

            pane.TrySetFolderSelection("Assets");
            pane.SyncNavigationHistory();
            pane.TrySetFolderSelection("Assets/Art");
            pane.SyncNavigationHistory();
            pane.TrySetFolderSelection("Assets/Audio");
            pane.SyncNavigationHistory();

            pane.NavigateBack();

            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(pane.CurrentEntry, Is.EqualTo(NavigationEntry.Folder("Assets/Art")));
            Assert.That(pane.CanNavigateBack, Is.True);
            Assert.That(pane.CanNavigateForward, Is.True);
        }

        // Navigation history only ever shrank on a forward branch, so a long
        // editing session grew it one entry per navigation for the life of the
        // pane. It is capped at 256 now, trimmed from the FRONT: the newest 256
        // navigations survive and Back still walks all of them.
        [Test]
        public void ThreeHundredNavigations_KeepTheNewestTwoHundredAndFiftySixAndBackStillWalksThem()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            for (var i = 0; i < 300; i++)
            {
                Assert.That(pane.TrySetFolderSelection("Assets/F" + i), Is.True);
                pane.SyncNavigationHistory();
            }

            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/F299")));

            var stepsBack = 0;
            while (pane.CanNavigateBack)
            {
                pane.NavigateBack();
                stepsBack++;
                Assert.That(stepsBack, Is.LessThan(400), "Back must terminate");
            }

            Assert.That(
                stepsBack,
                Is.EqualTo(255),
                "256 entries means 255 Back steps from the newest one");
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/F44")),
                "the oldest surviving entry is the 300th-from-last, not the first navigation");
            Assert.That(pane.CanNavigateForward, Is.True, "the trimmed history is still walkable forward");
        }

        [Test]
        public void ProjectedAssets_PublishesTheStableResultReferenceOnce()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;
            var result = new[]
            {
                new ZepAssetEntry(
                    "Hero",
                    "Assets/Hero.png",
                    "Texture2D")
            };
            var updates = new List<ZepPaneUpdate>();
            pane.Changed += (_, update) => updates.Add(update);

            pane.SetProjectedAssets(result);
            pane.SetProjectedAssets(result);

            Assert.That(pane.ProjectedAssets, Is.SameAs(result));
            Assert.That(updates,
                Is.EqualTo(new[] { ZepPaneUpdate.Projection }));
        }

        // Search exit reveals the result the user PICKED. A selection made
        // before the search — which survives navigation, unlike the search
        // itself — must not become the reveal target, or cancelling a search
        // the user only typed into drags them back to a folder they left.
        [Test]
        public void SearchExit_WithoutPickingAResult_LeavesThePaneWhereItIs()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            pane.TrySetFolderSelection("Assets/Art");
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Art/Hero.png") });
            pane.TrySetFolderSelection("Assets/Audio");
            pane.SetSearchText("hero");

            Assert.That(
                pane.TryRevealPickedSearchResultOnExit(out _),
                Is.False,
                "Nothing was picked in this search, so there is no result to return to.");
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Audio")));
            Assert.That(pane.SelectedAssetPaths, Is.Empty);
        }

        [Test]
        public void SearchExit_AfterPickingAResult_ReopensAtItsFolderWithItSelected()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            pane.TrySetFolderSelection("Assets/Audio");
            pane.SetSearchText("hero");
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Art/UI/Hero.png") });

            Assert.That(pane.TryRevealPickedSearchResultOnExit(out var asset), Is.True);
            Assert.That(asset, Is.EqualTo(AssetPath.From("Assets/Art/UI/Hero.png")));
            Assert.That(pane.SearchText, Is.Empty);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(pane.SelectedFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                pane.SelectedAssetPaths,
                Is.EqualTo(new[] { AssetPath.From("Assets/Art/UI/Hero.png") }));

            // The pick belonged to the finished search; a later search the user
            // types into but never picks from must not resurrect it.
            pane.TrySetFolderSelection("Assets/Audio");
            pane.SetSearchText("hero");
            Assert.That(pane.TryRevealPickedSearchResultOnExit(out _), Is.False);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Audio")));
        }

        // A tag view has no containing folder to reveal into — the tag IS the
        // container. Clearing the search must end the search and frame the pick,
        // not drop the tag filter and navigate away from the view the user was
        // searching in.
        [Test]
        public void SearchExit_InsideATagView_KeepsTheTagViewAndFramesThePick()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            pane.TrySetFolderSelection("Assets/Art");
            Assert.That(pane.ApplyTagView("red"), Is.True);
            pane.SetSelectedSearchScope(ZepSearchScope.Assets);
            pane.SetSearchText("hero");
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Art/UI/Hero.png") });

            Assert.That(pane.TryRevealPickedSearchResultOnExit(out var asset), Is.True);
            Assert.That(asset, Is.EqualTo(AssetPath.From("Assets/Art/UI/Hero.png")));
            Assert.That(pane.SearchText, Is.Empty);
            Assert.That(
                pane.ActiveMode,
                Is.EqualTo(AssetsPaneMode.Tags),
                "cancelling a search inside a tag view must not drop the tag filter");
            Assert.That(pane.SelectedTagFilterId, Is.EqualTo("red"));
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art")),
                "a tag view does not browse, so the exit must not navigate");
            Assert.That(
                pane.SelectedAssetPaths,
                Is.EqualTo(new[] { AssetPath.From("Assets/Art/UI/Hero.png") }));

            // The tag scope the user chose is still the tag preference, and the
            // browser's remembered folder was never pointed at the pick — it is
            // still the untouched default, not the pick's parent.
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(pane.EnterMode(AssetsPaneMode.Browser), Is.True);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets")));
        }

        [Test]
        public void TwoPanes_OwnIndependentNavigationSearchFilterSelectionAndHistory()
        {
            var art = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            var audio = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Audio")));

            art.SyncNavigationHistory();
            art.TrySetFolderSelection("Assets/Art/UI");
            art.SyncNavigationHistory();
            art.SetSearchText("icon");
            art.SetSelectedSearchScope(ZepSearchScope.CurrentFolder);
            art.SetSelectedTypeFilter("Texture2D");
            art.SetTypeFilterOptions(new[]
            {
                new ZepTypeFilterOption("Texture2D", 1)
            });
            art.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Art/UI/Icon.png") });
            art.VisibleAssetsProvider = () => new[]
            {
                new ZepAssetEntry("Icon", "Assets/Art/UI/Icon.png", "Texture2D")
            };

            audio.SyncNavigationHistory();
            audio.TrySetFolderSelection("Assets/Audio/Music");
            audio.SyncNavigationHistory();
            audio.SetSearchText("theme");
            audio.SetSelectedSearchScope(ZepSearchScope.Assets);
            audio.SetSelectedTypeFilter("AudioClip");
            audio.SetTypeFilterOptions(new[]
            {
                new ZepTypeFilterOption("AudioClip", 1)
            });
            audio.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/Audio/Music/Theme.wav") });
            audio.VisibleAssetsProvider = () => new[]
            {
                new ZepAssetEntry("Theme", "Assets/Audio/Music/Theme.wav", "AudioClip")
            };

            Assert.That(art.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(art.SearchText, Is.EqualTo("icon"));
            Assert.That(art.SelectedSearchScope, Is.EqualTo(ZepSearchScope.CurrentFolder));
            Assert.That(art.SelectedTypeFilter, Is.EqualTo("Texture2D"));
            Assert.That(art.TypeFilterOptions.Single().TypeName, Is.EqualTo("Texture2D"));
            Assert.That(art.SelectedAssetPath, Is.EqualTo(AssetPath.From("Assets/Art/UI/Icon.png")));
            Assert.That(art.LastSelectedAssetPath, Is.EqualTo(AssetPath.From("Assets/Art/UI/Icon.png")));
            Assert.That(art.GetSelectedAssetEntries().Single().Path, Is.EqualTo("Assets/Art/UI/Icon.png"));
            Assert.That(art.CanNavigateBack, Is.True);

            Assert.That(audio.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Audio/Music")));
            Assert.That(audio.SearchText, Is.EqualTo("theme"));
            Assert.That(audio.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(audio.SelectedTypeFilter, Is.EqualTo("AudioClip"));
            Assert.That(audio.TypeFilterOptions.Single().TypeName, Is.EqualTo("AudioClip"));
            Assert.That(audio.SelectedAssetPath, Is.EqualTo(AssetPath.From("Assets/Audio/Music/Theme.wav")));
            Assert.That(audio.LastSelectedAssetPath, Is.EqualTo(AssetPath.From("Assets/Audio/Music/Theme.wav")));
            Assert.That(audio.GetSelectedAssetEntries().Single().Path, Is.EqualTo("Assets/Audio/Music/Theme.wav"));
            Assert.That(audio.CanNavigateBack, Is.True);
        }

        [Test]
        public void RootedFolder_RejectsSiblingPrefixCollisionAndAboveRootNavigation()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));

            Assert.That(pane.TrySetFolderSelection("Assets/Art"), Is.True);
            Assert.That(pane.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(pane.BreadcrumbRootPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(pane.BreadcrumbCurrentPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));

            Assert.That(pane.TrySetFolderSelection("Assets/Audio"), Is.False);
            Assert.That(pane.TrySetFolderSelection("Assets/Art2"), Is.False);
            Assert.That(pane.TrySetFolderSelection("Assets"), Is.False);
            Assert.That(pane.TrySetCurrentFolderPath(AssetPath.From("Packages/Other")), Is.False);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));

            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Art/UI/Icon.png"),
                AssetPath.From("Assets/Art2/Leaked.png")
            });
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[]
            {
                AssetPath.From("Assets/Art/UI/Icon.png")
            }));
        }

        [Test]
        public void RootedFolder_SearchScopesAndResultsStayInsideSegmentAwareRoot()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));

            pane.SetSearchScopeOptions(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "Current")
            });

            Assert.That(
                pane.SearchScopeOptions.Select(option => option.Scope),
                Is.EqualTo(new[]
                {
                    ZepSearchScope.Assets,
                    ZepSearchScope.CurrentFolder
                }));
            Assert.That(pane.SetSelectedSearchScope(ZepSearchScope.All), Is.False);
            Assert.That(pane.SetSelectedSearchScope(ZepSearchScope.Packages), Is.False);
            Assert.That(pane.SetSelectedSearchScope(ZepSearchScope.CurrentFolder), Is.True);
            Assert.That(pane.IsSearchResultAllowed(AssetPath.From("Assets/Art")), Is.True);
            Assert.That(pane.IsSearchResultAllowed(AssetPath.From("Assets/Art/UI/Icon.png")), Is.True);
            Assert.That(pane.IsSearchResultAllowed(AssetPath.From("Assets/Art2/Icon.png")), Is.False);
            Assert.That(pane.IsSearchResultAllowed(AssetPath.From("Assets/Audio/Icon.png")), Is.False);
        }

        // Re-asserting the selection that is already current is the hot path
        // (every projection reconcile does it) and now returns before it
        // allocates the array, list and set the general setter needs. The fast
        // path must be a true no-op, and must not swallow a real change: the
        // second half moves the selection to a sibling to prove it still lands.
        [Test]
        public void SetSelectedAssetPath_ReAssertingTheCurrentSelection_IsANoOpButStillAcceptsAChange()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            var selection = AssetPath.From("Assets/Art/UI/Icon.png");
            pane.SetSelectedAssetPath(selection);

            var updates = 0;
            pane.Changed += (_, __) => updates++;
            pane.SetSelectedAssetPath(selection);

            Assert.That(updates, Is.Zero, "an unchanged selection must not notify");
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { selection }));
            Assert.That(pane.SelectedAssetPath, Is.EqualTo(selection));

            var sibling = AssetPath.From("Assets/Art/UI/Other.png");
            pane.SetSelectedAssetPath(sibling);

            Assert.That(updates, Is.EqualTo(1));
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { sibling }));
        }

        // Selecting outside a rooted pane is rejected whether or not the fast
        // path above is in play: a single path that the scope disallows must
        // leave the pane with nothing selected, not with that path.
        [Test]
        public void SetSelectedAssetPath_OutsideTheRootedSource_LeavesTheSelectionEmpty()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));

            pane.SetSelectedAssetPath(AssetPath.From("Assets/Audio/Clip.wav"));

            Assert.That(pane.SelectedAssetPaths, Is.Empty);
            Assert.That(pane.SelectedAssetPath.IsEmpty, Is.True);
        }

        [Test]
        public void RootedFolder_ReconcileSourceAtomicallyRewritesRootStateAndHistory()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            pane.SyncNavigationHistory();
            pane.TrySetFolderSelection("Assets/Art/UI/Icons");
            pane.SyncNavigationHistory();
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Art/UI/Icons/Add.png")
            });

            var reconciled = pane.TryReconcileSource(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Illustration")),
                new[]
                {
                    AssetPath.From("Assets"),
                    AssetPath.From("Assets/Illustration"),
                    AssetPath.From("Assets/Illustration/UI"),
                    AssetPath.From("Assets/Illustration/UI/Icons")
                });

            Assert.That(reconciled, Is.True);
            Assert.That(
                pane.Source,
                Is.EqualTo(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Illustration"))));
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icons")));
            Assert.That(
                pane.SelectedAssetPaths,
                Is.EqualTo(new[]
                {
                    AssetPath.From("Assets/Illustration/UI/Icons/Add.png")
                }));
            Assert.That(
                pane.LastSelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icons/Add.png")));

            pane.NavigateBack();
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration")));
            pane.NavigateForward();
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icons")));
        }

        // The pick is path-keyed like every other field the reconcile rewrites.
        // Left behind, it points outside the new root, IsSearchResultAllowed
        // rejects it, and the search the user is still typing loses its exit
        // reveal for no visible reason.
        [Test]
        public void RootedFolder_ReconcileSourceMovesThePickedSearchResultWithTheRoot()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            pane.SetSearchText("add");
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Art/UI/Icons/Add.png")
            });

            Assert.That(
                pane.TryReconcileSource(
                    ZepPaneSource.RootedFolder(
                        AssetPath.From("Assets/Illustration")),
                    new[]
                    {
                        AssetPath.From("Assets"),
                        AssetPath.From("Assets/Illustration"),
                        AssetPath.From("Assets/Illustration/UI"),
                        AssetPath.From("Assets/Illustration/UI/Icons")
                    }),
                Is.True);

            Assert.That(pane.TryRevealPickedSearchResultOnExit(out var asset), Is.True);
            Assert.That(
                asset,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icons/Add.png")));
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Illustration/UI/Icons")));
        }

        [Test]
        public void RootedFolder_ReconcileSourceMovesExpansionWithTheRootAndPrunesDeadKeys()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/UI"), true);
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Art/Retired"), true);

            var reconciled = pane.TryReconcileSource(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Illustration")),
                new[]
                {
                    AssetPath.From("Assets"),
                    AssetPath.From("Assets/Illustration"),
                    AssetPath.From("Assets/Illustration/UI")
                });

            Assert.That(reconciled, Is.True);
            // Retired rewrites to a path the live set does not contain, so it is
            // pruned rather than carried across — a blind rewrite would keep it.
            Assert.That(
                pane.ExpandedAssetFolderPaths,
                Is.EquivalentTo(new[] { AssetPath.From("Assets/Illustration/UI") }));
            Assert.That(
                pane.IsAssetFolderExpanded(AssetPath.From("Assets/Art/UI")),
                Is.False,
                "the pre-move key must not survive: a folder recreated there would inherit the expansion");
        }

        [Test]
        public void RootedFolder_ReconcileMissingRootRejectsWithoutPartialMutation()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));
            pane.TrySetFolderSelection("Assets/Art/UI");
            pane.SetSelectedAssetPath(AssetPath.From("Assets/Art/UI/Icon.png"));

            var reconciled = pane.TryReconcileSource(
                ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Illustration")),
                new[] { AssetPath.From("Assets") });

            Assert.That(reconciled, Is.False);
            Assert.That(
                pane.Source,
                Is.EqualTo(ZepPaneSource.RootedFolder(
                    AssetPath.From("Assets/Art"))));
            Assert.That(
                pane.CurrentFolderPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                pane.SelectedAssetPath,
                Is.EqualTo(AssetPath.From("Assets/Art/UI/Icon.png")));
        }

        [Test]
        public void PaneSources_CompareByMeaningAndRejectInvalidConstruction()
        {
            Assert.That(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art/")),
                Is.EqualTo(ZepPaneSource.RootedFolder(AssetPath.From("assets/art"))));
            Assert.That(
                ZepPaneSource.FixedTag("Favorite"),
                Is.EqualTo(ZepPaneSource.FixedTag("favorite")));
            Assert.That(
                ZepPaneSource.Primary,
                Is.Not.EqualTo(ZepPaneSource.FixedTag("favorite")));
            Assert.Throws<ArgumentException>(() =>
                ZepPaneSource.RootedFolder(AssetPath.From("Outside")));
            Assert.Throws<ArgumentException>(() => ZepPaneSource.FixedTag(" "));
        }

        [Test]
        public void FixedTag_SearchClearingAndHistoryCannotChangeSourceIdentity()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));

            tag.SyncNavigationHistory();
            tag.SetSearchText("icon");
            tag.SetSearchText(string.Empty);
            Assert.That(tag.SetSelectedTagFilterId(string.Empty), Is.False);
            Assert.That(tag.SetSelectedTagFilterId("red"), Is.False);
            Assert.That(tag.ApplyEntry(NavigationEntry.Tag("red")), Is.False);
            Assert.That(tag.ApplyTagView("red"), Is.False);
            tag.NavigateBack();
            tag.NavigateForward();

            Assert.That(tag.Source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tag.SelectedTagFilterId, Is.EqualTo("blue"));
        }

        // Opening a folder from a tag pane's list used to be refused (and the
        // shell bounced the user to the primary pane). The pane now browses the
        // folder in place, under the tag, with the folder as its ceiling.
        [Test]
        public void TagPane_OpeningAFolderFromItsList_BrowsesItInPlaceUnderTheTag()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            tag.SyncNavigationHistory();
            tag.SetSearchText("ico");

            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            tag.SyncNavigationHistory();

            Assert.That(tag.Source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
            Assert.That(
                tag.SelectedTagFilterId,
                Is.Empty,
                "a tag filter would make the projection list the tag, not the folder");
            Assert.That(tag.TagBrowseRootPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(tag.BreadcrumbRootPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(tag.SelectedFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(tag.SearchText, Is.Empty);
            Assert.That(tag.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
            Assert.That(
                tag.CurrentEntry,
                Is.EqualTo(NavigationEntry.Folder("Assets/Art/UI", "Assets/Art/UI")));
        }

        [Test]
        public void TagPane_CannotClimbAboveTheFolderItOpened()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.False);
            Assert.That(tag.TrySetFolderSelection("Assets"), Is.False);
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI/Icons"), Is.True);
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI/Icons")));
            Assert.That(tag.IsPathWithinSource(AssetPath.From("Assets/Art/Hero.png")), Is.False);
            Assert.That(tag.IsPathWithinSource(AssetPath.From("Assets/Art/UI/Icon.png")), Is.True);
            Assert.That(tag.IsSearchScopeAllowed(ZepSearchScope.All), Is.False);
            Assert.That(tag.IsSearchScopeAllowed(ZepSearchScope.Assets), Is.True);
            Assert.That(tag.IsSearchScopeAllowed(ZepSearchScope.CurrentFolder), Is.True);
        }

        [Test]
        public void TagPane_TheTagCrumbReturnsToTheListAndDropsTheFolderState()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.True);
            tag.SyncNavigationHistory();
            tag.SetSearchText("hero");

            Assert.That(tag.EnterMode(AssetsPaneMode.Tags), Is.True);
            tag.SyncNavigationHistory();

            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tag.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(tag.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(tag.BreadcrumbRootPath.IsEmpty, Is.True);
            Assert.That(tag.SearchText, Is.Empty);
            Assert.That(tag.SelectedSearchScope, Is.EqualTo(ZepSearchScope.All));
            Assert.That(tag.CurrentEntry, Is.EqualTo(NavigationEntry.Tag("blue")));
            Assert.That(
                tag.EnterMode(AssetsPaneMode.Browser),
                Is.False,
                "Browser is reached by opening a folder, never bare");
        }

        [Test]
        public void TagPane_BackReachesTheListAndForwardReentersUnderTheRecordedCeiling()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.True);
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            tag.SyncNavigationHistory();

            tag.NavigateBack();
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));

            tag.NavigateBack();
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tag.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(tag.CurrentEntry, Is.EqualTo(NavigationEntry.Tag("blue")));
            Assert.That(tag.CanNavigateBack, Is.False);
            Assert.That(tag.CanNavigateForward, Is.True);

            tag.NavigateForward();
            tag.NavigateForward();
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(
                tag.TagBrowseRootPath,
                Is.EqualTo(AssetPath.From("Assets/Art")),
                "re-entry restores the ceiling the entry was recorded under");
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.True);
        }

        [Test]
        public void TagPane_OpeningFromTheListAlwaysMakesThatFolderTheCeiling()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.True);
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            tag.SyncNavigationHistory();
            tag.NavigateBack();
            tag.NavigateBack();

            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            Assert.That(tag.TagBrowseRootPath, Is.EqualTo(AssetPath.From("Assets/Art/UI")));
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.False);
        }

        [Test]
        public void TagPane_DeletedOpenedFolder_FallsBackToTheListInsteadOfClosing()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);

            tag.ReconcileTagBrowseRoot(
                AssetPath.From("Assets/Art/UI"),
                path => path.Value != "Assets/Art/UI");

            Assert.That(tag.Source, Is.EqualTo(ZepPaneSource.FixedTag("blue")));
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tag.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(tag.SelectedTagFilterId, Is.EqualTo("blue"));
        }

        [Test]
        public void TagPane_MovedOpenedFolder_RewritesTheCeilingCurrentFolderAndHistory()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art"), Is.True);
            tag.SyncNavigationHistory();
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            tag.SyncNavigationHistory();

            tag.ReconcileTagBrowseRoot(
                AssetPath.From("Assets/Gfx"),
                path => path.Value == "Assets/Gfx" || path.Value == "Assets/Gfx/UI");

            Assert.That(tag.TagBrowseRootPath, Is.EqualTo(AssetPath.From("Assets/Gfx")));
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Gfx/UI")));
            Assert.That(
                tag.CurrentEntry,
                Is.EqualTo(NavigationEntry.Folder("Assets/Gfx/UI", "Assets/Gfx")));

            tag.NavigateBack();
            Assert.That(tag.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Gfx")));
            tag.NavigateBack();
            Assert.That(
                tag.CurrentEntry,
                Is.EqualTo(NavigationEntry.Tag("blue")),
                "the list entry is not path-keyed and survives the move");
        }

        [Test]
        public void PrimaryPane_RestoresTagAndBrowserSearchScopesIndependently()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            pane.SetSelectedSearchScope(ZepSearchScope.Packages);

            Assert.That(pane.ApplyTagView("blue"), Is.True);
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.All));
            pane.SetSelectedSearchScope(ZepSearchScope.Assets);

            Assert.That(pane.EnterMode(AssetsPaneMode.Browser), Is.True);
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Packages));

            Assert.That(pane.ApplyTagView("blue"), Is.True);
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.Assets));
        }

        [Test]
        public void PrimaryPane_ProjectionReconcileAtTheRoot_KeepsTheRememberedFolderScope()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetSelectedSearchScope(ZepSearchScope.CurrentFolder);

            // At the Assets root the projection's option list has no
            // CurrentFolder entry, so the resolver falls back to All.
            pane.TrySetFolderSelection("Assets");
            pane.ReconcileSelectedSearchScope(ZepSearchScope.All);
            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.All));

            pane.TrySetFolderSelection("Assets/Art");

            Assert.That(
                pane.SelectedSearchScope,
                Is.EqualTo(ZepSearchScope.CurrentFolder),
                "a reconciled fallback at the root must not become the remembered Browser scope");
        }

        [Test]
        public void PrimaryPane_UserRePickingTheReconciledScope_ReplacesTheMemory()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            pane.TrySetFolderSelection("Assets/Art");
            pane.SetSelectedSearchScope(ZepSearchScope.CurrentFolder);
            pane.TrySetFolderSelection("Assets");
            pane.ReconcileSelectedSearchScope(ZepSearchScope.All);

            // Same value the reconcile already applied — but this time the user
            // asked for it, so it is the preference from now on.
            pane.SetSelectedSearchScope(ZepSearchScope.All);
            pane.TrySetFolderSelection("Assets/Art");

            Assert.That(pane.SelectedSearchScope, Is.EqualTo(ZepSearchScope.All));
        }

        // -------------------------------------------------------------------
        // List-view folder expansion (Finder-style disclosure) state.
        // -------------------------------------------------------------------

        [Test]
        public void FolderExpansion_ToggleAddsAndRemoves_AndRaisesExpansionUpdate()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            var updates = new List<ZepPaneUpdate>();
            pane.Changed += (_, update) => updates.Add(update);
            var folder = AssetPath.From("Assets/Art/UI");

            pane.ToggleAssetFolderExpanded(folder);

            Assert.That(pane.IsAssetFolderExpanded(folder), Is.True);
            Assert.That(pane.ExpandedAssetFolderPaths, Is.EqualTo(new[] { folder }));
            Assert.That(updates, Is.EqualTo(new[] { ZepPaneUpdate.Expansion }));

            pane.ToggleAssetFolderExpanded(folder);

            Assert.That(pane.IsAssetFolderExpanded(folder), Is.False);
            Assert.That(pane.ExpandedAssetFolderPaths, Is.Empty);
        }

        [Test]
        public void FolderExpansion_RedundantSet_DoesNotRaise()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            var folder = AssetPath.From("Assets/Art/UI");
            pane.SetAssetFolderExpanded(folder, true);

            var raised = 0;
            pane.Changed += (_, _) => raised++;
            pane.SetAssetFolderExpanded(folder, true);
            pane.SetAssetFolderExpanded(AssetPath.From("Assets/Missing"), false);
            pane.SetAssetFolderExpanded(default, true);

            Assert.That(raised, Is.Zero);
        }

        [Test]
        public void FolderExpansion_CollapseWithDescendantSelected_MovesSelectionToTheFolderInOneRaise()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            var folder = AssetPath.From("Assets/Art/UI");
            pane.SetAssetFolderExpanded(folder, true);
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Art/UI/Icon.png"),
                AssetPath.From("Assets/Art/UI/Deep/Nested.png"),
            });

            var updates = new List<ZepPaneUpdate>();
            pane.Changed += (_, update) => updates.Add(update);

            pane.SetAssetFolderExpanded(folder, false);

            // Finder behaviour: the rows just disappeared, so the folder that
            // swallowed them takes the selection — in one coalesced raise so
            // downstream reconciliation sees the final state.
            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { folder }));
            Assert.That(updates, Is.EqualTo(new[]
            {
                ZepPaneUpdate.Selection | ZepPaneUpdate.Expansion
            }));
        }

        [Test]
        public void FolderExpansion_CollapseWithUnrelatedSelection_LeavesSelectionAlone()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            var folder = AssetPath.From("Assets/Art/UI");
            var unrelated = AssetPath.From("Assets/Audio/Theme.wav");
            pane.SetAssetFolderExpanded(folder, true);
            pane.SetSelectedAssetPaths(new[] { unrelated });

            pane.SetAssetFolderExpanded(folder, false);

            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { unrelated }));
        }

        [Test]
        public void FolderExpansion_CollapseWithTheFolderItselfSelected_KeepsThatSelection()
        {
            var pane = new ZepPaneState(ZepPaneSource.Primary);
            var folder = AssetPath.From("Assets/Art/UI");
            pane.SetAssetFolderExpanded(folder, true);
            pane.SetSelectedAssetPaths(new[] { folder });

            pane.SetAssetFolderExpanded(folder, false);

            Assert.That(pane.SelectedAssetPaths, Is.EqualTo(new[] { folder }));
        }

        // In a tag view the tag IS the container being browsed (ActiveMode
        // derives Tags from it), not a filter narrowing one. Chrome that asks
        // "is this view filtered?" — the empty-state message — must count only
        // what a user can stack on top of any container: search and the type
        // filter.
        [Test]
        public void FilterBeyondContainer_DoesNotCountTheTagThatIsTheContainer()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            Assert.That(pane.ApplyTagView("blue"), Is.True);
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(pane.HasActiveAssetFilter(), Is.True);
            Assert.That(
                pane.HasAssetFilterBeyondContainer(),
                Is.False,
                "an untouched tag view is a container, not a filtered one");

            pane.SetSearchText("hero");
            Assert.That(pane.HasAssetFilterBeyondContainer(), Is.True);

            pane.SetSearchText(string.Empty);
            pane.SetSelectedTypeFilter("Texture2D");
            Assert.That(pane.HasAssetFilterBeyondContainer(), Is.True);
        }

        // The shell reads the selected entries twice in one block and the
        // context menu once per action it offers; each read used to scan the
        // whole visible list and allocate a set to do it.
        [Test]
        public void GetSelectedAssetEntries_IsRebuiltOnlyWhenItsInputsMove()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;
            var visible = new List<ZepAssetEntry>
            {
                new ZepAssetEntry("A", "Assets/A.asset", "ScriptableObject"),
                new ZepAssetEntry("B", "Assets/B.asset", "ScriptableObject"),
                new ZepAssetEntry("C", "Assets/C.asset", "ScriptableObject"),
            };
            pane.VisibleAssetsProvider = () => visible;
            pane.SetSelectedAssetPaths(new[] { AssetPath.From("Assets/B.asset") });

            var first = pane.GetSelectedAssetEntries();
            Assert.That(first.Select(e => e.Path), Is.EqualTo(new[] { "Assets/B.asset" }));
            Assert.That(
                pane.GetSelectedAssetEntries(),
                Is.SameAs(first),
                "nothing moved between the two reads");

            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/A.asset"),
                AssetPath.From("Assets/C.asset"),
            });
            var afterSelection = pane.GetSelectedAssetEntries();
            Assert.That(afterSelection, Is.Not.SameAs(first));
            Assert.That(
                afterSelection.Select(e => e.Path),
                Is.EqualTo(new[] { "Assets/A.asset", "Assets/C.asset" }),
                "and in visible order, not selection order");

            // A new projection with the same selection still has to be re-read:
            // the entries themselves are new instances.
            var republished = new List<ZepAssetEntry>
            {
                new ZepAssetEntry("A", "Assets/A.asset", "ScriptableObject"),
                new ZepAssetEntry("C", "Assets/C.asset", "ScriptableObject"),
            };
            visible = republished;
            var afterProjection = pane.GetSelectedAssetEntries();
            Assert.That(afterProjection, Is.Not.SameAs(afterSelection));
            Assert.That(afterProjection[0], Is.SameAs(republished[0]));

            // An inline-create ghost is spliced into the SAME list instance.
            republished.Insert(0, new ZepAssetEntry("A", "Assets/A.asset", "ScriptableObject"));
            var afterGhost = pane.GetSelectedAssetEntries();
            Assert.That(afterGhost, Is.Not.SameAs(afterProjection));
            Assert.That(afterGhost.Count, Is.EqualTo(3));

            // And a provider swap invalidates too.
            pane.VisibleAssetsProvider = () => visible;
            Assert.That(pane.GetSelectedAssetEntries(), Is.Not.SameAs(afterGhost));
        }

        // In search mode the visible order is the LAZY projection, which exists
        // so a 30k-match result never builds 30k entries. Reading the selected
        // entries walked that order end to end, materializing every entry to
        // compare its path — on every memo miss (audit N1).
        [Test]
        public void GetSelectedAssetEntries_OverALazySearchProjection_ReadsOnlyTheSelectedRows()
        {
            const int fileCount = 30000;
            var records = new AssetRecord[fileCount];
            for (var i = 0; i < fileCount; i++)
            {
                records[i] = new AssetRecord(
                    "g-" + i,
                    "Assets/Search/Item" + i + ".asset",
                    "Item" + i,
                    "ScriptableObject");
            }

            var folders = new[]
            {
                new ZepAssetEntry("Search", "Assets/Search", "Folder"),
            };
            var lazy = new LazyAssetEntryList(
                folders,
                records,
                new TagProjectionService(
                    new UnusedGateway(),
                    new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { })));

            var state = new ZepState();
            var pane = state.PrimaryPane;
            pane.VisibleAssetsProvider = () => lazy;
            // Picked out of visible order, near the END of the projection.
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Search/Item29998.asset"),
                AssetPath.From("Assets/Search/Item29001.asset"),
                AssetPath.From("Assets/Search/Item29999.asset"),
            });

            var entries = pane.GetSelectedAssetEntries();

            Assert.That(
                entries.Select(e => e.Path),
                Is.EqualTo(new[]
                {
                    "Assets/Search/Item29001.asset",
                    "Assets/Search/Item29998.asset",
                    "Assets/Search/Item29999.asset",
                }),
                "the same entries the scan returned, in visible order — not selection order");
            Assert.That(
                lazy.MaterializedCount,
                Is.LessThanOrEqualTo(3),
                "only the selected rows may be built; the scan built all 30,000");
            Assert.That(
                lazy.IndexerAccessCount,
                Is.EqualTo(3),
                "and the order is indexed three times, not read 30,001 times");

            // Identity, not just paths: these are the projection's own entries
            // at the resolved indices (one folder row precedes the files).
            Assert.That(entries[0], Is.SameAs(lazy[1 + 29001]));
            Assert.That(entries[2], Is.SameAs(lazy[1 + 29999]));

            // A selected path the projection does not hold is skipped, exactly
            // as a scan that never matched it skipped it.
            pane.SetSelectedAssetPaths(new[]
            {
                AssetPath.From("Assets/Search/Item12.asset"),
                AssetPath.From("Assets/Elsewhere/Gone.asset"),
            });
            Assert.That(
                pane.GetSelectedAssetEntries().Select(e => e.Path),
                Is.EqualTo(new[] { "Assets/Search/Item12.asset" }));
        }

        [Test]
        public void ParentNavigation_ClimbsOneFolderAndStopsAtTheProjectRoots()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            Assert.That(pane.TrySetFolderSelection("Assets/Art/Props"), Is.True);

            Assert.That(pane.TryNavigateToParentFolder(), Is.True);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));

            Assert.That(pane.TryNavigateToParentFolder(), Is.True);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets")));

            Assert.That(
                pane.TryNavigateToParentFolder(),
                Is.False,
                "the Assets root has no parent to browse to");
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets")));

            Assert.That(pane.TrySetFolderSelection("Packages"), Is.True);
            Assert.That(
                pane.TryNavigateToParentFolder(),
                Is.False,
                "the Packages root has no parent to browse to either");
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Packages")));
        }

        // A tag view has no folder hierarchy at all, so the shortcut is a no-op
        // there even though the pane still remembers a current folder.
        [Test]
        public void ParentNavigation_InATagView_IsRefused()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            Assert.That(pane.TrySetFolderSelection("Assets/Art"), Is.True);
            Assert.That(pane.ApplyTagView("red"), Is.True);
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));

            Assert.That(pane.TryNavigateToParentFolder(), Is.False);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
            Assert.That(pane.SelectedTagFilterId, Is.EqualTo("red"));
        }

        // The rooted-auxiliary ceiling is TrySetFolderSelection's, and parent-nav
        // inherits it: a pane rooted at Assets/Art cannot climb to Assets.
        [Test]
        public void ParentNavigation_AtARootedAuxiliarysOwnRoot_IsRefused()
        {
            var pane = new ZepPaneState(
                ZepPaneSource.RootedFolder(AssetPath.From("Assets/Art")));

            Assert.That(pane.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            Assert.That(pane.TryNavigateToParentFolder(), Is.True);
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));

            Assert.That(
                pane.TryNavigateToParentFolder(),
                Is.False,
                "a rooted pane must not climb above the folder it was opened on");
            Assert.That(pane.CurrentFolderPath, Is.EqualTo(AssetPath.From("Assets/Art")));
        }

        [Test]
        public void ToggleTagView_TurnsTheTagOnOffAndSwitchesBetweenTags()
        {
            var state = new ZepState();
            var pane = state.PrimaryPane;

            Assert.That(pane.ToggleTagView("blue"), Is.True);
            Assert.That(pane.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));

            Assert.That(pane.ToggleTagView("green"), Is.True);
            Assert.That(
                pane.SelectedTagFilterId,
                Is.EqualTo("green"),
                "a different tag switches the view rather than clearing it");

            Assert.That(pane.ToggleTagView("green"), Is.True);
            Assert.That(
                pane.SelectedTagFilterId,
                Is.Empty,
                "asserting the live tag a second time turns the tag view off");
            Assert.That(pane.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));
        }

        // A tag pane pinned to its tag reads the tag as absent while it browses
        // an opened folder, so re-asserting that same tag is the "switch to it"
        // leg — which is exactly the contract that returns it to its list.
        [Test]
        public void ToggleTagView_OnATagPaneBrowsingAFolder_ReturnsItToItsList()
        {
            var tag = new ZepPaneState(ZepPaneSource.FixedTag("blue"));
            Assert.That(tag.TrySetFolderSelection("Assets/Art/UI"), Is.True);
            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Browser));

            Assert.That(tag.ToggleTagView("blue"), Is.True);

            Assert.That(tag.ActiveMode, Is.EqualTo(AssetsPaneMode.Tags));
            Assert.That(tag.SelectedTagFilterId, Is.EqualTo("blue"));
            Assert.That(tag.TagBrowseRootPath.IsEmpty, Is.True);
            Assert.That(
                tag.Source,
                Is.EqualTo(ZepPaneSource.FixedTag("blue")),
                "the toggle must not change the pane's identity");

            Assert.That(
                tag.ToggleTagView("red"),
                Is.False,
                "a pinned tag pane refuses any other tag");
            Assert.That(tag.SelectedTagFilterId, Is.EqualTo("blue"));
        }
    }
}
