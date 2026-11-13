using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Zep.Editor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    // The editor default theme paints .unity-button:hover:enabled /
    // :active:enabled and .unity-collection-view__item:hover:enabled at
    // specificity 0-3-0 (measured by dumping DefaultCommonDark_inter.uss on
    // 6000.3.10f1). A class-only pseudo rule (0-2-0) can never own those fills,
    // whatever the sheet order — so until 2026-09-03 the skin's grey hover and
    // blue press showed through on every flat button, action button and folder
    // tree row, identically in all themes (styling-standards §2, §3.1).
    //
    // Grepping the sheet cannot prove a rule matched; these tests mount real
    // controls under the real sheets, flip the pseudo-state, and read the
    // resolved paint.
    public sealed class FlatButtonPaintTests
    {
        private const int Active = 1 << 0;
        private const int Hover = 1 << 1;

        [UnityTest]
        public IEnumerator FlatButton_PaintsRestHoverAndPressFromTokens()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var button = new Button();
                button.AddToClassList(ZepUiClasses.FlatButton);
                button.AddToClassList(ZepUiClasses.SettingsCloseButton);
                root.Add(button);
                yield return null;
                yield return null;

                Assert.That(button.resolvedStyle.backgroundColor.a, Is.LessThanOrEqualTo(0.01f),
                    "a flat button paints nothing at rest");

                SetPseudoState(button, Hover);
                yield return null;
                yield return null;
                AssertColor(button.resolvedStyle.backgroundColor, tokens["--zep-bg-item-hover"],
                    "flat button hover fill must be --zep-bg-item-hover, not the editor skin's grey");

                SetPseudoState(button, Active);
                yield return null;
                yield return null;
                AssertColor(button.resolvedStyle.backgroundColor, tokens["--zep-bg-button-secondary-active"],
                    "flat button press fill must be --zep-bg-button-secondary-active, not the editor skin's blue");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator OpacityOnlyGlyphButtons_OptOutOfTheHoverFill()
        {
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var toggle = new Button();
                toggle.AddToClassList(ZepUiClasses.FlatButton);
                toggle.AddToClassList(ZepUiClasses.SidebarToggle);
                root.Add(toggle);
                var nav = new Button();
                nav.AddToClassList(ZepUiClasses.FlatButton);
                nav.AddToClassList(ZepUiClasses.NavigationButton);
                root.Add(nav);
                yield return null;

                SetPseudoState(toggle, Hover);
                SetPseudoState(nav, Active);
                yield return null;
                yield return null;
                Assert.That(toggle.resolvedStyle.backgroundColor.a, Is.LessThanOrEqualTo(0.01f),
                    "the sidebar toggle gives opacity feedback only — no hover fill");
                Assert.That(nav.resolvedStyle.backgroundColor.a, Is.LessThanOrEqualTo(0.01f),
                    "the nav chevrons give opacity feedback only — no press fill");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ActionButtons_PaintHoverAndPressFromTokens()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var secondary = new Button();
                secondary.AddToClassList(ZepUiClasses.ActionButton);
                secondary.AddToClassList(ZepUiClasses.ActionButtonSecondary);
                root.Add(secondary);
                var primary = new Button();
                primary.AddToClassList(ZepUiClasses.ActionButton);
                primary.AddToClassList(ZepUiClasses.ActionButtonPrimary);
                root.Add(primary);
                yield return null;
                yield return null;
                AssertColor(secondary.resolvedStyle.backgroundColor, tokens["--zep-bg-button-secondary"],
                    "secondary resting fill");

                SetPseudoState(secondary, Hover);
                SetPseudoState(primary, Active);
                yield return null;
                yield return null;
                AssertColor(secondary.resolvedStyle.backgroundColor, tokens["--zep-bg-button-secondary-hover"],
                    "secondary hover fill must come from the theme, not .unity-button:hover:enabled");
                AssertColor(primary.resolvedStyle.backgroundColor, tokens["--zep-bg-button-primary-active"],
                    "primary press fill must come from the theme, not .unity-button:active:enabled");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator ListViewItems_HoverFromTheTheme()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var list = new ListView(new List<string> { "a", "b", "c" }, 20,
                    () => new Label(), (e, i) => ((Label)e).text = i.ToString());
                list.style.height = 100;
                root.Add(list);
                yield return null;
                yield return null;
                yield return null;

                var row = list.GetRootElementForIndex(0);
                Assert.That(row, Is.Not.Null, "the list must realize a row in the rig");
                SetPseudoState(row, Hover);
                yield return null;
                yield return null;
                AssertColor(row.resolvedStyle.backgroundColor, tokens["--zep-bg-item-hover"],
                    "list item hover must be --zep-bg-item-hover, not the editor skin's rgb(48,48,48)");
            }
            finally
            {
                window.Close();
            }
        }

        // UI Toolkit keeps resolving :hover and :active on a DISABLED element, so an
        // unqualified interaction leg animates a control that refuses the click: the
        // greyed-out + button lifted 1px under the pointer and dimmed to the 0.8 press
        // opacity when pressed, which reads as a button that took the press and did
        // nothing. The enabled probes in the same test prove the pseudo-state really
        // was applied, so a disabled assertion cannot pass because nothing happened.
        [UnityTest]
        public IEnumerator DisabledActionButtons_DoNotLiftOnHoverOrDimOnPress()
        {
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var enabled = new Button();
                enabled.AddToClassList(ZepUiClasses.ActionButton);
                enabled.AddToClassList(ZepUiClasses.ActionButtonSecondary);
                root.Add(enabled);
                var disabled = new Button();
                disabled.AddToClassList(ZepUiClasses.ActionButton);
                disabled.AddToClassList(ZepUiClasses.ActionButtonSecondary);
                root.Add(disabled);
                disabled.SetEnabled(false);
                // translate and opacity are the two transitioned properties on this
                // button, and resolvedStyle reports the ANIMATED value: batchmode frames
                // are milliseconds apart, so a 0.2s transition has barely left its start
                // and every reading would look like "no lift". Zero the duration so the
                // probe reads the value the cascade actually resolved.
                FreezeTransitions(enabled);
                FreezeTransitions(disabled);
                yield return null;
                yield return null;

                Assert.That(GetPseudoState(disabled), Is.Not.EqualTo(0),
                    "SetEnabled(false) must show up in pseudoStates — otherwise this probe "
                        + "cannot express disabled+hover at all");
                Assert.That(disabled.resolvedStyle.opacity, Is.LessThan(enabled.resolvedStyle.opacity - 0.05f),
                    "a disabled action button must read as disabled: " + disabled.resolvedStyle.opacity
                        + " vs enabled " + enabled.resolvedStyle.opacity);
                Assert.That(disabled.resolvedStyle.opacity, Is.EqualTo(0.3f).Within(0.01f),
                    "the panel's own disabled opacity must own the button, not the skin's dimming "
                        + "— .zep-navigation-button:disabled sets the same 0.3");
                var restingOpacity = disabled.resolvedStyle.opacity;

                AddPseudoState(enabled, Hover);
                AddPseudoState(disabled, Hover);
                yield return null;
                yield return null;
                Assert.That(enabled.resolvedStyle.translate.y, Is.EqualTo(-1f).Within(0.01f),
                    "an enabled action button still lifts on hover");
                Assert.That(disabled.resolvedStyle.translate.y, Is.EqualTo(0f).Within(0.01f),
                    "a disabled action button must not lift under the pointer");

                SetPseudoState(enabled, Active);
                AddPseudoState(disabled, Active);
                yield return null;
                yield return null;
                Assert.That(enabled.resolvedStyle.opacity, Is.EqualTo(0.8f).Within(0.01f),
                    "an enabled action button still dims on press");
                Assert.That(disabled.resolvedStyle.opacity, Is.EqualTo(restingOpacity).Within(0.01f),
                    "a disabled action button must keep its disabled opacity when pressed, "
                        + "not take the 0.8 press dim");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SelectedViewToggle_KeepsItsFillWhileHovered()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var bar = new VisualElement();
                bar.AddToClassList(ZepUiClasses.FloatingControls);
                root.Add(bar);
                var button = new Button();
                button.AddToClassList(ZepUiClasses.FlatButton);
                button.AddToClassList(ZepUiClasses.AssetViewToggleButton);
                button.AddToClassList(ZepUiClasses.AssetViewToggleButtonActive);
                bar.Add(button);

                foreach (var state in new[] { 0, Hover, Hover | Active })
                {
                    SetPseudoState(button, state);
                    yield return null;
                    yield return null;
                    AssertColor(button.resolvedStyle.backgroundColor, tokens["--zep-bg-toggle-active"],
                        "selected view-mode fill, state " + state);
                }

                button.RemoveFromClassList(ZepUiClasses.AssetViewToggleButtonActive);
                SetPseudoState(button, Hover);
                yield return null;
                yield return null;
                AssertColor(button.resolvedStyle.backgroundColor, tokens["--zep-bg-item-hover"],
                    "an unselected view-mode button still gets the shared hover fill");
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator SelectedGridTile_KeepsItsEditingFillWhileHovered()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var tile = new VisualElement();
                tile.AddToClassList(ZepUiClasses.AssetGridItem);
                tile.AddToClassList(ZepUiClasses.AssetGridItemSelected);
                tile.AddToClassList(ZepUiClasses.AssetGridItemEditing);
                root.Add(tile);
                var card = new VisualElement();
                card.AddToClassList(ZepUiClasses.AssetGridCard);
                tile.Add(card);

                foreach (var state in new[] { 0, Hover })
                {
                    SetPseudoState(card, state);
                    yield return null;
                    yield return null;
                    AssertColor(card.resolvedStyle.backgroundColor, tokens["--zep-bg-item-hover"],
                        "editing fill, state " + state);
                }

                tile.RemoveFromClassList(ZepUiClasses.AssetGridItemEditing);
                yield return null;
                yield return null;
                AssertColor(card.resolvedStyle.backgroundColor, tokens["--zep-bg-item-selected-accent"],
                    "finishing the edit restores the selected fill under the pointer");
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator DisabledNavigationButton_DoesNotReactToHoverOrPress()
        {
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                var enabled = new Button();
                var disabled = new Button();
                foreach (var button in new[] { enabled, disabled })
                {
                    button.AddToClassList(ZepUiClasses.FlatButton);
                    button.AddToClassList(ZepUiClasses.NavigationButton);
                    FreezeTransitions(button);
                    root.Add(button);
                }
                disabled.SetEnabled(false);
                yield return null;
                yield return null;

                foreach (var state in new[] { Hover, Active })
                {
                    SetPseudoState(enabled, state);
                    AddPseudoState(disabled, state);
                    yield return null;
                    yield return null;
                    Assert.That(enabled.resolvedStyle.translate.y,
                        Is.EqualTo(state == Hover ? -1f : 0f).Within(0.01f));
                    Assert.That(enabled.resolvedStyle.opacity,
                        Is.EqualTo(state == Hover ? 1f : 0.55f).Within(0.01f));
                    Assert.That(disabled.resolvedStyle.translate.y, Is.EqualTo(0f).Within(0.01f));
                    Assert.That(disabled.resolvedStyle.opacity, Is.EqualTo(0.3f).Within(0.01f));
                }
            }
            finally { window.Close(); }
        }

        [UnityTest]
        public IEnumerator TypeFilter_UsesSecondaryPaintUnlessAFilterIsActive()
        {
            var tokens = DefaultsTokens();
            var window = ScriptableObject.CreateInstance<EditorWindow>();
            window.ShowUtility();
            try
            {
                var root = MountRoot(window);
                // The production filter is a focusable VisualElement, not a Button.
                var filter = new VisualElement { name = "zep-type-filter-button", focusable = true };
                filter.AddToClassList(ZepUiClasses.ActionButton);
                filter.AddToClassList(ZepUiClasses.ActionButtonSecondary);
                filter.AddToClassList(ZepUiClasses.CreateButton);
                root.Add(filter);
                foreach (var filtered in new[] { false, true })
                {
                    filter.EnableInClassList(ZepUiClasses.TypeFilterButtonActive, filtered);
                    foreach (var state in new[] { 0, Hover, Active, Hover | Active })
                    {
                        SetPseudoState(filter, state);
                        yield return null;
                        yield return null;
                        var background = filtered ? "--zep-bg-button-primary"
                            : (state & Active) != 0 ? "--zep-bg-button-secondary-active"
                            : state == Hover ? "--zep-bg-button-secondary-hover" : "--zep-bg-button-secondary";
                        AssertColor(filter.resolvedStyle.backgroundColor, tokens[background],
                            "type-filter fill, filtered=" + filtered + ", state=" + state);
                        AssertColor(filter.resolvedStyle.color,
                            tokens[filtered ? "--zep-text-button-primary" : "--zep-text-button-secondary"],
                            "type-filter text");
                        AssertColor(filter.resolvedStyle.borderTopColor,
                            tokens[filtered ? "--zep-bg-button-primary" : "--zep-border-pane"],
                            "type-filter border");
                    }
                }
            }
            finally { window.Close(); }
        }

        private static VisualElement MountRoot(EditorWindow window)
        {
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            AttachSheet(root, ZepUiClasses.PanelStyleSheetPath);
            AttachSheet(root, ZepUiClasses.DefaultsStyleSheetPath);
            return root;
        }

        private static void AttachSheet(VisualElement root, string path)
        {
            var sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            Assert.That(sheet, Is.Not.Null, $"stylesheet missing: {path}");
            root.styleSheets.Add(sheet);
        }

        // VisualElement.pseudoStates is internal; flipping it is exactly what the
        // pointer does, minus the pointer.
        private static void SetPseudoState(VisualElement element, int state)
        {
            var prop = PseudoStatesProperty();
            prop.SetValue(element, Enum.ToObject(prop.PropertyType, state));
        }

        // SetPseudoState OVERWRITES, which would erase the Disabled bit SetEnabled(false)
        // just set. Whatever bit value this Unity uses for Disabled, OR-ing preserves it.
        private static void AddPseudoState(VisualElement element, int state) =>
            SetPseudoState(element, GetPseudoState(element) | state);

        private static void FreezeTransitions(VisualElement element) =>
            element.style.transitionDuration =
                new List<TimeValue> { new TimeValue(0f, TimeUnit.Millisecond) };

        private static int GetPseudoState(VisualElement element) =>
            Convert.ToInt32(PseudoStatesProperty().GetValue(element), CultureInfo.InvariantCulture);

        private static PropertyInfo PseudoStatesProperty()
        {
            var prop = typeof(VisualElement).GetProperty("pseudoStates",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(prop, Is.Not.Null, "VisualElement.pseudoStates is gone — update the probe");
            return prop;
        }

        private static void AssertColor(Color actual, Color expected, string what)
        {
            Assert.That(Mathf.Abs(actual.r - expected.r), Is.LessThan(0.01f), what + " (r) " + Describe(actual) + " vs " + Describe(expected));
            Assert.That(Mathf.Abs(actual.g - expected.g), Is.LessThan(0.01f), what + " (g) " + Describe(actual) + " vs " + Describe(expected));
            Assert.That(Mathf.Abs(actual.b - expected.b), Is.LessThan(0.01f), what + " (b) " + Describe(actual) + " vs " + Describe(expected));
            Assert.That(Mathf.Abs(actual.a - expected.a), Is.LessThan(0.01f), what + " (a) " + Describe(actual) + " vs " + Describe(expected));
        }

        private static string Describe(Color c) =>
            $"rgba({c.r * 255f:0},{c.g * 255f:0},{c.b * 255f:0},{c.a:0.##})";

        private static Dictionary<string, Color> DefaultsTokens()
        {
            var text = File.ReadAllText(Path.GetFullPath(ZepUiClasses.DefaultsStyleSheetPath));
            var result = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, @"(?<name>--zep-[a-z0-9-]+)\s*:\s*(?<value>[^;]+);"))
            {
                var raw = m.Groups["value"].Value.Trim();
                var c = Regex.Match(raw,
                    @"^rgba?\(\s*(?<r>\d+)\s*,\s*(?<g>\d+)\s*,\s*(?<b>\d+)\s*(?:,\s*(?<a>[0-9.]+)\s*)?\)$");
                if (!c.Success) continue;
                result[m.Groups["name"].Value] = new Color(
                    int.Parse(c.Groups["r"].Value, CultureInfo.InvariantCulture) / 255f,
                    int.Parse(c.Groups["g"].Value, CultureInfo.InvariantCulture) / 255f,
                    int.Parse(c.Groups["b"].Value, CultureInfo.InvariantCulture) / 255f,
                    c.Groups["a"].Success ? float.Parse(c.Groups["a"].Value, CultureInfo.InvariantCulture) : 1f);
            }
            return result;
        }
    }
}
