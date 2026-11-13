using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Zep.Core.Domain;

namespace Zep.Core.Indexing
{
    public sealed class AssetRecord : IEquatable<AssetRecord>
    {
        // Type names are extremely low cardinality (a few dozen distinct values across an
        // entire project: "MonoScript", "Texture2D", "GameObject", ...). Interning them
        // de-duplicates 100k+ string allocations down to a few dozen at the cost of one
        // dictionary lookup per record. The same trick applies to NormalizedTypeName.
        // ConcurrentDictionary because record construction can happen on Parallel.ForEach
        // workers from NativeFileSystemAssetRecordSource.
        private static readonly ConcurrentDictionary<string, string> TypeNameInterner =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // NormalizedTypeSearchText is a function of (normalized type name, extension,
        // normalized base types) only, so its cardinality is the number of distinct
        // TYPE SHAPES in a project - a few dozen to a few hundred - not the number of
        // records. Without this the 100+ character text was allocated once per record.
        private static readonly ConcurrentDictionary<string, string> TypeSearchTextInterner =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        public AssetRecord(
            string guid,
            string assetPath,
            string name,
            string typeName,
            string tagSearchText = "",
            IReadOnlyList<string> unityLabels = null,
            IReadOnlyList<string> baseTypeNames = null)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                throw new ArgumentException("GUID must be provided.", nameof(guid));
            }

            Guid = guid;
            AssetPath = assetPath ?? string.Empty;
            Name = name ?? string.Empty;
            TypeName = InternTypeName(typeName ?? string.Empty);
            TagSearchText = tagSearchText ?? string.Empty;
            UnityLabels = CopyNonEmpty(unityLabels);
            BaseTypeNames = CopyNonEmpty(baseTypeNames);

            NormalizedGuid = NormalizeText(guid);
            NormalizedAssetPath = NormalizePath(AssetPath);
            NormalizedName = NormalizeText(Name);
            NormalizedTypeName = InternTypeName(NormalizeText(TypeName));
            NormalizedBaseTypeNames = NormalizeTypeNames(BaseTypeNames);
            NormalizedTagSearchText = NormalizeText(TagSearchText);
            NormalizedExtension = GetNormalizedExtension(NormalizedAssetPath);
            NormalizedTypeSearchText = BuildNormalizedTypeSearchText(
                NormalizedTypeName,
                NormalizedExtension,
                NormalizedBaseTypeNames);
            NormalizedUnityLabels = NormalizeLabels(UnityLabels);
            NormalizedUnityLabelSearchText = string.Join("\n", NormalizedUnityLabels);
            NormalizedSearchText = BuildNormalizedSearchText(
                NormalizedName,
                NormalizedTypeName,
                NormalizedTagSearchText,
                NormalizedUnityLabelSearchText);
        }

        // A folder move rewrites one input - the path - and a tag assignment
        // rewrites one input - the tag text. Reconstructing the record through the
        // full constructor re-normalises the guid, the name, the type name, every
        // base type and every label, and rebuilds the any-field search text, all of
        // which come out byte-identical to the source's. A 20k-descendant folder
        // move paid that for every descendant. Passing null for either input means
        // "carry the source's value and everything derived from it"; the two named
        // factories below say which one the caller is changing (two
        // (AssetRecord, string) constructors would be ambiguous at the call site).
        private AssetRecord(
            AssetRecord source,
            string newAssetPath,
            string newTagSearchText)
        {
            Guid = source.Guid;
            Name = source.Name;
            TypeName = source.TypeName;
            UnityLabels = source.UnityLabels;
            BaseTypeNames = source.BaseTypeNames;
            NormalizedGuid = source.NormalizedGuid;
            NormalizedName = source.NormalizedName;
            NormalizedTypeName = source.NormalizedTypeName;
            NormalizedBaseTypeNames = source.NormalizedBaseTypeNames;
            NormalizedUnityLabels = source.NormalizedUnityLabels;
            NormalizedUnityLabelSearchText = source.NormalizedUnityLabelSearchText;

            if (newAssetPath == null)
            {
                AssetPath = source.AssetPath;
                NormalizedAssetPath = source.NormalizedAssetPath;
                NormalizedExtension = source.NormalizedExtension;
                NormalizedTypeSearchText = source.NormalizedTypeSearchText;
            }
            else
            {
                AssetPath = newAssetPath;
                NormalizedAssetPath = NormalizePath(newAssetPath);
                NormalizedExtension = GetNormalizedExtension(NormalizedAssetPath);

                // The extension feeds the kind aliases, so the type search text is
                // path-derived too. Interned, so an unchanged extension costs a
                // dictionary probe rather than a fresh string.
                NormalizedTypeSearchText = BuildNormalizedTypeSearchText(
                    NormalizedTypeName,
                    NormalizedExtension,
                    NormalizedBaseTypeNames);
            }

            if (newTagSearchText == null)
            {
                TagSearchText = source.TagSearchText;
                NormalizedTagSearchText = source.NormalizedTagSearchText;
                NormalizedSearchText = source.NormalizedSearchText;
            }
            else
            {
                TagSearchText = newTagSearchText;
                NormalizedTagSearchText = NormalizeText(newTagSearchText);
                NormalizedSearchText = BuildNormalizedSearchText(
                    NormalizedName,
                    NormalizedTypeName,
                    NormalizedTagSearchText,
                    NormalizedUnityLabelSearchText);
            }
        }

        // The record this asset becomes when it is moved to newAssetPath.
        internal static AssetRecord WithAssetPath(AssetRecord source, string newAssetPath)
        {
            return new AssetRecord(source, newAssetPath ?? string.Empty, null);
        }

        // The record this asset becomes when its tag assignment changes.
        internal static AssetRecord WithTagSearchText(
            AssetRecord source,
            string newTagSearchText)
        {
            return new AssetRecord(source, null, newTagSearchText ?? string.Empty);
        }

        private static string InternTypeName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return TypeNameInterner.GetOrAdd(value, value);
        }

        internal string Guid { get; }

        public string AssetPath { get; }

        public string Name { get; }

        public string TypeName { get; }

        internal string TagSearchText { get; }

        internal IReadOnlyList<string> UnityLabels { get; }

        internal IReadOnlyList<string> BaseTypeNames { get; }

        internal string NormalizedGuid { get; }

        internal string NormalizedAssetPath { get; }

        internal string NormalizedName { get; }

        internal string NormalizedTypeName { get; }

        internal IReadOnlyList<string> NormalizedBaseTypeNames { get; }

        internal string NormalizedTypeSearchText { get; }

        internal string NormalizedTagSearchText { get; }

        internal string NormalizedExtension { get; }

        internal IReadOnlyList<string> NormalizedUnityLabels { get; }

        internal string NormalizedUnityLabelSearchText { get; }

        internal string NormalizedSearchText { get; }

        public override string ToString()
        {
            return $"{Name} ({AssetPath})";
        }

        public bool Equals(AssetRecord other)
        {
            if (ReferenceEquals(null, other))
            {
                return false;
            }

            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return string.Equals(Guid, other.Guid, StringComparison.Ordinal)
                && string.Equals(AssetPath, other.AssetPath, StringComparison.Ordinal)
                && string.Equals(Name, other.Name, StringComparison.Ordinal)
                && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
                && string.Equals(TagSearchText, other.TagSearchText, StringComparison.Ordinal)
                && ValuesEqual(UnityLabels, other.UnityLabels)
                && ValuesEqual(BaseTypeNames, other.BaseTypeNames);
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as AssetRecord);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Guid.GetHashCode();
                hashCode = (hashCode * 397) ^ AssetPath.GetHashCode();
                hashCode = (hashCode * 397) ^ Name.GetHashCode();
                hashCode = (hashCode * 397) ^ TypeName.GetHashCode();
                hashCode = (hashCode * 397) ^ TagSearchText.GetHashCode();
                for (var i = 0; i < UnityLabels.Count; i++)
                {
                    hashCode = (hashCode * 397) ^ UnityLabels[i].GetHashCode();
                }

                for (var i = 0; i < BaseTypeNames.Count; i++)
                {
                    hashCode = (hashCode * 397) ^ BaseTypeNames[i].GetHashCode();
                }

                return hashCode;
            }
        }

        // Deliberately EXCLUDES the asset path. An unqualified search should match an asset's
        // own NAME (plus its type and tags), not its parent folders — folding the path
        // in flooded results with every asset that merely LIVED under a folder whose name
        // matched the term (e.g. searching "plugins" returned everything under any Plugins/
        // directory). Explicit `path:` queries and folder scope still use NormalizedAssetPath
        // directly (via the separate path gram index), so path search remains available.
        //
        // The GUID is EXCLUDED for the same reason, since 2026-09-02. A 32-hex GUID
        // contributes ~31 bigrams and ~30 trigrams per record to the any-field gram
        // indices — the single largest slice of search memory (measured: 2.7 KB of
        // postings per record) — and it bought a result set nobody wanted: because
        // every record's text carried 31 hex bigrams, the two-character query "ab"
        // matched 43% of a 100k-record project. Unqualified GUID matching now lives in
        // AssetSearchService.MatchesToken as a >= 4-character PREFIX of NormalizedGuid;
        // `guid:` keeps full partial-substring behaviour through its own gram index.
        internal static string BuildNormalizedSearchText(
            string name,
            string typeName,
            string tags,
            string unityLabels)
        {
            var totalLength = name.Length
                + 1 + typeName.Length
                + 1 + tags.Length
                + 1 + unityLabels.Length;
            return string.Create(
                totalLength,
                (name, typeName, tags, unityLabels),
                static (span, state) =>
            {
                var offset = 0;
                state.name.AsSpan().CopyTo(span.Slice(offset));
                offset += state.name.Length;
                span[offset++] = '\n';
                state.typeName.AsSpan().CopyTo(span.Slice(offset));
                offset += state.typeName.Length;
                span[offset++] = '\n';
                state.tags.AsSpan().CopyTo(span.Slice(offset));
                offset += state.tags.Length;
                span[offset++] = '\n';
                state.unityLabels.AsSpan().CopyTo(span.Slice(offset));
            });
        }

        internal static string BuildNormalizedTypeSearchText(
            string typeName,
            string extension,
            IReadOnlyList<string> baseTypeNames = null)
        {
            var aliases = GetKindAliases(extension);
            var baseTypeLength = 0;
            if (baseTypeNames != null)
            {
                for (var i = 0; i < baseTypeNames.Count; i++)
                {
                    baseTypeLength += 1 + baseTypeNames[i].Length;
                }
            }

            var builder = new StringBuilder(
                aliases.Length + typeName.Length + baseTypeLength + 16);
            if (!string.IsNullOrEmpty(aliases))
            {
                builder.Append(aliases).Append('\n');
            }

            builder.Append(typeName);
            if (baseTypeNames != null)
            {
                for (var i = 0; i < baseTypeNames.Count; i++)
                {
                    builder.Append('\n').Append(baseTypeNames[i]);
                }
            }

            var built = builder.Append("\nfile\nobject").ToString();
            return TypeSearchTextInterner.GetOrAdd(built, built);
        }

        private static string GetKindAliases(string extension)
        {
            switch (extension)
            {
                case "prefab":
                    return "prefab";
                case "fbx":
                case "obj":
                case "blend":
                case "dae":
                case "3ds":
                case "dxf":
                case "skp":
                    return "model";
                case "unity":
                    return "scene";
                case "cs":
                    return "script";
                case "png":
                case "jpg":
                case "jpeg":
                case "tga":
                case "bmp":
                case "tif":
                case "tiff":
                case "psd":
                case "gif":
                case "exr":
                case "hdr":
                    return "image";
                case "wav":
                case "mp3":
                case "ogg":
                case "aiff":
                case "aif":
                case "mod":
                case "it":
                case "s3m":
                case "xm":
                    return "audio";
                case "mp4":
                case "mov":
                case "webm":
                case "m4v":
                case "avi":
                    return "video";
                default:
                    return string.Empty;
            }
        }

        internal static string GetNormalizedExtension(string normalizedAssetPath)
        {
            var dot = normalizedAssetPath.LastIndexOf('.');
            var slash = normalizedAssetPath.LastIndexOf('/');
            return dot > slash && dot < normalizedAssetPath.Length - 1
                ? normalizedAssetPath.Substring(dot + 1)
                : string.Empty;
        }

        private static string[] CopyNonEmpty(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
            {
                return Array.Empty<string>();
            }

            var count = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    count++;
                }
            }

            if (count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[count];
            var writeIndex = 0;
            for (var i = 0; i < values.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    result[writeIndex++] = values[i].Trim();
                }
            }

            return result;
        }

        private static string[] NormalizeLabels(IReadOnlyList<string> labels)
        {
            if (labels == null || labels.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[labels.Count];
            for (var i = 0; i < labels.Count; i++)
            {
                result[i] = NormalizeText(labels[i]);
            }

            return result;
        }

        private static string[] NormalizeTypeNames(IReadOnlyList<string> typeNames)
        {
            if (typeNames == null || typeNames.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new string[typeNames.Count];
            for (var i = 0; i < typeNames.Count; i++)
            {
                result[i] = InternTypeName(NormalizeText(typeNames[i]));
            }

            return result;
        }

        private static bool ValuesEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        internal static string NormalizeText(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            // One scan answers both questions. Most normalisation input is already
            // ASCII lower-case (GUIDs are lower-case 32-hex, most asset paths and
            // names are typed in lower case), and for that input the normalised
            // value IS the input - returning the same instance makes NormalizedGuid
            // alias Guid and NormalizedAssetPath alias AssetPath instead of doubling
            // the string count of a 100k-record catalog.
            var isAscii = true;
            var needsLower = false;
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                if (current > 0x7F)
                {
                    isAscii = false;
                    break;
                }

                // ToLowerInvariant changes exactly 'A'-'Z' within ASCII.
                if (current >= 'A' && current <= 'Z')
                {
                    needsLower = true;
                }
            }

            if (isAscii)
            {
                if (!needsLower)
                {
                    return value;
                }

                return string.Create(value.Length, value, static (span, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        span[i] = char.ToLowerInvariant(source[i]);
                    }
                });
            }

            var canonical = value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
            return canonical.ToLowerInvariant();
        }

        internal static string NormalizePath(string value)
        {
            var normalized = NormalizeText(value);
            if (normalized.IndexOf('\\') < 0)
            {
                // Carries NormalizeText's aliasing through: an already lower-case
                // forward-slash path normalises to the very instance passed in.
                return normalized;
            }

            return string.Create(normalized.Length, normalized, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    span[i] = source[i] == '\\' ? '/' : source[i];
                }
            });
        }
    }
}
