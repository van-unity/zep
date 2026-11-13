using System;
using System.Collections;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    public sealed class UnityProjectUndoBridgeTests
    {
        private string _stateKey;
        private SessionUndoHistoryStorage _storage;
        private ZepUnityUndoCursor _cursor;
        private GameObject _unrelatedObject;
        private ProjectCommandHistory _history;
        private UnityProjectUndoBridge _bridge;

        [SetUp]
        public void SetUp()
        {
            _undoObserved = null;
            _redoObserved = null;
            _failRedo = false;
            _failUndoAtCall = 0;
            _undoCallCount = 0;
            _stateKey = "Zep.Tests.Undo." + Guid.NewGuid().ToString("N");
            _storage = new SessionUndoHistoryStorage(_stateKey);
            _cursor = ScriptableObject.CreateInstance<ZepUnityUndoCursor>();
            _cursor.hideFlags = HideFlags.HideAndDontSave;
            _cursor.Initialize(_storage.SessionId, 0);
            _unrelatedObject = new GameObject("Zep Undo Test Object");
            _unrelatedObject.hideFlags = HideFlags.HideAndDontSave;

            _historySaveCount = 0;
            _undoDepthAtLastHistorySave = -1;
            _redoDepthAtLastHistorySave = -1;
            _history = new ProjectCommandHistory(
                _storage.UndoStack,
                _storage.RedoStack,
                RecordHistorySave);
            _history.Trimmed += _storage.NoteTrimmedRecords;
            _bridge = new UnityProjectUndoBridge(
                _cursor,
                _storage,
                _history,
                UndoPanelCommand,
                RedoPanelCommand,
                work => work(),
                () => _history.ClearRedo());
            _history.Executed += _bridge.RecordExecuted;
            _history.Invalidated += _bridge.InvalidateTimeline;
        }

        [TearDown]
        public void TearDown()
        {
            if (_history != null && _bridge != null)
            {
                _history.Executed -= _bridge.RecordExecuted;
                _history.Invalidated -= _bridge.InvalidateTimeline;
            }

            _bridge?.Dispose();
            if (_cursor != null)
            {
                Undo.ClearUndo(_cursor);
                UnityEngine.Object.DestroyImmediate(_cursor);
            }

            if (_unrelatedObject != null)
            {
                Undo.ClearUndo(_unrelatedObject);
                UnityEngine.Object.DestroyImmediate(_unrelatedObject);
            }

            _storage?.EraseForTests();
        }

        [Test]
        public void UnityTimeline_InterleavesUnrelatedInspectorChangeWithPanelCommand()
        {
            var panelUndoCount = 0;
            var panelRedoCount = 0;
            _undoObserved = () => panelUndoCount++;
            _redoObserved = () => panelRedoCount++;

            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            RegisterUnrelatedChange(3);

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(_unrelatedObject.layer, Is.Zero);
            Assert.That(panelUndoCount, Is.Zero, "the newer Inspector change must undo first");

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            _bridge.ReconcileNowForTests();
            Assert.That(panelUndoCount, Is.EqualTo(1), "duplicate callbacks must not replay twice");
            Assert.That(_history.CanRedo, Is.True);

            Undo.PerformRedo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelRedoCount, Is.EqualTo(1));
            Assert.That(_unrelatedObject.layer, Is.Zero);

            Undo.PerformRedo();
            _bridge.ReconcileNowForTests();
            Assert.That(_unrelatedObject.layer, Is.EqualTo(3));
            Assert.That(panelRedoCount, Is.EqualTo(1), "unrelated redo must not replay a panel command");
        }

        [Test]
        public void BulkPanelGroup_UndoesAndRedoesAllCommandsInOneUnityStep()
        {
            var panelUndoCount = 0;
            var panelRedoCount = 0;
            var bulkCount = 0;
            _undoObserved = () => panelUndoCount++;
            _redoObserved = () => panelRedoCount++;

            _bridge.Dispose();
            _history.Executed -= _bridge.RecordExecuted;
            _history.Invalidated -= _bridge.InvalidateTimeline;
            _bridge = new UnityProjectUndoBridge(
                _cursor,
                _storage,
                _history,
                UndoPanelCommand,
                RedoPanelCommand,
                work =>
                {
                    bulkCount++;
                    work();
                },
                () => _history.ClearRedo());
            _history.Executed += _bridge.RecordExecuted;
            _history.Invalidated += _bridge.InvalidateTimeline;

            _history.BeginExecutionGroup();
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            _history.EndExecutionGroup();
            Assert.That(_cursor.Revision, Is.EqualTo(3));

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelUndoCount, Is.EqualTo(3));
            Assert.That(bulkCount, Is.EqualTo(1));

            Undo.PerformRedo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelRedoCount, Is.EqualTo(3));
            Assert.That(bulkCount, Is.EqualTo(2));
        }

        [Test]
        public void SessionStorage_ReloadsStacksAndAppliedRevisionAtomically()
        {
            var sessionId = _storage.SessionId;
            _storage.UndoStack.Add(NewRecord(ProjectCommandKind.CreateFolder));
            _storage.SetAppliedRevision(1);
            _storage.Save();

            var reloaded = new SessionUndoHistoryStorage(_stateKey);

            Assert.That(reloaded.SessionId, Is.EqualTo(sessionId));
            Assert.That(reloaded.AppliedRevision, Is.EqualTo(1));
            Assert.That(reloaded.UndoStack, Has.Count.EqualTo(1));
            Assert.That(reloaded.UndoStack[0].Kind, Is.EqualTo(ProjectCommandKind.CreateFolder));
            Assert.That(reloaded.RedoStack, Is.Empty);
        }

        [Test]
        public void SessionStorage_RejectsRevisionThatDoesNotMatchAppliedStack()
        {
            SessionState.SetString(
                _stateKey,
                "{\"SessionId\":\"corrupt\",\"AppliedRevision\":2,"
                + "\"UndoStack\":[{\"Kind\":0,\"PrimaryPath\":\"Assets\","
                + "\"SecondaryPath\":\"Assets/Test\",\"AssetGuid\":\"guid-test\"}],"
                + "\"RedoStack\":[]}");

            var reloaded = new SessionUndoHistoryStorage(_stateKey);

            Assert.That(reloaded.SessionId, Is.Not.EqualTo("corrupt"));
            Assert.That(reloaded.AppliedRevision, Is.Zero);
            Assert.That(reloaded.UndoStack, Is.Empty);
            Assert.That(reloaded.RedoStack, Is.Empty);
        }

        [Test]
        public void SessionStorage_RejectsDuplicateTagDefinitionIdentity()
        {
            SessionState.SetString(
                _stateKey,
                "{\"SessionId\":\"corrupt\",\"AppliedRevision\":1,"
                + "\"UndoStack\":[{\"Kind\":9,"
                + "\"DesiredTagDefinitions\":["
                + "{\"Id\":\"red\",\"Name\":\"Red\",\"Color\":1},"
                + "{\"Id\":\"red\",\"Name\":\"Other Red\",\"Color\":1}],"
                + "\"OriginalTagDefinitions\":["
                + "{\"Id\":\"red\",\"Name\":\"Red\",\"Color\":1}],"
                + "\"DesiredGuidTagAssignments\":[],"
                + "\"OriginalGuidTagAssignments\":[]}],\"RedoStack\":[]}");

            var reloaded = new SessionUndoHistoryStorage(_stateKey);

            Assert.That(reloaded.SessionId, Is.Not.EqualTo("corrupt"));
            Assert.That(reloaded.AppliedRevision, Is.Zero);
            Assert.That(reloaded.UndoStack, Is.Empty);
            Assert.That(reloaded.RedoStack, Is.Empty);
        }

        [Test]
        public void FailedReplay_ResetsTimelineInsteadOfShiftingOlderCommands()
        {
            var panelUndoCount = 0;
            _undoObserved = () => panelUndoCount++;
            RegisterUnrelatedChange(9);

            _history.PushExecuted(NewRecord(ProjectCommandKind.CreateFolder).ToCommand());
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelUndoCount, Is.EqualTo(1));
            Assert.That(_history.CanRedo, Is.True);

            _failRedo = true;
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Panel undo reconciliation failed: A panel filesystem command could not be replayed. The panel undo timeline was reset safely.");
            Undo.PerformRedo();
            _bridge.ReconcileNowForTests();

            Assert.That(_history.CanUndo, Is.False);
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_cursor.Revision, Is.Zero);

            _failRedo = false;
            RegisterUnrelatedChange(11);
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelUndoCount, Is.EqualTo(1),
                "an older command must not slide into the failed marker's revision");
            Assert.That(_unrelatedObject.layer, Is.EqualTo(9),
                "the test-owned native change still undoes normally after the reset");
        }

        [Test]
        public void HistoryInvalidation_RemovesManagedAndNativePanelTimeline()
        {
            var originalSessionId = _storage.SessionId;
            var panelUndoCount = 0;
            _undoObserved = () => panelUndoCount++;
            RegisterUnrelatedChange(5);
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());

            _history.Invalidate();

            Assert.That(_history.CanUndo, Is.False);
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(_storage.SessionId, Is.Not.EqualTo(originalSessionId));
            Assert.That(_cursor.SessionId, Is.EqualTo(_storage.SessionId));
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_cursor.Revision, Is.Zero);

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(panelUndoCount, Is.Zero);
            Assert.That(_unrelatedObject.layer, Is.Zero);
        }

        [UnityTest]
        public IEnumerator ExternalNativeRecordFlush_ClearsPanelRedoBranch()
        {
            yield return null; // drain setup object creation
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            yield return null; // publish the panel cursor change first
            Assert.That(_history.CanRedo, Is.True);

            RegisterUnrelatedChange(7);
            Undo.FlushUndoRecordObjects();
            yield return null;

            Assert.That(_history.CanRedo, Is.False,
                "a new non-property native record must invalidate panel redo too");
        }

        [Test]
        public void EmptyNativeFlush_DoesNotClearPanelRedoBranch()
        {
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(_history.CanRedo, Is.True);

            Undo.FlushUndoRecordObjects();

            Assert.That(_history.CanRedo, Is.True,
                "finalizing an empty native group must not cut the redo branch");
        }

        [Test]
        public void PendingNativeUndo_IsReconciledBeforeANewPanelCommandIsRecorded()
        {
            var panelUndoCount = 0;
            _undoObserved = () => panelUndoCount++;
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());

            Undo.PerformUndo();
            Assert.That(_storage.AppliedRevision, Is.EqualTo(1),
                "the delayed bridge has not replayed yet");

            Assert.That(_bridge.PrepareForNewCommand(), Is.True);
            Assert.That(panelUndoCount, Is.EqualTo(1));
            Assert.That(_storage.AppliedRevision, Is.Zero);

            _history.PushExecuted(NewRecord(ProjectCommandKind.CreateFolder).ToCommand());
            Assert.That(_cursor.Revision, Is.EqualTo(1));
            Assert.That(_storage.AppliedRevision, Is.EqualTo(1));
            Assert.That(_history.CanUndo, Is.True);
            Assert.That(_history.CanRedo, Is.False);
        }

        [Test]
        public void FailedMiddleCommand_CompensatesEarlierBatchChangesBeforeReset()
        {
            var modeledAppliedCommands = 3;
            _undoObserved = () => modeledAppliedCommands--;
            _redoObserved = () => modeledAppliedCommands++;
            _failUndoAtCall = 2;

            _history.BeginExecutionGroup();
            _history.PushExecuted(NewRecord(ProjectCommandKind.CreateFolder).ToCommand());
            _history.PushExecuted(NewRecord(ProjectCommandKind.Duplicate).ToCommand());
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            _history.EndExecutionGroup();

            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Panel undo reconciliation failed: A panel filesystem command could not be replayed. The panel undo timeline was reset safely.");
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();

            Assert.That(modeledAppliedCommands, Is.EqualTo(3),
                "the successful first inverse must be compensated after the second fails");
            Assert.That(_history.CanUndo, Is.False);
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_cursor.Revision, Is.Zero);
        }

        [Test]
        public void InvalidationAfterPanelUndo_RemovesTheNativeRedoMarker()
        {
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            Assert.That(_history.CanRedo, Is.True);

            _history.Invalidate();
            var sessionAfterInvalidation = _storage.SessionId;
            Undo.PerformRedo();
            _bridge.ReconcileNowForTests();

            Assert.That(_storage.SessionId, Is.EqualTo(sessionAfterInvalidation));
            Assert.That(_cursor.SessionId, Is.EqualTo(sessionAfterInvalidation));
            Assert.That(_cursor.Revision, Is.Zero);
        }

        // One native step stands for the whole group, so the replay below runs
        // 50 inverse commands. Persisting after each one re-serialises both
        // stacks 50 times for a single Cmd+Z; the stacks only have to be durable
        // in the shape the step leaves them in.
        [Test]
        public void BulkGroupUndo_PersistsTheHistoryOnceForTheWholeNativeStep()
        {
            const int commandCount = 50;
            var panelUndoCount = 0;
            _undoObserved = () => panelUndoCount++;

            _history.BeginExecutionGroup();
            for (var i = 0; i < commandCount; i++)
            {
                _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            }

            _history.EndExecutionGroup();
            Assert.That(_cursor.Revision, Is.EqualTo(commandCount));

            var savesBeforeReplay = _historySaveCount;
            var previousPerfEnabled = ZepPerfLog.IsEnabled;
            var previousPerfSink = ZepPerfLog.Sink;
            long persistedWritesDuringReplay;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                Undo.PerformUndo();
                _bridge.ReconcileNowForTests();

                persistedWritesDuringReplay = ZepPerfLog
                    .CaptureSnapshot()
                    .GetCount(ZepPerfCounter.UndoHistorySaves);
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousPerfEnabled;
                ZepPerfLog.Sink = previousPerfSink;
                ZepPerfLog.Reset();
            }

            Assert.That(panelUndoCount, Is.EqualTo(commandCount), "the whole group must still replay");
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_history.CanUndo, Is.False);
            Assert.That(_history.CanRedo, Is.True, "redo must still be armed for the same step");
            Assert.That(
                _historySaveCount - savesBeforeReplay,
                Is.EqualTo(1),
                "a 50-command replay must persist the stacks once, not once per command");
            Assert.That(
                persistedWritesDuringReplay,
                Is.EqualTo(1L),
                "exactly one session-storage write may reach the undo state during the replay");
            Assert.That(
                _undoDepthAtLastHistorySave, Is.Zero,
                "the single save must carry the stacks in their FINAL replayed shape");
            Assert.That(_redoDepthAtLastHistorySave, Is.EqualTo(commandCount));
        }

        // The suppression window has to cover the compensation as well: a replay
        // that dies half way rolls the applied commands back, and it is THAT
        // stack shape which must reach storage, in one write.
        [Test]
        public void FailedBulkReplay_PersistsTheCompensatedStackInOneHistorySave()
        {
            const int commandCount = 20;
            const int failingCall = 12;
            _failUndoAtCall = failingCall;

            _history.BeginExecutionGroup();
            for (var i = 0; i < commandCount; i++)
            {
                _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            }

            _history.EndExecutionGroup();
            var savesBeforeReplay = _historySaveCount;

            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Panel undo reconciliation failed: A panel filesystem command could not be replayed. The panel undo timeline was reset safely.");
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();

            // Two history saves: the dead record's discard (immediate, outside
            // the transfer path) and the one write that persists the compensated
            // stacks. Before the suppression window this was 1 + 11 undo
            // transfers + 11 compensating transfers = 23.
            Assert.That(
                _historySaveCount - savesBeforeReplay,
                Is.EqualTo(2),
                "the failed replay must not persist once per applied and compensated command");
            Assert.That(
                _undoDepthAtLastHistorySave,
                Is.EqualTo(commandCount - 1),
                "the last save must carry the compensated stack, minus the discarded dead record");
            Assert.That(
                _redoDepthAtLastHistorySave, Is.Zero,
                "compensation must have moved every applied command back before that save");
            Assert.That(_history.CanUndo, Is.False, "the timeline still resets after a failed replay");
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_cursor.Revision, Is.Zero);
        }

        // The stacks live in session storage for the whole editor session and are
        // re-serialised on every save, so the undo stack has a ceiling. Dropping
        // the oldest records moves the stack out from under Unity's absolute
        // revisions, which is what the trimmed base exists to absorb.
        [Test]
        public void PushingPastTheUndoCeiling_DropsTheOldestRecordsAndKeepsTheRevisionInvariant()
        {
            const int pushCount = 250;
            const int expectedDropped = pushCount - ProjectCommandHistory.MaxUndoDepth;
            var panelUndoCount = 0;
            _undoObserved = () => panelUndoCount++;

            for (var i = 0; i < pushCount; i++)
            {
                _history.PushExecuted(NewRecordNamed(ProjectCommandKind.CreateFolder, i).ToCommand());
            }

            Assert.That(
                _storage.UndoStack, Has.Count.EqualTo(ProjectCommandHistory.MaxUndoDepth),
                "the undo stack must stop growing at its ceiling");
            Assert.That(
                _storage.UndoStack[0].PrimaryPath,
                Is.EqualTo("Assets/Test" + expectedDropped + ".txt"),
                "the OLDEST records are the ones dropped");
            Assert.That(
                _storage.UndoStack[_storage.UndoStack.Count - 1].PrimaryPath,
                Is.EqualTo("Assets/Test" + (pushCount - 1) + ".txt"),
                "the newest command must still be on top");
            Assert.That(
                _storage.AppliedRevision, Is.EqualTo(ProjectCommandHistory.MaxUndoDepth),
                "AppliedRevision == UndoStack.Count is what SessionUndoHistoryStorage.IsValid checks");
            Assert.That(
                _cursor.Revision, Is.EqualTo(pushCount),
                "Unity's cursor keeps counting absolute revisions; only the base moved");
            Assert.That(_storage.AppliedNativeRevision, Is.EqualTo(pushCount));

            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();

            Assert.That(panelUndoCount, Is.EqualTo(1), "the top of a trimmed stack must still replay");
            Assert.That(_storage.UndoStack, Has.Count.EqualTo(ProjectCommandHistory.MaxUndoDepth - 1));
            Assert.That(_storage.AppliedRevision, Is.EqualTo(ProjectCommandHistory.MaxUndoDepth - 1));
            Assert.That(_cursor.Revision, Is.EqualTo(pushCount - 1));
            Assert.That(
                _storage.RedoStack[0].PrimaryPath,
                Is.EqualTo("Assets/Test" + (pushCount - 1) + ".txt"),
                "the record that replayed must be the newest one, not a trimmed neighbour");

            var reloaded = new SessionUndoHistoryStorage(_stateKey);
            Assert.That(
                reloaded.AppliedRevision, Is.EqualTo(ProjectCommandHistory.MaxUndoDepth - 1),
                "a domain reload must accept the trimmed payload instead of resetting the timeline");
            Assert.That(reloaded.UndoStack, Has.Count.EqualTo(ProjectCommandHistory.MaxUndoDepth - 1));
            Assert.That(reloaded.RedoStack, Has.Count.EqualTo(1));
            Assert.That(reloaded.AppliedNativeRevision, Is.EqualTo(pushCount - 1),
                "the trimmed base has to survive the reload, or the cursor stops lining up");
        }

        // Walking back past the oldest surviving record is the one thing the
        // trim makes reachable. It must fail closed - never apply an older
        // command at a marker whose record is gone.
        [Test]
        public void UndoingPastTheTrimmedBase_RollsBackAndResetsInsteadOfReplayingAWrongRecord()
        {
            const int pushCount = 250;
            var panelUndoCount = 0;
            var panelRedoCount = 0;
            _undoObserved = () => panelUndoCount++;
            _redoObserved = () => panelRedoCount++;

            for (var i = 0; i < pushCount; i++)
            {
                _history.PushExecuted(NewRecordNamed(ProjectCommandKind.CreateFolder, i).ToCommand());
            }

            // Unity restoring a snapshot from below the trimmed base is exactly
            // what an old native step does after the stack has been capped.
            _cursor.SetRevision(pushCount - ProjectCommandHistory.MaxUndoDepth - 1);
            LogAssert.Expect(
                LogType.Warning,
                "[Zep] Panel undo reconciliation failed: A panel filesystem command could not be replayed. The panel undo timeline was reset safely.");
            _bridge.ReconcileNowForTests();

            Assert.That(
                panelUndoCount, Is.EqualTo(ProjectCommandHistory.MaxUndoDepth),
                "only the records the stack still holds may be replayed");
            Assert.That(
                panelRedoCount, Is.EqualTo(ProjectCommandHistory.MaxUndoDepth),
                "every replayed command must be compensated before the reset");
            Assert.That(_history.CanUndo, Is.False);
            Assert.That(_history.CanRedo, Is.False);
            Assert.That(_storage.AppliedRevision, Is.Zero);
            Assert.That(_storage.AppliedNativeRevision, Is.Zero, "the reset clears the trimmed base too");
            Assert.That(_cursor.Revision, Is.Zero);
        }

        private Action _undoObserved;
        private Action _redoObserved;
        private bool _failRedo;
        private int _failUndoAtCall;
        private int _undoCallCount;
        private int _historySaveCount;
        private int _undoDepthAtLastHistorySave;
        private int _redoDepthAtLastHistorySave;

        // Stands in for the storage's own Save so a test can count the saves the
        // HISTORY asks for (the bridge also saves directly, and the perf counter
        // cannot tell the two apart) and see the stack shape each one persists.
        private void RecordHistorySave()
        {
            _historySaveCount++;
            _undoDepthAtLastHistorySave = _storage.UndoStack.Count;
            _redoDepthAtLastHistorySave = _storage.RedoStack.Count;
            _storage.Save();
        }

        private bool UndoPanelCommand()
        {
            if (!_history.CanUndo)
            {
                return false;
            }

            _undoCallCount++;
            if (_failUndoAtCall > 0 && _undoCallCount == _failUndoAtCall)
            {
                _history.DiscardUndo();
                return false;
            }

            var command = _history.PeekUndo();
            _history.CompleteUndo(command);
            _undoObserved?.Invoke();
            return true;
        }

        private bool RedoPanelCommand()
        {
            if (!_history.CanRedo)
            {
                return false;
            }

            if (_failRedo)
            {
                _history.DiscardRedo();
                return false;
            }

            var command = _history.PeekRedo();
            _history.CompleteRedo(command);
            _redoObserved?.Invoke();
            return true;
        }

        // Undoing an ORDINARY SCENE EDIT is not a new action: Unity keeps its own
        // redo stack, so the panel must keep its redo branch too. The publication
        // that undo produces arrives once Undo.isProcessing is already false, so
        // neither synchronous guard catches it; treating it as a branch cut left
        // Unity's redo stack holding a panel marker with no command behind it,
        // and the next redo reset the entire timeline.
        [UnityTest]
        public IEnumerator UndoOfAnUnrelatedSceneEdit_KeepsThePanelRedoBranch()
        {
            yield return null; // drain setup object creation

            // The scene edit goes in FIRST so the panel command sits on top of it
            // and the first undo pops the panel command, leaving the scene edit
            // as the next thing Cmd+Z will reach.
            RegisterUnrelatedChange(7);
            _history.PushExecuted(NewRecord(ProjectCommandKind.Delete).ToCommand());
            yield return null;

            // Undo the panel command and let the bridge replay it: redo is armed.
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            yield return null;
            Assert.That(_history.CanRedo, Is.True, "the panel undo must arm redo");

            // Now undo the SCENE edit that sits below it. This publishes
            // ChangeGameObjectOrComponentProperties on a later beat.
            Undo.PerformUndo();
            _bridge.ReconcileNowForTests();
            yield return null;
            yield return null;

            Assert.That(_history.CanRedo, Is.True,
                "undoing an unrelated scene edit must not cut the panel redo branch");
        }

        private void RegisterUnrelatedChange(int value)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.RegisterCompleteObjectUndo(_unrelatedObject, "Change Main Camera Scale");
            _unrelatedObject.layer = value;
            EditorUtility.SetDirty(_unrelatedObject);
            Undo.CollapseUndoOperations(group);
            Undo.IncrementCurrentGroup();
        }

        // Distinguishable records: a stack cap has to be proven to drop the
        // OLDEST entries, which identical records could not show.
        private static ProjectCommandRecord NewRecordNamed(ProjectCommandKind kind, int index)
        {
            var record = NewRecord(kind);
            record.PrimaryPath = "Assets/Test" + index + ".txt";
            record.Name = "Test" + index;
            return record;
        }

        private static ProjectCommandRecord NewRecord(ProjectCommandKind kind)
        {
            return new ProjectCommandRecord
            {
                Kind = kind,
                PrimaryPath = "Assets/Test.txt",
                SecondaryPath = "/tmp/zep-undo/Test.txt",
                Name = "Test",
                AssetGuid = "guid-test",
            };
        }

    }
}
