using System;
using Zep.Core.Domain;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.AssetsArea
{
    internal sealed class BreadcrumbWidget : VisualElement
    {
        private string _rootPath = string.Empty;
        private string _currentPath = string.Empty;
        // A non-path crumb ahead of the folder segments — a tag pane's tag,
        // shown alone while the pane lists the tag and as the way back once a
        // folder is open. Empty for every path-only breadcrumb. Its colour
        // dot is the only thing telling a tag pane from a folder pane of the
        // same name, now that no title row names the pane's kind.
        private string _leadingSegment = string.Empty;
        private ZepTagColor _leadingColor;

        internal event Action<string> SegmentClicked;

        internal event Action LeadingSegmentClicked;

        internal BreadcrumbWidget()
        {
            name = "zep-breadcrumb";
            AddToClassList(ZepUiClasses.Breadcrumb);
        }

        internal void SetLeadingSegment(string label, ZepTagColor color)
        {
            var normalized = label ?? string.Empty;
            if (string.Equals(_leadingSegment, normalized, StringComparison.Ordinal)
                && _leadingColor == color)
            {
                return;
            }

            _leadingSegment = normalized;
            _leadingColor = color;
            Rebuild();
        }

        internal void SetPath(string rootPath, string currentPath)
        {
            var normalizedRoot = rootPath ?? string.Empty;
            var normalizedCurrent = currentPath ?? string.Empty;
            if (string.Equals(
                    _rootPath,
                    normalizedRoot,
                    StringComparison.Ordinal)
                && string.Equals(
                    _currentPath,
                    normalizedCurrent,
                    StringComparison.Ordinal))
            {
                return;
            }

            _rootPath = normalizedRoot;
            _currentPath = normalizedCurrent;
            Rebuild();
        }

        private void Rebuild()
        {
            Clear();

            var hasVisibleSegment = false;
            if (!string.IsNullOrEmpty(_leadingSegment))
            {
                var dot = new VisualElement
                {
                    name = "zep-breadcrumb-tag-dot",
                    pickingMode = PickingMode.Ignore
                };
                dot.AddToClassList(ZepUiClasses.TagColorDot);
                ZepTagColorPalette.ApplyBackgroundColor(dot, _leadingColor);
                Add(dot);

                var leading = new Button(() => LeadingSegmentClicked?.Invoke())
                {
                    name = "zep-breadcrumb-leading-segment",
                    text = _leadingSegment,
                    tooltip = _leadingSegment
                };
                leading.AddToClassList(ZepUiClasses.BreadcrumbSegment);
                leading.AddToClassList(ZepUiClasses.FlatButton);
                Add(leading);
                hasVisibleSegment = true;
            }

            if (string.IsNullOrEmpty(_currentPath))
            {
                return;
            }

            var segments = _currentPath.Split('/');
            var cumulative = string.Empty;
            var clampToRoot = IsCurrentPathInsideRoot();
            var reachedRoot = false;

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                {
                    continue;
                }

                cumulative = i == 0 ? segment : cumulative + "/" + segment;
                if (clampToRoot && !reachedRoot)
                {
                    if (!string.Equals(
                            cumulative,
                            _rootPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    reachedRoot = true;
                }

                var segmentPath = cumulative;

                if (hasVisibleSegment)
                {
                    var separator = new Label("\u203a") { name = "zep-breadcrumb-separator" };
                    separator.AddToClassList(ZepUiClasses.BreadcrumbSeparator);
                    Add(separator);
                }

                var button = new Button(() => SegmentClicked?.Invoke(segmentPath))
                {
                    name = "zep-breadcrumb-segment",
                    text = segment,
                    tooltip = segmentPath
                };
                button.AddToClassList(ZepUiClasses.BreadcrumbSegment);
                button.AddToClassList(ZepUiClasses.FlatButton);
                Add(button);
                hasVisibleSegment = true;
            }
        }

        private bool IsCurrentPathInsideRoot()
        {
            if (string.IsNullOrEmpty(_rootPath)
                || _currentPath.Length < _rootPath.Length
                || !_currentPath.StartsWith(
                    _rootPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return _currentPath.Length == _rootPath.Length
                || _currentPath[_rootPath.Length] == '/';
        }
    }
}
