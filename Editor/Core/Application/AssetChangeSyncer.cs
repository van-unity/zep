using System;
using System.Collections.Generic;
using Zep.Core.Application.Ports;
using Zep.Core.Indexing;

namespace Zep.Core.Application
{

// Applies a normalized external change batch through the canonical catalog
// boundary and reports both what it applied and the paths it could not resolve.
internal delegate ExternalChangeApplicationResult TryApplyExternalChanges(
	ProjectAssetChanges changes,
	out IReadOnlyList<string> unresolvedPaths);

internal sealed class AssetChangeSyncer
{
	private readonly IDeferredCallScheduler _scheduler;

	private readonly TryApplyExternalChanges _tryApplyIncremental;

	// Fired (deferred, coalesced) after an incrementally-patched batch: the
	// projection caches are already current, so only the view model catches up.
	private readonly Action _onApplyProjectionOnly;

	private readonly Action _onFullRefreshNeeded;

	private readonly Action _processCallback;

	// How many owned asset batches are still waiting for the coarse
	// projectChanged Unity raises for each of them. A single-shot bool absorbed
	// only the first, so every batch after it fell through to a full project
	// reconciliation the panel had already done incrementally.
	private int _acknowledgedOwnedBatches;

	private bool _isIncrementalProjectionScheduled;

	private bool _isDisposed;

	internal AssetChangeSyncer(
		IDeferredCallScheduler scheduler,
		TryApplyExternalChanges tryApplyIncremental,
		Action onApplyProjectionOnly,
		Action onFullRefreshNeeded)
	{
		_scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
		_tryApplyIncremental = tryApplyIncremental ?? throw new ArgumentNullException(nameof(tryApplyIncremental));
		_onApplyProjectionOnly = onApplyProjectionOnly ?? throw new ArgumentNullException(nameof(onApplyProjectionOnly));
		_onFullRefreshNeeded = onFullRefreshNeeded ?? throw new ArgumentNullException(nameof(onFullRefreshNeeded));
		_processCallback = ProcessScheduledProjectionUpdate;
	}

	internal void HandleProjectChanged()
	{
		if (!_isDisposed)
		{
			if (_acknowledgedOwnedBatches > 0)
			{
				_acknowledgedOwnedBatches--;
			}
			else
			{
				_onFullRefreshNeeded();
			}
		}
	}

	internal void AcknowledgeOwnedAssetBatch()
	{
		if (!_isDisposed)
		{
			_acknowledgedOwnedBatches++;
		}
	}

	internal void HandleAssetChanges(ProjectAssetChanges changes)
	{
		changes = changes.Normalize();
		if (_isDisposed || changes.IsEmpty)
		{
			return;
		}

		// A Changed result means catalog and dependent projections are current for
		// the paths it applied, and only the coalesced view-model publication
		// remains. Unresolved paths are independent of that: whatever was applied
		// stays applied, and only the remainder needs authoritative recovery.
		var incrementalResult = _tryApplyIncremental(changes, out var unresolvedPaths);
		if (incrementalResult == ExternalChangeApplicationResult.RequiresFullRebuild)
		{
			_onFullRefreshNeeded();
			return;
		}

		if (incrementalResult == ExternalChangeApplicationResult.Changed)
		{
			ScheduleProjectionUpdate();
		}
		else if (unresolvedPaths.Count == 0)
		{
			ZepPerfLog.Count(ZepPerfCounter.DeduplicatedAcknowledgements);
			AcknowledgeOwnedAssetBatch();
			return;
		}

		// A concrete path that the catalog boundary cannot resolve is recovery,
		// not permission to mutate a second file-only index. Schedule the existing
		// authoritative refresh path; the recorded concrete-change overlay already
		// carries these paths for it.
		if (unresolvedPaths.Count > 0)
		{
			_onFullRefreshNeeded();
		}
	}

	internal void Dispose()
	{
		_isDisposed = true;
		_scheduler.Cancel(_processCallback);
	}

	private void ScheduleProjectionUpdate()
	{
		AcknowledgeOwnedAssetBatch();
		if (!_isIncrementalProjectionScheduled)
		{
			_isIncrementalProjectionScheduled = true;
			_scheduler.Schedule(_processCallback);
		}
	}

	private void ProcessScheduledProjectionUpdate()
	{
		_isIncrementalProjectionScheduled = false;
		if (_isDisposed)
		{
			return;
		}

		ZepPerfLog.Count(ZepPerfCounter.EditorTurnPublications);

		_onApplyProjectionOnly();
	}
}

}
