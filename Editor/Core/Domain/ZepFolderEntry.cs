using System;
using System.Collections.Generic;
using System.Text;

namespace Zep.Core.Domain
{
    public sealed class ZepFolderEntry
    {
        public ZepFolderEntry(
            string name,
            string path,
            int depth,
            bool isReadOnly,
            string guid = "",
            IReadOnlyList<string> unityLabels = null)
        {
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Depth = depth < 0 ? 0 : depth;
            IsReadOnly = isReadOnly;
            Guid = guid ?? string.Empty;
            UnityLabels = CopyNonEmpty(unityLabels);
            // Pre-normalized for FolderMatchesSearch — the per-keystroke search
            // path used to allocate a fresh lowercase copy of every folder's
            // name/path on every call. Computing once at construction keeps the
            // search loop allocation-free.
            NormalizedName = Normalize(Name);
            NormalizedPath = Normalize(Path);
            NormalizedGuid = Normalize(Guid);
            NormalizedUnityLabels = NormalizeLabels(UnityLabels);
            NormalizedUnityLabelSearchText = string.Join("\n", NormalizedUnityLabels);
        }

        public string Name { get; }

        public string Path { get; }

        public int Depth { get; }

        internal bool IsReadOnly { get; }

        internal string Guid { get; }

        internal IReadOnlyList<string> UnityLabels { get; }

        internal string NormalizedName { get; }

        internal string NormalizedPath { get; }

        internal string NormalizedGuid { get; }

        internal IReadOnlyList<string> NormalizedUnityLabels { get; }

        internal string NormalizedUnityLabelSearchText { get; }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var isAscii = true;
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                {
                    isAscii = false;
                    break;
                }
            }

            if (isAscii)
            {
                return string.Create(value.Length, value, static (span, src) =>
                {
                    for (var i = 0; i < src.Length; i++)
                    {
                        span[i] = char.ToLowerInvariant(src[i]);
                    }
                });
            }

            var canonical = value.IsNormalized(NormalizationForm.FormC)
                ? value
                : value.Normalize(NormalizationForm.FormC);
            return canonical.ToLowerInvariant();
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
                result[i] = Normalize(labels[i]);
            }

            return result;
        }
    }
}
