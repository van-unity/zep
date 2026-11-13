using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Zep.Core.Application
{
    /// <summary>
    /// The text half of creating a script or other templated asset: reducing a
    /// file name to a C# identifier, expanding Unity's template tokens, wrapping
    /// in the project's root namespace, and the built-in type declarations. It
    /// reads and writes no files; the caller supplies the template text and the
    /// project's root namespace.
    /// </summary>
    public static class ScriptTemplateText
    {
        public static string ExpandTemplateContent(
            string templateContent,
            string defaultFileName,
            string assetPath,
            string rootNamespace)
        {
            var assetName = Path.GetFileNameWithoutExtension(assetPath);
            var templateIdentifier = Path.GetFileNameWithoutExtension(defaultFileName);
            var isCSharpTemplate = string.Equals(
                Path.GetExtension(defaultFileName),
                ".cs",
                StringComparison.OrdinalIgnoreCase);

            // In a .cs template #SCRIPTNAME# lands in a type position, and file names are
            // far more permissive than C# identifiers ("My Player", "Foo.Bar", "3Dee" are
            // all legal file names), so the raw base name has to be reduced to an
            // identifier first or the generated file will not compile. When nothing usable
            // survives the reduction, the template's own identifier stays — a name/class
            // mismatch compiles, invalid C# does not. Non-.cs templates keep the raw name:
            // #SCRIPTNAME# is the assembly name in .asmdef and #NAME# is a shader path.
            var scriptName = assetName;
            if (isCSharpTemplate)
            {
                var derivedIdentifier = DeriveScriptIdentifier(assetName);
                scriptName = string.IsNullOrEmpty(derivedIdentifier)
                    ? templateIdentifier
                    : derivedIdentifier;
            }

            var expandedContent = (templateContent ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("#SCRIPTNAME#", scriptName)
                .Replace("#SCRIPTNAME_LOWER#", scriptName.ToLowerInvariant())
                .Replace("#NAME#", assetName)
                .Replace("#NOTRIM#", string.Empty);

            if (isCSharpTemplate)
            {
                expandedContent = ReplaceScriptIdentifier(
                    expandedContent,
                    templateIdentifier,
                    scriptName);
            }

            expandedContent = ApplyRootNamespace(expandedContent, rootNamespace);
            return expandedContent.Replace("\n", Environment.NewLine);
        }

        // Reserved keywords are lexically fine as identifier characters but cannot BE an
        // identifier ("public class class" does not compile), so a name that reduces to one
        // takes the same skip route as a name that reduces to nothing. Contextual keywords
        // (var, record, async, …) are legal identifiers and are deliberately absent.
        private static readonly HashSet<string> ReservedKeywords = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char",
            "checked", "class", "const", "continue", "decimal", "default", "delegate", "do",
            "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
            "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int",
            "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
            "object", "operator", "out", "override", "params", "private", "protected",
            "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
            "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while"
        };

        // Reduces an asset base name to a C# identifier the way Unity's template pipeline
        // does: keep only identifier characters, and prefix '_' when the survivor cannot
        // open an identifier (a leading digit, mark or formatting character). Non-ASCII
        // letters are valid C# identifier characters and must survive verbatim.
        // Returns string.Empty when nothing usable survives; callers must then leave the
        // template's own identifier alone instead of stamping an invalid one.
        public static string DeriveScriptIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            var identifier = new StringBuilder(name.Length + 1);
            for (var i = 0; i < name.Length; i++)
            {
                var current = name[i];
                if (IsIdentifierPartCharacter(current))
                {
                    identifier.Append(current);
                }
            }

            if (identifier.Length == 0)
            {
                return string.Empty;
            }

            if (!IsIdentifierStartCharacter(identifier[0]))
            {
                identifier.Insert(0, '_');
            }

            var derived = identifier.ToString();
            return ReservedKeywords.Contains(derived) ? string.Empty : derived;
        }

        private static bool IsIdentifierStartCharacter(char character)
        {
            return character == '_'
                || char.IsLetter(character)
                || CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.LetterNumber;
        }

        private static bool IsIdentifierPartCharacter(char character)
        {
            if (IsIdentifierStartCharacter(character))
            {
                return true;
            }

            switch (CharUnicodeInfo.GetUnicodeCategory(character))
            {
                case UnicodeCategory.DecimalDigitNumber:
                case UnicodeCategory.ConnectorPunctuation:
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.SpacingCombiningMark:
                case UnicodeCategory.Format:
                    return true;
                default:
                    return false;
            }
        }

        public static string ReplaceScriptIdentifier(
            string content,
            string originalIdentifier,
            string replacementIdentifier)
        {
            if (string.IsNullOrEmpty(content)
                || string.IsNullOrEmpty(originalIdentifier)
                || string.Equals(
                    originalIdentifier,
                    replacementIdentifier,
                    StringComparison.Ordinal))
            {
                return content ?? string.Empty;
            }

            var pattern = "(?<![\\p{L}\\p{Nd}_])"
                + Regex.Escape(originalIdentifier)
                + "(?![\\p{L}\\p{Nd}_])";
            return Regex.Replace(
                content,
                pattern,
                _ => replacementIdentifier ?? string.Empty);
        }

        private static string ApplyRootNamespace(string templateContent, string projectRootNamespace)
        {
            const string beginToken = "#ROOTNAMESPACEBEGIN#";
            const string endToken = "#ROOTNAMESPACEEND#";

            var beginIndex = templateContent.IndexOf(beginToken, StringComparison.Ordinal);
            var endIndex = templateContent.IndexOf(endToken, StringComparison.Ordinal);
            if (beginIndex < 0 || endIndex < 0 || endIndex <= beginIndex)
            {
                return templateContent
                    .Replace(beginToken, string.Empty)
                    .Replace(endToken, string.Empty);
            }

            var beginLineStart = FindLineStart(templateContent, beginIndex);
            var endLineEnd = FindLineEnd(templateContent, endIndex + endToken.Length);

            // Body is the text strictly between the two tokens (not between their whole
            // lines), so a template placing both markers on a single line —
            // "#ROOTNAMESPACEBEGIN#...#ROOTNAMESPACEEND#" — doesn't yield a negative-length
            // slice (which previously threw and surfaced as a generic "Failed to create").
            // before/after still drop the entire marker lines.
            var bodyStart = beginIndex + beginToken.Length;
            var before = templateContent.Substring(0, beginLineStart);
            var wrappedBody = templateContent.Substring(bodyStart, endIndex - bodyStart);
            var after = templateContent.Substring(endLineEnd);
            var rootNamespace = projectRootNamespace?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(rootNamespace))
            {
                return before + wrappedBody + after;
            }

            return before
                + $"namespace {rootNamespace}\n{{\n"
                + Indent(wrappedBody.Trim('\n'), "    ")
                + "\n}\n"
                + after.TrimStart('\n');
        }

        private static int FindLineStart(string content, int index)
        {
            if (index <= 0)
            {
                return 0;
            }

            var newlineIndex = content.LastIndexOf('\n', Math.Min(index, content.Length - 1));
            return newlineIndex < 0 ? 0 : newlineIndex + 1;
        }

        private static int FindLineEnd(string content, int index)
        {
            if (index >= content.Length)
            {
                return content.Length;
            }

            var newlineIndex = content.IndexOf('\n', index);
            return newlineIndex < 0 ? content.Length : newlineIndex + 1;
        }

        public static string SanitizeRequestedName(string requestedName, string extensionWithDot)
        {
            var normalized = (requestedName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            // Strip ONLY the recipe's own extension if the user re-typed it (e.g. "Foo.mat"
            // for a Material). Any other dot is a legitimate part of the asset name — Unity
            // allows "Rock_v1.5" or a C# type "Foo.Bar" — so we must not run a generic
            // extension strip here; that silently truncated names at the last dot.
            if (!string.IsNullOrWhiteSpace(extensionWithDot)
                && normalized.EndsWith(extensionWithDot, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - extensionWithDot.Length);
            }

            var invalidCharacters = Path.GetInvalidFileNameChars();
            var sanitizedCharacters = new char[normalized.Length];
            var writeIndex = 0;
            for (var i = 0; i < normalized.Length; i++)
            {
                var current = normalized[i];
                if (Array.IndexOf(invalidCharacters, current) >= 0)
                {
                    continue;
                }

                sanitizedCharacters[writeIndex++] = current;
            }

            return new string(sanitizedCharacters, 0, writeIndex).Trim();
        }

        public static string GetLegacyBuiltInTemplateFileName(string currentFileName)
        {
            switch (currentFileName)
            {
                case "1-Scripting__MonoBehaviour Script-NewMonoBehaviourScript.cs.txt":
                    return "81-C# Script-NewBehaviourScript.cs.txt";
                case "1-Scripting__Playables__Playable Behaviour Script-NewPlayableBehaviour.cs.txt":
                    return "87-Playables__Playable Behaviour C# Script-NewPlayableBehaviour.cs.txt";
                case "2-Scripting__Playables__Playable Asset Script-NewPlayableAsset.cs.txt":
                    return "88-Playables__Playable Asset C# Script-NewPlayableAsset.cs.txt";
                case "22-Scripting__Assembly Definition-NewAssembly.asmdef.txt":
                    return "91-Assembly Definition-NewAssembly.asmdef.txt";
                case "23-Scripting__Assembly Definition Reference-NewAssemblyReference.asmref.txt":
                    return "93-Assembly Definition Reference-NewAssemblyReference.asmref.txt";
                case "1-Shader__Standard Surface Shader-NewSurfaceShader.shader.txt":
                    return "83-Shader__Standard Surface Shader-NewSurfaceShader.shader.txt";
                case "2-Shader__Unlit Shader-NewUnlitShader.shader.txt":
                    return "84-Shader__Unlit Shader-NewUnlitShader.shader.txt";
                case "3-Shader__Image Effect Shader-NewImageEffectShader.shader.txt":
                    return "85-Shader__Image Effect Shader-NewImageEffectShader.shader.txt";
                case "4-Shader__Compute Shader-NewComputeShader.compute.txt":
                    return "90-Shader__Compute Shader-NewComputeShader.compute.txt";
                case "5-Shader__Ray Tracing Shader-NewRayTracingShader.raytrace.txt":
                    return "93-Shader__Ray Tracing Shader-NewRayTracingShader.raytrace.txt";
                default:
                    return string.Empty;
            }
        }

        public static string BuildClassTemplate(string typeName, string rootNamespace)
        {
            return WrapInRootNamespace(
                typeName,
                rootNamespace,
                $"public class {typeName}{Environment.NewLine}{{{Environment.NewLine}}}");
        }

        public static string BuildInterfaceTemplate(string typeName, string rootNamespace)
        {
            return WrapInRootNamespace(
                typeName,
                rootNamespace,
                $"public interface {typeName}{Environment.NewLine}{{{Environment.NewLine}}}");
        }

        public static string BuildRecordTemplate(string typeName, string rootNamespace)
        {
            return WrapInRootNamespace(typeName, rootNamespace, $"public record {typeName};");
        }

        public static string BuildStructTemplate(string typeName, string rootNamespace)
        {
            return WrapInRootNamespace(
                typeName,
                rootNamespace,
                $"public struct {typeName}{Environment.NewLine}{{{Environment.NewLine}}}");
        }

        public static string BuildEnumTemplate(string typeName, string rootNamespace)
        {
            return WrapInRootNamespace(
                typeName,
                rootNamespace,
                $"public enum {typeName}{Environment.NewLine}{{{Environment.NewLine}    Value{Environment.NewLine}}}");
        }

        private static string WrapInRootNamespace(string typeName, string rootNamespace, string body)
        {
            if (string.IsNullOrWhiteSpace(rootNamespace))
            {
                return body + Environment.NewLine;
            }

            return
                $"namespace {rootNamespace}{Environment.NewLine}" +
                $"{{{Environment.NewLine}" +
                Indent(body, "    ") +
                $"{Environment.NewLine}}}{Environment.NewLine}";
        }

        private static string Indent(string content, string indentation)
        {
            if (string.IsNullOrEmpty(content))
            {
                return string.Empty;
            }

            var lines = content.Replace("\r\n", "\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = indentation + lines[i];
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
