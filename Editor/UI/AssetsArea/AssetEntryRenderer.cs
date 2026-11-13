using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Editor.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    // Static rendering helpers shared by ListAssetWidget and GridAssetWidget.
    // All methods are pure or depend only on their parameters; no shared mutable state.
    internal static class AssetEntryRenderer
    {

        private static readonly Dictionary<string, string> TypeDisplayNameOverrides =
            new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                { "MonoScript", "C# Script" },
                { "SceneAsset", "Scene" },
                { "Texture2D", "Texture" },
                { "AudioClip", "Audio" },
            };

        // Ghost (inline-create) icon resolution: ghost paths don't resolve on disk,
        // so AssetDatabase-based lookups return null. Try multiple strategies based
        // on the ghost's display name extension + type name, using Unity's built-in
        // icon names. Keyed by the lowercase extension (e.g. ".cs", ".mat").
        private static readonly Dictionary<string, string> GhostIconBuiltinByExtension =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { ".cs", "cs Script Icon" },
                { ".shader", "Shader Icon" },
                { ".compute", "ComputeShader Icon" },
                { ".raytrace", "RayTracingShader Icon" },
                { ".asmdef", "AssemblyDefinitionAsset Icon" },
                { ".asmref", "AssemblyDefinitionReferenceAsset Icon" },
                { ".mat", "Material Icon" },
                { ".physicMaterial", "PhysicMaterial Icon" },
                { ".physicsMaterial2D", "PhysicsMaterial2D Icon" },
                { ".asset", "ScriptableObject Icon" },
                { ".unity", "SceneAsset Icon" },
                { ".prefab", "Prefab Icon" },
                { ".anim", "AnimationClip Icon" },
                { ".controller", "AnimatorController Icon" },
                { ".overrideController", "AnimatorOverrideController Icon" },
                { ".mask", "AvatarMask Icon" },
                { ".playable", "PlayableAsset Icon" },
                { ".signal", "SignalAsset Icon" },
                { ".txt", "TextAsset Icon" },
                { ".json", "TextAsset Icon" },
                { ".xml", "TextAsset Icon" },
                { ".md", "TextAsset Icon" },
                { ".uss", "StyleSheet Icon" },
                { ".uxml", "VisualTreeAsset Icon" },
                { ".preset", "Preset Icon" },
            };

        // The built-in tables those names index are fixed for the life of the
        // skin, so the same ghost resolves to the same texture on every bind —
        // and a ghost row rebinds on every keystroke while its name is typed.
        // The skin flag is part of the key rather than a reason to clear the
        // memo: the light and dark answers (`d_` textures) are both worth
        // keeping across a flip. The key space is the set of types inline-create
        // can produce, so there is nothing to evict.
        private static readonly Dictionary<(string TypeName, string Extension, bool ProSkin), Texture2D>
            GhostBuiltinIconCache =
                new Dictionary<(string TypeName, string Extension, bool ProSkin), Texture2D>();

        // One increment per pass through the built-in lookup, i.e. per memo miss.
        internal static int GhostIconLookupsForTests;

        // Both format caches are keyed by a value the user is looking at right
        // now, so the working set is the rows on screen. Clearing wholesale at
        // the cap threw that working set away and made the whole visible list
        // re-format on the next bind; dropping the oldest quarter keeps it.
        // Insertion order (not last-use) is the eviction order: the queues below
        // are the only ordering, and re-ordering them per cache hit would put
        // bookkeeping back on the bind path these caches exist to keep cheap.
        internal const int FormatCacheCapacity = 5000;
        private const int FormatCacheEvictionCount = FormatCacheCapacity / 4;

        internal static readonly Dictionary<DateTime, string> FormattedDateCache =
            new Dictionary<DateTime, string>();

        private static readonly Queue<DateTime> FormattedDateOrder = new Queue<DateTime>();

        internal static readonly Dictionary<long, string> FormattedSizeCache =
            new Dictionary<long, string>();

        private static readonly Queue<long> FormattedSizeOrder = new Queue<long>();

        private static readonly string[] ByteSuffixes = { "KB", "MB", "GB", "TB" };

        internal static Texture2D ResolveAssetIcon(
            UnityZepIconRepository iconRepository,
            ZepAssetEntry asset)
        {
            if (asset == null)
            {
                return iconRepository.GetAssetIcon(null, null);
            }

            // Ghost rows (inline-create placeholders) have a sentinel path that doesn't
            // resolve on disk. Run a layered lookup so the user always sees a proper
            // glyph (C# script, Material, Folder, ...) instead of an empty cell.
            if (string.Equals(asset.Path, AssetsAreaInlineEditRequest.GhostPath, StringComparison.Ordinal))
            {
                return ResolveGhostIcon(iconRepository, asset) ?? iconRepository.GetAssetIcon(null, null);
            }

            // Custom material thumbnails are scoped to the assets view: this
            // resolver is only called by the grid/list widgets, which makes it
            // the scope boundary — the thumb must not enter the icon
            // repository's shared cache (Quick Look reads that and keeps the
            // stock look by design).
            var materialThumb = MaterialThumbnails.MaterialThumbnailProvider.TryGet(
                asset.Path, asset.TypeName, out var pendingCustomThumb);
            if (materialThumb != null)
            {
                return materialThumb;
            }

            if (pendingCustomThumb)
            {
                // A custom thumb is rendering (or warm-loading): show the
                // instant mini icon but keep the stock AssetPreview
                // upgrade out of the repo's queue — it would load the
                // material and its textures to render a sphere the swap
                // will immediately replace.
                return iconRepository.GetAssetIcon(asset.Path, asset.TypeName, allowPreviewUpgrade: false);
            }

            return iconRepository.GetAssetIcon(asset.Path, asset.TypeName);
        }

        private static Texture2D ResolveGhostIcon(
            UnityZepIconRepository iconRepository,
            ZepAssetEntry asset)
        {
            if (string.Equals(asset.TypeName, "Folder", StringComparison.OrdinalIgnoreCase))
            {
                return iconRepository.GetFolderIcon(string.Empty);
            }

            // Strategy 1: repository's type-icon lookup (uses cached project samples
            // when available — matches the icon that real assets of this type show).
            var typeIcon = iconRepository.GetTypeIcon(asset.TypeName);
            if (typeIcon != null) return typeIcon;

            var typeName = asset.TypeName ?? string.Empty;
            var extension = Path.GetExtension(asset.Name ?? string.Empty) ?? string.Empty;
            var key = (typeName, extension, EditorGUIUtility.isProSkin);
            if (GhostBuiltinIconCache.TryGetValue(key, out var cachedBuiltin))
            {
                return cachedBuiltin;
            }

            var resolvedBuiltin = LookUpGhostBuiltinIcon(typeName, extension);
            GhostBuiltinIconCache[key] = resolvedBuiltin;
            return resolvedBuiltin;
        }

        // Strategies 2-4: Unity's built-in icon tables. Their answer depends only
        // on the type name, the extension and the skin, so the caller memoizes it
        // — the ghost row rebinds on every keystroke while its name is typed.
        private static Texture2D LookUpGhostBuiltinIcon(string typeName, string extension)
        {
            GhostIconLookupsForTests++;

            // Strategy 2: built-in Unity icon name by extension. `FindTexture` handles
            // the dark-skin `d_` prefix automatically.
            if (!string.IsNullOrEmpty(extension)
                && GhostIconBuiltinByExtension.TryGetValue(extension, out var iconName))
            {
                var builtin = EditorGUIUtility.FindTexture(iconName);
                if (builtin != null) return builtin;

                var iconContent = EditorGUIUtility.IconContent(iconName);
                if (iconContent?.image is Texture2D iconTexture) return iconTexture;
            }

            // Strategy 3: built-in from the TypeName itself (e.g. "Material Icon").
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                var typeNamed = EditorGUIUtility.FindTexture(typeName + " Icon");
                if (typeNamed != null) return typeNamed;
            }

            // Strategy 4: Unity's generic default-asset glyph — better than nothing.
            return EditorGUIUtility.FindTexture("DefaultAsset Icon");
        }

        internal static void ApplyFolderIconTint(VisualElement iconImage, ZepAssetEntry entry)
        {
            if (iconImage == null)
                return;

            var isFolder = entry != null
                && string.Equals(entry.TypeName, "Folder", System.StringComparison.OrdinalIgnoreCase);
            var tintColor = isFolder
                ? entry.FolderTintColor
                : ZepTagColor.None;

            ApplyFolderIconTint(iconImage, isFolder, tintColor);
        }

        // The one owner of the tinted-vs-themed dance: the tag color is an
        // inline write and the theme tint is the zep-themed-folder-icon class,
        // so the two must be swapped together and the inline value cleared
        // (StyleKeyword.Null) when the class takes back over — see
        // styling-standards §4. Also used by the sidebar's Favorites rows so a
        // tagged folder reads identically in both places.
        internal static void ApplyFolderIconTint(
            VisualElement iconImage,
            bool isFolder,
            ZepTagColor tintColor)
        {
            if (iconImage == null)
                return;

            if (isFolder && tintColor != ZepTagColor.None)
            {
                iconImage.EnableInClassList(ZepUiClasses.ThemedFolderIcon, false);
                var target = (Color)ZepTagColorPalette.GetColor(tintColor);
                var current = iconImage.style.unityBackgroundImageTintColor;
                if (current.keyword == StyleKeyword.Null || current.value != target)
                {
                    iconImage.style.unityBackgroundImageTintColor = new StyleColor(target);
                }

                return;
            }

            iconImage.EnableInClassList(ZepUiClasses.ThemedFolderIcon, isFolder);
            if (iconImage.style.unityBackgroundImageTintColor.keyword != StyleKeyword.Null)
            {
                iconImage.style.unityBackgroundImageTintColor = StyleKeyword.Null;
            }
        }

        internal static void RebuildTagBadges(
            VisualElement container,
            IReadOnlyList<ZepTag> tags,
            bool includeNames,
            int maxVisibleTags)
        {
            if (container == null)
            {
                return;
            }

            var tagCount = tags?.Count ?? 0;
            container.style.display = tagCount > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (tagCount == 0)
            {
                for (var i = 0; i < container.childCount; i++)
                {
                    container[i].style.display = DisplayStyle.None;
                }
                return;
            }

            var visibleCount = maxVisibleTags <= 0
                ? tagCount
                : Mathf.Min(tagCount, maxVisibleTags);

            var hasOverflow = visibleCount < tagCount;
            var totalNeeded = visibleCount + (hasOverflow ? 1 : 0);

            while (container.childCount < totalNeeded)
            {
                var chip = new VisualElement();
                chip.AddToClassList(ZepUiClasses.RowChip);
                chip.AddToClassList(ZepUiClasses.TagChip);
                container.Add(chip);
            }

            for (var i = 0; i < visibleCount; i++)
            {
                var element = container[i];
                if (element is Label || !element.ClassListContains(ZepUiClasses.TagChip))
                {
                    var chip = new VisualElement();
                    chip.AddToClassList(ZepUiClasses.RowChip);
                    chip.AddToClassList(ZepUiClasses.TagChip);
                    container.RemoveAt(i);
                    container.Insert(i, chip);
                    element = chip;
                }
                UpdateTagChip(element, tags[i], includeNames);
            }

            if (hasOverflow)
            {
                var overflowIndex = visibleCount;
                var element = container[overflowIndex];
                var overflowLabel = element as Label;
                if (overflowLabel == null)
                {
                    overflowLabel = new Label();
                    overflowLabel.name = "zep-tag-chip-overflow";
                    overflowLabel.AddToClassList(ZepUiClasses.RowChip);
                    container.RemoveAt(overflowIndex);
                    container.Insert(overflowIndex, overflowLabel);
                }
                overflowLabel.text = "+" + (tagCount - visibleCount);
                overflowLabel.style.display = DisplayStyle.Flex;
            }

            for (var i = totalNeeded; i < container.childCount; i++)
            {
                container[i].style.display = DisplayStyle.None;
            }
        }

        // Stable hash of a tag list, used by row binders to skip RebuildTagBadges
        // when the tag set hasn't changed between binds. Returns 0 for null/empty
        // so the caller can compare to a default-initialized int field cheaply.
        internal static int ComputeTagSignature(IReadOnlyList<ZepTag> tags, bool includeNames, int maxVisibleTags)
        {
            var tagCount = tags?.Count ?? 0;
            if (tagCount == 0)
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                hash = (hash * 31) + tagCount;
                hash = (hash * 31) + (includeNames ? 1 : 0);
                hash = (hash * 31) + maxVisibleTags;
                for (var i = 0; i < tagCount; i++)
                {
                    var tag = tags[i];
                    hash = (hash * 31) + (tag.Id == null ? 0 : StringComparer.Ordinal.GetHashCode(tag.Id));
                    hash = (hash * 31) + (int)tag.Color;
                    if (includeNames)
                    {
                        hash = (hash * 31) + (tag.Name == null ? 0 : StringComparer.Ordinal.GetHashCode(tag.Name));
                    }
                }
                // Avoid the sentinel 0 (treated as "no tags") for non-empty inputs.
                return hash == 0 ? 1 : hash;
            }
        }

        internal static string ResolveTypeDisplayName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return string.Empty;
            }

            return TypeDisplayNameOverrides.TryGetValue(typeName, out var display)
                ? display
                : typeName;
        }

        internal static string FormatModifiedDate(DateTime? modifiedDate)
        {
            if (!modifiedDate.HasValue)
                return string.Empty;

            var val = modifiedDate.Value;
            if (FormattedDateCache.TryGetValue(val, out var cached))
                return cached;

            var result = val.ToString("MMM d, yyyy HH:mm");
            StoreFormatted(FormattedDateCache, FormattedDateOrder, val, result);
            return result;
        }

        internal static string FormatSize(long? size)
        {
            if (!size.HasValue)
                return string.Empty;

            var val = size.Value;
            if (FormattedSizeCache.TryGetValue(val, out var cached))
                return cached;

            var result = FormatBytes(val);
            StoreFormatted(FormattedSizeCache, FormattedSizeOrder, val, result);
            return result;
        }

        // Cache and insertion order have to be dropped together or the queue
        // starts naming keys the dictionary no longer holds and eviction stops
        // freeing anything. Only the eviction tests need this today.
        internal static void ClearFormatCaches()
        {
            FormattedDateCache.Clear();
            FormattedDateOrder.Clear();
            FormattedSizeCache.Clear();
            FormattedSizeOrder.Clear();
        }

        // The caller has already established that `key` is absent, so the queue
        // never holds a duplicate and its head is always the oldest live key.
        private static void StoreFormatted<TKey>(
            Dictionary<TKey, string> cache,
            Queue<TKey> insertionOrder,
            TKey key,
            string formatted)
        {
            if (cache.Count >= FormatCacheCapacity)
            {
                for (var i = 0; i < FormatCacheEvictionCount && insertionOrder.Count > 0; i++)
                {
                    cache.Remove(insertionOrder.Dequeue());
                }
            }

            cache[key] = formatted;
            insertionOrder.Enqueue(key);
        }

        private static string FormatBytes(long byteCount)
        {
            if (byteCount < 0)
            {
                return string.Empty;
            }

            if (byteCount < 1024)
            {
                return byteCount + " B";
            }

            var suffixes = ByteSuffixes;
            var value = byteCount;
            var suffixIndex = -1;
            while (value >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                value /= 1024;
                suffixIndex++;
            }

            if (suffixIndex <= 0)
            {
                var kbValue = byteCount / 1024d;
                return kbValue >= 10d
                    ? $"{Math.Round(kbValue):0} KB"
                    : $"{kbValue:0.#} KB";
            }

            var divisor = Math.Pow(1024d, suffixIndex + 1);
            var scaledValue = byteCount / divisor;
            return scaledValue >= 10d
                ? $"{Math.Round(scaledValue):0} {suffixes[suffixIndex]}"
                : $"{scaledValue:0.#} {suffixes[suffixIndex]}";
        }

        internal static ZepAssetEntry ResolveAssetEntryUserData(object userData)
        {
            switch (userData)
            {
                case ZepAssetEntry direct:
                    return direct;
                case ListAssetWidget.AssetRowParts rowParts:
                    return rowParts.Entry;
                case GridAssetWidget.AssetGridItemParts gridParts:
                    return gridParts.Entry;
                default:
                    return null;
            }
        }

        // Walk target's ancestor chain (inclusive of target itself), returning the
        // nearest ZepAssetEntry resolved from element.userData. stopAt
        // terminates the walk *before* visiting that element (pass null to walk to
        // the document root). Returns null when no ancestor carries an entry.
        internal static ZepAssetEntry FindAssetEntryInAncestors(
            VisualElement target,
            VisualElement stopAt)
        {
            var current = target;
            while (current != null && current != stopAt)
            {
                var entry = ResolveAssetEntryUserData(current.userData);
                if (entry != null) return entry;
                current = current.parent;
            }
            return null;
        }

        // The list rows' and grid tiles' sub-asset disclosure chevron. The
        // textures and their paths belong to ZepChevronIcons, which
        // the sidebar's chevrons share; this keeps the assets-area name the
        // widgets bind against.
        internal static StyleBackground GetDisclosureChevron(bool expanded)
        {
            return ZepChevronIcons.GetChevronBackground(expanded);
        }

        // Deliberately nameless: chip / dot / label are styled by class and
        // reached through their parent, never by `#id` or a UQuery on a name.
        // Stamping a per-tag name here interpolated three strings per chip on
        // every badge rebuild, and each `name` write also re-runs style
        // matching on the element (id selectors participate) — pure cost for
        // an identity nothing reads.
        private static void UpdateTagChip(VisualElement chip, ZepTag tag, bool includeName)
        {
            chip.style.display = DisplayStyle.Flex;

            VisualElement dot;
            if (chip.childCount > 0)
            {
                dot = chip[0];
            }
            else
            {
                dot = new VisualElement();
                dot.AddToClassList(ZepUiClasses.TagColorDot);
                chip.Add(dot);
            }
            dot.style.backgroundColor = new StyleColor(ZepTagColorPalette.GetColor(tag.Color));

            if (includeName)
            {
                Label label;
                if (chip.childCount > 1)
                {
                    label = chip[1] as Label;
                    if (label == null)
                    {
                        chip.RemoveAt(1);
                        label = new Label();
                        label.AddToClassList(ZepUiClasses.TagChipLabel);
                        chip.Add(label);
                    }
                    else
                    {
                        // Clear (don't stamp Flex): a persistent inline display
                        // would beat the narrow-layout USS rule that hides chip
                        // labels, and fresh labels carry no inline value either.
                        label.style.display = StyleKeyword.Null;
                    }
                }
                else
                {
                    label = new Label();
                    label.AddToClassList(ZepUiClasses.TagChipLabel);
                    chip.Add(label);
                }
                label.text = tag.Name;
            }
            else
            {
                if (chip.childCount > 1)
                {
                    chip[1].style.display = DisplayStyle.None;
                }
            }
        }

    }
}
