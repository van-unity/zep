using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Zep.Core.State;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using Zep.Core.Domain;
using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI.AssetsArea;
using Zep.Editor.UI.ContextMenu;

namespace Zep.Editor.UI.Tests
{
    public sealed class ThemeSystemTests
    {
        [Test]
        public void UssVariableParser_WhiteKeyword_IsCaseInsensitiveOpaqueWhite()
        {
            Assert.That(UssVariableParser.TryParseColor("WHITE", out var color), Is.True);
            Assert.That(color.R, Is.EqualTo(1f));
            Assert.That(color.G, Is.EqualTo(1f));
            Assert.That(color.B, Is.EqualTo(1f));
            Assert.That(color.A, Is.EqualTo(1f));
        }

        [Test]
        public void Registry_DeferredProviderFault_PreservesYieldedDefinitionsAndLaterProviders()
        {
            var first = BuildRuntimeDefinition("FirstProviderTheme");
            var duplicate = BuildRuntimeDefinition(first.Id);
            var healthy = BuildRuntimeDefinition("LaterProviderTheme");
            IEnumerable<ZepThemeDefinition> FaultingThemes()
            {
                yield return first;
                throw new InvalidOperationException("deferred discovery failed");
            }

            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new DelegateThemeProvider(FaultingThemes));
            registry.RegisterProvider(new DelegateThemeProvider(() => new[] { duplicate, healthy }));
            LogAssert.Expect(LogType.Warning,
                "[Zep] Theme provider DelegateThemeProvider threw: deferred discovery failed");

            var definitions = registry.AvailableDefinitions;

            Assert.That(definitions, Is.EqualTo(new[] { first, healthy }),
                "A late provider fault must retain yielded entries and their first-provider ID ownership.");
            Assert.That(definitions[0], Is.SameAs(first));
            Assert.That(registry.AvailableDefinitions, Is.SameAs(definitions),
                "A provider fault must still leave a usable discovery snapshot.");
        }

        [Test]
        public void Registry_InvokeTimeProviderFault_ContinuesToLaterProviders()
        {
            var healthy = BuildRuntimeDefinition("AfterInvokeFailure");
            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new DelegateThemeProvider(
                () => throw new InvalidOperationException("invocation failed")));
            registry.RegisterProvider(new DelegateThemeProvider(() => new[] { healthy }));
            LogAssert.Expect(LogType.Warning,
                "[Zep] Theme provider DelegateThemeProvider threw: invocation failed");

            Assert.That(registry.AvailableDefinitions, Is.EqualTo(new[] { healthy }));
        }

        [Test]
        public void Registry_ProviderDisposalFault_PreservesDefinitionsAndContinues()
        {
            var first = BuildRuntimeDefinition("BeforeDisposalFailure");
            var healthy = BuildRuntimeDefinition("AfterDisposalFailure");
            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new DisposalFaultThemeProvider(first));
            registry.RegisterProvider(new DelegateThemeProvider(() => new[] { healthy }));
            LogAssert.Expect(LogType.Warning,
                "[Zep] Theme provider DisposalFaultThemeProvider threw: disposal failed");

            Assert.That(registry.AvailableDefinitions, Is.EqualTo(new[] { first, healthy }));
        }

        [Test]
        public void Registry_CorruptProjectThemeIdentity_ContinuesWithoutRewritingAsset()
        {
            using var scope = new TempAssetScope();
            var asset = scope.CreateScriptableObjectAsset<ZepUserTheme>("CorruptTheme.asset");
            asset.Apply("ValidBeforeCorruption", "Corrupt identity", false,
                new Dictionary<string, ZepColor>());
            var serialized = new SerializedObject(asset);
            serialized.FindProperty("_themeId").stringValue = " \t ";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            var path = AssetDatabase.GetAssetPath(asset);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            var originalBytes = File.ReadAllBytes(path);
            var healthy = BuildRuntimeDefinition("AfterCorruptProjectTheme");
            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new ProjectAssetThemeProvider());
            registry.RegisterProvider(new DelegateThemeProvider(() => new[] { healthy }));
            LogAssert.Expect(LogType.Warning, new Regex(
                @"^\[Zep\] Theme provider ProjectAssetThemeProvider threw: Theme id must be non-empty\."));

            Assert.DoesNotThrow(registry.RefreshRegistry);

            Assert.That(registry.AvailableDefinitions, Does.Contain(healthy));
            Assert.That(registry.AvailableThemes.Any(theme => theme.Id == healthy.Id), Is.True);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(originalBytes),
                "Discovery must preserve the corrupt serialized asset as evidence.");
            Assert.That(AssetDatabase.LoadAssetAtPath<ZepUserTheme>(path).ThemeId,
                Is.EqualTo(" \t "));
        }

        private sealed class DelegateThemeProvider : IZepThemeProvider
        {
            private readonly Func<IEnumerable<ZepThemeDefinition>> _getThemes;

            public DelegateThemeProvider(Func<IEnumerable<ZepThemeDefinition>> getThemes)
            {
                _getThemes = getThemes;
            }

            public IEnumerable<ZepThemeDefinition> GetThemes() => _getThemes();
        }

        private sealed class DisposalFaultThemeProvider : IZepThemeProvider,
            IEnumerable<ZepThemeDefinition>, IEnumerator<ZepThemeDefinition>
        {
            private bool _moved;

            public DisposalFaultThemeProvider(ZepThemeDefinition definition)
            {
                Current = definition;
            }

            public ZepThemeDefinition Current { get; }
            object IEnumerator.Current => Current;
            public IEnumerable<ZepThemeDefinition> GetThemes() => this;
            public IEnumerator<ZepThemeDefinition> GetEnumerator() => this;
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public bool MoveNext() => !_moved && (_moved = true);
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => throw new InvalidOperationException("disposal failed");
        }

        // §9.1 — Atomic-swap invariant

        [TestCase(false)]
        [TestCase(true)]
        public void MissingThemeId_UsesFallbackIdentityForSheetClassAndNotifications(bool persistPreference)
        {
            var hadId = EditorPrefs.HasKey(ZepThemePreferences.ThemeStringPreferenceKey);
            var previousId = EditorPrefs.GetString(ZepThemePreferences.ThemeStringPreferenceKey, string.Empty);
            var hadLegacy = EditorPrefs.HasKey(ZepThemePreferences.ThemeLegacyPreferenceKey);
            var previousLegacy = EditorPrefs.GetBool(ZepThemePreferences.ThemeLegacyPreferenceKey);
            var persistedChanges = 0;
            Action onPersisted = () => persistedChanges++;
            ZepShellView shell = null;
            try
            {
                EditorPrefs.SetString(ZepThemePreferences.ThemeStringPreferenceKey, "ZepLight");
                EditorPrefs.SetBool(ZepThemePreferences.ThemeLegacyPreferenceKey, true);
                shell = new ZepShellView(new ZepState());
                var applied = new List<string>();
                shell.ThemeApplied += applied.Add;
                ZepThemePreferences.PersistedThemeChanged += onPersisted;

                shell.ThemeSwitcher.SetTheme("user-removed-theme", persistPreference);

                Assert.That(shell.ThemeSwitcher.CurrentTheme, Is.EqualTo(ZepThemeRegistry.DefaultThemeId));
                Assert.That(shell.GetClasses().Where(name => name.StartsWith("zep-theme-", StringComparison.Ordinal)),
                    Is.EqualTo(new[] { "zep-theme-ZepDark" }));
                Assert.That(CollectThemeStyleSheets(shell), Is.EqualTo(new[] {
                    AssetDatabase.LoadAssetAtPath<StyleSheet>(shell.Registry.GetTheme("ZepDark").StyleSheetPath) }));
                Assert.That(shell.styleSheets.count, Is.EqualTo(3));
                Assert.That(applied, Is.EqualTo(new[] { "ZepDark" }),
                    "Mirrors must receive the identity of the sheet actually applied.");
                Assert.That(persistedChanges, Is.EqualTo(persistPreference ? 1 : 0));
                Assert.That(ZepThemePreferences.GetTheme(),
                    Is.EqualTo(persistPreference ? "ZepDark" : "ZepLight"));
                Assert.That(EditorPrefs.GetBool(ZepThemePreferences.ThemeLegacyPreferenceKey),
                    Is.EqualTo(!persistPreference));
            }
            finally
            {
                ZepThemePreferences.PersistedThemeChanged -= onPersisted;
                shell?.Teardown();
                if (hadId) EditorPrefs.SetString(ZepThemePreferences.ThemeStringPreferenceKey, previousId);
                else EditorPrefs.DeleteKey(ZepThemePreferences.ThemeStringPreferenceKey);
                if (hadLegacy) EditorPrefs.SetBool(ZepThemePreferences.ThemeLegacyPreferenceKey, previousLegacy);
                else EditorPrefs.DeleteKey(ZepThemePreferences.ThemeLegacyPreferenceKey);
            }
        }

        [UnityTest]
        public IEnumerator MissingThemeId_PaintsZepDarkInsteadOfTheGenericDefaults()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            var shell = new ZepShellView(new ZepState());
            try
            {
                window.Show();
                window.rootVisualElement.Add(shell);
                shell.ThemeSwitcher.SetTheme("user-removed-theme", persistPreference: false);
                yield return null;
                yield return null;
                var expected = ParseThemeTokens(Path.GetFullPath(
                    "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_ZepDark.uss"));
                var fallback = ParseThemeTokens(Path.GetFullPath(ZepUiClasses.DefaultsStyleSheetPath));

                // Compared as whole bytes: ZepDark and the Defaults sheet differ by two
                // steps per channel, which is inside SameRgb's tolerance.
                var painted = Describe(shell.resolvedStyle.backgroundColor);
                Assert.That(painted, Is.EqualTo(Describe(expected["--zep-bg-root"])));
                Assert.That(painted, Is.Not.EqualTo(Describe(fallback["--zep-bg-root"])),
                    "Attaching ZepDark with a removed ID class leaves only the generic Defaults palette active.");
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [Test]
        public void RegisteredRuntimeThemeId_KeepsItsIdentityAndReleasesTheReplacedSheet()
        {
            var definition = BuildRuntimeDefinition("RegisteredRuntimeIdentity");
            var registry = new ZepThemeRegistry();
            registry.RegisterProvider(new DelegateThemeProvider(() => new[] { definition }));
            registry.RefreshRegistry();
            var shell = new ZepShellView(new ZepState(),
                commandHandler: null, assetSelectionAction: null, registry: registry);
            try
            {
                var applied = new List<string>();
                shell.ThemeApplied += applied.Add;

                shell.ThemeSwitcher.SetTheme(definition.Id, persistPreference: false);

                var runtimeSheet = FindAttachedRuntimeSheet(shell);
                Assert.That(runtimeSheet, Is.Not.Null);
                Assert.That(shell.ThemeSwitcher.CurrentTheme, Is.EqualTo(definition.Id));
                Assert.That(shell.ClassListContains(ZepThemeRegistry.GetThemeClassName(definition.Id)), Is.True);
                Assert.That(applied, Is.EqualTo(new[] { definition.Id }));
                Assert.That(shell.styleSheets.count, Is.EqualTo(3));

                shell.ThemeSwitcher.SetTheme("user-removed-theme", persistPreference: false);

                Assert.That(runtimeSheet == null, Is.True, "Fallback must release the replaced native runtime sheet.");
                Assert.That(shell.ThemeSwitcher.CurrentTheme, Is.EqualTo("ZepDark"));
                Assert.That(applied, Is.EqualTo(new[] { definition.Id, "ZepDark" }));
                Assert.That(shell.styleSheets.count, Is.EqualTo(3));
            }
            finally { shell.Teardown(); }
        }

        [Test]
        public void InitialRegistry_PreservesThePreferredIdentityUntilDeferredDiscovery()
        {
            const string preferredId = "user-deferred-theme";
            var registry = ZepThemeRegistry.CreateInitial(preferredId);
            var target = new VisualElement();
            var switcher = new ZepThemeSwitcher(target, registry, null, null);
            try
            {
                switcher.SetTheme(preferredId, persistPreference: false);

                Assert.That(switcher.CurrentTheme, Is.EqualTo(preferredId));
                Assert.That(target.ClassListContains(ZepThemeRegistry.GetThemeClassName(preferredId)), Is.True);
                Assert.That(registry.AvailableThemes.Single().Id, Is.EqualTo(preferredId));
                Assert.That(registry.AvailableDefinitions, Is.Empty);
            }
            finally { switcher.Teardown(); }
        }

        [Test]
        public void SetTheme_LeavesExactlyOneThemeClassOnRoot()
        {
            var shell = new ZepShellView(new ZepState());

            foreach (var theme in FileBackedThemes(shell.Registry))
            {
                InvokeSetTheme(shell, theme.Id);

                var themeClasses = shell.GetClasses()
                    .Where(c => c.StartsWith("zep-theme-", System.StringComparison.Ordinal))
                    .ToList();

                Assert.That(themeClasses.Count, Is.EqualTo(1),
                    $"Expected exactly one zep-theme-* class after SetTheme(\"{theme.Id}\"), found: {string.Join(", ", themeClasses)}");
                Assert.That(themeClasses[0], Is.EqualTo(ZepThemeRegistry.GetThemeClassName(theme.Id)));
            }
        }

        [Test]
        public void SetTheme_LeavesExactlyOneThemeStyleSheetAttached()
        {
            var shell = new ZepShellView(new ZepState());

            foreach (var theme in FileBackedThemes(shell.Registry))
            {
                InvokeSetTheme(shell, theme.Id);

                var themeSheets = CollectThemeStyleSheets(shell);
                Assert.That(themeSheets.Count, Is.EqualTo(1),
                    $"Expected exactly one theme stylesheet after SetTheme(\"{theme.Id}\"), found {themeSheets.Count}");

                var expectedSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(theme.StyleSheetPath);
                Assert.That(themeSheets[0], Is.EqualTo(expectedSheet),
                    $"Attached theme stylesheet should match registry path for \"{theme.Id}\"");
            }
        }

        // §9.2 — Cascade structural invariant
        // resolvedStyle requires a rendered panel and is not testable in batchmode -nographics.
        // The class + stylesheet combination IS the mechanism that enables cascade to reach
        // descendants; these tests verify that mechanism is correctly wired.

        [Test]
        public void SetTheme_ClassAndSheetBothUpdateOnEachSwitch()
        {
            var shell = new ZepShellView(new ZepState());
            var themes = FileBackedThemes(shell.Registry);
            Assume.That(themes.Count, Is.GreaterThan(0));

            var firstId = themes[0].Id;
            var secondId = themes.Count > 1 ? themes[1].Id : themes[0].Id;

            InvokeSetTheme(shell, firstId);
            var classAfterFirst = shell.GetClasses()
                .Where(c => c.StartsWith("zep-theme-", System.StringComparison.Ordinal))
                .ToList();
            var sheetAfterFirst = CollectThemeStyleSheets(shell);

            Assert.That(classAfterFirst, Has.Count.EqualTo(1));
            Assert.That(classAfterFirst[0], Is.EqualTo(ZepThemeRegistry.GetThemeClassName(firstId)));
            Assert.That(sheetAfterFirst, Has.Count.EqualTo(1));

            InvokeSetTheme(shell, secondId);
            var classAfterSecond = shell.GetClasses()
                .Where(c => c.StartsWith("zep-theme-", System.StringComparison.Ordinal))
                .ToList();
            var sheetAfterSecond = CollectThemeStyleSheets(shell);

            Assert.That(classAfterSecond, Has.Count.EqualTo(1));
            Assert.That(classAfterSecond[0], Is.EqualTo(ZepThemeRegistry.GetThemeClassName(secondId)));
            Assert.That(sheetAfterSecond, Has.Count.EqualTo(1));
            Assert.That(sheetAfterSecond[0], Is.EqualTo(
                AssetDatabase.LoadAssetAtPath<StyleSheet>(shell.Registry.GetTheme(secondId).StyleSheetPath)));
        }

        // §5.12 — the registry republishes ThemesChanged on every theme asset import.
        // The shell used to re-apply the current theme unconditionally: sheet
        // remove/add, root class swap, icon-cache drop and a ThemeApplied fan-out to
        // every mirror (settings, Quick Look, popups) for a theme nobody changed.

        [Test]
        public void RegistryThemesChanged_WithAnUnchangedRegistry_DoesNotReapplyTheTheme()
        {
            var shell = new ZepShellView(new ZepState());
            var themes = FileBackedThemes(shell.Registry);
            Assume.That(themes.Count, Is.GreaterThan(0));
            InvokeSetTheme(shell, themes[0].Id);

            var reapplied = 0;
            shell.ThemeApplied += _ => reapplied++;
            ZepThemeSwitcher.ApplyThemeSheetCountForTests = 0;

            shell.Registry.NotifyThemesChanged();

            Assert.That(ZepThemeSwitcher.ApplyThemeSheetCountForTests, Is.Zero,
                "an unchanged registry must not re-attach the theme sheet");
            Assert.That(reapplied, Is.Zero, "no theme changed, so nothing may be announced");
            Assert.That(CollectThemeStyleSheets(shell), Has.Count.EqualTo(1));
        }

        [Test]
        public void RegistryThemesChanged_WhenTheSheetAssetWasReloaded_ReappliesTheTheme()
        {
            var shell = new ZepShellView(new ZepState());
            var themes = FileBackedThemes(shell.Registry);
            Assume.That(themes.Count, Is.GreaterThan(0));
            InvokeSetTheme(shell, themes[0].Id);

            // A theme .uss reimport leaves the id alone but hands the next
            // LoadAssetAtPath a NEW StyleSheet object while the switcher still holds —
            // and the root still wears — the old one. Reproduce exactly that state.
            var loaded = AssetDatabase.LoadAssetAtPath<StyleSheet>(themes[0].StyleSheetPath);
            var stale = ScriptableObject.CreateInstance<StyleSheet>();
            try
            {
                shell.styleSheets.Remove(loaded);
                shell.styleSheets.Add(stale);
                SetActiveThemeSheetForTest(shell.ThemeSwitcher, stale);

                var reapplied = 0;
                shell.ThemeApplied += _ => reapplied++;
                ZepThemeSwitcher.ApplyThemeSheetCountForTests = 0;

                shell.Registry.NotifyThemesChanged();

                Assert.That(ZepThemeSwitcher.ApplyThemeSheetCountForTests, Is.EqualTo(1),
                    "a reloaded sheet object is a real change and must swap in");
                Assert.That(reapplied, Is.EqualTo(1));
                Assert.That(CollectThemeStyleSheets(shell), Is.EqualTo(new[] { loaded }));
            }
            finally
            {
                shell.styleSheets.Remove(stale);
                UnityEngine.Object.DestroyImmediate(stale);
            }
        }

        [Test]
        public void SetTheme_ToADifferentTheme_StillAppliesOnce()
        {
            var shell = new ZepShellView(new ZepState());
            var themes = FileBackedThemes(shell.Registry);
            Assume.That(themes.Count, Is.GreaterThan(1));
            InvokeSetTheme(shell, themes[0].Id);

            ZepThemeSwitcher.ApplyThemeSheetCountForTests = 0;
            InvokeSetTheme(shell, themes[1].Id);

            Assert.That(ZepThemeSwitcher.ApplyThemeSheetCountForTests, Is.EqualTo(1));
            Assert.That(shell.ThemeSwitcher.CurrentTheme, Is.EqualTo(themes[1].Id));
        }

        [Test]
        public void IsAppliedThemeUpToDate_ForADefinitionTheme_IsAlwaysFalse()
        {
            var shell = new ZepShellView(new ZepState());
            try
            {
                var definition = BuildRuntimeDefinition("TestRuntimeUpToDate");
                InvokeSetThemeDefinition(shell, definition);

                Assert.That(shell.ThemeSwitcher.IsAppliedThemeUpToDate(definition.Id), Is.False,
                    "a definition theme is rebuilt from its variables on every apply, so it can never early-out");
            }
            finally
            {
                shell.Teardown();
            }
        }

        // §9.3 + §9.4 — Folder icon tint management via ApplyFolderIconTint

        [Test]
        public void ApplyFolderIconTint_TagColorFolder_SetsInlineTintAndRemovesCascadeClass()
        {
            var iconElement = new VisualElement();
            var entry = new ZepAssetEntry(
                "Art",
                "Assets/Art",
                "Folder",
                folderTintColor: ZepTagColor.Blue);

            AssetEntryRenderer.ApplyFolderIconTint(iconElement, entry);

            Assert.That(iconElement.ClassListContains("zep-themed-folder-icon"), Is.False,
                "Tag-colored folder should not have zep-themed-folder-icon — cascade tint would override the tag color");
            Assert.That(iconElement.style.unityBackgroundImageTintColor.keyword, Is.Not.EqualTo(StyleKeyword.Null),
                "Tag-colored folder should have an explicit inline tint, not the cascade default");
            Assert.That(iconElement.style.unityBackgroundImageTintColor.value,
                Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Blue)),
                "Inline tint should equal the tag color from the palette");
        }

        [Test]
        public void ApplyFolderIconTint_UntaggedFolder_ClearsInlineTintAndAddsCascadeClass()
        {
            var iconElement = new VisualElement();
            iconElement.style.unityBackgroundImageTintColor = new StyleColor(Color.red);

            var entry = new ZepAssetEntry(
                "Art",
                "Assets/Art",
                "Folder",
                folderTintColor: ZepTagColor.None);

            AssetEntryRenderer.ApplyFolderIconTint(iconElement, entry);

            Assert.That(iconElement.ClassListContains("zep-themed-folder-icon"), Is.True,
                "Untagged folder should have zep-themed-folder-icon so the theme cascade controls the tint");
            Assert.That(iconElement.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null),
                "Untagged folder should revert to cascade tint via StyleKeyword.Null");
        }

        [Test]
        public void ApplyFolderIconTint_RevertFromTagColor_RestoresCascade()
        {
            var iconElement = new VisualElement();

            var taggedEntry = new ZepAssetEntry(
                "Art", "Assets/Art", "Folder",
                folderTintColor: ZepTagColor.Red);
            AssetEntryRenderer.ApplyFolderIconTint(iconElement, taggedEntry);

            Assert.That(iconElement.ClassListContains("zep-themed-folder-icon"), Is.False);
            Assert.That(iconElement.style.unityBackgroundImageTintColor.keyword, Is.Not.EqualTo(StyleKeyword.Null));

            var untaggedEntry = new ZepAssetEntry(
                "Art", "Assets/Art", "Folder",
                folderTintColor: ZepTagColor.None);
            AssetEntryRenderer.ApplyFolderIconTint(iconElement, untaggedEntry);

            Assert.That(iconElement.ClassListContains("zep-themed-folder-icon"), Is.True,
                "After removing the tag color, zep-themed-folder-icon should be restored for cascade tinting");
            Assert.That(iconElement.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null),
                "After removing the tag color, cascade should be re-engaged via StyleKeyword.Null");
        }

        [Test]
        public void ApplyFolderIconTint_NonFolder_RemovesCascadeClass()
        {
            var iconElement = new VisualElement();
            iconElement.AddToClassList("zep-themed-folder-icon");

            var entry = new ZepAssetEntry(
                "Marble.mat", "Assets/Marble.mat", "Material");

            AssetEntryRenderer.ApplyFolderIconTint(iconElement, entry);

            Assert.That(iconElement.ClassListContains("zep-themed-folder-icon"), Is.False,
                "Non-folder asset should not have zep-themed-folder-icon");
            Assert.That(iconElement.style.unityBackgroundImageTintColor.keyword, Is.EqualTo(StyleKeyword.Null));
        }

        // §9.5 — No tintColor writes inside CustomStyleResolvedEvent callbacks.
        // We check for the specific anti-pattern: tintColor assignment paired
        // with CustomStyleResolvedEvent in the same file.

        [Test]
        public void ProductionCode_HasNoTintColorWritesInCustomStyleResolvedCallbacks()
        {
            var packageEditorRoot = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor");
            Assert.That(Directory.Exists(packageEditorRoot), Is.True,
                $"Package editor root not found at: {packageEditorRoot}");

            var violations = Directory
                .GetFiles(packageEditorRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "Tests" + Path.DirectorySeparatorChar))
                .Where(f =>
                {
                    var text = File.ReadAllText(f);
                    return text.Contains("CustomStyleResolvedEvent") && text.Contains("tintColor");
                })
                .Select(Path.GetFileName)
                .ToList();

            Assert.That(violations, Is.Empty,
                $"tintColor write inside CustomStyleResolvedEvent callback found in: {string.Join(", ", violations)}");
        }

        // §9.6 — Schema parity: Defaults and every built-in theme USS declare exactly
        // the schema variables, in schema order. Defaults is deliberately included:
        // it once escaped this test and drifted from the schema silently. Its selector
        // scope differs (.zep-root without a zep-theme class), but variable
        // declarations parse identically.

        [Test]
        public void BuiltinThemeUssFiles_DeclareEverySchemaVariable()
        {
            var stylesPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles");
            Assert.That(Directory.Exists(stylesPath), Is.True, $"Styles folder missing: {stylesPath}");

            var themeFiles = Directory.GetFiles(stylesPath, "ZepTheme_*.uss").ToList();
            Assert.That(themeFiles, Is.Not.Empty, "No built-in theme USS files discovered");

            var schema = ZepThemeSchema.RequiredVariables.ToList();
            var declRegex = new Regex(@"(--zep-[a-z0-9-]+)\s*:", RegexOptions.Compiled);

            foreach (var file in themeFiles)
            {
                var text = File.ReadAllText(file);
                var declared = new List<string>();
                foreach (Match m in declRegex.Matches(text))
                    declared.Add(m.Groups[1].Value);

                var missing = schema.Except(declared).OrderBy(s => s).ToList();
                Assert.That(missing, Is.Empty,
                    $"{Path.GetFileName(file)} missing schema variables: {string.Join(", ", missing)}");

                var extra = declared.Except(schema).OrderBy(s => s).ToList();
                Assert.That(extra, Is.Empty,
                    $"{Path.GetFileName(file)} declares variables outside the schema: {string.Join(", ", extra)}");

                Assert.That(declared, Is.EqualTo(schema),
                    $"{Path.GetFileName(file)} must declare the {schema.Count} schema variables exactly once each, in schema order (see THEME_SCHEMA.md)");
            }
        }

        // §9.7 — In-memory apply: SetTheme(ZepThemeDefinition, bool) attaches
        // a runtime StyleSheet and adds the theme class without touching disk.

        [Test]
        public void SetTheme_InMemoryDefinition_AddsClassAndAttachesRuntimeStyleSheet()
        {
            var shell = new ZepShellView(new ZepState());
            var variables = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var name in ZepThemeSchema.RequiredVariables)
                variables[name] = Color.magenta;

            var definition = new ZepThemeDefinition(
                "TestRuntime",
                "Test (Runtime)",
                isLight: false,
                ZepThemeSource.UserPrefs,
                variables.ToZepColorDict());

            InvokeSetThemeDefinition(shell, definition);

            var themeClasses = shell.GetClasses()
                .Where(c => c.StartsWith("zep-theme-", StringComparison.Ordinal))
                .ToList();
            Assert.That(themeClasses, Has.Count.EqualTo(1),
                $"Expected exactly one zep-theme-* class, got: {string.Join(", ", themeClasses)}");
            Assert.That(themeClasses[0],
                Is.EqualTo(ZepThemeRegistry.GetThemeClassName("TestRuntime")));

            var hasRuntimeSheet = false;
            for (var i = 0; i < shell.styleSheets.count; i++)
            {
                var sheet = shell.styleSheets[i];
                if (sheet == null) continue;
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sheet)))
                    hasRuntimeSheet = true;
            }
            Assert.That(hasRuntimeSheet, Is.True,
                "RuntimeThemeStyleSheetBuilder should produce a non-asset StyleSheet attached to the shell.");

            shell.Teardown();

            for (var i = 0; i < shell.styleSheets.count; i++)
            {
                var sheet = shell.styleSheets[i];
                Assert.That(sheet == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sheet)), Is.True,
                    "Shell teardown should remove its final runtime theme stylesheet.");
            }
        }

        // §9.7b — Runtime theme sheet lifetime. The sheet is a bare
        // ScriptableObject, not an asset: without HideAndDontSave a scene load
        // destroys it and the panel silently falls back to Defaults mid-session.
        // The switcher is its only owner, so teardown must destroy it exactly
        // once and a later SetTheme must build a fresh one.

        [Test]
        public void RuntimeThemeStyleSheet_IsFlaggedHideAndDontSave_SoASceneLoadCannotDestroyIt()
        {
            var sheet = RuntimeThemeStyleSheetBuilder.Build(
                "TestRuntimeFlags",
                BuildSchemaColors(Color.magenta));

            Assert.That(sheet, Is.Not.Null,
                "the in-memory theme path must be available in this editor");
            try
            {
                Assert.That(sheet.hideFlags, Is.EqualTo(HideFlags.HideAndDontSave));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sheet);
            }
        }

        [Test]
        public void ShellTeardown_DestroysTheRuntimeThemeSheetOnce_AndReapplyRebuildsIt()
        {
            var shell = new ZepShellView(new ZepState());

            try
            {
                InvokeSetThemeDefinition(shell, BuildRuntimeDefinition("TestRuntimeLifetime"));
                var first = FindAttachedRuntimeSheet(shell);
                Assert.That(first, Is.Not.Null,
                    "SetTheme(definition) must attach a runtime sheet to start with");

                shell.Teardown();

                Assert.That(first == null, Is.True,
                    "teardown owns the runtime sheet and must destroy it");
                Assert.That(FindAttachedRuntimeSheet(shell), Is.Null,
                    "teardown must also detach it from the root");

                // Second teardown is a no-op: nothing left to destroy, no throw.
                shell.Teardown();

                InvokeSetThemeDefinition(shell, BuildRuntimeDefinition("TestRuntimeLifetime"));
                var second = FindAttachedRuntimeSheet(shell);
                Assert.That(second, Is.Not.Null,
                    "re-applying the theme after a teardown must build a fresh sheet");
                Assert.That(ReferenceEquals(second, first), Is.False);
            }
            finally
            {
                shell.Teardown();
            }
        }

        [Test]
        public void WindowDisable_TearsDownTheShell_SoAReloadCannotOrphanTheRuntimeThemeSheet()
        {
            var shell = new ZepShellView(new ZepState());
            var window = ScriptableObject.CreateInstance<ZepWindow>();

            try
            {
                InvokeSetThemeDefinition(shell, BuildRuntimeDefinition("TestRuntimeReload"));
                var sheet = FindAttachedRuntimeSheet(shell);
                Assert.That(sheet, Is.Not.Null);

                typeof(ZepWindow)
                    .GetField(
                        "_shell",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(window, shell);

                // OnDisable is the door a domain reload goes through; OnDestroy
                // is not called there, so this is where the sheet must be freed.
                typeof(ZepWindow)
                    .GetMethod(
                        "OnDisable",
                        System.Reflection.BindingFlags.Instance
                            | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(window, null);

                Assert.That(sheet == null, Is.True,
                    "OnDisable must release the shell's runtime theme sheet");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                shell.Teardown();
            }
        }

        private static Dictionary<string, Color> BuildSchemaColors(Color value)
        {
            var variables = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var name in ZepThemeSchema.RequiredVariables)
                variables[name] = value;
            return variables;
        }

        private static ZepThemeDefinition BuildRuntimeDefinition(string id)
        {
            return new ZepThemeDefinition(
                id,
                id,
                isLight: false,
                ZepThemeSource.UserPrefs,
                BuildSchemaColors(Color.magenta).ToZepColorDict());
        }

        private static StyleSheet FindAttachedRuntimeSheet(ZepShellView shell)
        {
            for (var i = 0; i < shell.styleSheets.count; i++)
            {
                var sheet = shell.styleSheets[i];
                if (sheet == null) continue;
                if (string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sheet)))
                    return sheet;
            }

            return null;
        }

        // §9.8 — Registry uniformity: project-asset themes appear in AvailableDefinitions
        // alongside built-ins, with the correct Source.

        [Test]
        public void Registry_AvailableDefinitions_IncludesProjectAssetThemesAlongsideBuiltins()
        {
            using var scope = new TempAssetScope();

            var asset = scope.CreateScriptableObjectAsset<ZepUserTheme>("FixtureTheme.asset");
            var vars = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var name in ZepThemeSchema.RequiredVariables)
                vars[name] = Color.cyan;
            asset.Apply("FixtureUserTheme", "Fixture User Theme", isLight: false, vars.ToZepColorDict());
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            var registry = ZepThemeProviderRegistration.CreateConfigured();
            var defs = registry.AvailableDefinitions;
            Assert.That(defs.Any(d => d.Source == ZepThemeSource.Builtin), Is.True,
                "Built-in themes must be registered before project assets.");

            var fixtureDef = defs.FirstOrDefault(d => d.Id == "FixtureUserTheme");
            Assert.That(fixtureDef, Is.Not.Null,
                $"Fixture user theme not surfaced. Got ids: {string.Join(", ", defs.Select(d => d.Id))}");
            Assert.That(fixtureDef.Source, Is.EqualTo(ZepThemeSource.ProjectAsset));
        }

        // §9.9 — Round-trip: ZepUserTheme → ScriptableObject save → reload →
        // ToDefinition preserves Id and variable values.

        [Test]
        public void ZepUserTheme_RoundTrip_PreservesIdAndVariables()
        {
            using var scope = new TempAssetScope();

            var initial = scope.CreateScriptableObjectAsset<ZepUserTheme>("RoundTrip.asset");
            var path = AssetDatabase.GetAssetPath(initial);
            var vars = new Dictionary<string, Color>(StringComparer.Ordinal)
            {
                { "--zep-bg-root", new Color(0.1f, 0.2f, 0.3f, 1f) },
                { "--zep-text-main", new Color(0.9f, 0.9f, 0.9f, 1f) },
                { "--zep-bg-button-primary", new Color(0.4f, 0.5f, 0.8f, 1f) },
            };
            initial.Apply("RoundTripTheme", "Round Trip", isLight: true, vars.ToZepColorDict());
            EditorUtility.SetDirty(initial);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

            var reloaded = AssetDatabase.LoadAssetAtPath<ZepUserTheme>(path);
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded.ThemeId, Is.EqualTo("RoundTripTheme"));
            Assert.That(reloaded.IsLight, Is.True);

            var def = reloaded.ToDefinition();
            Assert.That(def.Source, Is.EqualTo(ZepThemeSource.ProjectAsset));
            Assert.That(def.Variables.Count, Is.EqualTo(vars.Count));
            foreach (var kv in vars)
                Assert.That(def.Variables[kv.Key], Is.EqualTo(kv.Value.ToZepColor()), $"Mismatch for {kv.Key}");
        }

        // §9.10 — Transition staleness: no color-class property in any transition-property
        // rule. The single documented exception is the sidebar tags-row drop-pulse
        // (background-color, 0.6s) in Zep.uss — exactly one occurrence; a
        // second background-color transition anywhere is a new violation.

        [Test]
        public void AllThemeUss_NoColorDrivenTransitions_ExceptDocumentedPulse()
        {
            var packageEditorRoot = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor");
            Assert.That(Directory.Exists(packageEditorRoot), Is.True);

            const string pulseFileName = "Zep.uss";

            var disallowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "all",
                "color",
                "background-color",
                "border-color",
                "border-top-color",
                "border-right-color",
                "border-bottom-color",
                "border-left-color",
                "-unity-background-image-tint-color",
            };

            var transitionRegex = new Regex(@"transition-property\s*:\s*([^;]+);", RegexOptions.Compiled);
            var violations = new List<string>();
            var pulseOccurrences = 0;

            foreach (var file in Directory.GetFiles(packageEditorRoot, "*.uss", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(file);
                var text = File.ReadAllText(file);

                foreach (Match m in transitionRegex.Matches(text))
                {
                    var props = m.Groups[1].Value
                        .Split(',')
                        .Select(p => p.Trim())
                        .Where(p => p.Length > 0)
                        .ToList();
                    foreach (var prop in props)
                    {
                        if (!disallowed.Contains(prop)) continue;
                        if (fileName == pulseFileName && prop == "background-color")
                        {
                            pulseOccurrences++;
                            continue;
                        }
                        violations.Add($"{fileName}: transition-property includes '{prop}'");
                    }
                }
            }

            Assert.That(violations, Is.Empty,
                "Color-driven transitions cause stale-color flashes on theme swap. Found:\n  "
                    + string.Join("\n  ", violations));
            Assert.That(pulseOccurrences, Is.EqualTo(1),
                $"{pulseFileName} must contain exactly one background-color transition-property "
                    + $"(the documented sidebar tags-row drop-pulse); found {pulseOccurrences}.");
        }

        // §3 (styling-standards) — the selection fill is a translucent accent tint
        // whose opacity is a fixed property of the design, not of the theme. Before
        // this was pinned, --zep-bg-item-selected-accent ranged from an opaque solid
        // (MacOS/Unity/Vanilla) to alpha 0.14 (SereneLight), so "selected" shouted in
        // one theme and all but vanished in another. Only the alpha is fixed; each
        // theme keeps its own hue.

        [Test]
        public void EveryThemeUss_DeclaresTheSameSelectionAccentAlpha()
        {
            var stylesPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles");
            var themeFiles = Directory.GetFiles(stylesPath, "ZepTheme_*.uss").ToList();
            Assert.That(themeFiles, Is.Not.Empty, "No built-in theme USS files discovered");

            var declRegex = new Regex(
                @"--zep-bg-item-selected-accent\s*:\s*(?<value>[^;]+);", RegexOptions.Compiled);
            var alphaRegex = new Regex(
                @"^rgba\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*(?<alpha>[0-9.]+)\s*\)$", RegexOptions.Compiled);
            var violations = new List<string>();

            foreach (var file in themeFiles)
            {
                var name = Path.GetFileName(file);
                var match = declRegex.Match(File.ReadAllText(file));
                Assert.That(match.Success, Is.True, $"{name} does not declare --zep-bg-item-selected-accent");

                var value = match.Groups["value"].Value.Trim();
                var alphaMatch = alphaRegex.Match(value);
                if (!alphaMatch.Success)
                {
                    violations.Add($"{name}: '{value}' is not a translucent rgba(r, g, b, a) value");
                    continue;
                }

                var alpha = float.Parse(
                    alphaMatch.Groups["alpha"].Value, CultureInfo.InvariantCulture);
                if (Mathf.Abs(alpha - ThemeDerivation.SelectedAccentAlpha) > 0.001f)
                {
                    violations.Add(
                        $"{name}: alpha {alpha} != {ThemeDerivation.SelectedAccentAlpha}");
                }
            }

            Assert.That(violations, Is.Empty,
                "Every theme must paint the selection accent at the same opacity "
                    + $"({ThemeDerivation.SelectedAccentAlpha}, ThemeDerivation.SelectedAccentAlpha). Found:\n  "
                    + string.Join("\n  ", violations));
        }

        // §3 (styling-standards) — selection is ONE token pair for every selectable
        // surface. The grid used to own --zep-*-grid-item-selected, which drifted until
        // the same selection read differently in list and grid mode. The pair is gone
        // from the schema, so this asserts both halves: the grid binds the shared
        // tokens, and the old names survive nowhere.

        [Test]
        public void GridTileSelection_BindsTheSharedSelectionTokens()
        {
            var editorRoot = Path.GetFullPath("Packages/com.van-unity.zep/Editor");
            var mainSheetPath = Path.Combine(editorRoot, "UI", "Styles", "Zep.uss");
            // Matched directly rather than via ExtractRule: this is a two-selector
            // group, so the separator's exact whitespace must not decide the test.
            // The selection is painted on the tile's card (the root is the
            // unpainted row-cell that hosts the sub-asset group container).
            const string subject =
                @"\.zep-asset-grid-item-selected\s*>\s*\.zep-asset-grid-card";
            var match = Regex.Match(
                File.ReadAllText(mainSheetPath),
                subject + @"\s*,\s*" + subject + @":hover\s*\{(?<body>[^}]*)\}",
                RegexOptions.CultureInvariant);
            Assert.That(match.Success, Is.True,
                "Missing the grid tile selection rule in Zep.uss");

            var rule = match.Groups["body"].Value;
            StringAssert.Contains("background-color: var(--zep-bg-item-selected-accent);", rule);
            StringAssert.Contains("border-color: var(--zep-border-item-selected-accent);", rule);

            // The exact token names, not the substring: the rule above carries a
            // comment naming the removed pair as --zep-*-grid-item-selected.
            var removed = new[] { "--zep-bg-grid-item-selected", "--zep-border-grid-item-selected" };
            var survivors = Directory
                .GetFiles(editorRoot, "*.uss", SearchOption.AllDirectories)
                .Where(f => removed.Any(File.ReadAllText(f).Contains))
                .Select(Path.GetFileName)
                .OrderBy(f => f)
                .ToList();
            Assert.That(survivors, Is.Empty,
                "--zep-bg-grid-item-selected / --zep-border-grid-item-selected were removed from the "
                    + "schema; a sheet still referencing them resolves to nothing. Found in:\n  "
                    + string.Join("\n  ", survivors));
        }

        // The ping is one thin ring in the selection accent pair — the same
        // tokens every selected surface uses — never a stack of rings in
        // unrelated high-contrast colours. A second ring rule must not return.
        [Test]
        public void AssetPingPulseUss_IsOneThinRingInTheSelectionAccent()
        {
            var mainSheetPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var text = File.ReadAllText(mainSheetPath);
            var ringRule = ExtractRule(text, ".zep-asset-ping-ring");
            StringAssert.Contains("border-width: 1px;", ringRule);
            StringAssert.Contains("background-color: var(--zep-bg-item-selected-accent);", ringRule);
            StringAssert.Contains("border-color: var(--zep-border-item-selected-accent);", ringRule);
            StringAssert.DoesNotContain(".zep-asset-ping-ring-echo", text);
        }

        // §4 (styling-standards) — static geometry belongs to the class, not to an
        // inline write. `.zep-creator-slider` and `.zep-drag-overlay-icon` each used to
        // carry a duplicate inline copy in C#; this pins the values the sheet must keep
        // delivering now that the copies are gone, on the real element types the
        // production code builds (so Unity's own slider/image styling is in the cascade
        // too). Unlike §9.2 above, this runs in the batchmode rig because the rig is
        // not invoked with -nographics.

        [UnityTest]
        public IEnumerator StructuralSheet_OwnsCreatorSliderAndDragGhostIconGeometry()
        {
            var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            Assert.That(styleSheet, Is.Not.Null, "the single structural sheet must be loadable");

            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            window.position = new Rect(100f, 100f, 600f, 400f);
            window.Show();
            window.rootVisualElement.styleSheets.Add(styleSheet);

            var slider = new SliderInt(0, 255) { showInputField = false };
            slider.AddToClassList("zep-creator-slider");
            window.rootVisualElement.Add(slider);

            var icon = new Image { scaleMode = ScaleMode.ScaleToFit };
            icon.AddToClassList("zep-drag-overlay-icon");
            window.rootVisualElement.Add(icon);

            var gridRow = new VisualElement();
            gridRow.AddToClassList(ZepUiClasses.AssetGridRow);
            window.rootVisualElement.Add(gridRow);

            try
            {
                yield return null;

                Assert.That(slider.resolvedStyle.flexGrow, Is.EqualTo(1f),
                    ".zep-creator-slider must supply flex-grow — ThemeCreatorScreen no longer writes it inline");
                Assert.That(icon.resolvedStyle.width, Is.EqualTo(16f),
                    ".zep-drag-overlay-icon must supply the 16px box the drag ghost used to pin inline");
                Assert.That(icon.resolvedStyle.height, Is.EqualTo(16f),
                    ".zep-drag-overlay-icon must supply the 16px box the drag ghost used to pin inline");
                Assert.That(icon.resolvedStyle.flexShrink, Is.EqualTo(0f),
                    ".zep-drag-overlay-icon must keep the ghost icon from shrinking beside a long label");
                Assert.That(gridRow.resolvedStyle.flexGrow, Is.EqualTo(0f),
                    "fixed-height grid rows must not absorb empty viewport space");
            }
            finally
            {
                window.Close();
            }
        }

        // §3.1 (styling-standards) — Unity's built-in composite controls paint their
        // OWN sub-elements from the EDITOR SKIN, not from the panel theme, and those
        // built-in declarations block inheritance. Any sub-element the sheet does not
        // re-bind therefore renders in the skin's colour and ignores the theme
        // entirely — it does not merely look wrong in one theme, it looks identical
        // in all of them. That is what shipped in the New Tag prompt: the PopupField's
        // label resolved to rgb(196,196,196) and its value text to rgb(228,228,228)
        // under every one of the 16 themes, and the asset-grid zoom slider kept the
        // skin's rgb(153,153,153) dragger on a rgb(94,94,94) tracker.
        //
        // Grepping the sheet cannot catch this — the offending rule looks complete,
        // it just omits one property. So this asserts the RESOLVED colour of each
        // sub-element against the token its rule binds, in every built-in theme.
        // Add a leg here whenever a Unity composite control gains a themed surface.

        private static readonly (string Element, string Token)[] SkinLeakProbes =
        {
            ("prompt caption", "--zep-text-main"),
            ("prompt popup value text", "--zep-text-input"),
            ("prompt text field text", "--zep-text-input"),
            ("prompt popup arrow tint", "--zep-icon-tint"),
            ("grid size slider dragger", "--zep-text-subtle"),
            ("grid size slider tracker", "--zep-border-input"),
            ("settings tag favourite (inactive)", "--zep-text-subtle"),
            // ToolbarSearchField's two buttons paint background images that come
            // from the EDITOR SKIN (d_search_icon@2x / d_clear_uielements@2x).
            // Both rules key on the `unity-search-field-base__` vocabulary — the
            // element does NOT carry ToolbarSearchField's own
            // `unity-toolbar-search-field__*` spelling, and the sheet keyed on
            // that spelling for months, so the whole search-glyph block was inert
            // and the skin icon showed through in all 17 themes.
            ("finder selected row fill", "--zep-bg-item-selected-accent"),
            ("finder selected row border", "--zep-border-item-selected-accent"),
            ("finder selected filter fill", "--zep-bg-item-selected-accent"),
            ("finder type row fill", "--zep-bg-item-selected-accent"),
            ("finder type field text", "--zep-text-input"),
            ("finder type field fill", "--zep-bg-input"),
            ("finder type field border", "--zep-border-input"),
            ("search field text", "--zep-text-input"),
            ("search field glyph tint", "--zep-icon-tint"),
            ("search field clear glyph tint", "--zep-icon-tint"),
            // An input painted on --zep-bg-input: --zep-text-input is the half of
            // that pair §3.2 puts a contrast floor on. This bound --zep-text-main.
            ("settings tag name field text", "--zep-text-input"),
            ("settings toggle caption", "--zep-text-main"),
            ("settings toggle unchecked fill", "--zep-bg-input"),
            ("settings toggle unchecked border", "--zep-border-input"),
            ("settings toggle checked fill", "--zep-bg-button-primary"),
            ("settings toggle checked tick", "--zep-text-button-primary"),
            ("shortcut picker text", "--zep-text-input"),
            ("shortcut picker fill", "--zep-bg-input"),
            ("shortcut picker border", "--zep-border-input"),
            ("shortcut picker arrow", "--zep-icon-tint"),
            ("subasset parent fill", "--zep-bg-pane"),
            ("subasset child fill", "--zep-bg-asset-list-band"),
            ("subasset divider", "--zep-border-pane"),
            ("subasset type label", "--zep-text-subtle"),
            ("subasset disclosure tint", "--zep-icon-tint"),
        };

        [UnityTest]
        public IEnumerator EveryTheme_PaintsBuiltInControlSubElements_FromTokensNotTheEditorSkin()
        {
            var stylesPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles");
            var structural = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var defaults = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss");
            Assert.That(structural, Is.Not.Null, "the single structural sheet must be loadable");
            Assert.That(defaults, Is.Not.Null, "the fallback variable table must be loadable");

            var themeIds = Directory
                .GetFiles(stylesPath, "ZepTheme_*.uss")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(n => n.Substring("ZepTheme_".Length))
                .Where(id => !string.Equals(id, "Defaults", StringComparison.Ordinal))
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
            Assert.That(themeIds, Is.Not.Empty, "No built-in theme USS files discovered");

            var violations = new List<string>();

            foreach (var themeId in themeIds)
            {
                var themeSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                    "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_"
                        + themeId + ".uss");
                Assert.That(themeSheet, Is.Not.Null, $"theme sheet for {themeId} must be loadable");
                var tokens = ParseThemeTokens(Path.Combine(
                    stylesPath, "ZepTheme_" + themeId + ".uss"));

                var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
                window.position = new Rect(100f, 100f, 640f, 400f);
                window.Show();

                var root = new VisualElement();
                root.AddToClassList(ZepUiClasses.Root);
                root.AddToClassList("zep-theme-" + themeId);
                root.styleSheets.Add(structural);
                root.styleSheets.Add(defaults);
                root.styleSheets.Add(themeSheet);

                var card = new VisualElement();
                card.AddToClassList(ZepUiClasses.PromptCard);

                // Mirrors what the New Tag prompt actually builds: a stacked caption
                // above each control (ZepPromptOverlayWidget +
                // ContextMenuActions.BuildTagColorPickerExtraContent).
                var caption = new Label("Color");
                caption.AddToClassList(ZepUiClasses.PromptFieldLabel);
                card.Add(caption);

                var textField = new TextField { name = "zep-prompt-field" };
                textField.AddToClassList(ZepUiClasses.PromptField);
                card.Add(textField);

                var popup = new PopupField<string>(new List<string> { "Gray" }, 0)
                {
                    name = "zep-prompt-popup-field",
                };
                popup.AddToClassList(ZepUiClasses.PromptPopupField);
                card.Add(popup);
                root.Add(card);

                var slider = new SliderInt(16, 128) { showInputField = false };
                slider.AddToClassList(ZepUiClasses.AssetGridSizeSlider);
                root.Add(slider);

                // A chrome-less Button is a composite surface too: Unity's
                // .unity-button colours it from the skin unless the panel re-binds.
                var favourite = new Button { text = "☆" };
                favourite.AddToClassList(ZepUiClasses.SettingsTagFavoriteButton);
                root.Add(favourite);

                var parentTile = new VisualElement();
                parentTile.AddToClassList(ZepUiClasses.AssetGridItemGrouped);
                var parentFill = new VisualElement();
                parentFill.AddToClassList(ZepUiClasses.AssetGridSubAssetGroupBackground);
                parentTile.Add(parentFill);
                var disclosure = new Button();
                disclosure.AddToClassList(ZepUiClasses.AssetGridDisclosureButton);
                disclosure.AddToClassList(ZepUiClasses.FlatButton);
                disclosure.AddToClassList(ZepUiClasses.ThemedIcon);
                parentTile.Add(disclosure);
                root.Add(parentTile);
                var childTile = new VisualElement();
                childTile.AddToClassList(ZepUiClasses.AssetGridItemSubAsset);
                childTile.AddToClassList(ZepUiClasses.AssetGridItemGrouped);
                var childFill = new VisualElement();
                childFill.AddToClassList(ZepUiClasses.AssetGridSubAssetGroupBackground);
                childTile.Add(childFill);
                var typeLabel = new Label("Material");
                typeLabel.AddToClassList(ZepUiClasses.AssetGridSubAssetType);
                childTile.Add(typeLabel);
                root.Add(childTile);

                // Non-empty value on purpose: the clear button only paints a glyph
                // once the field has text (Unity adds a `--off` variant class while
                // it is empty, and the sheet honours it).
                var search = new UnityEditor.UIElements.ToolbarSearchField
                {
                    name = "zep-search",
                    value = "probe",
                };
                search.AddToClassList(ZepUiClasses.SearchField);
                root.Add(search);
                var finderWidget = new Zep.Editor.UI.TopBar.SearchBarWidget();
                var finderTypeField = finderWidget.Q<TextField>("zep-search-types-query");
                var finderFilter = finderWidget.Q<Button>("zep-search-type-all");
                finderFilter.RemoveFromHierarchy();
                root.Add(finderFilter);
                finderWidget.SetSearchText("t:Texture");
                typeof(Zep.Editor.UI.TopBar.SearchBarWidget)
                    .GetMethod("SetTypePickerVisible", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(finderWidget, new object[] { true });
                var finderTypeRow = finderWidget.Query<Button>(className: ZepUiClasses.SearchTypeRow)
                    .ToList().Single(button => (string)button.userData == "Texture");
                finderTypeRow.RemoveFromHierarchy();
                root.Add(finderTypeRow);
                var finderList = new VisualElement { name = "zep-search-results" };
                var finderItem = new VisualElement();
                finderItem.AddToClassList("unity-collection-view__item");
                var finderRow = new VisualElement();
                finderRow.AddToClassList(ZepUiClasses.SearchSuggestionRow);
                finderRow.AddToClassList(ZepUiClasses.SearchSuggestionRowSelected);
                finderItem.Add(finderRow);
                finderList.Add(finderItem);
                root.Add(finderList);
                finderTypeField.RemoveFromHierarchy();
                root.Add(finderTypeField);

                var tagNameField = new TextField();
                tagNameField.AddToClassList(ZepUiClasses.SettingsTagNameField);
                root.Add(tagNameField);

                var settings = new ZepSettingsScreen(BuildSettingsModel());
                settings.SetOpen(true);
                root.Add(settings);
                var uncheckedToggle = settings.Q<Toggle>("zep-settings-include-meta-toggle");
                var checkedToggle = settings.Q<Toggle>("zep-settings-prompt-meta-toggle");
                uncheckedToggle.SetValueWithoutNotify(false);
                checkedToggle.SetValueWithoutNotify(true);

                window.rootVisualElement.Add(root);

                try
                {
                    yield return null;
                    yield return null;

                    var actual = new Dictionary<string, Color>(StringComparer.Ordinal)
                    {
                        ["subasset parent fill"] = parentFill.resolvedStyle.backgroundColor,
                        ["subasset child fill"] = childFill.resolvedStyle.backgroundColor,
                        ["subasset divider"] = parentFill.resolvedStyle.borderRightColor,
                        ["subasset type label"] = typeLabel.resolvedStyle.color,
                        ["subasset disclosure tint"] = disclosure.resolvedStyle.unityBackgroundImageTintColor,
                        ["prompt caption"] = caption.resolvedStyle.color,
                        ["prompt popup value text"] = popup
                            .Q(className: "unity-base-popup-field__text").resolvedStyle.color,
                        ["prompt text field text"] = textField
                            .Q(className: "unity-text-element--inner-input-field-component")
                            .resolvedStyle.color,
                        ["prompt popup arrow tint"] = popup
                            .Q(className: "unity-base-popup-field__arrow")
                            .resolvedStyle.unityBackgroundImageTintColor,
                        ["grid size slider dragger"] = slider
                            .Q(className: "unity-base-slider__dragger")
                            .resolvedStyle.backgroundColor,
                        ["grid size slider tracker"] = slider
                            .Q(className: "unity-base-slider__tracker")
                            .resolvedStyle.backgroundColor,
                        ["settings tag favourite (inactive)"] = favourite.resolvedStyle.color,
                        ["finder selected row fill"] = finderRow.resolvedStyle.backgroundColor,
                        ["finder selected row border"] = finderRow.resolvedStyle.borderTopColor,
                        ["finder selected filter fill"] = finderFilter.resolvedStyle.backgroundColor,
                        ["finder type row fill"] = finderTypeRow.resolvedStyle.backgroundColor,
                        ["finder type field text"] = finderTypeField.Q(className: "unity-text-element--inner-input-field-component").resolvedStyle.color,
                        ["finder type field fill"] = finderTypeField.Q(className: "unity-base-text-field__input").resolvedStyle.backgroundColor,
                        ["finder type field border"] = finderTypeField.Q(className: "unity-base-text-field__input").resolvedStyle.borderTopColor,
                        ["search field text"] = search.Q<TextElement>(
                            className: "unity-text-element--inner-input-field-component").resolvedStyle.color,
                        ["search field glyph tint"] = search
                            .Q(className: "unity-search-field-base__search-button")
                            .resolvedStyle.unityBackgroundImageTintColor,
                        ["search field clear glyph tint"] = search
                            .Q(className: "unity-search-field-base__cancel-button")
                            .resolvedStyle.unityBackgroundImageTintColor,
                        ["settings tag name field text"] = tagNameField
                            .Q(className: "unity-base-text-field__input")
                            .resolvedStyle.color,
                        ["settings toggle caption"] = uncheckedToggle
                            .Q(className: "unity-toggle__text").resolvedStyle.color,
                        ["settings toggle unchecked fill"] = uncheckedToggle
                            .Q(className: "unity-toggle__checkmark").resolvedStyle.backgroundColor,
                        ["settings toggle unchecked border"] = uncheckedToggle
                            .Q(className: "unity-toggle__checkmark").resolvedStyle.borderTopColor,
                        ["settings toggle checked fill"] = checkedToggle
                            .Q(className: "unity-toggle__checkmark").resolvedStyle.backgroundColor,
                        ["settings toggle checked tick"] = checkedToggle
                            .Q(className: ZepUiClasses.SettingsToggleTick).resolvedStyle.color,
                        ["shortcut picker text"] = settings.Q("zep-search-shortcut-key")
                            .Q(className: "unity-base-popup-field__text").resolvedStyle.color,
                        ["shortcut picker fill"] = settings.Q("zep-search-shortcut-key")
                            .Q(className: "unity-base-popup-field__input").resolvedStyle.backgroundColor,
                        ["shortcut picker border"] = settings.Q("zep-search-shortcut-key")
                            .Q(className: "unity-base-popup-field__input").resolvedStyle.borderTopColor,
                        ["shortcut picker arrow"] = settings.Q("zep-search-shortcut-key")
                            .Q(className: "unity-base-popup-field__arrow").resolvedStyle.unityBackgroundImageTintColor,
                    };

                    foreach (var toggle in new[] { uncheckedToggle, checkedToggle })
                    {
                        Assert.That(NameOfBackgroundImage(toggle.Q(className: "unity-toggle__checkmark")),
                            Is.EqualTo("none"), "The skin's checkbox texture must not cover the themed fill.");
                    }

                    foreach (var probe in SkinLeakProbes)
                    {
                        Assert.That(tokens.ContainsKey(probe.Token), Is.True,
                            $"{themeId} does not declare {probe.Token}");
                        var expected = tokens[probe.Token];
                        var resolved = actual[probe.Element];
                        if (SameRgb(resolved, expected)) continue;
                        violations.Add(
                            $"{themeId}: {probe.Element} resolved to {Describe(resolved)} "
                                + $"but {probe.Token} is {Describe(expected)} — that colour is "
                                + "coming from the editor skin, not the theme");
                    }
                }
                finally
                {
                    window.Close();
                }

                yield return null;
            }

            Assert.That(violations, Is.Empty,
                "Unity composite-control sub-elements must be re-bound to --zep-* tokens "
                    + "(styling-standards.md §3.1). Found:\n  "
                    + string.Join("\n  ", violations));
        }

        // §3.1 — the tint probe above proves the search-field rules MATCH; this
        // proves they substitute the package's own art for the skin's, and that the
        // substitution respects the state Unity uses to hide the clear button.
        // Unity keeps the clear button in the layout at all times and drops its
        // glyph by clearing background-image on a `--off` variant class it adds
        // beside the base one. The panel's clear rule ties that variant on
        // specificity, so an empty search box would otherwise carry a permanent,
        // dead "clear" cross.

        [UnityTest]
        public IEnumerator SearchField_SubstitutesPackageGlyphs_AndClearsTheCrossWhenEmpty()
        {
            var structural = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var defaults = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss");
            Assert.That(structural, Is.Not.Null, "the single structural sheet must be loadable");
            Assert.That(defaults, Is.Not.Null, "the fallback variable table must be loadable");

            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 400f);
            window.Show();

            var root = new VisualElement();
            root.AddToClassList(ZepUiClasses.Root);
            root.styleSheets.Add(structural);
            root.styleSheets.Add(defaults);

            var search = new UnityEditor.UIElements.ToolbarSearchField
            {
                name = "zep-search",
                value = string.Empty,
            };
            search.AddToClassList(ZepUiClasses.SearchField);
            root.Add(search);
            window.rootVisualElement.Add(root);

            try
            {
                yield return null;
                yield return null;

                var glyph = search.Q(className: "unity-search-field-base__search-button");
                var clear = search.Q(className: "unity-search-field-base__cancel-button");
                Assert.That(glyph, Is.Not.Null,
                    "ToolbarSearchField must still expose unity-search-field-base__search-button — "
                        + "if this class was renamed the search-glyph rules are inert again");
                Assert.That(clear, Is.Not.Null,
                    "ToolbarSearchField must still expose unity-search-field-base__cancel-button");

                Assert.That(NameOfBackgroundImage(glyph), Is.EqualTo("search-icon"),
                    "the search glyph must be the package's own icon, not the editor skin's "
                        + "d_search_icon@2x (styling-standards.md §3.1)");

                Assert.That(NameOfBackgroundImage(clear), Is.EqualTo("none"),
                    "an EMPTY search box must paint no clear cross — Unity's --off variant "
                        + "class has to keep winning over the panel's substitution rule");

                search.value = "probe";
                yield return null;
                yield return null;

                Assert.That(NameOfBackgroundImage(clear), Is.EqualTo("plus-icon"),
                    "once the field has text the clear cross must be the package's own glyph "
                        + "(the plus texture turned 45°), not the skin's d_clear_uielements@2x");
                Assert.That(clear.resolvedStyle.rotate.angle.value, Is.EqualTo(45f).Within(0.01f),
                    "the clear glyph is the plus texture rotated into a cross — without the "
                        + "rotation it renders as a plus, which reads as 'add', not 'clear'");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SearchField_TextLineFitsInsideInput_WhenEmptyAndTyped()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 500f);
            window.Show();
            var root = window.rootVisualElement;
            root.AddToClassList(ZepUiClasses.Root);
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));
            root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss"));
            var bar = new TopBar.SearchBarWidget();
            root.Add(bar);
            bar.FocusSearch();
            var field = bar.Q<UnityEditor.UIElements.ToolbarSearchField>();
            var textField = field.Q<TextField>();
            try
            {
                float? emptyCaretY = null;
                foreach (var value in new[] { string.Empty, "Agyp", string.Empty })
                {
                    field.value = value;
                    textField.Focus();
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    var text = textField.Q<TextElement>(
                        className: "unity-text-element--inner-input-field-component");
                    Assert.That(text, Is.Not.Null);
                    var lineHeight = text.MeasureTextSize("Agyp", 0, VisualElement.MeasureMode.Undefined,
                        0, VisualElement.MeasureMode.Undefined).y;
                    Assert.That(text.contentRect.height, Is.GreaterThanOrEqualTo(lineHeight - 1f),
                        $"Search input clips its line: content={text.contentRect.height}, line={lineHeight}");
                    for (var ancestor = text.parent; ancestor != field.parent; ancestor = ancestor.parent)
                    {
                        Assert.That(text.worldBound.yMin, Is.GreaterThanOrEqualTo(ancestor.worldBound.yMin - 1f));
                        Assert.That(text.worldBound.yMax, Is.LessThanOrEqualTo(ancestor.worldBound.yMax + 1f),
                            $"Text extends below {ancestor.name} ({ancestor.GetType().Name})");
                    }
                    var caretY = textField.textSelection.cursorPosition.y;
                    if (emptyCaretY == null) emptyCaretY = caretY;
                    Assert.That(caretY, Is.EqualTo(emptyCaretY.Value).Within(1f),
                        "Typing or clearing search must not shift the caret vertically");
                }
            }
            finally
            {
                window.Close();
            }
        }

        private static string NameOfBackgroundImage(VisualElement element)
        {
            var image = element.resolvedStyle.backgroundImage;
            if (image.texture != null) return image.texture.name;
            if (image.sprite != null) return image.sprite.name;
            if (image.vectorImage != null) return image.vectorImage.name;
            return "none";
        }

        // §3 (styling-standards) — a "selected"/"active" surface is painted by a
        // plain class rule, but the sibling :hover rule on the same subject carries
        // one more simple selector and therefore OUT-RANKS it. Every such pair needs
        // an explicit `-selected:hover` companion or the selected surface silently
        // reverts under the pointer — which is exactly the bug the search-scope pill
        // already carries a WHY comment about. FlatButtonPaintTests covers the
        // view-mode buttons against their shared flat-button hover rule at runtime.

        [Test]
        public void SelectedSurfaces_KeepTheirPaintWhileHovered()
        {
            var text = File.ReadAllText(Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss"));

            var pairs = new[]
            {
                // (subject, the token the hovered state must still resolve to)
                (".zep-quicklook-filmstrip-tile-selected",
                    "--zep-bg-item-selected-accent"),
                (".zep-asset-grid-item-selected > .zep-asset-grid-card",
                    "--zep-bg-item-selected-accent"),
            };

            var missing = new List<string>();
            foreach (var (subject, token) in pairs)
            {
                // Matched as a two-selector group directly: the separator's exact
                // whitespace must not decide the test.
                var match = Regex.Match(
                    text,
                    Regex.Escape(subject) + @"\s*,\s*" + Regex.Escape(subject) + @":hover\s*\{(?<body>[^}]*)\}",
                    RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    missing.Add($"{subject} — no `<subject>, <subject>:hover` rule group");
                    continue;
                }

                if (!match.Groups["body"].Value.Contains($"background-color: var({token});"))
                {
                    missing.Add($"{subject} — its :hover companion does not bind {token}");
                }
            }

            Assert.That(missing, Is.Empty,
                "A selected/active surface must restate its fill on :hover, or the sibling "
                    + ":hover rule out-ranks it and the selection vanishes under the pointer. "
                    + "Found:\n  " + string.Join("\n  ", missing));
        }

        // §3.1 (styling-standards) — Unity lays an EMPTY single-line input's caret out
        // at the TOP of the inner text element and only honours the inherited
        // -unity-text-align: middle-left once the string is non-empty. So an input
        // whose content box is TALLER than its line box shows the caret high and then
        // jerks it down on the first keystroke. The prompt field forced height: 32px on
        // the field root and gave the input no vertical padding, which produced a 29px
        // content box around a 15px line box and put the caret 7.5px above centre.
        // Sizing the input from its own padding keeps the two boxes equal.

        [UnityTest]
        public IEnumerator PromptTextField_WhenEmpty_KeepsTheCaretOnTheSameLineAsTypedText()
        {
            var structural = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/Zep.uss");
            var defaults = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_Defaults.uss");
            Assert.That(structural, Is.Not.Null);
            Assert.That(defaults, Is.Not.Null);

            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            window.position = new Rect(100f, 100f, 640f, 400f);
            window.Show();

            var root = new VisualElement();
            root.AddToClassList(ZepUiClasses.Root);
            root.styleSheets.Add(structural);
            root.styleSheets.Add(defaults);

            var card = new VisualElement();
            card.AddToClassList(ZepUiClasses.PromptCard);
            var field = new TextField { name = "zep-prompt-field" };
            field.AddToClassList(ZepUiClasses.PromptField);
            card.Add(field);
            root.Add(card);
            window.rootVisualElement.Add(root);

            try
            {
                yield return null;
                yield return null;
                field.Focus();
                yield return null;
                yield return null;

                var whenEmpty = field.textSelection.cursorPosition.y;

                field.value = "Ag";
                yield return null;
                yield return null;

                var whenTyped = field.textSelection.cursorPosition.y;

                // Unsorted-expectation guard: these are two independently measured
                // positions, not one value read twice — before the fix they were
                // 14.52 and 21.76, a 7.2px jump.
                Assert.That(whenEmpty, Is.EqualTo(whenTyped).Within(1f),
                    $"The caret sits at y={whenEmpty} in an empty prompt field but at "
                        + $"y={whenTyped} once text is typed. The input's content box is "
                        + "taller than its line box — size it from its own padding instead "
                        + "of forcing a height on the field root.");

                var inner = field.Q(className: "unity-text-element--inner-input-field-component");
                Assert.That(inner.layout.height, Is.EqualTo(15f).Within(1.5f),
                    "The prompt input's inner text element must hug the line box; a taller "
                        + "box is what lets the empty-field caret drift to the top.");
            }
            finally
            {
                window.Close();
            }
        }

        // §3.2 (styling-standards) — legibility floors.
        //
        // A theme is a table of raw colours with nothing stopping a pair from
        // becoming unreadable, and 16 of the 17 tables had at least one such pair
        // on 2026-07-29: SolarLight's subtle text at 2.18:1, Antigravity Light's
        // at 2.14:1, and six themes whose primary/danger button label dropped
        // BELOW its resting contrast on :hover — the state that is supposed to
        // invite the click. Every floor below is the WCAG 2.x contrast ratio,
        // computed on the composited colour where a token is translucent.
        //
        // The modal legs are not text: a prompt card is only visible because the
        // scrim pushes the backdrop away from it, so they measure the card (and
        // its 1px border) against scrim-over-pane, not against the bare pane.

        private const float FloorBodyText = 4.5f;
        private const float FloorUiText = 3.0f;
        private const float FloorPlaceholder = 2.5f;
        private const float FloorCardVsBackdrop = 1.20f;
        private const float FloorBorderVsBackdrop = 1.30f;
        private const float FloorBorderVsCard = 1.12f;

        private static readonly (string Label, string Fg, string Bg, float Floor)[] ContrastPairs =
        {
            ("text-main / pane",      "--zep-text-main",              "--zep-bg-pane",                  FloorBodyText),
            ("text-main / root",      "--zep-text-main",              "--zep-bg-root",                  FloorBodyText),
            ("text-main / card",      "--zep-text-main",              "--zep-bg-prompt-card",           FloorBodyText),
            ("text-subtle / pane",    "--zep-text-subtle",            "--zep-bg-pane",                  FloorUiText),
            ("text-input / input",    "--zep-text-input",             "--zep-bg-input",                 FloorBodyText),
            ("placeholder / input",   "--zep-text-input-placeholder", "--zep-bg-input",                 FloorPlaceholder),
            ("primary label",         "--zep-text-button-primary",    "--zep-bg-button-primary",        FloorUiText),
            ("primary label :hover",  "--zep-text-button-primary",    "--zep-bg-button-primary-hover",  FloorUiText),
            ("primary label :active", "--zep-text-button-primary",    "--zep-bg-button-primary-active", FloorUiText),
            ("danger label",          "--zep-text-button-danger",     "--zep-bg-button-danger",         FloorUiText),
            ("danger label :hover",   "--zep-text-button-danger",     "--zep-bg-button-danger-hover",   FloorUiText),
            ("icon tint / pane",      "--zep-icon-tint",              "--zep-bg-pane",                  FloorUiText),
        };

        [Test]
        public void EveryBuiltInTheme_MeetsTheLegibilityFloors()
        {
            var stylesPath = Path.GetFullPath(
                "Packages/com.van-unity.zep/Editor/UI/Styles");
            var themeFiles = Directory.GetFiles(stylesPath, "ZepTheme_*.uss").ToList();
            Assert.That(themeFiles, Is.Not.Empty, "No built-in theme USS files discovered");

            var violations = new List<string>();
            foreach (var file in themeFiles)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var tokens = ParseThemeTokens(file);
                foreach (var v in ContrastViolations(name, tokens))
                    violations.Add(v);
            }

            Assert.That(violations, Is.Empty,
                "Theme tables must stay legible (styling-standards.md §3.2). Found:\n  "
                    + string.Join("\n  ", violations));
        }

        // The same floors for themes nobody hand-checks: ThemeDerivation builds a
        // whole table from one accent, so a bad rule there ships silently on every
        // colour a user picks. Sweeping the hue circle is the point — the light
        // branch's modal failed at EVERY hue, not at an unlucky one.
        [Test]
        public void DerivedUserThemes_MeetTheLegibilityFloors_AcrossTheHueCircle()
        {
            var violations = new List<string>();
            for (var step = 0; step < 24; step++)
            {
                var hue = step / 24f;
                foreach (var isLight in new[] { true, false })
                {
                    var accent = ZepColor.FromHsv(hue, 0.72f, isLight ? 0.80f : 0.72f);
                    var vars = ThemeDerivation.Compute(accent, isLight);
                    var tokens = vars.ToDictionary(
                        kv => kv.Key,
                        kv => new Color(kv.Value.R, kv.Value.G, kv.Value.B, kv.Value.A),
                        StringComparer.Ordinal);
                    var label = $"derived hue={hue:0.00} {(isLight ? "light" : "dark")}";
                    foreach (var v in ContrastViolations(label, tokens))
                        violations.Add(v);
                }
            }

            Assert.That(violations, Is.Empty,
                "ThemeDerivation must produce legible tables for any accent "
                    + "(styling-standards.md §3.2). Found:\n  "
                    + string.Join("\n  ", violations));
        }

        private static IEnumerable<string> ContrastViolations(
            string themeName, IDictionary<string, Color> t)
        {
            foreach (var (label, fg, bg, floor) in ContrastPairs)
            {
                if (!t.TryGetValue(fg, out var f) || !t.TryGetValue(bg, out var b)) continue;
                var r = Contrast(f, b);
                if (r < floor)
                    yield return $"{themeName}: {label} = {r:0.00}:1, floor {floor:0.00}";
            }

            // The modal card and its border are measured against the backdrop the
            // scrim actually produces, not against the bare pane.
            if (t.TryGetValue("--zep-bg-prompt-scrim", out var scrim)
                && t.TryGetValue("--zep-bg-pane", out var pane)
                && t.TryGetValue("--zep-bg-prompt-card", out var card)
                && t.TryGetValue("--zep-border-prompt-card", out var border))
            {
                var backdrop = Composite(scrim, pane);
                var cardVs = Contrast(card, backdrop);
                if (cardVs < FloorCardVsBackdrop)
                    yield return $"{themeName}: modal card vs backdrop = {cardVs:0.00}:1, "
                        + $"floor {FloorCardVsBackdrop:0.00} (the dialog has no edge)";
                var borderVs = Contrast(border, backdrop);
                if (borderVs < FloorBorderVsBackdrop)
                    yield return $"{themeName}: card border vs backdrop = {borderVs:0.00}:1, "
                        + $"floor {FloorBorderVsBackdrop:0.00}";
                var borderVsCard = Contrast(border, card);
                if (borderVsCard < FloorBorderVsCard)
                    yield return $"{themeName}: card border vs card fill = {borderVsCard:0.00}:1, "
                        + $"floor {FloorBorderVsCard:0.00}";
            }
        }

        private static Color Composite(Color fg, Color bg)
        {
            var a = fg.a;
            return new Color(
                fg.r * a + bg.r * (1f - a),
                fg.g * a + bg.g * (1f - a),
                fg.b * a + bg.b * (1f - a),
                1f);
        }

        private static float Contrast(Color fg, Color bg)
        {
            var f = fg.a < 1f ? Composite(fg, bg) : fg;
            return ThemeColorMath.ContrastRatio(
                new ZepColor(f.r, f.g, f.b, 1f), new ZepColor(bg.r, bg.g, bg.b, 1f));
        }

        // ---- Helpers ----

        private static bool SameRgb(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;

        private static string Describe(Color c) =>
            $"rgb({Mathf.RoundToInt(c.r * 255f)},{Mathf.RoundToInt(c.g * 255f)},{Mathf.RoundToInt(c.b * 255f)})";

        private static Dictionary<string, Color> ParseThemeTokens(string path)
        {
            var declRegex = new Regex(
                @"(?<name>--zep-[a-z0-9-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.CultureInvariant);
            var result = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (Match m in declRegex.Matches(File.ReadAllText(path)))
            {
                if (TryParseUssColor(m.Groups["value"].Value.Trim(), out var color))
                    result[m.Groups["name"].Value] = color;
            }
            return result;
        }

        private static bool TryParseUssColor(string raw, out Color color)
        {
            color = default;
            if (string.Equals(raw, "white", StringComparison.Ordinal)) { color = Color.white; return true; }
            if (string.Equals(raw, "black", StringComparison.Ordinal)) { color = Color.black; return true; }
            var m = Regex.Match(
                raw,
                @"^rgba?\(\s*(?<r>\d+)\s*,\s*(?<g>\d+)\s*,\s*(?<b>\d+)\s*(?:,\s*(?<a>[0-9.]+)\s*)?\)$",
                RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            color = new Color(
                int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture) / 255f,
                int.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture) / 255f,
                int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture) / 255f,
                m.Groups["a"].Success
                    ? float.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture)
                    : 1f);
            return true;
        }

        private static string ExtractRule(string text, string selector)
        {
            var match = Regex.Match(
                text,
                Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}",
                RegexOptions.CultureInvariant);
            Assert.That(match.Success, Is.True, $"Missing USS rule for {selector}");
            return match.Groups["body"].Value;
        }

        private static IList<ZepThemeMetadata> FileBackedThemes(ZepThemeRegistry registry)
        {
            return registry.AvailableThemes
                .Where(t => !string.IsNullOrEmpty(t.StyleSheetPath))
                .ToList();
        }

        private static void InvokeSetTheme(ZepShellView shell, string themeId)
        {
            shell.ThemeSwitcher.SetTheme(themeId, false);
        }

        // Stands in for the one thing a headless test cannot cause: an import that
        // replaces the theme's StyleSheet object while the switcher holds the old one.
        private static void SetActiveThemeSheetForTest(ZepThemeSwitcher switcher, StyleSheet sheet)
        {
            var field = typeof(ZepThemeSwitcher).GetField(
                "_activeThemeStyleSheet",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "ZepThemeSwitcher._activeThemeStyleSheet was renamed");
            field.SetValue(switcher, sheet);
        }

        private static void InvokeSetThemeDefinition(ZepShellView shell, ZepThemeDefinition definition)
        {
            shell.ThemeSwitcher.SetTheme(definition, false);
        }

        private static List<StyleSheet> CollectThemeStyleSheets(ZepShellView shell)
        {
            var result = new List<StyleSheet>();
            for (var i = 0; i < shell.styleSheets.count; i++)
            {
                var sheet = shell.styleSheets[i];
                if (sheet == null) continue;
                var path = AssetDatabase.GetAssetPath(sheet);
                if (IsThemeStyleSheet(path))
                    result.Add(sheet);
            }
            return result;
        }

        private static bool IsThemeStyleSheet(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var name = Path.GetFileNameWithoutExtension(path);
            return name.StartsWith("ZepTheme_", System.StringComparison.Ordinal)
                && !name.EndsWith("Defaults", System.StringComparison.Ordinal);
        }

        // §5.17 — the settings drawer stays attached (at width 0) for the whole
        // session, so every model raise ran its rebuilds. One coarse Changed event
        // rebuilt all four sections, which meant a theme switch tore down and rebuilt
        // the tag editor (a TextField, three Buttons and four closures per tag).
        [Test]
        public void SettingsScreen_ThemeChange_RebuildsTheThemeSectionOnly()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                MountSettingsScreen(window, model);

                ZepSettingsScreen.RebuildThemeDropdownCountForTests = 0;
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                model.SetCurrentThemeId("ThemeB");

                Assert.That(ZepSettingsScreen.RebuildThemeDropdownCountForTests, Is.EqualTo(1),
                    "a theme change must rebuild the theme dropdown exactly once");
                Assert.That(ZepSettingsScreen.RebuildTagEditorCountForTests, Is.EqualTo(0),
                    "a theme change must not touch the tag editor");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void SettingsScreen_TagChange_RebuildsTheTagEditorOnly()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                MountSettingsScreen(window, model);

                ZepSettingsScreen.RebuildThemeDropdownCountForTests = 0;
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                model.SetTags(new[]
                {
                    new ZepTag("t1", "Red", ZepTagColor.Red),
                    new ZepTag("t2", "Blue", ZepTagColor.Blue)
                });

                Assert.That(ZepSettingsScreen.RebuildTagEditorCountForTests, Is.EqualTo(1),
                    "a tag change must rebuild the tag editor exactly once");
                Assert.That(ZepSettingsScreen.RebuildThemeDropdownCountForTests, Is.EqualTo(0),
                    "a tag change must not touch the theme dropdown");
            }
            finally
            {
                window.Close();
            }
        }

        // A registry refresh describes ids and details of the same catalog; pushing
        // them separately announced the same refresh twice.
        [Test]
        public void SettingsScreen_ThemeCatalogRefresh_RebuildsTheThemeSectionOnce()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                MountSettingsScreen(window, model);

                ZepSettingsScreen.RebuildThemeDropdownCountForTests = 0;
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                model.SetThemeCatalog(
                    new[] { "ThemeA", "ThemeB", "ThemeC" },
                    new[]
                    {
                        new ZepSettingsThemeOption("ThemeA", "Theme A", ZepThemeSource.Builtin),
                        new ZepSettingsThemeOption("ThemeB", "Theme B", ZepThemeSource.Builtin),
                        new ZepSettingsThemeOption("ThemeC", "Theme C", ZepThemeSource.UserPrefs)
                    });

                Assert.That(ZepSettingsScreen.RebuildThemeDropdownCountForTests, Is.EqualTo(1),
                    "ids + details describe one catalog and must rebuild the theme section once");
                Assert.That(ZepSettingsScreen.RebuildTagEditorCountForTests, Is.EqualTo(0),
                    "a theme catalog refresh must not touch the tag editor");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void SettingsScreen_UnchangedModelPush_RebuildsNothing()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                MountSettingsScreen(window, model);

                ZepSettingsScreen.RebuildThemeDropdownCountForTests = 0;
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                model.SetCurrentThemeId("ThemeA");
                model.SetTags(new[] { new ZepTag("t1", "Red", ZepTagColor.Red) });

                Assert.That(ZepSettingsScreen.RebuildThemeDropdownCountForTests, Is.EqualTo(0),
                    "re-pushing the same theme id must not rebuild anything");
                Assert.That(ZepSettingsScreen.RebuildTagEditorCountForTests, Is.EqualTo(0),
                    "re-pushing the same tags must not rebuild anything");
            }
            finally
            {
                window.Close();
            }
        }

        // §6.11 — the swatch was the one menu left on the native GenericMenu after
        // the themed-menu migration. GenericMenu cannot carry the colour dot, which
        // is the whole content of this menu.
        [Test]
        public void SettingsScreen_TagSwatchMenu_OffersEveryPaletteColourWithItsSwatch()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                var screen = MountSettingsScreen(window, model);
                var tag = new ZepTag("t1", "Red", ZepTagColor.Red);
                var swatch = screen.Q<VisualElement>("zep-settings-tag-swatch-t1");
                Assert.That(swatch, Is.Not.Null, "the tag row must render a swatch or this test proves nothing");

                var items = screen.BuildTagColorMenuItems(tag, swatch, () => tag.Name);

                Assert.That(items.Select(item => item.Label), Is.EqualTo(new[]
                {
                    "Gray", "Green", "Purple", "Blue", "Yellow", "Red", "Orange"
                }), "every selectable palette colour, in palette order (None is the fallback slot, not a choice)");
                Assert.That(items.Select(item => item.IconTagColor), Is.EqualTo(new ZepTagColor?[]
                {
                    ZepTagColor.Gray,
                    ZepTagColor.Green,
                    ZepTagColor.Purple,
                    ZepTagColor.Blue,
                    ZepTagColor.Yellow,
                    ZepTagColor.Red,
                    ZepTagColor.Orange
                }), "the themed popup paints a swatch per item from IconTagColor");
                Assert.That(items.Where(item => item.IsChecked).Select(item => item.Label), Is.EqualTo(new[] { "Red" }),
                    "only the tag's current colour is checked");
                Assert.That(items.All(item => item.IsEnabled && !item.IsSeparator && !item.HasChildren), Is.True,
                    "the colour list is a flat set of enabled leaves");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void SettingsScreen_TagSwatchMenu_SelectingAColourRepaintsTheSwatchAndUpdatesTheTag()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var model = BuildSettingsModel();
                var screen = MountSettingsScreen(window, model);
                var tag = new ZepTag("t1", "Red", ZepTagColor.Red, isFavorite: true);
                var swatch = screen.Q<VisualElement>("zep-settings-tag-swatch-t1");
                var updates = new List<ZepTag>();
                screen.TagDefinitionUpdated += updated =>
                {
                    updates.Add(updated);
                    model.SetTags(new[] { updated });
                };

                var items = screen.BuildTagColorMenuItems(tag, swatch, () => "Renamed");

                items.First(item => item.Label == "Red").OnClick.Invoke();
                Assert.That(updates, Is.Empty, "re-picking the colour the tag already has must not raise an update");

                items.First(item => item.Label == "Blue").OnClick.Invoke();
                Assert.That(updates.Count, Is.EqualTo(1));
                Assert.That(updates[0].Id, Is.EqualTo("t1"));
                Assert.That(updates[0].Color, Is.EqualTo(ZepTagColor.Blue));
                Assert.That(updates[0].Name, Is.EqualTo("Renamed"),
                    "the pending name in the row's field is carried into the update");
                Assert.That(updates[0].IsFavorite, Is.True, "picking a colour must not clear the favourite flag");
                Assert.That(screen.Q<VisualElement>("zep-settings-tag-swatch-t1").style.backgroundColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Blue)),
                    "the swatch repaints to the committed colour");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SettingsTabs_UseFullDrawerWidth_AndPreviewTracksTheActiveTheme()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            var shell = new ZepShellView(new ZepState());
            try
            {
                window.Show();
                window.position = new Rect(100, 100, 800, 600);
                window.rootVisualElement.Add(shell);
                var screen = shell.Q<ZepSettingsScreen>();
                screen.SetOpen(true);
                var nav = screen.Q("zep-settings-nav");
                var content = screen.Q("zep-settings-content");
                var tabs = nav.Query<Button>().ToList();
                var preview = screen.Q("zep-settings-theme-preview");
                var accent = preview.Q<Label>(className: ZepUiClasses.SettingsThemePreviewAccent);
                var title = preview.Q<Label>(className: ZepUiClasses.SectionTitle);

                foreach (var themeId in new[] { "ZepLight", "ZepDark" })
                {
                    shell.ThemeSwitcher.SetTheme(themeId, false);
                    for (var frame = 0; frame < 5; frame++) yield return null;
                    var tokens = ParseThemeTokens(Path.GetFullPath(
                        "Packages/com.van-unity.zep/Editor/UI/Styles/ZepTheme_" + themeId + ".uss"));
                    Assert.That(SameRgb(preview.resolvedStyle.backgroundColor, tokens["--zep-bg-root"]), Is.True);
                    Assert.That(SameRgb(title.resolvedStyle.color, tokens["--zep-text-main"]), Is.True);
                    Assert.That(SameRgb(accent.resolvedStyle.backgroundColor, tokens["--zep-bg-item-selected-accent"]), Is.True);
                    Assert.That(accent.resolvedStyle.backgroundColor.a,
                        Is.EqualTo(tokens["--zep-bg-item-selected-accent"].a).Within(0.01f));
                    Assert.That(content.worldBound.width, Is.GreaterThan(280f));
                    Assert.That(content.worldBound.x, Is.EqualTo(nav.worldBound.x).Within(0.1f));
                    Assert.That(content.worldBound.yMin, Is.GreaterThan(nav.worldBound.yMax));
                    Assert.That(tabs.Count, Is.EqualTo(4));
                    for (var index = 0; index < tabs.Count; index++)
                    {
                        var tab = tabs[index];
                        Assert.That(tab.worldBound.y, Is.EqualTo(tabs[0].worldBound.y).Within(0.1f));
                        Assert.That(tab.worldBound.xMax, Is.LessThanOrEqualTo(nav.worldBound.xMax + 0.1f));
                        Assert.That(SameRgb(tab.resolvedStyle.color, tokens["--zep-text-main"]), Is.True);
                        var textWidth = tab.MeasureTextSize(tab.text, 0, VisualElement.MeasureMode.Undefined,
                            0, VisualElement.MeasureMode.Undefined).x;
                        Assert.That(tab.contentRect.width, Is.GreaterThanOrEqualTo(textWidth), tab.text);
                        using (var submit = new NavigationSubmitEvent { target = tab }) tab.SendEvent(submit);
                        yield return null;
                        for (var section = 0; section < content.childCount; section++)
                        {
                            Assert.That(content[section].resolvedStyle.display,
                                Is.EqualTo(section == index ? DisplayStyle.Flex : DisplayStyle.None));
                            Assert.That(tabs[section].ClassListContains(ZepUiClasses.SettingsNavButtonActive),
                                Is.EqualTo(section == index));
                        }
                    }

                    using (var submit = new NavigationSubmitEvent { target = tabs[0] }) tabs[0].SendEvent(submit);
                    yield return null;
                    var dropdown = screen.Q("zep-theme-dropdown");
                    var create = screen.Q("zep-theme-actions").Q<Button>();
                    Assert.That(create.worldBound.x, Is.EqualTo(dropdown.worldBound.x).Within(0.1f));
                    Assert.That(preview.worldBound.x, Is.EqualTo(dropdown.worldBound.x).Within(0.1f));
                    Assert.That(preview.worldBound.width, Is.EqualTo(dropdown.worldBound.width).Within(0.1f));
                }

                screen.ShowTagsSection();
                yield return null;
                Assert.That(content[1].resolvedStyle.display, Is.EqualTo(DisplayStyle.Flex),
                    "Manage Tags must still open the Finder Tags tab directly.");
            }
            finally
            {
                shell.Teardown();
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator SettingsCompressionToggles_AlignBeforeWrappedCaptions_AndToggleWithKeyboard()
        {
            var window = ScriptableObject.CreateInstance<StyleGeometryTestWindow>();
            try
            {
                var screen = MountSettingsScreen(window, BuildSettingsModel());
                window.position = new Rect(100, 100, 640, 400);
                var root = window.rootVisualElement;
                root.AddToClassList(ZepUiClasses.Root);
                root.AddToClassList("zep-theme-ZepLight");
                foreach (var sheet in new[] { "Zep", "ZepTheme_Defaults", "ZepTheme_ZepLight" })
                    root.styleSheets.Add(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                        "Packages/com.van-unity.zep/Editor/UI/Styles/" + sheet + ".uss"));
                screen.SetOpen(true);
                var compressionTab = screen.Q("zep-settings-nav").Query<Button>().ToList()[2];
                using (var submit = new NavigationSubmitEvent { target = compressionTab }) compressionTab.SendEvent(submit);
                for (var frame = 0; frame < 5; frame++) yield return null;

                var changes = new List<bool>();
                screen.IncludeMetaInArchiveChanged += changes.Add;
                screen.PromptForMetaOnCompressChanged += changes.Add;
                float? checkboxX = null;
                foreach (var toggle in screen.Query<Toggle>().ToList())
                {
                    var box = toggle.Q(className: "unity-toggle__checkmark");
                    var caption = toggle.Q<Label>(className: "unity-toggle__text");
                    var tick = box.Q<Label>(className: ZepUiClasses.SettingsToggleTick);
                    Assert.That(caption, Is.Not.Null);
                    Assert.That(box.worldBound.xMax, Is.LessThan(caption.worldBound.xMin));
                    Assert.That(box.worldBound.width, Is.EqualTo(16f).Within(0.1f));
                    Assert.That(box.worldBound.height, Is.EqualTo(16f).Within(0.1f));
                    if (checkboxX.HasValue) Assert.That(box.worldBound.x, Is.EqualTo(checkboxX.Value).Within(0.1f));
                    checkboxX = box.worldBound.x;
                    Assert.That(caption.resolvedStyle.whiteSpace, Is.EqualTo(WhiteSpace.Normal));
                    Assert.That(caption.worldBound.xMax, Is.LessThanOrEqualTo(screen.worldBound.xMax - 15f));
                    Assert.That(caption.worldBound.yMax, Is.LessThanOrEqualTo(toggle.worldBound.yMax + 0.1f));

                    toggle.Focus();
                    var before = toggle.value;
                    for (var press = 0; press < 2; press++)
                    {
                        using (var submit = new NavigationSubmitEvent { target = toggle }) toggle.SendEvent(submit);
                        yield return null;
                        yield return null;
                        Assert.That(toggle.value, Is.EqualTo(press == 0 ? !before : before));
                        Assert.That(changes.Last(), Is.EqualTo(toggle.value));
                        Assert.That(tick.resolvedStyle.visibility,
                            Is.EqualTo(toggle.value ? Visibility.Visible : Visibility.Hidden));
                        Assert.That(NameOfBackgroundImage(box), Is.EqualTo("none"));
                        Assert.That(tick.worldBound.width, Is.GreaterThan(0f));

                        // Hover/press also affect the input and checkbox descendants
                        // under the pointer; preserve Checked and Focus while probing.
                        var pseudo = typeof(VisualElement).GetProperty("pseudoStates",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        Assert.That(pseudo, Is.Not.Null);
                        var surfaces = new[] { toggle, toggle.Q(className: "unity-toggle__input"), box };
                        var originalStates = surfaces.Select(element => Convert.ToInt32(pseudo.GetValue(element))).ToArray();
                        var fill = box.resolvedStyle.backgroundColor;
                        foreach (var state in new[] { "Hover", "Active" })
                        {
                            var flag = Convert.ToInt32(Enum.Parse(pseudo.PropertyType, state));
                            for (var i = 0; i < surfaces.Length; i++)
                                pseudo.SetValue(surfaces[i], Enum.ToObject(pseudo.PropertyType, originalStates[i] | flag));
                            yield return null;
                            yield return null;
                            Assert.That(NameOfBackgroundImage(box), Is.EqualTo("none"), state);
                            Assert.That(SameRgb(box.resolvedStyle.backgroundColor, fill), Is.True, state);
                        }
                        for (var i = 0; i < surfaces.Length; i++)
                            pseudo.SetValue(surfaces[i], Enum.ToObject(pseudo.PropertyType, originalStates[i]));
                    }
                }
                Assert.That(changes.Count, Is.EqualTo(4));
            }
            finally
            {
                window.Close();
            }
        }

        private static ZepSettingsScreenModel BuildSettingsModel()
        {
            var model = new ZepSettingsScreenModel();
            model.SetThemeCatalog(
                new[] { "ThemeA", "ThemeB" },
                new[]
                {
                    new ZepSettingsThemeOption("ThemeA", "Theme A", ZepThemeSource.Builtin),
                    new ZepSettingsThemeOption("ThemeB", "Theme B", ZepThemeSource.Builtin)
                });
            model.SetCurrentThemeId("ThemeA");
            model.SetTags(new[] { new ZepTag("t1", "Red", ZepTagColor.Red) });
            return model;
        }

        // Mounting matters: the screen subscribes to the model on attach.
        private static ZepSettingsScreen MountSettingsScreen(
            EditorWindow window,
            ZepSettingsScreenModel model)
        {
            window.position = new Rect(100f, 100f, 600f, 400f);
            window.Show();
            var screen = new ZepSettingsScreen(model);
            window.rootVisualElement.Add(screen);
            Assert.That(screen.panel, Is.Not.Null, "the screen must be attached for the counters to mean anything");
            return screen;
        }

        private sealed class StyleGeometryTestWindow : EditorWindow
        {
        }
    }
}
