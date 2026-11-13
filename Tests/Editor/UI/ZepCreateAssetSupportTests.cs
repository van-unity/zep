using System;
using Zep.Core.FileOperations;
using System.IO;
using System.Reflection;
using Zep.Core.Application;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.UI.Tests
{
    public sealed class ZepCreateAssetSupportTests
    {
        private string _folderPath;

        [SetUp]
        public void SetUp()
        {
            _folderPath = "Assets/ZepCreateRecipeTests-"
                + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folderPath.Substring("Assets/".Length));
        }

        [TearDown]
        public void TearDown()
        {
            ZepPerfLog.IsEnabled = false;
            ZepPerfLog.Reset();
            AssetDatabase.DeleteAsset(_folderPath);
        }

        // The user's create commit landed as menuPath=Assets/Create/MonoBehaviour
        // Script with hasRecipe=False: Unity 6 lists that item at the top of the
        // Create menu as well as under Scripting, and only the Scripting path was
        // a recipe key, so the panel's own create ran Unity's menu item instead
        // (default name, rename after import). Both paths must name the recipe.
        [Test]
        public void TopLevelMonoBehaviourScriptItem_HasTheScriptRecipe()
        {
            Assert.That(
                Unsupported.GetSubmenus("Assets/Create"),
                Does.Contain("Assets/Create/MonoBehaviour Script"),
                "this editor offers the top-level item the alias exists for");
            Assert.That(
                ZepCreateAssetSupport.TryGetDefaultFileName(
                    "Assets/Create/MonoBehaviour Script",
                    out var defaultFileName),
                Is.True,
                "the top-level item must resolve to the script recipe");
            Assert.That(defaultFileName, Is.EqualTo("NewMonoBehaviourScript.cs"));
            Assert.That(
                ZepCreateAssetSupport.TryGetDefaultFileName(
                    "Assets/Create/Scripting/MonoBehaviour Script",
                    out var scriptingDefaultFileName),
                Is.True);
            Assert.That(scriptingDefaultFileName, Is.EqualTo(defaultFileName));
        }

        // Every editor session starts on an untitled scene, and Unity refuses
        // NewScene(Additive) while one is open - the route this recipe used to
        // take - so New Scene failed until the user first saved a scene.
        [Test]
        public void SceneRecipe_WithAnUntitledSceneOpen_CreatesTheAssetAndLeavesOpenScenesAlone()
        {
            var addedUntitledScene = default(UnityEngine.SceneManagement.Scene);
            if (!AnyOpenSceneIsUntitled())
            {
                addedUntitledScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Additive);
            }

            try
            {
                Assert.That(AnyOpenSceneIsUntitled(), Is.True, "the fixture needs an untitled open scene");
                var openScenesBefore = UnityEngine.SceneManagement.SceneManager.sceneCount;

                var created = ZepCreateAssetSupport.TryCreateNamedAsset(
                    "Assets/Create/Scene",
                    _folderPath,
                    "CreatedScene",
                    out var createdPath,
                    out var errorMessage);

                Assert.That(created, Is.True, errorMessage);
                Assert.That(createdPath, Is.EqualTo(_folderPath + "/CreatedScene.unity"));
                Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(createdPath), Is.Not.Null);
                Assert.That(
                    UnityEngine.SceneManagement.SceneManager.sceneCount,
                    Is.EqualTo(openScenesBefore),
                    "creating a scene asset must not open or close a scene");
                Assert.That(
                    UnityEngine.SceneManagement.SceneManager.GetSceneByPath(createdPath).IsValid(),
                    Is.False,
                    "the new scene must not be left open");
            }
            finally
            {
                if (addedUntitledScene.IsValid())
                {
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(addedUntitledScene, true);
                }
            }
        }

        private static bool AnyOpenSceneIsUntitled()
        {
            for (var i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                if (string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).path))
                {
                    return true;
                }
            }

            return false;
        }

        [TestCase("Assets/Create/Material", "Material")]
        [TestCase("Assets/Create/Physics Material 2D", "Physics2D")]
        [TestCase("Assets/Create/Physics Material", "Physics3D")]
        public void NativeRecipe_UsesOneCreateAssetWithoutSaveOrImport(
            string commandPath,
            string requestedName)
        {
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();

            var created = ZepCreateAssetSupport.TryCreateNamedAsset(
                commandPath,
                _folderPath,
                requestedName,
                out var createdPath,
                out var errorMessage);
            var snapshot = ZepPerfLog.CaptureSnapshot();

            Assert.That(created, Is.True, errorMessage);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(createdPath), Is.Not.Null);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseCreateAssetCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseSaveAssetsCalls), Is.Zero);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseImportAssetCalls), Is.Zero);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseGlobalRefreshCalls), Is.Zero);
        }

        [Test]
        public void TextRecipe_UsesOneTargetedImportWithoutGlobalRefresh()
        {
            ZepPerfLog.IsEnabled = true;
            ZepPerfLog.Reset();

            var created = ZepCreateAssetSupport.TryCreateNamedAsset(
                "Assets/Create/Shader/Unlit Shader",
                _folderPath,
                "UnlitProbe",
                out var createdPath,
                out var errorMessage);
            var snapshot = ZepPerfLog.CaptureSnapshot();

            Assert.That(created, Is.True, errorMessage);
            Assert.That(AssetDatabase.LoadMainAssetAtPath(createdPath), Is.Not.Null);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseImportAssetCalls), Is.EqualTo(1));
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.AssetDatabaseGlobalRefreshCalls), Is.Zero);
            Assert.That(snapshot.GetCount(
                ZepPerfCounter.FullSynchronizerRuns), Is.Zero);
        }

        [Test]
        public void NamedRecipeDestination_InALocalPackage_UsesTheRegisteredPhysicalRoot()
        {
            var physicalRoot = Path.Combine(Path.GetTempPath(),
                "ZepCreatePackageTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(physicalRoot);
            var originalRoots = RegisteredPackageRoots.Current;
            try
            {
                const string packagePath = "Packages/com.example.create-local";
                RegisteredPackageRoots.SetForTests(new RegisteredPackageRootSnapshot(
                    new[]
                    {
                        new RegisteredPackageRoot(packagePath, physicalRoot, "Local", isWritable: true),
                    },
                    isComplete: true));
                const string assetPath = packagePath + "/Generated/Probe.shader";
                Assert.That(UnityProjectAssetGateway.CanWriteAssetPath(assetPath), Is.True);

                // Both text and code recipes use this filesystem boundary. A fake
                // package mapping cannot be imported by Unity, so assert the write
                // destination without creating an asset under an unregistered path.
                var resolveDestination = typeof(ZepCreateAssetSupport).GetMethod(
                    "GetAbsoluteProjectPath", BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(resolveDestination, Is.Not.Null);
                var actual = (string)resolveDestination.Invoke(null, new object[] { assetPath });

                Assert.That(Path.GetFullPath(actual),
                    Is.EqualTo(Path.Combine(physicalRoot, "Generated", "Probe.shader")));
                Assert.That(Path.GetFullPath(actual),
                    Is.Not.EqualTo(GetAbsoluteProjectPath(assetPath)),
                    "a Local package must not write a shadow directory below the project root");
            }
            finally
            {
                RegisteredPackageRoots.SetForTests(originalRoots);
                Directory.Delete(physicalRoot, recursive: true);
            }
        }

        [Test]
        public void ScriptIdentifierReplacement_RenamesOnlyTheGeneratedType()
        {
            const string source =
                "public class NewMonoBehaviourScript : MonoBehaviour\n"
                + "{\n"
                + "    private NewMonoBehaviourScript _self;\n"
                + "    private NewMonoBehaviourScriptFactory _factory;\n"
                + "}\n";

            var rewritten = ScriptTemplateText.ReplaceScriptIdentifier(
                source,
                "NewMonoBehaviourScript",
                "newscript30");

            Assert.That(rewritten, Does.Contain("class newscript30 : MonoBehaviour"));
            Assert.That(rewritten, Does.Contain("private newscript30 _self;"));
            Assert.That(
                rewritten,
                Does.Contain("private NewMonoBehaviourScriptFactory _factory;"));
            Assert.That(
                rewritten,
                Does.Not.Contain("class NewMonoBehaviourScript : MonoBehaviour"));
        }

        [TestCase("newscript30", "newscript30")]
        [TestCase("Already_Valid1", "Already_Valid1")]
        [TestCase("My Player", "MyPlayer")]
        [TestCase("Foo.Bar", "FooBar")]
        [TestCase("3Dee", "_3Dee")]
        [TestCase("日本語", "日本語")]
        [TestCase("_", "_")]
        public void ScriptIdentifier_IsDerivedFromTheFileName(
            string fileBaseName,
            string expectedIdentifier)
        {
            Assert.That(
                ScriptTemplateText.DeriveScriptIdentifier(fileBaseName),
                Is.EqualTo(expectedIdentifier));
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("+-+")]
        [TestCase("!?")]
        public void ScriptIdentifier_WithNothingUsable_IsEmpty(string fileBaseName)
        {
            Assert.That(
                ScriptTemplateText.DeriveScriptIdentifier(fileBaseName),
                Is.Empty);
        }

        // A keyword is made of identifier characters, so it survives the reduction
        // intact and would be stamped straight into a type position.
        [TestCase("class")]
        [TestCase("static")]
        [TestCase("new")]
        public void ScriptIdentifier_ThatIsAReservedKeyword_IsEmpty(string fileBaseName)
        {
            Assert.That(
                ScriptTemplateText.DeriveScriptIdentifier(fileBaseName),
                Is.Empty);
        }

        [TestCase("Class", "Class")]
        [TestCase("classy", "classy")]
        [TestCase("record", "record")]
        public void ScriptIdentifier_ThatOnlyLooksLikeAKeyword_IsKept(
            string fileBaseName,
            string expectedIdentifier)
        {
            Assert.That(
                ScriptTemplateText.DeriveScriptIdentifier(fileBaseName),
                Is.EqualTo(expectedIdentifier));
        }

        [Test]
        public void ScriptTemplate_ForAKeywordFileName_KeepsTheTemplateIdentifier()
        {
            var expanded = ZepCreateAssetSupport.ExpandTemplateContent(
                "public class #SCRIPTNAME# : MonoBehaviour\n{\n}\n",
                "NewMonoBehaviourScript.cs",
                "Assets/Probe/class.cs");

            Assert.That(
                expanded,
                Does.Contain("public class NewMonoBehaviourScript : MonoBehaviour"));
            Assert.That(expanded, Does.Not.Contain("public class class"));
        }

        [TestCase("My Player", "MyPlayer")]
        [TestCase("Foo.Bar", "FooBar")]
        [TestCase("3Dee", "_3Dee")]
        [TestCase("日本語", "日本語")]
        public void ScriptTemplate_SubstitutesTheDerivedIdentifierNotTheRawFileName(
            string fileBaseName,
            string expectedIdentifier)
        {
            var expanded = ZepCreateAssetSupport.ExpandTemplateContent(
                "public class #SCRIPTNAME# : MonoBehaviour\n{\n}\n",
                "NewMonoBehaviourScript.cs",
                "Assets/Probe/" + fileBaseName + ".cs");

            Assert.That(
                expanded,
                Does.Contain("public class " + expectedIdentifier + " : MonoBehaviour"));
        }

        [TestCase("My Player", "MyPlayer")]
        [TestCase("3Dee", "_3Dee")]
        public void ScriptTemplate_RewritesAHardCodedTemplateIdentifierToTheDerivedOne(
            string fileBaseName,
            string expectedIdentifier)
        {
            var expanded = ZepCreateAssetSupport.ExpandTemplateContent(
                "public class NewMonoBehaviourScript : MonoBehaviour\n{\n}\n",
                "NewMonoBehaviourScript.cs",
                "Assets/Probe/" + fileBaseName + ".cs");

            Assert.That(
                expanded,
                Does.Contain("public class " + expectedIdentifier + " : MonoBehaviour"));
            Assert.That(expanded, Does.Not.Contain("NewMonoBehaviourScript"));
        }

        [TestCase("+-+")]
        [TestCase("!?")]
        public void ScriptTemplate_WithNoDerivableIdentifier_KeepsTheTemplateIdentifier(
            string fileBaseName)
        {
            var expanded = ZepCreateAssetSupport.ExpandTemplateContent(
                "public class #SCRIPTNAME# : MonoBehaviour\n{\n}\n",
                "NewMonoBehaviourScript.cs",
                "Assets/Probe/" + fileBaseName + ".cs");

            Assert.That(
                expanded,
                Does.Contain("public class NewMonoBehaviourScript : MonoBehaviour"));
        }

        [Test]
        public void NonScriptTemplate_KeepsTheRawFileNameInScriptNameSubstitutions()
        {
            var expanded = ZepCreateAssetSupport.ExpandTemplateContent(
                "{\n\t\"name\": \"#SCRIPTNAME#\"\n}\n",
                "NewAssembly.asmdef",
                "Assets/Probe/My Player.asmdef");

            Assert.That(expanded, Does.Contain("\"name\": \"My Player\""));
        }

        [Test]
        public void CodeRecipe_DerivesTheDeclaredTypeFromTheFileName()
        {
            var created = ZepCreateAssetSupport.TryCreateNamedAsset(
                "Assets/Create/Scripting/C# Class",
                _folderPath,
                "My Player",
                out var createdPath,
                out var errorMessage);

            Assert.That(created, Is.True, errorMessage);
            Assert.That(createdPath, Does.EndWith("/My Player.cs"));
            Assert.That(
                File.ReadAllText(GetAbsoluteProjectPath(createdPath)),
                Does.Contain("public class MyPlayer"));
        }

        private static string GetAbsoluteProjectPath(string assetPath)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            return Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
