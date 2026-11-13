using System.Collections.Generic;

namespace Zep.Core.Theming
{

public static class ZepThemeSchema
{
	public static IReadOnlyList<string> RequiredVariables { get; } = new string[39]
	{
		"--zep-bg-root", "--zep-bg-pane", "--zep-bg-asset-list-band", "--zep-border-pane", "--zep-text-main", "--zep-text-subtle", "--zep-bg-input", "--zep-border-input", "--zep-text-input", "--zep-text-input-placeholder",
		"--zep-bg-button-primary", "--zep-bg-button-primary-hover", "--zep-bg-button-primary-active", "--zep-text-button-primary", "--zep-bg-button-secondary", "--zep-bg-button-secondary-hover", "--zep-bg-button-secondary-active", "--zep-text-button-secondary",
		"--zep-bg-button-danger", "--zep-bg-button-danger-hover", "--zep-text-button-danger", "--zep-bg-item-hover", "--zep-bg-item-selected-accent", "--zep-border-item-selected-accent", "--zep-bg-marquee", "--zep-border-marquee",
		"--zep-bg-splitter-grip", "--zep-bg-splitter-grip-hover", "--zep-bg-splitter-grip-dragging",
		"--zep-bg-status-pulse", "--zep-bg-prompt-scrim", "--zep-bg-prompt-card", "--zep-border-prompt-card", "--zep-bg-info-chip", "--zep-bg-tag-favorite-active", "--zep-bg-toggle-group", "--zep-bg-toggle-active", "--zep-tag-selected-border", "--zep-icon-tint"
	};

}

}
