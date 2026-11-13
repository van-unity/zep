using System;
using System.Collections;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // Three defects the empty folder exposed on 2026-07-29, all in one screen:
    //
    //  1. an empty folder rendered as a blank slab — ListAssetWidget swapped the
    //     items source for an empty array AND hid the ListView, so
    //     AlternatingRowBackground.All had nothing to stripe;
    //  2. the bottom bar was an absolutely-positioned strip pinned to bottom:0,
    //     so content rendered UNDER it (the empty-state banner was visibly cut
    //     in half by it) and every scroll view carried a 44px compensation pad;
    //  3. the "This folder is empty." banner duplicated what the bar's file
    //     count already said.
    //
    // Only the resolved layout catches 1 and 2, so these mount the real widgets
    // under the real stylesheets and read worldBound / resolvedStyle.
    public sealed class AssetsAreaEmptyFolderChromeTests
    {
        private const float RowHeight = AssetsAreaConstants.DefaultAssetRowHeight;

        // ZepTheme_Defaults.uss: --zep-bg-asset-list-band.
        private const float BandAlpha = 0.05f;

        [UnityTest]
        public IEnumerator EmptyFolder_KeepsTheListMountedWithItsTailRows()
        {
            var window = CreateWindow();
            try
            {
                var view = MountEmptyBrowser(window);
                yield return null;
                yield return null;

                var list = InnerListView(view);
                Assert.That(list, Is.Not.Null, "list mode must realize the ListView");
                Assert.That(list.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "hiding the ListView on an empty folder is what removed the Finder stripes — "
                    + "an empty folder must render the same striped surface a one-file folder does");
                Assert.That(list.itemsSource, Is.Not.Null);
                Assert.That(list.itemsSource.Count, Is.EqualTo(2),
                    "the source must keep its two tail dummies so the empty folder has the same "
                    + "row shape (and therefore the same band parity) as a populated one");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator EmptyFolder_StripesTheBandsDownTheWholeViewport()
        {
            var window = CreateWindow();
            try
            {
                var view = MountEmptyBrowser(window);
                yield return null;
                yield return null;

                var list = InnerListView(view);
                var bands = BandElements(list);
                Assert.That(bands, Is.Not.Empty,
                    "an empty folder must still paint alternating bands");

                var lowestBand = bands[0];
                var paintedCount = 0;
                foreach (var band in bands)
                {
                    if (band.resolvedStyle.backgroundColor.a > 0.01f)
                    {
                        paintedCount++;
                    }

                    if (band.worldBound.yMax > lowestBand.worldBound.yMax)
                    {
                        lowestBand = band;
                    }
                }

                Assert.That(paintedCount, Is.EqualTo(bands.Count),
                    "every band element must resolve to --zep-bg-asset-list-band, not transparent");
                Assert.That(lowestBand.resolvedStyle.backgroundColor.a,
                    Is.EqualTo(BandAlpha).Within(0.005f),
                    "the band must be the theme's translucent list band");

                // The bands must not stop after the two tail rows: the striping
                // reaches the bottom of the list, which is what "even empty
                // folders should have the stripes" means on screen.
                Assert.That(lowestBand.worldBound.yMax,
                    Is.GreaterThan(list.worldBound.yMax - RowHeight),
                    "the striped tail must run to the bottom of the list, not stop below the "
                    + $"last row (list bottom {list.worldBound.yMax}, "
                    + $"lowest band bottom {lowestBand.worldBound.yMax})");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator BottomBar_TakesItsOwnRowInsteadOfOverlayingTheAssets()
        {
            var window = CreateWindow();
            try
            {
                var view = MountEmptyBrowser(window);
                yield return null;
                yield return null;

                var bar = view.Q<VisualElement>(className: ZepUiClasses.FloatingControls);
                var browser = view.Q("zep-browser-feature");
                Assert.That(bar, Is.Not.Null);
                Assert.That(browser, Is.Not.Null);

                Assert.That(bar.resolvedStyle.position, Is.EqualTo(Position.Relative),
                    "the bar must be in flow — an absolute bar floats over the asset content");
                Assert.That(browser.worldBound.yMax, Is.LessThanOrEqualTo(bar.worldBound.yMin + 0.5f),
                    "the asset area must end where the bar begins; overlapping is what put the "
                    + "empty-folder banner behind the bar "
                    + $"(assets end {browser.worldBound.yMax}, bar starts {bar.worldBound.yMin})");
                Assert.That(bar.worldBound.yMax, Is.LessThanOrEqualTo(view.worldBound.yMax + 0.5f),
                    "the bar must sit inside the pane, not hang past its bottom edge");
                Assert.That(bar.worldBound.height, Is.GreaterThan(0f),
                    "the bar must keep its row when the pane is short");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator EmptyFolder_ShowsNoEmptyStateBanner()
        {
            var window = CreateWindow();
            try
            {
                var view = MountEmptyBrowser(window);
                yield return null;

                var browser = view.Q("zep-browser-feature");
                Assert.That(
                    browser.Q<Label>(className: ZepUiClasses.EmptyState),
                    Is.Null,
                    "the browser's empty-folder banner is redundant with the bar's file count");

                // Tags keep theirs — they have no count-bearing
                // breadcrumb row, and only the browser's copy was redundant.
                Assert.That(
                    view.Q("zep-tags-feature").Q<Label>(
                        className: ZepUiClasses.EmptyState),
                    Is.Not.Null,
                    "only the browser's banner was removed");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------

        private static EmptyFolderChromeTestWindow CreateWindow()
        {
            var window = ScriptableObject.CreateInstance<EmptyFolderChromeTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 480f);
            window.Show();
            return window;
        }

        // A primary pane with no controller: its projections are null, so the
        // feature's pull source is null and the explicitly pushed empty list
        // stays put instead of being replaced by the real project's assets.
        private static ZepPaneView MountEmptyBrowser(EditorWindow window)
        {
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            AttachSheet(root, ZepUiClasses.PanelStyleSheetPath);
            AttachSheet(root, ZepUiClasses.DefaultsStyleSheetPath);

            var state = new ZepState();
            var view = new ZepPaneView(
                state,
                state.PrimaryPane,
                new UnityZepIconRepository());
            root.Add(view);

            view.AssetsArea.SetViewMode(AssetViewMode.List);
            view.BrowserFeature.SetAssets(Array.Empty<ZepAssetEntry>());
            return view;
        }

        private static void AttachSheet(VisualElement root, string path)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {path}");
            root.styleSheets.Add(sheet);
        }

        private static ListView InnerListView(ZepPaneView view)
            => view.Q<ListView>("zep-asset-list");

        // Unity tags both odd rows and the filler rows it creates for the empty
        // viewport tail with this class; Zep.uss colours both from one
        // rule, so this is exactly the set that should read as a stripe.
        private static List<VisualElement> BandElements(ListView list)
        {
            var bands = new List<VisualElement>();
            list.Query<VisualElement>(
                    className: "unity-collection-view__item--alternative-background")
                .ForEach(bands.Add);
            return bands;
        }

        private sealed class EmptyFolderChromeTestWindow : EditorWindow
        {
        }
    }
}
