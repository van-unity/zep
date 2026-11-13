using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Zep.Core.Domain;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;

namespace Zep.Editor.UI.Sidebar
{
    internal sealed class TagsFilterWidget : VisualElement
    {
        private readonly SidebarSectionHeader _header;
        private readonly Button _addButton;
        private readonly VisualElement _tagList;
        private readonly SidebarSplitDragPrepareInteraction _splitDrag;

        private readonly List<ZepTagFilterOption> _options = new List<ZepTagFilterOption>();
        private string _activeTagId = string.Empty;
        private bool _canCreateTag;

        internal event Action<string> TagClicked;
        internal event Action CreateTagRequested;
        internal event Action<string, IReadOnlyList<string>> TagDropReceived;

        internal const string ExpandedSessionKey =
            "Zep.Sidebar.TagsExpanded";

        internal TagsFilterWidget()
        {
            name = "zep-sidebar-tags";
            AddToClassList(ZepUiClasses.SidebarSection);
            AddToClassList(ZepUiClasses.SidebarTags);

            _header = new SidebarSectionHeader("Tags", ExpandedSessionKey)
            {
                name = "zep-tag-filter-row"
            };
            _header.ExpandedChanged += _ => ApplyExpandedState();

            _addButton = new Button { name = "zep-tag-create-button", text = string.Empty };
            _addButton.AddToClassList(ZepUiClasses.FlatButton);
            _addButton.AddToClassList(ZepUiClasses.TagFilterButton);
            _addButton.AddToClassList(ZepUiClasses.MonochromeIcon);
            _addButton.RegisterCallback<MouseDownEvent>(evt => evt.StopPropagation());
            _addButton.clicked += () => CreateTagRequested?.Invoke();
            _addButton.style.display = DisplayStyle.None;
            _addButton.tooltip = "Create a new tag";

            var addIconTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(
                "Packages/com.van-unity.zep/Editor/UI/Icons/plus-icon.png");
            if (addIconTexture != null)
            {
                var addIcon = new VisualElement { name = "zep-tag-create-button-icon", pickingMode = PickingMode.Ignore };
                addIcon.AddToClassList(ZepUiClasses.CreateButtonIcon);
                addIcon.style.backgroundImage = new StyleBackground(addIconTexture);
                _addButton.Insert(0, addIcon);
            }

            // The "+" is the header's accessory: right-aligned next to
            // Hide/Show, revealed on header hover via USS.
            _header.AddAccessory(_addButton);

            _tagList = new VisualElement { name = "zep-sidebar-tags-list" };
            _tagList.RegisterCallback<DragUpdatedEvent>(OnTagListDragUpdated);
            _tagList.RegisterCallback<DragPerformEvent>(OnTagListDragPerform);
            _splitDrag = new SidebarSplitDragPrepareInteraction(
                _tagList,
                SnapshotSplitDragPayload,
                CommitSplitSourceClick);

            Add(_header);
            Add(_tagList);

            ApplyExpandedState();
        }

        internal void SetTags(IReadOnlyList<ZepTagFilterOption> options)
        {
            if (options != null && _options.Count == options.Count)
            {
                var identical = true;
                for (var i = 0; i < options.Count; i++)
                {
                    // The typed IEquatable overload, not object.Equals(a, b) —
                    // the static form boxes BOTH readonly structs, and this
                    // compare runs per tag on every refresh tick (so, on every
                    // keystroke while a search is live).
                    if (!_options[i].Equals(options[i]))
                    {
                        identical = false;
                        break;
                    }
                }
                if (identical)
                {
                    return;
                }
            }
            else if (options == null && _options.Count == 0)
            {
                return;
            }

            _options.Clear();
            if (options != null)
            {
                for (var i = 0; i < options.Count; i++)
                {
                    _options.Add(options[i]);
                }
            }

            RebuildTagList();
        }

        internal void SetActiveTagId(string tagId)
        {
            var normalized = tagId ?? string.Empty;
            if (string.Equals(_activeTagId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _activeTagId = normalized;
            // Toggle the selected class on existing rows instead of clearing
            // and rebuilding the whole tag list. Active-tag changes happen on
            // every sidebar refresh tick; the full rebuild used to fire ~20
            // VisualElement allocations per click for the typical 10-tag setup.
            ApplyActiveTagClass();
        }

        private void ApplyActiveTagClass()
        {
            foreach (var child in _tagList.Children())
            {
                if (child.userData is not string id)
                {
                    continue;
                }

                child.EnableInClassList(
                    ZepUiClasses.RowSelected,
                    string.Equals(_activeTagId, id, StringComparison.OrdinalIgnoreCase));
            }
        }

        internal void SetCanCreateTag(bool canCreateTag)
        {
            if (_canCreateTag == canCreateTag)
            {
                return;
            }

            _canCreateTag = canCreateTag;
            _addButton.SetEnabled(_canCreateTag);
            _addButton.style.display = _canCreateTag ? DisplayStyle.Flex : DisplayStyle.None;
        }

        internal bool TryGetTagIdAtTarget(VisualElement target, out string tagId)
        {
            tagId = string.Empty;
            var current = target;
            while (current != null && current != this)
            {
                if (current.userData is string candidate && !string.IsNullOrEmpty(candidate))
                {
                    tagId = candidate;
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        internal ZepSplitDragPayload SnapshotSplitDragPayload(
            VisualElement target)
        {
            if (!TryGetTagIdAtTarget(target, out var tagId))
            {
                return null;
            }

            for (var i = 0; i < _options.Count; i++)
            {
                var option = _options[i];
                if (!string.Equals(
                        option.TagId,
                        tagId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new ZepSplitDragPayload(
                    ZepPaneSource.FixedTag(option.TagId),
                    option.Tag.Name);
            }

            return null;
        }

        internal void CancelSplitDragPreparation()
        {
            _splitDrag.Cancel();
        }

        // Dropping project assets onto a tag row applies that tag. Only the
        // row under the cursor is a valid target; dragging over the header or
        // empty list space is ignored. Adding a tag an asset already has is a
        // no-op handled downstream by the tag mutation logic.
        private void OnTagListDragUpdated(DragUpdatedEvent evt)
        {
            if (!TryGetTagIdAtTarget(evt.target as VisualElement, out _))
            {
                return;
            }

            if (EditorDragAndDropPaths.ReadProjectAssetPaths().Count == 0)
            {
                return;
            }

            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
            evt.StopPropagation();
        }

        private void OnTagListDragPerform(DragPerformEvent evt)
        {
            if (!TryGetTagIdAtTarget(evt.target as VisualElement, out var tagId))
            {
                return;
            }

            // Copied, not the shared hover memo: the tag mutation keeps the
            // payload past this event.
            var projectPaths = EditorDragAndDropPaths.CopyProjectAssetPaths();
            if (projectPaths.Count == 0)
            {
                return;
            }

            DragAndDrop.AcceptDrag();
            TagDropReceived?.Invoke(tagId, projectPaths);
            evt.StopPropagation();
        }

        private void CommitSplitSourceClick(ZepSplitDragPayload payload)
        {
            for (var i = 0; i < _options.Count; i++)
            {
                if (string.Equals(
                        _options[i].TagId,
                        payload.Source.TagId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    TagClicked?.Invoke(payload.Source.TagId);
                    return;
                }
            }
        }

        internal void AnimateTagHighlight(string tagId)
        {
            if (string.IsNullOrEmpty(tagId)) return;

            foreach (var child in _tagList.Children())
            {
                if (child.userData is not string id
                    || !string.Equals(id, tagId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                child.AddToClassList(ZepUiClasses.SidebarTagsRowPulse);
                child.schedule
                    .Execute(() => child.RemoveFromClassList(ZepUiClasses.SidebarTagsRowPulse))
                    .ExecuteLater(16);
                break;
            }
        }

        private void ApplyExpandedState()
        {
            _tagList.style.display = _header.IsExpanded ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RebuildTagList()
        {
            var activeCount = 0;
            for (var i = 0; i < _options.Count; i++)
            {
                var option = _options[i];
                if (string.IsNullOrWhiteSpace(option.TagId))
                {
                    continue;
                }

                VisualElement row;
                if (activeCount < _tagList.childCount)
                {
                    row = _tagList[activeCount];
                }
                else
                {
                    row = CreateTagRow();
                    _tagList.Add(row);
                }

                UpdateTagRow(row, option);
                activeCount++;
            }

            while (_tagList.childCount > activeCount)
            {
                _tagList.RemoveAt(_tagList.childCount - 1);
            }
        }

        private VisualElement CreateTagRow()
        {
            // Focusable so a click keeps keyboard focus inside the sidebar —
            // the selection fill reads focus from the pane (SidebarWidget).
            var row = new VisualElement { focusable = true };
            row.AddToClassList(ZepUiClasses.FolderRow);
            row.AddToClassList(ZepUiClasses.SidebarTagsRow);
            row.AddToClassList(ZepUiClasses.SidebarRow);

            var iconContainer = new VisualElement();
            iconContainer.AddToClassList(ZepUiClasses.RowIcon);
            iconContainer.AddToClassList(ZepUiClasses.TagColorDot);
            row.Add(iconContainer);

            var content = new VisualElement();
            content.AddToClassList(ZepUiClasses.RowContent);
            var label = new Label();
            label.AddToClassList(ZepUiClasses.RowPrimaryText);
            content.Add(label);
            row.Add(content);

            var badge = new Label();
            badge.AddToClassList(ZepUiClasses.RowChip);
            row.Add(badge);

            row.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.button != 0 || evt.clickCount <= 0)
                {
                    return;
                }

                if (row.userData is string tagId)
                {
                    TagClicked?.Invoke(tagId);
                }
                evt.StopPropagation();
            });

            return row;
        }

        private void UpdateTagRow(VisualElement row, ZepTagFilterOption option)
        {
            row.name = "zep-sidebar-tags-row-" + option.TagId;
            row.userData = option.TagId;
            // The name elides next to the count badge, so hover restores it.
            row.tooltip = string.IsNullOrWhiteSpace(option.Tag.Name)
                ? string.Empty
                : option.Tag.Name;

            var iconContainer = row[0];
            var content = row[1];
            var label = (Label)content[0];
            var badge = (Label)row[2];

            // Apply background color if changed
            var targetColor = (Color)ZepTagColorPalette.GetColor(option.Tag.Color);
            if (iconContainer.style.backgroundColor.keyword == StyleKeyword.Null ||
                iconContainer.style.backgroundColor.value != targetColor)
            {
                ZepTagColorPalette.ApplyBackgroundColor(iconContainer, option.Tag.Color);
            }

            // Apply name if changed
            if (label.text != option.Tag.Name)
            {
                label.text = option.Tag.Name;
            }

            // Apply count badge
            if (option.Count > 0)
            {
                var countStr = option.Count.ToString();
                if (badge.text != countStr)
                {
                    badge.text = countStr;
                }
                if (badge.style.display != DisplayStyle.Flex)
                {
                    badge.style.display = DisplayStyle.Flex;
                }
            }
            else
            {
                if (badge.style.display != DisplayStyle.None)
                {
                    badge.style.display = DisplayStyle.None;
                }
            }

            var isSelected = string.Equals(_activeTagId, option.TagId, StringComparison.OrdinalIgnoreCase);
            row.EnableInClassList(ZepUiClasses.RowSelected, isSelected);
        }

    }
}
