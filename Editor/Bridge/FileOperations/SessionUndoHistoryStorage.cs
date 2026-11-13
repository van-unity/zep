using System;
using System.Collections.Generic;
using Zep.Core.FileOperations;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.FileOperations
{
    // One versioned payload owns both command stacks and the Unity cursor that
    // those stacks currently represent. Keeping them in one SessionState write
    // prevents a domain reload from rehydrating a stack at a different revision.
    internal sealed class SessionUndoHistoryStorage
    {
        internal const string DefaultStateKey = "Zep.UndoState.V4";

        private readonly string _stateKey;
        private readonly State _state;

        internal List<ProjectCommandRecord> UndoStack => _state.UndoStack;
        internal List<ProjectCommandRecord> RedoStack => _state.RedoStack;
        // How deep into the undo STACK the applied revision sits. This is the
        // value the AppliedRevision == UndoStack.Count invariant is about.
        internal int AppliedRevision => _state.AppliedRevision;

        // The same position expressed on Unity's absolute revision axis, which
        // keeps counting every command ever recorded even after the history has
        // dropped its oldest records. The cursor object stores that absolute
        // number inside Unity's native undo steps, so every comparison against
        // the cursor has to use this one.
        internal int AppliedNativeRevision => _state.AppliedRevision + _state.TrimmedRevisionBase;
        internal string SessionId => _state.SessionId;
        internal string CursorInstanceKey => _stateKey + ".CursorInstanceId";
        internal bool HasHistory => UndoStack.Count > 0 || RedoStack.Count > 0;

        internal SessionUndoHistoryStorage(string stateKey = null)
        {
            _stateKey = string.IsNullOrWhiteSpace(stateKey) ? DefaultStateKey : stateKey;
            _state = Load(_stateKey) ?? new State();
            _state.UndoStack = _state.UndoStack ?? new List<ProjectCommandRecord>();
            _state.RedoStack = _state.RedoStack ?? new List<ProjectCommandRecord>();

            if (!IsValid(_state))
            {
                Reset();
            }
            else
            {
                // Persist immediately when a fresh state was created. A domain
                // reload before the first command must reattach to the same ID.
                Save();
            }
        }

        internal void SetAppliedRevision(int revision)
        {
            _state.AppliedRevision = Math.Max(0, revision);
        }

        internal void SetAppliedNativeRevision(int nativeRevision)
        {
            SetAppliedRevision(nativeRevision - _state.TrimmedRevisionBase);
        }

        // The history capped its undo stack and dropped its OLDEST records.
        // Their native revisions are now unreachable, so the base moves up by
        // the same amount: the applied position on Unity's axis is unchanged,
        // while AppliedRevision keeps matching the (now shorter) stack. An undo
        // that walks past the base finds an empty stack and fails closed in
        // ReplayTo instead of applying an older command at the wrong marker.
        internal void NoteTrimmedRecords(int droppedRecordCount)
        {
            if (droppedRecordCount <= 0)
            {
                return;
            }

            _state.TrimmedRevisionBase += droppedRecordCount;
            _state.AppliedRevision = Math.Max(0, _state.AppliedRevision - droppedRecordCount);
        }

        internal void Reset()
        {
            // Clear the existing list instances: ProjectCommandHistory holds
            // these exact references for the lifetime of the host.
            _state.UndoStack.Clear();
            _state.RedoStack.Clear();
            _state.SessionId = Guid.NewGuid().ToString("N");
            _state.AppliedRevision = 0;
            _state.TrimmedRevisionBase = 0;
            Save();
        }

        internal void Save()
        {
            Zep.Core.Application.ZepPerfLog.Count(
                Zep.Core.Application.ZepPerfCounter
                    .UndoHistorySaves);
            SessionState.SetString(_stateKey, JsonUtility.ToJson(_state));
        }

        internal void EraseForTests()
        {
            SessionState.EraseString(_stateKey);
#if UNITY_6000_5_OR_NEWER
            SessionState.EraseEntityId(CursorInstanceKey);
#else
            SessionState.EraseInt(CursorInstanceKey);
#endif
        }

        private static State Load(string key)
        {
            var json = SessionState.GetString(key, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<State>(json);
            }
            catch (Exception)
            {
                // Corrupt or pre-versioned payloads cannot be trusted to line
                // up with Unity's native cursor. Starting clean is data-safe.
                return null;
            }
        }

        private static bool IsValid(State state)
        {
            if (state == null
                || string.IsNullOrWhiteSpace(state.SessionId)
                || state.AppliedRevision < 0
                || state.TrimmedRevisionBase < 0
                || state.AppliedRevision != state.UndoStack.Count)
            {
                return false;
            }

            for (var i = 0; i < state.UndoStack.Count; i++)
            {
                if (state.UndoStack[i] == null || state.UndoStack[i].ToCommand() == null)
                {
                    return false;
                }
            }

            for (var i = 0; i < state.RedoStack.Count; i++)
            {
                if (state.RedoStack[i] == null || state.RedoStack[i].ToCommand() == null)
                {
                    return false;
                }
            }

            return true;
        }

        [Serializable]
        private sealed class State
        {
            public string SessionId = string.Empty;
            public int AppliedRevision;
            // Native revisions whose records the history has already dropped.
            // Absent from payloads written before the undo stack was capped,
            // which JsonUtility reads back as 0 - exactly the untrimmed case.
            public int TrimmedRevisionBase;
            public List<ProjectCommandRecord> UndoStack = new List<ProjectCommandRecord>();
            public List<ProjectCommandRecord> RedoStack = new List<ProjectCommandRecord>();
        }
    }
}
