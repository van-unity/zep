using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    internal sealed class ZepStatusBarWidget : VisualElement
    {
        private const int VisibleMs = 1900;
        private const int HideTransitionMs = 170;

        // The status the panel sits on when nothing has happened: the label's
        // starting text, the empty-message fallback, and the baseline below.
        private const string IdleMessage = "Ready";

        private readonly Label _statusLabel;
        private string _lastMessage = IdleMessage;
        private int _toastVersion;

        public ZepStatusBarWidget()
        {
            name = "zep-status-bar";
            AddToClassList(ZepUiClasses.StatusBar);
            style.display = DisplayStyle.None;

            var pulse = new VisualElement { name = "zep-status-pulse" };
            pulse.AddToClassList(ZepUiClasses.StatusPulse);

            _statusLabel = new Label(IdleMessage) { name = "zep-status-label" };
            _statusLabel.AddToClassList(ZepUiClasses.StatusLabel);

            Add(pulse);
            Add(new Label("Workspace Status") { name = "zep-status-title" }.WithClass(ZepUiClasses.StatusTitle));
            Add(_statusLabel);
        }

        public void UpdateStatus(string statusMessage)
        {
            var normalized = string.IsNullOrWhiteSpace(statusMessage) ? IdleMessage : statusMessage.Trim();
            _statusLabel.text = normalized;

            // The baseline is seeded with the idle message at construction
            // rather than with whatever arrives first: the shell pushes the
            // view model's status on every global refresh, and the first of
            // those is the idle status the bar already reads, so it is still
            // absorbed - but a real first message is now shown instead of
            // being swallowed as a snapshot.
            if (string.Equals(_lastMessage, normalized, System.StringComparison.Ordinal))
            {
                return;
            }

            _lastMessage = normalized;
            ShowToast();
        }

        private void ShowToast()
        {
            style.display = DisplayStyle.Flex;
            AddToClassList(ZepUiClasses.StatusVisible);
            var version = ++_toastVersion;

            schedule.Execute(() =>
            {
                if (version != _toastVersion)
                    return;

                RemoveFromClassList(ZepUiClasses.StatusVisible);
                schedule.Execute(() =>
                {
                    if (version == _toastVersion)
                        style.display = DisplayStyle.None;
                }).ExecuteLater(HideTransitionMs);
            }).ExecuteLater(VisibleMs);
        }
    }
}
