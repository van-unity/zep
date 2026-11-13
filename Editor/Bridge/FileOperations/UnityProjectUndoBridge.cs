using System;
using Zep.Core.FileOperations;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.FileOperations
{
    // A serialized depth cursor is the only object registered with Unity Undo.
    // Unity owns its chronology; ProjectCommandHistory owns the actual inverse
    // filesystem commands. Comparing the two makes unrelated Inspector/scene
    // operations naturally interleave without guessing from focus or shortcuts.
    internal sealed class ZepUnityUndoCursor : ScriptableObject
    {
        [SerializeField] private string _sessionId = string.Empty;
        [SerializeField] private int _revision;

        internal string SessionId => _sessionId;
        internal int Revision => _revision;

        internal void Initialize(string sessionId, int revision)
        {
            _sessionId = sessionId ?? string.Empty;
            _revision = Math.Max(0, revision);
        }

        internal void SetRevision(int revision)
        {
            _revision = Math.Max(0, revision);
        }
    }

    internal sealed class UnityProjectUndoBridge : IDisposable
    {
        private readonly ZepUnityUndoCursor _cursor;
        private readonly SessionUndoHistoryStorage _storage;
        // The replay loop below is the one place that knows k inverse commands
        // belong to a single native step, so it is the one place that can tell
        // the history to persist the stacks once for the whole step.
        private readonly ProjectCommandHistory _history;
        private readonly Func<bool> _undo;
        private readonly Func<bool> _redo;
        private readonly Action<Action> _runBulk;
        private readonly Action _clearRedo;
        private readonly EditorApplication.CallbackFunction _delayedReconciliation;

        private bool _isDisposed;
        private bool _isRegistering;
        private bool _isApplying;
        private bool _delayScheduled;
        // ObjectChangeEvents.changesPublished is deferred: it arrives after
        // PerformUndo has returned, with Undo.isProcessing already back to false
        // (measured on 6000.3.10f1). The synchronous guards therefore cannot tell
        // an undo-caused publication from a new edit, and treating the former as a
        // branch cut destroys the panel redo that Unity's own redo stack still
        // holds. This latch marks the next publication as self-caused.
        private bool _expectSelfCausedPublication;

        internal UnityProjectUndoBridge(
            ZepUnityUndoCursor cursor,
            SessionUndoHistoryStorage storage,
            ProjectCommandHistory history,
            Func<bool> undo,
            Func<bool> redo,
            Action<Action> runBulk,
            Action clearRedo)
        {
            _cursor = cursor ?? throw new ArgumentNullException(nameof(cursor));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _history = history ?? throw new ArgumentNullException(nameof(history));
            _undo = undo ?? throw new ArgumentNullException(nameof(undo));
            _redo = redo ?? throw new ArgumentNullException(nameof(redo));
            _runBulk = runBulk ?? throw new ArgumentNullException(nameof(runBulk));
            _clearRedo = clearRedo ?? throw new ArgumentNullException(nameof(clearRedo));
            _delayedReconciliation = ReconcileWhenEditorIsReady;

            Undo.undoRedoEvent += OnUndoRedo;
            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.willFlushUndoRecord += OnWillFlushUndoRecord;
            ObjectChangeEvents.changesPublished += OnObjectChangesPublished;

            if (_cursor.Revision != _storage.AppliedNativeRevision)
            {
                ScheduleReconciliation();
            }
        }

        internal static UnityProjectUndoBridge Create(
            SessionUndoHistoryStorage storage,
            ProjectCommandHistory history,
            Func<bool> undo,
            Func<bool> redo,
            Action<Action> runBulk,
            Action clearRedo)
        {
            if (storage == null) throw new ArgumentNullException(nameof(storage));
            var cursor = FindOrCreateCursor(storage);
            return new UnityProjectUndoBridge(cursor, storage, history, undo, redo, runBulk, clearRedo);
        }

        internal void RecordExecuted(ProjectCommandRecord record, int commandCount)
        {
            if (_isDisposed || record == null || commandCount <= 0)
            {
                return;
            }

            try
            {
                // A deferred native undo must settle before another panel command
                // can be represented. This race is exceptionally narrow (the
                // editor normally drains delayCall before accepting more input),
                // but failing closed is safer than registering corrupt chronology.
                if (_cursor.SessionId != _storage.SessionId
                    || _cursor.Revision != _storage.AppliedNativeRevision)
                {
                    ResetTimeline("A panel operation arrived before pending undo reconciliation completed.");
                    return;
                }

                var nextRevision = checked(_cursor.Revision + commandCount);
                var operationName = GetOperationName(record.Kind, commandCount);

                // A new panel command is a new action, so nothing still pending
                // from an earlier undo may be treated as self-caused after it.
                // This is what bounds the latch: it is armed by an undo/redo or a
                // replay, consumed by the publication that follows, and cleared
                // here the moment real work is recorded again.
                _expectSelfCausedPublication = false;

                _isRegistering = true;
                Undo.IncrementCurrentGroup();
                var group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName(operationName);
                Undo.RegisterCompleteObjectUndo(_cursor, operationName);
                _cursor.SetRevision(nextRevision);
                EditorUtility.SetDirty(_cursor);
                _storage.SetAppliedNativeRevision(nextRevision);
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
            catch (Exception ex)
            {
                ResetTimeline("Unity could not register the panel operation: " + ex.Message);
            }
            finally
            {
                _isRegistering = false;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            Undo.undoRedoEvent -= OnUndoRedo;
            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.willFlushUndoRecord -= OnWillFlushUndoRecord;
            ObjectChangeEvents.changesPublished -= OnObjectChangesPublished;
            EditorApplication.delayCall -= _delayedReconciliation;
            _delayScheduled = false;
        }

        internal void InvalidateTimeline()
        {
            if (_isDisposed)
            {
                return;
            }

            EditorApplication.delayCall -= _delayedReconciliation;
            _delayScheduled = false;
            ResetTimelineState();
        }

        internal bool PrepareForNewCommand()
        {
            if (_isDisposed || _isRegistering || _isApplying || Undo.isProcessing
                || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return false;
            }

            if (string.Equals(_cursor.SessionId, _storage.SessionId, StringComparison.Ordinal)
                && _cursor.Revision == _storage.AppliedNativeRevision)
            {
                return true;
            }

            EditorApplication.delayCall -= _delayedReconciliation;
            _delayScheduled = false;
            Reconcile();
            return !_isDisposed
                && string.Equals(_cursor.SessionId, _storage.SessionId, StringComparison.Ordinal)
                && _cursor.Revision == _storage.AppliedNativeRevision;
        }

        // For a change the caller has ALREADY made and now only needs recorded
        // (a recipe-created asset). Creating a script starts a compile before
        // its record arrives, and the new-command gate above refuses while
        // compiling - so every new script failed to record, which wiped the
        // whole history. A compile does not stop Unity taking an undo record;
        // what it does stop is replaying a pending reconciliation, so only a
        // timeline that is already level is accepted while one runs.
        internal bool PrepareForAppliedCommand()
        {
            if (!EditorApplication.isCompiling)
            {
                return PrepareForNewCommand();
            }

            return !_isDisposed && !_isRegistering && !_isApplying && !Undo.isProcessing
                && !EditorApplication.isUpdating
                && string.Equals(_cursor.SessionId, _storage.SessionId, StringComparison.Ordinal)
                && _cursor.Revision == _storage.AppliedNativeRevision;
        }

        internal void ReconcileNowForTests()
        {
            EditorApplication.delayCall -= _delayedReconciliation;
            _delayScheduled = false;
            Reconcile();
        }

        private static ZepUnityUndoCursor FindOrCreateCursor(SessionUndoHistoryStorage storage)
        {
            // The stored id resolves the EXACT cursor object Unity's native undo
            // stack references across a domain reload; the type scan below can only
            // match by session id. Unity 6.5 stores the id as an EntityId (the int
            // APIs are obsolete errors there); 6.3 resolves the same int through
            // EntityId; 6.0-6.2 predate EntityId entirely.
#if UNITY_6000_5_OR_NEWER
            var storedCursor = EditorUtility.EntityIdToObject(
                SessionState.GetEntityId(storage.CursorInstanceKey, EntityId.None)) as ZepUnityUndoCursor;
#elif UNITY_6000_3_OR_NEWER
            var instanceId = SessionState.GetInt(storage.CursorInstanceKey, 0);
            var storedCursor = EditorUtility.EntityIdToObject((EntityId)instanceId) as ZepUnityUndoCursor;
#else
            var instanceId = SessionState.GetInt(storage.CursorInstanceKey, 0);
            var storedCursor = EditorUtility.InstanceIDToObject(instanceId) as ZepUnityUndoCursor;
#endif
            var cursor = storedCursor != null
                && string.Equals(storedCursor.SessionId, storage.SessionId, StringComparison.Ordinal)
                    ? storedCursor
                    : null;

            // Exactly one host owns the panel timeline. A schema/session change
            // after domain reload leaves the old hidden cursor object alive in
            // Unity's native stack; remove every nonmatching/duplicate cursor so
            // users never traverse stale no-op "Zep" entries.
            var cursors = Resources.FindObjectsOfTypeAll<ZepUnityUndoCursor>();
            for (var i = 0; i < cursors.Length; i++)
            {
                var candidate = cursors[i];
                if (candidate == cursor)
                {
                    continue;
                }

                if (cursor == null
                    && string.Equals(candidate.SessionId, storage.SessionId, StringComparison.Ordinal))
                {
                    cursor = candidate;
                    continue;
                }

                ClearUndoSafely(candidate);
                UnityEngine.Object.DestroyImmediate(candidate);
            }

            if (cursor == null)
            {
                // Losing the native object means its place in Unity's timeline is
                // unknowable. Never attach persisted commands to a fabricated depth.
                if (storage.HasHistory || storage.AppliedRevision != 0)
                {
                    storage.Reset();
                }

                cursor = ScriptableObject.CreateInstance<ZepUnityUndoCursor>();
                cursor.name = "Zep Undo Cursor";
                cursor.hideFlags = HideFlags.HideAndDontSave;
                cursor.Initialize(storage.SessionId, 0);
            }

            if (!string.Equals(cursor.SessionId, storage.SessionId, StringComparison.Ordinal)
                || cursor.Revision < 0)
            {
                ClearUndoSafely(cursor);
                storage.Reset();
                cursor.Initialize(storage.SessionId, 0);
            }

#if UNITY_6000_5_OR_NEWER
            SessionState.SetEntityId(storage.CursorInstanceKey, cursor.GetEntityId());
#else
            SessionState.SetInt(storage.CursorInstanceKey, cursor.GetInstanceID());
#endif
            return cursor;
        }

        private static void ClearUndoSafely(UnityEngine.Object target)
        {
            try
            {
                Undo.ClearUndo(target);
            }
            catch (Exception)
            {
                // Resetting the session still prevents an old cursor snapshot
                // from being interpreted as a current filesystem command.
            }
        }

        private void OnUndoRedo(in UndoRedoInfo info)
        {
            // Unity publishes this undo's object changes later in the same tick,
            // once isProcessing is false again. Claim that publication now so it
            // is not mistaken for a new edit and used to cut the redo branch.
            _expectSelfCausedPublication = true;
            ScheduleReconciliation();
        }

        private UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            try
            {
                if (_isDisposed
                    || _isRegistering
                    || _isApplying
                    || Undo.isProcessing
                    || modifications == null
                    || _cursor.Revision != _storage.AppliedNativeRevision)
                {
                    return modifications;
                }

                for (var i = 0; i < modifications.Length; i++)
                {
                    var currentTarget = modifications[i].currentValue?.target;
                    var previousTarget = modifications[i].previousValue?.target;
                    if ((currentTarget != null && currentTarget != _cursor)
                        || (previousTarget != null && previousTarget != _cursor))
                    {
                        _clearRedo();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Zep] Could not invalidate redo history: " + ex.Message);
            }

            // This callback is part of Unity's global pipeline. Never modify or
            // swallow another editor's property modifications.
            return modifications;
        }

        private void OnWillFlushUndoRecord()
        {
            try
            {
                if (_isDisposed
                    || _isRegistering
                    || _isApplying
                    || Undo.isProcessing
                    || _cursor.Revision != _storage.AppliedNativeRevision)
                {
                    return;
                }

                // RegisterCompleteObjectUndo and object lifecycle APIs do not
                // necessarily produce property modifications. Flushing any
                // external native record still creates a new branch, so panel
                // redo must be invalidated just as Unity invalidates its redo side.
                _clearRedo();
            }
            catch (Exception ex)
            {
                Debug.LogError("[Zep] Could not invalidate redo history: " + ex.Message);
            }
        }

        private void OnObjectChangesPublished(ref ObjectChangeEventStream stream)
        {
            try
            {
                if (_isDisposed
                    || _isRegistering
                    || _isApplying
                    || Undo.isProcessing
                    || _cursor.Revision != _storage.AppliedNativeRevision
                    || !stream.isCreated
                    || stream.length == 0)
                {
                    return;
                }

                // An undo/redo — Unity's own or the panel's replay — is not a new
                // action and must not cut either redo branch. Unity keeps its redo
                // stack across an undo; cutting ours here desynchronised the two,
                // and the next redo of a panel marker then found no command,
                // which reset the whole timeline and lost the user's history.
                if (_expectSelfCausedPublication)
                {
                    _expectSelfCausedPublication = false;
                    return;
                }

                // AssetDatabase import/restore fallout is published as asset-only
                // object changes even when it did not create a new native Undo
                // record. Treating those notifications as a branch cut erases the
                // panel redo that the replay just created. Actual external asset
                // Undo registration is covered by postprocessModifications and
                // willFlushUndoRecord; this fallback is only for scene/hierarchy
                // APIs that bypass those callbacks.
                for (var i = 0; i < stream.length; i++)
                {
                    var changeKind = stream.GetEventType(i);
                    if (changeKind == ObjectChangeKind.ChangeAssetObjectProperties)
                    {
                        stream.GetChangeAssetObjectPropertiesEvent(i, out var change);
                        // entityId replaced instanceId in 6.5, where the int pair
                        // is an obsolete error; 6.3-6.4 only expose instanceId.
#if UNITY_6000_5_OR_NEWER
                        if (change.entityId == _cursor.GetEntityId())
#else
                        if (change.instanceId == _cursor.GetInstanceID())
#endif
                        {
                            return;
                        }

                        continue;
                    }

                    if (changeKind == ObjectChangeKind.CreateAssetObject
                        || changeKind == ObjectChangeKind.DestroyAssetObject)
                    {
                        continue;
                    }

                    _clearRedo();
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError("[Zep] Could not invalidate redo history: " + ex.Message);
            }
        }

        private void ScheduleReconciliation()
        {
            if (_isDisposed || _delayScheduled)
            {
                return;
            }

            _delayScheduled = true;
            EditorApplication.delayCall += _delayedReconciliation;
        }

        private void ReconcileWhenEditorIsReady()
        {
            _delayScheduled = false;
            if (_isDisposed)
            {
                return;
            }

            if (Undo.isProcessing || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                ScheduleReconciliation();
                return;
            }

            Reconcile();
        }

        private void Reconcile()
        {
            if (_isDisposed || _isApplying)
            {
                return;
            }

            if (!string.Equals(_cursor.SessionId, _storage.SessionId, StringComparison.Ordinal)
                || _cursor.Revision < 0)
            {
                ResetTimeline("The persisted panel history no longer matches Unity's undo cursor.");
                return;
            }

            var targetRevision = _cursor.Revision;
            var startingRevision = _storage.AppliedNativeRevision;
            if (targetRevision == startingRevision)
            {
                // Deliberately does NOT disarm the latch. Reconcile can run
                // before the deferred publication arrives (PrepareForNewCommand
                // reconciles synchronously, and a foreign undo reaches here with
                // no gap to close), so clearing it here would hand the undo's own
                // publication straight to the branch cut this latch exists to
                // prevent. A new native record is what disarms it — see
                // RecordExecuted and the two synchronous record callbacks.
                return;
            }

            _isApplying = true;
            try
            {
                Action replay = () => ReplayTo(targetRevision);
                if (Math.Abs((long)targetRevision - startingRevision) > 1L)
                {
                    _runBulk(replay);
                }
                else
                {
                    replay();
                }
            }
            catch (Exception ex)
            {
                ResetTimeline("Panel undo reconciliation failed: " + ex.Message);
            }
            finally
            {
                _isApplying = false;
                // The replay's own AssetDatabase fallout publishes after this
                // returns, when _isApplying no longer guards anything. Claim it
                // so the replay cannot erase the redo entry it just created.
                _expectSelfCausedPublication = true;
            }

            if (!_isDisposed && _cursor.Revision != _storage.AppliedNativeRevision)
            {
                ScheduleReconciliation();
            }
        }

        private void ReplayTo(int targetRevision)
        {
            var appliedCommandCount = 0;
            // Every command below transfers one record between the stacks, and
            // each transfer used to re-serialise both stacks into session
            // storage. The stacks only have to be durable in the shape this
            // whole step leaves them in — including the compensated shape when
            // a command in the middle fails, which is why the release sits in a
            // finally around the loop AND its compensation.
            _history.BeginSaveSuppression();
            try
            {
                while (_storage.AppliedNativeRevision != targetRevision)
                {
                    var previousRevision = _storage.AppliedNativeRevision;
                    var isUndo = targetRevision < previousRevision;
                    var nextRevision = previousRevision + (isUndo ? -1 : 1);
                    _storage.SetAppliedNativeRevision(nextRevision);

                    try
                    {
                        var succeeded = isUndo ? _undo() : _redo();
                        if (!succeeded)
                        {
                            // Core deliberately discards a dead command, so the
                            // remaining stack no longer has a one-to-one revision
                            // mapping. Continuing would apply an older command at
                            // this marker on the next native step. Fail closed and
                            // let Reconcile reset both timelines.
                            throw new InvalidOperationException(
                                "A panel filesystem command could not be replayed.");
                        }
                    }
                    catch
                    {
                        _storage.SetAppliedNativeRevision(previousRevision);
                        _storage.Save();
                        CompensateReplay(isUndo, appliedCommandCount);
                        throw;
                    }

                    appliedCommandCount++;
                }
            }
            finally
            {
                _history.EndSaveSuppression();
            }
        }

        private void CompensateReplay(bool wasUndo, int appliedCommandCount)
        {
            for (var i = 0; i < appliedCommandCount; i++)
            {
                var previousRevision = _storage.AppliedNativeRevision;
                var nextRevision = previousRevision + (wasUndo ? 1 : -1);
                _storage.SetAppliedNativeRevision(nextRevision);
                var succeeded = wasUndo ? _redo() : _undo();
                if (!succeeded)
                {
                    _storage.SetAppliedNativeRevision(previousRevision);
                    _storage.Save();
                    throw new InvalidOperationException(
                        "A partially applied panel batch could not be rolled back safely.");
                }
            }
        }

        private void ResetTimeline(string reason)
        {
            ResetTimelineState();
            Debug.LogWarning("[Zep] " + reason + " The panel undo timeline was reset safely.");
        }

        private void ResetTimelineState()
        {
            ClearUndoSafely(_cursor);

            try
            {
                _storage.Reset();
                _cursor.Initialize(_storage.SessionId, 0);
                EditorUtility.SetDirty(_cursor);
#if UNITY_6000_5_OR_NEWER
                SessionState.SetEntityId(_storage.CursorInstanceKey, _cursor.GetEntityId());
#else
                SessionState.SetInt(_storage.CursorInstanceKey, _cursor.GetInstanceID());
#endif
            }
            catch (Exception ex)
            {
                Debug.LogError("[Zep] Undo timeline reset failed: " + ex.Message);
            }
        }

        private static string GetOperationName(ProjectCommandKind kind, int commandCount)
        {
            if (commandCount > 1)
            {
                return "Zep: Batch (" + commandCount + " operations)";
            }

            switch (kind)
            {
                case ProjectCommandKind.CreateFolder:
                    return "Zep: Create Folder";
                case ProjectCommandKind.Rename:
                    return "Zep: Rename";
                case ProjectCommandKind.Duplicate:
                    return "Zep: Duplicate";
                case ProjectCommandKind.Move:
                    return "Zep: Move";
                case ProjectCommandKind.Delete:
                    return "Zep: Delete";
                case ProjectCommandKind.SetTags:
                    return "Zep: Change Tags";
                case ProjectCommandKind.CreateAsset:
                    return "Zep: Create Asset";
                case ProjectCommandKind.SetTagDefinitions:
                    return "Zep: Change Tag Definitions";
                default:
                    return "Zep: Change Assets";
            }
        }
    }
}
