using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.State;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.UI;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Tests.Editor.UI
{
    public sealed class ContextMenuPopupTests
    {
        [TestCase("%r", true, "⌘R")]
        [TestCase("#&d", true, "⌥⇧D")]
        [TestCase("%#s", true, "⇧⌘S")]
        [TestCase("_F2", true, "F2")]
        [TestCase("%DEL", true, "⌘⌦")]
        [TestCase(" %r ", true, "⌘R")]
        [TestCase("%r", false, "Ctrl+R")]
        [TestCase("%#s", false, "Ctrl+Shift+S")]
        [TestCase("&BACKSPACE", false, "Alt+Backspace")]
        [TestCase("⌘⌫", true, "⌘⌫")]
        [TestCase("", true, "")]
        [TestCase(null, true, "")]
        public void HotkeyText_FormatsUnityHotkeysAndPassesOursThrough(string hotkey, bool macStyle, string expected)
        {
            Assert.That(ContextMenuHotkeyText.Format(hotkey, macStyle), Is.EqualTo(expected));
        }

        private static Rect Identity(Rect rect) => rect;

        [Test]
        public void PlaceRoot_PointerAnchorOpensAtThePoint()
        {
            var rect = ContextMenuPopupPlacement.PlaceRoot(new Rect(100f, 200f, 0f, 0f), new Vector2(240f, 300f), Identity);

            Assert.That(rect, Is.EqualTo(new Rect(100f, 200f, 240f, 300f)));
        }

        [Test]
        public void PlaceRoot_ButtonAnchorDropsBelowItLeftAligned()
        {
            var rect = ContextMenuPopupPlacement.PlaceRoot(new Rect(100f, 200f, 24f, 24f), new Vector2(240f, 300f), Identity);

            Assert.That(rect, Is.EqualTo(new Rect(100f, 224f, 240f, 300f)));
        }

        [Test]
        public void PlaceSubmenu_OpensToTheRightAlignedWithTheRow()
        {
            var parent = new Rect(500f, 300f, 240f, 400f);

            var rect = ContextMenuPopupPlacement.PlaceSubmenu(parent, 350f, new Vector2(200f, 100f), 4f, 6f, Identity);

            Assert.That(rect, Is.EqualTo(new Rect(736f, 344f, 200f, 100f)));
        }

        [Test]
        public void PlaceSubmenu_FlipsLeftWhenTheScreenFitWouldCoverTheParent()
        {
            var parent = new Rect(500f, 300f, 240f, 400f);
            // A "screen" that ends at x = 800 shifts anything wider left.
            Rect Fit(Rect rect) => rect.xMax > 800f ? new Rect(800f - rect.width, rect.y, rect.width, rect.height) : rect;

            var rect = ContextMenuPopupPlacement.PlaceSubmenu(parent, 350f, new Vector2(200f, 100f), 4f, 6f, Fit);

            Assert.That(rect.x, Is.EqualTo(304f), "flipped to the parent's left edge, overlapping by 4px");
            Assert.That(rect.y, Is.EqualTo(344f));
        }

        [Test]
        public void PlaceSubmenu_KeepsTheRightSideWhenTheFitOnlyMovesItVertically()
        {
            var parent = new Rect(500f, 300f, 240f, 400f);
            Rect Fit(Rect rect) => new Rect(rect.x, Mathf.Min(rect.y, 600f), rect.width, rect.height);

            var rect = ContextMenuPopupPlacement.PlaceSubmenu(parent, 900f, new Vector2(200f, 100f), 4f, 6f, Fit);

            Assert.That(rect.x, Is.EqualTo(736f));
            Assert.That(rect.y, Is.EqualTo(600f));
        }

        [UnityTest]
        public IEnumerator PopupWithoutSession_ClosesAfterEnable()
        {
            // Layout restoration enables the window without its managed session.
            // It has no native close button and cannot dispatch Escape or focus loss.
            var popup = ScriptableObject.CreateInstance<ContextMenuPopupWindow>();
            try
            {
                popup.position = new Rect(300f, 200f, 180f, 100f);
                popup.ShowPopup();
                Assert.That(popup != null, Is.True, "cleanup must leave time for normal creation to assign the session");

                for (var frame = 0; frame < 10 && popup != null; frame++)
                {
                    yield return null;
                }

                Assert.That(popup == null, Is.True, "a restored popup with no session must close itself");
            }
            finally
            {
                if (popup != null)
                {
                    popup.Close();
                }
            }
        }

        [UnityTest]
        public IEnumerator PopupWithSession_SurvivesEnableAndClosesOnEscape()
        {
            var existing = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            var shell = new ZepShellView(new ZepState());
            var host = ScriptableObject.CreateInstance<EditorWindow>();
            ContextMenuPopupRenderer.Session session = null;
            try
            {
                host.position = new Rect(40f, 40f, 400f, 300f);
                host.Show();
                host.rootVisualElement.Add(shell);
                yield return null;

                var items = new[]
                {
                    new ZepContextMenuItem(
                        "Probe", "Probe", "Probe", string.Empty, true, false, false,
                        Array.Empty<ZepContextMenuItem>()),
                };
                session = new ContextMenuPopupRenderer.Session(shell, host, null);
                session.OpenRoot(items, new Rect(300f, 200f, 0f, 0f));
                var popup = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>().Except(existing).Single();

                for (var frame = 0; frame < 10; frame++)
                {
                    yield return null;
                }

                Assert.That(popup != null, Is.True, "the session assigned after OnEnable must preserve a live menu");
                var menu = popup.rootVisualElement.Q(className: "zep-menu");
                Assert.That(menu.Query(className: "zep-menu-item").ToList().Count, Is.EqualTo(1));
                using (var escape = KeyDownEvent.GetPooled('\0', KeyCode.Escape, EventModifiers.None))
                {
                    menu.SendEvent(escape);
                }

                Assert.That(popup == null, Is.True, "the live menu must retain its normal Escape dismissal");
            }
            finally
            {
                session?.CloseAll(refocusHost: false);
                host.Close();
                shell.Teardown();
            }
        }

        [UnityTest]
        public IEnumerator KeyboardNavigation_InitialUpDispatchesLastEnabledRow_AndPreservesDownAndWrap()
        {
            var existing = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            var shell = new ZepShellView(new ZepState());
            var host = ScriptableObject.CreateInstance<EditorWindow>();
            ContextMenuPopupRenderer.Session session = null;
            try
            {
                host.position = new Rect(40f, 40f, 400f, 300f);
                host.Show();
                host.rootVisualElement.Add(shell);
                yield return null;

                foreach (var trailingIneligibleRows in new[] { false, true })
                {
                    var activated = new List<string>();
                    ZepContextMenuItem Item(string label, bool enabled = true) =>
                        new ZepContextMenuItem(
                            label, label, label, string.Empty, enabled, false, false,
                            Array.Empty<ZepContextMenuItem>(),
                            onClick: () => activated.Add(label));
                    var items = new List<ZepContextMenuItem>
                    {
                        Item("First"), Item("Middle"), Item("Last")
                    };
                    if (trailingIneligibleRows)
                    {
                        items.Add(Item("Disabled", false));
                        items.Add(ZepContextMenuItem.CreateSeparator());
                    }

                    session = new ContextMenuPopupRenderer.Session(shell, host, null);
                    session.OpenRoot(items, new Rect(300f, 200f, 0f, 0f));
                    var popup = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>().Except(existing).Single();
                    // Keep native pointer hover out of the starting-state assertion.
                    popup.SetActive(-1);
                    using (var down = KeyDownEvent.GetPooled('\0', KeyCode.DownArrow, EventModifiers.None))
                    {
                        session.OnKey(down);
                    }
                    Assert.That(popup.ActiveIndex, Is.Zero, "initial Down starts at the first enabled row");

                    using (var up = KeyDownEvent.GetPooled('\0', KeyCode.UpArrow, EventModifiers.None))
                    {
                        session.OnKey(up);
                    }
                    Assert.That(popup.ActiveIndex, Is.EqualTo(2), "Up wraps to the last enabled row");
                    using (var down = KeyDownEvent.GetPooled('\0', KeyCode.DownArrow, EventModifiers.None))
                    {
                        session.OnKey(down);
                    }
                    Assert.That(popup.ActiveIndex, Is.Zero, "Down wraps past disabled rows and separators");

                    popup.SetActive(-1);
                    using (var up = KeyDownEvent.GetPooled('\0', KeyCode.UpArrow, EventModifiers.None))
                    {
                        session.OnKey(up);
                    }
                    Assert.That(popup.ActiveIndex, Is.EqualTo(2), "initial Up starts at the last enabled row");

                    // Batchmode does not reliably pump delayCall within a test.
                    // Deliver only this activation's deferred callback, preserving
                    // unrelated editor work that was already queued.
                    var pending = EditorApplication.delayCall;
                    EditorApplication.delayCall = null;
                    EditorApplication.CallbackFunction dispatch;
                    try
                    {
                        using (var enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
                        {
                            session.OnKey(enter);
                        }
                        dispatch = EditorApplication.delayCall;
                    }
                    finally
                    {
                        EditorApplication.delayCall = pending;
                    }
                    Assert.That(popup == null, Is.True, "Return closes the menu before dispatch");
                    Assert.That(activated, Is.Empty, "the selected action stays deferred until after dismissal");
                    Assert.That(dispatch, Is.Not.Null);
                    dispatch.Invoke();
                    Assert.That(activated, Is.EqualTo(new[] { "Last" }));
                }
            }
            finally
            {
                session?.CloseAll(refocusHost: false);
                host.Close();
                shell.Teardown();
            }
        }

        // A level taller than the screen (the Assets > Create submenu) used to
        // jump to the screen's top-left corner once laid out: the height
        // correction re-fitted from EditorWindow.position, which Unity reports
        // as (0, 0) right after ShowPopup. It must stay where it was placed,
        // capped to the screen, with the rows past the bottom edge scrolling.
        [UnityTest]
        public IEnumerator TallMenu_StaysWhereItWasPlacedAndScrollsToItsLastRow()
        {
            const int itemCount = 120; // 120 x 24px is taller than any screen
            var items = new ZepContextMenuItem[itemCount];
            for (var i = 0; i < itemCount; i++)
            {
                var label = "Item " + i;
                items[i] = new ZepContextMenuItem(
                    label, label, label, string.Empty, true, false, false, Array.Empty<ZepContextMenuItem>());
            }

            var existing = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>();
            var shell = new ZepShellView(new ZepState());
            var host = ScriptableObject.CreateInstance<EditorWindow>();
            ContextMenuPopupRenderer.Session session = null;
            try
            {
                host.position = new Rect(40f, 40f, 400f, 300f);
                host.Show();
                host.rootVisualElement.Add(shell);
                yield return null;

                session = new ContextMenuPopupRenderer.Session(shell, host, null);
                session.OpenRoot(items, new Rect(300f, 200f, 0f, 0f));
                var popup = Resources.FindObjectsOfTypeAll<ContextMenuPopupWindow>().Except(existing).Single();
                var placed = NativeScreenRect(popup);
                Assert.That(placed.height, Is.LessThan(itemCount * 24f), "the screen fit must cap the level's height");

                var rows = popup.rootVisualElement.Query(className: "zep-menu-item").ToList();
                Assert.That(rows.Count, Is.EqualTo(itemCount));
                var lastRow = rows[itemCount - 1];
                for (var frame = 0; frame < 500 && lastRow.layout.height <= 0f; frame++)
                {
                    yield return null;
                }

                yield return null;
                yield return null;
                Assert.That(lastRow.layout.height, Is.GreaterThan(0f), "the menu must have laid out or this test proves nothing");

                var laidOut = NativeScreenRect(popup);
                Assert.That(laidOut.x, Is.EqualTo(placed.x).Within(0.5f), "laying out must not move the menu sideways");
                Assert.That(laidOut.y, Is.EqualTo(placed.y).Within(0.5f), "laying out must not move the menu vertically");
                Assert.That(laidOut.height, Is.EqualTo(placed.height).Within(0.5f));

                var scroll = popup.rootVisualElement.Q<ScrollView>();
                Assert.That(scroll, Is.Not.Null, "rows past the screen edge need a scroll container");
                Assert.That(scroll.contentContainer.layout.height, Is.GreaterThan(scroll.contentViewport.layout.height),
                    "the rows must lay out taller than the capped window");

                for (var i = 0; i < itemCount; i++)
                {
                    using (var down = KeyDownEvent.GetPooled('\0', KeyCode.DownArrow, EventModifiers.None))
                    {
                        session.OnKey(down);
                    }
                }

                Assert.That(popup.ActiveIndex, Is.EqualTo(itemCount - 1));
                for (var frame = 0; frame < 500 && lastRow.worldBound.yMax > scroll.contentViewport.worldBound.yMax + 0.5f; frame++)
                {
                    yield return null;
                }

                Assert.That(lastRow.worldBound.yMax, Is.LessThanOrEqualTo(scroll.contentViewport.worldBound.yMax + 0.5f),
                    "keyboard selection must scroll the last row into view");
            }
            finally
            {
                session?.CloseAll(refocusHost: false);
                host.Close();
                shell.Teardown();
            }
        }

        // The rect of the native window actually on screen. EditorWindow.position
        // cannot be trusted for a popup (see above), so read the container.
        private static Rect NativeScreenRect(EditorWindow window)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var hostView = typeof(EditorWindow).GetField("m_Parent", flags)?.GetValue(window);
            var container = hostView?.GetType().GetProperty("window", flags)?.GetValue(hostView);
            var position = container?.GetType().GetProperty("position", flags);
            Assert.That(position, Is.Not.Null, "ContainerWindow.position must be reachable to read the on-screen rect");
            return (Rect)position.GetValue(container);
        }

        // §4.11 — a user (definition-only) theme has no sheet on disk, so every
        // popup level builds its own in-memory StyleSheet. Only the owning
        // window can free it, so opening and closing the menu must not grow the
        // count of live runtime sheets.
        [Test]
        public void ClosingAPopup_ReleasesItsRuntimeThemeSheet()
        {
            const string themeId = "ContextMenuPopupRuntimeProbe";
            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new SingleDefinitionThemeProvider(BuildRuntimeDefinition(themeId)));
            registry.RefreshRegistry();

            var shell = new ZepShellView(
                new ZepState(),
                commandHandler: null,
                assetSelectionAction: null,
                contextMenuProvider: null,
                iconRepository: null,
                themeSaveOrchestrator: null,
                registry: registry);
            try
            {
                shell.ThemeSwitcher.SetTheme(themeId, persistPreference: false);
                var baseline = CountRuntimeThemeSheets(themeId);
                Assert.That(baseline, Is.GreaterThan(0),
                    "the shell must hold a runtime sheet for a definition-only theme or this test proves nothing");

                var items = new[]
                {
                    new ZepContextMenuItem(
                        "Assets/Probe",
                        "Assets/Probe",
                        "Probe",
                        string.Empty,
                        true,
                        false,
                        false,
                        Array.Empty<ZepContextMenuItem>())
                };

                for (var i = 0; i < 20; i++)
                {
                    var window = ContextMenuPopupWindow.Create(null, shell, items, 0);
                    // The session closes a level with Close(), which throws on a
                    // window that was never shown (batchmode gives it no host
                    // container). DestroyImmediate drives the same OnDisable →
                    // OnDestroy pair that a real close does.
                    UnityEngine.Object.DestroyImmediate(window);
                }

                Assert.That(CountRuntimeThemeSheets(themeId), Is.EqualTo(baseline),
                    "every closed popup must destroy the runtime theme sheet it built");
            }
            finally
            {
                shell.Teardown();
            }
        }

        private static int CountRuntimeThemeSheets(string themeId)
        {
            var expected = $"ZepTheme_{themeId}_Runtime";
            var count = 0;
            foreach (var sheet in Resources.FindObjectsOfTypeAll<StyleSheet>())
            {
                if (sheet != null && string.Equals(sheet.name, expected, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static ZepThemeDefinition BuildRuntimeDefinition(string themeId)
        {
            var variables = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var name in ZepThemeSchema.RequiredVariables)
            {
                variables[name] = Color.magenta;
            }

            return new ZepThemeDefinition(
                themeId,
                themeId,
                isLight: false,
                ZepThemeSource.UserPrefs,
                variables.ToZepColorDict());
        }

        private sealed class SingleDefinitionThemeProvider : IZepThemeProvider
        {
            private readonly ZepThemeDefinition _definition;

            internal SingleDefinitionThemeProvider(ZepThemeDefinition definition)
            {
                _definition = definition;
            }

            public IEnumerable<ZepThemeDefinition> GetThemes()
            {
                return new[] { _definition };
            }
        }
    }
}
