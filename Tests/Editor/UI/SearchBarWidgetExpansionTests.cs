using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Editor.UI;
using Zep.Editor.UI.TopBar;
using Zep.Editor.UI.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zep.Tests.Editor.UI
{
    // Search dialog lifecycle, keyboard selection and coalesced typing.
    public sealed class SearchBarWidgetExpansionTests
    {
        [Test]
        public void Overlay_StartsClosed_AndStateRefreshDoesNotOpenIt()
        {
            var widget = new SearchBarWidget();
            widget.SetSearchText("hero");
            Assert.That(widget.IsVisible, Is.False);
            Assert.That(widget.style.display.value, Is.EqualTo(DisplayStyle.None));
            widget.FocusSearch();
            widget.SetSearchText(string.Empty);
            Assert.That(widget.IsVisible, Is.True);
        }

        [Test]
        public void Escape_ClosesImmediately_PreservesQuery_AndFlushesPendingTyping()
        {
            using var rig = new SuggestionRig();
            rig.Field.value = "hero";
            Assert.That(rig.Widget.Q("zep-search-close"), Is.Null);
            InvokeKey(rig.Widget, KeyCode.Escape);
            Assert.That(rig.Widget.IsVisible, Is.False);
            Assert.That(rig.Field.value, Is.EqualTo("hero"));
            Assert.That(rig.Raised, Is.EqualTo(new[] { "hero" }));
            rig.Scheduler.RunAll();
            Assert.That(rig.Raised.Count, Is.EqualTo(1));
        }

        [Test]
        public void KeyboardNavigation_DoesNotActivateResultsWithEnter()
        {
            using var rig = new SuggestionRig();
            var opened = new List<string>();
            rig.Widget.OpenRequested += (path, folder) => opened.Add(path);
            rig.Type("pla");
            Assert.That(rig.Widget.SelectedResult.Path, Is.EqualTo("Assets/Player.prefab"));
            InvokeKey(rig.Widget, KeyCode.DownArrow);
            Assert.That(rig.Widget.SelectedResult.Path, Is.EqualTo("Assets/PlayerController.cs"));
            InvokeKey(rig.Widget, KeyCode.Return);
            InvokeKey(rig.Widget, KeyCode.KeypadEnter);
            Assert.That(opened, Is.Empty);
            Assert.That(rig.Field.value, Is.EqualTo("pla"));
            Assert.That(rig.Widget.IsVisible, Is.True);
            Assert.That(rig.Widget.Q(className: ZepUiClasses.SearchFooter).Query<Button>().ToList(), Is.Empty);
        }

        [UnityTest]
        public IEnumerator Space_UsesQuickLookOnlyFromResults_AndLeavesQueryAndOtherControlsAlone()
        {
            using var rig = new SuggestionRig();
            rig.Type("Player");
            // Native focus and ModalFade's scheduled onShown can outlast many
            // rapid editor-test frames. Start keyboard gestures from a ready field.
            var focusDeadline = EditorApplication.timeSinceStartup + 2d;
            while (!rig.FieldHasFocus && EditorApplication.timeSinceStartup < focusDeadline) yield return null;
            Assert.That(rig.FieldHasFocus, Is.True, "The finder query must own focus before keyboard interaction");
            var requested = new List<ZepAssetEntry>();
            rig.Widget.QuickLookRequested += () => requested.Add(rig.Widget.SelectedResult);
            var results = rig.Widget.Q<ListView>("zep-search-results");
            var keyHandler = typeof(SearchBarWidget).GetMethod("OnSearchFieldKeyDown", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var target in new VisualElement[] { rig.TextInput, rig.Widget.Q<TextField>("zep-search-types-query"),
                         rig.Widget.Q<Button>("zep-search-type-all") })
            {
                using var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space, character = ' ' });
                space.target = target;
                keyHandler.Invoke(rig.Widget, new object[] { space });
                Assert.That(space.isPropagationStopped, Is.False, "Typing and button activation keep their Space key");
            }
            Assert.That(requested, Is.Empty);
            rig.SendKey(KeyCode.DownArrow);
            Assert.That(rig.Widget.focusController.focusedElement, Is.SameAs(results));
            using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space, character = ' ' }))
                results.SendEvent(space);
            Assert.That(requested.Single().Path, Is.EqualTo("Assets/PlayerController.cs"));
            Assert.That(rig.Widget.SearchText, Is.EqualTo("Player"));
            Assert.That(rig.Widget.IsVisible, Is.True);
            foreach (var modifier in new[] { EventModifiers.Shift, EventModifiers.Control, EventModifiers.Command, EventModifiers.Alt })
            {
                using var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space, modifiers = modifier });
                space.target = results;
                keyHandler.Invoke(rig.Widget, new object[] { space });
                Assert.That(space.isPropagationStopped, Is.False);
            }
            rig.FocusField();
            rig.Click(rig.Row(0));
            Assert.That(rig.Widget.focusController.focusedElement, Is.SameAs(results));
            using (var space = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space }))
                results.SendEvent(space);
            Assert.That(requested.Last().Path, Is.EqualTo("Assets/Player.prefab"));
            var dismissals = 0;
            rig.Widget.QuickLookDismissRequested += () => ++dismissals == 1;
            InvokeKey(rig.Widget, KeyCode.Escape);
            Assert.That(rig.Widget.IsVisible, Is.True, "Escape closes Quick Look before Search");
            InvokeKey(rig.Widget, KeyCode.Escape);
            Assert.That(rig.Widget.IsVisible, Is.False);
        }

        [Test]
        public void Space_DoesNotPreviewEmptyOrPendingResults()
        {
            using var rig = new SuggestionRig();
            rig.Type("Player");
            var requests = 0;
            rig.Widget.QuickLookRequested += () => requests++;
            var results = rig.Widget.Q<ListView>("zep-search-results");
            void Space()
            {
                using var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Space });
                evt.target = results;
                typeof(SearchBarWidget).GetMethod("OnSearchFieldKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(rig.Widget, new object[] { evt });
            }
            rig.Widget.SetResults(null);
            Space();
            rig.Field.value = "Changed";
            rig.Widget.SetResults(new[] { new ZepAssetEntry("Stale", "Assets/Stale.png", "Texture2D") });
            Assert.That(rig.Widget.HasPendingQuery, Is.True);
            Space();
            Assert.That(requests, Is.Zero);
        }

        [Test]
        public void PendingQuery_DoesNotOpenAStaleResult()
        {
            using var rig = new SuggestionRig();
            var opened = 0;
            rig.Widget.OpenRequested += (_, _) => opened++;
            rig.Type("pla");
            rig.Field.value = "something else";
            typeof(SearchBarWidget).GetMethod("OpenSuggestion", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(rig.Widget, new object[] { 0 });
            Assert.That(opened, Is.Zero);
            Assert.That(rig.Widget.IsVisible, Is.True);
        }

        [Test]
        public void ExactName_RemainsAnOpenableResult_AndEmptyResultsHaveFeedback()
        {
            using var rig = new SuggestionRig();
            rig.Type("Player");
            Assert.That(rig.Widget.SelectedResult.Name, Is.EqualTo("Player"));
            rig.Widget.SetSuggestions(null);
            Assert.That(rig.ListIsOpen, Is.False);
            Assert.That(rig.Widget.Q<Label>(className: ZepUiClasses.SearchEmpty).text,
                Does.Contain("No matching assets"));
        }

        [UnityTest]
        public IEnumerator ReopenDuringDismissal_StaysVisible_AndRestoresFocusOnClose()
        {
            using var rig = new SuggestionRig();
            for (var frame = 0; frame < 10; frame++) yield return null;
            rig.Widget.Hide();
            var anchor = rig.Widget.parent;
            anchor.Focus();
            rig.Widget.FocusSearch();
            yield return new WaitForSecondsRealtime(0.25f);
            Assert.That(rig.Widget.IsVisible, Is.True);
            Assert.That(rig.Widget.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(rig.FieldHasFocus, Is.True);
            rig.Widget.Hide();
            Assert.That(anchor.focusController.focusedElement, Is.SameAs(anchor));
        }

        [UnityTest]
        public IEnumerator TabNavigation_StaysInsideTheDialog_AndIncludesScopeButtons()
        {
            using var rig = new SuggestionRig();
            var scope = new Button { text = "All assets" };
            rig.Widget.Q("zep-search-card").Add(scope);
            for (var frame = 0; frame < 10; frame++) yield return null;
            rig.FocusField();
            InvokeKey(rig.Widget, KeyCode.Tab);
            Assert.That(rig.Widget.focusController.focusedElement,
                Is.SameAs(rig.Widget.Q<Button>("zep-search-type-all")));
            var reachedScope = false;
            for (var i = 0; i < 30; i++)
            {
                InvokeKey(rig.Widget, KeyCode.Tab);
                var focused = rig.Widget.focusController.focusedElement as VisualElement;
                Assert.That(focused != null && rig.Widget.Contains(focused), Is.True);
                if (focused == scope) { reachedScope = true; break; }
            }
            Assert.That(reachedScope, Is.True);
            InvokeKey(rig.Widget, KeyCode.Tab);
            Assert.That(rig.FieldHasFocus, Is.True);
        }

        [UnityTest]
        public IEnumerator SingleClick_Selects_DoubleClickRevealsAndDismisses()
        {
            using var rig = new SuggestionRig();
            var opened = new List<string>();
            rig.Widget.OpenRequested += (path, folder) => opened.Add(path);
            rig.Type("pla");
            for (var frame = 0; frame < 10; frame++) yield return null;
            Assert.That(rig.Row(1).worldBound.height, Is.GreaterThan(0));
            var picked = rig.Widget.panel.Pick(rig.Row(1).worldBound.center);
            Assert.That(picked == rig.Row(1) || rig.Row(1).Contains(picked), Is.True,
                "The result must be the pointer target; picked " + picked?.name);
            rig.Click(rig.Row(1));
            Assert.That(opened, Is.Empty);
            Assert.That(rig.Widget.IsVisible, Is.True);
            Assert.That(rig.Widget.SelectedResult.Path, Is.EqualTo("Assets/PlayerController.cs"));
            rig.Click(rig.Row(1), 2);
            Assert.That(opened, Is.EqualTo(new[] { "Assets/PlayerController.cs" }));
            Assert.That(rig.Widget.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator SmallWindows_KeepControlsInsideTheCard_AndScrollResults()
        {
            using var rig = new SuggestionRig();
            // ScrollTo uses world bounds; measure after the opening transform settles.
            yield return new WaitForSecondsRealtime(0.2f);
            var card = rig.Widget.Q("zep-search-card");
            var scopes = new TopBarWidget();
            scopes.SetSearchScope(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets"),
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder,
                    "'A very long current folder name that must fit the search dialog'")
            }, true, ZepSearchScope.Assets);
            card.Insert(2, scopes.SearchScopeSurface);
            var assets = new List<ZepAssetEntry>();
            for (var i = 0; i < 60; i++)
                assets.Add(new ZepAssetEntry("Player " + i,
                    "Assets/A/Very/Long/Path/With/Many/Folders/Player" + i + ".prefab", "GameObject"));

            foreach (var size in new[]
            {
                new Vector2(520, 320), new Vector2(320, 320),
                new Vector2(900, 300), new Vector2(640, 600), new Vector2(360, 320)
            })
            {
                rig.Resize(size);
                rig.Type("Player");
                rig.Widget.SetResults(assets);
                for (var frame = 0; frame < 12; frame++) yield return null;
                AssertInside(card, rig.Widget, size.ToString());
                AssertInside(rig.Field, card, "search field");
                Assert.That(rig.Widget.Q("zep-search-close"), Is.Null);
                foreach (var button in scopes.SearchScopeSurface.Query<Button>().ToList())
                    AssertInside(button, card, "scope button");
                var footer = rig.Widget.Q<VisualElement>(className: ZepUiClasses.SearchFooter);
                AssertInside(footer, card, "keyboard hints");
                var viewport = ((ScrollView)rig.Widget.SuggestionSurface).contentViewport;
                Assert.That(viewport.worldBound.height, Is.GreaterThanOrEqualTo(rig.Row(0).worldBound.height),
                    "At least one complete result must remain visible at " + size);
                Assert.That(viewport.worldBound.yMax, Is.LessThanOrEqualTo(footer.worldBound.yMin));

                InvokeKey(rig.Widget, KeyCode.UpArrow);
                for (var frame = 0; frame < 5; frame++) yield return null;
                Assert.That(rig.SelectedRow, Is.SameAs(rig.Row(59)));
                AssertInside(rig.Row(59), viewport, "last result after keyboard navigation");

                rig.Widget.SetSuggestions(null);
                for (var frame = 0; frame < 5; frame++) yield return null;
                AssertInside(rig.Widget.Q<Label>(className: ZepUiClasses.SearchEmpty),
                    card, "empty state");
                AssertInside(footer, card, "empty-state keyboard hints");
            }
        }

        [UnityTest]
        public IEnumerator Resizing_KeepsDrawerFiltersAndSelectionVisible_WithoutActionButtons()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            yield return new WaitForSecondsRealtime(0.2f);
            var card = rig.Widget.Q("zep-search-card");
            var scopes = new TopBarWidget();
            scopes.SetSearchScope(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets"),
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder,
                    "'A very long folder name that wraps onto another line'")
            }, true, ZepSearchScope.All);
            card.Insert(2, scopes.SearchScopeSurface);
            rig.Type("Player");
            var assets = Enumerable.Range(0, 60).Select(i => new ZepAssetEntry(
                "Player " + i, "Assets/Very/Long/Folder/Player" + i + ".prefab", "GameObject")).ToArray();
            rig.Widget.SetResults(assets);
            for (var frame = 0; frame < 8; frame++) yield return null;
            InvokeKey(rig.Widget, KeyCode.DownArrow);
            InvokeKey(rig.Widget, KeyCode.DownArrow);
            ((ScrollView)rig.Widget.SuggestionSurface).scrollOffset = new Vector2(0f, 128f);
            rig.Resize(new Vector2(400, 400));
            yield return WaitForSelectionAfterResize(new Vector2(400, 400), 2);
            AssertInside(rig.Row(2), ((ScrollView)rig.Widget.SuggestionSurface).contentViewport,
                "A partially clipped first row must become fully visible after resize");
            InvokeKey(rig.Widget, KeyCode.UpArrow);
            InvokeKey(rig.Widget, KeyCode.UpArrow);
            InvokeKey(rig.Widget, KeyCode.UpArrow);
            Assert.That(rig.Widget.Q("zep-search-preview"), Is.Null);
            Assert.That(rig.Widget.Q(className: "zep-search-preview"), Is.Null);
            var footer = rig.Widget.Q(className: ZepUiClasses.SearchFooter);
            var viewport = ((ScrollView)rig.Widget.SuggestionSurface).contentViewport;

            foreach (var size in new[] { new Vector2(700, 620), new Vector2(400, 400),
                         new Vector2(500, 800), new Vector2(1000, 700), new Vector2(1000, 400),
                         new Vector2(860, 420), new Vector2(700, 440), new Vector2(480, 520), new Vector2(700, 620) })
            {
                rig.Resize(size);
                yield return WaitForSelectionAfterResize(size, 59);
                AssertInside(footer, card, "footer at " + size);
                Assert.That(footer.Query<Button>().ToList(), Is.Empty);
                Assert.That(viewport.worldBound.height, Is.GreaterThanOrEqualTo(56f));
                Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[59]));
                AssertInside(rig.Row(59), viewport, "selection after resize at " + size);
                rig.Click(rig.Widget.Q<Button>("zep-search-more-types"));
                var picker = rig.Widget.Q(className: ZepUiClasses.SearchTypePicker);
                var drawerDeadline = EditorApplication.timeSinceStartup + 2;
                while ((!picker.ClassListContains(ZepUiClasses.SearchTypePickerOpen)
                    || picker.worldBound.xMax > card.worldBound.xMax + 1f)
                    && EditorApplication.timeSinceStartup < drawerDeadline) yield return null;
                Assert.That(picker.style.display.value, Is.EqualTo(DisplayStyle.Flex));
                AssertInside(picker, card, "type picker at " + size);
                AssertInside(picker.Q(className: ZepUiClasses.SearchTypeFooter), picker, "drawer footer");
                Assert.That(picker.Q<ScrollView>().contentViewport.worldBound.height, Is.GreaterThan(56f));
                Assert.That(picker.worldBound.xMax, Is.EqualTo(card.worldBound.xMax).Within(2f));
                Assert.That(viewport.worldBound.height, Is.GreaterThanOrEqualTo(56f));
                AssertInside(footer, card, "footer with type picker at " + size);
                InvokeKey(rig.Widget, KeyCode.Escape);
                yield return new WaitForSecondsRealtime(0.2f);
            }
            rig.Widget.SetContext("MonoScript", "blue", "Blue", null);
            rig.Type("Player t:Prefab l:Default path:Assets");
            rig.Widget.SetResults(assets);
            rig.Resize(new Vector2(400, 400));
            yield return WaitForSelectionAfterResize(new Vector2(400, 400), 0);
            AssertInside(footer, card, "footer with many filters");
            Assert.That(viewport.worldBound.height, Is.GreaterThanOrEqualTo(56f));
            var filters = rig.Widget.Q<ScrollView>(className: ZepUiClasses.SearchActiveFilters);
            Assert.That(filters.contentViewport.worldBound.height, Is.GreaterThanOrEqualTo(24f),
                "Active restrictions must remain reachable at minimum size");
            rig.Click(rig.Widget.Q<Button>("zep-search-more-types"));
            rig.Resize(new Vector2(1000, 400));
            yield return WaitForSelectionAfterResize(new Vector2(1000, 400), 0);
            yield return new WaitForSecondsRealtime(0.25f);
            var resizedPicker = rig.Widget.Q(className: ZepUiClasses.SearchTypePicker);
            AssertInside(resizedPicker, card, "type picker kept open during resize");
            AssertInside(resizedPicker.Q(className: ZepUiClasses.SearchTypeFooter), resizedPicker, "resized drawer footer");

            IEnumerator WaitForSelectionAfterResize(Vector2 size, int index)
            {
                var resultViewport = ((ScrollView)rig.Widget.SuggestionSurface).contentViewport;
                var deadline = EditorApplication.timeSinceStartup + 2d;
                while (EditorApplication.timeSinceStartup < deadline)
                {
                    var row = rig.Row(index);
                    if (Mathf.Abs(rig.Widget.layout.width - size.x) <= 1f
                        && Mathf.Abs(rig.Widget.layout.height - size.y) <= 1f
                        && row != null && row.worldBound.height > 0f
                        && row.worldBound.yMin >= resultViewport.worldBound.yMin - 1f
                        && row.worldBound.yMax <= resultViewport.worldBound.yMax + 1f) break;
                    rig.Repaint();
                    yield return null;
                }
                Assert.That(rig.Widget.layout.width, Is.EqualTo(size.x).Within(1f), "Native finder resize width");
                Assert.That(rig.Widget.layout.height, Is.EqualTo(size.y).Within(1f), "Native finder resize height");
            }
        }

        [UnityTest]
        public IEnumerator TypeButtons_ComposeWithTypedSyntax_AndClearOnlyFilters()
        {
            using var rig = new SuggestionRig();
            rig.Type("hero path:\"Assets/My Art\" custom:value");
            for (var i = 0; i < 10; i++) yield return null;
            rig.Click(rig.Widget.Q<Button>("zep-search-type-texture"));
            rig.Scheduler.RunAll();
            var parsed = SearchQuery.Parse(rig.Widget.SearchText);
            Assert.That(parsed.TypeTerms.Select(t => t.Value), Is.EqualTo(new[] { "texture" }));
            Assert.That(rig.Widget.SearchText, Does.Contain("path:\"Assets/My Art\""));
            rig.Click(rig.Widget.Q<Button>("zep-search-type-material"));
            rig.Scheduler.RunAll();
            Assert.That(SearchQuery.Parse(rig.Widget.SearchText).TypeTerms.Count, Is.EqualTo(2));
            rig.Widget.SetContext("MonoScript", "blue", "Blue", null);
            rig.Widget.ClearFilters();
            rig.Scheduler.RunAll();
            Assert.That(rig.Widget.InheritedType, Is.Empty);
            Assert.That(rig.Widget.InheritedTag, Is.Empty);
            Assert.That(rig.Widget.SearchText, Does.Contain("hero"));
            Assert.That(rig.Widget.SearchText, Does.Contain("custom:value"));
            Assert.That(SearchQuery.FilterSpans(rig.Widget.SearchText), Is.Empty);
        }

        [Test]
        public void UpdatingResults_PreservesSelectionByPath_AndNavigationIncludesEveryMatch()
        {
            using var rig = new SuggestionRig();
            rig.Type("Player");
            var assets = Enumerable.Range(0, 250).Select(i => new ZepAssetEntry(
                "Player " + i, "Assets/Player" + i + ".prefab", "GameObject")).ToArray();
            rig.Widget.SetResults(assets);
            InvokeKey(rig.Widget, KeyCode.UpArrow);
            Assert.That(rig.Widget.ResultCount, Is.EqualTo(250));
            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[249]));
            rig.Widget.SetResults(assets.Reverse().ToArray());
            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[249]));
            InvokeKey(rig.Widget, KeyCode.DownArrow);
            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[248]));
        }

        [UnityTest]
        public IEnumerator Resizing_DegradesWidthAndHeightSeparately()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            yield return new WaitForSecondsRealtime(0.2f);
            rig.Type("Player");
            rig.Widget.SetResults(new[] { new ZepAssetEntry("Player", "Assets/Player.prefab", "GameObject") });
            for (var frame = 0; frame < 8; frame++) yield return null;
            var context = rig.Widget.Q(className: ZepUiClasses.SearchContext);
            var prefabChip = rig.Widget.Q("zep-search-type-prefab");
            var allChip = rig.Widget.Q("zep-search-type-all");
            var moreTypes = rig.Widget.Q("zep-search-more-types");
            var hints = rig.Widget.Q(className: ZepUiClasses.SearchHints);
            foreach (var (size, narrow, @short) in new[]
                     {
                         (new Vector2(700f, 620f), false, false),
                         (new Vector2(420f, 620f), true, false),
                         (new Vector2(900f, 380f), false, true),
                         (new Vector2(400f, 340f), true, true),
                         (new Vector2(SearchBarWidget.NarrowWidth, SearchBarWidget.ShortHeight), false, false)
                     })
            {
                rig.Resize(size);
                var deadline = EditorApplication.timeSinceStartup + 2d;
                while (EditorApplication.timeSinceStartup < deadline
                       && (Mathf.Abs(rig.Widget.layout.width - size.x) > 1f
                           || Mathf.Abs(rig.Widget.layout.height - size.y) > 1f))
                {
                    rig.Repaint();
                    yield return null;
                }
                yield return null;
                var label = size.x + "x" + size.y;
                Assert.That(rig.Widget.ClassListContains(ZepUiClasses.SearchOverlayNarrow), Is.EqualTo(narrow), label + " narrow");
                Assert.That(rig.Widget.ClassListContains(ZepUiClasses.SearchOverlayShort), Is.EqualTo(@short), label + " short");
                Assert.That(prefabChip.resolvedStyle.display == DisplayStyle.None, Is.EqualTo(narrow),
                    label + ": named type chips collapse by width only");
                Assert.That(allChip.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), label + ": All stays");
                Assert.That(moreTypes.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex), label + ": Types… stays");
                Assert.That(rig.Row(0).Q(className: ZepUiClasses.SearchResultType).resolvedStyle.display == DisplayStyle.None,
                    Is.EqualTo(narrow), label + ": the result type column collapses by width only");
                Assert.That(context.resolvedStyle.display == DisplayStyle.None, Is.EqualTo(@short),
                    label + ": the context line collapses by height only");
                Assert.That(hints.resolvedStyle.display == DisplayStyle.None, Is.EqualTo(@short),
                    label + ": the footer hints collapse by height only");
            }
        }

        [UnityTest]
        public IEnumerator UpdatingResults_KeepsAVisibleSelectionInView_WhenItsIndexChanges()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            yield return new WaitForSecondsRealtime(0.2f);
            rig.Type("Player");
            var assets = Enumerable.Range(0, 60).Select(i => new ZepAssetEntry(
                "Player " + i, "Assets/Player" + i + ".prefab", "GameObject")).ToArray();
            rig.Widget.SetResults(assets);
            for (var frame = 0; frame < 8; frame++) yield return null;
            rig.Widget.MoveResultSelection(-1);
            for (var frame = 0; frame < 5; frame++) yield return null;
            var viewport = ((ScrollView)rig.Widget.SuggestionSurface).contentViewport;
            AssertInside(rig.Row(59), viewport, "selected last result before refresh");

            rig.Widget.SetResults(assets.Reverse().ToArray());
            for (var frame = 0; frame < 5; frame++) yield return null;

            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[59]));
            AssertInside(rig.Row(0), viewport, "selected result moved to the first row");
        }

        [UnityTest]
        public IEnumerator UpdatingResults_KeepsTheFallbackSelectionInView_WhenTheSelectedResultDisappears()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            yield return new WaitForSecondsRealtime(0.2f);
            rig.Type("Player");
            var assets = Enumerable.Range(0, 60).Select(i => new ZepAssetEntry(
                "Player " + i, "Assets/Player" + i + ".prefab", "GameObject")).ToArray();
            rig.Widget.SetResults(assets);
            for (var frame = 0; frame < 8; frame++) yield return null;
            rig.Widget.MoveResultSelection(-1);
            for (var frame = 0; frame < 5; frame++) yield return null;
            var viewport = ((ScrollView)rig.Widget.SuggestionSurface).contentViewport;
            AssertInside(rig.Row(59), viewport, "selected last result before removal");

            // Keep the count and viewport geometry stable: only the catalog identities change.
            rig.Widget.SetResults(assets.Take(59).Append(new ZepAssetEntry(
                "Player replacement", "Assets/PlayerReplacement.prefab", "GameObject")).ToArray());
            for (var frame = 0; frame < 5; frame++) yield return null;

            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[0]));
            AssertInside(rig.Row(0), viewport, "first result selected after removal");
        }

        [UnityTest]
        public IEnumerator UpdatingResults_PreservesUserScrollAwayFromTheSelection()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            yield return new WaitForSecondsRealtime(0.2f);
            rig.Type("Player");
            var assets = Enumerable.Range(0, 60).Select(i => new ZepAssetEntry(
                "Player " + i, "Assets/Player" + i + ".prefab", "GameObject")).ToArray();
            rig.Widget.SetResults(assets);
            for (var frame = 0; frame < 8; frame++) yield return null;
            // Editor test frames can outrun the panel scheduler. Let the initial
            // viewport resize finish revealing selection before simulating user scroll.
            yield return new WaitForSecondsRealtime(0.1f);
            var scroll = (ScrollView)rig.Widget.SuggestionSurface;
            scroll.scrollOffset = new Vector2(0f, 1000f);
            for (var frame = 0; frame < 5; frame++) yield return null;
            var previousOffset = scroll.scrollOffset;
            Assert.That(previousOffset.y, Is.GreaterThan(500f));

            rig.Widget.SetResults(assets.Reverse().ToArray());
            for (var frame = 0; frame < 5; frame++) yield return null;

            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[0]));
            Assert.That(scroll.scrollOffset.y, Is.EqualTo(previousOffset.y).Within(1f),
                "Refreshing results must not pull a deliberately scrolled viewport back to selection");
        }

        [UnityTest]
        public IEnumerator TypePicker_EscapeUnwindsBeforeClosing_AndHoverDoesNotSelect()
        {
            using var rig = new SuggestionRig();
            rig.Type("Player");
            for (var i = 0; i < 10; i++) yield return null;
            rig.Hover(rig.Row(1), PointerEnterEvent.GetPooled());
            Assert.That(rig.Widget.SelectedResult.Path, Is.EqualTo("Assets/Player.prefab"));
            rig.Click(rig.Widget.Q<Button>("zep-search-more-types"));
            Assert.That(rig.Widget.Q(className: ZepUiClasses.SearchTypePicker).style.display.value, Is.EqualTo(DisplayStyle.Flex));
            var typeField = rig.Widget.Q<TextField>("zep-search-types-query");
            rig.Widget.SetScopeContext("Assets", true, false, new[] { new ZepTypeFilterOption("NewCustomType", 1) });
            typeField.value = "NewCustomType";
            Assert.That(rig.Widget.Q(className: ZepUiClasses.SearchTypeChoices).Query<Button>().ToList().Select(b => (string)b.userData),
                Is.EqualTo(new[] { "NewCustomType" }), "newly indexed types must appear without resetting filters");
            typeField.value = "texture";
            using (var enter = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.Return }))
                typeField.SendEvent(enter);
            Assert.That(rig.Widget.IsVisible, Is.True, "Enter in type search must not activate an unrelated asset");
            InvokeKey(rig.Widget, KeyCode.Escape);
            Assert.That(rig.Widget.IsVisible, Is.True);
            InvokeKey(rig.Widget, KeyCode.Escape);
            Assert.That(rig.Widget.IsVisible, Is.False);
        }

        [UnityTest]
        public IEnumerator TypeDrawer_MultiSelectsSearchesAndClears_WithoutLosingContextOrKeyboardFocus()
        {
            using var rig = new SuggestionRig(nativeLayout: true);
            rig.Type("hero path:Assets");
            rig.Widget.SetScopeContext("Assets", true, false,
                new[] { new ZepTypeFilterOption("AddressableAssetGroupSchemaWithAVeryLongName", 1) });
            yield return new WaitForSecondsRealtime(0.2f);
            var trigger = rig.Widget.Q<Button>("zep-search-more-types");
            rig.Click(trigger);
            yield return new WaitForSecondsRealtime(0.25f);
            var drawer = rig.Widget.Q(className: ZepUiClasses.SearchTypePicker);
            var backdrop = rig.Widget.Q(className: ZepUiClasses.SearchTypeBackdrop);
            var query = rig.Widget.Q<TextField>("zep-search-types-query");
            var choices = drawer.Q<ScrollView>();
            Assert.That(drawer.ClassListContains(ZepUiClasses.SearchTypePickerOpen), Is.True);
            Assert.That(rig.Widget.focusController.focusedElement, Is.SameAs(query));
            Assert.That(choices.Query<Label>(className: ZepUiClasses.SearchTypeGroup).ToList().Select(l => l.text),
                Is.EqualTo(new[] { "Common types", "Project types" }));
            query.value = "texture";
            InvokeKey(rig.Widget, KeyCode.DownArrow);
            var row = rig.Widget.focusController.focusedElement as Button;
            Assert.That(row?.userData, Is.EqualTo("Texture"));
            using (var submit = new NavigationSubmitEvent { target = row }) row.SendEvent(submit);
            rig.Scheduler.RunAll();
            Assert.That(SearchQuery.Parse(rig.Widget.SearchText).TypeTerms.Select(t => t.Value), Does.Contain("texture"));
            Assert.That((rig.Widget.focusController.focusedElement as Button)?.userData, Is.EqualTo("Texture"));
            query.Focus();
            query.value = "material";
            for (var frame = 0; frame < 3; frame++) yield return null;
            rig.Click(choices.Query<Button>().ToList().Single());
            rig.Scheduler.RunAll();
            Assert.That(SearchQuery.Parse(rig.Widget.SearchText).TypeTerms.Count, Is.EqualTo(2));
            Assert.That(drawer.Q<Label>(className: ZepUiClasses.SearchTypeSummary).text, Is.EqualTo("2 selected"));
            Assert.That(rig.Widget.SearchText, Does.Contain("hero path:Assets"));
            query.value = "does-not-exist";
            Assert.That(choices.Q<Label>(className: ZepUiClasses.SearchEmpty).text, Does.Contain("No matching types"));
            rig.Click(drawer.Q<Button>("zep-search-types-clear"));
            rig.Scheduler.RunAll();
            Assert.That(SearchQuery.Parse(rig.Widget.SearchText).TypeTerms, Is.Empty);
            Assert.That(rig.Widget.SearchText, Is.EqualTo("hero path:Assets"));
            Assert.That(drawer.Q<Label>(className: ZepUiClasses.SearchTypeSummary).text, Is.EqualTo("All types"));
            for (var i = 0; i < 8; i++)
            {
                InvokeKey(rig.Widget, KeyCode.Tab);
                Assert.That(drawer.Contains(rig.Widget.focusController.focusedElement as VisualElement), Is.True);
            }
            rig.Click(backdrop, screenPoint: new Vector2(backdrop.worldBound.xMin + 8, backdrop.worldBound.center.y));
            for (var frame = 0; frame < 2; frame++) yield return null;
            Assert.That(rig.Widget.IsVisible, Is.True);
            Assert.That(rig.Widget.focusController.focusedElement, Is.SameAs(trigger));
            // Reopen through the focused trigger while the closing scrim still
            // covers its pointer target, just as Space does in the native editor.
            using (var submit = new NavigationSubmitEvent { target = trigger }) trigger.SendEvent(submit);
            yield return new WaitForSecondsRealtime(0.25f);
            Assert.That(backdrop.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
            Assert.That(drawer.ClassListContains(ZepUiClasses.SearchTypePickerOpen), Is.True);
            rig.Widget.SetContext("Texture", "", "", null);
            query.value = "texture";
            for (var frame = 0; frame < 3; frame++) yield return null;
            var inherited = choices.Query<Button>().ToList().Single();
            Assert.That(inherited.Q<Label>(className: ZepUiClasses.SearchTypeCheck).text, Is.EqualTo("✓"));
            rig.Click(inherited);
            rig.Scheduler.RunAll();
            Assert.That(rig.Widget.InheritedType, Is.Empty);
            Assert.That(SearchQuery.Parse(rig.Widget.SearchText).TypeTerms, Is.Empty);
            rig.Click(drawer.Q<Button>("zep-search-types-done"));
            yield return new WaitForSecondsRealtime(0.2f);
            Assert.That(backdrop.resolvedStyle.display, Is.EqualTo(DisplayStyle.None));
            Assert.That(rig.Widget.IsVisible, Is.True);
        }

        [UnityTest]
        public IEnumerator ResultRows_ShowTypePathAndLiteralNames_AndRecycleBeyondFifty()
        {
            using var rig = new SuggestionRig();
            rig.Type("Hero");
            var assets = Enumerable.Range(0, 150).Select(i => new ZepAssetEntry(
                "Hero " + i, "Assets/Art/Hero" + i + ".png", "Texture2D")).ToArray();
            assets[0] = new ZepAssetEntry("<b>Hero</b> & friend", "Assets/Art/Hero.png", "Texture2D");
            rig.Widget.SetResults(assets);
            for (var i = 0; i < 10; i++) yield return null;
            var row = rig.Row(0);
            var label = row.Q<Label>(className: ZepUiClasses.SearchSuggestionPrimary);
            Assert.That(label.enableRichText, Is.False);
            Assert.That(label.text, Is.EqualTo("<b>Hero</b> & friend"));
            Assert.That(row.Q<Label>(className: ZepUiClasses.SearchSuggestionSecondary).text, Is.EqualTo("Assets/Art"));
            Assert.That(row.Q<Label>(className: ZepUiClasses.SearchResultType).text, Is.EqualTo("Texture"));
            Assert.That(row.Q<Image>(), Is.Not.Null);
            Assert.That(rig.Widget.Query(className: ZepUiClasses.SearchSuggestionRow).ToList().Count, Is.LessThan(30));
            InvokeKey(rig.Widget, KeyCode.UpArrow);
            for (var i = 0; i < 5; i++) yield return null;
            Assert.That(rig.Row(149), Is.Not.Null);
            Assert.That(rig.Widget.SelectedResult, Is.SameAs(assets[149]));
        }

        [UnityTest]
        public IEnumerator RecentQueries_RecordOnlyActivatedQueries_PersistAndClearLocally()
        {
            var key = "QuickFinder.Tests." + Guid.NewGuid();
            try
            {
                using (var rig = new SuggestionRig(key))
                {
                    rig.Type("Player");
                    Assert.That(EditorPrefs.HasKey(key), Is.False);
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    rig.Click(rig.Row(0), 2);
                }
                using (var rig = new SuggestionRig(key))
                {
                    for (var i = 0; i < 10; i++) yield return null;
                    var history = rig.Widget.Q(className: ZepUiClasses.SearchHistory);
                    Assert.That(history.Query<Button>().ToList().Select(b => b.text), Does.Contain("Player"));
                    rig.Click(history.Query<Button>().ToList().Single(b => b.text == "Player"));
                    rig.Scheduler.RunAll();
                    Assert.That(rig.Widget.SearchText, Is.EqualTo("Player"));
                    rig.Type(string.Empty);
                    for (var i = 0; i < 3; i++) yield return null;
                    rig.Click(history.Query<Button>().ToList().Single(b => b.text == "Clear history"));
                    Assert.That(EditorPrefs.HasKey(key), Is.False);
                    Assert.That(history.childCount, Is.Zero);
                }
            }
            finally { EditorPrefs.DeleteKey(key); }
        }

        private static void AssertInside(VisualElement child, VisualElement parent, string context)
        {
            Assert.That(child, Is.Not.Null, context);
            Assert.That(child.worldBound.width, Is.GreaterThan(0), context);
            Assert.That(child.worldBound.height, Is.GreaterThan(0), context);
            Assert.That(child.worldBound.xMin, Is.GreaterThanOrEqualTo(parent.worldBound.xMin - 1), context);
            Assert.That(child.worldBound.yMin, Is.GreaterThanOrEqualTo(parent.worldBound.yMin - 1), context);
            Assert.That(child.worldBound.xMax, Is.LessThanOrEqualTo(parent.worldBound.xMax + 1), context);
            Assert.That(child.worldBound.yMax, Is.LessThanOrEqualTo(parent.worldBound.yMax + 1), context);
        }

        // Typing used to run the whole downstream pipeline — parse, search,
        // folder scan, selection reconcile, suggestions — once per character.
        // It is now coalesced onto the editor tick, so ten keystrokes cost one
        // run and the intermediate texts are dropped, never queued behind it.
        [Test]
        public void Typing_CoalescesToOneRaisePerTickCarryingTheLatestText()
        {
            var scheduler = new RigScheduler();
            var widget = new SearchBarWidget(scheduler);
            var window = ScriptableObject.CreateInstance<SearchBarTestWindow>();
            window.position = new Rect(100f, 100f, 400f, 120f);
            window.Show();
            try
            {
                window.rootVisualElement.Add(widget);
                var field = widget.Q<ToolbarSearchField>("zep-search");
                Assert.That(field, Is.Not.Null);
                var raised = new List<string>();
                widget.SearchTextChanged += value => raised.Add(value);

                const string typed = "helloworld";
                for (var length = 1; length <= typed.Length; length++)
                {
                    field.value = typed.Substring(0, length);
                }

                Assert.That(raised, Is.Empty,
                    "the pipeline must not run inside the keystroke");

                scheduler.RunAll();

                Assert.That(raised, Is.EqualTo(new[] { "helloworld" }));

                // A later keystroke queues again — the coalescing is per tick,
                // not a one-shot latch.
                field.value = "helloworlds";
                scheduler.RunAll();

                Assert.That(raised, Is.EqualTo(new[] { "helloworld", "helloworlds" }));
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        // Escape (and the ✕, which produces the same empty ChangeEvent) is a
        // command, not typing: the shell reveals the picked result inside this
        // event. Waiting a tick would show the user an empty search first, and
        // the abandoned keystroke behind it must not arrive afterwards and
        // re-enter the search.
        [Test]
        public void ClearingTheField_RaisesImmediatelyAndDropsTheQueuedKeystroke()
        {
            var scheduler = new RigScheduler();
            var widget = new SearchBarWidget(scheduler);
            var window = ScriptableObject.CreateInstance<SearchBarTestWindow>();
            window.position = new Rect(100f, 100f, 400f, 120f);
            window.Show();
            try
            {
                window.rootVisualElement.Add(widget);
                var field = widget.Q<ToolbarSearchField>("zep-search");
                var raised = new List<string>();
                widget.SearchTextChanged += value => raised.Add(value);

                field.value = "hero";
                field.value = string.Empty;

                Assert.That(raised, Is.EqualTo(new[] { string.Empty }),
                    "the clear raises in the same event and the keystroke behind it is dropped");

                scheduler.RunAll();

                Assert.That(raised, Is.EqualTo(new[] { string.Empty }),
                    "the superseded keystroke must not arrive on the next tick");
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        // The shell pushes the pane's text on every refresh tick. Between a
        // keystroke and the tick that raises it, that text is one keystroke
        // behind — writing it back would silently delete what the user typed,
        // which is the only way the coalescing could lose input.
        [Test]
        public void ARefreshTickWhileAKeystrokeIsQueued_DoesNotOverwriteTheTypedText()
        {
            var scheduler = new RigScheduler();
            var widget = new SearchBarWidget(scheduler);
            var window = ScriptableObject.CreateInstance<SearchBarTestWindow>();
            window.position = new Rect(100f, 100f, 400f, 120f);
            window.Show();
            try
            {
                window.rootVisualElement.Add(widget);
                // The user opened the field and is typing into it.
                widget.FocusSearch();
                var field = widget.Q<ToolbarSearchField>("zep-search");
                var raised = new List<string>();
                widget.SearchTextChanged += value => raised.Add(value);

                field.value = "hero";
                // The pane still holds the pre-keystroke text.
                widget.SetSearchText(string.Empty);

                Assert.That(field.value, Is.EqualTo("hero"));
                Assert.That(widget.IsVisible, Is.True);

                scheduler.RunAll();

                Assert.That(raised, Is.EqualTo(new[] { "hero" }));
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        private sealed class SearchBarTestWindow : EditorWindow
        {
        }

        // The widget and its list mounted the way the shell mounts them: as
        // siblings under a focusable root (the shell root is focusable, which
        // is what a click on the list would otherwise hand focus to).
        private sealed class SuggestionRig : System.IDisposable
        {
            private static readonly AssetSearchSuggestion[] EngineAnswer =
            {
                new AssetSearchSuggestion(
                    new AssetRecord("player", "Assets/Player.prefab", "Player", "GameObject"),
                    isFuzzy: false),
                new AssetSearchSuggestion(
                    new AssetRecord("controller", "Assets/PlayerController.cs", "PlayerController", "MonoScript"),
                    isFuzzy: false)
            };

            private readonly SearchBarTestWindow _window;

            internal SuggestionRig(string historyKey = null, bool nativeLayout = false)
            {
                Widget = new SearchBarWidget(Scheduler, historyKey: historyKey);
                _window = ScriptableObject.CreateInstance<SearchBarTestWindow>();
                _window.Show();
                _window.position = new Rect(100f, 100f, 640f, 500f);
                var root = new VisualElement { focusable = true };
                _window.rootVisualElement.Add(root);
                root.style.flexGrow = 1;
                root.AddToClassList(ZepUiClasses.Root);
                if (nativeLayout) root.AddToClassList(ZepUiClasses.SearchWindow);
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));
                root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss"));
                root.Add(Widget);
                Widget.FocusSearch();
                Widget.SearchTextChanged += text =>
                {
                    Raised.Add(text);
                    Widget.SetSuggestions(string.IsNullOrEmpty(text) ? null : EngineAnswer);
                };
                Field = Widget.Q<ToolbarSearchField>("zep-search");
                TextInput = Field.Q<TextField>();
            }

            internal RigScheduler Scheduler { get; } = new RigScheduler();
            internal SearchBarWidget Widget { get; }
            internal ToolbarSearchField Field { get; }
            internal TextField TextInput { get; }
            internal List<string> Raised { get; } = new List<string>();

            internal void Resize(Vector2 size) =>
                _window.position = new Rect(_window.position.position, size);

            internal void Repaint() => _window.Repaint();

            internal bool ListIsOpen => Widget.SuggestionSurface.ClassListContains(
                ZepUiClasses.SearchSuggestionsVisible);

            internal VisualElement SelectedRow => Widget.SuggestionSurface.Q<VisualElement>(
                className: ZepUiClasses.SearchSuggestionRowSelected);

            internal bool FieldHasFocus =>
                Field.focusController?.focusedElement is VisualElement focused
                && (focused == Field || Field.Contains(focused));

            internal List<string> RowTexts
            {
                get
                {
                    var texts = new List<string>();
                    Widget.SuggestionSurface.Query<VisualElement>(
                            className: ZepUiClasses.SearchSuggestionRow)
                        .ForEach(row =>
                        {
                            if (row.style.display != DisplayStyle.None)
                            {
                                texts.Add(row.Q<Label>(
                                    className: ZepUiClasses.SearchSuggestionPrimary).text);
                            }
                        });
                    return texts;
                }
            }

            internal VisualElement Row(int index) =>
                Widget.SuggestionSurface.Q<VisualElement>("zep-search-suggestion-" + index);

            // A keystroke: the field changes, and the coalesced raise lands on
            // the next tick.
            internal void Type(string text)
            {
                Field.value = text;
                Scheduler.RunAll();
            }

            internal void FocusField() => TextInput.Focus();

            internal void SendKey(KeyCode keyCode)
            {
                using var evt = KeyDownEvent.GetPooled(new Event
                {
                    type = EventType.KeyDown,
                    keyCode = keyCode
                });
                TextInput.SendEvent(evt);
            }

            // Enter/leave go only to their assigned target.
            internal void Hover(VisualElement row, EventBase evt)
            {
                using (evt)
                {
                    evt.target = row;
                    row.SendEvent(evt);
                }
            }

            internal void Click(VisualElement target, int clickCount = 1, Vector2? screenPoint = null)
            {
                var position = screenPoint ?? target.worldBound.center;
                using (var down = PointerDownEvent.GetPooled(new Event
                {
                    type = EventType.MouseDown,
                    button = 0,
                    clickCount = clickCount,
                    mousePosition = position
                }))
                {
                    target.SendEvent(down);
                }

                // Release the global pressed-button state a dispatched
                // PointerDownEvent sets, or it leaks into later tests.
                using var up = PointerUpEvent.GetPooled(new Event
                {
                    type = EventType.MouseUp,
                    button = 0,
                    mousePosition = position
                });
                target.SendEvent(up);
            }

            public void Dispose()
            {
                _window.Close();
                Object.DestroyImmediate(_window);
            }
        }

        private static void InvokeKey(SearchBarWidget widget, KeyCode keyCode)
        {
            var method = typeof(SearchBarWidget).GetMethod(
                "OnSearchFieldKeyDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = keyCode
            });
            method.Invoke(widget, new object[] { evt });
        }
    }
}
