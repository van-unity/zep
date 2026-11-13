using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;
using Zep.Editor.UI;
using Zep.Editor.UI.Tests;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Tests.Editor.UI
{
    // The receiver has two mutually exclusive implementations (see the WHY TWO
    // RECEIVERS comment on ZepPingReceiver), so the contract each one
    // depends on is pinned per editor version: the Unity internals each one
    // relies on, and the maximize-error behavior each one must not trigger.
    // Unity's own ping dispatch is not ours to test.
    public sealed class ZepPingReceiverTests
    {
        private PingReceiverStateSnapshot _revealState;

        [SetUp]
        public void PreservePendingReveal()
        {
            _revealState = new PingReceiverStateSnapshot();
        }

        [TearDown]
        public void RestorePendingReveal()
        {
            _revealState?.Dispose();
            _revealState = null;
        }

#if !UNITY_6000_3_OR_NEWER
        [Test]
        public void LegacyProjectBrowser_ExposesRequiredPingReceiverContract()
        {
            var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser, UnityEditor");

            Assert.That(projectBrowserType, Is.Not.Null);
            Assert.That(projectBrowserType.GetMethod(
                "Init", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null),
                Is.Not.Null);
            Assert.That(projectBrowserType.GetField(
                "m_LastFramedID", BindingFlags.NonPublic | BindingFlags.Instance),
                Is.Not.Null);
            Assert.That(projectBrowserType.GetMethod(
                "FrameObject",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(int), typeof(bool) },
                null),
                Is.Not.Null);
            Assert.That(projectBrowserType.GetField(
                "s_LastInteractedProjectBrowser",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                Is.Not.Null);
        }
        // Unity's maximize (WindowLayout.MaximizePresent) runs this check and
        // logs an error for every EditorWindow without a host view. The legacy
        // receiver is one by design; the filter must hide its error and only
        // its error — an undocked real Project window has to keep reporting.
        [Test]
        public void MaximizeConsistencyCheck_ReportsAPlainProjectBrowserButNotTheReceiver()
        {
            var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser, UnityEditor");
            var lastInteracted = projectBrowserType.GetField(
                "s_LastInteractedProjectBrowser",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var check = typeof(EditorWindow).Assembly
                .GetType("UnityEditor.WindowLayout")
                ?.GetMethod("CheckWindowConsistency", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(check, Is.Not.Null, "the check Unity's maximize runs");

            var previousLastInteracted = lastInteracted.GetValue(null);
            var errors = new List<string>();
            Application.LogCallback collect = (message, stackTrace, type) =>
            {
                if (type == LogType.Error)
                {
                    errors.Add(message);
                }
            };
            EditorWindow receiver = null;
            EditorWindow plain = null;

            try
            {
                receiver = (EditorWindow)ScriptableObject.CreateInstance(projectBrowserType);
                receiver.hideFlags = HideFlags.HideAndDontSave;
                receiver.titleContent = new GUIContent(ZepPingReceiver.LegacyReceiverName);
                plain = (EditorWindow)ScriptableObject.CreateInstance(projectBrowserType);
                plain.hideFlags = HideFlags.HideAndDontSave;

                ZepPingReceiver.InstallLegacyMaximizeErrorFilter();

                // The plain window's error is the positive control, and any other
                // undocked window in the run would log too: collect, don't fail.
                LogAssert.ignoreFailingMessages = true;
                Application.logMessageReceived += collect;
                check.Invoke(null, null);
            }
            finally
            {
                Application.logMessageReceived -= collect;
                LogAssert.ignoreFailingMessages = false;
                if (receiver != null)
                {
                    UnityEngine.Object.DestroyImmediate(receiver);
                }

                if (plain != null)
                {
                    UnityEngine.Object.DestroyImmediate(plain);
                }

                lastInteracted.SetValue(null, previousLastInteracted);
            }

            Assert.That(
                errors.Where(message => message.Contains(ZepPingReceiver.LegacyReceiverName)),
                Is.Empty,
                "the hidden receiver must not report on maximize");
            Assert.That(
                errors,
                Has.Some.EqualTo("Invalid editor window of type: UnityEditor.ProjectBrowser, title: Project"),
                "a real undocked Project window must still be reported");
        }
#else
        // Unity's PingObject dispatch is `window is IFramableContainer`, checked
        // against the interface declared in UnityEditor.CoreModule. UnityEditor.dll
        // declares a second, unrelated one, so pin the identity the built-in
        // implementer carries.
        [Test]
        public void PanelWindow_ImplementsTheFramableInterfaceUnityPingsThrough()
        {
            var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser, UnityEditor");
            Assert.That(projectBrowserType, Is.Not.Null);

            var framableInterface = projectBrowserType.GetInterfaces()
                .SingleOrDefault(itf => itf.Name == "IFramableContainer");
            Assert.That(
                framableInterface,
                Is.Not.Null,
                "6.3 dispatches pings only to IFramableContainer windows");
            Assert.That(
                framableInterface.IsAssignableFrom(typeof(ZepWindow)),
                Is.True,
                "the panel window must satisfy the exact check Unity's dispatch runs");
        }

        // §4.9 + §5.29. The receiver used to be a hidden, never-docked window
        // built on the first open, and Unity's maximize logged an error for it
        // ("Invalid editor window"). The panel window is the receiver now.
        [Test]
        public void FirstPanelWindowOpen_CreatesNoOtherWindowAndCountsTheWindowOnce()
        {
            var previousWindows = SnapshotOpenWindows();
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                SetOpenWindows(null);
                var windowsBefore = Resources.FindObjectsOfTypeAll<EditorWindow>().Length;

                InvokeOn(window, "RegisterOpenWindow");

                Assert.That(
                    Resources.FindObjectsOfTypeAll<EditorWindow>().Length,
                    Is.EqualTo(windowsBefore),
                    "the first open must not build a hidden receiver window");
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(1));
                Assert.That(ZepWindow.HasOpenWindow, Is.True);
                Assert.That(
                    ZepWindow.OpenWindows,
                    Is.EqualTo(new[] { window }),
                    "the registry IS the set of open windows the ping/menu/refocus lookups read");
                Assert.That(
                    ZepWindow.FindByPanel(null),
                    Is.Null,
                    "an unmounted owner has no panel and therefore no host window");

                // CreateGUI runs again after every domain reload while OnDestroy
                // runs only on a real close, so a second registration of the same
                // window must not inflate the count.
                InvokeOn(window, "RegisterOpenWindow");
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(1));

                InvokeOn(window, "OnDestroy");
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(0));
                Assert.That(ZepWindow.HasOpenWindow, Is.False);

                InvokeOn(window, "OnDestroy");
                Assert.That(
                    ZepWindow.OpenWindowCount,
                    Is.EqualTo(0),
                    "an unregistered window must not push the count negative");
            }
            finally
            {
                SetOpenWindows(previousWindows);
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // Unity pings every framable window, so each open panel is called; the
        // reveal runs on the shared controller and must run once per ping.
        [Test]
        public void OnlyTheFirstRegisteredPanel_AnswersAPing()
        {
            var previousWindows = SnapshotOpenWindows();
            var first = ScriptableObject.CreateInstance<ZepWindow>();
            var second = ScriptableObject.CreateInstance<ZepWindow>();
            var unregistered = ScriptableObject.CreateInstance<ZepWindow>();
            first.hideFlags = HideFlags.HideAndDontSave;
            second.hideFlags = HideFlags.HideAndDontSave;
            unregistered.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                SetOpenWindows(null);
                Assert.That(first.AnswersPings, Is.False, "no panel is open yet");

                InvokeOn(first, "RegisterOpenWindow");
                InvokeOn(second, "RegisterOpenWindow");

                Assert.That(first.AnswersPings, Is.True);
                Assert.That(second.AnswersPings, Is.False, "a second panel must not reveal again");
                Assert.That(unregistered.AnswersPings, Is.False, "a window without a built panel has nowhere to reveal");

                InvokeOn(first, "OnDestroy");
                Assert.That(second.AnswersPings, Is.True, "closing the answering panel hands pings to the next");
            }
            finally
            {
                SetOpenWindows(previousWindows);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
                UnityEngine.Object.DestroyImmediate(unregistered);
            }
        }
#endif

        [Test]
        public void ClosingLastPanelWindow_EmptiesRegistryAndReopenAnswersPings()
        {
            var previousWindows = SnapshotOpenWindows();
            var first = ScriptableObject.CreateInstance<ZepWindow>();
            var reopened = ScriptableObject.CreateInstance<ZepWindow>();
            first.hideFlags = HideFlags.HideAndDontSave;
            reopened.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                SetOpenWindows(null);
                InvokeOn(first, "RegisterOpenWindow");
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(1));

                // A real close disables the window before destroying it.
                InvokeOn(first, "OnDisable");
                UnityEngine.Object.DestroyImmediate(first);

                Assert.That(ZepWindow.OpenWindows, Is.Empty);
                Assert.That(ZepWindow.OpenWindowCount, Is.Zero);
                Assert.That(ZepWindow.HasOpenWindow, Is.False);

                InvokeOn(reopened, "RegisterOpenWindow");
                Assert.That(ZepWindow.OpenWindows, Is.EqualTo(new[] { reopened }));
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(1));
#if UNITY_6000_3_OR_NEWER
                Assert.That(reopened.AnswersPings, Is.True,
                    "a closed first window must not block the reopened window's pings");
#endif
            }
            finally
            {
                if (first != null)
                {
                    UnityEngine.Object.DestroyImmediate(first);
                }
                UnityEngine.Object.DestroyImmediate(reopened);
                SetOpenWindows(previousWindows);
            }
        }

        [Test]
        public void DisableAndReenableWindow_KeepsSingleLiveRegistration()
        {
            var previousWindows = SnapshotOpenWindows();
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;

            try
            {
                SetOpenWindows(null);
                InvokeOn(window, "RegisterOpenWindow");

                InvokeOn(window, "OnDisable");
                InvokeOn(window, "OnDisable");
                Assert.That(ZepWindow.OpenWindows, Is.Empty);
                Assert.That(ZepWindow.HasOpenWindow, Is.False);

                // CreateGUI re-registers after either a rebuild or a reload.
                InvokeOn(window, "RegisterOpenWindow");
                InvokeOn(window, "RegisterOpenWindow");
                Assert.That(ZepWindow.OpenWindows, Is.EqualTo(new[] { window }));
                Assert.That(ZepWindow.OpenWindowCount, Is.EqualTo(1));
#if UNITY_6000_3_OR_NEWER
                Assert.That(window.AnswersPings, Is.True);
#endif

                InvokeOn(window, "OnDisable");
                InvokeOn(window, "OnDestroy");
                InvokeOn(window, "OnDestroy");
                Assert.That(ZepWindow.OpenWindows, Is.Empty);
                Assert.That(ZepWindow.HasOpenWindow, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                SetOpenWindows(previousWindows);
            }
        }

        // §5.28. Unity can ping the same just-created asset more than once while
        // it imports; each unlatched chain ends in a full core.Refresh().
        [Test]
        public void CreatedAssetPing_RunsOneRetryChainPerPath()
        {
            const string first = "Assets/ZEP_PingLatchProbeA.mat";
            const string second = "Assets/ZEP_PingLatchProbeB.mat";
            var pendingField = ReceiverField("_pendingRevealPath");
            var previousPending = pendingField.GetValue(null);
            var previousWindows = SnapshotOpenWindows();
            var before = ZepPingReceiver.CreatedAssetRevealChainsForTests;

            try
            {
                // No panel: the queued attempts exit at the first guard rather
                // than reaching the shared controller host.
                SetOpenWindows(null);
                pendingField.SetValue(null, null);

                InvokeAcknowledge(first);
                InvokeAcknowledge(first);

                Assert.That(
                    ZepPingReceiver.CreatedAssetRevealChainsForTests - before,
                    Is.EqualTo(1),
                    "a second ping for the same importing asset must join the chain in flight");
                Assert.That(pendingField.GetValue(null), Is.EqualTo(first));

                InvokeAcknowledge(second);

                Assert.That(
                    ZepPingReceiver.CreatedAssetRevealChainsForTests - before,
                    Is.EqualTo(2),
                    "a different asset must still get its own chain");
            }
            finally
            {
                pendingField.SetValue(null, previousPending);
                SetOpenWindows(previousWindows);
            }
        }

        // §5.28, the other half: a chain that ends without revealing must release
        // the path, or every later ping for that asset is suppressed. The exit
        // taken here is the no-panel one; all four exits share the one finally.
        // The attempt body is run directly because EditorApplication.delayCall
        // does not fire while a batchmode test is executing.
        [Test]
        public void CreatedAssetPing_ReleasesThePathWhenTheChainEnds()
        {
            const string path = "Assets/ZEP_PingLatchProbeC.mat";
            var pendingField = ReceiverField("_pendingRevealPath");
            var previousPending = pendingField.GetValue(null);
            var previousWindows = SnapshotOpenWindows();
            var before = ZepPingReceiver.CreatedAssetRevealChainsForTests;

            try
            {
                SetOpenWindows(null);
                pendingField.SetValue(null, null);

                InvokeAcknowledge(path);
                Assert.That(pendingField.GetValue(null), Is.EqualTo(path));

                Invoke("RunCreatedAssetRevealAttempt", path, false, 0L, 3,
                    ReceiverField("_createdAssetRevealGeneration").GetValue(null));

                Assert.That(
                    pendingField.GetValue(null),
                    Is.Null,
                    "the chain must release the path when it ends");

                InvokeAcknowledge(path);
                Assert.That(
                    ZepPingReceiver.CreatedAssetRevealChainsForTests - before,
                    Is.EqualTo(2),
                    "a ping after the chain ended must start a fresh one");
            }
            finally
            {
                pendingField.SetValue(null, previousPending);
                SetOpenWindows(previousWindows);
            }
        }

        private static void InvokeAcknowledge(string path)
            => Invoke("AcknowledgeAndRevealCreatedAsset", path, false, 0L, 3);

        [Test]
        public void PendingRevealSnapshot_WhenCoreBecameReady_ResumesWithoutDroppingQueuedCallbacks()
        {
            var source = new RigRecordSource(Array.Empty<AssetRecord>(), new[] { "Assets" });
            var index = new InMemoryAssetIndex();
            var definitions = new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { });
            var gateway = new RigAssetGateway(source) { TagDefinitions = definitions };
            var scheduler = new RigScheduler();
            using var core = new ZepController(
                new ZepState(), gateway, new ProjectFileCommandService(gateway),
                source, index, new AssetSearchService(index), definitions,
                new RigSynchronizer(source, index), scheduler, new RigShellReveal(),
                new RigAddressablesQuery());
            Assert.That(core.IsPaneSourceReconciliationReady, Is.False);
            var resumed = 0;
            var queuedCalls = 0;
            var retryCalls = 0;
            Action handler = null;
            handler = () =>
            {
                core.PaneSourceReconciliationReady -= handler;
                resumed++;
                EditorApplication.delayCall += () => retryCalls++;
            };
            core.PaneSourceReconciliationReady += handler;
            ReceiverField("_pendingRevealReadyCore").SetValue(null, core);
            ReceiverField("_pendingRevealReadyHandler").SetValue(null, handler);
            EditorApplication.delayCall += () => queuedCalls++;

            using (var snapshot = new PingReceiverStateSnapshot())
            {
                core.Refresh();
                scheduler.RunAll();
                Assert.That(core.IsPaneSourceReconciliationReady, Is.True);
                Assert.That(resumed, Is.Zero, "the user's handler is suspended while the test owns the state");
            }

            Assert.That(resumed, Is.EqualTo(1), "readiness already fired, so restore must resume immediately");
            var delayed = EditorApplication.delayCall;
            EditorApplication.delayCall = null;
            delayed();
            Assert.That(queuedCalls, Is.EqualTo(1), "keep callbacks queued before the snapshot");
            Assert.That(retryCalls, Is.EqualTo(1), "keep the retry queued by readiness restoration");
        }

        [Test]
        public void PendingRevealSnapshot_RestoresIdentityAndKeepsOriginalAttemptLive()
        {
            var previousWindows = SnapshotOpenWindows();
            try
            {
                SetOpenWindows(null);
                const string path = "Assets/ZEP_PingPreserved.asset";
                Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 101L, 3);
                var originalAttempt = EditorApplication.delayCall;
                var chains = ZepPingReceiver.CreatedAssetRevealChainsForTests;
                using (var snapshot = new PingReceiverStateSnapshot())
                {
                    Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 202L, 3);
                }

                Assert.That(EditorApplication.delayCall, Is.EqualTo(originalAttempt));
                Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 101L, 3);
                Assert.That(ZepPingReceiver.CreatedAssetRevealChainsForTests,
                    Is.EqualTo(chains), "the restored identical target must still coalesce");
                EditorApplication.delayCall();
                Assert.That(ReceiverField("_pendingRevealPath").GetValue(null), Is.Null,
                    "the original queued attempt must still own its restored generation");
            }
            finally
            {
                SetOpenWindows(previousWindows);
            }
        }

        [Test]
        public void CreatedAssetPing_ClosingLastPanel_CancelsPendingChain()
        {
            var pendingField = ReceiverField("_pendingRevealPath");
            var previousPending = pendingField.GetValue(null);
            var previousWindows = SnapshotOpenWindows();
            var previousDelayCall = EditorApplication.delayCall;
            var window = ScriptableObject.CreateInstance<ZepWindow>();
            window.hideFlags = HideFlags.HideAndDontSave;
            try
            {
                SetOpenWindows(null);
                InvokeOn(window, "RegisterOpenWindow");
                pendingField.SetValue(null, null);
                EditorApplication.delayCall = null;
                InvokeAcknowledge("Assets/ZEP_PingClosing.mat");
                var staleAttempt = EditorApplication.delayCall;

                InvokeOn(window, "OnDisable");

                Assert.That(pendingField.GetValue(null), Is.Null,
                    "closing the last panel must release its pending reveal immediately");
                staleAttempt();
                Assert.That(pendingField.GetValue(null), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
                EditorApplication.delayCall = previousDelayCall;
                pendingField.SetValue(null, previousPending);
                SetOpenWindows(previousWindows);
            }
        }

        [Test]
        public void CreatedAssetPing_OldAttemptAfterReturningToSamePath_KeepsNewestChain()
        {
            const string first = "Assets/ZEP_PingGenerationA.mat";
            const string second = "Assets/ZEP_PingGenerationB.mat";
            var pendingField = ReceiverField("_pendingRevealPath");
            var previousPending = pendingField.GetValue(null);
            var previousWindows = SnapshotOpenWindows();
            var previousDelayCall = EditorApplication.delayCall;
            try
            {
                SetOpenWindows(null);
                pendingField.SetValue(null, null);
                EditorApplication.delayCall = null;
                InvokeAcknowledge(first);
                var staleAttempt = EditorApplication.delayCall;
                Assert.That(staleAttempt, Is.Not.Null);
                EditorApplication.delayCall = null;
                InvokeAcknowledge(second);
                InvokeAcknowledge(first);

                staleAttempt();

                Assert.That(pendingField.GetValue(null), Is.EqualTo(first),
                    "a cancelled attempt must not release a newer chain for the same path");
            }
            finally
            {
                EditorApplication.delayCall = previousDelayCall;
                pendingField.SetValue(null, previousPending);
                SetOpenWindows(previousWindows);
            }
        }

        [Test]
        public void CreatedAssetPing_SubAssetsInSameContainer_CoalescesOnlyIdenticalTarget()
        {
            const string path = "Assets/ZEP_PingSubAssetGeneration.asset";
            var pendingField = ReceiverField("_pendingRevealPath");
            var previousPending = pendingField.GetValue(null);
            var previousWindows = SnapshotOpenWindows();
            var previousDelayCall = EditorApplication.delayCall;
            var before = ZepPingReceiver.CreatedAssetRevealChainsForTests;
            try
            {
                SetOpenWindows(null);
                pendingField.SetValue(null, null);
                EditorApplication.delayCall = null;
                Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 101L, 3);
                var staleAttempt = EditorApplication.delayCall;
                EditorApplication.delayCall = null;
                Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 202L, 3);
                Invoke("AcknowledgeAndRevealCreatedAsset", path, true, 202L, 3);

                Assert.That(ZepPingReceiver.CreatedAssetRevealChainsForTests - before,
                    Is.EqualTo(2), "different subassets need separate targets; repeated identical pings join");

                staleAttempt();

                Assert.That(pendingField.GetValue(null), Is.EqualTo(path),
                    "the old subasset attempt must not release the newer target's chain");
            }
            finally
            {
                EditorApplication.delayCall = previousDelayCall;
                pendingField.SetValue(null, previousPending);
                SetOpenWindows(previousWindows);
            }
        }

        // The emit/reflection helpers are private statics on the receiver. They
        // are called through reflection so this stays a test-only change.
        private static object Invoke(string methodName, params object[] arguments)
        {
            var method = typeof(ZepPingReceiver).GetMethod(
                methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, methodName);
            return method.Invoke(null, arguments);
        }

        private static FieldInfo ReceiverField(string fieldName)
        {
            var field = typeof(ZepPingReceiver).GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, fieldName);
            return field;
        }

        // The window's own lifecycle hooks, invoked the way ThemeSystemTests
        // drives OnDisable.
        private static void InvokeOn(ZepWindow window, string methodName)
        {
            var method = typeof(ZepWindow).GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, methodName);
            method.Invoke(window, null);
        }

        private static List<ZepWindow> OpenWindowsList()
        {
            var field = typeof(ZepWindow).GetField(
                "_openWindows", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, "_openWindows");
            return (List<ZepWindow>)field.GetValue(null);
        }

        private static List<ZepWindow> SnapshotOpenWindows()
        {
            return new List<ZepWindow>(OpenWindowsList());
        }

        // Null empties the registry; a snapshot restores it.
        private static void SetOpenWindows(List<ZepWindow> windows)
        {
            var list = OpenWindowsList();
            list.Clear();
            if (windows != null)
            {
                list.AddRange(windows);
            }
        }
    }

    // These fixtures temporarily own process-wide ping state. Restore the
    // request and the event/queue registrations together so an interactive
    // user's pending attempt remains a live request after the test finishes.
    internal sealed class PingReceiverStateSnapshot : IDisposable
    {
        private static readonly FieldInfo[] Fields =
        {
            Field("_pendingRevealPath"),
            Field("_pendingRevealHasSubAssetTarget"),
            Field("_pendingRevealSubAssetLocalId"),
            Field("_createdAssetRevealGeneration"),
            Field("_pendingRevealReadyCore"),
            Field("_pendingRevealReadyHandler"),
            Field("CreatedAssetRevealChainsForTests")
        };

        private readonly object[] _values = Fields.Select(field => field.GetValue(null)).ToArray();
        private readonly EditorApplication.CallbackFunction _delayCall = EditorApplication.delayCall;
        private bool _disposed;

        internal PingReceiverStateSnapshot()
        {
            CancelPendingReveal();
            EditorApplication.delayCall = null;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CancelPendingReveal();
            for (var i = 0; i < Fields.Length; i++)
            {
                Fields[i].SetValue(null, _values[i]);
            }

            var core = _values[4] as ZepController;
            var handler = _values[5] as Action;
            EditorApplication.delayCall = _delayCall;
            if (core != null && handler != null)
            {
                if (core.IsPaneSourceReconciliationReady)
                {
                    handler();
                }
                else
                {
                    core.PaneSourceReconciliationReady += handler;
                }
            }
        }

        private static FieldInfo Field(string name)
        {
            var field = typeof(ZepPingReceiver).GetField(
                name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.That(field, Is.Not.Null, name);
            return field;
        }

        private static void CancelPendingReveal()
        {
            typeof(ZepPingReceiver).GetMethod(
                "CancelCreatedAssetReveal", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, null);
        }
    }
}
