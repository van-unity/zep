using System;
using System.Collections.Generic;

namespace Zep.Core.FileOperations
{
    // Owns the undo/redo stacks for the file-command service. The stacks
    // are kept as List<ProjectCommandRecord> (not Stack<command>) so a
    // Bridge-side persistence layer can hold the same underlying lists
    // and round-trip them through SessionState. Without that, a domain
    // reload triggered by reimport (very common after a folder move that
    // touches a .cs/.asmdef) would wipe the in-memory history and leave
    // the user with "Nothing to undo" right after a real operation.
    //
    // The parameterless constructor preserves the in-memory-only mode
    // used by tests: same behavior as before, no save callback fires.
    public sealed class ProjectCommandHistory
    {
        // Both stacks are re-serialised on every save and live in session
        // storage that survives domain reload for the whole editor session, so
        // an unbounded history is a session-long leak that also makes every
        // later save slower. 200 steps is far past what a user walks back by
        // hand, and the oldest records are the ones nobody reaches.
        internal const int MaxUndoDepth = 200;

        private readonly List<ProjectCommandRecord> _undoStack;
        private readonly List<ProjectCommandRecord> _redoStack;
        private readonly Action _save;
        private int _executionGroupDepth;
        private int _pendingExecutionCount;
        private ProjectCommandRecord _pendingExecutionRecord;
        private int _saveSuppressionDepth;
        private bool _isSavePending;

        // The Bridge uses this one seam to register successful panel mutations
        // in Unity's native undo timeline. A group count greater than one means
        // the commands were executed inside one user-visible bulk operation.
        public event Action<ProjectCommandRecord, int> Executed;

        // The Bridge removes matching cursor records whenever history is reset.
        public event Action Invalidated;

        // Raised with the number of records dropped off the BOTTOM of the undo
        // stack when it reaches its ceiling. Whoever maps these records onto an
        // external revision (the Bridge's session storage) has to move its base
        // by the same amount, or the depth it recorded would point at a record
        // that no longer exists.
        public event Action<int> Trimmed;

        public ProjectCommandHistory()
            : this(new List<ProjectCommandRecord>(), new List<ProjectCommandRecord>(), null)
        {
        }

        public ProjectCommandHistory(
            List<ProjectCommandRecord> undoStack,
            List<ProjectCommandRecord> redoStack,
            Action save)
        {
            _undoStack = undoStack ?? throw new ArgumentNullException(nameof(undoStack));
            _redoStack = redoStack ?? throw new ArgumentNullException(nameof(redoStack));
            _save = save;
        }

        private Action<string> _releasePayload;

        internal void SetPayloadCleanup(Action<string> releasePayload)
        {
            _releasePayload = releasePayload;
        }

        internal bool CanUndo => _undoStack.Count > 0;

        internal bool CanRedo => _redoStack.Count > 0;

        internal void PushExecuted(IReversibleProjectCommand command)
        {
            var record = command?.ToRecord();
            if (record == null) return;
            var abandoned = _redoStack.ToArray();
            _undoStack.Add(record);
            _redoStack.Clear();
            ReleaseUnreferencedPayloads(abandoned);

            if (_executionGroupDepth > 0)
            {
                _pendingExecutionCount++;
                _pendingExecutionRecord = record;
                return;
            }

            NotifyExecutedAndSave(record, 1);
        }

        internal IReversibleProjectCommand PeekUndo()
        {
            return _undoStack[_undoStack.Count - 1].ToCommand();
        }

        internal IReversibleProjectCommand PeekRedo()
        {
            return _redoStack[_redoStack.Count - 1].ToCommand();
        }

        // What the next Undo()/Redo() would run, without rehydrating it. Callers
        // that must react to a REPLAY's side effects have to know this before it
        // runs: afterwards the record has already moved to the other stack.
        internal ProjectCommandKind? PeekUndoKind()
        {
            return _undoStack.Count == 0
                ? (ProjectCommandKind?)null
                : _undoStack[_undoStack.Count - 1].Kind;
        }

        internal ProjectCommandKind? PeekRedoKind()
        {
            return _redoStack.Count == 0
                ? (ProjectCommandKind?)null
                : _redoStack[_redoStack.Count - 1].Kind;
        }

        internal void CompleteUndo(IReversibleProjectCommand command)
        {
            if (ForgetIfUndoneInsideItsOwnGroup(command))
            {
                return;
            }

            TransferTop(_undoStack, _redoStack, command);
        }

        internal void CompleteRedo(IReversibleProjectCommand command)
        {
            TransferTop(_redoStack, _undoStack, command);
        }

        internal void DiscardUndo()
        {
            if (ForgetIfUndoneInsideItsOwnGroup())
            {
                return;
            }

            _undoStack.RemoveAt(_undoStack.Count - 1);
            _save?.Invoke();
        }

        // A command undone inside the execution group that ran it never
        // happened as far as the group is concerned. The Bridge maps the group
        // onto ONE native undo step sized by the count it is notified with, so
        // a record that was executed and reverted within the group must leave
        // history entirely: counted, the step would stand for a record that is
        // no longer on the stack, and the next Cmd+Z would replay whatever sits
        // beneath it; transferred to the redo stack, a lone Redo would re-apply
        // the half that was rolled back. Reached by a bulk's own compensation
        // (Replace's rollback of the displacement when the move that was to
        // follow it fails), whether that rollback succeeds or is discarded.
        //
        // While the pending count is non-zero the top of the undo stack is a
        // record pushed in this group: pushes append, a push clears the redo
        // stack, and an in-group undo pops the top — so a replay the Bridge
        // drives through RunBulk (nothing pushed, count zero) keeps the normal
        // transfer.
        private bool ForgetIfUndoneInsideItsOwnGroup(IReversibleProjectCommand command = null)
        {
            if (_executionGroupDepth == 0 || _pendingExecutionCount == 0)
            {
                return false;
            }

            var removed = command?.ToRecord() ?? _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            if (command != null) ReleaseUnreferencedPayloads(new[] { removed });
            _pendingExecutionCount--;
            _pendingExecutionRecord = _pendingExecutionCount > 0
                ? _undoStack[_undoStack.Count - 1]
                : null;
            RequestSave();
            return true;
        }

        internal void DiscardRedo()
        {
            _redoStack.RemoveAt(_redoStack.Count - 1);
            _save?.Invoke();
        }

        internal void BeginExecutionGroup()
        {
            _executionGroupDepth++;
        }

        internal void EndExecutionGroup()
        {
            if (_executionGroupDepth <= 0)
            {
                throw new InvalidOperationException("No project command execution group is active.");
            }

            _executionGroupDepth--;
            if (_executionGroupDepth > 0 || _pendingExecutionCount == 0)
            {
                return;
            }

            var record = _pendingExecutionRecord;
            var count = _pendingExecutionCount;
            _pendingExecutionRecord = null;
            _pendingExecutionCount = 0;
            NotifyExecutedAndSave(record, count);
        }

        // One native undo step can stand for k panel commands (the Bridge
        // collapses an execution group into a single step), so replaying it
        // drives k stack transfers. Each transfer would otherwise re-serialise
        // BOTH stacks into session storage: O(k^2) bytes on one keystroke. A
        // replay driver holds this depth across its loop, and the stacks are
        // persisted once, in the shape they end up in.
        public void BeginSaveSuppression()
        {
            _saveSuppressionDepth++;
        }

        public void EndSaveSuppression()
        {
            if (_saveSuppressionDepth <= 0)
            {
                throw new InvalidOperationException("No project command save suppression is active.");
            }

            _saveSuppressionDepth--;
            if (_saveSuppressionDepth > 0 || !_isSavePending)
            {
                return;
            }

            _isSavePending = false;
            _save?.Invoke();
        }

        internal bool ClearRedo()
        {
            if (_redoStack.Count == 0)
            {
                return false;
            }

            var abandoned = _redoStack.ToArray();
            _redoStack.Clear();
            ReleaseUnreferencedPayloads(abandoned);
            _save?.Invoke();
            return true;
        }

        private void ReleaseUnreferencedPayloads(IEnumerable<ProjectCommandRecord> abandoned)
        {
            if (_releasePayload == null) return;
            var retained = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in _undoStack) retained.Add(record?.GetUndoPayloadPath() ?? string.Empty);
            foreach (var record in _redoStack) retained.Add(record?.GetUndoPayloadPath() ?? string.Empty);
            var released = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in abandoned)
            {
                var path = record?.GetUndoPayloadPath();
                if (!string.IsNullOrEmpty(path) && !retained.Contains(path) && released.Add(path))
                    _releasePayload(path);
            }
        }

        internal void Invalidate()
        {
            var abandoned = new List<ProjectCommandRecord>(_undoStack);
            abandoned.AddRange(_redoStack);
            _undoStack.Clear();
            _redoStack.Clear();
            ReleaseUnreferencedPayloads(abandoned);
            _pendingExecutionRecord = null;
            _pendingExecutionCount = 0;

            try
            {
                Invalidated?.Invoke();
            }
            finally
            {
                _save?.Invoke();
            }
        }

        private void TransferTop(
            List<ProjectCommandRecord> source,
            List<ProjectCommandRecord> destination,
            IReversibleProjectCommand command)
        {
            var record = command?.ToRecord();
            source.RemoveAt(source.Count - 1);
            if (record != null)
            {
                destination.Add(record);
            }

            RequestSave();
        }

        private void RequestSave()
        {
            if (_saveSuppressionDepth > 0)
            {
                _isSavePending = true;
                return;
            }

            _save?.Invoke();
        }

        private void NotifyExecutedAndSave(ProjectCommandRecord record, int count)
        {
            try
            {
                Executed?.Invoke(record, count);
            }
            finally
            {
                // Bridge listeners update their applied Unity revision before
                // this write, so history + revision land in one persisted payload.
                // The trim runs between the two for the same reason: it reports
                // what it dropped while the revision listener can still follow,
                // and both land in that single payload.
                TrimUndoStackToMaxDepth();
                _save?.Invoke();
            }
        }

        private void TrimUndoStackToMaxDepth()
        {
            var excess = _undoStack.Count - MaxUndoDepth;
            if (excess <= 0)
            {
                return;
            }

            var abandoned = _undoStack.GetRange(0, excess);
            _undoStack.RemoveRange(0, excess);
            ReleaseUnreferencedPayloads(abandoned);
            Trimmed?.Invoke(excess);
        }
    }
}
