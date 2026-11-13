using System;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.TopBar
{
    internal sealed class NavigationButtonsWidget : VisualElement
    {
        private readonly Button _backButton;
        private readonly Button _forwardButton;

        internal event Action BackClicked;
        internal event Action ForwardClicked;

        internal NavigationButtonsWidget(string elementNamePrefix)
        {
            name = elementNamePrefix + "-nav";
            AddToClassList(ZepUiClasses.FolderNavContainer);

            _backButton = CreateNavigationButton(
                elementNamePrefix + "-back",
                "Packages/com.van-unity.zep/Editor/UI/Icons/chevron-left.png",
                () => BackClicked?.Invoke());
            _backButton.tooltip = "Back";

            _forwardButton = CreateNavigationButton(
                elementNamePrefix + "-forward",
                "Packages/com.van-unity.zep/Editor/UI/Icons/chevron-right.png",
                () => ForwardClicked?.Invoke());
            _forwardButton.tooltip = "Forward";

            Add(_backButton);
            Add(_forwardButton);
        }

        internal void SetCanGoBack(bool canGoBack)
        {
            _backButton.SetEnabled(canGoBack);
        }

        internal void SetCanGoForward(bool canGoForward)
        {
            _forwardButton.SetEnabled(canGoForward);
        }

        private static Button CreateNavigationButton(string buttonName, string iconPath, Action onClick)
        {
            var button = new Button(onClick)
            {
                name = buttonName,
                text = string.Empty
            };
            button.AddToClassList(ZepUiClasses.NavigationButton);
            button.AddToClassList(ZepUiClasses.FlatButton);
            button.AddToClassList(ZepUiClasses.MonochromeIcon);
            TopBarWidget.AttachPackageIcon(button, iconPath, ZepUiClasses.CreateButtonIcon);
            return button;
        }
    }
}
