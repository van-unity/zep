using Zep.Core.FileOperations;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.FileOperations
{
    // The asset walks ask this predicate whether a directory is really one asset.
    // Answering true for an ordinary folder hides a whole subtree from the index;
    // answering false for a plugin bundle indexes internals that carry no GUID, which
    // is the reload defect this predicate exists to stop. The suffix set is the one
    // the AssetDatabase confirmed on 6000.3.10f1.
    public sealed class AssetPathClassificationTests
    {
        [TestCase("Native.bundle")]
        [TestCase("Native.framework")]
        [TestCase("Native.xcframework")]
        [TestCase("Native.plugin")]
        public void IsUnitySingleAssetDirectory_VerifiedSuffix_IsASingleAsset(string directoryName)
        {
            Assert.That(
                AssetPathClassification.IsUnitySingleAssetDirectory(directoryName),
                Is.True);
        }

        [TestCase("Native.BUNDLE")]
        [TestCase("Native.Framework")]
        [TestCase("Native.XCFramework")]
        [TestCase("Native.PlugIn")]
        public void IsUnitySingleAssetDirectory_IgnoresSuffixCase(string directoryName)
        {
            Assert.That(
                AssetPathClassification.IsUnitySingleAssetDirectory(directoryName),
                Is.True,
                "the filesystem is case-insensitive here, so the classification must be too");
        }

        // Each of these is a directory the walk must still descend into.
        [TestCase("Native.bundle2")]
        [TestCase("bundle")]
        [TestCase("framework")]
        [TestCase("Native.bundle.backup")]
        [TestCase("Editor")]
        [TestCase("Native")]
        [TestCase("")]
        [TestCase(null)]
        public void IsUnitySingleAssetDirectory_OrdinaryDirectory_IsNotASingleAsset(string directoryName)
        {
            Assert.That(
                AssetPathClassification.IsUnitySingleAssetDirectory(directoryName),
                Is.False);
        }
    }
}
