namespace Zep.Core.State
{
    // Which mode the assets pane is showing. Lives in Core.State because the
    // state layer owns it and both Core and the UI dispatch on it.
    public enum AssetsPaneMode
    {
        Browser,
        Tags
    }
}
