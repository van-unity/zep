using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.AssetsArea.Substrate;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // Behavior of the substrate's canonical selection: the click resolver
    // (replace / cmd-toggle / shift-range), commit + event emission, and the
    // drag-aware deferred-emission gate. These paths used to be split between
    // the substrate and Unity ListView's native selection; they are now the
    // single selection authority for both view modes.
    public sealed class AssetCollectionViewSelectionTests
    {
        private static AssetCollectionView CreateView(params string[] assetPaths)
        {
            var view = new AssetCollectionView(
                new UnityZepIconRepository(),
                new AssetsAreaInlineEditController(),
                AssetViewMode.List,
                initialGridItemSize: 136,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                presentationLookup: null);

            view.SetAssets(assetPaths
                .Select(p => new ZepAssetEntry(System.IO.Path.GetFileName(p), p, "ScriptableObject"))
                .ToArray());
            return view;
        }

        [Test]
        public void CommitClickSelection_RaisesSelectionChangedWithMatchingEntries()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.CommitClickSelection(new[] { "Assets/B.asset" });

            Assert.That(raised, Is.Not.Null);
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/B.asset" }));
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));
        }

        [Test]
        public void ResolveClickSelection_PlainClickReplacesSelection()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            var result = view.ResolveClickSelection("Assets/B.asset", toggle: false, range: false);

            Assert.That(result, Is.EqualTo(new[] { "Assets/B.asset" }));
        }

        [Test]
        public void ResolveClickSelection_ToggleAddsUnselectedAndRemovesSelected()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset", "Assets/B.asset" });

            var added = view.ResolveClickSelection("Assets/C.asset", toggle: true, range: false);
            Assert.That(added, Is.EquivalentTo(new[] { "Assets/A.asset", "Assets/B.asset", "Assets/C.asset" }));

            var removed = view.ResolveClickSelection("Assets/B.asset", toggle: true, range: false);
            Assert.That(removed, Is.EqualTo(new[] { "Assets/A.asset" }));
        }

        [Test]
        public void ResolveClickSelection_RangeSelectsFromAnchorInAssetOrder()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset", "Assets/D.asset");
            view.CommitClickSelection(new[] { "Assets/B.asset" });

            var result = view.ResolveClickSelection("Assets/D.asset", toggle: false, range: true);

            Assert.That(result, Is.EqualTo(new[] { "Assets/B.asset", "Assets/C.asset", "Assets/D.asset" }));
        }

        [Test]
        public void DeferredEmission_BuffersCommitUntilFlushAndDropsOnDiscard()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            var raisedCount = 0;
            view.SelectionChanged += _ => raisedCount++;

            view.BeginDeferredSelectionEmission();
            view.CommitClickSelection(new[] { "Assets/A.asset" });
            Assert.That(raisedCount, Is.Zero, "The gesture hasn't resolved; the upward emission must be buffered.");

            view.FlushDeferredSelectionEmission();
            Assert.That(raisedCount, Is.EqualTo(1), "A click (pointer-up without drag) flushes the buffered emission.");

            view.BeginDeferredSelectionEmission();
            view.CommitClickSelection(new[] { "Assets/B.asset" });
            view.DiscardDeferredSelectionEmission();
            view.FlushDeferredSelectionEmission();
            Assert.That(raisedCount, Is.EqualTo(1), "A drag discards the buffered emission — the Inspector keeps its target.");

            // The in-panel selection still advanced even though the emission was dropped.
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void MissedMouseUp_PublishesPendingClickAndKeepsKeyboardSelectionLive(
            bool gridMode, bool preserveMultipleSelection)
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            view.SetViewMode(gridMode ? AssetViewMode.Grid : AssetViewMode.List);
            view.CommitClickSelection(preserveMultipleSelection
                ? new[] { "Assets/A.asset", "Assets/C.asset" }
                : new[] { "Assets/C.asset" });
            var surface = new VisualElement();
            surface.Add(view);
            var interaction = new AssetDragPrepareInteraction(view, surface, isFolderPath: _ => false);
            var target = new VisualElement { userData = view.Assets[0] };
            view.Add(target);
            var published = new List<string[]>();
            view.SelectionChanged += entries => published.Add(entries.Select(entry => entry.Path).ToArray());
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;

            using var pointerDown = PointerDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown, button = 0, clickCount = 1,
            });
            pointerDown.target = target;
            typeof(AssetDragPrepareInteraction).GetMethod("OnCollectionPointerDown", instance)
                .Invoke(interaction, new object[] { pointerDown });
            using var mouseDown = MouseDownEvent.GetPooled(new Event
            {
                type = EventType.MouseDown, button = 0, clickCount = 1,
            });
            mouseDown.target = target;
            typeof(AssetDragPrepareInteraction).GetMethod("OnAssetMouseDown", instance)
                .Invoke(interaction, new object[] { mouseDown });
            Assert.That(published, Is.Empty, "the unresolved press must not retarget the Inspector");

            // Model the next move after a release outside the panel. Set the
            // event's button snapshot independently of editor-global input.
            using var move = MouseMoveEvent.GetPooled(new Event { type = EventType.MouseMove });
            typeof(MouseMoveEvent).GetProperty(nameof(MouseMoveEvent.pressedButtons)).SetValue(move, 0);
            Assert.That(interaction.OnSurfaceMouseMove(move), Is.False);

            Assert.That(published, Has.Count.EqualTo(1));
            Assert.That(published[0], Is.EqualTo(new[] { "Assets/A.asset" }));
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);
            Assert.That(published, Has.Count.EqualTo(2), "the recovered gesture must release the emission gate");
            Assert.That(published[1], Is.EqualTo(new[] { "Assets/B.asset" }));

            using var mouseUp = MouseUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0 });
            interaction.OnSurfaceMouseUp(mouseUp);
            Assert.That(published, Has.Count.EqualTo(2), "a late release must not publish the click again");
            interaction.Cancel();
        }

        [Test]
        public void ApplyMarqueePreview_ReplacesSelectionWithoutEmitting()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });
            var raisedCount = 0;
            view.SelectionChanged += _ => raisedCount++;

            // The preview is a delta: the rectangle moved off A and onto B.
            view.ApplyMarqueePreview(
                new[] { "Assets/B.asset" },
                new[] { "Assets/A.asset" });

            // The preview is the substrate's live selection (so a mid-drag
            // cancel must restore), but only the marquee commit may emit.
            Assert.That(raisedCount, Is.Zero);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));
        }

        // The widgets read the substrate's canonical selection SET by
        // reference (audit §5.1), so the one place a widget mutates its mirror
        // — the marquee preview — must copy first. Without the copy the
        // preview rewrites the set the substrate resolves clicks against, and
        // a cmd-click on a previewed path removes it instead of adding it.
        [Test]
        public void WidgetMarqueePreview_LeavesTheSubstrateSelectionAlone()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });
            var widget = view.Q<ListAssetWidget>();
            Assert.That(widget, Is.Not.Null, "the list widget is the active view here");

            widget.ApplyMarqueePreview(new[] { "Assets/B.asset" }, null);

            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
            Assert.That(
                view.ResolveClickSelection("Assets/B.asset", toggle: true, range: false),
                Is.EquivalentTo(new[] { "Assets/A.asset", "Assets/B.asset" }),
                "a leaked preview would make the substrate think B is already selected");
        }

        // A commit pushes the selection down, and the pane echoes it straight
        // back (SelectionChanged -> state -> SetSelection). Neither push may
        // rebuild the widget's set.
        [Test]
        public void SelectionPushAndItsPaneEcho_DoNotRebuildTheWidgetSelectionSet()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            Assert.That(view.Q<ListAssetWidget>(), Is.Not.Null);
            ListAssetWidget.SelectionSetBuildsForTests = 0;

            view.CommitClickSelection(new[] { "Assets/B.asset" });
            view.SetSelection(new[] { AssetPath.From("Assets/B.asset") });

            Assert.That(
                ListAssetWidget.SelectionSetBuildsForTests,
                Is.LessThanOrEqualTo(1),
                "the commit and its echo pushed the same set twice");
        }

        [Test]
        public void CommitMarqueeSelection_RaisesSelectionChangedWithMatchingEntries()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.CommitMarqueeSelection(new[] { "Assets/A.asset", "Assets/C.asset" });

            Assert.That(raised, Is.Not.Null);
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/A.asset", "Assets/C.asset" }));
            Assert.That(view.SelectedPaths, Is.EqualTo(new[]
            {
                AssetPath.From("Assets/A.asset"),
                AssetPath.From("Assets/C.asset"),
            }));
        }

        [Test]
        public void SetViewMode_LazilyCreatesTheOtherWidgetAndKeepsSelection()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            // The grid widget does not exist until the first switch to Grid.
            Assert.That(view.Q("zep-grid-asset-view"), Is.Null,
                "Grid widget must not be constructed while list mode is active.");

            view.SetViewMode(AssetViewMode.Grid);

            Assert.That(view.CurrentViewMode, Is.EqualTo(AssetViewMode.Grid));
            Assert.That(view.Q("zep-grid-asset-view"), Is.Not.Null);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FirstViewModeSwitch_KeepsTheSelectedSubAssetAndContinuesFromItsCell(bool startInGrid)
        {
            WithSelectedSubAsset(startInGrid, (view, subs) =>
            {
                view.SetViewMode(startInGrid ? AssetViewMode.List : AssetViewMode.Grid);

                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(1));
                Assert.That(view.SelectedPaths, Is.Empty);
                SubAssetDescriptor committed = null;
                view.SubAssetSelected += sub => committed = sub;

                Assert.That(view.MoveSelection(startInGrid ? AssetSelectionMove.Down : AssetSelectionMove.Right, false), Is.True);
                Assert.That(committed?.Key, Is.EqualTo(subs[1].Key));
            });
        }

        [TestCase(false, "clear")]
        [TestCase(true, "clear")]
        [TestCase(false, "empty-marquee")]
        [TestCase(true, "empty-marquee")]
        [TestCase(false, "main-selection")]
        [TestCase(true, "main-selection")]
        public void FirstViewModeSwitch_DoesNotRestoreAClearedSubAsset(bool startInGrid, string transition)
        {
            WithSelectedSubAsset(startInGrid, (view, _) =>
            {
                switch (transition)
                {
                    case "clear": view.ClearSelection(); break;
                    case "empty-marquee": view.CommitMarqueeSelection(Array.Empty<string>()); break;
                    case "main-selection":
                        view.SetSelection(new[] { AssetPath.From("Assets/B.asset") });
                        view.SetSelection(Array.Empty<AssetPath>());
                        break;
                }

                view.SetViewMode(startInGrid ? AssetViewMode.List : AssetViewMode.Grid);

                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(-1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FirstViewModeSwitch_DoesNotRestoreASubAssetWhoseParentLeftAndReturned(bool startInGrid)
        {
            WithSelectedSubAsset(startInGrid, (view, _) =>
            {
                view.SetAssets(Array.Empty<ZepAssetEntry>());
                view.SetAssets(SubAssetSelectionEntries());
                view.SetSubAssetExpanded("Assets/A.asset", true);
                view.SetViewMode(startInGrid ? AssetViewMode.List : AssetViewMode.Grid);

                Assert.That(ActiveWidget(view).VisibleItemCount, Is.EqualTo(4), "the child is visible again");
                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(-1));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FirstViewModeSwitch_DoesNotRestoreARemovedChildThatReturned(bool startInGrid)
        {
            WithSelectedSubAsset(startInGrid, (view, subs) =>
            {
                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests = _ => new[] { subs[1] };
                SubAssetCatalog.GetOrRequest("Assets/A.asset");
                SubAssetCatalog.TickForTests();
                view.SetAssets(SubAssetSelectionEntries());
                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(-1));

                SubAssetCatalog.ResetForTests();
                SubAssetCatalog.ResolveOverrideForTests = _ => subs;
                SubAssetCatalog.GetOrRequest("Assets/A.asset");
                SubAssetCatalog.TickForTests();
                view.SetAssets(SubAssetSelectionEntries());
                view.SetViewMode(startInGrid ? AssetViewMode.List : AssetViewMode.Grid);

                Assert.That(ActiveWidget(view).VisibleItemCount, Is.EqualTo(4), "the removed child is visible again");
                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(-1));
            });
        }

        private static IAssetWidget ActiveWidget(AssetCollectionView view)
            => view.CurrentViewMode == AssetViewMode.List
                ? (IAssetWidget)view.Q<ListAssetWidget>()
                : view.Q<GridAssetWidget>();

        private static ZepAssetEntry[] SubAssetSelectionEntries()
            => new[]
            {
                new ZepAssetEntry("A", "Assets/A.asset", "ScriptableObject"),
                new ZepAssetEntry("B", "Assets/B.asset", "ScriptableObject"),
            };

        private static void WithSelectedSubAsset(bool startInGrid, Action<AssetCollectionView, SubAssetDescriptor[]> check)
        {
            SubAssetCatalog.ResetForTests();
            var subs = new[]
            {
                new SubAssetDescriptor("Assets/A.asset", 1, default, "First", "ScriptableObject", null),
                new SubAssetDescriptor("Assets/A.asset", 2, default, "Second", "ScriptableObject", null),
            };
            SubAssetCatalog.ResolveOverrideForTests = _ => subs;
            try
            {
                var view = new AssetCollectionView(new UnityZepIconRepository(), new AssetsAreaInlineEditController(),
                    startInGrid ? AssetViewMode.Grid : AssetViewMode.List, 136,
                    AssetsAreaConstants.MinAssetGridItemSize, AssetsAreaConstants.MaxAssetGridItemSize, null);
                view.SetAssets(SubAssetSelectionEntries());
                SubAssetCatalog.GetOrRequest("Assets/A.asset");
                SubAssetCatalog.TickForTests();
                view.SetSubAssetExpanded("Assets/A.asset", true);
                view.CommitSubAssetSelection(subs[0]);
                Assert.That(ActiveWidget(view).ResolveCursorVisibleIndex(-1), Is.EqualTo(1), "fixture guard");
                Assert.That(startInGrid ? view.Q<ListAssetWidget>() == null : view.Q<GridAssetWidget>() == null,
                    Is.True, "the opposite view stays lazy until its first switch");

                check(view, subs);
            }
            finally
            {
                SubAssetCatalog.ResolveOverrideForTests = null;
                SubAssetCatalog.ResetForTests();
            }
        }

        [Test]
        public void AssetPingPulse_RepeatedPlayRestartsTheExistingRingAndResetHidesIt()
        {
            var parent = new VisualElement();
            var pulse = new AssetPingPulse();
            parent.Add(pulse);
            parent.Add(new VisualElement());

            pulse.Play();

            Assert.That(pulse.style.display.value, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(parent.IndexOf(pulse), Is.EqualTo(parent.childCount - 1));
            Assert.That(pulse.childCount, Is.EqualTo(1), "one ring, no echo");
            var ring = pulse[0];
            Assert.That(ring.style.opacity.value, Is.EqualTo(AssetPingPulse.StartOpacity).Within(0.001f));
            Assert.That(ring.style.scale.value.value.x, Is.EqualTo(1f).Within(0.001f),
                "the ring starts at rest and only grows as it fades");
            Assert.That(parent.style.scale.keyword, Is.EqualTo(StyleKeyword.Null),
                "the entry itself is never scaled by a ping");

            // Simulate the ring mid-fade. A second ping must reuse the one bounded
            // element and snap it back to the armed state rather than appending
            // another overlay.
            ring.style.opacity = 0f;
            pulse.Play();
            Assert.That(pulse.childCount, Is.EqualTo(1));
            Assert.That(ring.style.opacity.value, Is.EqualTo(AssetPingPulse.StartOpacity).Within(0.001f));
            Assert.That(ring.style.scale.value.value.x, Is.EqualTo(1f).Within(0.001f));

            pulse.Reset();
            Assert.That(pulse.style.display.value, Is.EqualTo(DisplayStyle.None));
            Assert.That(parent.style.scale.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        [Test]
        public void SelectAllAssets_SelectsEveryAssetAndRaisesSelectionChanged()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.SelectAllAssets();

            Assert.That(raised, Is.Not.Null);
            Assert.That(raised.Select(e => e.Path),
                Is.EqualTo(new[] { "Assets/A.asset", "Assets/B.asset", "Assets/C.asset" }));
        }

        [Test]
        public void MoveSelection_DownMovesTheLeadOneRow()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            var handled = view.MoveSelection(AssetSelectionMove.Down, extend: false);

            Assert.That(handled, Is.True);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));
        }

        [Test]
        public void MoveSelection_WithoutSelectionEntersAtTheDirectionalEnd()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");

            view.MoveSelection(AssetSelectionMove.Down, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));

            view.ClearSelection();
            view.MoveSelection(AssetSelectionMove.Up, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/C.asset") }));
        }

        [Test]
        public void MoveSelection_ClampsAtTheListEnds()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            var handled = view.MoveSelection(AssetSelectionMove.Up, extend: false);

            Assert.That(handled, Is.True);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
        }

        [Test]
        public void MoveSelection_ShiftExtensionKeepsTheAnchorStable()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            view.CommitClickSelection(new[] { "Assets/B.asset" });

            // Extend down: anchor B, lead C.
            view.MoveSelection(AssetSelectionMove.Down, extend: true);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[]
            {
                AssetPath.From("Assets/B.asset"),
                AssetPath.From("Assets/C.asset"),
            }));

            // Extend up: shrinks back to the anchor…
            view.MoveSelection(AssetSelectionMove.Up, extend: true);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));

            // …and again: grows past it with the anchor still at [0].
            view.MoveSelection(AssetSelectionMove.Up, extend: true);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[]
            {
                AssetPath.From("Assets/B.asset"),
                AssetPath.From("Assets/A.asset"),
            }));
        }

        [Test]
        public void MoveSelection_HomeAndEndJumpToTheListEnds()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            view.CommitClickSelection(new[] { "Assets/B.asset" });

            view.MoveSelection(AssetSelectionMove.Last, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/C.asset") }));

            view.MoveSelection(AssetSelectionMove.First, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
        }

        [Test]
        public void MoveSelection_HorizontalArrowsAreIgnoredInListMode()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            var handled = view.MoveSelection(AssetSelectionMove.Right, extend: false);

            Assert.That(handled, Is.False);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A.asset") }));
        }

        [Test]
        public void MoveSelection_HorizontalArrowsStepInGridMode()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset");
            view.SetViewMode(AssetViewMode.Grid);
            view.CommitClickSelection(new[] { "Assets/A.asset" });

            var handled = view.MoveSelection(AssetSelectionMove.Right, extend: false);

            Assert.That(handled, Is.True);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/B.asset") }));
        }

        [Test]
        public void SelectFirstNameMatch_JumpsToThePrefixCaseInsensitively()
        {
            var view = CreateView("Assets/Alpha.asset", "Assets/beta.asset", "Assets/Gamma.asset");

            var matched = view.SelectFirstNameMatch("BE");

            Assert.That(matched, Is.True);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/beta.asset") }));
        }

        [Test]
        public void SelectFirstNameMatch_WithoutMatchLeavesSelectionAlone()
        {
            var view = CreateView("Assets/Alpha.asset", "Assets/Beta.asset");
            view.CommitClickSelection(new[] { "Assets/Alpha.asset" });

            var matched = view.SelectFirstNameMatch("zzz");

            Assert.That(matched, Is.False);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/Alpha.asset") }));
        }

        // -------------------------------------------------------------------
        // Shift-selection pivot. These repros only bite once the
        // SelectionChanged→SetSelection round-trip re-pushes the selection in
        // ASSET (index) order — the reorder that used to drift the anchor to the
        // top-most item after the first extension. The helper below wires exactly
        // that round-trip so the tests exercise the real failure mode.
        // -------------------------------------------------------------------

        private static readonly string[] SevenAssets =
        {
            "Assets/1.asset", "Assets/2.asset", "Assets/3.asset", "Assets/4.asset",
            "Assets/5.asset", "Assets/6.asset", "Assets/7.asset",
        };

        private static void WireIndexOrderRoundTrip(AssetCollectionView view, string[] order)
        {
            view.SelectionChanged += entries =>
            {
                var selected = new HashSet<string>(entries.Select(e => e.Path));
                view.SetSelection(order.Where(selected.Contains).Select(AssetPath.From).ToList());
            };
        }

        private static IEnumerable<string> SelectedValues(AssetCollectionView view)
            => view.SelectedPaths.Select(p => p.Value);

        [Test]
        public void ShiftClick_ExtendsFromFixedPivot_AcrossSelectionRoundTrip()
        {
            var view = CreateView(SevenAssets);
            WireIndexOrderRoundTrip(view, SevenAssets);

            // Click 5 (plain click sets the pivot at 5).
            view.CommitClickSelection(view.ResolveClickSelection("Assets/5.asset", toggle: false, range: false));

            // Shift-click 3 → {3,4,5}.
            view.CommitClickSelection(view.ResolveClickSelection("Assets/3.asset", toggle: false, range: true));
            Assert.That(SelectedValues(view),
                Is.EquivalentTo(new[] { "Assets/3.asset", "Assets/4.asset", "Assets/5.asset" }));

            // Shift-click 7 → {5,6,7} — the pivot is still 5, NOT the drifted top
            // (before the fix this produced {3,4,5,6,7}).
            view.CommitClickSelection(view.ResolveClickSelection("Assets/7.asset", toggle: false, range: true));
            Assert.That(SelectedValues(view),
                Is.EquivalentTo(new[] { "Assets/5.asset", "Assets/6.asset", "Assets/7.asset" }));
        }

        [Test]
        public void ShiftArrowUpTwice_ExtendsFromFixedPivot_AcrossSelectionRoundTrip()
        {
            var view = CreateView(SevenAssets);
            WireIndexOrderRoundTrip(view, SevenAssets);

            // Click 5 (pivot at 5).
            view.CommitClickSelection(view.ResolveClickSelection("Assets/5.asset", toggle: false, range: false));

            // Shift+Up → {4,5}.
            view.MoveSelection(AssetSelectionMove.Up, extend: true);
            Assert.That(SelectedValues(view),
                Is.EquivalentTo(new[] { "Assets/4.asset", "Assets/5.asset" }));

            // Shift+Up again → {3,4,5} — before the fix the round-trip reorder
            // drifted the anchor and this shrank to {4}.
            view.MoveSelection(AssetSelectionMove.Up, extend: true);
            Assert.That(SelectedValues(view),
                Is.EquivalentTo(new[] { "Assets/3.asset", "Assets/4.asset", "Assets/5.asset" }));
        }

        // -------------------------------------------------------------------
        // Folder expansion (list mode): the substrate flattens expanded
        // folders' children into the selection order, so every resolver —
        // select-all, ranges, keyboard steps, entry lookup — treats expanded
        // children as real rows.
        // -------------------------------------------------------------------

        private static ZepAssetEntry Folder(string path) =>
            new ZepAssetEntry(System.IO.Path.GetFileName(path), path, "Folder");

        private static ZepAssetEntry File(string path) =>
            new ZepAssetEntry(System.IO.Path.GetFileName(path), path, "ScriptableObject");

        private static AssetCollectionView CreateExpandableView(
            Dictionary<string, IReadOnlyList<ZepAssetEntry>> contentsByFolder,
            params ZepAssetEntry[] baseAssets)
        {
            // No version source: these fixtures mutate the contents dictionary
            // directly, which is exactly the case the always-probe fallback
            // exists for.
            return CreateExpandableView(contentsByFolder, null, baseAssets);
        }

        // The version stands in for BrowseProjectionService.FolderContentsVersion:
        // the substrate re-probes its expanded folders only when it moves.
        private static AssetCollectionView CreateExpandableView(
            Dictionary<string, IReadOnlyList<ZepAssetEntry>> contentsByFolder,
            Func<int> folderContentsVersion,
            params ZepAssetEntry[] baseAssets)
        {
            var view = new AssetCollectionView(
                new UnityZepIconRepository(),
                new AssetsAreaInlineEditController(),
                AssetViewMode.List,
                initialGridItemSize: 136,
                AssetsAreaConstants.MinAssetGridItemSize,
                AssetsAreaConstants.MaxAssetGridItemSize,
                presentationLookup: null,
                folderContentsLookup: path =>
                    contentsByFolder.TryGetValue(path, out var contents) ? contents : null,
                folderContentsVersion: folderContentsVersion);

            view.SetFolderExpansion(enabled: true, System.Array.Empty<AssetPath>());
            view.SetAssets(baseAssets);
            return view;
        }

        private static void Expand(AssetCollectionView view, params string[] folders)
            => view.SetFolderExpansion(true, folders.Select(AssetPath.From).ToList());

        [Test]
        public void FolderExpansion_SelectAllTakesExpandedChildrenInFlattenedOrder()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { Folder("Assets/A/Sub"), File("Assets/A/1.asset") },
                ["Assets/A/Sub"] = new[] { File("Assets/A/Sub/2.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            Expand(view, "Assets/A");
            view.SelectAllAssets();

            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/Sub", "Assets/A/1.asset", "Assets/Z.asset",
            }));

            Expand(view, "Assets/A", "Assets/A/Sub");
            view.SelectAllAssets();

            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/Sub", "Assets/A/Sub/2.asset", "Assets/A/1.asset", "Assets/Z.asset",
            }));
        }

        [Test]
        public void FolderExpansion_DisabledOrCollapsed_KeepsTheFlatOrder()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.SelectAllAssets();
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/A", "Assets/Z.asset" }),
                "No folder expanded: the base order must flow through untouched.");

            // Expansion set present but the feature disabled (active filter):
            // the projection must render flat.
            view.SetFolderExpansion(false, new[] { AssetPath.From("Assets/A") });
            view.SelectAllAssets();
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/A", "Assets/Z.asset" }));
        }

        // 6.14: the change detector compared the RAW incoming count against the
        // stored set, so a push carrying a duplicate could match on size while
        // every entry was already expanded — and the folder that dropped out of
        // the push stayed open.
        [Test]
        public void FolderExpansion_APushWithADuplicate_StillCollapsesTheDroppedFolder()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
                ["Assets/B"] = new[] { File("Assets/B/2.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), Folder("Assets/B"));

            Expand(view, "Assets/A", "Assets/B");
            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/1.asset", "Assets/B", "Assets/B/2.asset",
            }));

            view.SetFolderExpansion(
                true,
                new[] { AssetPath.From("Assets/A"), AssetPath.From("Assets/A") });

            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/1.asset", "Assets/B",
            }), "the duplicate must not stand in for the folder that left the set");
        }

        [Test]
        public void FolderExpansion_VisibleAssetsExposeTheFlattenedOrder()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));

            Expand(view, "Assets/A");
            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/1.asset", "Assets/Z.asset",
            }));

            view.SetFolderExpansion(false, new[] { AssetPath.From("Assets/A") });
            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/Z.asset",
            }));
        }

        // The Quick Look / Selection-mirror bug mechanism: a selected expanded
        // child resolves to its entry only when the pane's visible-assets
        // provider serves the FLATTENED order. The base list — what the
        // provider used to serve — cannot resolve it.
        [Test]
        public void FolderExpansion_SelectedChildResolvesThroughTheVisibleAssetsProvider()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var baseAssets = new[] { Folder("Assets/A"), File("Assets/Z.asset") };
            var view = CreateExpandableView(contents, baseAssets);
            Expand(view, "Assets/A");

            var pane = new ZepState().PrimaryPane;
            pane.SetSelectedAssetPath(AssetPath.From("Assets/A/1.asset"));

            pane.VisibleAssetsProvider = () => baseAssets;
            Assert.That(pane.GetSelectedAssetEntries(), Is.Empty,
                "The base folder listing cannot resolve an expanded child's path.");

            pane.VisibleAssetsProvider = () => view.VisibleAssets;
            var entries = pane.GetSelectedAssetEntries();
            Assert.That(entries.Select(e => e.Path), Is.EqualTo(new[] { "Assets/A/1.asset" }));
        }

        [Test]
        public void FolderExpansion_ShiftRangeCrossesTheExpansionBoundary()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset"), File("Assets/A/2.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));
            Expand(view, "Assets/A");

            view.CommitClickSelection(view.ResolveClickSelection("Assets/A", toggle: false, range: false));
            view.CommitClickSelection(view.ResolveClickSelection("Assets/Z.asset", toggle: false, range: true));

            Assert.That(view.SelectedPaths, Is.EqualTo(new[]
            {
                AssetPath.From("Assets/A"),
                AssetPath.From("Assets/A/1.asset"),
                AssetPath.From("Assets/A/2.asset"),
                AssetPath.From("Assets/Z.asset"),
            }));
        }

        [Test]
        public void FolderExpansion_ArrowDownStepsIntoTheFirstExpandedChild()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));
            Expand(view, "Assets/A");
            view.CommitClickSelection(new[] { "Assets/A" });

            view.MoveSelection(AssetSelectionMove.Down, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A/1.asset") }));

            view.MoveSelection(AssetSelectionMove.Down, extend: false);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/Z.asset") }));
        }

        [Test]
        public void FolderExpansion_CommittingAChildRaisesItsRealEntry()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"));
            Expand(view, "Assets/A");
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.CommitClickSelection(new[] { "Assets/A/1.asset" });

            // The child is not in the base list; the entry must resolve from
            // the expanded order — this is what keeps the Inspector mirror and
            // context menus working on expanded rows.
            Assert.That(raised, Is.Not.Null);
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/A/1.asset" }));
            Assert.That(raised[0], Is.SameAs(contents["Assets/A"][0]));
        }

        [Test]
        public void FolderExpansion_TrySelectAssetPath_HonorsVisibility()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"));

            Assert.That(view.TrySelectAssetPath("Assets/A/1.asset"), Is.False,
                "A collapsed folder's child is not on screen and must not be selectable.");

            Expand(view, "Assets/A");

            Assert.That(view.TrySelectAssetPath("Assets/A/1.asset"), Is.True);
            Assert.That(view.SelectedPaths, Is.EqualTo(new[] { AssetPath.From("Assets/A/1.asset") }));
        }

        [Test]
        public void FolderExpansion_ChangedChildContents_ReflattenOnTheNextPush()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
            };
            var baseAssets = new[] { Folder("Assets/A") };
            var view = CreateExpandableView(contents, baseAssets);
            Expand(view, "Assets/A");
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            // An edit inside the expanded subfolder patches that folder's
            // cached children list without replacing the base list instance —
            // the flatten must notice the swapped children reference.
            contents["Assets/A"] = new[] { File("Assets/A/1.asset"), File("Assets/A/2.asset") };
            view.SetAssets(baseAssets);

            view.SelectAllAssets();
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/1.asset", "Assets/A/2.asset",
            }));
        }

        // The flatten memoizes what it read from the browse cache. An
        // incremental external change patches ONE folder's bucket without
        // touching the base list instance, so every case below is a bucket the
        // memo has to notice on its own or the rows stay stale until an
        // unrelated re-flatten.

        [Test]
        public void FolderExpansion_ExpandedFolderThatWasEmpty_ShowsItsFirstChild()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>();
            var baseAssets = new[] { Folder("Assets/A"), File("Assets/Z.asset") };
            var view = CreateExpandableView(contents, baseAssets);

            // Expanded while still empty: nothing to splice in yet.
            Expand(view, "Assets/A");
            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/Z.asset",
            }));

            contents["Assets/A"] = new[] { File("Assets/A/1.asset") };

            Assert.That(view.VisibleAssets.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/A", "Assets/A/1.asset", "Assets/Z.asset",
            }), "an already-expanded folder must render children that appear after it was expanded");
        }

        [Test]
        public void FolderExpansion_CollapsedFolderChevron_TracksItsFirstAndLastChild()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>();
            var baseAssets = new[] { Folder("Assets/A") };
            var view = CreateExpandableView(contents, baseAssets);

            Assert.That(ShowsChevron(view, "Assets/A"), Is.False,
                "an empty folder has nothing to expand into");

            contents["Assets/A"] = new[] { File("Assets/A/1.asset") };
            Assert.That(ShowsChevron(view, "Assets/A"), Is.True,
                "the first child must make the folder expandable");

            contents["Assets/A"] = System.Array.Empty<ZepAssetEntry>();
            Assert.That(ShowsChevron(view, "Assets/A"), Is.False,
                "deleting the last child must not leave a chevron that expands into nothing");
        }

        [Test]
        public void FolderExpansion_UnchangedTree_DoesNotReflatten()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
                ["Assets/B"] = System.Array.Empty<ZepAssetEntry>(),
            };
            var baseAssets = new[] { Folder("Assets/A"), Folder("Assets/B"), File("Assets/Z.asset") };
            var view = CreateExpandableView(contents, baseAssets);
            Expand(view, "Assets/A");

            var first = view.VisibleAssets;

            Assert.That(view.VisibleAssets, Is.SameAs(first),
                "an unchanged tree must reuse the memoized flatten");

            // A bucket that is swapped for an equally-empty one changes no row:
            // the collapsed probe compares emptiness, not references.
            contents["Assets/B"] = new List<ZepAssetEntry>();
            Assert.That(view.VisibleAssets, Is.SameAs(first),
                "re-caching an empty folder must not churn the flatten");
        }

        // Reading the selection order is not an event — the shell, the context
        // menu and every resolver do it several times per interaction. Each
        // read used to re-probe every expanded folder's bucket.
        [Test]
        public void SelectionOrderReads_ProbeTheExpandedFoldersOncePerRealChange()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset") },
                ["Assets/B"] = new[] { File("Assets/B/1.asset") },
            };
            var contentsVersion = 7;
            var view = CreateExpandableView(
                contents,
                () => contentsVersion,
                Folder("Assets/A"),
                Folder("Assets/B"));
            Expand(view, "Assets/A", "Assets/B");

            var flatten = view.VisibleAssets;
            var probesAfterFirstRead = view.ChildSourceProbeCount;

            for (var read = 0; read < 10; read++)
            {
                Assert.That(view.VisibleAssets, Is.SameAs(flatten));
            }

            Assert.That(
                view.ChildSourceProbeCount,
                Is.EqualTo(probesAfterFirstRead),
                "nothing moved, so ten reads must cost no probe at all");

            // A folder delta lands: the projection's bucket version moves, and
            // exactly one probe re-validates the flatten.
            contentsVersion++;
            var probesBeforeDelta = view.ChildSourceProbeCount;
            for (var read = 0; read < 10; read++)
            {
                _ = view.VisibleAssets;
            }

            Assert.That(
                view.ChildSourceProbeCount - probesBeforeDelta,
                Is.EqualTo(1),
                "one bucket change costs one probe, not one per read");

            // And a bucket that really did change still re-flattens.
            contents["Assets/A"] = new[]
            {
                File("Assets/A/1.asset"),
                File("Assets/A/2.asset"),
            };
            contentsVersion++;
            Assert.That(view.VisibleAssets, Is.Not.SameAs(flatten));
            Assert.That(
                view.VisibleAssets.Select(e => e.Path),
                Does.Contain("Assets/A/2.asset"));
        }

        // Every path→row resolution the UI does on a keystroke runs against
        // the flattened order. A 5,000-row folder used to answer each of them
        // by scanning, because ExpandedAssetList had no lookup of its own.
        [Test]
        public void ExpandedList_ResolvesPathsThroughItsOwnIndex_NeverThroughAScan()
        {
            const int childCount = 5000;
            var children = new List<ZepAssetEntry>(childCount);
            for (var i = 0; i < childCount; i++)
            {
                children.Add(File($"Assets/Big/{i:D4}.asset"));
            }

            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/Big"] = children,
            };
            var view = CreateExpandableView(contents, Folder("Assets/Big"), File("Assets/Z.asset"));
            Expand(view, "Assets/Big");

            var order = (ExpandedAssetList)view.VisibleAssets;
            Assert.That(order.Count, Is.EqualTo(childCount + 2));

            ZepPathHelpers.LinearAssetPathScans = 0;
            var callsBefore = order.IndexOfPathCallCount;
            for (var probe = 0; probe < 20; probe++)
            {
                var childIndex = probe * (childCount / 20);
                Assert.That(
                    ZepPathHelpers.FindAssetPathIndex(
                        order,
                        $"Assets/Big/{childIndex:D4}.asset"),
                    Is.EqualTo(childIndex + 1),
                    "the expanded child sits one row below the folder that owns it");
            }

            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(order, "Assets/Gone.asset"),
                Is.EqualTo(-1),
                "a path outside the order must still miss");
            Assert.That(order.IndexOfPathCallCount - callsBefore, Is.EqualTo(21));
            Assert.That(
                ZepPathHelpers.LinearAssetPathScans,
                Is.Zero,
                "the flattened order must never fall through to the linear leg");
        }

        // The arrow keys resolve their target by index and then scrolled to it
        // BY PATH, which walked the same order again. The move already knows
        // where it landed.
        [Test]
        public void ArrowMove_ScrollsByTheIndexItAlreadyHas()
        {
            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/A"] = new[] { File("Assets/A/1.asset"), File("Assets/A/2.asset") },
            };
            var view = CreateExpandableView(contents, Folder("Assets/A"), File("Assets/Z.asset"));
            Expand(view, "Assets/A");
            view.CommitClickSelection(new[] { "Assets/A" });

            var order = (ExpandedAssetList)view.VisibleAssets;
            var callsBefore = order.IndexOfPathCallCount;

            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);

            Assert.That(SelectedValues(view), Is.EqualTo(new[] { "Assets/A/1.asset" }));
            Assert.That(view.VisibleAssets, Is.SameAs(order), "the move must not re-flatten");
            Assert.That(
                order.IndexOfPathCallCount - callsBefore,
                Is.EqualTo(3),
                "the anchor, the lead and the entry the commit resolves — three index "
                + "lookups. Landing on a row still costs no lookup of its own: the "
                + "scroll rides the order index the move already has.");
        }

        // The whole selection pipeline resolves paths against the ORDER'S OWN
        // index (audit 4.1 + 4.2). It used to walk the order instead: one
        // compare per row for the anchor/lead and the range ends, one hash per
        // row to turn the selected paths back into entries — on every click,
        // arrow step and marquee commit.

        private static AssetCollectionView CreateBigExpandedView(int childCount)
        {
            var children = new List<ZepAssetEntry>(childCount);
            for (var i = 0; i < childCount; i++)
            {
                children.Add(File($"Assets/Big/{i:D4}.asset"));
            }

            var contents = new Dictionary<string, IReadOnlyList<ZepAssetEntry>>
            {
                ["Assets/Big"] = children,
            };
            var view = CreateExpandableView(contents, Folder("Assets/Big"), File("Assets/Z.asset"));
            Expand(view, "Assets/Big");
            return view;
        }

        [Test]
        public void SelectionCommit_ResolvesTheSelectedEntryThroughTheOrderIndex()
        {
            var view = CreateBigExpandedView(childCount: 5000);
            var order = (ExpandedAssetList)view.VisibleAssets;
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            ZepPathHelpers.LinearAssetPathScans = 0;
            var callsBefore = order.IndexOfPathCallCount;

            view.CommitClickSelection(new[] { "Assets/Big/4999.asset" });

            Assert.That(
                order.IndexOfPathCallCount - callsBefore,
                Is.EqualTo(1),
                "one selected path costs one lookup, wherever in the 5,000 rows it sits");
            Assert.That(
                ZepPathHelpers.LinearAssetPathScans,
                Is.Zero,
                "the flattened order must never fall through to the linear leg");
            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[] { "Assets/Big/4999.asset" }));
        }

        [Test]
        public void SelectedEntries_FollowVisibleOrder_NotTheOrderThePathsWereCommittedIn()
        {
            var view = CreateBigExpandedView(childCount: 200);
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            view.CommitClickSelection(new[]
            {
                "Assets/Z.asset",
                "Assets/Big/0100.asset",
                "Assets/Big/0002.asset",
            });

            Assert.That(raised.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/Big/0002.asset",
                "Assets/Big/0100.asset",
                "Assets/Z.asset",
            }), "consumers (Inspector, Quick Look, the pane mirror) read this as the visible order");
        }

        [Test]
        public void ShiftRange_ResolvesBothEndsThroughTheOrderIndex()
        {
            var view = CreateBigExpandedView(childCount: 5000);
            view.CommitClickSelection(new[] { "Assets/Big/4000.asset" });
            var order = (ExpandedAssetList)view.VisibleAssets;

            ZepPathHelpers.LinearAssetPathScans = 0;
            var callsBefore = order.IndexOfPathCallCount;

            var range = view.ResolveClickSelection("Assets/Big/4003.asset", toggle: false, range: true);

            Assert.That(
                order.IndexOfPathCallCount - callsBefore,
                Is.EqualTo(2),
                "the pivot and the clicked row — not one compare per row");
            Assert.That(ZepPathHelpers.LinearAssetPathScans, Is.Zero);
            Assert.That(range, Is.EqualTo(new[]
            {
                "Assets/Big/4000.asset",
                "Assets/Big/4001.asset",
                "Assets/Big/4002.asset",
                "Assets/Big/4003.asset",
            }));
        }

        [Test]
        public void ArrowMove_OverAFiveThousandRowOrder_CostsTheSameThreeLookupsAsOverThree()
        {
            var view = CreateBigExpandedView(childCount: 5000);
            view.CommitClickSelection(new[] { "Assets/Big/2000.asset" });
            var order = (ExpandedAssetList)view.VisibleAssets;

            ZepPathHelpers.LinearAssetPathScans = 0;
            var callsBefore = order.IndexOfPathCallCount;

            Assert.That(view.MoveSelection(AssetSelectionMove.Down, extend: false), Is.True);

            Assert.That(SelectedValues(view), Is.EqualTo(new[] { "Assets/Big/2001.asset" }));
            Assert.That(
                order.IndexOfPathCallCount - callsBefore,
                Is.EqualTo(3),
                "the same constant ArrowMove_ScrollsByTheIndexItAlreadyHas measures over a "
                + "three-row order: the cost of a keystroke does not follow the folder size");
            Assert.That(ZepPathHelpers.LinearAssetPathScans, Is.Zero);
        }

        // A plain browse projection is a bare array with no index of its own.
        // The probe scan stays its resolution — pushing k selected paths
        // through the shared resolver would be k full scans, not one.
        [Test]
        public void SelectionCommit_OverAnUnindexedOrder_ScansOnceAndKeepsVisibleOrder()
        {
            var view = CreateView("Assets/A.asset", "Assets/B.asset", "Assets/C.asset");
            Assert.That(view.VisibleAssets, Is.Not.InstanceOf<IAssetPathLookup>());
            IReadOnlyList<ZepAssetEntry> raised = null;
            view.SelectionChanged += entries => raised = entries;

            ZepPathHelpers.LinearAssetPathScans = 0;
            view.CommitClickSelection(new[] { "Assets/C.asset", "Assets/A.asset" });

            Assert.That(
                raised.Select(e => e.Path),
                Is.EqualTo(new[] { "Assets/A.asset", "Assets/C.asset" }));
            Assert.That(
                ZepPathHelpers.LinearAssetPathScans,
                Is.Zero,
                "the fallback probes the order once for the whole selection");
        }

        private static bool ShowsChevron(AssetCollectionView view, string folderPath)
        {
            var visible = view.VisibleAssets;
            var expanded = (ExpandedAssetList)visible;
            for (var i = 0; i < visible.Count; i++)
            {
                if (visible[i].Path == folderPath)
                {
                    return expanded.ShowsChevronAt(i);
                }
            }

            throw new AssertionException($"{folderPath} is not a visible row.");
        }
    }
}
