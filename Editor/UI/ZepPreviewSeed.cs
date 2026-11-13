using Zep.Core.FileOperations;
using Zep.Core.Domain;
using Zep.Core.State;

namespace Zep.Editor.UI
{
    public static class ZepPreviewSeed
    {
        public static ZepState Build()
        {
            var viewModel = new ZepState();
            viewModel.PrimaryPane.TrySetCurrentFolderPath(AssetPath.From("Assets/Art/UI"));
            viewModel.PrimaryPane.TrySetSelectedFolderPath(AssetPath.From("Assets/Art/UI"));
            // Full lineage down to the current folder, mirroring
            // FolderEntriesBuilder (which always seeds the roots): the folder
            // trees render strictly parent-down, so rootless fixtures show an
            // empty sidebar.
            viewModel.SetFolders(new[]
            {
                new ZepFolderEntry("Assets", "Assets", 0, false),
                new ZepFolderEntry("Art", "Assets/Art", 1, false),
                new ZepFolderEntry("UI", "Assets/Art/UI", 2, false),
                new ZepFolderEntry("Animations", "Assets/Art/UI/Animations", 3, false),
                new ZepFolderEntry("Icons", "Assets/Art/UI/Icons", 3, false),
                new ZepFolderEntry("Prefabs", "Assets/Art/UI/Prefabs", 3, false)
            });
            // After Step 7 the asset list is no longer plumbed through the
            // view model — each AssetsArea feature pulls from a projection.
            // Without a real controller we have no projection to feed, so the
            // preview renders chrome (folders, tags, status) but an empty
            // asset list. Acceptable for design preview; real preview data
            // would need a fake projection feature seam.
            viewModel.PrimaryPane.SetTypeFilterOptions(new[]
            {
                new ZepTypeFilterOption(string.Empty, 3),
                new ZepTypeFilterOption("UXML", 1),
                new ZepTypeFilterOption("USS", 1),
                new ZepTypeFilterOption("C#", 1)
            });
            viewModel.PrimaryPane.SetSearchScopeOptions(new[]
            {
                new ZepSearchScopeOption(ZepSearchScope.All, "All"),
                new ZepSearchScopeOption(ZepSearchScope.Packages, "In Packages"),
                new ZepSearchScopeOption(ZepSearchScope.Assets, "In Assets"),
                new ZepSearchScopeOption(ZepSearchScope.CurrentFolder, "'UI'")
            });
            viewModel.SetTagFilterOptions(new[]
            {
                new ZepTagFilterOption(new ZepTag("blue", "UI", ZepTagColor.Blue, true), 2),
                new ZepTagFilterOption(new ZepTag("purple", "Polish", ZepTagColor.Purple, true), 1),
                new ZepTagFilterOption(new ZepTag("green", "Ready", ZepTagColor.Green, true), 1)
            });
            viewModel.PrimaryPane.SetSelectedAssetPath(
                AssetPath.From("Assets/Art/UI/Zep.uxml"));
            viewModel.SetStatusMessage("Preview mode");
            return viewModel;
        }
    }
}
