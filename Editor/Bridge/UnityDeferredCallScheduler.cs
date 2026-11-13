using System;
using System.Collections.Generic;
using Zep.Core.Application.Ports;
using UnityEditor;
using UnityEngine;
// Recovered seam: the decompiler emitted bare `CallbackFunction` for the
// nested EditorApplication delegate type.
using CallbackFunction = UnityEditor.EditorApplication.CallbackFunction;

namespace Zep.Editor.Bridge
{

// Owns its queue instead of chaining one wrapper per action onto
// EditorApplication.delayCall. Unity nulls delayCall and invokes a SNAPSHOT of
// the multicast, so a throw from any subscriber drops every subscriber behind
// it. With one wrapper per action that silently stranded the wrappers'
// bookkeeping — the affected actions could never be scheduled again for the
// rest of the domain's life, and the consumers' one-shot latches
// (IndexRefreshScheduler, AssetChangeSyncer) stayed latched with no error to
// attribute it to. One pump plus a private list keeps a throw local to the one
// action that threw.
internal sealed class UnityDeferredCallScheduler : IDeferredCallScheduler
{
	// _pendingSet is the authority on "still scheduled": it is what dedupes a
	// double Schedule and what makes Cancel effective even while the pump is
	// mid-drain, because the pump re-checks it immediately before each invoke.
	// _pending only preserves scheduling order.
	private readonly List<Action> _pending = new List<Action>();

	private readonly HashSet<Action> _pendingSet = new HashSet<Action>();

	private readonly CallbackFunction _pump;

	private readonly CallbackFunction _recoveryUpdate;

	internal UnityDeferredCallScheduler()
	{
		_pump = Drain;
		_recoveryUpdate = RecoverPumpRegistration;
	}

	public void Schedule(Action action)
	{
		if (action == null)
		{
			return;
		}

		if (_pendingSet.Add(action))
		{
			_pending.Add(action);
		}

		EnsureRecoveryRegistered();
		EnsurePumpRegistered();
	}

	public void Cancel(Action action)
	{
		if (action == null || !_pendingSet.Remove(action))
		{
			return;
		}

		// A no-op when the pump already took the entry into its drain batch;
		// the membership check at invoke time is what cancels it there.
		_pending.Remove(action);
		if (_pendingSet.Count == 0)
		{
			CleanupIdleRegistrations();
		}
	}

	// The multicast we last confirmed contains the pump. Delegates are
	// immutable, so every += and -= — Unity's own null-then-dispatch included —
	// publishes a DIFFERENT instance. Reference identity is therefore proof that
	// nothing has touched delayCall since we verified it, which is why this can
	// be cached where a bool could not: a bool would survive a foreign
	// subscriber throwing ahead of us and claim we were still registered,
	// wedging the queue permanently — the exact failure this class exists to
	// prevent. Anything else, including null, falls through to the full probe.
	private CallbackFunction _verifiedDelayCall;

	// Test hook: how many GetInvocationList() arrays this scheduler allocated.
	// The whole point of the cache is that a burst of Schedule calls pays for at
	// most one.
	internal int InvocationListProbeCount { get; private set; }

	private void EnsurePumpRegistered()
	{
		var registered = EditorApplication.delayCall;
		if (registered != null)
		{
			if (ReferenceEquals(registered, _verifiedDelayCall))
			{
				return;
			}

			InvocationListProbeCount++;
			var subscribers = registered.GetInvocationList();
			for (var i = 0; i < subscribers.Length; i++)
			{
				if (subscribers[i].Equals(_pump))
				{
					_verifiedDelayCall = registered;
					return;
				}
			}
		}

		EditorApplication.delayCall += _pump;
		_verifiedDelayCall = EditorApplication.delayCall;
	}

	private void EnsureRecoveryRegistered()
	{
		EditorApplication.update -= _recoveryUpdate;
		EditorApplication.update += _recoveryUpdate;
	}

	private void RecoverPumpRegistration()
	{
		if (_pendingSet.Count == 0)
		{
			CleanupIdleRegistrations();
			return;
		}

		EnsurePumpRegistered();
	}

	private void CleanupIdleRegistrations()
	{
		EditorApplication.update -= _recoveryUpdate;
		EditorApplication.delayCall -= _pump;
		_verifiedDelayCall = null;
	}

	private void Drain()
	{
		_verifiedDelayCall = null;
		if (_pending.Count == 0)
		{
			CleanupIdleRegistrations();
			return;
		}

		// Drain a snapshot so an action that reschedules itself runs on the NEXT
		// tick instead of spinning inside this drain.
		var batch = _pending.ToArray();
		_pending.Clear();

		for (var i = 0; i < batch.Length; i++)
		{
			var action = batch[i];
			if (!_pendingSet.Remove(action))
			{
				continue;
			}

			try
			{
				action();
			}
			catch (Exception exception)
			{
				Debug.LogError(
					"[Zep] A deferred callback (" + DescribeAction(action)
					+ ") threw; the remaining deferred callbacks still ran.\n" + exception);
			}
		}

		if (_pendingSet.Count > 0)
		{
			EnsureRecoveryRegistered();
			EnsurePumpRegistered();
		}
		else
		{
			CleanupIdleRegistrations();
		}
	}

	private static string DescribeAction(Action action)
	{
		var method = action.Method;
		return (method.DeclaringType?.Name ?? "<unknown>") + "." + method.Name;
	}
}

}
