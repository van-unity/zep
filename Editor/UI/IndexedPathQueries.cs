using Zep.Core.Application;
using UnityEditor;

namespace Zep.Editor.UI
{
    internal static class IndexedPathQueries
    {
        internal static bool IsFolder(ZepController controller, string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            return controller != null && controller.TryGetIndexedPathKind(assetPath, out var isFolder)
                ? isFolder
                : AssetDatabase.IsValidFolder(assetPath);
        }

        // Null when neither the index nor Unity knows the path.
        internal static string ResolveTypeName(ZepController controller, string assetPath)
        {
            if (controller != null && controller.TryGetIndexedAsset(assetPath, out _, out var typeName))
            {
                return typeName;
            }

            ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseTypeQueries);
            return AssetDatabase.GetMainAssetTypeAtPath(assetPath)?.Name;
        }
    }
}
