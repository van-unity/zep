using System;
using UnityEngine;

namespace Zep.Editor.UI.ContextMenu
{
    // Desktop-space placement for the popup menu windows. `fitToScreen` is the
    // editor's own "shift this rect onto the monitor under the mouse" helper;
    // it is injected so the flip decisions stay testable without a display.
    internal static class ContextMenuPopupPlacement
    {
        // A sized anchor is a button: drop below it, left-aligned. A zero-size
        // anchor is a pointer: open at the point.
        internal static Rect PlaceRoot(Rect anchorScreen, Vector2 size, Func<Rect, Rect> fitToScreen)
        {
            var origin = anchorScreen.size.sqrMagnitude > 0f
                ? new Vector2(anchorScreen.x, anchorScreen.yMax)
                : anchorScreen.position;
            return fitToScreen(new Rect(origin, size));
        }

        // Submenus open to the right of the parent, first row aligned with the
        // parent row. When the screen fit has to push the rect left (it would
        // hang off the monitor) the submenu covers its own parent, so flip it
        // to the parent's left instead.
        internal static Rect PlaceSubmenu(
            Rect parentScreen,
            float rowTopScreenY,
            Vector2 size,
            float overlap,
            float topPadding,
            Func<Rect, Rect> fitToScreen)
        {
            var right = new Rect(parentScreen.xMax - overlap, rowTopScreenY - topPadding, size.x, size.y);
            var fitted = fitToScreen(right);
            if (fitted.x >= right.x - 0.5f)
            {
                return fitted;
            }

            var left = new Rect(parentScreen.x - size.x + overlap, right.y, size.x, size.y);
            return fitToScreen(left);
        }
    }
}
