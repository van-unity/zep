using System;
using System.Collections;
using System.Collections.Generic;
using Zep.Core.Application;
using UnityEditor;

namespace Zep.Editor.Bridge.FileOperations
{
    // A Finder drag carries its ORIGINAL file paths in DragAndDrop's generic
    // data, under a pasteboard key that has changed across editor versions and
    // whose value may be a single string or a sequence of them. Try the known
    // keys in order and take the first that yields readable paths.
    internal sealed class MacOSDragSourcePathResolver : IDragSourcePathResolver
    {
        private static readonly string[] CandidateKeys =
        {
            "NSFilenamesPboardType",
            "public.file-url",
            "FileNames",
            "FileName"
        };

        public IReadOnlyList<string> TryResolveOriginalPaths()
        {
            for (var i = 0; i < CandidateKeys.Length; i++)
            {
                object payload;
                try
                {
                    payload = DragAndDrop.GetGenericData(CandidateKeys[i]);
                }
                catch (InvalidCastException)
                {
                    // A key holding a value of an unexpected type is a miss, not
                    // a failure — keep looking.
                    continue;
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var paths = ReadPaths(payload);
                if (paths != null)
                {
                    return paths;
                }
            }

            return null;
        }

        // Null means "this key carried nothing usable, try the next one". An
        // IList<string> is trusted even when empty — it says the key IS the
        // path carrier and this drag simply has none — while a loose sequence
        // that yielded no strings was the wrong key.
        private static List<string> ReadPaths(object payload)
        {
            switch (payload)
            {
                case null:
                    return null;
                case string singlePath:
                    return new List<string> { singlePath };
                case IList<string> paths:
                    return new List<string>(paths);
                case IEnumerable sequence:
                {
                    var readPaths = new List<string>();
                    foreach (var entry in sequence)
                    {
                        if (entry is string path)
                        {
                            readPaths.Add(path);
                        }
                    }

                    return readPaths.Count > 0 ? readPaths : null;
                }
                default:
                    return null;
            }
        }
    }
}
