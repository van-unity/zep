using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;

namespace Zep.Editor.UI.Tests
{
    // A real ZepController over in-memory records: the only way a UI
    // test can exercise a code path that reads or mutates through the command
    // handler, because the handler is the concrete controller (the pre-incident
    // IZepCommandHandler seam is gone and no fake can stand in for it).
    // Shared because two test classes need the same stack; the knobs
    // (CanWriteOverride/CanTagOverride, the tag map) exist so a caller can pin a
    // capability without inventing a second gateway.
    internal sealed class ZepUiControllerRig : IDisposable
    {
        internal ZepState State { get; private set; }

        internal ZepController Controller { get; private set; }

        internal RigScheduler Scheduler { get; private set; }

        internal RigAssetGateway Gateway { get; private set; }

        internal TagDefinitionStore TagDefinitions { get; private set; }

        internal RigAddressablesQuery Addressables { get; private set; }

        internal static ZepUiControllerRig Build(
            IEnumerable<AssetRecord> records,
            IEnumerable<string> folders,
            IEnumerable<TagDefinitionRecord> tagDefinitions = null)
        {
            var source = new RigRecordSource(records, folders);
            var gateway = new RigAssetGateway(source);
            var index = new InMemoryAssetIndex();
            var definitions = new TagDefinitionStore(
                tagDefinitions == null
                    ? new List<TagDefinitionRecord>()
                    : new List<TagDefinitionRecord>(tagDefinitions),
                () => { });
            var rig = new ZepUiControllerRig
            {
                State = new ZepState(),
                Scheduler = new RigScheduler(),
                Gateway = gateway,
                TagDefinitions = definitions,
                Addressables = new RigAddressablesQuery()
            };
            gateway.TagDefinitions = definitions;
            rig.Controller = new ZepController(
                rig.State,
                gateway,
                new ProjectFileCommandService(gateway),
                source,
                index,
                new AssetSearchService(index),
                definitions,
                new RigSynchronizer(source, index),
                rig.Scheduler,
                new RigShellReveal(),
                rig.Addressables);
            rig.Controller.Refresh();
            rig.Scheduler.RunAll();
            return rig;
        }

        internal ZepShellView BuildShell()
        {
            return new ZepShellView(
                State,
                Controller,
                (Action<ZepPaneState, IReadOnlyList<ZepAssetEntry>>)null);
        }

        public void Dispose()
        {
            try
            {
                Controller?.Dispose();
            }
            finally
            {
                Controller = null;
                Gateway?.DeleteCreatedUndoPayloads();
            }
        }
    }

    // Minimal record source: just enough gateway/index behavior to run the
    // browse and search projections; no file-operation support.
    internal sealed class RigRecordSource : IAssetRecordSource
    {
        internal RigRecordSource(IEnumerable<AssetRecord> records, IEnumerable<string> folders)
        {
            Records = new List<AssetRecord>(records);
            Folders = folders.Select(AssetPath.From).ToList();
        }

        internal List<AssetRecord> Records { get; }

        internal List<AssetPath> Folders { get; }

        public bool IsThreadSafe => true;

        public void PrepareForEnumeration(
            IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
            IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
        {
        }

        public IEnumerable<AssetRecord> GetRecords() => Records;

        public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
        {
            return Folders
                .Select(folderPath => new AssetFolderRecord(folderPath))
                .ToList();
        }

        public AssetRecord BuildRecord(AssetPath assetPath)
        {
            return Records.FirstOrDefault(record =>
                string.Equals(
                    record.AssetPath,
                    assetPath.Value,
                    StringComparison.OrdinalIgnoreCase));
        }

        public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
        {
            return ContainsFolder(folderPath.Value)
                ? new AssetFolderRecord(folderPath)
                : null;
        }

        internal bool ContainsFolder(string path)
        {
            return Folders.Any(folder =>
                string.Equals(folder.Value, path, StringComparison.OrdinalIgnoreCase));
        }

        // A moved folder takes its contents with it, so the rewrite is by
        // prefix, not by exact path — the same shape the real project has and
        // the only one under which a move test can tell a folder move from an
        // asset move.
        internal void MovePath(string oldPath, string newPath)
        {
            for (var i = 0; i < Folders.Count; i++)
            {
                if (IsSameOrDescendant(Folders[i].Value, oldPath))
                {
                    Folders[i] = AssetPath.From(
                        newPath + Folders[i].Value.Substring(oldPath.Length));
                }
            }

            for (var i = 0; i < Records.Count; i++)
            {
                var record = Records[i];
                if (!IsSameOrDescendant(record.AssetPath, oldPath))
                {
                    continue;
                }

                var movedPath = newPath + record.AssetPath.Substring(oldPath.Length);
                // Only the moved item itself can be renamed on the way (Keep
                // Both); everything under it keeps the name it had.
                var movedName = string.Equals(
                    record.AssetPath,
                    oldPath,
                    StringComparison.OrdinalIgnoreCase)
                        ? AssetPathFormat.GetNameWithoutExtension(movedPath)
                        : record.Name;
                Records[i] = new AssetRecord(
                    record.Guid,
                    movedPath,
                    movedName,
                    record.TypeName,
                    record.TagSearchText);
            }
        }

        internal void RemovePath(string path)
        {
            Records.RemoveAll(record => IsSameOrDescendant(record.AssetPath, path));
            Folders.RemoveAll(folder => IsSameOrDescendant(folder.Value, path));
        }

        private static bool IsSameOrDescendant(string path, string root)
        {
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                || (path.Length > root.Length
                    && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    && path[root.Length] == '/');
        }
    }

    internal sealed class RigSynchronizer : IAssetIndexSynchronizer
    {
        private readonly RigRecordSource _source;
        private readonly InMemoryAssetIndex _index;

        internal RigSynchronizer(RigRecordSource source, InMemoryAssetIndex index)
        {
            _source = source;
            _index = index;
        }

        public void Synchronize(Action onComplete = null)
        {
            _index.Clear();
            foreach (var record in _source.Records)
            {
                _index.Upsert(record);
            }

            onComplete?.Invoke();
        }

        public void SynchronizeAsync(Action onFullScanStarted, Action onComplete)
        {
            onFullScanStarted?.Invoke();
            Synchronize(onComplete);
        }

        public void InvalidateInProgressScan()
        {
        }

        public bool HasBackgroundJob => false;

        public void Dispose()
        {
        }

        public IReadOnlyList<AssetFolderRecord> GetFolderRecordsForProjection()
        {
            return _source.GetFolderRecords();
        }
    }

    internal sealed class RigAssetGateway : IProjectAssetGateway, IProjectTagDefinitionGateway
    {
        private readonly RigRecordSource _source;
        private readonly List<string> _createdUndoPayloads = new List<string>();
        private readonly Dictionary<string, IReadOnlyList<string>> _tagIdsByPath =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        internal RigAssetGateway(RigRecordSource source)
        {
            _source = source;
        }

        // Production topology: the gateway persists tag definitions into the very
        // store the controller reads (ZepTagSettings.instance.Store), so
        // the compare-and-set in ChangeTagDefinitionsAfterPreparation sees the
        // controller's own definitions. The rig mirrors that by sharing one store.
        internal TagDefinitionStore TagDefinitions { get; set; }

        // Null = derive from the path (the production-like answer). Set to pin a
        // capability the fixture wants to test independently of path shape.
        internal bool? CanWriteOverride { get; set; }

        internal bool? CanTagOverride { get; set; }

        // Per-path pins, checked before CanWriteOverride. Existing fixtures that
        // only set CanWriteOverride are unaffected (this map stays empty for
        // them); a fixture that needs two folders to disagree on writability
        // (e.g. an unwritable "Packages" mount with a writable local package
        // under it) adds entries here instead of a second fake gateway.
        internal Dictionary<string, bool> CanWriteOverrideByPath { get; } =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyDictionary<string, IReadOnlyList<string>> TagIdsByPath => _tagIdsByPath;

        internal void SeedTagIds(string assetPath, params string[] tagIds)
        {
            _tagIdsByPath[assetPath] = tagIds;
        }

        internal Exception TagVersionException { get; set; }

        public int TagVersion
        {
            get
            {
                if (TagVersionException != null)
                {
                    throw TagVersionException;
                }

                return 1;
            }
        }

        public bool Exists(AssetPath assetPath)
        {
            return IsFolder(assetPath) || _source.BuildRecord(assetPath) != null;
        }

        public bool IsFolder(AssetPath assetPath) => _source.ContainsFolder(assetPath.Value);

        public bool CanWrite(AssetPath assetPath) =>
            CanWriteOverrideByPath.TryGetValue(assetPath.Value, out var pinned)
                ? pinned
                : CanWriteOverride ?? AssetPathClassification.IsWritablePath(assetPath);

        public bool CanTag(AssetPath assetPath) => CanTagOverride ?? Exists(assetPath);

        public bool EnsureFolder(AssetPath folderPath) => true;

        public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName) =>
            ProjectFileOperationResult.Failure("Not used.");

        // The real gateway refuses to move onto something that is already
        // there, and reports where the item ended up. This fake used to decline
        // every move outright ("Not used."), so no UI test could tell a move
        // that landed from a move that collided.
        public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath)
        {
            if (!allowOverwriteWithUniquePath
                && !string.Equals(
                    sourcePath.Value,
                    destinationPath.Value,
                    StringComparison.OrdinalIgnoreCase)
                && Exists(destinationPath))
            {
                return ProjectFileOperationResult.Failure(
                    "Move Asset failed: '" + destinationPath.Value + "' already exists.",
                    sourcePath.Value);
            }

            _source.MovePath(sourcePath.Value, destinationPath.Value);
            return ProjectFileOperationResult.Success(
                destinationPath.Value,
                sourcePath.Value);
        }

        public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
            ProjectFileOperationResult.Failure("Not used.");

        public ProjectFileOperationResult Delete(AssetPath assetPath) =>
            ProjectFileOperationResult.Failure("Not used.");

        private string CreateUndoPayload(bool isFolder)
        {
            var storagePath = Path.Combine(Path.GetTempPath(), "ZepUndo", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath));
            if (isFolder)
            {
                _createdUndoPayloads.Add(storagePath);
                Directory.CreateDirectory(storagePath);
            }
            else
            {
                using (new FileStream(storagePath, FileMode.CreateNew, FileAccess.Write))
                {
                    _createdUndoPayloads.Add(storagePath);
                }
            }

            return storagePath;
        }

        internal void DeleteCreatedUndoPayloads()
        {
            foreach (var path in _createdUndoPayloads)
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            _createdUndoPayloads.Clear();
        }

        // Captures the removed item's identity for replacement/deletion tests.
        internal Dictionary<string, string> StoredPaths { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default)
        {
            if (!Exists(assetPath))
            {
                return ProjectFileOperationResult.Failure(
                    "'" + assetPath.Value + "' could not be found.", assetPath.Value);
            }

            if (payloadPath.IsEmpty)
            {
                payloadPath = AssetPath.From(CreateUndoPayload(IsFolder(assetPath)));
            }

            StoredPaths[payloadPath.Value] = assetPath.Value;
            _source.RemovePath(assetPath.Value);
            return ProjectFileOperationResult.Success(payloadPath.Value, assetPath.Value);
        }

        internal bool WasStoredForUndo(string assetPath) =>
            StoredPaths.Values.Any(original =>
                string.Equals(original, assetPath, StringComparison.OrdinalIgnoreCase));

        public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) =>
            ProjectFileOperationResult.Failure("Not used.");

        public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) =>
            ProjectFileOperationResult.Failure("Not used.");

        public IReadOnlyList<string> GetTagIds(AssetPath assetPath)
        {
            return _tagIdsByPath.TryGetValue(assetPath.Value, out var tagIds)
                ? tagIds
                : Array.Empty<string>();
        }

        // This store is path-keyed, so the GUID adds nothing here.
        public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) =>
            GetTagIds(assetPath);

        public ProjectFileOperationResult SetTagIds(
            IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath)
        {
            foreach (var entry in tagIdsByAssetPath)
            {
                _tagIdsByPath[entry.Key.Value] = entry.Value.ToArray();
            }

            return ProjectFileOperationResult.Success(string.Empty, string.Empty);
        }

        public bool Open(AssetPath assetPath) => Exists(assetPath);

        public void Reveal(AssetPath assetPath)
        {
        }

        // One per RunBulk: the controller opens the asset batch and the undo
        // execution group in the same place, so this counts the bulk scopes a
        // batch actually ran under.
        internal int BatchScopes { get; private set; }

        public void BeginBatch()
        {
            BatchScopes++;
        }

        public void EndBatch()
        {
        }

        public string GetGuid(AssetPath assetPath)
        {

            var record = _source.BuildRecord(assetPath);
            return record != null ? record.Guid : string.Empty;
        }

        public string GetPathForGuid(string assetGuid)
        {
            var record = _source.Records.FirstOrDefault(candidate =>
                string.Equals(candidate.Guid, assetGuid, StringComparison.OrdinalIgnoreCase));
            return record != null ? record.AssetPath : string.Empty;
        }

        public string ResolveAbsolutePath(AssetPath assetPath) => string.Empty;

        public ProjectTagStateSnapshot CaptureTagState()
        {
            return new ProjectTagStateSnapshot(
                TagDefinitions == null
                    ? Array.Empty<ZepTag>()
                    : TagDefinitions.GetOrderedTags(),
                BuildAssignmentsByGuid());
        }

        public ProjectFileOperationResult CompareAndSetTagState(
            ProjectTagStateSnapshot expected,
            ProjectTagStateSnapshot desired)
        {
            var current = CaptureTagState();
            if (!DefinitionsMatch(expected.Definitions, current.Definitions))
            {
                return ProjectFileOperationResult.Failure(
                    "Tag definitions changed before this operation could be applied.");
            }

            TagDefinitions?.ReplaceAll(desired.Definitions);
            _tagIdsByPath.Clear();
            foreach (var entry in desired.AssignmentsByGuid)
            {
                var path = GetPathForGuid(entry.Key);
                if (!string.IsNullOrEmpty(path))
                {
                    _tagIdsByPath[path] = entry.Value.ToArray();
                }
            }

            return ProjectFileOperationResult.Success(string.Empty, string.Empty);
        }

        private Dictionary<string, IReadOnlyList<string>> BuildAssignmentsByGuid()
        {
            var assignments = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _tagIdsByPath)
            {
                var guid = GetGuid(AssetPath.From(entry.Key));
                if (!string.IsNullOrEmpty(guid))
                {
                    assignments[guid] = entry.Value;
                }
            }

            return assignments;
        }

        private static bool DefinitionsMatch(
            IReadOnlyList<ZepTag> left,
            IReadOnlyList<ZepTag> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed class RigScheduler : IDeferredCallScheduler
    {
        private readonly List<Action> _actions = new List<Action>();

        public void Schedule(Action action)
        {
            if (action != null && !_actions.Contains(action))
            {
                _actions.Add(action);
            }
        }

        public void Cancel(Action action)
        {
            _actions.Remove(action);
        }

        internal void RunAll()
        {
            while (_actions.Count > 0)
            {
                var action = _actions[0];
                _actions.RemoveAt(0);
                action();
            }
        }
    }

    internal sealed class RigShellReveal : IShellRevealService
    {
        public void Reveal(string absolutePath)
        {
        }
    }

    internal sealed class RigAddressablesQuery : IAddressablesQuery
    {
        // Settable so a shell test can see both faces of the package gate.
        public bool IsInstalled { get; set; }

        public event Action Changed
        {
            add { }
            remove { }
        }

        public AddressablesCapture Capture()
        {
            return new AddressablesCapture(
                Array.Empty<string>(),
                Array.Empty<string>(),
                isComplete: true);
        }

        public HashSet<string> Expand(
            AddressablesCapture capture,
            IReadOnlyList<AssetRecord> records)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
