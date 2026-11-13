using System;
using System.Collections.Generic;
using Zep.Core.Application;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    internal sealed class AssetsAreaInlineEditController
    {
        public event Action<string> Confirmed;
        public event Action Aborted;

        public AssetsAreaInlineEditDescriptor Descriptor => _descriptor;

        private AssetsAreaInlineEditDescriptor _descriptor = AssetsAreaInlineEditDescriptor.None;
        private string _basename = string.Empty;
        // The TextField hosting the active edit, captured at bind time so the
        // controller can release its focus when the edit ends.
        private TextField _activeField;
        // Selecting the whole name is a once-per-session courtesy, not a
        // per-bind one. Set when the scheduled focus leg actually applies it.
        private bool _selectionAppliedForSession;
        // Caret and selection anchor as they stood the instant a release blurred
        // the field, so the bind that re-claims the row can put them back. The
        // blur itself zeroes them, so this is the last moment they exist.
        // -1 means nothing is waiting to be restored.
        private int _releasedCursorIndex = -1;
        private int _releasedSelectIndex = -1;

        public void SetDescriptor(AssetsAreaInlineEditDescriptor descriptor)
        {
            var wasActive = _descriptor.IsActive;
            var samePath = wasActive && string.Equals(_descriptor.Path, descriptor.Path, StringComparison.Ordinal);
            _descriptor = descriptor;

            if (!descriptor.IsActive)
            {
                _basename = string.Empty;
                ReleaseActiveFieldFocus();
                ForgetSelectionState();
            }
            else if (!samePath)
            {
                _basename = descriptor.InitialBasename ?? string.Empty;
                if (wasActive)
                {
                    ReleaseActiveFieldFocus();
                }

                ForgetSelectionState();
            }
            // retain _basename when descriptor stays on the same path
        }

        // A session's selection state: whether its one whole-name selection has
        // been applied, and any caret a release still owes back to the row.
        private void ForgetSelectionState()
        {
            _selectionAppliedForSession = false;
            _releasedCursorIndex = -1;
            _releasedSelectIndex = -1;
        }

        // Ending an edit only hides the row's edit container — hiding does NOT
        // blur. Focus stayed on the invisible text input, and since the shell
        // yields every key to focused text inputs, the whole panel keyboard
        // went dead after the first committed rename. Release focus explicitly
        // the moment the edit stops being active; the shell's focus-out
        // reclaim then re-anchors keyboard input at the panel root.
        private void ReleaseActiveFieldFocus()
        {
            var field = _activeField;
            _activeField = null;
            if (field == null)
            {
                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log("[Zep/kbd] release-edit-focus field=null");
                }

                return;
            }

            var focused = field.panel?.focusController?.focusedElement as VisualElement;
            var fieldOwnsFocus = focused != null && (focused == field || field.Contains(focused));
            if (ZepPerfLog.IsEnabled)
            {
                ZepPerfLog.Log(
                    $"[Zep/kbd] release-edit-focus focused={(focused == null ? "null" : focused.GetType().Name)}"
                    + $" fieldOwnsFocus={fieldOwnsFocus} fieldAttached={field.panel != null}");
            }

            if (fieldOwnsFocus)
            {
                // Blurring resets the field's caret and selection, and the bind
                // that re-claims this row must not drop the user at the start of
                // a fully selected name — remember where they were typing.
                _releasedCursorIndex = field.cursorIndex;
                _releasedSelectIndex = field.selectIndex;
                focused.Blur();
            }
        }

        public bool IsActiveForPath(string path)
        {
            return _descriptor.IsActive
                && !string.IsNullOrEmpty(path)
                && string.Equals(path, _descriptor.Path, StringComparison.Ordinal);
        }

        // Apply edit-state visuals to a single row. Call from each view's bind step
        // AFTER setting the row's entry data. editContainer may equal nameField for
        // views without a separate wrapper. extensionLabel may be null.
        //
        // nameField/editContainer may be null when the row is NOT the active
        // edit target — rows create their TextField on demand (the first time
        // they actually host an edit), so the common bind passes null and only
        // pays the restore-branch style writes.
        //
        // mutedElements are the row's children the editing-class USS mute rules
        // hide (list secondary label; grid meta labels + tags container). The
        // bind path stamps data-driven inline display on them, and an inline
        // value silently beats any class-conditional USS rule — so while the
        // edit is active the inline value is cleared and the mute rules decide.
        // No restore on exit: both views re-stamp the data-driven values before
        // this runs (the list on every bind; the grid via the edit-state leg of
        // its dirty check), and the grid tags container's USS default matches
        // its stamped value.
        public void BindRow(
            string entryPath,
            Label nameLabel,
            VisualElement editContainer,
            TextField nameField,
            Label extensionLabel,
            VisualElement rowRoot,
            string editingUssClass,
            IReadOnlyList<VisualElement> mutedElements = null)
        {
            if (nameLabel == null) return;

            var isEditRow = IsActiveForPath(entryPath);
            if (isEditRow && (nameField == null || editContainer == null)) return;

            if (isEditRow)
            {
                nameLabel.style.display = DisplayStyle.None;
                editContainer.style.display = DisplayStyle.Flex;
                if (mutedElements != null)
                {
                    for (var i = 0; i < mutedElements.Count; i++)
                    {
                        mutedElements[i].style.display = StyleKeyword.Null;
                    }
                }
                if (extensionLabel != null)
                {
                    var ext = _descriptor.Extension ?? string.Empty;
                    extensionLabel.text = ext;
                    extensionLabel.style.display = ext.Length > 0 ? DisplayStyle.Flex : DisplayStyle.None;
                }

                nameField.SetValueWithoutNotify(_basename ?? string.Empty);
                _activeField = nameField;
                var capturedPath = entryPath;
                nameField.schedule.Execute(() =>
                {
                    // Row may have been recycled before the scheduler fires — only
                    // grab focus if this row still hosts the active edit target.
                    // The field identity check matters on 6.5: a refresh pass can
                    // move the edit to a NEW physical row before this fires, and
                    // focusing the stale field would land keyboard input on a row
                    // that no longer hosts the edit.
                    if (IsActiveForPath(capturedPath)
                        && ReferenceEquals(nameField, _activeField))
                    {
                        FocusAndSelectField(nameField);
                    }
                }).ExecuteLater(0);
            }
            else
            {
                nameLabel.style.display = DisplayStyle.Flex;
                if (editContainer != null)
                {
                    editContainer.style.display = DisplayStyle.None;
                }

                // This physical row hosts the active edit and is being rebound
                // to a different entry — recycled by a scroll, or the edited
                // path left the data set. Hiding the container does NOT blur,
                // and every field callback is keyed on the row's path, so the
                // session would live on invisibly: Enter/Escape guarded-dead
                // and the shell still yielding every key to the hidden input.
                ReleaseEditRow(nameField);
            }

            rowRoot?.EnableInClassList(editingUssClass, isEditRow);
        }

        // The view RELEASED this row (unbindItem), or the bind pass above
        // recycled it to a different entry.
        public void HandleRowReleased(TextField nameField)
        {
            ReleaseEditRow(nameField);
        }

        // A physical row that hosted the active edit was taken away. That is
        // NOT proof the edit is over: Unity 6.5's ListView releases and
        // re-binds every active row inside a single refresh pass (both the
        // itemsSource swap and RefreshItems do this), so the begin-edit push
        // itself briefly releases the row it just bound, and a scroll can move
        // the edit to a new physical row in the same pass. Ending the session
        // here turned every inline create/rename into a silent no-op on 6.5.
        //
        // Release the field now (its focus must not linger on a recycled row)
        // and decide NEXT TICK: a re-claiming BindRow sets _activeField again
        // and the session continues; if no row re-claimed the edit by then,
        // the path truly left the view — cancel, never commit. The deferred
        // check cannot ride the field's scheduler: a released row may be
        // detached and never re-attach, and a detached element's scheduled
        // items do not tick. A one-shot editor update handler ticks
        // regardless. Releasing the field nulls _activeField, so a second
        // release before the tick cannot stack a second live notification —
        // the extra callback fires once and exits on the guard.
        private void ReleaseEditRow(TextField field)
        {
            if (field == null || !ReferenceEquals(field, _activeField))
            {
                return;
            }

            ReleaseActiveFieldFocus();
            SchedulePendingAbort();
        }

        private void SchedulePendingAbort()
        {
            var releasedPath = _descriptor.Path;
            EditorApplication.CallbackFunction notifyOnce = null;
            notifyOnce = () =>
            {
                EditorApplication.update -= notifyOnce;
                // Keep the session when a row re-claimed the edit during the
                // refresh pass, when something else (Escape, a commit) already
                // settled it, or when a DIFFERENT edit opened in the meantime —
                // that new session is not ours to cancel.
                if (_activeField != null
                    || !_descriptor.IsActive
                    || !string.Equals(_descriptor.Path, releasedPath, StringComparison.Ordinal))
                {
                    return;
                }

                _descriptor = AssetsAreaInlineEditDescriptor.None;
                _basename = string.Empty;
                ForgetSelectionState();
                Aborted?.Invoke();
            };
            EditorApplication.update += notifyOnce;
        }

        public void HandleRowKeyDown(KeyDownEvent evt, string rowEntryPath, TextField field)
        {
            if (!IsActiveForPath(rowEntryPath)) return;

            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                Confirmed?.Invoke(field?.value ?? _basename);
                evt.StopPropagation();
            }
        }

        // Escape lives in its own TRICKLE-phase leg (widgets register it
        // TrickleDown, unlike the bubble-phase Enter handler above): the field's
        // inner text element consumes the Escape KeyDown during its own
        // processing — restoring the pre-edit text and blurring — so a bubble
        // handler never sees the key, and the blur becomes FocusOut→Confirmed.
        // That turned Escape into a COMMIT: a ghost create landed the asset
        // under its default name instead of cancelling. Same interception the
        // search bar uses for its Escape-clears-search rule.
        public void HandleRowEscapeKeyDown(KeyDownEvent evt, string rowEntryPath)
        {
            if (evt.keyCode != KeyCode.Escape) return;
            if (!IsActiveForPath(rowEntryPath)) return;

            Aborted?.Invoke();
            evt.StopPropagation();
        }

        public void HandleRowValueChanged(string newValue, string rowEntryPath)
        {
            if (!IsActiveForPath(rowEntryPath)) return;
            _basename = newValue ?? string.Empty;
        }

        public void HandleRowFocusOut(string rowEntryPath, TextField field)
        {
            if (!IsActiveForPath(rowEntryPath)) return;
            // A released row can still emit FocusOut while its pending-abort
            // window is open (the release blurs the field); only the field
            // that currently hosts the edit may turn focus loss into a commit.
            if (!ReferenceEquals(field, _activeField)) return;
            Confirmed?.Invoke(field?.value ?? _basename);
        }

        // Focus has to be re-taken on EVERY bind of the edit row: on 6.5 a
        // refresh pass releases the row (which blurs the field) before it
        // rebinds it, so skipping this would drop the user out of the edit.
        // Selecting the whole name, however, belongs to the START of the
        // session only — a background import finishing mid-rename rebinds the
        // row, and re-selecting there made the next keystroke replace
        // everything the user had typed. Focus() re-selects all by itself
        // (UITK's selectAllOnFocus default), so a later bind puts back the
        // caret the release took away and otherwise leaves the field alone.
        private void FocusAndSelectField(TextField field)
        {
            if (field == null) return;

            field.Focus();

            if (!_selectionAppliedForSession)
            {
                var length = field.value?.Length ?? 0;
                if (length > 0)
                {
                    field.SelectRange(0, length);
                }

                _selectionAppliedForSession = true;
                _releasedCursorIndex = -1;
                _releasedSelectIndex = -1;
                return;
            }

            if (_releasedCursorIndex < 0)
            {
                return;
            }

            field.SelectRange(_releasedCursorIndex, _releasedSelectIndex);
            _releasedCursorIndex = -1;
            _releasedSelectIndex = -1;
        }
    }
}
