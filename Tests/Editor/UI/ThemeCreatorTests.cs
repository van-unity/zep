using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Zep.Core.State;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI.Theming;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    public sealed class ThemeCreatorTests
    {
        // ---- Derivation ----

        [Test]
        public void ThemeDerivation_DeterministicForSameInput()
        {
            var accent = new ZepColor(0.30f, 0.45f, 0.85f);
            var first = ThemeDerivation.Compute(accent, isLight: false);
            var second = ThemeDerivation.Compute(accent, isLight: false);
            Assert.That(first.Count, Is.EqualTo(second.Count));
            foreach (var key in first.Keys)
                AssertColorClose(second[key], first[key], $"Variable {key}");
        }

        [Test]
        public void ThemeDerivation_ProducesEverySchemaVariable()
        {
            var dict = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false);
            var schema = new HashSet<string>(ZepThemeSchema.RequiredVariables, StringComparer.Ordinal);
            var produced = new HashSet<string>(dict.Keys, StringComparer.Ordinal);
            Assert.That(schema.Except(produced), Is.Empty, "Missing schema variables");
            Assert.That(produced.Except(schema), Is.Empty, "Unexpected variables");
        }

        [Test]
        public void ThemeDerivation_AccentDrivesAccentVars_AndModeOnlyVarsAreStable()
        {
            var blue = ThemeDerivation.Compute(new ZepColor(0.30f, 0.50f, 0.85f), isLight: false);
            var orange = ThemeDerivation.Compute(new ZepColor(0.85f, 0.40f, 0.15f), isLight: false);

            foreach (var key in ThemeDerivation.AccentDerivedVariables)
                Assert.That(ColorsEqual(blue[key], orange[key]), Is.False,
                    $"Accent-derived {key} should change when accent changes");

            var modeOnly = new[]
            {
                "--zep-bg-button-danger",
                "--zep-bg-button-danger-hover",
                "--zep-text-button-danger",
                "--zep-bg-status-pulse",
                "--zep-bg-tag-favorite-active",
                "--zep-bg-button-secondary",
                "--zep-bg-button-secondary-hover",
                "--zep-bg-button-secondary-active",
                "--zep-bg-toggle-group",
                "--zep-bg-prompt-scrim",
                "--zep-tag-selected-border",
            };
            foreach (var key in modeOnly)
                Assert.That(ColorsEqual(blue[key], orange[key]), Is.True,
                    $"Mode-only var {key} should not change when only accent changes");
        }

        [Test]
        public void ThemeDerivation_BackgroundTintsWithSeedHue()
        {
            var blueBg = ThemeDerivation.Compute(new ZepColor(0.20f, 0.40f, 0.95f), isLight: false)["--zep-bg-root"];
            var redBg = ThemeDerivation.Compute(new ZepColor(0.95f, 0.20f, 0.30f), isLight: false)["--zep-bg-root"];
            Color.RGBToHSV(blueBg.ToUnityColor(), out var blueHue, out _, out _);
            Color.RGBToHSV(redBg.ToUnityColor(), out var redHue, out _, out _);
            Assert.That(Mathf.Abs(blueHue - redHue), Is.GreaterThan(0.05f),
                "Background hue should follow the seed's hue (tonal palette)");
        }

        [Test]
        public void ThemeDerivation_LightAndDarkProduceDistinctBackgrounds()
        {
            var accent = new ZepColor(0.3f, 0.5f, 0.8f);
            var dark = ThemeDerivation.Compute(accent, isLight: false);
            var light = ThemeDerivation.Compute(accent, isLight: true);
            Assert.That(ColorsEqual(dark["--zep-bg-root"], light["--zep-bg-root"]), Is.False);
            Assert.That(ColorsEqual(dark["--zep-text-main"], light["--zep-text-main"]), Is.False);
        }

        [Test]
        public void ThemeDerivation_TextOnBackground_MeetsContrastFloor()
        {
            foreach (var isLight in new[] { false, true })
            {
                var vars = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight);
                AssertContrast(vars, "--zep-text-main", "--zep-bg-root", 4.5f);
                AssertContrast(vars, "--zep-text-main", "--zep-bg-pane", 4.5f);
                AssertContrast(vars, "--zep-text-input", "--zep-bg-input", 4.5f);
            }
        }

        // ---- Default themes SO ----

        [Test]
        public void DefaultThemes_ReturnsConfiguredIdForEachMode()
        {
            var so = ScriptableObject.CreateInstance<ZepDefaultThemes>();
            try
            {
                Assert.That(so.GetDefaultThemeId(true), Is.EqualTo("ZepLight"));
                Assert.That(so.GetDefaultThemeId(false), Is.EqualTo("ZepDark"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(so);
            }
        }

        // ---- ScreenModel (MVVM) ----

        [Test]
        public void ScreenModel_LoadForNew_PullsDefaultAccentForMode()
        {
            var fake = new FakeOrchestrator();
            fake.DarkAccent = new Color(0.10f, 0.20f, 0.30f);
            fake.LightAccent = new Color(0.50f, 0.60f, 0.70f);
            var model = new ThemeCreatorScreenModel(fake);

            model.LoadForNew(isLight: true);
            AssertColorClose(model.Accent, fake.LightAccent, "light accent");
            Assert.That(model.IsLight, Is.True);

            model.LoadForNew(isLight: false);
            AssertColorClose(model.Accent, fake.DarkAccent, "dark accent");
            Assert.That(model.IsLight, Is.False);
        }

        [Test]
        public void ScreenModel_SetAccent_RecomputesVariables_AndMarksDirty()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            model.LoadForNew(isLight: false);
            var beforeBg = model.Variables["--zep-bg-button-primary"];
            var raises = 0;
            model.Changed += () => raises++;

            model.SetAccent(new Color(0.95f, 0.10f, 0.20f));

            Assert.That(model.Accent, Is.EqualTo(new Color(0.95f, 0.10f, 0.20f)));
            Assert.That(ColorsEqual(model.Variables["--zep-bg-button-primary"], beforeBg), Is.False);
            Assert.That(model.IsDirty, Is.True);
            Assert.That(raises, Is.EqualTo(1));
        }

        [Test]
        public void ScreenModel_SetIsLight_HardResetsAccentToDefault()
        {
            var fake = new FakeOrchestrator();
            fake.DarkAccent = new Color(0.10f, 0.20f, 0.30f);
            fake.LightAccent = new Color(0.80f, 0.80f, 0.80f);
            var model = new ThemeCreatorScreenModel(fake);
            model.LoadForNew(isLight: false);

            model.SetAccent(new Color(0.95f, 0.10f, 0.20f));
            Assert.That(ColorsEqual(model.Accent, new Color(0.95f, 0.10f, 0.20f)), Is.True);

            model.SetIsLight(true);

            Assert.That(model.IsLight, Is.True);
            AssertColorClose(model.Accent, fake.LightAccent, "accent reset to light default");
        }

        [Test]
        public void ScreenModel_CanSave_FalseWhenNameEmpty_TrueWhenSet()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            model.LoadForNew(isLight: false);
            Assert.That(model.CanSave, Is.False);
            model.SetThemeName("My Theme");
            Assert.That(model.CanSave, Is.True);
            model.SetThemeName("   ");
            Assert.That(model.CanSave, Is.False);
        }

        [Test]
        public void ScreenModel_Save_ReturnsErrorOnIdCollision()
        {
            var fake = new FakeOrchestrator();
            fake.ExistingIds.Add("user-existing-theme");
            var model = new ThemeCreatorScreenModel(fake);
            model.LoadForNew(isLight: false);
            model.SetThemeName("Existing Theme");

            var result = model.Save();

            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Does.Contain("already exists"));
            Assert.That(fake.SaveInvocations, Is.Empty);
        }

        [Test]
        public void ScreenModel_Save_ProjectMode_DelegatesToOrchestrator()
        {
            var fake = new FakeOrchestrator();
            var model = new ThemeCreatorScreenModel(fake);
            model.LoadForNew(isLight: false);
            model.SetThemeName("Cool Theme");
            model.SetSaveDestination(ThemeSaveMode.ProjectAsset);

            var result = model.Save();

            Assert.That(result.Success, Is.True);
            Assert.That(fake.SaveInvocations.Count, Is.EqualTo(1));
            Assert.That(fake.SaveInvocations[0].mode, Is.EqualTo(ThemeSaveMode.ProjectAsset));
            Assert.That(model.IsDirty, Is.False);
            Assert.That(model.Mode, Is.EqualTo(ThemeCreatorMode.Edit));
        }

        [Test]
        public void ScreenModel_LoadForEdit_HidesSaveDestinationToggle()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            var def = new ZepThemeDefinition(
                "user-existing", "User Existing", isLight: false,
                ZepThemeSource.ProjectAsset,
                ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false));
            model.LoadForEdit(def);
            Assert.That(model.Mode, Is.EqualTo(ThemeCreatorMode.Edit));
            Assert.That(model.ShowSaveDestinationToggle, Is.False);
            Assert.That(model.ThemeName, Is.EqualTo("User Existing"));
            Assert.That(model.SaveDestination, Is.EqualTo(ThemeSaveMode.ProjectAsset));
        }

        [TestCase(false, true)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(true, false)]
        public void CreatorSave_AppliesAndPersistsTheSavedThemeOnlyOnSuccess(bool saveToProject, bool succeeds)
        {
            var destination = saveToProject ? ThemeSaveMode.ProjectAsset : ThemeSaveMode.UserPrefs;
            var hadId = EditorPrefs.HasKey(ZepThemePreferences.ThemeStringPreferenceKey);
            var previousId = EditorPrefs.GetString(ZepThemePreferences.ThemeStringPreferenceKey, string.Empty);
            var hadLegacy = EditorPrefs.HasKey(ZepThemePreferences.ThemeLegacyPreferenceKey);
            var previousLegacy = EditorPrefs.GetBool(ZepThemePreferences.ThemeLegacyPreferenceKey);
            ZepShellView shell = null;
            try
            {
                EditorPrefs.SetString(ZepThemePreferences.ThemeStringPreferenceKey, "ZepDark");
                EditorPrefs.SetBool(ZepThemePreferences.ThemeLegacyPreferenceKey, false);
                var registry = new ZepThemeRegistry();
                var fake = new FakeOrchestrator { Registry = registry, SaveError = succeeds ? null : "Storage unavailable." };
                registry.RegisterProvider(fake);
                registry.RefreshRegistry();
                shell = new ZepShellView(new ZepState(), null, null,
                    themeSaveOrchestrator: fake, registry: registry);
                var overlay = (ZepOverlayPageController)typeof(ZepShellView)
                    .GetField("_overlayController", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
                typeof(ZepOverlayPageController).GetMethod("OnCreateThemeRequested",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(overlay, null);
                var model = (ThemeCreatorScreenModel)typeof(ZepOverlayPageController)
                    .GetField("_themeCreatorModel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(overlay);
                model.SetThemeName("Saved Palette");
                model.SetIsLight(true);
                model.SetSaveDestination(destination);
                var applied = new List<string>();
                shell.ThemeApplied += applied.Add;
                var screen = shell.Q<ThemeCreatorScreen>();

                // Run the existing save-button callback so this also checks the
                // saved identity survives the screen-to-overlay notification.
                typeof(ThemeCreatorScreen).GetMethod("OnSaveClicked",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(screen, null);

                var expectedId = succeeds ? "user-saved-palette" : "ZepDark";
                Assert.That(shell.ThemeSwitcher.CurrentTheme, Is.EqualTo(expectedId));
                Assert.That(ZepThemePreferences.GetTheme(), Is.EqualTo(expectedId));
                Assert.That(EditorPrefs.GetBool(ZepThemePreferences.ThemeLegacyPreferenceKey), Is.EqualTo(succeeds));
                Assert.That(shell.GetClasses().Where(name => name.StartsWith("zep-theme-", StringComparison.Ordinal)),
                    Is.EqualTo(new[] { ZepThemeRegistry.GetThemeClassName(expectedId) }));
                Assert.That(shell.styleSheets.count, Is.EqualTo(3));
                Assert.That(applied, Is.EqualTo(succeeds ? new[] { expectedId } : Array.Empty<string>()),
                    "Only a successful save announces the applied identity to mirrors.");
                Assert.That(overlay.SettingsScreenModel.CurrentThemeId, Is.EqualTo(expectedId));
                Assert.That(overlay.IsThemeCreatorPageActive, Is.EqualTo(!succeeds));
                if (succeeds)
                {
                    Assert.That(registry.AvailableThemes.Any(theme => theme.Id == expectedId), Is.True);
                    Assert.That(registry.AvailableDefinitions.Single(theme => theme.Id == expectedId).Source,
                        Is.EqualTo(destination == ThemeSaveMode.ProjectAsset
                            ? ZepThemeSource.ProjectAsset : ZepThemeSource.UserPrefs));
                }
            }
            finally
            {
                shell?.Teardown();
                if (hadId) EditorPrefs.SetString(ZepThemePreferences.ThemeStringPreferenceKey, previousId);
                else EditorPrefs.DeleteKey(ZepThemePreferences.ThemeStringPreferenceKey);
                if (hadLegacy) EditorPrefs.SetBool(ZepThemePreferences.ThemeLegacyPreferenceKey, previousLegacy);
                else EditorPrefs.DeleteKey(ZepThemePreferences.ThemeLegacyPreferenceKey);
            }
        }

        // ---- Save round-trips ----

        [TestCase("{ invalid theme json")]
        [TestCase("{}")]
        [TestCase("{\"themes\":null}")]
        [TestCase("{\"metadata\":{\"themes\":[]}}")]
        [TestCase("{\"note\":\"\\\"themes\\\":[]\"}")]
        [TestCase("{\"themes\":null,\"metadata\":{\"themes\":[]}}")]
        [TestCase("{\"themes\":\"[]\"}")]
        [TestCase("{\"themes\":[null]}")]
        [TestCase("{\"themes\":[{}]}")]
        [TestCase("{\"themes\":[{\"id\":\"   \"}]}")]
        [TestCase("{\"themes\":[{\"id\":\"user-valid-first\"},{\"displayName\":\"Missing identity\"}]}")]
        [TestCase("{\"themes\":[{\"id\":\"user-valid-first\"},{\"id\":\"user-valid-first\"}]}")]
        public void LocalThemeCorruption_RefusesSaveAndDeleteWithoutReplacingStoredBytes(string json)
        {
            var hadPreference = EditorPrefs.HasKey(UserPrefsThemeProvider.EditorPrefsKey);
            var originalPreference = EditorPrefs.GetString(UserPrefsThemeProvider.EditorPrefsKey, string.Empty);
            try
            {
                EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, json);
                Assert.Throws<InvalidDataException>(() => new UserPrefsThemeProvider().GetThemes().ToList());
                var registry = new ZepThemeRegistry();
                registry.RegisterProvider(new UserPrefsThemeProvider());
                registry.RegisterProvider(new BuiltinThemeProvider());
                var orchestrator = new ThemeSaveOrchestrator(registry);
                LogAssert.Expect(LogType.Error,
                    new Regex(@"^\[Zep\] Theme save failed: System\.IO\.InvalidDataException:"));
                LogAssert.Expect(LogType.Warning,
                    new Regex(@"^\[Zep\] Theme provider UserPrefsThemeProvider threw:"));

                var result = orchestrator.Save("user-new", "New", false,
                    new Dictionary<string, ZepColor>(), ThemeSaveMode.UserPrefs);

                Assert.That(result.Success, Is.False);
                Assert.That(result.Error, Does.Contain("Local theme storage"));
                Assert.That(EditorPrefs.GetString(UserPrefsThemeProvider.EditorPrefsKey), Is.EqualTo(json));
                Assert.That(registry.AvailableDefinitions.Any(definition => definition.Id == "ZepDark"), Is.True,
                    "Corrupt local storage must not prevent discovery from the later builtin provider.");
                LogAssert.Expect(LogType.Warning,
                    new Regex(@"^\[Zep\] Theme delete failed: Local theme storage"));
                LogAssert.Expect(LogType.Warning,
                    new Regex(@"^\[Zep\] Theme provider UserPrefsThemeProvider threw:"));

                Assert.That(orchestrator.DeleteUserTheme("user-valid-first", ZepThemeSource.UserPrefs), Is.False);
                Assert.That(EditorPrefs.GetString(UserPrefsThemeProvider.EditorPrefsKey), Is.EqualTo(json));
            }
            finally
            {
                if (hadPreference) EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, originalPreference);
                else EditorPrefs.DeleteKey(UserPrefsThemeProvider.EditorPrefsKey);
            }
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("  ")]
        [TestCase("{\"themes\":[]}")]
        [TestCase("{\"them\\u0065s\":[]}")]
        [TestCase("{\"metadata\":{\"themes\":null},\"themes\":[]}")]
        [TestCase("{\"note\":\"\\\"themes\\\":null\",\"themes\":[]}")]
        public void LocalThemeEmptyStorage_SupportsSaveEditAndDelete(string json)
        {
            var hadPreference = EditorPrefs.HasKey(UserPrefsThemeProvider.EditorPrefsKey);
            var originalPreference = EditorPrefs.GetString(UserPrefsThemeProvider.EditorPrefsKey, string.Empty);
            try
            {
                if (json == null) EditorPrefs.DeleteKey(UserPrefsThemeProvider.EditorPrefsKey);
                else EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, json);
                var registry = new ZepThemeRegistry();
                registry.RegisterProvider(new UserPrefsThemeProvider());
                var orchestrator = new ThemeSaveOrchestrator(registry);
                var first = new Dictionary<string, ZepColor> { ["--zep-bg-root"] = new ZepColor(.1f, .2f, .3f) };
                var changed = new Dictionary<string, ZepColor> { ["--zep-bg-root"] = new ZepColor(.4f, .5f, .6f) };

                Assert.That(orchestrator.Save("user-roundtrip", "First", false, first, ThemeSaveMode.UserPrefs).Success, Is.True);
                Assert.That(orchestrator.Save("user-roundtrip", "Edited", true, changed, ThemeSaveMode.UserPrefs).Success, Is.True);
                var reloaded = new UserPrefsThemeProvider().GetThemes().Single();
                Assert.That(reloaded.Id, Is.EqualTo("user-roundtrip"));
                Assert.That(reloaded.DisplayName, Is.EqualTo("Edited"));
                Assert.That(reloaded.IsLight, Is.True);
                AssertColorClose(reloaded.Variables["--zep-bg-root"], changed["--zep-bg-root"], "edited root");
                Assert.That(orchestrator.Save("User-Roundtrip", "Distinct case", false, first, ThemeSaveMode.UserPrefs).Success, Is.True);
                Assert.That(new UserPrefsThemeProvider().GetThemes().Count(), Is.EqualTo(2),
                    "Theme IDs use ordinal, case-sensitive identity.");
                Assert.That(orchestrator.DeleteUserTheme(reloaded.Id, ZepThemeSource.UserPrefs), Is.True);
                Assert.That(new UserPrefsThemeProvider().GetThemes().Single().Id, Is.EqualTo("User-Roundtrip"));
                Assert.That(orchestrator.DeleteUserTheme("User-Roundtrip", ZepThemeSource.UserPrefs), Is.True);
                Assert.That(new UserPrefsThemeProvider().GetThemes(), Is.Empty);
            }
            finally
            {
                if (hadPreference) EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, originalPreference);
                else EditorPrefs.DeleteKey(UserPrefsThemeProvider.EditorPrefsKey);
            }
        }

        [TestCase("{\"themes\":[{\"id\":\"legacy-theme\"}]}", 0)]
        [TestCase("{\"themes\":[{\"id\":\"legacy-theme\",\"variables\":null}]}", 0)]
        [TestCase("{\"themes\":[{\"id\":\"legacy-theme\",\"variables\":[null,{}, {\"name\":\"--zep-bg-root\",\"value\":{\"r\":0.1,\"g\":0.2,\"b\":0.3,\"a\":1}}]}]}", 1)]
        public void LocalThemeLegacyPartialOverrides_RemainReadableAndSurviveOtherThemeSaves(string json, int expectedVariableCount)
        {
            var hadPreference = EditorPrefs.HasKey(UserPrefsThemeProvider.EditorPrefsKey);
            var originalPreference = EditorPrefs.GetString(UserPrefsThemeProvider.EditorPrefsKey, string.Empty);
            try
            {
                EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, json);
                var legacy = new UserPrefsThemeProvider().GetThemes().Single();
                Assert.That(legacy.DisplayName, Is.EqualTo("legacy-theme"));
                Assert.That(legacy.Variables.Count, Is.EqualTo(expectedVariableCount));
                if (legacy.Variables.Count == 1)
                    AssertColorClose(legacy.Variables["--zep-bg-root"], new ZepColor(.1f, .2f, .3f), "legacy root");
                var registry = new ZepThemeRegistry();
                registry.RegisterProvider(new UserPrefsThemeProvider());
                var orchestrator = new ThemeSaveOrchestrator(registry);

                Assert.That(orchestrator.Save("user-other", "Other", false,
                    new Dictionary<string, ZepColor>(), ThemeSaveMode.UserPrefs).Success, Is.True);

                var reloaded = new UserPrefsThemeProvider().GetThemes().Single(theme => theme.Id == legacy.Id);
                Assert.That(reloaded.Variables.Keys, Is.EquivalentTo(legacy.Variables.Keys));
                foreach (var variable in legacy.Variables)
                    AssertColorClose(reloaded.Variables[variable.Key], variable.Value, variable.Key);
            }
            finally
            {
                if (hadPreference) EditorPrefs.SetString(UserPrefsThemeProvider.EditorPrefsKey, originalPreference);
                else EditorPrefs.DeleteKey(UserPrefsThemeProvider.EditorPrefsKey);
            }
        }

        [Test]
        public void UserTheme_SaveToProject_RoundTrip()
        {
            using var scope = new TempAssetScope();
            var registry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new Zep.Editor.Bridge.Theming.ThemeSaveOrchestrator(registry);
            var vars = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false);
            var id = "test-roundtrip-project";
            try
            {
                var result = orchestrator.Save(id, "RT Project", false, vars, ThemeSaveMode.ProjectAsset);
                Assert.That(result.Success, Is.True, result.Error);
                var def = registry.AvailableDefinitions.FirstOrDefault(d => d.Id == id);
                Assert.That(def, Is.Not.Null);
                Assert.That(def.Source, Is.EqualTo(ZepThemeSource.ProjectAsset));
                foreach (var kv in vars)
                    AssertColorClose(def.Variables[kv.Key], kv.Value, kv.Key);
            }
            finally
            {
                orchestrator.DeleteUserTheme(id, ZepThemeSource.ProjectAsset);
            }
        }

        [Test]
        public void UserTheme_SaveToLocal_RoundTrip()
        {
            var registry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new Zep.Editor.Bridge.Theming.ThemeSaveOrchestrator(registry);
            var vars = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false);
            var id = "test-roundtrip-local";
            try
            {
                var result = orchestrator.Save(id, "RT Local", false, vars, ThemeSaveMode.UserPrefs);
                Assert.That(result.Success, Is.True, result.Error);
                var def = registry.AvailableDefinitions.FirstOrDefault(d => d.Id == id);
                Assert.That(def, Is.Not.Null);
                Assert.That(def.Source, Is.EqualTo(ZepThemeSource.UserPrefs));
                foreach (var kv in vars)
                    AssertColorClose(def.Variables[kv.Key], kv.Value, kv.Key);
            }
            finally
            {
                orchestrator.DeleteUserTheme(id, ZepThemeSource.UserPrefs);
            }
        }

        [Test]
        public void DeletingActiveUserTheme_FallsBackToDefault()
        {
            using var scope = new TempAssetScope();
            var registry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new Zep.Editor.Bridge.Theming.ThemeSaveOrchestrator(registry);
            var vars = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false);
            var id = "test-fallback-on-delete";

            var saveResult = orchestrator.Save(id, "FallbackTest", false, vars, ThemeSaveMode.ProjectAsset);
            Assert.That(saveResult.Success, Is.True);

            var shell = new ZepShellView(
                new ZepState(),
                commandHandler: null,
                assetSelectionAction: null,
                registry: registry);
            InvokeShellSetTheme(shell, id);
            Assert.That(GetActiveThemeClass(shell), Is.EqualTo(ZepThemeRegistry.GetThemeClassName(id)));

            orchestrator.DeleteUserTheme(id, ZepThemeSource.ProjectAsset);

            Assert.That(GetActiveThemeClass(shell),
                Is.EqualTo(ZepThemeRegistry.GetThemeClassName(ZepThemeRegistry.DefaultThemeId)),
                "Shell should fall back to the default theme class after the active user theme is deleted.");
        }

        [Test]
        public void SaveSuccess_InvalidatesBothRegistrySurfaces()
        {
            using var scope = new TempAssetScope();
            var registry = ZepThemeProviderRegistration.CreateConfigured();
            var orchestrator = new Zep.Editor.Bridge.Theming.ThemeSaveOrchestrator(registry);
            var vars = ThemeDerivation.Compute(new ZepColor(0.3f, 0.5f, 0.8f), isLight: false);
            var id = "test-registry-invalidation";
            try
            {
                var result = orchestrator.Save(id, "Reg Inv", false, vars, ThemeSaveMode.ProjectAsset);
                Assert.That(result.Success, Is.True);
                Assert.That(registry.AvailableDefinitions.Any(d => d.Id == id), Is.True);
                Assert.That(registry.AvailableThemes.Any(t => t.Id == id), Is.True);
            }
            finally
            {
                orchestrator.DeleteUserTheme(id, ZepThemeSource.ProjectAsset);
            }
        }

        // ---- Runtime path probe ----

        [Test]
        public void RuntimeThemeStyleSheetBuilder_BuildsOnPinnedUnityVersion()
        {
            var vars = ThemeDerivation.Compute(new ZepColor(0.5f, 0.5f, 0.5f), isLight: false);
            var sheet = RuntimeThemeStyleSheetBuilder.Build("preview", vars.ToUnityColorDict());
            try { Assert.That(sheet, Is.Not.Null); }
            finally { if (sheet != null) UnityEngine.Object.DestroyImmediate(sheet); }
        }

        // ---- Preview rebuild coalescing (§4.10) ----

        // A seed-slider drag reports a ChangeEvent per pointer move; each one
        // used to destroy and re-import the preview's runtime stylesheet.
        [UnityTest]
        public IEnumerator PreviewCard_TenSliderTicksInOneFrame_RebuildTheSheetOnce()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            var screen = new ThemeCreatorScreen(model);
            var window = ScriptableObject.CreateInstance<ThemeCreatorTestWindow>();
            window.position = new Rect(100f, 100f, 900f, 700f);
            window.Show();

            try
            {
                window.rootVisualElement.Add(screen);
                yield return null;

                // Let the mount-time build land before measuring the drag.
                var mountDeadline = EditorApplication.timeSinceStartup + 5d;
                while (RuntimeThemeStyleSheetBuilder.BuildCountForTests == 0
                    && EditorApplication.timeSinceStartup < mountDeadline)
                {
                    window.Repaint();
                    yield return null;
                }

                var sliders = screen.Query<SliderInt>().ToList();
                Assert.That(sliders.Count, Is.EqualTo(3), "the creator has one slider per channel");

                RuntimeThemeStyleSheetBuilder.BuildCountForTests = 0;
                for (var i = 0; i < 10; i++)
                {
                    sliders[0].value = 100 + i;
                }

                Assert.That(
                    RuntimeThemeStyleSheetBuilder.BuildCountForTests,
                    Is.Zero,
                    "a slider drag must not rebuild the preview sheet per tick");

                // Scheduled items tick on repaint, which batchmode does not do on
                // its own, so drive repaints until the coalesced rebuild lands.
                var deadline = EditorApplication.timeSinceStartup + 5d;
                while (RuntimeThemeStyleSheetBuilder.BuildCountForTests == 0
                    && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                yield return null;
                yield return null;

                Assert.That(
                    RuntimeThemeStyleSheetBuilder.BuildCountForTests,
                    Is.EqualTo(1),
                    "the burst must rebuild the preview sheet exactly once");
            }
            finally
            {
                window.Close();
            }
        }

        [UnityTest]
        public IEnumerator MountingTheCreator_BuildsThePreviewSheetOnce()
        {
            var window = ScriptableObject.CreateInstance<ThemeCreatorTestWindow>();
            window.position = new Rect(100f, 100f, 900f, 700f);
            window.Show();

            try
            {
                RuntimeThemeStyleSheetBuilder.BuildCountForTests = 0;
                var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
                var screen = new ThemeCreatorScreen(model);
                window.rootVisualElement.Add(screen);

                var deadline = EditorApplication.timeSinceStartup + 5d;
                while (RuntimeThemeStyleSheetBuilder.BuildCountForTests == 0
                    && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                yield return null;
                yield return null;

                Assert.That(
                    RuntimeThemeStyleSheetBuilder.BuildCountForTests,
                    Is.EqualTo(1),
                    "construction plus attach must build the preview sheet once");
            }
            finally
            {
                window.Close();
            }
        }

        // The card disposes its sheet on detach; a rebuild scheduled just before
        // that must not run afterwards and resurrect it.
        [UnityTest]
        public IEnumerator PendingPreviewRebuild_NoOpsAfterDetach()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            var screen = new ThemeCreatorScreen(model);
            var window = ScriptableObject.CreateInstance<ThemeCreatorTestWindow>();
            window.position = new Rect(100f, 100f, 900f, 700f);
            window.Show();

            try
            {
                window.rootVisualElement.Add(screen);
                yield return null;

                var mountDeadline = EditorApplication.timeSinceStartup + 5d;
                while (RuntimeThemeStyleSheetBuilder.BuildCountForTests == 0
                    && EditorApplication.timeSinceStartup < mountDeadline)
                {
                    window.Repaint();
                    yield return null;
                }

                RuntimeThemeStyleSheetBuilder.BuildCountForTests = 0;
                model.SetAccent(new Color(0.91f, 0.13f, 0.24f));
                window.rootVisualElement.Remove(screen);

                for (var i = 0; i < 5; i++)
                {
                    window.Repaint();
                    yield return null;
                }

                Assert.That(
                    RuntimeThemeStyleSheetBuilder.BuildCountForTests,
                    Is.Zero,
                    "a rebuild pending at detach must be dropped");
            }
            finally
            {
                window.Close();
            }
        }

        // ---- Modal fade guard (§6.10c) ----

        // Show and Hide both finish on a scheduled item, 16 ms and 140 ms out.
        // A Hide that lands inside the fade-in must win: without the version
        // guard the show callback still runs and repaints the scrim opaque over
        // a modal the controller has already dismissed.
        [UnityTest]
        public IEnumerator HideRightAfterShow_LeavesTheModalHidden()
        {
            var model = new ThemeCreatorScreenModel(new FakeOrchestrator());
            var screen = new ThemeCreatorScreen(model);
            var window = ScriptableObject.CreateInstance<ThemeCreatorTestWindow>();
            window.position = new Rect(100f, 100f, 900f, 700f);
            window.Show();

            try
            {
                window.rootVisualElement.Add(screen);
                yield return null;

                screen.Show();
                screen.Hide();

                // Scheduled items tick on repaint, which batchmode does not do on
                // its own; drive repaints until the 140 ms hide callback lands.
                var deadline = EditorApplication.timeSinceStartup + 5d;
                while (screen.style.display.value != DisplayStyle.None
                    && EditorApplication.timeSinceStartup < deadline)
                {
                    window.Repaint();
                    yield return null;
                }

                Assert.That(screen.style.display.value, Is.EqualTo(DisplayStyle.None),
                    "the hide callback must take the scrim out of layout");
                Assert.That(screen.style.opacity.value, Is.EqualTo(0f),
                    "the superseded show callback must not repaint the scrim opaque");
            }
            finally
            {
                window.Close();
            }
        }

        // ---- Helpers ----

        private sealed class ThemeCreatorTestWindow : EditorWindow
        {
        }

        private static void InvokeShellSetTheme(ZepShellView shell, string themeId)
        {
            shell.ThemeSwitcher.SetTheme(themeId, false);
        }

        private static string GetActiveThemeClass(ZepShellView shell)
        {
            foreach (var cls in shell.GetClasses())
            {
                if (cls.StartsWith("zep-theme-", StringComparison.Ordinal))
                    return cls;
            }
            return null;
        }

        private sealed class FakeOrchestrator : IThemeSaveOrchestrator, IZepThemeProvider
        {
            private readonly List<ZepThemeDefinition> _definitions = new();
            public ZepThemeRegistry Registry;
            public string SaveError;
            public Color DarkAccent = new Color(0.30f, 0.42f, 0.86f);
            public Color LightAccent = new Color(0.17f, 0.38f, 0.88f);
            public List<string> ExistingIds = new List<string>();
            public List<(string id, string display, bool isLight, ThemeSaveMode mode)> SaveInvocations = new();

            public ThemeSaveResult Save(string themeId, string displayName, bool isLight,
                IReadOnlyDictionary<string, ZepColor> variables, ThemeSaveMode mode)
            {
                SaveInvocations.Add((themeId, displayName, isLight, mode));
                if (SaveError != null) return ThemeSaveResult.Fail(SaveError);
                ExistingIds.Add(themeId);
                _definitions.Add(new ZepThemeDefinition(themeId, displayName, isLight,
                    mode == ThemeSaveMode.ProjectAsset ? ZepThemeSource.ProjectAsset : ZepThemeSource.UserPrefs,
                    variables));
                Registry?.RefreshRegistry();
                Registry?.NotifyThemesChanged();
                return ThemeSaveResult.Ok(themeId);
            }

            public IEnumerable<ZepThemeDefinition> GetThemes() => _definitions;

            public bool DeleteUserTheme(string themeId, ZepThemeSource source)
            {
                return ExistingIds.Remove(themeId);
            }

            public bool ThemeIdExists(string themeId)
            {
                return ExistingIds.Contains(themeId);
            }

            public ThemeDefaultAccent GetDefaultAccentForMode(bool isLight)
            {
                return new ThemeDefaultAccent((isLight ? LightAccent : DarkAccent).ToZepColor(), null);
            }
        }

        private static void AssertContrast(IDictionary<string, ZepColor> vars, string fg, string bg, float min)
        {
            var ratio = ThemeColorMath.ContrastRatio(vars[fg], vars[bg]);
            Assert.That(ratio, Is.GreaterThanOrEqualTo(min - 0.01f),
                $"Contrast {fg} vs {bg} = {ratio:F2}, expected >= {min:F2}");
        }

        private static bool ColorsEqual(Color a, Color b)
        {
            return ColorsEqual(a.ToZepColor(), b.ToZepColor());
        }

        private static bool ColorsEqual(ZepColor a, ZepColor b)
        {
            const float eps = 0.0005f;
            return Mathf.Abs(a.R - b.R) < eps
                && Mathf.Abs(a.G - b.G) < eps
                && Mathf.Abs(a.B - b.B) < eps
                && Mathf.Abs(a.A - b.A) < eps;
        }

        private static void AssertColorClose(Color actual, Color expected, string label)
        {
            AssertColorClose(actual.ToZepColor(), expected.ToZepColor(), label);
        }

        private static void AssertColorClose(ZepColor actual, ZepColor expected, string label)
        {
            const float eps = 0.005f;
            Assert.That(Mathf.Abs(actual.R - expected.R), Is.LessThan(eps), $"{label} R");
            Assert.That(Mathf.Abs(actual.G - expected.G), Is.LessThan(eps), $"{label} G");
            Assert.That(Mathf.Abs(actual.B - expected.B), Is.LessThan(eps), $"{label} B");
            Assert.That(Mathf.Abs(actual.A - expected.A), Is.LessThan(eps), $"{label} A");
        }
    }
}
