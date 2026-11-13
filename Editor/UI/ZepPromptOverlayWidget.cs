using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal sealed class ZepPromptOverlayWidget : VisualElement
    {
        private readonly VisualElement _card;
        private readonly Label _titleLabel;
        private readonly Label _fieldLabel;
        private readonly TextField _field;
        private readonly Button _submitButton;
        private readonly VisualElement _extraContainer;
        private Action<string> _submitAction;
        // Recovered seam: set by ShowConfirm so Submit runs a parameterless
        // confirm callback instead of the text-field submit path.
        private Action _confirmAction;
        private readonly ModalFade _fade = new ModalFade();
        // Logical visibility, not the rendered one. Hide() keeps display Flex
        // for the 140 ms fade, and the shortcut controller asks this to decide
        // whether the overlay owns the keyboard — reading style.display made it
        // swallow every key pressed in the moment right after a dismiss.
        private bool _isVisible;

        public bool IsVisible => _isVisible;

        public ZepPromptOverlayWidget()
        {
            name = "zep-modal-scrim";
            AddToClassList(ZepUiClasses.ModalScrim);
            AddToClassList(ZepUiClasses.PromptRoot);
            style.display = DisplayStyle.None;

            RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.target == this && evt.button == 0)
                    Hide();
            });

            _card = new VisualElement { name = "zep-prompt-card" };
            _card.AddToClassList(ZepUiClasses.PromptCard);

            _titleLabel = new Label("Prompt") { name = "zep-prompt-title" };
            _titleLabel.AddToClassList(ZepUiClasses.PromptTitle);
            _card.Add(_titleLabel);

            _fieldLabel = new Label("Value") { name = "zep-prompt-field-label" };
            _fieldLabel.AddToClassList(ZepUiClasses.PromptFieldLabel);
            _card.Add(_fieldLabel);

            _field = new TextField { name = "zep-prompt-field" };
            _field.AddToClassList(ZepUiClasses.PromptField);
            _field.RegisterCallback<KeyDownEvent>(evt =>
            {
                // Escape never reaches here: the shell's TrickleDown key router
                // hides this overlay and stops the event (ZepShortcutController
                // .HandleKeyDown, the IsPromptOverlayVisible leg).
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    Submit();
                    evt.StopPropagation();
                }
            });
            _card.Add(_field);

            _extraContainer = new VisualElement { name = "zep-prompt-extra" };
            _extraContainer.style.display = DisplayStyle.None;
            _card.Add(_extraContainer);

            var actions = new VisualElement { name = "zep-prompt-actions" };
            actions.AddToClassList(ZepUiClasses.PromptActions);

            var cancel = new Button(Hide) { text = "Cancel" };
            cancel.AddToClassList(ZepUiClasses.ActionButton);
            cancel.AddToClassList(ZepUiClasses.ActionButtonSecondary);

            _submitButton = new Button(Submit) { text = "OK" };
            _submitButton.AddToClassList(ZepUiClasses.ActionButton);
            _submitButton.AddToClassList(ZepUiClasses.ActionButtonPrimary);
            // The button's enabled state is a pure function of what is in the
            // field: Submit refuses a blank name, and with the button left
            // enabled that refusal was invisible — the click and the Return both
            // read as a dead control. Nothing else may write this state.
            _field.RegisterValueChangedCallback(_ => UpdateSubmitEnabled());

            actions.Add(cancel);
            actions.Add(_submitButton);
            _card.Add(actions);

            Add(_card);
        }

        public void Show(string title, string label, string initialValue, string submitLabel, Action<string> onSubmit, VisualElement extraContent = null)
        {
            _submitAction = onSubmit;
            // Recovered seam: clear any pending confirm state and re-show the text
            // field in case the previous use was a ShowConfirm.
            _confirmAction = null;
            _titleLabel.text = title ?? string.Empty;
            _fieldLabel.text = label ?? string.Empty;
            _submitButton.text = submitLabel ?? "OK";
            _field.style.display = DisplayStyle.Flex;
            _field.SetValueWithoutNotify(initialValue ?? string.Empty);
            // SetValueWithoutNotify skips the change callback, so the state has
            // to be evaluated once here: a prompt seeded with a name opens
            // ready to submit, an empty one opens refusing.
            UpdateSubmitEnabled();

            _extraContainer.Clear();
            if (extraContent != null)
            {
                _extraContainer.Add(extraContent);
                _extraContainer.style.display = DisplayStyle.Flex;
            }
            else
            {
                _extraContainer.style.display = DisplayStyle.None;
            }

            _isVisible = true;
            _fade.Show(this, _card, () =>
            {
                _field.Focus();
                _field.SelectAll();
            });
        }

        // Recovered seam: confirmation variant of Show. No text input — the field
        // is hidden and the field label carries the message; the submit button
        // runs the parameterless onConfirm. extraContent (e.g. a toggle) is
        // surfaced in the same extra container Show uses, so callers can read its
        // value back inside onConfirm.
        public void ShowConfirm(string title, string message, string confirmLabel, Action onConfirm, VisualElement extraContent = null)
        {
            _submitAction = null;
            _confirmAction = onConfirm;
            _titleLabel.text = title ?? string.Empty;
            _fieldLabel.text = message ?? string.Empty;
            _submitButton.text = confirmLabel ?? "OK";

            _field.SetValueWithoutNotify(string.Empty);
            _field.style.display = DisplayStyle.None;
            // A confirm prompt has nothing to type, so the blank field must not
            // disable its button.
            _submitButton.SetEnabled(true);

            _extraContainer.Clear();
            if (extraContent != null)
            {
                _extraContainer.Add(extraContent);
                _extraContainer.style.display = DisplayStyle.Flex;
            }
            else
            {
                _extraContainer.style.display = DisplayStyle.None;
            }

            _isVisible = true;
            _fade.Show(this, _card, () => _submitButton.Focus());
        }

        public void Hide()
        {
            _isVisible = false;
            _fade.Hide(this, _card);
            _submitAction = null;
            _confirmAction = null;
            // Recovered seam: ShowConfirm hides the field; restore it for the next
            // text-prompt Show.
            _field.style.display = DisplayStyle.Flex;
            _extraContainer.Clear();
            _extraContainer.style.display = DisplayStyle.None;
        }

        private void UpdateSubmitEnabled()
        {
            _submitButton.SetEnabled(!string.IsNullOrWhiteSpace(_field.value));
        }

        private void Submit()
        {
            // Recovered seam: confirm prompts have no text input — run the
            // parameterless callback and skip the non-empty value guard.
            if (_confirmAction != null)
            {
                var confirm = _confirmAction;
                Hide();
                confirm.Invoke();
                return;
            }

            var value = _field.value;
            if (string.IsNullOrWhiteSpace(value))
                return;
            var action = _submitAction;
            Hide();
            action?.Invoke(value.Trim());
        }
    }
}
