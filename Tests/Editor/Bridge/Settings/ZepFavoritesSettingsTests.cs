using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Indexing;
using Zep.Editor.Bridge;
using Zep.Editor.Bridge.Settings;
using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI;
using NUnit.Framework;
using UnityEditor;

namespace Zep.Editor.Tests.Bridge.Settings
{
    // The one-time carry-over from the project-shared favorites file to the
    // per-user one: the legacy asset is read as text (deserializing it would
    // construct a second ScriptableSingleton), so the parse is pinned against
    // the exact shapes Unity writes for a string list.
    public sealed class ZepFavoritesSettingsTests
    {
        private string _tempDirectory;

        [SetUp]
        public void SetUp()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "zep-favorites-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_tempDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }

        [Test]
        public void ImportLegacy_ReadsThePathsBlock_PlainAndQuoted_InFileOrder()
        {
            var legacy = WriteLegacy(
                "%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!114 &1\n"
                + "MonoBehaviour:\n"
                + "  m_ObjectHideFlags: 61\n"
                + "  m_Name: \n"
                + "  m_EditorClassIdentifier: \n"
                + "  FavoriteFolderPaths:\n"
                + "  - Assets/Zebra\n"
                + "  - 'Assets/With: Colon'\n"
                + "  - \"Packages/com.example.pkg/Runtime\"\n"
                + "  SomethingLater: 3\n");
            var into = new List<string>();

            var imported = ZepFavoritesSettings.TryImportLegacyFavorites(legacy, into);

            Assert.That(imported, Is.True);
            Assert.That(into, Is.EqualTo(new[]
            {
                "Assets/Zebra",
                "Assets/With: Colon",
                "Packages/com.example.pkg/Runtime"
            }));
        }

        [Test]
        public void ImportLegacy_EmptyList_ImportsNothing()
        {
            var legacy = WriteLegacy(
                "--- !u!114 &1\n"
                + "MonoBehaviour:\n"
                + "  FavoriteFolderPaths: []\n");
            var into = new List<string>();

            Assert.That(ZepFavoritesSettings.TryImportLegacyFavorites(legacy, into), Is.False);
            Assert.That(into, Is.Empty);
        }

        [Test]
        public void ImportLegacy_UnitySerializedPaths_PreservesQuotedAndUnicodeNames()
        {
            var settings = ZepFavoritesSettings.instance;
            var previousPaths = settings.FavoriteFolderPaths.ToArray();
            var paths = new[]
            {
                "Assets/Artist's: Work",
                "Assets/Привет",
                "Packages/com.example.pkg/Café",
                "Assets/🎨 Art"
            };
            var legacy = Path.Combine(_tempDirectory, "UnitySerializedFavorites.asset");
            try
            {
                settings.FavoriteFolderPaths.Clear();
                settings.FavoriteFolderPaths.AddRange(paths);
                UnityEditorInternal.InternalEditorUtility.SaveToSerializedFileAndForget(
                    new UnityEngine.Object[] { settings }, legacy, true);
                var serialized = File.ReadAllText(legacy);
                TestContext.WriteLine(serialized);
                var into = new List<string>();

                Assert.That(
                    ZepFavoritesSettings.TryImportLegacyFavorites(legacy, into),
                    Is.True,
                    serialized);
                Assert.That(into, Is.EqualTo(paths), serialized);
            }
            finally
            {
                settings.FavoriteFolderPaths.Clear();
                settings.FavoriteFolderPaths.AddRange(previousPaths);
            }
        }

        [Test]
        public void ImportLegacy_MissingFileOrBlock_ImportsNothing()
        {
            var into = new List<string>();

            Assert.That(
                ZepFavoritesSettings.TryImportLegacyFavorites(
                    Path.Combine(_tempDirectory, "missing.asset"),
                    into),
                Is.False);

            var unrelated = WriteLegacy("--- !u!114 &1\nMonoBehaviour:\n  m_Name: x\n");
            Assert.That(ZepFavoritesSettings.TryImportLegacyFavorites(unrelated, into), Is.False);
            Assert.That(into, Is.Empty);
        }

        [Test]
        public void SessionHost_ExternalFolderMovePersistsFavoritesWithoutAnyWindowOrShell()
        {
            // This test exercises the session owner without closing anyone's
            // real panel when run interactively.
            Assume.That(ZepWindow.HasOpenWindow, Is.False);
            Assume.That(UnityEngine.Resources.FindObjectsOfTypeAll<ZepPaneWindow>(), Is.Empty);
            const string settingsPath = "UserSettings/ZepFavoritesSettings.asset";
            var previousFile = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
            var settings = ZepFavoritesSettings.instance;
            var previousPaths = settings.FavoriteFolderPaths.ToArray();
            try
            {
                var store = settings.Store;
                using (var scope = new TempAssetScope())
                {
                    var art = scope.FolderPath + "/Art";
                    var audio = scope.FolderPath + "/Audio";
                    var sibling = scope.FolderPath + "/Art2";
                    var moved = scope.FolderPath + "/Illustration";
                    AssetDatabase.CreateFolder(scope.FolderPath, "Art");
                    AssetDatabase.CreateFolder(art, "UI");
                    AssetDatabase.CreateFolder(scope.FolderPath, "Audio");
                    AssetDatabase.CreateFolder(scope.FolderPath, "Art2");
                    var core = ZepControllerHost.Shared.Core;
                    core.HandleAssetChanges(new ProjectAssetChanges(
                        new[] { art, art + "/UI", audio, sibling }, Array.Empty<string>(),
                        Array.Empty<string>(), Array.Empty<string>()));
                    settings.FavoriteFolderPaths.Clear();
                    store.Add(audio);
                    store.Add(art + "/UI");
                    store.Add(sibling);

                    Assert.That(AssetDatabase.MoveAsset(art, moved), Is.Empty);
                    // The real postprocessor may already have delivered the
                    // callback; a repeated notification must remain harmless.
                    core.HandleAssetChanges(new ProjectAssetChanges(
                        Array.Empty<string>(), Array.Empty<string>(),
                        new[] { moved }, new[] { art }));

                    Assert.That(store.Paths, Is.EqualTo(new[] { audio, moved + "/UI", sibling }));
                    var saved = File.ReadAllText(settingsPath);
                    Assert.That(saved, Does.Contain(moved + "/UI"));
                    Assert.That(saved, Does.Not.Contain(art + "/UI"));
                    Assert.That(core.GetVisibleFolderEntry(store.Paths[1])?.Path,
                        Is.EqualTo(moved + "/UI"), "the next panel resolves the persisted favorite");
                }
            }
            finally
            {
                settings.FavoriteFolderPaths.Clear();
                settings.FavoriteFolderPaths.AddRange(previousPaths);
                if (previousFile != null) File.WriteAllBytes(settingsPath, previousFile);
                else if (File.Exists(settingsPath)) File.Delete(settingsPath);
            }
        }

        private string WriteLegacy(string yaml)
        {
            var path = Path.Combine(_tempDirectory, "ZepFavoritesSettings.asset");
            File.WriteAllText(path, yaml);
            return path;
        }
    }
}
