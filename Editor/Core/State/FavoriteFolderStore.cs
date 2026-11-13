using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    public sealed class FavoriteFolderStore
    {
        private readonly List<string> _paths;
        private readonly Action _save;
        // First-contact distrust only, like TagDefinitionStore: freshly
        // deserialized data is sanitized once, and every store-driven mutation
        // keeps the invariants by construction. Nothing is written back until
        // a real edit saves — the file stays the user's artifact.
        private bool _sanitized;

        public FavoriteFolderStore(List<string> paths, Action save)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _save = save ?? throw new ArgumentNullException(nameof(save));
        }

        public IReadOnlyList<string> Paths
        {
            get
            {
                EnsureSanitized();
                return _paths;
            }
        }

        public bool Add(string folderPath)
        {
            EnsureSanitized();
            var normalized = NormalizeCandidate(folderPath);
            if (normalized.Length == 0 || IndexOf(normalized) >= 0)
            {
                return false;
            }

            _paths.Add(normalized);
            _save();
            return true;
        }

        public bool Remove(string folderPath)
        {
            EnsureSanitized();
            var index = IndexOf(AssetPathFormat.Normalize(folderPath));
            if (index < 0)
            {
                return false;
            }

            _paths.RemoveAt(index);
            _save();
            return true;
        }

        // Reorders folderPath to sit immediately before anchorFolderPath; an
        // empty anchor moves it to the end. Returns false (and does not save)
        // when either path is unknown or the move would change nothing.
        public bool MoveBefore(string folderPath, string anchorFolderPath)
        {
            EnsureSanitized();
            var from = IndexOf(AssetPathFormat.Normalize(folderPath));
            if (from < 0)
            {
                return false;
            }

            var anchor = AssetPathFormat.Normalize(anchorFolderPath);
            int target;
            if (string.IsNullOrEmpty(anchor))
            {
                target = _paths.Count;
            }
            else
            {
                target = IndexOf(anchor);
                if (target < 0)
                {
                    return false;
                }
            }

            var value = _paths[from];
            _paths.RemoveAt(from);
            if (target > from)
            {
                target--;
            }

            if (target == from)
            {
                _paths.Insert(from, value);
                return false;
            }

            _paths.Insert(target, value);
            _save();
            return true;
        }

        // Follows a completed rename or move: every favorite that IS previousPath,
        // or lives under it, is re-rooted at newPath. Same same-or-descendant rule
        // the pane state uses to carry its expanded folders across a move, so the
        // two never disagree about what "the folder moved" means.
        //
        // A favorite for a folder that was DELETED is deliberately left alone: the
        // store's contract is that a path that comes back regains its favorite, and
        // the view already renders nothing for a path the index does not hold.
        public bool Rewrite(string previousPath, string newPath)
        {
            EnsureSanitized();
            var oldRoot = AssetPathFormat.Normalize(previousPath);
            var newRoot = NormalizeCandidate(newPath);
            if (oldRoot.Length == 0
                || newRoot.Length == 0
                || string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase)
                // A create reports its PARENT as the previous path, and nothing can
                // move into its own subtree — so this shape is never a move, and
                // rewriting on it would drag the parent's favorite onto whatever was
                // just created inside it.
                || IsSameOrDescendant(newRoot, oldRoot))
            {
                return false;
            }

            var kept = new List<string>(_paths.Count);
            var changed = false;
            for (var i = 0; i < _paths.Count; i++)
            {
                var path = _paths[i];
                if (IsSameOrDescendant(path, oldRoot))
                {
                    path = newRoot + path.Substring(oldRoot.Length);
                    changed = true;
                }

                var duplicate = false;
                for (var j = 0; j < kept.Count; j++)
                {
                    if (string.Equals(kept[j], path, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    kept.Add(path);
                }
            }

            if (!changed)
            {
                return false;
            }

            _paths.Clear();
            _paths.AddRange(kept);
            _save();
            return true;
        }

        public bool Contains(string folderPath)
        {
            EnsureSanitized();
            return IndexOf(AssetPathFormat.Normalize(folderPath)) >= 0;
        }

        private static bool IsSameOrDescendant(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.Length > root.Length
                && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path[root.Length] == '/';
        }

        private int IndexOf(string normalizedPath)
        {
            if (string.IsNullOrEmpty(normalizedPath))
            {
                return -1;
            }

            for (var i = 0; i < _paths.Count; i++)
            {
                if (string.Equals(_paths[i], normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string NormalizeCandidate(string folderPath)
        {
            var normalized = AssetPathFormat.Normalize(folderPath);
            return !string.IsNullOrEmpty(normalized)
                && AssetPathClassification.IsProjectAssetPath(normalized)
                ? normalized
                : string.Empty;
        }

        private void EnsureSanitized()
        {
            if (_sanitized)
            {
                return;
            }

            _sanitized = true;
            var kept = new List<string>(_paths.Count);
            for (var i = 0; i < _paths.Count; i++)
            {
                var normalized = NormalizeCandidate(_paths[i]);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var duplicate = false;
                for (var j = 0; j < kept.Count; j++)
                {
                    if (string.Equals(kept[j], normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                {
                    kept.Add(normalized);
                }
            }

            // Rewrite on any difference, including pure normalization (a raw
            // "Assets\\Foo\\" entry must not survive next to the normalized
            // form IndexOf compares against).
            var changed = kept.Count != _paths.Count;
            for (var i = 0; !changed && i < kept.Count; i++)
            {
                changed = !string.Equals(kept[i], _paths[i], StringComparison.Ordinal);
            }

            if (changed)
            {
                _paths.Clear();
                _paths.AddRange(kept);
            }
        }
    }
}
