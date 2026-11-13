using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Zep.Core.Application;
using Zep.Editor.Bridge.FileOperations;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI
{
    internal static class ZepCreateAssetSupport
    {
        private static readonly Dictionary<string, CreateAssetRecipe> Recipes = BuildRecipes();

        public static bool TryGetDefaultFileName(string commandPath, out string defaultFileName)
        {
            if (TryGetRecipe(commandPath, out var recipe))
            {
                defaultFileName = recipe.DefaultFileName;
                return true;
            }

            defaultFileName = string.Empty;
            return false;
        }

        public static bool TryCreateNamedAsset(
            string commandPath,
            string targetFolderPath,
            string requestedName,
            out string createdAssetPath,
            out string errorMessage)
        {
            createdAssetPath = string.Empty;
            errorMessage = string.Empty;

            if (!TryGetRecipe(commandPath, out var recipe))
            {
                errorMessage = $"No supported named-create recipe exists for '{commandPath}'.";
                return false;
            }

            var normalizedTargetFolder = NormalizeTargetFolderPath(targetFolderPath);
            var sanitizedName = ScriptTemplateText.SanitizeRequestedName(requestedName, recipe.ExtensionWithDot);
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                errorMessage = "Please enter a valid asset name.";
                return false;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseUniquePathQueries);
            var uniqueAssetPath = AssetDatabase.GenerateUniqueAssetPath(
                CombineAssetPath(normalizedTargetFolder, sanitizedName + recipe.ExtensionWithDot));

            try
            {
                if (!recipe.TryCreate(uniqueAssetPath))
                {
                    errorMessage = $"Failed to create '{Path.GetFileName(uniqueAssetPath)}'.";
                    return false;
                }

                createdAssetPath = uniqueAssetPath;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                errorMessage = $"Failed to create '{Path.GetFileName(uniqueAssetPath)}'.";
                return false;
            }
        }

        // Recipes are matched ONLY by exact command path. We deliberately do NOT fall back
        // to fuzzy matching on the menu label or last path segment: create-menu items are
        // harvested from Unity's live menu tree, so a user's
        // [CreateAssetMenu(menuName="MyTool/Material")] produces the leaf label "Material",
        // which a label fallback would hijack into the built-in Material recipe — creating a
        // plain .mat instead of the user's asset. Any command path that isn't an exact recipe
        // key flows through ZepCreateOrchestrator's ExecuteMenuItem fallback, which
        // runs the real menu item. Internal callers (PromptCreateScriptAt/Scene/Material)
        // already pass exact recipe keys, so nothing legitimate needs the fuzzy lookup.
        private static bool TryGetRecipe(string commandPath, out CreateAssetRecipe recipe)
        {
            return Recipes.TryGetValue(commandPath ?? string.Empty, out recipe);
        }

        private static Dictionary<string, CreateAssetRecipe> BuildRecipes()
        {
            var recipes = new Dictionary<string, CreateAssetRecipe>(StringComparer.OrdinalIgnoreCase);
            var editorContentsPath = EditorApplication.applicationContentsPath;
            var builtInTemplateRoot = Path.Combine(editorContentsPath, "Resources/ScriptTemplates");
            var physicsMaterialType = typeof(PhysicsMaterial);

            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/MonoBehaviour Script",
                Path.Combine(builtInTemplateRoot, "1-Scripting__MonoBehaviour Script-NewMonoBehaviourScript.cs.txt"),
                "NewMonoBehaviourScript.cs");
            // Unity 6 also lists "MonoBehaviour Script" at the top of Assets/Create,
            // outside the Scripting submenu the template file names it under. The
            // create menu is harvested from the live menu tree, so that item
            // reaches the orchestrator under the top-level path; without this key
            // it fell through to the ExecuteMenuItem fallback (default name, then
            // a rename after the import — the class-name collision and journal
            // shapes the reload fixes had to absorb).
            recipes["Assets/Create/MonoBehaviour Script"] =
                recipes["Assets/Create/Scripting/MonoBehaviour Script"];
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/Empty C# Script",
                Path.Combine(builtInTemplateRoot, "3-Scripting__Empty C# Script-NewEmptyCSharpScript.cs.txt"),
                "NewEmptyCSharpScript.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/ScriptableObject Script",
                Path.Combine(builtInTemplateRoot, "2-Scripting__ScriptableObject Script-NewScriptableObjectScript.cs.txt"),
                "NewScriptableObjectScript.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/Playables/Playable Behaviour Script",
                Path.Combine(builtInTemplateRoot, "1-Scripting__Playables__Playable Behaviour Script-NewPlayableBehaviour.cs.txt"),
                "NewPlayableBehaviour.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/Playables/Playable Asset Script",
                Path.Combine(builtInTemplateRoot, "2-Scripting__Playables__Playable Asset Script-NewPlayableAsset.cs.txt"),
                "NewPlayableAsset.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/Assembly Definition",
                Path.Combine(builtInTemplateRoot, "22-Scripting__Assembly Definition-NewAssembly.asmdef.txt"),
                "NewAssembly.asmdef");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/Assembly Definition Reference",
                Path.Combine(builtInTemplateRoot, "23-Scripting__Assembly Definition Reference-NewAssemblyReference.asmref.txt"),
                "NewAssemblyReference.asmref");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/Standard Surface Shader",
                Path.Combine(builtInTemplateRoot, "1-Shader__Standard Surface Shader-NewSurfaceShader.shader.txt"),
                "NewSurfaceShader.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/Unlit Shader",
                Path.Combine(builtInTemplateRoot, "2-Shader__Unlit Shader-NewUnlitShader.shader.txt"),
                "NewUnlitShader.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/Image Effect Shader",
                Path.Combine(builtInTemplateRoot, "3-Shader__Image Effect Shader-NewImageEffectShader.shader.txt"),
                "NewImageEffectShader.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/Compute Shader",
                Path.Combine(builtInTemplateRoot, "4-Shader__Compute Shader-NewComputeShader.compute.txt"),
                "NewComputeShader.compute");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/Ray Tracing Shader",
                Path.Combine(builtInTemplateRoot, "5-Shader__Ray Tracing Shader-NewRayTracingShader.raytrace.txt"),
                "NewRayTracingShader.raytrace");

            AddTemplateRecipe(
                recipes,
                "Assets/Create/Scripting/URP Renderer Feature Script",
                "Packages/com.unity.render-pipelines.universal/Editor/ScriptTemplates/ScriptableRendererFeature.txt",
                "NewURPRenderFeature.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/URP Unlit Shader",
                "Packages/com.unity.render-pipelines.universal/Editor/ScriptTemplates/UnlitURP.txt",
                "NewUnlitUniversalRenderPipelineShader.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/SRP Blit Shader",
                "Packages/com.unity.render-pipelines.core/Editor/ScriptTemplates/BlitSRP.txt",
                "NewBlitScriptableRenderPipelineShader.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/HDRP Custom FullScreen Pass",
                "Packages/com.unity.render-pipelines.high-definition/Editor/RenderPipeline/CustomPass/CustomPassFullScreenShader.template",
                "New FullScreen CustomPass.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/HDRP Custom Renderers Pass",
                "Packages/com.unity.render-pipelines.high-definition/Editor/RenderPipeline/CustomPass/CustomPassRenderersShader.template",
                "New Renderers CustomPass.shader");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Rendering/HDRP C# Custom Pass",
                "Packages/com.unity.render-pipelines.high-definition/Editor/RenderPipeline/CustomPass/CustomPassCSharpScript.template",
                "New Custom Pass.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Rendering/HDRP C# Post Process Volume",
                "Packages/com.unity.render-pipelines.high-definition/Editor/PostProcessing/Templates/CustomPostProcessingVolume.template",
                "NewPostProcessVolume.cs");
            AddTemplateRecipe(
                recipes,
                "Assets/Create/Shader/HDRP Post Process",
                "Packages/com.unity.render-pipelines.high-definition/Editor/PostProcessing/Templates/CustomPostProcessingShader.template",
                "New Post Process Volume.shader");

            AddMaterialRecipe(recipes);
            AddSceneRecipe(recipes);
            AddReflectedObjectRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Rendering/Volume Profile"
                },
                "New Volume Profile.asset",
                "UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime");
            AddObjectRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Physics Material 2D",
                    "Assets/Create/Physics 2D Material"
                },
                "New Physics Material 2D.physicsMaterial2D",
                typeof(PhysicsMaterial2D));
            AddObjectRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Physics Material",
                    "Assets/Create/Physics 3D Material"
                },
                "New Physics Material.physicMaterial",
                physicsMaterialType);
            AddReflectedObjectRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/TextMeshPro/Color Gradient"
                },
                "New TMP Color Gradient.asset",
                "TMPro.TMP_ColorGradient, Unity.TextMeshPro");
            AddReflectedObjectRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/TextMeshPro/Style Sheet"
                },
                "Text StyleSheet.asset",
                "TMPro.TMP_StyleSheet, Unity.TextMeshPro");
            AddCodeRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Scripting/C# Class",
                    "Assets/Create/C#/Class"
                },
                "NewClass.cs",
                typeName => ScriptTemplateText.BuildClassTemplate(
                    typeName,
                    EditorSettings.projectGenerationRootNamespace));
            AddCodeRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Scripting/C# Interface",
                    "Assets/Create/C#/Interface"
                },
                "NewInterface.cs",
                typeName => ScriptTemplateText.BuildInterfaceTemplate(
                    typeName,
                    EditorSettings.projectGenerationRootNamespace));
            AddCodeRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Scripting/C# Record",
                    "Assets/Create/C#/Record"
                },
                "NewRecord.cs",
                typeName => ScriptTemplateText.BuildRecordTemplate(
                    typeName,
                    EditorSettings.projectGenerationRootNamespace));
            AddCodeRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Scripting/C# Struct",
                    "Assets/Create/C#/Struct"
                },
                "NewStruct.cs",
                typeName => ScriptTemplateText.BuildStructTemplate(
                    typeName,
                    EditorSettings.projectGenerationRootNamespace));
            AddCodeRecipe(
                recipes,
                new[]
                {
                    "Assets/Create/Scripting/C# Enum",
                    "Assets/Create/C#/Enum"
                },
                "NewEnum.cs",
                typeName => ScriptTemplateText.BuildEnumTemplate(
                    typeName,
                    EditorSettings.projectGenerationRootNamespace));

            return recipes;
        }

        private static void AddTemplateRecipe(
            IDictionary<string, CreateAssetRecipe> recipes,
            string commandPath,
            string templatePath,
            string defaultFileName)
        {
            recipes[commandPath] = new CreateAssetRecipe(
                defaultFileName,
                assetPath =>
                {
                    return TryCreateAssetFromTemplate(
                        templatePath,
                        defaultFileName,
                        assetPath);
                });
        }

        private static void AddCodeRecipe(
            IDictionary<string, CreateAssetRecipe> recipes,
            IReadOnlyList<string> commandPaths,
            string defaultFileName,
            Func<string, string> contentBuilder)
        {
            for (var i = 0; i < commandPaths.Count; i++)
            {
                var commandPath = commandPaths[i];
                recipes[commandPath] = new CreateAssetRecipe(
                    defaultFileName,
                    assetPath =>
                    {
                        var absolutePath = GetAbsoluteProjectPath(assetPath);
                        var directory = Path.GetDirectoryName(absolutePath);
                        if (string.IsNullOrWhiteSpace(directory))
                        {
                            return false;
                        }

                        // The builders emit a type declaration, so the file base name has
                        // to be reduced to an identifier first; when nothing usable
                        // survives, the recipe's default type name keeps the file
                        // compiling (same fallback as the template path).
                        var derivedTypeName = ScriptTemplateText.DeriveScriptIdentifier(
                            Path.GetFileNameWithoutExtension(assetPath));
                        var typeName = string.IsNullOrEmpty(derivedTypeName)
                            ? Path.GetFileNameWithoutExtension(defaultFileName)
                            : derivedTypeName;

                        Directory.CreateDirectory(directory);
                        File.WriteAllText(
                            absolutePath,
                            contentBuilder(typeName),
                            new UTF8Encoding(false));
                        ImportWrittenAsset(assetPath);
                        return AssetExists(assetPath);
                    });
            }
        }

        private static void AddReflectedObjectRecipe(
            IDictionary<string, CreateAssetRecipe> recipes,
            IReadOnlyList<string> commandPaths,
            string defaultFileName,
            params string[] typeNames)
        {
            AddObjectRecipe(recipes, commandPaths, defaultFileName, ResolveType(typeNames));
        }

        private static void AddObjectRecipe(
            IDictionary<string, CreateAssetRecipe> recipes,
            IReadOnlyList<string> commandPaths,
            string defaultFileName,
            Type assetType)
        {
            for (var i = 0; i < commandPaths.Count; i++)
            {
                var commandPath = commandPaths[i];
                recipes[commandPath] = new CreateAssetRecipe(
                    defaultFileName,
                    assetPath =>
                    {
                        var assetName = Path.GetFileNameWithoutExtension(assetPath);
                        var asset = CreateNamedAssetInstance(assetType, assetName);
                        if (asset == null)
                        {
                            return false;
                        }

                        ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateAssetCalls);
                        AssetDatabase.CreateAsset(asset, assetPath);
                        return AssetExists(assetPath);
                    });
            }
        }

        private static void AddMaterialRecipe(IDictionary<string, CreateAssetRecipe> recipes)
        {
            recipes["Assets/Create/Material"] = new CreateAssetRecipe(
                "New Material.mat",
                assetPath =>
                {
                    var shader = ResolveDefaultMaterialShader();
                    if (shader == null)
                    {
                        return false;
                    }

                    var material = new Material(shader);
                    material.name = Path.GetFileNameWithoutExtension(assetPath);
                    ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseCreateAssetCalls);
                    AssetDatabase.CreateAsset(material, assetPath);
                    return AssetExists(assetPath);
                });
        }

        // What Unity's own Assets/Create/Scene runs: it writes the default scene
        // straight to disk and never touches the open scene set. The public
        // route below it, NewScene(Additive), is refused outright while an
        // untitled scene is open - which is every editor session until its
        // first scene save - so New Scene failed there. Internal, hence bound
        // by name; null on an editor that no longer has it, and the recipe
        // then falls back to the additive route.
        private static readonly Func<string, bool, bool> CreateSceneAsset = BindCreateSceneAsset();

        private static Func<string, bool, bool> BindCreateSceneAsset()
        {
            var method = typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod(
                "CreateSceneAsset",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(string), typeof(bool) },
                null);
            return method == null || method.ReturnType != typeof(bool)
                ? null
                : (Func<string, bool, bool>)Delegate.CreateDelegate(
                    typeof(Func<string, bool, bool>), method);
        }

        private static void AddSceneRecipe(IDictionary<string, CreateAssetRecipe> recipes)
        {
            recipes["Assets/Create/Scene"] = new CreateAssetRecipe(
                "NewScene.unity",
                assetPath =>
                {
                    if (CreateSceneAsset != null)
                    {
                        return CreateSceneAsset(assetPath, true);
                    }

                    // Create the .unity file WITHOUT disturbing the editor's open scene set.
                    // NewSceneMode.Single would close every currently-open scene and replace
                    // them with a fresh empty one — with no save prompt — destroying unsaved
                    // edits. Additive builds the template scene alongside the open ones; we
                    // save it to disk, then CloseScene removes it again, leaving the user's
                    // working scenes untouched (matching Unity's own Assets/Create/Scene).
                    var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                        UnityEditor.SceneManagement.NewSceneSetup.DefaultGameObjects,
                        UnityEditor.SceneManagement.NewSceneMode.Additive);
                    var saved = UnityEditor.SceneManagement.EditorSceneManager.SaveScene(scene, assetPath);
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    return saved;
                });
        }

        private static Shader ResolveDefaultMaterialShader()
        {
            var currentPipeline = GraphicsSettings.currentRenderPipeline;
            if (currentPipeline != null && currentPipeline.defaultMaterial != null && currentPipeline.defaultMaterial.shader != null)
            {
                return currentPipeline.defaultMaterial.shader;
            }

            var defaultPipeline = GraphicsSettings.defaultRenderPipeline;
            if (defaultPipeline != null && defaultPipeline.defaultMaterial != null && defaultPipeline.defaultMaterial.shader != null)
            {
                return defaultPipeline.defaultMaterial.shader;
            }

            return Shader.Find("Standard")
                ?? Shader.Find("Universal Render Pipeline/Lit")
                ?? Shader.Find("HDRP/Lit");
        }

        private static bool TryCreateAssetFromTemplate(
            string templatePath,
            string defaultFileName,
            string assetPath)
        {
            if (!TryReadTemplateContent(templatePath, out var templateContent))
            {
                return false;
            }

            var absolutePath = GetAbsoluteProjectPath(assetPath);
            var directory = Path.GetDirectoryName(absolutePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            Directory.CreateDirectory(directory);
            File.WriteAllText(
                absolutePath,
                ExpandTemplateContent(templateContent, defaultFileName, assetPath),
                new UTF8Encoding(false));
            ImportWrittenAsset(assetPath);
            return AssetExists(assetPath);
        }

        private static bool TryReadTemplateContent(string templatePath, out string templateContent)
        {
            templateContent = string.Empty;
            if (string.IsNullOrWhiteSpace(templatePath))
            {
                return false;
            }

            if (Path.IsPathRooted(templatePath))
            {
                if (!File.Exists(templatePath)
                    && !TryResolveLegacyBuiltInTemplate(templatePath, out templatePath))
                {
                    return false;
                }

                templateContent = File.ReadAllText(templatePath);
                return true;
            }

            var textAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(templatePath);
            if (textAsset == null)
            {
                return false;
            }

            templateContent = textAsset.text;
            return !string.IsNullOrEmpty(templateContent);
        }

        private static bool TryResolveLegacyBuiltInTemplate(
            string requestedPath,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            var legacyFileName = ScriptTemplateText.GetLegacyBuiltInTemplateFileName(
                Path.GetFileName(requestedPath));
            if (string.IsNullOrEmpty(legacyFileName))
            {
                return false;
            }

            resolvedPath = Path.Combine(
                Path.GetDirectoryName(requestedPath) ?? string.Empty,
                legacyFileName);
            return File.Exists(resolvedPath);
        }

        // The template text rules live in Core; the project's root namespace is the
        // one input only the editor knows.
        internal static string ExpandTemplateContent(
            string templateContent,
            string defaultFileName,
            string assetPath)
        {
            return ScriptTemplateText.ExpandTemplateContent(
                templateContent,
                defaultFileName,
                assetPath,
                EditorSettings.projectGenerationRootNamespace);
        }

        private static void ImportWrittenAsset(string assetPath)
        {
            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseImportAssetCalls);
            AssetDatabase.ImportAsset(
                assetPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static Object CreateNamedAssetInstance(Type assetType, string assetName)
        {
            if (assetType == null)
            {
                return null;
            }

            if (typeof(ScriptableObject).IsAssignableFrom(assetType))
            {
                var scriptableObject = ScriptableObject.CreateInstance(assetType);
                if (scriptableObject != null)
                {
                    scriptableObject.name = assetName;
                    return scriptableObject;
                }
            }

            var stringConstructor = assetType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            if (stringConstructor != null && stringConstructor.Invoke(new object[] { assetName }) is Object namedInstance)
            {
                namedInstance.name = assetName;
                return namedInstance;
            }

            var parameterlessConstructor = assetType.GetConstructor(
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null);
            if (parameterlessConstructor != null && parameterlessConstructor.Invoke(null) is Object instance)
            {
                instance.name = assetName;
                return instance;
            }

            return null;
        }

        private static Type ResolveType(IReadOnlyList<string> typeNames)
        {
            if (typeNames == null)
            {
                return null;
            }

            for (var i = 0; i < typeNames.Count; i++)
            {
                var typeName = typeNames[i];
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    continue;
                }

                var resolvedType = Type.GetType(typeName, false);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            return null;
        }

        private static bool AssetExists(string assetPath)
        {
            return !string.IsNullOrWhiteSpace(assetPath) && AssetDatabase.LoadMainAssetAtPath(assetPath) != null;
        }

        private static string NormalizeTargetFolderPath(string targetFolderPath)
        {
            var normalized = string.IsNullOrWhiteSpace(targetFolderPath) ? "Assets" : targetFolderPath.Replace('\\', '/').TrimEnd('/');
            return string.IsNullOrWhiteSpace(normalized) ? "Assets" : normalized;
        }

        private static string CombineAssetPath(string folderPath, string fileName)
        {
            return (folderPath.TrimEnd('/') + "/" + fileName.TrimStart('/')).Replace('\\', '/');
        }

        private static string GetAbsoluteProjectPath(string assetPath)
        {
            return ProjectPaths.ToAbsolutePath(assetPath);
        }

        private readonly struct CreateAssetRecipe
        {
            internal CreateAssetRecipe(string defaultFileName, Func<string, bool> tryCreate)
            {
                DefaultFileName = defaultFileName ?? string.Empty;
                TryCreate = tryCreate ?? throw new ArgumentNullException(nameof(tryCreate));
            }

            internal string DefaultFileName { get; }

            internal Func<string, bool> TryCreate { get; }

            internal string ExtensionWithDot => Path.GetExtension(DefaultFileName) ?? string.Empty;
        }
    }
}
