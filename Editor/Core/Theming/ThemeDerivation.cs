using System;
using System.Collections.Generic;

namespace Zep.Core.Theming
{
    public static class ThemeDerivation
    {
        internal const float SelectedAccentAlpha = 0.30f;

        public static Dictionary<string, ZepColor> Compute(ZepColor accent, bool isLight)
        {
            ZepColor.ToHsv(accent, out var hue, out _, out _);

            var bgRoot = isLight
                ? ZepColor.FromHsv(hue, 0.04f, 0.96f)
                : ZepColor.FromHsv(hue, 0.18f, 0.075f);
            var textMain = isLight
                ? ZepColor.FromHsv(hue, 0.40f, 0.14f)
                : ZepColor.FromHsv(hue, 0.06f, 0.95f);
            var danger = isLight ? ZepColor.FromBytes(210, 76, 88) : ZepColor.FromBytes(214, 84, 91);
            var pulse = isLight ? ZepColor.FromBytes(64, 177, 114) : ZepColor.FromBytes(104, 225, 160);
            var favorite = ZepColor.FromBytes(242, 194, 63);
            var iconTint = textMain;
            var tagBorder = isLight
                ? new ZepColor(38f / 255f, 51f / 255f, 77f / 255f, 0.8f)
                : new ZepColor(1f, 1f, 1f, 0.9f);

            var bgPane = isLight
                ? ThemeColorMath.Lighten(bgRoot, 0.25f)
                : ThemeColorMath.Lighten(bgRoot, 0.05f);
            var bgBand = ThemeColorMath.Darken(bgPane, 0.025f);

            var borderPane = isLight
                ? ThemeColorMath.Darken(bgRoot, 0.18f)
                : ThemeColorMath.Lighten(bgRoot, 0.22f);

            var textSubtle = ThemeColorMath.Mix(textMain, bgPane, 0.42f);

            var bgInput = isLight
                ? ThemeColorMath.Lighten(bgRoot, 0.6f)
                : ThemeColorMath.Darken(bgRoot, 0.18f);
            var borderInput = isLight
                ? ThemeColorMath.Darken(bgInput, 0.16f)
                : ThemeColorMath.Lighten(bgInput, 0.22f);
            var placeholder = ThemeColorMath.Mix(textSubtle, bgInput, 0.25f);

            var accentHover = ThemeColorMath.Lighten(accent, 0.07f);
            var accentActive = ThemeColorMath.Darken(accent, 0.08f);
            var textOnAccent = ThemeColorMath.ContrastingForeground(accent);

            var secondary = isLight
                ? new ZepColor(0f, 0f, 0f, 0.05f)
                : new ZepColor(1f, 1f, 1f, 0.08f);
            var secondaryHover = isLight
                ? new ZepColor(0f, 0f, 0f, 0.08f)
                : new ZepColor(1f, 1f, 1f, 0.12f);
            var secondaryActive = isLight
                ? new ZepColor(0f, 0f, 0f, 0.04f)
                : new ZepColor(1f, 1f, 1f, 0.05f);

            var dangerHover = ThemeColorMath.Lighten(danger, 0.07f);
            var textOnDanger = ThemeColorMath.ContrastingForeground(danger);

            var bgItemHover = isLight
                ? ThemeColorMath.Darken(bgPane, 0.06f)
                : ThemeColorMath.Lighten(bgPane, 0.05f);
            // One alpha for both polarities, matching every built-in theme sheet:
            // the selection fill is a translucent accent tint whose opacity must
            // not vary by theme (see styling-standards.md §3).
            var bgItemSelectedAccent = ThemeColorMath.WithAlpha(accent, SelectedAccentAlpha);
            var borderItemSelectedAccent = ThemeColorMath.WithAlpha(accent, 0.65f);

            var bgMarquee = ThemeColorMath.WithAlpha(accent, 0.12f);
            var borderMarquee = ThemeColorMath.WithAlpha(accent, 0.55f);

            var bgSplitterGrip = ThemeColorMath.WithAlpha(textSubtle, 0.30f);
            var bgSplitterGripHover = ThemeColorMath.WithAlpha(textSubtle, 0.50f);

            // A modal card is only visible because the scrim pushes the backdrop
            // away from it. The light branch used to paint a LIGHT scrim at alpha
            // 0.17 over an already near-white pane, so the card landed 1.16:1
            // against its own backdrop at every hue — a dialog with no edge. Every
            // hand-written light theme darkens instead; matching that takes the
            // derived light modal to ~2.8:1. The dark branch cleared its floor by
            // 0.004, so the card lift and scrim there buy real headroom rather
            // than sit on the line. ThemeSystemTests pins both.
            var bgPromptScrim = isLight
                ? new ZepColor(0.10f, 0.11f, 0.16f, 0.45f)
                : new ZepColor(0.03f, 0.04f, 0.07f, 0.55f);
            var bgPromptCard = ThemeColorMath.Lighten(bgPane, isLight ? 0.02f : 0.06f);
            var borderPromptCard = ThemeColorMath.Mix(borderPane, bgPane, 0.20f);

            var bgInfoChip = isLight
                ? ThemeColorMath.Darken(bgPane, 0.04f)
                : ThemeColorMath.Lighten(bgPane, 0.10f);

            var bgToggleGroup = isLight
                ? new ZepColor(0f, 0f, 0f, 0.06f)
                : new ZepColor(0f, 0f, 0f, 0.20f);
            var bgToggleActive = isLight
                ? ZepColor.White
                : ThemeColorMath.Lighten(bgPane, 0.10f);

            return new Dictionary<string, ZepColor>(StringComparer.Ordinal)
            {
                ["--zep-bg-root"] = bgRoot,
                ["--zep-bg-pane"] = bgPane,
                ["--zep-bg-asset-list-band"] = bgBand,
                ["--zep-border-pane"] = borderPane,
                ["--zep-text-main"] = textMain,
                ["--zep-text-subtle"] = textSubtle,
                ["--zep-bg-input"] = bgInput,
                ["--zep-border-input"] = borderInput,
                ["--zep-text-input"] = textMain,
                ["--zep-text-input-placeholder"] = placeholder,
                ["--zep-bg-button-primary"] = accent,
                ["--zep-bg-button-primary-hover"] = accentHover,
                ["--zep-bg-button-primary-active"] = accentActive,
                ["--zep-text-button-primary"] = textOnAccent,
                ["--zep-bg-button-secondary"] = secondary,
                ["--zep-bg-button-secondary-hover"] = secondaryHover,
                ["--zep-bg-button-secondary-active"] = secondaryActive,
                ["--zep-text-button-secondary"] = textMain,
                ["--zep-bg-button-danger"] = danger,
                ["--zep-bg-button-danger-hover"] = dangerHover,
                ["--zep-text-button-danger"] = textOnDanger,
                ["--zep-bg-item-hover"] = bgItemHover,
                ["--zep-bg-item-selected-accent"] = bgItemSelectedAccent,
                ["--zep-border-item-selected-accent"] = borderItemSelectedAccent,
                ["--zep-bg-marquee"] = bgMarquee,
                ["--zep-border-marquee"] = borderMarquee,
                ["--zep-bg-splitter-grip"] = bgSplitterGrip,
                ["--zep-bg-splitter-grip-hover"] = bgSplitterGripHover,
                ["--zep-bg-splitter-grip-dragging"] = accent,
                ["--zep-bg-status-pulse"] = pulse,
                ["--zep-bg-prompt-scrim"] = bgPromptScrim,
                ["--zep-bg-prompt-card"] = bgPromptCard,
                ["--zep-border-prompt-card"] = borderPromptCard,
                ["--zep-bg-info-chip"] = bgInfoChip,
                ["--zep-bg-tag-favorite-active"] = favorite,
                ["--zep-bg-toggle-group"] = bgToggleGroup,
                ["--zep-bg-toggle-active"] = bgToggleActive,
                ["--zep-tag-selected-border"] = tagBorder,
                ["--zep-icon-tint"] = iconTint,
            };
        }

        internal static IReadOnlyList<string> AccentDerivedVariables { get; } = new[]
        {
            "--zep-bg-button-primary",
            "--zep-bg-button-primary-hover",
            "--zep-bg-button-primary-active",
            "--zep-bg-item-selected-accent",
            "--zep-border-item-selected-accent",
            "--zep-bg-marquee",
            "--zep-border-marquee",
            "--zep-bg-splitter-grip-dragging",
        };

        public static ZepColor ExtractAccent(IReadOnlyDictionary<string, ZepColor> variables)
        {
            if (variables == null)
                throw new ArgumentNullException(nameof(variables));
            return variables.TryGetValue("--zep-bg-button-primary", out var c)
                ? c
                : ZepColor.FromBytes(76, 108, 219);
        }
    }
}
