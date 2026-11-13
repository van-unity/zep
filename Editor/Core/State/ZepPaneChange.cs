using System;

namespace Zep.Core.State
{
    public enum ZepPaneChangeKind
    {
        Updated,
        Added,
        Replaced,
        Activated,
        Closed,
        Restored
    }

    [Flags]
    public enum ZepPaneUpdate
    {
        None = 0,
        Navigation = 1 << 0,
        Search = 1 << 1,
        Filter = 1 << 2,
        Selection = 1 << 3,
        Options = 1 << 4,
        VisibleAssetsProvider = 1 << 5,
        History = 1 << 6,
        Expansion = 1 << 7,
        Projection = 1 << 8
    }

    public readonly struct ZepPaneChange
    {
        private ZepPaneChange(
            ZepPaneChangeKind kind,
            ZepPaneState oldPane,
            ZepPaneState newPane,
            ZepPaneUpdate update)
        {
            Kind = kind;
            OldPane = oldPane;
            NewPane = newPane;
            Update = update;
        }

        public ZepPaneChangeKind Kind { get; }

        public ZepPaneState OldPane { get; }

        public ZepPaneState NewPane { get; }

        internal ZepPaneSource? OldSource => OldPane?.Source;

        internal ZepPaneSource? NewSource => NewPane?.Source;

        public ZepPaneUpdate Update { get; }

        public static ZepPaneChange Updated(
            ZepPaneState pane,
            ZepPaneUpdate update)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Updated,
                pane,
                pane,
                update);
        }

        internal static ZepPaneChange Added(ZepPaneState pane)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Added,
                null,
                pane,
                ZepPaneUpdate.None);
        }

        internal static ZepPaneChange Replaced(
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Replaced,
                oldPane,
                newPane,
                ZepPaneUpdate.None);
        }

        internal static ZepPaneChange Activated(
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Activated,
                oldPane,
                newPane,
                ZepPaneUpdate.None);
        }

        internal static ZepPaneChange Closed(
            ZepPaneState closedPane,
            ZepPaneState activePane)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Closed,
                closedPane,
                activePane,
                ZepPaneUpdate.None);
        }

        internal static ZepPaneChange Restored(
            ZepPaneState activePane)
        {
            return new ZepPaneChange(
                ZepPaneChangeKind.Restored,
                null,
                activePane,
                ZepPaneUpdate.None);
        }
    }

    public enum ZepSplitOperationKind
    {
        Rejected,
        Added,
        Replaced,
        Activated,
        Closed
    }

    public readonly struct ZepSplitOperationResult
    {
        private ZepSplitOperationResult(
            ZepSplitOperationKind kind,
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            Kind = kind;
            OldPane = oldPane;
            NewPane = newPane;
        }

        public ZepSplitOperationKind Kind { get; }

        internal ZepPaneState OldPane { get; }

        public ZepPaneState NewPane { get; }

        internal static ZepSplitOperationResult Added(ZepPaneState pane)
        {
            return new ZepSplitOperationResult(
                ZepSplitOperationKind.Added,
                null,
                pane);
        }

        internal static ZepSplitOperationResult Replaced(
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            return new ZepSplitOperationResult(
                ZepSplitOperationKind.Replaced,
                oldPane,
                newPane);
        }

        internal static ZepSplitOperationResult Activated(
            ZepPaneState oldPane,
            ZepPaneState newPane)
        {
            return new ZepSplitOperationResult(
                ZepSplitOperationKind.Activated,
                oldPane,
                newPane);
        }

        internal static ZepSplitOperationResult Closed(
            ZepPaneState closedPane,
            ZepPaneState activePane)
        {
            return new ZepSplitOperationResult(
                ZepSplitOperationKind.Closed,
                closedPane,
                activePane);
        }

        public static ZepSplitOperationResult Rejected()
        {
            return new ZepSplitOperationResult(
                ZepSplitOperationKind.Rejected,
                null,
                null);
        }
    }
}
