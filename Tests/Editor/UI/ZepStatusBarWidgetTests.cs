using System;
using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // The status channel's contract: a real message is never swallowed, a
    // routine re-push of the same message never re-animates the bar, and the
    // failure paths that repeat the same reason reach the toast as well as the
    // bar so the second attempt is not silent.
    public sealed class ZepStatusBarWidgetTests
    {
        [Test]
        public void TheFirstRealMessageAfterTheWindowOpens_IsShown()
        {
            var bar = new ZepStatusBarWidget();

            bar.UpdateStatus("Deleted 1 item.");

            Assert.That(bar.style.display.value, Is.EqualTo(DisplayStyle.Flex),
                "the very first message must not be consumed as a baseline snapshot");
            Assert.That(bar.ClassListContains(ZepUiClasses.StatusVisible), Is.True);
        }

        [Test]
        public void TheIdleStatusPushedOnStartup_DoesNotAnimateTheBar()
        {
            var bar = new ZepStatusBarWidget();

            // What ZepShellView.PushGlobalState pushes on the first
            // RefreshView: ZepState's status message starts empty.
            bar.UpdateStatus(string.Empty);

            Assert.That(bar.style.display.value, Is.EqualTo(DisplayStyle.None),
                "the panel must not toast its own idle status when the window opens");
            Assert.That(ToastCount(bar), Is.EqualTo(0));
        }

        [Test]
        public void AnUnchangedMessageRePushedByARoutineRefresh_DoesNotRestartTheBar()
        {
            var bar = new ZepStatusBarWidget();
            bar.UpdateStatus("Renamed item");
            Assume.That(ToastCount(bar), Is.EqualTo(1), "precondition: the first push showed the bar");

            // PushGlobalState re-pushes the current status on EVERY global
            // refresh; an unchanged message must not re-arm the hide timer.
            bar.UpdateStatus("Renamed item");
            bar.UpdateStatus("Renamed item");

            Assert.That(ToastCount(bar), Is.EqualTo(1),
                "an identical re-push must not restart the toast animation");
        }

        [Test]
        public void TheLabel_AlwaysCarriesTheLatestMessage_ShownOrNot()
        {
            var bar = new ZepStatusBarWidget();
            var label = bar.Q<Label>("zep-status-label");

            bar.UpdateStatus(string.Empty);
            Assert.That(label.text, Is.EqualTo("Ready"), "an empty status reads as the idle message");

            bar.UpdateStatus("Duplicated item");
            Assert.That(label.text, Is.EqualTo("Duplicated item"));

            bar.UpdateStatus("Duplicated item");
            Assert.That(label.text, Is.EqualTo("Duplicated item"),
                "the label still reflects the latest message even when the bar did not re-animate");
        }

        // The bar can only show a message once; the toast is the channel that
        // reacts to every raise. These two cover the paths that had neither.
        [Test]
        public void DeletingTheAssetsRootTwice_SaysItBothTimes()
        {
            var errors = new List<string>();
            using (var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets" }))
            {
                var actions = new ContextMenuActions(
                    rig.Controller,
                    null,
                    null,
                    rig.State,
                    null,
                    null,
                    null,
                    showError: message => errors.Add(message));
                var root = new ZepFolderEntry("Assets", "Assets", 0, false);

                actions.DeleteSidebarFolder(rig.State.PrimaryPane, root);
                actions.DeleteSidebarFolder(rig.State.PrimaryPane, root);

                var refusal = "Project root and package folders cannot be deleted.";
                Assert.That(rig.Controller.StatusMessage, Is.EqualTo(refusal),
                    "the status line still carries the panel's own reason");
                Assert.That(errors, Is.EqualTo(new[] { refusal, refusal }),
                    "the second Delete on the roots must not be silent");
            }
        }

        [Test]
        public void APartlyFailedDuplicateBatch_ReachesTheToastAsWellAsTheStatusBar()
        {
            var errors = new List<string>();
            var folderName = "StatusToastDuplicate_" + Guid.NewGuid().ToString("N");
            var folderPath = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            try
            {
                using (var rig = ZepUiControllerRig.Build(
                    new[] { new AssetRecord("status-toast-folder-guid", folderPath, folderName, "Folder") },
                    new[] { "Assets", folderPath }))
                {
                    // Every item in the batch is refused, so the batch reports a
                    // summary instead of the last item's own message.
                    rig.Gateway.CanWriteOverride = false;
                    var actions = new ContextMenuActions(
                        rig.Controller,
                        null,
                        null,
                        rig.State,
                        null,
                        null,
                        null,
                        showError: message => errors.Add(message));

                    actions.DuplicateContextTargets(new ZepContextMenuRequest(
                        rig.State.PrimaryPane,
                        rig.Controller,
                        new[] { folderPath },
                        "Assets",
                        Array.Empty<ZepTagFilterOption>(),
                        new Dictionary<string, ICollection<string>>(StringComparer.OrdinalIgnoreCase),
                        Array.Empty<ZepAssetEntry>(),
                        null,
                        isOpenFolderEnabled: true,
                        isDeleteEnabled: true,
                        isCreationAllowed: true,
                        canModifyAllContextPaths: false));

                    Assert.That(rig.Controller.StatusMessage, Does.Contain("1 failed"),
                        "precondition: the batch reported a partial failure on the status bar");
                    Assert.That(errors, Is.EqualTo(new[] { rig.Controller.StatusMessage }),
                        "retrying the same batch fails the same way, so the toast has to carry the summary too");
                }
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        // The toast counter is the only observable of "the animation restarted":
        // both an initial and a repeated ShowToast leave the bar visible with the
        // same class, and the difference is the re-armed hide timer.
        private static int ToastCount(ZepStatusBarWidget bar)
        {
            var field = typeof(ZepStatusBarWidget).GetField(
                "_toastVersion",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "ZepStatusBarWidget._toastVersion");
            return (int)field.GetValue(bar);
        }
    }
}
