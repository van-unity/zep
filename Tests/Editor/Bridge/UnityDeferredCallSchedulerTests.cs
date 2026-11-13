using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Zep.Core.Application;
using Zep.Editor.Bridge;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zep.Editor.Tests.Bridge
{
    // These tests drive the real EditorApplication.delayCall field through the
    // exact dispatch Unity performs (snapshot the multicast, null the field,
    // invoke the snapshot). They do NOT wait for the editor to dispatch it:
    // batchmode flushes delay functions in unpredictable bursts, and every
    // behaviour under test — throw isolation, cancel-mid-flush, "next tick, not
    // this drain" — is a property of that dispatch shape rather than of when the
    // editor chooses to run it. Reproducing the dispatch keeps each case
    // deterministic and keeps one test's queue out of the next test's run.
    public sealed class UnityDeferredCallSchedulerTests
    {
        private EditorApplication.CallbackFunction _previousDelayCall;

        private EditorApplication.CallbackFunction _previousUpdate;

        [SetUp]
        public void SetUp()
        {
            _previousDelayCall = EditorApplication.delayCall;
            _previousUpdate = EditorApplication.update;
            EditorApplication.delayCall = null;
            EditorApplication.update = null;
        }

        [TearDown]
        public void TearDown()
        {
            EditorApplication.delayCall = _previousDelayCall;
            EditorApplication.update = _previousUpdate;
        }

        [Test]
        public void ThrowingAction_DoesNotPreventLaterScheduledActionsFromRunning()
        {
            LogAssert.Expect(LogType.Error, new Regex("A deferred callback"));
            var scheduler = new UnityDeferredCallScheduler();
            var ranAfterTheThrow = false;
            Action thrower = () => throw new InvalidOperationException("deferred boom");
            Action later = () => ranAfterTheThrow = true;

            scheduler.Schedule(thrower);
            scheduler.Schedule(later);
            DispatchDelayCall();

            Assert.That(ranAfterTheThrow, Is.True, "the action scheduled behind a thrower must still run");
        }

        [Test]
        public void AfterAThrow_SchedulingTheSameActionAgainRunsItAgain()
        {
            LogAssert.Expect(LogType.Error, new Regex("A deferred callback"));
            LogAssert.Expect(LogType.Error, new Regex("A deferred callback"));
            var scheduler = new UnityDeferredCallScheduler();
            var runs = 0;
            Action thrower = () =>
            {
                runs++;
                throw new InvalidOperationException("deferred boom");
            };

            scheduler.Schedule(thrower);
            DispatchDelayCall();
            Assert.That(runs, Is.EqualTo(1), "the first dispatch must run the action");

            scheduler.Schedule(thrower);
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(2), "a throw must not strand the action's bookkeeping");
        }

        [Test]
        public void CancelThenReschedule_RunsExactlyOnce()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var runs = 0;
            Action action = () => runs++;

            scheduler.Schedule(action);
            scheduler.Cancel(action);
            scheduler.Schedule(action);
            DispatchDelayCall();
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(1));
        }

        [Test]
        public void CancelFromInsideADeferredCallback_PreventsTheEntryInTheSameFlush()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var victimRuns = 0;
            var cancellerRan = false;
            Action victim = () => victimRuns++;
            Action canceller = () =>
            {
                cancellerRan = true;
                scheduler.Cancel(victim);
            };

            scheduler.Schedule(canceller);
            scheduler.Schedule(victim);
            DispatchDelayCall();
            DispatchDelayCall();

            Assert.That(cancellerRan, Is.True, "the cancelling callback must have run");
            Assert.That(victimRuns, Is.Zero, "cancelling mid-flush must reach an entry already in the drain batch");
        }

        [Test]
        public void SchedulingTheSameActionTwice_RunsItOnce()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var runs = 0;
            Action action = () => runs++;

            scheduler.Schedule(action);
            scheduler.Schedule(action);
            DispatchDelayCall();
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(1));
        }

        [Test]
        public void SelfReschedulingAction_RunsOnALaterDispatchNotInTheSameDrain()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var dispatches = 0;
            var runDispatches = new List<int>();
            Action selfRescheduling = null;
            selfRescheduling = () =>
            {
                runDispatches.Add(dispatches);
                if (runDispatches.Count == 1)
                {
                    scheduler.Schedule(selfRescheduling);
                }
            };

            scheduler.Schedule(selfRescheduling);

            dispatches = 1;
            DispatchDelayCall();
            Assert.That(runDispatches, Is.EqualTo(new[] { 1 }), "the drain must not re-run the rescheduled action");

            var probesBeforeRecovery = scheduler.InvocationListProbeCount;
            DispatchEditorUpdate();
            DispatchEditorUpdate();
            Assert.That(runDispatches, Is.EqualTo(new[] { 1 }), "recovery must not execute queued work");
            Assert.That(
                scheduler.InvocationListProbeCount,
                Is.EqualTo(probesBeforeRecovery),
                "unchanged recovery updates must not probe the delay multicast");

            dispatches = 2;
            DispatchDelayCall();

            Assert.That(runDispatches, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void DroppedDispatch_RecoversWithoutScheduling()
        {
            var scheduler = new UnityDeferredCallScheduler();
            EditorApplication.delayCall += () => throw new InvalidOperationException("foreign boom");
            var runs = 0;
            scheduler.Schedule(() => runs++);

            Assert.Throws<InvalidOperationException>(DispatchDelayCall);
            DispatchEditorUpdate();
            DispatchDelayCall();
            DispatchEditorUpdate();
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(1));
        }

        [Test]
        public void DroppedDispatch_SameActionRetryRepairsRegistration()
        {
            var scheduler = new UnityDeferredCallScheduler();
            EditorApplication.delayCall += () => throw new InvalidOperationException("foreign boom");
            var runs = 0;
            Action action = () => runs++;
            scheduler.Schedule(action);

            Assert.Throws<InvalidOperationException>(DispatchDelayCall);
            scheduler.Schedule(action);
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(1));
        }

        [Test]
        public void RecoveryHooks_DetachWhenIdleOrCancelled()
        {
            var scheduler = new UnityDeferredCallScheduler();
            EditorApplication.CallbackFunction foreignUpdate = () => { };
            EditorApplication.CallbackFunction foreignDelay = () => { };
            EditorApplication.update += foreignUpdate;
            EditorApplication.delayCall += foreignDelay;
            scheduler.Schedule(() => { });
            DispatchDelayCall();

            Assert.That(EditorApplication.update, Is.EqualTo(foreignUpdate));
            Assert.That(EditorApplication.delayCall, Is.Null);

            LogAssert.Expect(LogType.Error, new Regex("A deferred callback"));
            scheduler.Schedule(() => throw new InvalidOperationException("owned boom"));
            DispatchDelayCall();
            Assert.That(EditorApplication.update, Is.EqualTo(foreignUpdate));

            EditorApplication.delayCall += () => throw new InvalidOperationException("foreign boom");
            var cancelledRuns = 0;
            Action cancelled = () => cancelledRuns++;
            scheduler.Schedule(cancelled);
            Assert.Throws<InvalidOperationException>(DispatchDelayCall);

            EditorApplication.delayCall += foreignDelay;
            scheduler.Cancel(cancelled);
            Assert.That(EditorApplication.update, Is.EqualTo(foreignUpdate));
            Assert.That(EditorApplication.delayCall, Is.EqualTo(foreignDelay));

            DispatchEditorUpdate();
            DispatchDelayCall();
            Assert.That(cancelledRuns, Is.Zero);

            var freshRuns = 0;
            scheduler.Schedule(() => freshRuns++);
            DispatchDelayCall();
            Assert.That(freshRuns, Is.EqualTo(1));
            Assert.That(EditorApplication.update, Is.EqualTo(foreignUpdate));
            Assert.That(EditorApplication.delayCall, Is.Null);
        }

        [Test]
        public void RefreshScheduler_DroppedDispatchRecoversCoalescedRequest()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var refreshes = new List<bool>();
            var refreshScheduler = new IndexRefreshScheduler(refreshes.Add, scheduler);
            EditorApplication.delayCall += () => throw new InvalidOperationException("foreign boom");
            refreshScheduler.Request(false);

            Assert.Throws<InvalidOperationException>(DispatchDelayCall);
            refreshScheduler.Request(true);
            DispatchEditorUpdate();
            DispatchDelayCall();
            refreshScheduler.Request(false);
            DispatchDelayCall();

            Assert.That(refreshes, Is.EqualTo(new[] { true, false }));

            EditorApplication.delayCall += () => throw new InvalidOperationException("foreign boom");
            refreshScheduler.Request(false);
            Assert.Throws<InvalidOperationException>(DispatchDelayCall);
            refreshScheduler.Dispose();
            DispatchEditorUpdate();
            DispatchDelayCall();

            Assert.That(refreshes, Is.EqualTo(new[] { true, false }));
            Assert.That(EditorApplication.update, Is.Null);
            Assert.That(EditorApplication.delayCall, Is.Null);
        }

        // The pump can be dropped from a dispatch it never reaches, because Unity
        // invokes a snapshot and a throw from an earlier subscriber abandons the
        // rest of it. What must never happen again is the pre-rewrite outcome:
        // bookkeeping that claims the work is still queued while nothing will ever
        // run it.
        [Test]
        public void ForeignSubscriberThrowingAheadOfThePump_DoesNotWedgeTheQueue()
        {
            var scheduler = new UnityDeferredCallScheduler();
            EditorApplication.CallbackFunction foreignThrower =
                () => throw new InvalidOperationException("foreign boom");
            EditorApplication.delayCall += foreignThrower;
            var runs = 0;
            Action stranded = () => runs++;
            Action unrelated = () => { };

            scheduler.Schedule(stranded);
            Assert.Throws<InvalidOperationException>(DispatchDelayCall);
            Assert.That(runs, Is.Zero, "the pump never got to run in the abandoned dispatch");

            scheduler.Schedule(unrelated);
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(1), "the stranded entry must still be queued and runnable");
        }

        // EnsurePumpRegistered ran on EVERY Schedule and allocated a fresh
        // GetInvocationList() array each time to answer a question that cannot
        // change while nothing touches delayCall. Delegates are immutable, so
        // reference identity of the multicast is proof the pump is still in it.
        [Test]
        public void ABurstOfSchedules_ProbesTheInvocationListAtMostOnce()
        {
            var scheduler = new UnityDeferredCallScheduler();
            EditorApplication.CallbackFunction foreignSubscriber = () => { };
            EditorApplication.delayCall += foreignSubscriber;

            for (var i = 0; i < 100; i++)
            {
                var index = i;
                scheduler.Schedule(() => { _ = index; });
            }

            Assert.That(
                scheduler.InvocationListProbeCount,
                Is.LessThanOrEqualTo(1),
                "100 schedules in one tick must cost at most one invocation-list array");
        }

        // The cache must never survive a change to delayCall — that is exactly
        // the wedge the class exists to prevent. After Unity's dispatch nulls the
        // field, the next Schedule has to re-register rather than trust the
        // cached answer.
        [Test]
        public void AfterADispatch_TheNextScheduleReRegistersThePump()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var runs = 0;
            Action first = () => runs++;
            Action second = () => runs++;

            scheduler.Schedule(first);
            DispatchDelayCall();
            Assert.That(runs, Is.EqualTo(1));

            scheduler.Schedule(second);
            Assert.That(
                EditorApplication.delayCall,
                Is.Not.Null,
                "the pump must be back on delayCall after the field was nulled");

            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(2));
        }

        // A foreign subscriber replacing the multicast while the pump is missing
        // from it is precisely the case a bool flag would get wrong.
        [Test]
        public void PumpDroppedFromDelayCallAndReplacedByAForeignSubscriber_IsStillReRegistered()
        {
            var scheduler = new UnityDeferredCallScheduler();
            var runs = 0;
            Action queued = () => runs++;
            scheduler.Schedule(queued);

            // Drop the pump and put an unrelated subscriber in its place, so
            // delayCall is non-null but no longer contains us.
            EditorApplication.delayCall = null;
            EditorApplication.CallbackFunction foreignSubscriber = () => { };
            EditorApplication.delayCall += foreignSubscriber;

            Action alsoQueued = () => runs++;
            scheduler.Schedule(alsoQueued);
            DispatchDelayCall();

            Assert.That(runs, Is.EqualTo(2), "both queued actions must run; the queue must not wedge");
        }

        // Exactly what UnityEditor.EditorApplication.Internal_CallDelayFunctions does.
        private static void DispatchDelayCall()
        {
            var pending = EditorApplication.delayCall;
            EditorApplication.delayCall = null;
            pending?.Invoke();
        }

        private static void DispatchEditorUpdate()
        {
            EditorApplication.update?.Invoke();
        }
    }
}
