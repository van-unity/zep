using System;
using Zep.Core.Application;
using Zep.Core.Indexing;
using UnityEditor;
using UnityEngine;

namespace Zep.Editor.Bridge.Indexing
{

public sealed class ZepAssetPostprocessor : AssetPostprocessor
{
	public static event Action<ProjectAssetChanges> Changed;

	static ZepAssetPostprocessor()
	{
		AssetDatabaseAssetRecordSource.BindCoreIndexing();
	}

	private static void OnPostprocessAllAssets(
		string[] importedAssets,
		string[] deletedAssets,
		string[] movedAssets,
		string[] movedFromAssetPaths,
		bool didDomainReload)
	{
		ZepPerfLog.Count(ZepPerfCounter.RawPostprocessorCallbacks);
		try
		{
			var changes = new ProjectAssetChanges(
				importedAssets,
				deletedAssets,
				movedAssets,
				movedFromAssetPaths,
				didDomainReload).Normalize();
			ZepPerfLog.Count(ZepPerfCounter.NormalizedPostprocessorBatches);
			if (changes.IsEmpty)
			{
				return;
			}

			// Persist the concrete paths before notifying reload-resident state. A
			// script import can tear down the AppDomain immediately after this
			// callback, so the next controller must not depend on an in-memory event
			// subscriber having survived long enough to update the saved index.
			PersistentAssetIndexCache.RecordPendingChanges(changes);
			Action<ProjectAssetChanges> changed = ZepAssetPostprocessor.Changed;
			if (changed == null)
			{
				return;
			}

			using (ZepPerfLog.Measure(
				"postprocessor",
				ZepPerfStage.PostprocessorAcknowledgement))
			{
				changed(changes);
			}
		}
		catch (Exception arg)
		{
			Debug.LogError((object)$"[Zep] AssetPostprocessor subscriber threw: {arg}");
		}
	}
}

}
