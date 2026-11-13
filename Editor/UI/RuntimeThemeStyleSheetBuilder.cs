using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Zep.Core.Theming;
using Zep.Editor.Bridge.Theming;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal static class RuntimeThemeStyleSheetBuilder
    {
        // WHY: Unity exposes no public API for building a `StyleSheet` from USS text at
        // runtime. The cascade for `var(--zep-*)` requires a real StyleSheet asset, so we
        // reflect into the editor importer to parse generated USS in-memory. If Unity's
        // internals shift, callers fall back to the on-disk theme path.
        private const string ImporterTypeName = "UnityEditor.UIElements.StyleSheets.StyleSheetImporterImpl";

        // Test-only counter (idiom: ZepPathHelpers.LinearAssetPathScans):
        // proves the theme-creator preview rebuilds at most once per frame.
        internal static int BuildCountForTests;

        private static bool _resolveAttempted;
        private static MethodInfo _importMethod;
        private static object _importerInstance;

        public static StyleSheet Build(string themeId, IReadOnlyDictionary<string, Color> variables)
        {
            if (string.IsNullOrWhiteSpace(themeId))
                throw new ArgumentException("Theme id must be non-empty.", nameof(themeId));
            if (variables == null)
                throw new ArgumentNullException(nameof(variables));

            BuildCountForTests++;

            EnsureImporter();
            if (_importMethod == null || _importerInstance == null)
                return null;

            var ussText = BuildUssText(themeId, variables);
            var sheet = ScriptableObject.CreateInstance<StyleSheet>();
            sheet.name = $"ZepTheme_{themeId}_Runtime";
            // WHY: this sheet is the panel's live third stylesheet, but it is not an
            // asset — a plain CreateInstance object is destroyed when a scene loads,
            // which silently reverted a user theme to Defaults mid-session. The flag
            // keeps it alive; ZepThemeSwitcher.Teardown owns destroying it.
            sheet.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                _importMethod.Invoke(_importerInstance, new object[] { sheet, ussText });
            }
            catch (TargetInvocationException ex)
            {
                Debug.LogWarning($"[Zep] Runtime theme stylesheet import failed for '{themeId}': {ex.InnerException?.Message ?? ex.Message}");
                UnityEngine.Object.DestroyImmediate(sheet);
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Zep] Runtime theme stylesheet import failed for '{themeId}': {ex.Message}");
                UnityEngine.Object.DestroyImmediate(sheet);
                return null;
            }

            return sheet;
        }

        internal static string BuildUssText(string themeId, IReadOnlyDictionary<string, Color> variables)
        {
            var className = ZepThemeRegistry.GetThemeClassName(themeId);
            var sb = new StringBuilder();
            sb.Append(".zep-root.");
            sb.Append(className);
            sb.Append(" {\n");
            foreach (var key in ZepThemeSchema.RequiredVariables)
            {
                if (!variables.TryGetValue(key, out var color))
                    continue;
                sb.Append("    ");
                sb.Append(key);
                sb.Append(": ");
                sb.Append(FormatColor(color));
                sb.Append(";\n");
            }
            sb.Append("}\n");
            return sb.ToString();
        }

        private static string FormatColor(Color color)
        {
            var r = Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f);
            var g = Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f);
            var b = Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
            var a = Mathf.Clamp01(color.a);
            if (a >= 0.999f)
                return $"rgb({r}, {g}, {b})";
            return string.Format(CultureInfo.InvariantCulture, "rgba({0}, {1}, {2}, {3:0.###})", r, g, b, a);
        }

        private static void EnsureImporter()
        {
            if (_resolveAttempted)
                return;
            _resolveAttempted = true;

            var importerType = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(t => t != null && t.FullName == ImporterTypeName);

            if (importerType == null)
            {
                Debug.LogWarning($"[Zep] Could not locate {ImporterTypeName}; in-memory theme path disabled.");
                return;
            }

            var method = importerType.GetMethod(
                "Import",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(StyleSheet), typeof(string) },
                null);

            if (method == null)
            {
                Debug.LogWarning($"[Zep] {ImporterTypeName}.Import(StyleSheet, string) not found; in-memory theme path disabled.");
                return;
            }

            object instance;
            try
            {
                instance = Activator.CreateInstance(importerType, nonPublic: true);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Zep] Could not construct {ImporterTypeName}: {ex.Message}");
                return;
            }

            _importMethod = method;
            _importerInstance = instance;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try { return assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }
    }
}
