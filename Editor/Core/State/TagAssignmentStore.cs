using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    // Owner of the guid -> tag-id assignments and of the file that IS that
    // state (ProjectSettings/Zep.TagAssignments.json, a
    // VCS-tracked JSON document). The JSON codec is JsonText, so the invariants
    // live with the other Core stores and the store is testable without an
    // editor.
    public sealed class TagAssignmentStore
    {
        private readonly Dictionary<string, string[]> _tagIdsByGuid =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        private readonly string _filePath;
        private readonly Action<string> _reportProblem;

        private bool _isLoaded;
        private int _version;
        private DateTime _lastObservedWriteTimeUtc = DateTime.MinValue;
        private bool _saveDisabledByLoadFailure;
        private bool _reportedSaveRefusal;

        // The file IS the store's state; pointing a second instance at a scratch
        // path is how the corrupt-file behaviour is tested without touching the
        // project's real assignments.
        public TagAssignmentStore(
            string filePath,
            Action<string> reportProblem = null)
        {
            _filePath = string.IsNullOrWhiteSpace(filePath)
                ? throw new ArgumentException("A tag assignment file path is required.", nameof(filePath))
                : filePath;
            _reportProblem = reportProblem;
        }

        public int Version
        {
            get
            {
                EnsureLoaded();
                return _version;
            }
        }

        public IReadOnlyList<string> GetTagIds(string guid)
        {
            EnsureLoaded();

            var normalizedGuid = NormalizeGuid(guid);
            if (string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return Array.Empty<string>();
            }

            return _tagIdsByGuid.TryGetValue(normalizedGuid, out var tagIds)
                ? tagIds
                : Array.Empty<string>();
        }

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> GetAllTagIdsByGuid()
        {
            EnsureLoaded();
            var snapshot = new Dictionary<string, IReadOnlyList<string>>(
                _tagIdsByGuid.Count,
                StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _tagIdsByGuid)
            {
                snapshot[entry.Key] = (string[])entry.Value.Clone();
            }

            return snapshot;
        }

        internal bool ReplaceAll(
            IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByGuid)
        {
            EnsureLoaded();
            if (tagIdsByGuid == null) throw new ArgumentNullException(nameof(tagIdsByGuid));

            var replacement = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in tagIdsByGuid)
            {
                var normalizedGuid = NormalizeGuid(entry.Key);
                if (string.IsNullOrWhiteSpace(normalizedGuid)
                    || replacement.ContainsKey(normalizedGuid))
                {
                    throw new InvalidOperationException("The saved tag assignment snapshot is invalid.");
                }

                var normalizedTagIds = NormalizeTagIds(entry.Value);
                if (normalizedTagIds.Length > 0)
                {
                    replacement[normalizedGuid] = normalizedTagIds;
                }
            }

            if (AssignmentMapsEqual(_tagIdsByGuid, replacement))
            {
                return false;
            }

            var original = new Dictionary<string, string[]>(_tagIdsByGuid, StringComparer.OrdinalIgnoreCase);
            try
            {
                RestoreSnapshot(replacement);
                SaveToDisk();
                _version++;
                return true;
            }
            catch
            {
                RestoreSnapshot(original);
                throw;
            }
        }

        public bool SetTagIds(IReadOnlyDictionary<string, IReadOnlyList<string>> tagIdsByGuid)
        {
            EnsureLoaded();
            if (tagIdsByGuid == null || tagIdsByGuid.Count == 0)
            {
                return false;
            }

            PrepareForMutation();

            // Only the entries this call touches can need undoing, so the
            // rollback record is O(changed): one tag click used to clone the
            // whole assignment table (50k arrays) before it could write a byte.
            var rollbackJournal = new List<KeyValuePair<string, string[]>>();
            var changed = false;

            try
            {
                foreach (var entry in tagIdsByGuid)
                {
                    changed |= SetTagIdsWithoutSaving(entry.Key, entry.Value, rollbackJournal);
                }

                if (!changed)
                {
                    return false;
                }

                SaveToDisk();
                _version++;
                return true;
            }
            catch
            {
                RestoreTouchedEntries(rollbackJournal);
                throw;
            }
        }

        public bool ReloadIfFileChanged()
        {
            EnsureLoaded();

            var filePath = _filePath;
            // ONE stat, not two. This runs on every ApplyProjection through
            // EnsureSharedProjectionCaches, and File.Exists was answering a
            // question GetLastWriteTimeUtc already answers: a missing file (or a
            // missing directory) yields the framework's year-1601 sentinel
            // rather than throwing, and no real write time can equal it. Either
            // sentinel is unequal to every observed time, so the store reloads —
            // exactly what the File.Exists branch did. The catch covers the
            // paths that genuinely throw (an unreadable mount, a revoked ACL)
            // and lands on the same "reload" answer.
            DateTime currentWriteTimeUtc;
            try
            {
                currentWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
            }
            catch (IOException)
            {
                currentWriteTimeUtc = DateTime.MinValue;
            }
            catch (UnauthorizedAccessException)
            {
                currentWriteTimeUtc = DateTime.MinValue;
            }

            if (currentWriteTimeUtc == _lastObservedWriteTimeUtc)
            {
                return false;
            }

            LoadFromDisk();
            _version++;
            return true;
        }

        // Tag-definition commands use compare-and-set semantics across this file
        // and ZepTagSettings. A timestamp probe is not strong enough at
        // that boundary: external tools can preserve a file's mtime, and some
        // filesystems have coarse timestamp resolution. Always re-read before a
        // command captures or compares the complete tag state.
        internal void ReloadFromDisk()
        {
            EnsureLoaded();
            LoadFromDisk();
            _version++;
        }

        public void PrepareForMutation()
        {
            EnsureLoaded();
            if (LoadFromDisk())
            {
                _version++;
            }

            EnsureSavingIsAllowed();
        }

        private void EnsureLoaded()
        {
            if (_isLoaded)
            {
                return;
            }

            // Latch before the attempt: a load that fails must leave the store
            // alive (empty reads, refused writes), never retrying-and-rethrowing
            // on every tag call for the rest of the session.
            _isLoaded = true;
            LoadFromDisk();
        }

        // Failure policy: the file is authoritative, so a file we cannot parse
        // means we do not know the assignments — we keep whatever is already in
        // memory, say so once, and refuse to save until the file parses again.
        // The observed mtime is still recorded on failure so the panel does not
        // re-report on every version probe; recovery is therefore driven by the
        // user's edit CHANGING the mtime, which makes ReloadIfFileChanged parse
        // it again and clear the latch on success.
        private bool LoadFromDisk()
        {
            var filePath = _filePath;
            Dictionary<string, string[]> parsed;
            try
            {
                parsed = ParseAssignments(File.ReadAllText(filePath));
            }
            catch (FileNotFoundException)
            {
                InitializeMissingFile();
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                InitializeMissingFile();
                return false;
            }
            catch (Exception exception)
            {
                RecordWriteTimeIfAvailable();
                PoisonSaves("The tag assignment file could not be read (" + exception.Message
                    + "), so the tags already loaded were kept and tag changes cannot be saved"
                    + " until it parses again.");
                return false;
            }

            if (parsed == null)
            {
                RecordWriteTimeIfAvailable();
                PoisonSaves("The tag assignment file does not contain a tag assignment list,"
                    + " so the tags already loaded were kept and tag changes cannot be saved"
                    + " until it parses again.");
                return false;
            }

            var changed = !AssignmentMapsEqual(_tagIdsByGuid, parsed);
            RestoreSnapshot(parsed);
            _saveDisabledByLoadFailure = false;
            _reportedSaveRefusal = false;
            RecordWriteTimeIfAvailable();
            return changed;
        }

        private void InitializeMissingFile()
        {
            // Start empty and write the file, so the project has one from the
            // first load on.
            _tagIdsByGuid.Clear();
            _saveDisabledByLoadFailure = false;
            _reportedSaveRefusal = false;
            SaveToDisk();
        }

        private void RecordWriteTimeIfAvailable()
        {
            try
            {
                _lastObservedWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
            }
            catch (IOException)
            {
                // Keep the last trustworthy observation.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the last trustworthy observation.
            }
        }

        // Returns null when the payload is not a tag assignment file at all; throws
        // when the text is not JSON. Never touches _tagIdsByGuid, so a half-parsed
        // file can never become the in-memory state. Schema (unchanged from the
        // JsonUtility era, so existing files keep parsing):
        //   {"assignments":[{"guid":"<32 hex>","tagIds":["<id>", ...]}, ...]}
        internal static Dictionary<string, string[]> ParseAssignments(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var document = JsonText.Parse(json);
            if (!JsonText.TryGetMember(document, "assignments", out var assignmentsValue)
                || !(assignmentsValue is List<object> assignments))
            {
                return null;
            }

            var parsed = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < assignments.Count; i++)
            {
                var assignment = assignments[i];
                if (assignment == null)
                {
                    continue;
                }

                JsonText.TryGetMember(assignment, "guid", out var guidValue);
                var normalizedGuid = NormalizeGuid(guidValue as string);
                if (string.IsNullOrWhiteSpace(normalizedGuid))
                {
                    continue;
                }

                var normalizedTagIds = NormalizeTagIds(JsonText.ReadStringArray(assignment, "tagIds"));
                if (normalizedTagIds.Length == 0)
                {
                    continue;
                }

                parsed[normalizedGuid] = normalizedTagIds;
            }

            return parsed;
        }

        // Report on the transition only: CaptureTagState re-reads the file before
        // every tag command, so reporting per attempt would fill the console with
        // one warning per keystroke-driven command while the file stays broken.
        private void PoisonSaves(string message)
        {
            var wasPoisoned = _saveDisabledByLoadFailure;
            _saveDisabledByLoadFailure = true;
            if (!wasPoisoned)
            {
                ReportAssignmentFileProblem(message);
            }
        }

        // Mirrors ZepTagSettings' report-don't-repair channel: name the
        // file the user has to fix, and never rewrite it for them.
        private void ReportAssignmentFileProblem(string message)
        {
            _reportProblem?.Invoke(message + " The file was left unchanged: " + _filePath);
        }

        private void EnsureSavingIsAllowed()
        {
            if (!_saveDisabledByLoadFailure)
            {
                return;
            }

            if (!_reportedSaveRefusal)
            {
                _reportedSaveRefusal = true;
                ReportAssignmentFileProblem(
                    "Tag changes were not saved because the tag assignment file could not be read;"
                    + " saving would overwrite assignments this session never managed to load.");
            }

            throw new InvalidOperationException(
                "Tag changes cannot be saved while " + _filePath
                + " cannot be read. Fix or delete that file and try again.");
        }

        private void SaveToDisk()
        {
            EnsureSavingIsAllowed();

            var filePath = _filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? string.Empty);

            // Write-then-atomic-replace so a crash or IO failure mid-write can't truncate or
            // empty the file, which would silently drop every tag assignment. Replace swaps
            // the temp file in as one operation; a plain move covers the first-ever write.
            var tempPath = filePath + ".tmp";
            // Compact, not pretty-printed: this file is machine-owned and grows
            // with the project (a pretty print roughly doubles a multi-megabyte
            // write done on every tag click). The parser reads either form.
            File.WriteAllText(tempPath, SerializeAssignments());
            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }

            _lastObservedWriteTimeUtc = File.GetLastWriteTimeUtc(filePath);
        }

        // Sorted by guid so the VCS-tracked file diffs by content, not by
        // dictionary order.
        private string SerializeAssignments()
        {
            var guids = new List<string>(_tagIdsByGuid.Keys);
            guids.Sort(StringComparer.OrdinalIgnoreCase);
            var builder = new StringBuilder(32 + guids.Count * 64);
            builder.Append("{\"assignments\":[");
            for (var i = 0; i < guids.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"guid\":");
                JsonText.AppendString(builder, guids[i]);
                builder.Append(",\"tagIds\":");
                JsonText.AppendStringArray(builder, _tagIdsByGuid[guids[i]]);
                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        // Records what each MUTATED entry looked like beforehand (a null value
        // means the GUID had no assignment at all) so a failed save can put it
        // back without a copy of the whole table.
        private bool SetTagIdsWithoutSaving(
            string guid,
            IReadOnlyList<string> tagIds,
            List<KeyValuePair<string, string[]>> rollbackJournal)
        {
            var normalizedGuid = NormalizeGuid(guid);
            if (string.IsNullOrWhiteSpace(normalizedGuid))
            {
                return false;
            }

            var hadEntry = _tagIdsByGuid.TryGetValue(normalizedGuid, out var existingTagIds);
            var normalizedTagIds = NormalizeTagIds(tagIds);
            if (normalizedTagIds.Length == 0)
            {
                if (!hadEntry)
                {
                    return false;
                }

                rollbackJournal?.Add(
                    new KeyValuePair<string, string[]>(normalizedGuid, existingTagIds));
                _tagIdsByGuid.Remove(normalizedGuid);
                return true;
            }

            if (hadEntry && AreTagIdsEqual(existingTagIds, normalizedTagIds))
            {
                return false;
            }

            rollbackJournal?.Add(
                new KeyValuePair<string, string[]>(normalizedGuid, hadEntry ? existingTagIds : null));
            _tagIdsByGuid[normalizedGuid] = normalizedTagIds;
            return true;
        }

        // Reverse order: two keys in one batch can normalise to the same GUID,
        // and the FIRST record for it is the state the batch started from.
        private void RestoreTouchedEntries(List<KeyValuePair<string, string[]>> rollbackJournal)
        {
            for (var i = rollbackJournal.Count - 1; i >= 0; i--)
            {
                var entry = rollbackJournal[i];
                if (entry.Value == null)
                {
                    _tagIdsByGuid.Remove(entry.Key);
                }
                else
                {
                    _tagIdsByGuid[entry.Key] = entry.Value;
                }
            }
        }

        private void RestoreSnapshot(IReadOnlyDictionary<string, string[]> snapshot)
        {
            _tagIdsByGuid.Clear();
            foreach (var entry in snapshot)
            {
                _tagIdsByGuid[entry.Key] = entry.Value;
            }
        }

        private static string NormalizeGuid(string guid)
        {
            return string.IsNullOrWhiteSpace(guid)
                ? string.Empty
                : guid.Trim().ToLowerInvariant();
        }

        private static string[] NormalizeTagIds(IEnumerable<string> tagIds)
        {
            if (tagIds == null)
            {
                return Array.Empty<string>();
            }

            var normalizedTagIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tagId in tagIds)
            {
                var normalizedTagId = NormalizeTagId(tagId);
                if (!string.IsNullOrWhiteSpace(normalizedTagId) && seen.Add(normalizedTagId))
                {
                    normalizedTagIds.Add(normalizedTagId);
                }
            }

            return normalizedTagIds.ToArray();
        }

        private static string NormalizeTagId(string tagId)
        {
            return string.IsNullOrWhiteSpace(tagId)
                ? string.Empty
                : tagId.Trim().ToLowerInvariant();
        }

        private static bool AreTagIdsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            for (var index = 0; index < leftCount; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AssignmentMapsEqual(
            IReadOnlyDictionary<string, string[]> left,
            IReadOnlyDictionary<string, string[]> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out var rightTagIds)
                    || !AreTagIdsEqual(entry.Value, rightTagIds))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
