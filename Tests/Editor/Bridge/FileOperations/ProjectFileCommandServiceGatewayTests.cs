using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using NUnit.Framework;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    // The command service against the real Unity gateway and AssetDatabase. Its
    // fake-gateway cases are Unity-free and live with the Core tests.
    public sealed class ProjectFileCommandServiceGatewayTests
    {
        [Test]
        public void RenameThroughTheRealGateway_ResolvesTheIdentityOnceInsteadOfTwice()
        {
            // Same claim as the fake-gateway cases, but against the gateway that
            // actually opens the .meta file, counted where the read happens
            // (UnityMetaFileReader.ReadGuidFromMetaFile).
            //
            // The absolute count is not 1: AssetDatabase.MoveAsset provokes an
            // import, and this editor session's [InitializeOnLoad]
            // ZepControllerHost reacts to it with an identity read of
            // its own (measured as 1 in the batchmode rig). That read is
            // present before and after this change and belongs to a different
            // finding, so the assertions below compare two renames that both
            // pay it: a command the service handed a verified identity to, and
            // one built without that hand-off — which is exactly the shape
            // every rehydrated (FromRecord) replay has.
            var suffix = System.Guid.NewGuid().ToString("N");
            var folderName = "CommandServiceMetaReads_" + suffix;
            var folderPath = "Assets/" + folderName;
            UnityEditor.AssetDatabase.CreateFolder("Assets", folderName);
            var servicePath = folderPath + "/ViaService.txt";
            var standalonePath = folderPath + "/Standalone.txt";
            System.IO.File.WriteAllText(servicePath, "content");
            System.IO.File.WriteAllText(standalonePath, "content");
            UnityEditor.AssetDatabase.ImportAsset(servicePath, UnityEditor.ImportAssetOptions.ForceUpdate);
            UnityEditor.AssetDatabase.ImportAsset(standalonePath, UnityEditor.ImportAssetOptions.ForceUpdate);

            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;

                var gateway = new Zep.Editor.Bridge.FileOperations.UnityProjectAssetGateway();
                var service = new ProjectFileCommandService(gateway);

                ZepPerfLog.Reset();
                var identityGuid = gateway.GetGuid(AssetPath.From(standalonePath));
                var oneIdentityRead = ZepPerfLog.CaptureSnapshot()
                    .GetCount(ZepPerfCounter.MetaFileGuidReads);
                Assert.That(identityGuid, Is.Not.Empty, "the fixture asset must have a readable .meta");
                Assert.That(
                    oneIdentityRead,
                    Is.EqualTo(1),
                    "resolving one identity is exactly one .meta read");

                // No hand-off: the command validates for itself. This is the
                // red baseline, measured in this run.
                var standalone = new RenameCommand(
                    AssetPath.From(standalonePath), "StandaloneRenamed", false, identityGuid);
                ZepPerfLog.Reset();
                var standaloneResult = standalone.Execute(gateway);
                var unverifiedReads = ZepPerfLog.CaptureSnapshot()
                    .GetCount(ZepPerfCounter.MetaFileGuidReads);
                Assert.That(standaloneResult.Succeeded, Is.True, standaloneResult.ErrorMessage);
                Assert.That(
                    unverifiedReads,
                    Is.GreaterThanOrEqualTo(1),
                    "a command without a verified identity reads the .meta to validate");

                ZepPerfLog.Reset();
                var rename = service.Rename(AssetPath.From(servicePath), "ServiceRenamed");
                var serviceReads = ZepPerfLog.CaptureSnapshot()
                    .GetCount(ZepPerfCounter.MetaFileGuidReads);
                Assert.That(rename.Succeeded, Is.True, rename.ErrorMessage);
                Assert.That(
                    serviceReads,
                    Is.EqualTo(unverifiedReads),
                    "the service's own identity read REPLACES the command's; before this change "
                        + "the forward rename paid for both");

                ZepPerfLog.Reset();
                var undo = service.Undo();
                var undoReads = ZepPerfLog.CaptureSnapshot()
                    .GetCount(ZepPerfCounter.MetaFileGuidReads);
                Assert.That(undo.Succeeded, Is.True, undo.ErrorMessage);
                Assert.That(
                    undoReads,
                    Is.EqualTo(unverifiedReads),
                    "undo replays a rehydrated command and must still validate against disk");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
                ZepPerfLog.Reset();
                UnityEditor.AssetDatabase.DeleteAsset(folderPath);
            }
        }
    }
}
