using UnityEngine;
using UnityEngine.UIElements;
using Zep.Core.Domain;

namespace Zep.Editor.UI
{
    internal static class ZepTagColorPalette
    {
        // Indexed by (int)ZepTagColor.
        // Index 0 = None → fallback gray (matches the default branch of the previous switches).
        //
        // WHY these are Color literals instead of var(--zep-*) — the third and last
        // documented exception to §3 of styling-standards.md, and the only one that
        // lives in C# rather than in Zep.uss. A tag's color IS the tag's
        // identity: the user picks one of these seven from a fixed set, and "the red
        // tag" has to stay the same red in all 17 variable tables or the identity
        // stops meaning anything across a theme switch. Tokenizing would add 8
        // entries to the schema and to every theme sheet to encode 8 constants no
        // theme is allowed to vary. Same rationale as the R/G/B slider draggers.
        private static readonly Color32[] Colors = new Color32[]
        {
            new Color32(131, 139, 154, 255), // None (fallback)
            new Color32(157, 164, 174, 255), // Gray
            new Color32( 74, 194,  98, 255), // Green
            new Color32(170, 114, 255, 255), // Purple
            new Color32( 81, 156, 255, 255), // Blue
            new Color32(242, 194,  63, 255), // Yellow
            new Color32(239,  90,  85, 255), // Red
            new Color32(244, 145,  56, 255), // Orange
        };

        internal static Color GetColor(ZepTagColor color)
        {
            var index = (int)color;
            if (index < 0 || index >= Colors.Length) return Colors[0];
            return Colors[index];
        }

        internal static void ApplyBackgroundColor(VisualElement element, ZepTagColor color)
        {
            if (element == null) return;
            element.style.backgroundColor = new StyleColor(GetColor(color));
        }
    }
}
