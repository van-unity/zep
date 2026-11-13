using System;
using System.Collections.Generic;
using Zep.Editor.Bridge.Theming;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Theming
{
    internal sealed class ThemePreviewCard : VisualElement
    {
        public const string PreviewThemeId = "preview";

        private static readonly string PreviewThemeClass =
            ZepThemeRegistry.GetThemeClassName(PreviewThemeId);

        private StyleSheet _runtimeSheet;
        private IReadOnlyDictionary<string, Color> _pendingVariables;
        private bool _rebuildPending;
        private int _rebuildGeneration;

        public ThemePreviewCard()
        {
            AddToClassList(ZepUiClasses.CreatorPreviewCard);
            AddToClassList(ZepUiClasses.Root);
            AddToClassList(PreviewThemeClass);

            var heading = new Label("Sample heading");
            heading.AddToClassList(ZepUiClasses.CreatorPreviewHeading);
            Add(heading);

            var body = new Label("Body text in your custom palette.");
            body.AddToClassList(ZepUiClasses.CreatorPreviewBody);
            Add(body);

            var actions = new VisualElement();
            actions.AddToClassList(ZepUiClasses.CreatorPreviewActions);
            var primary = new Label("Primary");
            primary.AddToClassList(ZepUiClasses.CreatorPreviewButton);
            primary.AddToClassList(ZepUiClasses.CreatorPreviewButtonPrimary);
            actions.Add(primary);
            var outline = new Label("Outline");
            outline.AddToClassList(ZepUiClasses.CreatorPreviewButton);
            outline.AddToClassList(ZepUiClasses.CreatorPreviewButtonOutline);
            actions.Add(outline);
            Add(actions);

            RegisterCallback<DetachFromPanelEvent>(_ => OnDetachFromPanel());
        }

        // WHY: a seed-slider drag re-derives the palette on every pointer move
        // (~60/s) and each rebuild destroys the sheet, re-imports generated USS
        // through the reflective importer and restyles this subtree twice. Only
        // the newest palette is ever shown, so hold it and rebuild once on the
        // next frame, version-guarded like the creator's show/hide animations.
        public void ApplyVariables(IReadOnlyDictionary<string, Color> variables)
        {
            _pendingVariables = variables;
            if (_rebuildPending) return;

            _rebuildPending = true;
            var generation = ++_rebuildGeneration;
            schedule.Execute(() =>
            {
                if (generation != _rebuildGeneration) return;
                _rebuildPending = false;
                RebuildRuntimeSheet();
            });
        }

        private void RebuildRuntimeSheet()
        {
            var variables = _pendingVariables;
            _pendingVariables = null;
            DisposeRuntimeSheet();
            if (variables == null) return;
            _runtimeSheet = RuntimeThemeStyleSheetBuilder.Build(PreviewThemeId, variables);
            if (_runtimeSheet != null)
                styleSheets.Add(_runtimeSheet);
        }

        // A rebuild scheduled before detachment must not run afterwards: it would
        // re-create the sheet this card just disposed and leave it orphaned on a
        // dead element. Bumping the generation retires it.
        private void OnDetachFromPanel()
        {
            _rebuildGeneration++;
            _rebuildPending = false;
            _pendingVariables = null;
            DisposeRuntimeSheet();
        }

        private void DisposeRuntimeSheet()
        {
            if (_runtimeSheet == null) return;
            styleSheets.Remove(_runtimeSheet);
            UnityEngine.Object.DestroyImmediate(_runtimeSheet);
            _runtimeSheet = null;
        }
    }
}
