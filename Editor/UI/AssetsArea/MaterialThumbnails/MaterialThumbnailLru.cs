using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zep.Editor.UI.AssetsArea.MaterialThumbnails
{
    // Bounded store for the panel-OWNED material thumbnail textures, keyed by
    // asset path. Unlike UnityZepIconRepository._assetIconCache (which
    // holds references to Unity-owned textures and may simply Clear()), every
    // texture in here was created by the thumbnail pipeline and leaks native
    // memory unless explicitly destroyed. The LRU therefore never drops an
    // entry silently: eviction and removal hand the texture to the onEvict
    // callback, whose owner decides when to DestroyImmediate (the provider
    // defers destruction one tick so visible tiles rebind off the dying
    // texture first).
    //
    // Deliberately NOT cleared on catalog-change / repo cache clears:
    // correctness is owned by per-asset invalidation (Remove / RemoveWhereDependsOn),
    // and repo-side stale references self-heal via the fake-null recheck in
    // GetAssetIcon. Clearing here on every import batch would destroy every
    // thumb and force a re-render storm.
    internal sealed class MaterialThumbnailLru
    {
        private sealed class Entry
        {
            public Texture2D Texture;
            public string[] DependencyPaths;
            public LinkedListNode<string> RecencyNode;
        }

        private readonly Dictionary<string, Entry> _entries;
        private readonly LinkedList<string> _recency = new LinkedList<string>();
        private readonly int _capacity;
        private readonly Action<Texture2D> _onEvict;

        public MaterialThumbnailLru(int capacity, Action<Texture2D> onEvict)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _capacity = capacity;
            _onEvict = onEvict ?? throw new ArgumentNullException(nameof(onEvict));
            _entries = new Dictionary<string, Entry>(capacity, StringComparer.Ordinal);
        }

        public int Count => _entries.Count;

        // Probe + touch. Destroyed (fake-null) textures are treated as absent
        // and their entry is dropped, mirroring how GetAssetIcon recovers from
        // Unity's own preview-cache eviction.
        public Texture2D TryGet(string assetPath)
        {
            if (assetPath == null || !_entries.TryGetValue(assetPath, out var entry))
            {
                return null;
            }

            if (entry.Texture == null)
            {
                _recency.Remove(entry.RecencyNode);
                _entries.Remove(assetPath);
                return null;
            }

            if (entry.RecencyNode != _recency.First)
            {
                _recency.Remove(entry.RecencyNode);
                _recency.AddFirst(entry.RecencyNode);
            }

            return entry.Texture;
        }

        // Stores an owned texture. A pre-existing entry for the path is evicted
        // first (its texture goes through onEvict like any other), then the
        // capacity bound evicts from the least-recently-used end.
        public void Add(string assetPath, Texture2D texture, string[] dependencyPaths)
        {
            if (assetPath == null || texture == null)
            {
                return;
            }

            Remove(assetPath);

            while (_entries.Count >= _capacity && _recency.Last != null)
            {
                RemoveNode(_recency.Last);
            }

            var entry = new Entry
            {
                Texture = texture,
                DependencyPaths = dependencyPaths ?? Array.Empty<string>(),
                RecencyNode = new LinkedListNode<string>(assetPath),
            };
            _recency.AddFirst(entry.RecencyNode);
            _entries[assetPath] = entry;
        }

        public bool Remove(string assetPath)
        {
            if (assetPath == null || !_entries.TryGetValue(assetPath, out var entry))
            {
                return false;
            }

            RemoveNode(entry.RecencyNode);
            return true;
        }

        // Drops every entry whose material is one of the changed paths or
        // whose recorded dependency set intersects them. Used by the
        // invalidator when an import batch lands (a texture reimport must
        // kill dependent material thumbs — the material's own dependency hash
        // does not cover texture contents). Takes the whole batch as a set so
        // the cost is one pass over the entries regardless of batch size,
        // never O(changedPaths × entries). The set must use an
        // OrdinalIgnoreCase comparer to match Unity path semantics.
        // Returns the number of entries removed.
        public int RemoveWhereDependsOnAny(HashSet<string> changedPaths)
        {
            if (changedPaths == null || changedPaths.Count == 0 || _entries.Count == 0)
            {
                return 0;
            }

            List<LinkedListNode<string>> toRemove = null;
            foreach (var pair in _entries)
            {
                if (!DependsOnAny(pair.Key, pair.Value, changedPaths))
                {
                    continue;
                }

                toRemove ??= new List<LinkedListNode<string>>();
                toRemove.Add(pair.Value.RecencyNode);
            }

            if (toRemove == null)
            {
                return 0;
            }

            for (var i = 0; i < toRemove.Count; i++)
            {
                RemoveNode(toRemove[i]);
            }

            return toRemove.Count;
        }

        public void Clear()
        {
            var node = _recency.First;
            while (node != null)
            {
                var next = node.Next;
                RemoveNode(node);
                node = next;
            }
        }

        private static bool DependsOnAny(string materialPath, Entry entry, HashSet<string> changedPaths)
        {
            if (changedPaths.Contains(materialPath))
            {
                return true;
            }

            var deps = entry.DependencyPaths;
            for (var i = 0; i < deps.Length; i++)
            {
                if (changedPaths.Contains(deps[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private void RemoveNode(LinkedListNode<string> node)
        {
            var path = node.Value;
            if (!_entries.TryGetValue(path, out var entry))
            {
                return;
            }

            _recency.Remove(node);
            _entries.Remove(path);
            if (entry.Texture != null)
            {
                _onEvict(entry.Texture);
            }
        }
    }
}
