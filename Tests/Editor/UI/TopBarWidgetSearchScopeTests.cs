using System.Collections.Generic;
using System.Collections;
using System.Linq;
using Zep.Core.Domain;
using Zep.Editor.UI;
using Zep.Editor.UI.TopBar;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // The scope row is pushed from the shell's RefreshView, so it is re-set on
    // every refresh tick — including every keystroke while a search is live.
    // These pin that an unchanged row is left alone and that a selection change
    // moves a class instead of rebuilding the buttons.
    public sealed class TopBarWidgetSearchScopeTests
    {
        private static readonly ZepSearchScopeOption[] Options =
        {
            new ZepSearchScopeOption(ZepSearchScope.All, "All"),
            new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
            new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets")
        };

        [UnityTest]
        public IEnumerator Toolbar_AdaptsToWidthWithoutHidingAnActiveSearchOrFilter()
        {
            var window = ScriptableObject.CreateInstance<ToolbarTestWindow>();
            window.position = new Rect(100, 100, 800, 150);
            window.Show();
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss"));
            var bar = new TopBarWidget();
            bar.SetToolbarOverflowVisible(true);
            root.Add(bar);
            var search = bar.Q<VisualElement>("zep-search-button");
            var searchIcon = search.Q("zep-search-button-icon");
            var searchLabel = search.Q<Label>(className: ZepUiClasses.SearchLauncherLabel);
            var type = bar.Q<VisualElement>("zep-type-filter-button");
            var label = type.Q<Label>();
            var create = bar.Q<Button>("zep-create-button");
            try
            {
                foreach (var width in new[] { 800f, 420f, 320f, 800f })
                {
                    bar.style.width = width;
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    Assert.That(search.resolvedStyle.display,
                        Is.EqualTo(DisplayStyle.Flex));
                    Assert.That(searchLabel, Is.Not.Null);
                    Assert.That(searchLabel.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                    Assert.That(searchLabel.text, Is.EqualTo("Search"));
                    Assert.That(searchLabel.worldBound.xMin - searchIcon.worldBound.xMax,
                        Is.GreaterThanOrEqualTo(4f), "the icon must reserve space before the label");
                    Assert.That(label.resolvedStyle.display,
                        Is.EqualTo(width < 560 ? DisplayStyle.None : DisplayStyle.Flex));
                    Assert.That(create.Q<Label>().resolvedStyle.display, Is.EqualTo(label.resolvedStyle.display));
                    Assert.That(bar.Q("zep-toolbar-overflow-button").worldBound.xMax,
                        Is.LessThanOrEqualTo(bar.worldBound.xMax + 1), "toolbar actions must fit the dock");
                    Assert.That(search.worldBound.xMax, Is.LessThanOrEqualTo(type.worldBound.xMin + 1));
                }

                bar.SetSelectedType("Material");
                Assert.That(label.text, Is.EqualTo("Type: Material"));
                bar.SetSearchText("hero");
                bar.style.width = 320;
                for (var frame = 0; frame < 5; frame++) yield return null;
                Assert.That(search.resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex));
                Assert.That(searchLabel.text, Is.EqualTo("hero"));
                Assert.That(type.ClassListContains(ZepUiClasses.TypeFilterButtonActive), Is.True);
                Assert.That(search.worldBound.width, Is.GreaterThanOrEqualTo(64));
                Assert.That(bar.Q("zep-toolbar-overflow-button").worldBound.xMax,
                    Is.LessThanOrEqualTo(bar.worldBound.xMax + 1));
                bar.SetSelectedType(string.Empty);
                Assert.That(label.text, Is.EqualTo("Type: All"));
                Assert.That(type.ClassListContains(ZepUiClasses.TypeFilterButtonActive), Is.False);

                bar.SetSearchText("a very long search query that must be truncated");
                for (var frame = 0; frame < 5; frame++) yield return null;
                Assert.That(searchLabel.worldBound.xMin - searchIcon.worldBound.xMax,
                    Is.GreaterThanOrEqualTo(4f));
                Assert.That(searchLabel.worldBound.xMax, Is.LessThanOrEqualTo(search.worldBound.xMax));
                Assert.That(searchLabel.isElided, Is.True);
                bar.SetSearchText(string.Empty);
                Assert.That(searchLabel.text, Is.EqualTo("Search"));
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        // The launcher's magnifier is a white mask. Without a tint binding it
        // paints white in every theme, which vanishes on a light toolbar; the
        // type-filter glyph beside it is the token-bound reference.
        [UnityTest]
        public IEnumerator SearchLauncherIcon_TakesTheThemeIconTint_NotTheRawWhiteMask()
        {
            var window = ScriptableObject.CreateInstance<ToolbarTestWindow>();
            window.position = new Rect(100, 100, 800, 150);
            window.Show();
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss"));
            var bar = new TopBarWidget();
            root.Add(bar);
            try
            {
                yield return null;
                yield return null;

                var searchTint = bar.Q("zep-search-button-icon")
                    .resolvedStyle.unityBackgroundImageTintColor;
                var themedTint = bar.Q("zep-type-filter-button-icon")
                    .resolvedStyle.unityBackgroundImageTintColor;
                Assert.That((Color32)themedTint, Is.Not.EqualTo((Color32)Color.white),
                    "fixture: the default table's icon tint must differ from an unbound tint");
                Assert.That((Color32)searchTint, Is.EqualTo((Color32)themedTint),
                    "the launcher glyph must be tinted from --zep-icon-tint like its neighbours");
            }
            finally
            {
                window.Close();
                Object.DestroyImmediate(window);
            }
        }

        private sealed class ToolbarTestWindow : EditorWindow { }

        [Test]
        public void SetSearchScope_CalledTwiceWithEqualInputs_KeepsTheSameButtonInstances()
        {
            var topBar = new TopBarWidget();
            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Assets);
            var first = ScopeButtons(topBar);

            // A fresh list with equal content, as the projection rebuilds each tick.
            topBar.SetSearchScope(
                new List<ZepSearchScopeOption>(Options),
                hasScopedFilter: true,
                ZepSearchScope.Assets);

            AssertSameInstances(first, ScopeButtons(topBar));
        }

        [Test]
        public void SetSearchScope_SelectionOnlyChange_MovesTheClassWithoutRebuilding()
        {
            var topBar = new TopBarWidget();
            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Assets);
            var first = ScopeButtons(topBar);

            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Packages);

            AssertSameInstances(first, ScopeButtons(topBar));
            Assert.That(
                SelectedScopeNames(topBar),
                Is.EqualTo(new[] { "zep-search-scope-button-packages" }),
                "the selected class must move to the newly selected scope and leave no second one behind");
        }

        [Test]
        public void SetSearchScope_OptionsChange_RebuildsTheButtons()
        {
            var topBar = new TopBarWidget();
            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Assets);
            var first = ScopeButtons(topBar);

            var withCurrentFolder = new List<ZepSearchScopeOption>(Options)
            {
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "'Art'")
            };
            topBar.SetSearchScope(withCurrentFolder, hasScopedFilter: true, ZepSearchScope.Assets);

            var after = ScopeButtons(topBar);
            Assert.That(after.Count, Is.EqualTo(4));
            Assert.That(after.Intersect(first).Any(), Is.False, "a real option change must rebuild the row");
        }

        [Test]
        public void SetSearchScope_SameOptionsButDifferentLabel_RebuildsTheButtons()
        {
            var topBar = new TopBarWidget();
            var inArt = new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "'Art'")
            };
            topBar.SetSearchScope(inArt, hasScopedFilter: true, ZepSearchScope.CurrentFolder);

            var inAudio = new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "'Audio'")
            };
            topBar.SetSearchScope(inAudio, hasScopedFilter: true, ZepSearchScope.CurrentFolder);

            Assert.That(
                ScopeButtons(topBar).Cast<Button>().Select(b => b.text),
                Is.EqualTo(new[] { "'Audio'" }));
        }

        [Test]
        public void SetSearchScope_LosingTheScopedFilter_HidesAndClearsTheRow()
        {
            var topBar = new TopBarWidget();
            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Assets);

            topBar.SetSearchScope(Options, hasScopedFilter: false, ZepSearchScope.Assets);

            Assert.That(ScopeButtons(topBar), Is.Empty);
            Assert.That(ScopeRow(topBar).style.display.value, Is.EqualTo(DisplayStyle.None));

            // And it comes back on the next scoped tick rather than staying stale.
            topBar.SetSearchScope(Options, hasScopedFilter: true, ZepSearchScope.Assets);
            Assert.That(ScopeButtons(topBar).Count, Is.EqualTo(3));
            Assert.That(ScopeRow(topBar).style.display.value, Is.EqualTo(DisplayStyle.Flex));
        }

        private static void AssertSameInstances(
            IReadOnlyList<VisualElement> expected,
            IReadOnlyList<VisualElement> actual)
        {
            Assert.That(actual.Count, Is.EqualTo(expected.Count));
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.That(actual[i], Is.SameAs(expected[i]), $"scope button {i} was re-allocated");
            }
        }

        private static VisualElement ScopeRow(TopBarWidget topBar) =>
            topBar.Q<VisualElement>("zep-search-scope-row");

        private static List<VisualElement> ScopeButtons(TopBarWidget topBar) =>
            topBar.Q<VisualElement>("zep-search-scope-buttons").Children().ToList();

        private static IEnumerable<string> SelectedScopeNames(TopBarWidget topBar) =>
            ScopeButtons(topBar)
                .Where(b => b.ClassListContains(ZepUiClasses.SearchScopeButtonSelected))
                .Select(b => b.name);
    }
}
