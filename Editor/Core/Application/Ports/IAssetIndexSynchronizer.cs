using System;
using System.Collections.Generic;
using Zep.Core.Indexing;

namespace Zep.Core.Application.Ports
{

public interface IAssetIndexSynchronizer : IDisposable
{
	/// <summary>
	/// Refreshes the index immediately when the source is available. If an
	/// authoritative synchronization already owns the source, adopts that work and
	/// invokes <paramref name="onComplete"/> when its result is published instead of
	/// blocking the caller or enumerating the project twice.
	/// </summary>
	void Synchronize(Action onComplete = null);

	/// <summary>
	/// Hydrates from the persistent cache when possible; otherwise enumerates the
	/// project. <paramref name="onFullScanStarted"/> is invoked only when no valid
	/// cache exists and enumeration is about to begin.
	/// </summary>
	void SynchronizeAsync(Action onFullScanStarted, Action onComplete);

	/// <summary>
	/// Invalidates any background full scan that has not yet been published. The
	/// implementation must not let a point-in-time scan overwrite a newer Unity
	/// asset change when its main-thread completion callback runs.
	/// </summary>
	void InvalidateInProgressScan();

	// Folder records for the projection rebuild — the Bridge implementation returns
	// the cache-persisted list on the warm path, else a fresh source walk.
	// (Recovered seam: post-dates the May-13 decompile baseline.)
	IReadOnlyList<AssetFolderRecord> GetFolderRecordsForProjection();

	/// <summary>
	/// True from the moment the follow-up type-reconciliation job starts until that
	/// job has finished <b>and its snapshot save has resolved</b> — a caller that
	/// starts a new synchronization while this is true cancels the job and costs the
	/// next reload a cold scan.
	/// </summary>
	bool HasBackgroundJob { get; }
}

/// <summary>
/// Optional completion capability for callers that must distinguish publication
/// from a synchronization path that retired after a failure.
/// </summary>
internal interface IAssetIndexSynchronizationResults : IAssetIndexSynchronizer
{
	void SynchronizeWithOutcome(Action<AssetIndexSynchronizationResult> onComplete);

	void SynchronizeAsyncWithOutcome(
		Action onFullScanStarted,
		Action<AssetIndexSynchronizationResult> onComplete);
}

public sealed class AssetIndexSynchronizationResult
{
	private AssetIndexSynchronizationResult(bool succeeded, string errorMessage)
	{
		Succeeded = succeeded;
		ErrorMessage = errorMessage;
	}

	internal bool Succeeded { get; }

	internal string ErrorMessage { get; }

	public static AssetIndexSynchronizationResult Success { get; } =
		new AssetIndexSynchronizationResult(true, string.Empty);

	public static AssetIndexSynchronizationResult Failure(string errorMessage)
	{
		if (string.IsNullOrWhiteSpace(errorMessage))
		{
			throw new ArgumentException(
				"A synchronization failure requires an error message.",
				nameof(errorMessage));
		}

		return new AssetIndexSynchronizationResult(false, errorMessage);
	}
}

// Optional persistence capability. Concrete callbacks can durably invalidate a
// snapshot before global hydration without forcing the catalog to load.
internal interface ICatalogDirtyMarker
{
	void MarkPersistentStateDirty();
}

// Optional overlay recorder for a background authoritative scan. The concrete
// catalog applies the Unity callback first, then records only those paths so a
// restarted worker can replay them without a main-thread project sweep.
internal interface IAssetScanChangeRecorder
{
	void RecordConcreteChanges(ProjectAssetChanges changes);

	// Concrete paths the incremental source could not resolve are not covered
	// by the live catalog. A background proof/save must account for them before
	// it can retire their durable journal entries.
	void RecordUnresolvedChanges(IReadOnlyList<string> paths);
}

}
