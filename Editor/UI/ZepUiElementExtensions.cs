using UnityEngine.UIElements;

namespace Zep.Editor.UI {
    internal static class ZepUiElementExtensions
    {
        internal static T WithClass<T>(this T element, string className) where T : VisualElement
        {
            element.AddToClassList(className);
            return element;
        }
    }
}