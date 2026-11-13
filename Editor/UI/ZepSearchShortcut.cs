using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace Zep.Editor.UI
{
    // Owns the per-user Search binding and the available choices shared by
    // settings, the launcher hint, and keyboard dispatch. None means double Shift.
    internal static class ZepSearchShortcut
    {
        internal const string PreferenceKey = "Zep.SearchShortcut";
        private static Dictionary<KeyCombination, string> _occupied;
        internal static event Action Changed;

        static ZepSearchShortcut()
        {
            ShortcutManager.instance.activeProfileChanged += _ => InvalidateBindings();
            ShortcutManager.instance.shortcutBindingChanged += _ => InvalidateBindings();
        }

        internal static KeyCombination Current
        {
            get
            {
                // One preference write keeps the key and modifiers together.
                var value = EditorPrefs.GetInt(PreferenceKey, 0);
                return new KeyCombination((KeyCode)(value & 0xffff), (ShortcutModifiers)(value >> 16));
            }
        }

        internal static bool UsesDoubleShift => Current.Equals(default(KeyCombination));

        internal static bool TrySet(KeyCombination binding, out string reason)
        {
            reason = GetUnavailableReason(binding);
            if (reason != null) return false;
            if (binding.Equals(default(KeyCombination))) EditorPrefs.DeleteKey(PreferenceKey);
            else EditorPrefs.SetInt(PreferenceKey, (int)binding.keyCode | ((int)binding.modifiers << 16));
            Changed?.Invoke();
            return true;
        }

        private static void InvalidateBindings()
        {
            _occupied = null;
            Changed?.Invoke();
        }

        internal static List<KeyCombination> GetAvailableBindings()
        {
            var result = new List<KeyCombination>();
            var mac = ZepShortcutController.IsMacPlatform(Application.platform);
            var modifierBits = new[] { ShortcutModifiers.Action, ShortcutModifiers.Shift,
                ShortcutModifiers.Alt, ShortcutModifiers.Control };
            for (var mask = 0; mask < (mac ? 16 : 8); mask++)
            {
                var modifiers = ShortcutModifiers.None;
                for (var bit = 0; bit < modifierBits.Length; bit++)
                    if ((mask & (1 << bit)) != 0) modifiers |= modifierBits[bit];
                foreach (var key in CandidateKeys())
                {
                    var binding = new KeyCombination(key, modifiers);
                    if (GetUnavailableReason(binding) == null) result.Add(binding);
                }
            }
            return result;
        }

        private static IEnumerable<KeyCode> CandidateKeys()
        {
            for (var key = KeyCode.A; key <= KeyCode.Z; key++) yield return key;
            for (var key = KeyCode.Alpha0; key <= KeyCode.Alpha9; key++) yield return key;
            for (var key = KeyCode.F1; key <= KeyCode.F15; key++) yield return key;
            foreach (var key in new[] { KeyCode.BackQuote, KeyCode.Minus, KeyCode.Equals,
                KeyCode.LeftBracket, KeyCode.RightBracket, KeyCode.Backslash, KeyCode.Semicolon,
                KeyCode.Quote, KeyCode.Comma, KeyCode.Period, KeyCode.Slash,
                KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow,
                KeyCode.Home, KeyCode.End, KeyCode.PageUp, KeyCode.PageDown, KeyCode.Insert })
                yield return key;
            for (var key = KeyCode.Keypad0; key <= KeyCode.KeypadEquals; key++)
                if (key != KeyCode.KeypadEnter) yield return key;
        }

        internal static string GetUnavailableReason(KeyCombination binding)
        {
            if (binding.Equals(default(KeyCombination))) return null;
            if (!CandidateKeys().Contains(binding.keyCode)) return "Choose a keyboard shortcut from the available keys.";
            const ShortcutModifiers allowed = ShortcutModifiers.Action | ShortcutModifiers.Control
                | ShortcutModifiers.Alt | ShortcutModifiers.Shift;
            if ((binding.modifiers & ~allowed) != 0) return "Unsupported modifier keys.";
            var isFunctionKey = binding.keyCode >= KeyCode.F1 && binding.keyCode <= KeyCode.F15;
            if (!isFunctionKey && (binding.modifiers & ~ShortcutModifiers.Shift) == 0)
                return "Use a modifier with this key so typing and navigation keep working.";

            binding = Normalize(binding, Application.platform);
            if (ZepShortcutController.IsReservedSearchBinding(binding, Application.platform))
                return "Already used by Zep.";
            if (!ZepShortcutController.IsMacPlatform(Application.platform)
                && binding.keyCode == KeyCode.F4 && (binding.modifiers & ShortcutModifiers.Alt) != 0)
                return "Reserved for closing windows.";

            if (_occupied == null)
            {
                _occupied = new Dictionary<KeyCombination, string>();
                var manager = ShortcutManager.instance;
                foreach (var id in manager.GetAvailableShortcutIds())
                {
                    // A single chord must not take the first step of a Unity sequence.
                    foreach (var first in manager.GetShortcutBinding(id).keyCombinationSequence.Take(1))
                        _occupied[Normalize(first, Application.platform)] = id;
                }
            }
            return _occupied.TryGetValue(binding, out var owner) ? "Already used by Unity: " + owner : null;
        }

        internal static KeyCombination Normalize(KeyCombination binding, RuntimePlatform platform)
        {
            var modifiers = binding.modifiers;
            if (!ZepShortcutController.IsMacPlatform(platform)
                && (modifiers & ShortcutModifiers.Control) != 0)
                modifiers = (modifiers & ~ShortcutModifiers.Control) | ShortcutModifiers.Action;
            return new KeyCombination(binding.keyCode, modifiers);
        }

        internal static bool Matches(KeyCode key, EventModifiers modifiers, RuntimePlatform platform)
        {
            var current = Current;
            if (current.keyCode == KeyCode.None || current.keyCode != key) return false;
            var shortcutModifiers = ShortcutModifiers.None;
            if ((modifiers & EventModifiers.Shift) != 0) shortcutModifiers |= ShortcutModifiers.Shift;
            if ((modifiers & EventModifiers.Alt) != 0) shortcutModifiers |= ShortcutModifiers.Alt;
            if ((modifiers & EventModifiers.Control) != 0) shortcutModifiers |= ShortcutModifiers.Control;
            if ((modifiers & EventModifiers.Command) != 0) shortcutModifiers |= ShortcutModifiers.Action;
            return Normalize(current, platform).Equals(Normalize(new KeyCombination(key, shortcutModifiers), platform))
                && GetUnavailableReason(current) == null;
        }

        internal static string Format(KeyCombination binding) => binding.Equals(default(KeyCombination))
            ? "Double Shift" : (binding.modifiers == ShortcutModifiers.None ? string.Empty
                : FormatModifiers(binding.modifiers) + " + ") + FormatKey(binding.keyCode);

        internal static string FormatKey(KeyCode key) => key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9
            ? ((int)key - (int)KeyCode.Alpha0).ToString() : key.ToString();

        internal static string FormatModifiers(ShortcutModifiers modifiers)
        {
            var parts = new List<string>();
            if ((modifiers & ShortcutModifiers.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ShortcutModifiers.Action) != 0)
                parts.Add(ZepShortcutController.IsMacPlatform(Application.platform) ? "Cmd" : "Ctrl");
            if ((modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
            return parts.Count == 0 ? "None" : string.Join(" + ", parts);
        }
    }
}
