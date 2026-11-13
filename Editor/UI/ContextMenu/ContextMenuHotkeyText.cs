using System.Text;
using UnityEngine;

namespace Zep.Editor.UI.ContextMenu
{
    // Turns a Unity MenuItem hotkey string ("%r", "#&d", "_F2") into the text a
    // human expects to read on a menu row. Strings that are not in Unity's
    // format (our own "⌘⌫"-style hotkeys, or empty) pass through untouched.
    internal static class ContextMenuHotkeyText
    {
        internal static string Format(string hotkey)
        {
            return Format(hotkey, Application.platform == RuntimePlatform.OSXEditor);
        }

        internal static string Format(string hotkey, bool macStyle)
        {
            var text = (hotkey ?? string.Empty).Trim();
            if (text.Length == 0 || !IsUnityFormat(text[0]))
            {
                return text;
            }

            bool ctrl = false, alt = false, shift = false, cmd = false;
            var index = 0;
            for (; index < text.Length; index++)
            {
                var c = text[index];
                if (c == '%') { cmd = true; }
                else if (c == '#') { shift = true; }
                else if (c == '&') { alt = true; }
                else if (c == '^') { ctrl = true; }
                else if (c == '_') { }
                else { break; }
            }

            var key = FormatKey(text.Substring(index), macStyle);
            var builder = new StringBuilder();
            if (macStyle)
            {
                // Apple HIG order: control, option, shift, command.
                if (ctrl) builder.Append('⌃');
                if (alt) builder.Append('⌥');
                if (shift) builder.Append('⇧');
                if (cmd) builder.Append('⌘');
                builder.Append(key);
            }
            else
            {
                if (cmd || ctrl) builder.Append("Ctrl+");
                if (alt) builder.Append("Alt+");
                if (shift) builder.Append("Shift+");
                builder.Append(key);
            }

            return builder.ToString();
        }

        private static bool IsUnityFormat(char first)
        {
            return first == '%' || first == '#' || first == '&' || first == '^' || first == '_';
        }

        private static string FormatKey(string key, bool macStyle)
        {
            if (key.Length == 1)
            {
                return key.ToUpperInvariant();
            }

            switch (key.ToUpperInvariant())
            {
                case "DEL":
                case "DELETE":
                    return macStyle ? "⌦" : "Del";
                case "BACKSPACE":
                    return macStyle ? "⌫" : "Backspace";
                case "LEFT":
                    return macStyle ? "←" : "Left";
                case "RIGHT":
                    return macStyle ? "→" : "Right";
                case "UP":
                    return macStyle ? "↑" : "Up";
                case "DOWN":
                    return macStyle ? "↓" : "Down";
                case "HOME":
                    return macStyle ? "↖" : "Home";
                case "END":
                    return macStyle ? "↘" : "End";
                case "PGUP":
                    return macStyle ? "⇞" : "PgUp";
                case "PGDN":
                    return macStyle ? "⇟" : "PgDn";
                case "TAB":
                    return macStyle ? "⇥" : "Tab";
                case "SPACE":
                    return "Space";
                case "ENTER":
                case "RETURN":
                    return macStyle ? "↩" : "Enter";
                case "ESC":
                case "ESCAPE":
                    return macStyle ? "⎋" : "Esc";
                case "INS":
                case "INSERT":
                    return "Ins";
                default:
                    return key.ToUpperInvariant();
            }
        }
    }
}
