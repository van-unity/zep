using System;
using System.Collections.Generic;
using Zep.Core.Domain;
using Zep.Core.Theming;
using UnityEditor.ShortcutManagement;

namespace Zep.Editor.UI
{
    internal sealed class ZepSettingsThemeOption
    {
        public string Id { get; }
        public string DisplayName { get; }
        public ZepThemeSource Source { get; }

        public ZepSettingsThemeOption(string id, string displayName, ZepThemeSource source)
        {
            Id = id;
            DisplayName = displayName;
            Source = source;
        }
    }

    internal sealed class ZepSettingsScreenModel
    {
        private readonly List<string> _themeOptions = new List<string>();
        private readonly List<ZepSettingsThemeOption> _themeOptionDetails = new List<ZepSettingsThemeOption>();
        private readonly List<ZepTag> _tags = new List<ZepTag>();
        private string _currentThemeId = string.Empty;
        private bool _includeMetaInArchive;
        private bool _promptForMetaOnCompress = true;
        private readonly List<KeyCombination> _searchShortcutOptions = new List<KeyCombination>();

        // Coarse "something changed" signal, kept for callers that do not care which
        // part moved. The settings screen listens to the three fine-grained events
        // below instead: one raise used to rebuild all four of its sections, so a
        // theme switch tore down and rebuilt the whole tag editor.
        public event Action Changed;
        public event Action ThemesChanged;
        public event Action TagsChanged;
        public event Action CompressionChanged;
        public event Action ShortcutsChanged;

        public IReadOnlyList<KeyCombination> SearchShortcutOptions => _searchShortcutOptions;
        public KeyCombination SearchShortcut { get; private set; }
        public string SearchShortcutUnavailableReason { get; private set; }

        public void SetSearchShortcuts(IEnumerable<KeyCombination> options, KeyCombination current, string unavailableReason)
        {
            var changed = ReplaceContents(_searchShortcutOptions, options);
            changed |= !SearchShortcut.Equals(current) || SearchShortcutUnavailableReason != unavailableReason;
            SearchShortcut = current;
            SearchShortcutUnavailableReason = unavailableReason;
            if (!changed) return;
            ShortcutsChanged?.Invoke();
            Changed?.Invoke();
        }

        public IReadOnlyList<string> ThemeOptions => _themeOptions;
        public IReadOnlyList<ZepSettingsThemeOption> ThemeOptionDetails => _themeOptionDetails;

        public string CurrentThemeId => _currentThemeId;

        public IReadOnlyList<ZepTag> Tags => _tags;

        public bool IncludeMetaInArchive => _includeMetaInArchive;
        public bool PromptForMetaOnCompress => _promptForMetaOnCompress;

        // Ids and details describe the same catalog, so they are set together and
        // announced once — pushing them separately raised twice per registry refresh.
        public void SetThemeCatalog(
            IEnumerable<string> themeOptions,
            IEnumerable<ZepSettingsThemeOption> details)
        {
            var changed = ReplaceThemeOptions(themeOptions);
            changed |= ReplaceThemeOptionDetails(details);
            if (changed)
            {
                RaiseThemesChanged();
            }
        }

        private bool ReplaceThemeOptions(IEnumerable<string> themeOptions)
        {
            return ReplaceContents(_themeOptions, themeOptions);
        }

        private bool ReplaceThemeOptionDetails(IEnumerable<ZepSettingsThemeOption> details)
        {
            var snapshot = new List<ZepSettingsThemeOption>();
            if (details != null)
                foreach (var d in details) if (d != null) snapshot.Add(d);

            if (snapshot.Count == _themeOptionDetails.Count)
            {
                var same = true;
                for (var i = 0; i < snapshot.Count; i++)
                {
                    var a = _themeOptionDetails[i];
                    var b = snapshot[i];
                    if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                        || !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal)
                        || a.Source != b.Source)
                    {
                        same = false;
                        break;
                    }
                }
                if (same) return false;
            }

            _themeOptionDetails.Clear();
            _themeOptionDetails.AddRange(snapshot);
            return true;
        }

        public void SetCurrentThemeId(string currentThemeId)
        {
            var normalized = currentThemeId ?? string.Empty;
            if (string.Equals(_currentThemeId, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _currentThemeId = normalized;
            RaiseThemesChanged();
        }

        public void SetTags(IEnumerable<ZepTag> tags)
        {
            if (ReplaceContents(_tags, tags))
            {
                TagsChanged?.Invoke();
                Changed?.Invoke();
            }
        }

        public void SetIncludeMetaInArchive(bool includeMeta)
        {
            if (_includeMetaInArchive == includeMeta)
            {
                return;
            }

            _includeMetaInArchive = includeMeta;
            RaiseCompressionChanged();
        }

        public void SetPromptForMetaOnCompress(bool promptForMeta)
        {
            if (_promptForMetaOnCompress == promptForMeta)
            {
                return;
            }

            _promptForMetaOnCompress = promptForMeta;
            RaiseCompressionChanged();
        }

        private void RaiseThemesChanged()
        {
            ThemesChanged?.Invoke();
            Changed?.Invoke();
        }

        private void RaiseCompressionChanged()
        {
            CompressionChanged?.Invoke();
            Changed?.Invoke();
        }

        private static bool ReplaceContents<T>(List<T> target, IEnumerable<T> source)
        {
            if (source == null)
            {
                if (target.Count == 0)
                {
                    return false;
                }

                target.Clear();
                return true;
            }

            if (SequenceEquals(target, source))
            {
                return false;
            }

            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }

            return true;
        }

        private static bool SequenceEquals<T>(IReadOnlyList<T> target, IEnumerable<T> source)
        {
            if (source is IReadOnlyList<T> sourceList && sourceList.Count != target.Count)
            {
                return false;
            }

            var comparer = EqualityComparer<T>.Default;
            var index = 0;
            foreach (var item in source)
            {
                if (index >= target.Count)
                {
                    return false;
                }

                if (!comparer.Equals(target[index], item))
                {
                    return false;
                }

                index++;
            }

            return index == target.Count;
        }
    }
}
