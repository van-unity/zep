using System;
using System.Reflection;
using Zep.Core.Application;
using Zep.Editor.Bridge;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // Bridges Unity's global object "ping" into Zep's
    // reveal-and-frame flow. A ping is what fires when you click an asset
    // reference in the Inspector (a Sprite on a SpriteRenderer, a material, a
    // mesh, ...), press the locator on an ObjectField, or call
    // EditorGUIUtility.PingObject. Unity's built-in Project Browser flashes the
    // asset; this makes our panel navigate to and frame it too.
    //
    // WHY TWO RECEIVERS
    // Unity 6.3+ dispatches a ping only to EditorWindows implementing the
    // *internal* UnityEditor.IFramableContainer interface. There is no managed
    // ping event and no overridable frame hook on EditorWindow. The panel window
    // implements that interface through a bridge assembly Unity grants its
    // internals to (PingFramableEditorWindow) and forwards into HandleFrame, so
    // on 6.3+ there is no receiver object of our own at all.
    //
    // Unity 6.0 through 6.2 has no IFramableContainer. PingObject instead
    // loops the editor's private list of concrete ProjectBrowser instances and
    // calls their non-virtual FrameObject(int, true). On those versions we create
    // one unshown built-in ProjectBrowser, initialize it, and observe its private
    // last-framed id on editor updates. It remains a normal member of Unity's
    // ProjectBrowser list, so the legacy dispatch reaches it without patching
    // Unity or bringing in a third-party detour library. Being unshown, it has
    // no host view, and Unity's maximize logs an error for every such window;
    // LegacyReceiverMaximizeErrorFilter drops that one error for this window.
    //
    // ROBUSTNESS
    // Every step is guarded. If the legacy receiver cannot be created, pinging
    // simply does nothing in the panel — exactly the behavior before this feature.
    //
    // LIFETIME
    // Nothing here runs at domain load. A ping is only ever consumed by an open
    // panel (HandleFrame drops it otherwise), so ZepWindow arms the
    // legacy receiver when the first panel opens and stops its poll when the
    // last one closes.
    internal static class ZepPingReceiver
    {
        private const int CreatedAssetRevealAttempts = 3;
#if !UNITY_6000_3_OR_NEWER
        // Both the object name (to find a stale instance after a domain reload)
        // and the window title (the only thing Unity's maximize error names
        // besides the type, so the filter can tell this ProjectBrowser from a
        // real one).
        internal const string LegacyReceiverName = "Zep.PingReceiver";
        private const int NoLegacyFrame = 0;

        // The single hidden receiver window. Held in a static field so the native
        // object is not collected. HideAndDontSave keeps it out of the layout;
        // any stale named instance is removed before creating its replacement
        // after a domain reload.
        private static EditorWindow _receiver;
        private static FieldInfo _legacyLastFramedId;

        // Set by the one attempt to BUILD a receiver. It does not gate the legacy
        // poll subscription, which follows the open-window count and so must be
        // able to come back after the last panel closed and one is opened again.
        private static bool _attempted;
#endif

        // The created-asset path whose retry chain is running, so a second ping
        // for the same not-yet-indexed asset joins the chain in flight instead of
        // starting a second one.
        private static string _pendingRevealPath;
        private static bool _pendingRevealHasSubAssetTarget;
        private static long _pendingRevealSubAssetLocalId;
        private static int _createdAssetRevealGeneration;
        private static ZepController _pendingRevealReadyCore;
        private static Action _pendingRevealReadyHandler;

        // Chains started, for tests (never read by the receiver itself).
        internal static int CreatedAssetRevealChainsForTests;

#if !UNITY_6000_3_OR_NEWER
        // The first panel of the session is open. The hidden ProjectBrowser has
        // to be built from ZepWindow.OnGUI (IMGUI and EditorStyles must
        // be valid), so here we only restart its poll for a receiver that
        // already exists.
        internal static void OnFirstPanelWindowOpened()
        {
            StartLegacyPoll();
        }
#endif

        // The last panel closed: nothing can consume a ping until one opens
        // again. Release pending readiness subscriptions as well as the legacy
        // per-frame poll so a closed panel cannot retain or resume a reveal.
        internal static void OnLastPanelWindowClosed()
        {
            CancelCreatedAssetReveal();
#if !UNITY_6000_3_OR_NEWER
            EditorApplication.update -= PollLegacyProjectBrowser;
#endif
        }

        // Every FrameObject call Unity routes to a receiver — i.e. every ping:
        // from the panel window's IFramableContainer implementation on 6.3+, from
        // the legacy ProjectBrowser poll before that.
#if UNITY_6000_3_OR_NEWER
        internal static void HandleFrame(EntityId instanceId, bool ping)
#else
        private static void HandleFrame(int instanceId, bool ping)
#endif
        {
            // FrameObject is also the "frame without ping" path used elsewhere in
            // the editor. Only a real ping should reach into the panel.
            if (!ping)
            {
                return;
            }

            try
            {
                // Nothing to reveal into if the panel isn't open. This also keeps
                // a ping from spinning up the controller host for a user who
                // never opened the panel.
                if (!HasOpenPanelWindow())
                {
                    return;
                }

                // InstanceIDToObject(int) is obsolete from Unity 6.3; EntityIdToObject is
                // the replacement. Both EntityIdToObject and the EntityId type are absent
                // before 6.3 (the package minimum is 6.0), so the call must be
                // version-gated rather than referenced unconditionally.
#if UNITY_6000_3_OR_NEWER
                var pinged = EditorUtility.EntityIdToObject(instanceId);
#else
                var pinged = EditorUtility.InstanceIDToObject(instanceId);
#endif
                if (pinged == null)
                {
                    return;
                }

                var path = AssetDatabase.GetAssetPath(pinged);
                if (string.IsNullOrEmpty(path))
                {
                    // A scene object or other non-asset — no project row to frame.
                    return;
                }

                // A pinged SUB-asset (e.g. clicking the material reference
                // embedded in a TextMeshPro font asset) resolves to its
                // CONTAINER's path — GetAssetPath knows no finer identity — so
                // a plain reveal would frame the parent row. Carry the
                // sub-asset's own identity alongside the path whenever the
                // panel can show a row for it.
                var hasSubAssetTarget = TryGetSubAssetTarget(pinged, path, out var subAssetLocalId);

                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log(hasSubAssetTarget
                        ? $"[Zep/ping] reveal instanceId={instanceId} path={path} subAssetLocalId={subAssetLocalId}"
                        : $"[Zep/ping] reveal instanceId={instanceId} path={path}");
                }

                var generation = _createdAssetRevealGeneration;
                var revealed = TryReveal(path, hasSubAssetTarget, subAssetLocalId);
                if (generation != _createdAssetRevealGeneration)
                {
                    return;
                }

                if (revealed)
                {
                    CancelCreatedAssetReveal();
                    // Existing asset (normal reference ping) — revealed in place.
                    // Leave focus where the user was working (usually the Inspector).
                    if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/ping] revealed in place path={path}");
                    return;
                }
                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/ping] reveal missed path={path} chainHeldFor={_pendingRevealPath ?? "null"}");

                // The asset isn't in our index yet: Unity pings an asset it just
                // CREATED — e.g. the Inspector's "Create Material Preset" — while
                // it's still importing (Unity re-imports it a second time after the
                // ping). Acknowledge the concrete path once the import settles,
                // reveal it for real, and
                // bring our panel forward so Unity's own Project window (which its
                // ping brings to front) doesn't stay over ours.
                AcknowledgeAndRevealCreatedAsset(
                    path,
                    hasSubAssetTarget,
                    subAssetLocalId,
                    CreatedAssetRevealAttempts);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Zep] Ping reveal failed ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // The pinged object's identity as a sub-asset ROW target: true when it
        // is a visible representation of its container — the same list
        // SubAssetCatalog.ResolvePath builds rows from — with localId derived
        // the same way (stable local file id, else the session instance id) so
        // the computed key always matches the row's descriptor key. Hidden
        // sub-objects (no row to frame) report false and fall back to the
        // parent reveal.
        internal static bool TryGetSubAssetTarget(
            UnityEngine.Object pinged, string path, out long localId)
        {
            localId = 0L;
            if (!AssetDatabase.IsSubAsset(pinged))
            {
                return false;
            }

            var representations = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            if (representations == null)
            {
                return false;
            }

            var isRepresentation = false;
            for (var i = 0; i < representations.Length; i++)
            {
                if (representations[i] == pinged)
                {
                    isRepresentation = true;
                    break;
                }
            }

            if (!isRepresentation)
            {
                return false;
            }

            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(pinged, out _, out localId))
            {
#if UNITY_6000_5_OR_NEWER
                localId = (long)EntityId.ToULong(pinged.GetEntityId());
#else
                localId = pinged.GetInstanceID();
#endif
            }

            return true;
        }

        // Reveal for both ping flavors. A sub-asset target routes through the
        // shell, which owns the sub-row frame flow (Core deals only in paths);
        // everything else — including a sub-asset ping arriving before the
        // window's UI is built — is the plain Core reveal of the path.
        private static bool TryReveal(string path, bool hasSubAssetTarget, long subAssetLocalId)
        {
            if (hasSubAssetTarget)
            {
                var shell = FindOpenShell();
                if (shell != null)
                {
                    return shell.RevealAndFrameSubAsset(path, subAssetLocalId);
                }
            }

            return ZepControllerHost.Shared.Core.RevealAndFrameAsset(path);
        }

        // One retry chain per created target. Unity can ping the same just-created
        // asset more than once while it imports; without this latch each ping
        // would run its own three-attempt chain.
        private static void AcknowledgeAndRevealCreatedAsset(
            string path,
            bool hasSubAssetTarget,
            long subAssetLocalId,
            int attemptsRemaining)
        {
            if (string.Equals(_pendingRevealPath, path, StringComparison.Ordinal)
                && _pendingRevealHasSubAssetTarget == hasSubAssetTarget
                && (!hasSubAssetTarget || _pendingRevealSubAssetLocalId == subAssetLocalId))
            {
                return;
            }

            CancelCreatedAssetReveal();
            _pendingRevealPath = path;
            _pendingRevealHasSubAssetTarget = hasSubAssetTarget;
            _pendingRevealSubAssetLocalId = subAssetLocalId;
            CreatedAssetRevealChainsForTests++;
            ScheduleCreatedAssetRevealAttempt(
                path, hasSubAssetTarget, subAssetLocalId, attemptsRemaining,
                _createdAssetRevealGeneration);
        }

        private static void CancelCreatedAssetReveal()
        {
            _createdAssetRevealGeneration++;
            _pendingRevealPath = null;
            if (_pendingRevealReadyCore != null && _pendingRevealReadyHandler != null)
            {
                _pendingRevealReadyCore.PaneSourceReconciliationReady -= _pendingRevealReadyHandler;
            }

            _pendingRevealReadyCore = null;
            _pendingRevealReadyHandler = null;
        }

        // Deferred to a later tick so the just-created asset finishes importing
        // before the path-local acknowledgement and reveal.
        private static void ScheduleCreatedAssetRevealAttempt(
            string path,
            bool hasSubAssetTarget,
            long subAssetLocalId,
            int attemptsRemaining,
            int generation)
        {
            EditorApplication.delayCall += () => RunCreatedAssetRevealAttempt(
                path, hasSubAssetTarget, subAssetLocalId, attemptsRemaining, generation);
        }

        private static void RunCreatedAssetRevealAttempt(
            string path,
            bool hasSubAssetTarget,
            long subAssetLocalId,
            int attemptsRemaining,
            int generation)
        {
            if (generation != _createdAssetRevealGeneration)
            {
                return;
            }

            var chainContinues = false;
            try
            {
                if (!HasOpenPanelWindow())
                {
                    return;
                }

                var core = ZepControllerHost.Shared.Core;
                var acknowledged = core.AcknowledgeAssetPath(
                    Zep.Core.Domain.AssetPath.From(path));
                if (generation != _createdAssetRevealGeneration)
                {
                    return;
                }

                var revealed = acknowledged && TryReveal(path, hasSubAssetTarget, subAssetLocalId);
                if (generation != _createdAssetRevealGeneration)
                {
                    return;
                }

                if (ZepPerfLog.IsEnabled) ZepPerfLog.Log($"[Zep/ping] created-asset attempt path={path} acknowledged={acknowledged} revealed={revealed} ready={core.IsPaneSourceReconciliationReady} attemptsRemaining={attemptsRemaining}");
                if (revealed)
                {
                    EditorWindow.FocusWindowIfItsOpen(typeof(ZepWindow));
                    return;
                }

                // The panel is still reconciling its index — a domain reload's
                // hydration, or the retype job after a compile. The asset is not
                // missing; it is not projected yet. Wait for readiness and try
                // again without spending an attempt: three editor frames are
                // nothing next to a real project's hydration, and a chain that
                // ran out during it used to end in the rebuild below.
                if (!core.IsPaneSourceReconciliationReady)
                {
                    chainContinues = true;
                    void RetryWhenReady()
                    {
                        core.PaneSourceReconciliationReady -= RetryWhenReady;
                        if (generation != _createdAssetRevealGeneration)
                        {
                            return;
                        }

                        _pendingRevealReadyCore = null;
                        _pendingRevealReadyHandler = null;
                        ScheduleCreatedAssetRevealAttempt(
                            path,
                            hasSubAssetTarget,
                            subAssetLocalId,
                            attemptsRemaining,
                            generation);
                    }

                    _pendingRevealReadyCore = core;
                    _pendingRevealReadyHandler = RetryWhenReady;
                    core.PaneSourceReconciliationReady += RetryWhenReady;
                    return;
                }

                if (attemptsRemaining > 1)
                {
                    chainContinues = true;
                    ScheduleCreatedAssetRevealAttempt(
                        path,
                        hasSubAssetTarget,
                        subAssetLocalId,
                        attemptsRemaining - 1,
                        generation);
                    return;
                }

                // Exhausted with the index ready: the importer has not exposed a
                // canonical record for the path yet. This used to force an
                // authoritative rebuild — a full walk on the main thread, and when
                // that walk's save lost a race with a staged save, a distrusted
                // cache and the next reload's overlay — to frame one asset that
                // reaches the panel through its own import batch anyway. Give the
                // panel focus and let that batch do it.
                if (ZepPerfLog.IsEnabled)
                {
                    ZepPerfLog.Log($"[Zep/ping] created-asset reveal gave up path={path}");
                }

                EditorWindow.FocusWindowIfItsOpen(typeof(ZepWindow));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Zep] Ping reveal retry failed ({ex.GetType().Name}: {ex.Message}).");
            }
            finally
            {
                // Only the current request may release its path and readiness
                // subscription; a newer ping can return to the same path.
                if (!chainContinues && generation == _createdAssetRevealGeneration)
                {
                    CancelCreatedAssetReveal();
                }
            }
        }

        // A panel must be open for a ping to have anywhere to go. The window
        // keeps the count, so this costs nothing per ping.
        private static bool HasOpenPanelWindow() => ZepWindow.HasOpenWindow;

        // The open panel window's shell, or null while no window exists or its
        // UI is not yet built (CreateGUI pending). Only a sub-asset ping needs
        // the shell object itself; the count settles the common no-panel case
        // without loading every window in the editor.
        private static ZepShellView FindOpenShell()
        {
            if (!ZepWindow.HasOpenWindow)
            {
                return null;
            }

            var windows = ZepWindow.OpenWindows;
            for (var i = 0; i < windows.Count; i++)
            {
                var shell = windows[i] != null
                    ? windows[i].rootVisualElement?.Q<ZepShellView>()
                    : null;
                if (shell != null)
                {
                    return shell;
                }
            }

            return null;
        }

#if !UNITY_6000_3_OR_NEWER
        // Builds the receiver once per domain. Called from
        // ZepWindow.OnGUI so the hidden ProjectBrowser initializes while
        // IMGUI and EditorStyles are valid.
        internal static void EnsureReceiver()
        {
            if (_attempted)
            {
                return;
            }
            _attempted = true;

            try
            {
                if (TryCreateLegacyProjectBrowserReceiver())
                {
                    if (ZepPerfLog.IsEnabled)
                    {
                        ZepPerfLog.Log(
                            "[Zep/ping] legacy ProjectBrowser receiver active");
                    }

                    return;
                }

                Debug.LogWarning(
                    "[Zep] Inspector-ping integration unavailable (Unity's ProjectBrowser internals were not found). "
                    + "Pinging an asset from elsewhere won't reveal it in the panel; every other feature is unaffected.");
            }
            catch (Exception ex)
            {
                var reason = ex is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : ex;
                Debug.LogWarning(
                    $"[Zep] Inspector-ping integration unavailable ({reason.GetType().Name}: {reason.Message}). "
                    + "Pinging an asset from elsewhere won't reveal it in the panel; every other feature is unaffected.");
            }
        }

        private static bool TryCreateLegacyProjectBrowserReceiver()
        {
            var projectBrowserType = Type.GetType("UnityEditor.ProjectBrowser, UnityEditor");
            var initMethod = projectBrowserType?.GetMethod(
                "Init", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            var lastFramedId = projectBrowserType?.GetField(
                "m_LastFramedID", BindingFlags.NonPublic | BindingFlags.Instance);
            var lastInteracted = projectBrowserType?.GetField(
                "s_LastInteractedProjectBrowser",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            if (projectBrowserType == null
                || initMethod == null
                || lastFramedId == null
                || lastInteracted == null)
            {
                return false;
            }

            foreach (var candidate in Resources.FindObjectsOfTypeAll(projectBrowserType))
            {
                if (candidate != null && candidate.name == LegacyReceiverName)
                {
                    UnityEngine.Object.DestroyImmediate(candidate);
                }
            }

            var previousLastInteracted = lastInteracted.GetValue(null);
            if (previousLastInteracted is UnityEngine.Object previousWindow && previousWindow == null)
            {
                previousLastInteracted = null;
            }

            EditorWindow receiver = null;
            var created = false;
            try
            {
                receiver = (EditorWindow)ScriptableObject.CreateInstance(projectBrowserType);
                receiver.name = LegacyReceiverName;
                receiver.hideFlags = HideFlags.HideAndDontSave;
                receiver.position = new Rect(0f, 0f, 320f, 320f);
                initMethod.Invoke(receiver, null);

                // After Init: ProjectBrowser.OnEnable sets the "Project" title,
                // and the maximize-error filter recognizes this window by title.
                receiver.titleContent = new GUIContent(LegacyReceiverName);
                InstallLegacyMaximizeErrorFilter();

                // Init may frame the current selection while building the hidden
                // browser. Clear that value so only subsequent PingObject dispatches
                // are forwarded. Resetting after every observation also makes two
                // clicks on the same reference observable as two pings.
                lastFramedId.SetValue(receiver, NoLegacyFrame);

                _legacyLastFramedId = lastFramedId;
                _receiver = receiver;
                StartLegacyPoll();
                created = true;
                return true;
            }
            finally
            {
                // ProjectBrowser.OnEnable makes every new instance the globally
                // last-interacted browser. Restore Unity's prior value so its create
                // and frame commands continue targeting the user's real window.
                lastInteracted.SetValue(null, previousLastInteracted);

                if (!created && receiver != null)
                {
                    UnityEngine.Object.DestroyImmediate(receiver);
                }
            }
        }

        // Subscribes the per-frame poll for the two orders the two owners can
        // arrive in: the receiver is built while a panel is already open (from
        // OnGUI), or a panel opens for a receiver built earlier in the session.
        // Idempotent, so neither order double-subscribes.
        private static void StartLegacyPoll()
        {
            if (_receiver == null
                || _legacyLastFramedId == null
                || !ZepWindow.HasOpenWindow)
            {
                return;
            }

            EditorApplication.update -= PollLegacyProjectBrowser;
            EditorApplication.update += PollLegacyProjectBrowser;
        }

        private static void PollLegacyProjectBrowser()
        {
            if (_receiver == null || _legacyLastFramedId == null)
            {
                EditorApplication.update -= PollLegacyProjectBrowser;
                return;
            }

            try
            {
                var instanceId = (int)_legacyLastFramedId.GetValue(_receiver);
                if (instanceId == NoLegacyFrame)
                {
                    return;
                }

                _legacyLastFramedId.SetValue(_receiver, NoLegacyFrame);
                HandleFrame(instanceId, true);
            }
            catch (Exception ex)
            {
                EditorApplication.update -= PollLegacyProjectBrowser;
                _legacyLastFramedId = null;

                var failedReceiver = _receiver;
                _receiver = null;
                if (failedReceiver != null)
                {
                    UnityEngine.Object.DestroyImmediate(failedReceiver);
                }

                Debug.LogWarning(
                    $"[Zep] Legacy Inspector-ping integration stopped ({ex.GetType().Name}: {ex.Message}).");
            }
        }

        // Unity's maximize (WindowLayout.MaximizePresent → CheckWindowConsistency)
        // logs this error for every EditorWindow without a host view. The legacy
        // receiver is one by design, so it would report on every maximize. The
        // filter drops only that error for only this window (by its unique
        // title), so a genuinely broken Project window still reports. If another
        // tool later replaces the log handler, the error simply comes back.
        internal static void InstallLegacyMaximizeErrorFilter()
        {
            var logger = Debug.unityLogger;
            if (logger.logHandler is LegacyReceiverMaximizeErrorFilter)
            {
                return;
            }

            logger.logHandler = new LegacyReceiverMaximizeErrorFilter(logger.logHandler);
        }

        private sealed class LegacyReceiverMaximizeErrorFilter : ILogHandler
        {
            // Verbatim from WindowLayout.CheckWindowConsistency (6000.0–6000.5).
            private const string UnparentedWindowFormat = "Invalid editor window of type: {0}, title: {1}";

            private readonly ILogHandler _inner;

            internal LegacyReceiverMaximizeErrorFilter(ILogHandler inner)
            {
                _inner = inner;
            }

            public void LogFormat(
                LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                if (logType == LogType.Error
                    && string.Equals(format, UnparentedWindowFormat, StringComparison.Ordinal)
                    && args != null
                    && args.Length == 2
                    && string.Equals(args[1] as string, LegacyReceiverName, StringComparison.Ordinal))
                {
                    return;
                }

                _inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                _inner.LogException(exception, context);
            }
        }
#endif
    }
}
