using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Zep.Core.Domain;

namespace Zep.Core.State
{
    // Owns the project's tag definitions. The backing list is supplied by the
    // caller (Bridge's ScriptableSingleton holds the serialized field, and we
    // mutate that list directly so Unity's next Save() picks up the changes).
    // The `save` callback flushes after every mutation; Bridge wires it to
    // ScriptableSingleton.Save(true) and tests pass a no-op. `reportProblem`
    // is the matching sink for corrupt definitions found at load: Core cannot
    // reach the editor console and does not know the settings file's path, so
    // Bridge supplies both. Optional — a store with no backing file (the
    // controller's undo-snapshot store) has nothing to name.
    public sealed class TagDefinitionStore
    {
        private readonly List<TagDefinitionRecord> _definitions;
        private readonly Action _save;
        private readonly Action<string> _reportProblem;
        // Sanitization runs once per store instance: the backing list only
        // needs distrust on first contact (freshly deserialized data), and
        // every store-driven mutation keeps the invariants by construction.
        // Re-verifying on every call made EnsureDefaults a per-record
        // allocation source during index scans (StringBuilder + string per
        // definition, per asset, on parallel workers).
        private bool _defaultsVerified;
        // GetOrderedTags result, invalidated by every mutation. Rebuilding the
        // array per call ran LINQ once per tagged record per projection
        // rebuild and once per RefreshView keystroke via PushTagsState.
        private ZepTag[] _orderedTagsCache;

        public TagDefinitionStore(
            List<TagDefinitionRecord> definitions,
            Action save,
            Action<string> reportProblem = null)
        {
            _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
            _save = save ?? throw new ArgumentNullException(nameof(save));
            _reportProblem = reportProblem;
        }

        public void EnsureDefaults()
        {
            if (_defaultsVerified)
            {
                return;
            }

            var original = CopyRecords(_definitions);
            var originalCache = _orderedTagsCache;
            try
            {
                _defaultsVerified = true;
                if (_definitions.Count == 0)
                {
                    _definitions.AddRange(CreateDefaultTags());
                    _orderedTagsCache = null;
                    _save();
                    return;
                }

                // Same policy as ReplaceAll: an id IS the identity a GUID's tag
                // assignment points at, so a malformed or duplicate record is
                // dropped, never repaired. Inventing an id, or renaming a
                // second `red` to `red-2`, silently re-points every existing
                // `red` assignment at the first record — and the old code then
                // SAVED that guess over the user's file. Nothing here writes:
                // the file stays the user's artifact until a real edit saves.
                var kept = new List<TagDefinitionRecord>(_definitions.Count);
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < _definitions.Count; i++)
                {
                    var entry = _definitions[i];
                    if (entry == null)
                    {
                        ReportDiscardedDefinition(i, "the record is missing");
                        continue;
                    }

                    var id = entry.Id ?? string.Empty;
                    if (id.Length == 0)
                    {
                        ReportDiscardedDefinition(i, "the record has no id");
                        continue;
                    }

                    if (!string.Equals(id, SanitizeId(id), StringComparison.Ordinal))
                    {
                        ReportDiscardedDefinition(i, $"'{id}' is not a usable tag id");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Name))
                    {
                        ReportDiscardedDefinition(i, $"tag '{id}' has no name");
                        continue;
                    }

                    if (!seenIds.Add(id))
                    {
                        ReportDiscardedDefinition(
                            i,
                            $"a second record claims the id '{id}'");
                        continue;
                    }

                    // Presentation, not identity: trimming a name and deriving a
                    // missing color from the id cannot re-point an assignment,
                    // and neither is written back to the file.
                    entry.Name = entry.Name.Trim();
                    if (entry.Color == ZepTagColor.None)
                    {
                        entry.Color = GuessFallbackColor(entry.Id, i);
                    }

                    kept.Add(entry);
                }

                _orderedTagsCache = null;
                if (kept.Count == _definitions.Count)
                {
                    return;
                }

                _definitions.Clear();
                _definitions.AddRange(kept);
            }
            catch
            {
                _definitions.Clear();
                _definitions.AddRange(original);
                _defaultsVerified = false;
                _orderedTagsCache = originalCache;
                throw;
            }
        }

        private void ReportDiscardedDefinition(int index, string reason)
        {
            _reportProblem?.Invoke(
                $"Discarded tag definition #{index + 1} — {reason}.");
        }

        public IReadOnlyList<ZepTag> GetOrderedTags()
        {
            EnsureDefaults();
            return _orderedTagsCache ??= _definitions
                .Where(static entry => entry != null)
                .Select(static entry => new ZepTag(entry.Id, entry.Name, entry.Color, entry.IsFavorite))
                .ToArray();
        }

        internal bool TryGetTag(string tagId, out ZepTag tag)
        {
            EnsureDefaults();
            // Existing IDs are identities, not names to turn into slugs.
            var normalizedId = tagId?.Trim().ToLowerInvariant() ?? string.Empty;
            for (var i = 0; i < _definitions.Count; i++)
            {
                var entry = _definitions[i];
                if (entry == null || !string.Equals(entry.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tag = new ZepTag(entry.Id, entry.Name, entry.Color, entry.IsFavorite);
                return true;
            }

            tag = default;
            return false;
        }

        internal IReadOnlyList<ZepTag> ResolveAssignedTags(IEnumerable<string> tagIds)
        {
            // Resolve the requested ids before touching the definition list so
            // an empty request returns without the EnsureDefaults/ordered-tags
            // work (GetOrderedTags runs EnsureDefaults itself; the previous
            // extra direct call doubled the verification pass per record).
            // A list, not a HashSet: an asset carries a handful of tags, so the
            // set's buckets+slots pair cost more than the linear Remove over
            // three entries it replaces. This resolves per tagged record, per
            // index scan and per projection rebuild.
            var requestedIds = CollectDistinctNormalizedIds(tagIds);
            if (requestedIds.Count == 0)
            {
                return Array.Empty<ZepTag>();
            }

            var orderedTags = GetOrderedTags();
            var resolvedTags = new List<ZepTag>(requestedIds.Count);
            for (var i = 0; i < orderedTags.Count && requestedIds.Count > 0; i++)
            {
                if (RemoveId(requestedIds, orderedTags[i].Id))
                {
                    resolvedTags.Add(orderedTags[i]);
                }
            }

            // What survives the loop is the unknown ids, and they are almost
            // always none. The sort is in place and only when something is left:
            // OrderBy ran — and allocated its buffer and comparer chain — on
            // every well-formed record before. The ids are already deduplicated
            // OrdinalIgnoreCase, so no two compare equal and Sort's instability
            // cannot reorder them.
            if (requestedIds.Count > 0)
            {
                requestedIds.Sort(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < requestedIds.Count; i++)
                {
                    var unknownId = requestedIds[i];
                    resolvedTags.Add(new ZepTag(
                        unknownId,
                        BuildFallbackName(unknownId, ZepTagColor.Gray, resolvedTags.Count),
                        GuessFallbackColor(unknownId, resolvedTags.Count),
                        false));
                }
            }

            return resolvedTags;
        }

        private static List<string> CollectDistinctNormalizedIds(IEnumerable<string> tagIds)
        {
            var distinctIds = new List<string>();
            if (tagIds == null)
            {
                return distinctIds;
            }

            foreach (var tagId in tagIds)
            {
                var normalizedId = tagId?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(normalizedId)
                    && IndexOfId(distinctIds, normalizedId) < 0)
                {
                    distinctIds.Add(normalizedId);
                }
            }

            return distinctIds;
        }

        private static bool RemoveId(List<string> ids, string id)
        {
            var index = IndexOfId(ids, id);
            if (index < 0)
            {
                return false;
            }

            ids.RemoveAt(index);
            return true;
        }

        private static int IndexOfId(List<string> ids, string id)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                if (string.Equals(ids[i], id, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        public string BuildTagSearchText(IEnumerable<string> tagIds)
        {
            // Per-record hot path during index scans: the dominant case is an
            // untagged asset, whose GetTagIds is a cached string[] or
            // Array.Empty. It must not pay the resolve pipeline at all.
            if (tagIds == null || (tagIds is ICollection<string> tagIdCollection && tagIdCollection.Count == 0))
            {
                return string.Empty;
            }

            var assignedTags = ResolveAssignedTags(tagIds);
            if (assignedTags.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>(assignedTags.Count * 2);
            for (var i = 0; i < assignedTags.Count; i++)
            {
                var tag = assignedTags[i];
                if (!string.IsNullOrWhiteSpace(tag.Name))
                {
                    parts.Add(tag.Name);
                }

                var colorName = tag.Color.ToString();
                if (!string.IsNullOrWhiteSpace(colorName) && !string.Equals(colorName, ZepTagColor.None.ToString(), StringComparison.Ordinal))
                {
                    parts.Add(colorName);
                }
            }

            // Newline boundaries preserve individual tag names and colors for exact
            // `tag=` search while partial tag search still treats the text as one field.
            return string.Join("\n", parts);
        }

        internal bool TryCreateTag(string tagName, ZepTagColor color, out ZepTag tag, out string errorMessage)
        {
            EnsureDefaults();

            var normalizedName = string.IsNullOrWhiteSpace(tagName)
                ? string.Empty
                : tagName.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                tag = default;
                errorMessage = "Tag name must be provided.";
                return false;
            }

            for (var i = 0; i < _definitions.Count; i++)
            {
                var existing = _definitions[i];
                if (existing == null)
                {
                    continue;
                }

                if (string.Equals(existing.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    tag = default;
                    errorMessage = "A tag with that name already exists.";
                    return false;
                }
            }

            var baseId = SanitizeId(normalizedName);
            if (string.IsNullOrWhiteSpace(baseId))
            {
                baseId = "tag";
            }

            var seenIds = new HashSet<string>(
                _definitions
                    .Where(static entry => entry != null)
                    .Select(static entry => entry.Id ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);
            var uniqueId = BuildUniqueId(baseId, seenIds);
            var resolvedColor = color == ZepTagColor.None
                ? GuessFallbackColor(uniqueId, _definitions.Count)
                : color;

            var record = new TagDefinitionRecord
            {
                Id = uniqueId,
                Name = normalizedName,
                Color = resolvedColor,
                IsFavorite = true
            };

            _definitions.Add(record);
            _orderedTagsCache = null;
            _save();

            tag = new ZepTag(record.Id, record.Name, record.Color, record.IsFavorite);
            errorMessage = string.Empty;
            return true;
        }

        internal bool TryUpdateTag(
            string tagId,
            string tagName,
            ZepTagColor color,
            bool isFavorite,
            out ZepTag tag,
            out string errorMessage)
        {
            EnsureDefaults();

            var normalizedId = tagId?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                tag = default;
                errorMessage = "Tag could not be identified.";
                return false;
            }

            var normalizedName = string.IsNullOrWhiteSpace(tagName)
                ? string.Empty
                : tagName.Trim();
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                tag = default;
                errorMessage = "Tag name must be provided.";
                return false;
            }

            TagDefinitionRecord target = null;
            for (var i = 0; i < _definitions.Count; i++)
            {
                var existing = _definitions[i];
                if (existing != null && string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    target = existing;
                    break;
                }
            }

            if (target == null)
            {
                tag = default;
                errorMessage = "Tag could not be found.";
                return false;
            }

            // Only reject a name collision when this edit actually CHANGES the name into one
            // another tag already uses. Checking unconditionally made a tag uneditable when a
            // pre-existing duplicate name existed (EnsureDefaults dedupes ids, not names) —
            // editing its color/favorite failed because the other same-named tag "already
            // exists". Renaming INTO a collision is still blocked.
            if (!string.Equals(target.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                for (var i = 0; i < _definitions.Count; i++)
                {
                    var existing = _definitions[i];
                    if (existing == null)
                    {
                        continue;
                    }

                    if (!string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(existing.Name, normalizedName, StringComparison.OrdinalIgnoreCase))
                    {
                        tag = default;
                        errorMessage = "A tag with that name already exists.";
                        return false;
                    }
                }
            }

            target.Name = normalizedName;
            target.Color = color == ZepTagColor.None
                ? GuessFallbackColor(target.Id, _definitions.IndexOf(target))
                : color;
            target.IsFavorite = isFavorite;
            _orderedTagsCache = null;
            _save();

            tag = new ZepTag(target.Id, target.Name, target.Color, target.IsFavorite);
            errorMessage = string.Empty;
            return true;
        }

        internal bool TryDeleteTag(string tagId, out string errorMessage)
        {
            EnsureDefaults();

            var normalizedId = tagId?.Trim().ToLowerInvariant() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                errorMessage = "Tag could not be identified.";
                return false;
            }

            for (var i = 0; i < _definitions.Count; i++)
            {
                var existing = _definitions[i];
                if (existing == null || !string.Equals(existing.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (_definitions.Count <= 1)
                {
                    errorMessage = "At least one tag must remain.";
                    return false;
                }

                _definitions.RemoveAt(i);
                _orderedTagsCache = null;
                _save();
                errorMessage = string.Empty;
                return true;
            }

            errorMessage = "Tag could not be found.";
            return false;
        }

        internal void ResetToDefaults()
        {
            _definitions.Clear();
            _definitions.AddRange(CreateDefaultTags());
            _orderedTagsCache = null;
            _save();
        }

        // Replaces the complete definition set from an undo snapshot. IDs are
        // identity, so malformed or duplicate records are rejected rather than
        // sanitized into different tags during replay.
        internal void ReplaceAll(IReadOnlyList<ZepTag> tags)
        {
            if (tags == null) throw new ArgumentNullException(nameof(tags));

            var replacement = new List<TagDefinitionRecord>(tags.Count);
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                var sanitizedId = SanitizeId(tag.Id);
                if (string.IsNullOrWhiteSpace(sanitizedId)
                    || !string.Equals(sanitizedId, tag.Id, StringComparison.Ordinal)
                    || !seenIds.Add(sanitizedId)
                    || string.IsNullOrWhiteSpace(tag.Name)
                    || tag.Color == ZepTagColor.None)
                {
                    throw new InvalidOperationException("The saved tag definition snapshot is invalid.");
                }

                replacement.Add(new TagDefinitionRecord
                {
                    Id = tag.Id,
                    Name = tag.Name.Trim(),
                    Color = tag.Color,
                    IsFavorite = tag.IsFavorite
                });
            }

            var original = CopyRecords(_definitions);

            var originalDefaultsVerified = _defaultsVerified;
            var originalCache = _orderedTagsCache;
            try
            {
                _definitions.Clear();
                _definitions.AddRange(replacement);
                _defaultsVerified = true;
                _orderedTagsCache = null;
                _save();
            }
            catch
            {
                _definitions.Clear();
                _definitions.AddRange(original);
                _defaultsVerified = originalDefaultsVerified;
                _orderedTagsCache = originalCache;
                throw;
            }
        }

        private static List<TagDefinitionRecord> CopyRecords(
            IReadOnlyList<TagDefinitionRecord> records)
        {
            var copy = new List<TagDefinitionRecord>(records?.Count ?? 0);
            if (records == null) return copy;
            for (var i = 0; i < records.Count; i++)
            {
                var entry = records[i];
                copy.Add(entry == null
                    ? null
                    : new TagDefinitionRecord
                    {
                        Id = entry.Id,
                        Name = entry.Name,
                        Color = entry.Color,
                        IsFavorite = entry.IsFavorite
                    });
            }

            return copy;
        }

        private static IReadOnlyList<TagDefinitionRecord> CreateDefaultTags()
        {
            return new[]
            {
                CreateDefaultTag("red", "Red", ZepTagColor.Red),
                CreateDefaultTag("orange", "Orange", ZepTagColor.Orange),
                CreateDefaultTag("yellow", "Yellow", ZepTagColor.Yellow),
                CreateDefaultTag("green", "Green", ZepTagColor.Green),
                CreateDefaultTag("blue", "Blue", ZepTagColor.Blue),
                CreateDefaultTag("purple", "Purple", ZepTagColor.Purple),
                CreateDefaultTag("gray", "Gray", ZepTagColor.Gray)
            };
        }

        private static TagDefinitionRecord CreateDefaultTag(string id, string name, ZepTagColor color)
        {
            return new TagDefinitionRecord
            {
                Id = id,
                Name = name,
                Color = color,
                IsFavorite = true
            };
        }

        private static string SanitizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var character = char.ToLowerInvariant(value[i]);
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                    continue;
                }

                if ((character == '-' || character == '_' || character == ' ') && builder.Length > 0 && builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-');
        }

        private static string BuildUniqueId(string baseId, ISet<string> seenIds)
        {
            var suffix = 2;
            var candidate = baseId;
            while (seenIds.Contains(candidate))
            {
                candidate = baseId + "-" + suffix++;
            }

            return candidate;
        }

        private static string BuildFallbackName(string tagId, ZepTagColor color, int index)
        {
            if (!string.IsNullOrWhiteSpace(tagId))
            {
                var segments = tagId.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 0)
                {
                    return string.Join(" ", segments.Select(static segment =>
                        char.ToUpperInvariant(segment[0]) + segment.Substring(1)));
                }
            }

            if (color != ZepTagColor.None)
            {
                return color.ToString();
            }

            return "Tag " + (index + 1);
        }

        private static ZepTagColor GuessFallbackColor(string tagId, int index)
        {
            if (!string.IsNullOrWhiteSpace(tagId))
            {
                if (Enum.TryParse<ZepTagColor>(BuildFallbackName(tagId, ZepTagColor.None, index), true, out var parsedColor)
                    && parsedColor != ZepTagColor.None)
                {
                    return parsedColor;
                }
            }

            var colorCycle = new[]
            {
                ZepTagColor.Red,
                ZepTagColor.Orange,
                ZepTagColor.Yellow,
                ZepTagColor.Green,
                ZepTagColor.Blue,
                ZepTagColor.Purple,
                ZepTagColor.Gray
            };

            return colorCycle[Math.Abs(index) % colorCycle.Length];
        }
    }
}
