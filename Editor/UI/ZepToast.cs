using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal enum ZepToastKind
    {
        Success,
        Error,
    }

    internal sealed class ZepToast : VisualElement
    {
        private const long ShowDelayMs = 16;
        private const long SuccessHoldMs = 2400;
        private const long ErrorHoldMs = 4000;

        private readonly VisualElement _card;
        private readonly Label _glyph;
        private readonly Label _label;
        private IVisualElementScheduledItem _showTask;
        private IVisualElementScheduledItem _hideTask;

        public ZepToast()
        {
            AddToClassList(ZepUiClasses.Toast);
            pickingMode = PickingMode.Ignore;
            style.opacity = 0f;
            style.translate = new Translate(0f, -24f);

            _card = new VisualElement();
            _card.AddToClassList(ZepUiClasses.ToastCard);
            _card.pickingMode = PickingMode.Ignore;

            _glyph = new Label();
            _glyph.AddToClassList(ZepUiClasses.ToastGlyph);
            _card.Add(_glyph);

            _label = new Label();
            _label.AddToClassList(ZepUiClasses.ToastLabel);
            _card.Add(_label);

            Add(_card);
        }

        public void Show(string message, ZepToastKind kind)
        {
            // A toast is a notification — it must paint above everything, including
            // lazily-added overlays (the theme-creator modal is added to the shell
            // root after this toast, so without this a save-failed toast renders
            // beneath the modal scrim). pickingMode is Ignore, so being on top never
            // steals clicks from the overlay below.
            BringToFront();

            _label.text = message ?? string.Empty;
            _glyph.text = kind == ZepToastKind.Error ? "\u26A0" : "\u2713";

            _card.RemoveFromClassList(ZepUiClasses.ToastCardSuccess);
            _card.RemoveFromClassList(ZepUiClasses.ToastCardError);
            _card.AddToClassList(kind == ZepToastKind.Error
                ? ZepUiClasses.ToastCardError
                : ZepUiClasses.ToastCardSuccess);

            _showTask?.Pause();
            _hideTask?.Pause();

            style.opacity = 0f;
            style.translate = new Translate(0f, -24f);

            _showTask = schedule.Execute(() =>
            {
                style.opacity = 1f;
                style.translate = new Translate(0f, 24f);
            });
            _showTask.ExecuteLater(ShowDelayMs);

            var holdMs = kind == ZepToastKind.Error ? ErrorHoldMs : SuccessHoldMs;
            _hideTask = schedule.Execute(() =>
            {
                style.opacity = 0f;
                style.translate = new Translate(0f, -24f);
            });
            _hideTask.ExecuteLater(ShowDelayMs + holdMs);
        }
    }
}
