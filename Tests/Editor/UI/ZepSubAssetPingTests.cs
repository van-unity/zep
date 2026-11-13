using Zep.Editor.TestInfrastructure;
using Zep.Editor.UI;
using Zep.Editor.UI.AssetsArea;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zep.Tests.Editor.UI
{
    // A ScriptableObject type assets can embed as a visible sub-object.
    // Top-level (not nested in the fixture) so Unity's script resolution can
    // find it when the asset is imported back.
    internal sealed class SubAssetPingProbeObject : ScriptableObject
    {
    }

    // Behavior of the ping receiver's sub-asset target detection — the routing
    // decision behind "clicking a sub-asset reference frames the sub-asset
    // row, not its container's row". The visual half (expansion, highlight,
    // bloom) needs realized rows in a live window and is verified in-editor.
    public sealed class ZepSubAssetPingTests
    {
        [Test]
        public void TryGetSubAssetTarget_VisibleSubAsset_MatchesCatalogIdentity()
        {
            using (var scope = new TempAssetScope())
            {
                var path = scope.FolderPath + "/Container.asset";
                var main = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                AssetDatabase.CreateAsset(main, path);
                var sub = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                sub.name = "EmbeddedSub";
                AssetDatabase.AddObjectToAsset(sub, main);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                Assert.That(
                    ZepPingReceiver.TryGetSubAssetTarget(sub, path, out var localId),
                    Is.True);

                // The catalog keys the sub row off the stable local file id of
                // the SAME representation instance; the receiver must derive
                // the identical key or the reveal targets no row.
                var representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
                Assert.That(representations, Has.Length.EqualTo(1));
                Assert.That(
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                        representations[0], out _, out long catalogLocalId),
                    Is.True);
                Assert.That(
                    SubAssetDescriptor.MakeKey(path, localId),
                    Is.EqualTo(SubAssetDescriptor.MakeKey(path, catalogLocalId)));
            }
        }

        [Test]
        public void TryGetSubAssetTarget_MainAsset_ReturnsFalse()
        {
            using (var scope = new TempAssetScope())
            {
                var path = scope.FolderPath + "/Container.asset";
                var main = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                AssetDatabase.CreateAsset(main, path);
                var sub = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                sub.name = "EmbeddedSub";
                AssetDatabase.AddObjectToAsset(sub, main);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                Assert.That(
                    ZepPingReceiver.TryGetSubAssetTarget(main, path, out _),
                    Is.False);
            }
        }

        [Test]
        public void TryGetSubAssetTarget_HiddenSubObject_ReturnsFalse()
        {
            using (var scope = new TempAssetScope())
            {
                var path = scope.FolderPath + "/Container.asset";
                var main = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                AssetDatabase.CreateAsset(main, path);
                // Hidden sub-objects are not visible representations, so the
                // panel has no row for them — detection must report false and
                // let the ping fall back to revealing the container.
                var hidden = ScriptableObject.CreateInstance<SubAssetPingProbeObject>();
                hidden.name = "HiddenSub";
                hidden.hideFlags = HideFlags.HideInHierarchy;
                AssetDatabase.AddObjectToAsset(hidden, main);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

                Assert.That(
                    AssetDatabase.LoadAllAssetRepresentationsAtPath(path),
                    Is.Empty);
                Assert.That(
                    ZepPingReceiver.TryGetSubAssetTarget(hidden, path, out _),
                    Is.False);
            }
        }
    }
}
