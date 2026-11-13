using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Sidebar
{
    // The Finder-style header the Favorites and Tags sections sit under: a
    // muted label, an optional accessory (the Tags "+"), and a Hide/Show
    // toggle that reveals on hover. Clicking
    // anywhere on the header folds the section; the owning widget shows or
    // hides its body from ExpandedChanged. Expansion is per editor session:
    // a header given a SessionState key restores its fold across domain
    // reloads (the folder trees keep their expansion the same way).
    internal sealed class SidebarSectionHeader : VisualElement
    {
        private readonly Label _toggle;
        private readonly string _sessionStateKey;
        private bool _isExpanded = true;

        internal event Action<bool> ExpandedChanged;

        internal SidebarSectionHeader(string title, string sessionStateKey = null)
        {
            _sessionStateKey = sessionStateKey;
            if (!string.IsNullOrEmpty(sessionStateKey))
            {
                _isExpanded = SessionState.GetBool(sessionStateKey, true);
            }

            focusable = true;
            AddToClassList(ZepUiClasses.SidebarSectionHeader);

            var label = new Label(title) { pickingMode = PickingMode.Ignore };
            label.AddToClassList(ZepUiClasses.SidebarSectionLabel);

            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList(ZepUiClasses.FlexSpacer);

            _toggle = new Label { pickingMode = PickingMode.Ignore };
            _toggle.AddToClassList(ZepUiClasses.SidebarSectionToggle);

            Add(label);
            Add(spacer);
            Add(_toggle);

            // Mouse only, as the per-widget headers were: the shell's root
            // TrickleDown shortcut router consumes Space/Return before a
            // focused header could see them, and yielding there is not part
            // of this header's contract.
            RegisterCallback<MouseDownEvent>(OnMouseDown);
            ApplyToggleText();
        }

        internal bool IsExpanded => _isExpanded;

        // Accessories sit between the spacer and the Hide/Show toggle, i.e. at
        // the right edge next to it.
        internal void AddAccessory(VisualElement accessory)
        {
            Insert(IndexOf(_toggle), accessory);
        }

        internal void SetExpanded(bool expanded)
        {
            if (_isExpanded == expanded)
            {
                return;
            }

            _isExpanded = expanded;
            if (!string.IsNullOrEmpty(_sessionStateKey))
            {
                SessionState.SetBool(_sessionStateKey, _isExpanded);
            }

            ApplyToggleText();
            ExpandedChanged?.Invoke(_isExpanded);
        }

        internal void ToggleExpanded()
        {
            SetExpanded(!_isExpanded);
        }

        private void OnMouseDown(MouseDownEvent evt)
        {
            if (evt.button != 0 || evt.clickCount <= 0)
            {
                return;
            }

            ToggleExpanded();
            evt.StopPropagation();
        }

        private void ApplyToggleText()
        {
            _toggle.text = _isExpanded ? "Hide" : "Show";
        }
    }
}
