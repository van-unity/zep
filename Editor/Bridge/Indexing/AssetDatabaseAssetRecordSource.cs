using System;
using System.Collections.Generic;
using System.IO;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.FileOperations;
using Zep.Editor.Bridge.Settings;
using UnityEditor;
using UnityEditor.AssetImporters;
// Unity 6.5 moved the GUID struct (and AssetDatabase.GetLabels(GUID)) from
// UnityEditor to UnityEngine; earlier Unity 6 has it only in UnityEditor.
#if UNITY_6000_5_OR_NEWER
using GUID = UnityEngine.GUID;
#else
using GUID = UnityEditor.GUID;
#endif

namespace Zep.Editor.Bridge.Indexing
{
public sealed class AssetDatabaseAssetRecordSource : IAssetRecordSource
{
	private IReadOnlyList<AssetFolderRecord> _cachedFolderRecords = Array.Empty<AssetFolderRecord>();
	private bool _hasEnumeratedFolderSnapshot;
	private bool _hasPreparedMetadataSnapshot;
	private IReadOnlyDictionary<string, IReadOnlyList<string>> _preparedUnityLabelsByGuid;

	public bool IsThreadSafe => false;

	// The index cache and the synchronizer live in Core and cannot call Unity, so
	// their AssetDatabase queries, console sink, editor tick and session flag are
	// bound here. The load callback covers a normal domain load; the controller
	// host and the postprocessor call this too, because either can run before
	// load callbacks do.
	[InitializeOnLoadMethod]
	internal static void BindCoreIndexing()
	{
		PersistentAssetIndexCache.BindUnity(
			() => AssetDatabase.GlobalArtifactDependencyVersion,
			BuildUnityLabelSnapshot,
			BuildAuthoritativeTypeSnapshot,
			UnityEngine.Debug.LogWarning);
		AssetIndexSynchronizer.BindUnity(EditorHost.Instance);
		AssetRecordSourceHost.Bind(RecordSourceHost.Instance);
	}

	private sealed class RecordSourceHost : IAssetRecordSourceEditorHost
	{
		internal static readonly RecordSourceHost Instance = new RecordSourceHost();

		public bool TryRecoverTagState(out string error) =>
			UnityProjectAssetGateway.TryRecoverTagStateTransaction(out error);

		public void PrepareInferredTypeMetadata() =>
			AssetDatabaseAssetRecordSource.PrepareInferredTypeMetadata();

		public TagDefinitionStore TagDefinitions => ZepTagSettings.instance.Store;

		public TagAssignmentStore TagAssignments => ZepTagAssignmentStore.instance;

		public IReadOnlyDictionary<string, IReadOnlyList<string>> BuildUnityLabelSnapshot() =>
			AssetDatabaseAssetRecordSource.BuildUnityLabelSnapshot();

		public IReadOnlyDictionary<string, AssetTypeMetadata> BuildAuthoritativeTypeSnapshot() =>
			AssetDatabaseAssetRecordSource.BuildAuthoritativeTypeSnapshot();

		public AssetTypeMetadata ResolveTypeMetadata(
			string assetPath,
			string guid,
			IReadOnlyDictionary<string, AssetTypeMetadata> authoritativeTypesByGuid) =>
			AssetDatabaseAssetRecordSource.ResolveTypeMetadata(assetPath, guid, authoritativeTypesByGuid);

		public AssetTypeMetadata ResolveIncrementalTypeMetadata(string assetPath) =>
			AssetDatabaseAssetRecordSource.ResolveIncrementalTypeMetadata(assetPath);

		public AssetTypeMetadata ResolveMainThreadTypeMetadata(string assetPath) =>
			AssetDatabaseAssetRecordSource.ResolveMainThreadTypeMetadata(assetPath);

		public AssetFolderRecord BuildFolderRecord(
			string folderPath,
			IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null) =>
			BuildFolderRecordInternal(folderPath, unityLabelsByGuid);

		public AssetRecord BuildRecordFromAssetDatabase(AssetPath assetPath) =>
			new AssetDatabaseAssetRecordSource().BuildRecord(assetPath);

		public AssetFolderRecord BuildFolderRecordFromAssetDatabase(AssetPath folderPath) =>
			new AssetDatabaseAssetRecordSource().BuildFolderRecord(folderPath);

		public string AssetPathToGuid(string assetPath) => AssetDatabase.AssetPathToGUID(assetPath);

		public bool IsValidFolder(string assetPath) => AssetDatabase.IsValidFolder(assetPath);

		public string ToAbsolutePath(string assetPath) => ProjectPaths.ToAbsolutePath(assetPath);

		public RegisteredPackageRootSnapshot PackageRoots
		{
			get => RegisteredPackageRoots.Current;
			set => RegisteredPackageRoots.SetForTests(value);
		}

		public RegisteredPackageRootSnapshot RefreshPackageRoots() => RegisteredPackageRoots.Refresh();
	}

	private sealed class EditorHost : IAssetIndexEditorHost
	{
		internal static readonly EditorHost Instance = new EditorHost();

		public void AttachUpdate(Action poll) => EditorApplication.update += AsCallback(poll);

		public void DetachUpdate(Action poll) => EditorApplication.update -= AsCallback(poll);

		// Rebuilt from the poll's own target and method rather than wrapping the
		// Action: a wrapper's target would be the Action instance, so the delegate
		// built at detach time would not equal the one subscribed at attach time.
		private static EditorApplication.CallbackFunction AsCallback(Action poll) =>
			(EditorApplication.CallbackFunction)Delegate.CreateDelegate(
				typeof(EditorApplication.CallbackFunction),
				poll.Target,
				poll.Method);

		public void Log(string message) => UnityEngine.Debug.Log(message);

		public void LogWarning(string message) => UnityEngine.Debug.LogWarning(message);

		public void LogException(Exception exception) => UnityEngine.Debug.LogException(exception);

		public bool GetSessionFlag(string key) => SessionState.GetBool(key, false);

		public void SetSessionFlag(string key, bool value) => SessionState.SetBool(key, value);

		public string[] CaptureAllAssetPaths() => CaptureAllAssetPathsForTypeSnapshot();

		public HashSet<string> CaptureScriptedImporterExtensions() =>
			AssetDatabaseAssetRecordSource.CaptureScriptedImporterExtensions();

		public List<string> FilterAuthoritativeTypeCandidatePaths(
			IReadOnlyList<string> allAssetPaths,
			ISet<string> scriptedImporterExtensions) =>
			AssetDatabaseAssetRecordSource.FilterAuthoritativeTypeCandidatePaths(
				allAssetPaths,
				scriptedImporterExtensions);

		public void AddAuthoritativeTypeMetadata(
			string path,
			IDictionary<string, AssetTypeMetadata> typesByGuid) =>
			AssetDatabaseAssetRecordSource.AddAuthoritativeTypeMetadata(path, typesByGuid);

		public bool RequiresAuthoritativeType(
			string assetPath,
			ISet<string> scriptedImporterExtensions) =>
			AssetDatabaseAssetRecordSource.RequiresAuthoritativeType(
				assetPath,
				scriptedImporterExtensions);
	}

	public void PrepareForEnumeration(
		IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null,
		IReadOnlyDictionary<string, AssetTypeMetadata> typesByGuid = null)
	{
		_hasEnumeratedFolderSnapshot = false;
		_hasPreparedMetadataSnapshot = false;
		_preparedUnityLabelsByGuid = null;
		if (!UnityProjectAssetGateway.TryRecoverTagStateTransaction(out var error))
		{
			throw new ProjectTagStateUnavailableException(error);
		}

		_preparedUnityLabelsByGuid = unityLabelsByGuid ?? BuildUnityLabelSnapshot();
		_hasPreparedMetadataSnapshot = true;
	}

	public IEnumerable<AssetRecord> GetRecords()
	{
		if (!UnityProjectAssetGateway.TryRecoverTagStateTransaction(out var error))
		{
			throw new ProjectTagStateUnavailableException(error);
		}

		TagDefinitionStore store = ScriptableSingleton<ZepTagSettings>.instance.Store;
		TagAssignmentStore instance = ZepTagAssignmentStore.instance;
		store.EnsureDefaults();
		instance.ReloadIfFileChanged();
		var unityLabelsByGuid = _hasPreparedMetadataSnapshot
			? _preparedUnityLabelsByGuid
			: BuildUnityLabelSnapshot();
		_hasPreparedMetadataSnapshot = false;
		_preparedUnityLabelsByGuid = null;
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGetAllAssetPathsCalls);
			string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
		List<AssetFolderRecord> folderRecords = new List<AssetFolderRecord>();
		List<AssetRecord> assetRecords = new List<AssetRecord>();
		foreach (string assetPath in allAssetPaths)
		{
			if (string.IsNullOrEmpty(assetPath) || (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) && !assetPath.StartsWith("Packages/", StringComparison.Ordinal)))
			{
				continue;
			}
			if (AssetDatabase.IsValidFolder(assetPath))
			{
				folderRecords.Add(BuildFolderRecordInternal(assetPath, unityLabelsByGuid));
			}
			else if (!assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
			{
				AssetRecord assetRecord = BuildRecordInternal(assetPath, store, instance, unityLabelsByGuid);
				if (assetRecord != null)
				{
					assetRecords.Add(assetRecord);
				}
			}
		}
		_cachedFolderRecords = folderRecords;
		_hasEnumeratedFolderSnapshot = true;
		return assetRecords;
	}

	public IReadOnlyList<AssetFolderRecord> GetFolderRecords()
	{
		if (_hasEnumeratedFolderSnapshot)
		{
			_hasEnumeratedFolderSnapshot = false;
			return _cachedFolderRecords;
		}

		var unityLabelsByGuid = BuildUnityLabelSnapshot();
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGetAllAssetPathsCalls);
			string[] allAssetPaths = AssetDatabase.GetAllAssetPaths();
		List<AssetFolderRecord> folderRecords = new List<AssetFolderRecord>();
		foreach (string assetPath in allAssetPaths)
		{
			if (!string.IsNullOrEmpty(assetPath) && (assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath.StartsWith("Packages/", StringComparison.Ordinal)) && AssetDatabase.IsValidFolder(assetPath))
			{
				folderRecords.Add(BuildFolderRecordInternal(assetPath, unityLabelsByGuid));
			}
		}
		_cachedFolderRecords = folderRecords;
		return folderRecords;
	}

	public AssetFolderRecord BuildFolderRecord(AssetPath folderPath)
	{
		if (folderPath.IsEmpty)
		{
			return null;
		}

		string raw = folderPath;
		if ((!raw.StartsWith("Assets/", StringComparison.Ordinal)
				&& !raw.StartsWith("Packages/", StringComparison.Ordinal))
			|| !AssetDatabase.IsValidFolder(raw))
		{
			return null;
		}

		return BuildFolderRecordInternal(raw);
	}

	public AssetRecord BuildRecord(AssetPath assetPath)
	{
		if (assetPath.IsEmpty)
		{
			return null;
		}
		string normalizedPath = assetPath;
		if ((!normalizedPath.StartsWith("Assets/", StringComparison.Ordinal) && !normalizedPath.StartsWith("Packages/", StringComparison.Ordinal)))
		{
			return null;
		}
		if (normalizedPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (AssetDatabase.IsValidFolder(normalizedPath))
		{
			return null;
		}
		if (!UnityProjectAssetGateway.TryRecoverTagStateTransaction(out var error))
		{
			throw new ProjectTagStateUnavailableException(error);
		}

		TagDefinitionStore tagDefinitions = ScriptableSingleton<ZepTagSettings>.instance.Store;
		TagAssignmentStore tagAssignments = ZepTagAssignmentStore.instance;
		tagDefinitions.EnsureDefaults();
		tagAssignments.ReloadIfFileChanged();
		return BuildRecordInternal(normalizedPath, tagDefinitions, tagAssignments);
	}

	internal static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildUnityLabelSnapshot()
	{
		try
		{
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseFindAssetsCalls);
				var labeledGuids = AssetDatabase.FindAssets("l:");
			var labelsByGuid = new Dictionary<string, IReadOnlyList<string>>(
				labeledGuids.Length,
				StringComparer.OrdinalIgnoreCase);
			for (var i = 0; i < labeledGuids.Length; i++)
			{
				var guid = labeledGuids[i];
				if (string.IsNullOrEmpty(guid) || labelsByGuid.ContainsKey(guid))
				{
					continue;
				}

				// GUID-keyed read: Unity resolves labels from its own table without
				// importing the asset. The object-based overload made this snapshot
				// load EVERY labeled asset in the project on every hydration, cache
				// hits included.
				if (!GUID.TryParse(guid, out var typedGuid))
				{
					continue;
				}

					ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseLabelQueries);
					var labels = AssetDatabase.GetLabels(typedGuid);
				if (labels == null || labels.Length == 0)
				{
					continue;
				}

				Array.Sort(labels, StringComparer.Ordinal);
				labelsByGuid.Add(guid, labels);
			}

			return labelsByGuid;
		}
		catch (Exception ex)
		{
			// The native and hybrid sources fall back to direct .meta parsing when the
			// AssetDatabase is temporarily unavailable (for example during refresh).
			UnityEngine.Debug.LogWarning(
				$"[Zep] Unity-label snapshot failed ({ex.GetType().Name}: {ex.Message}); falling back to .meta files.");
			return null;
		}
	}

	// Extension inference is exact for Unity's built-in formats, but `.asset` and
	// unregistered extensions can represent arbitrary ScriptableObject or
	// ScriptedImporter types. Snapshot only those ambiguous paths on the main thread;
	// Hybrid/Native workers can then retain Unity's concrete type without touching an
	// editor API or paying a GetMainAssetTypeAtPath call for every texture/script/etc.
	internal static IReadOnlyDictionary<string, AssetTypeMetadata> BuildAuthoritativeTypeSnapshot()
	{
		try
		{
			var typesByGuid = new Dictionary<string, AssetTypeMetadata>(StringComparer.OrdinalIgnoreCase);
			var scriptedImporterExtensions = CaptureScriptedImporterExtensions();

			// `.asset`, unmapped extensions, and registered ScriptedImporter extensions
			// can all have a main-object type that extension inference cannot prove. Only
			// those paths pay a Unity type lookup, and only on a cold-cache rebuild.
			var candidates = FilterAuthoritativeTypeCandidatePaths(
				CaptureAllAssetPathsForTypeSnapshot(),
				scriptedImporterExtensions);
			for (var i = 0; i < candidates.Count; i++)
			{
				AddAuthoritativeTypeMetadata(candidates[i], typesByGuid);
			}

			return typesByGuid;
		}
		catch (Exception ex)
		{
			UnityEngine.Debug.LogWarning(
				$"[Zep] Concrete-type snapshot failed ({ex.GetType().Name}: {ex.Message}); requiring main-thread enumeration.");
			return null;
		}
	}

	internal static string[] CaptureAllAssetPathsForTypeSnapshot()
	{
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGetAllAssetPathsCalls);
		return AssetDatabase.GetAllAssetPaths();
	}

	internal static HashSet<string> CaptureScriptedImporterExtensions()
	{
		PrepareInferredTypeMetadata();
		return BuildScriptedImporterExtensions();
	}

	internal static List<string> FilterAuthoritativeTypeCandidatePaths(
		IReadOnlyList<string> allAssetPaths,
		ISet<string> scriptedImporterExtensions)
	{
		var candidates = new List<string>();
		if (allAssetPaths == null || scriptedImporterExtensions == null)
		{
			return candidates;
		}

		for (var i = 0; i < allAssetPaths.Count; i++)
		{
			var path = allAssetPaths[i];
			if (IsIndexableAssetPath(path)
				&& RequiresAuthoritativeType(path, scriptedImporterExtensions))
			{
				candidates.Add(path);
			}
		}

		return candidates;
	}

	internal static void AddAuthoritativeTypeMetadata(
		string path,
		IDictionary<string, AssetTypeMetadata> typesByGuid)
	{
		if (typesByGuid == null || AssetDatabase.IsValidFolder(path))
		{
			return;
		}

		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
		var guid = AssetDatabase.AssetPathToGUID(path);
		if (string.IsNullOrEmpty(guid) || typesByGuid.ContainsKey(guid))
		{
			return;
		}

		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseTypeQueries);
		var type = AssetDatabase.GetMainAssetTypeAtPath(path);
		if (type != null)
		{
			typesByGuid[guid] = BuildTypeMetadata(type);
		}
	}

	internal static AssetTypeMetadata ResolveTypeMetadata(
		string assetPath,
		string guid,
		IReadOnlyDictionary<string, AssetTypeMetadata> authoritativeTypesByGuid)
	{
		if (authoritativeTypesByGuid != null
			&& !string.IsNullOrEmpty(guid)
			&& authoritativeTypesByGuid.TryGetValue(guid, out var typeMetadata)
			&& typeMetadata != null)
		{
			return typeMetadata;
		}

		return ResolveInferredTypeMetadata(assetPath);
	}

	// The incremental (external-change) path used to ask Unity for EVERY changed
	// path's main type, while the cold scan asks only for the ambiguous ones. A
	// 5,000-file import paid 5,000 native type resolutions for records the cold
	// scan would have inferred from the extension. Same rule as the cold scan:
	// extension inference is exact for Unity's built-in formats, so only `.asset`,
	// unmapped, and ScriptedImporter extensions pay the Unity query.
	internal static AssetTypeMetadata ResolveIncrementalTypeMetadata(string assetPath)
	{
		PrepareInferredTypeMetadata();
		return RequiresAuthoritativeType(assetPath, ScriptedImporterExtensions)
			? ResolveMainThreadTypeMetadata(assetPath)
			: ResolveInferredTypeMetadata(assetPath);
	}

	// TypeCache is stable for the life of a domain, and statics reset with it.
	private static HashSet<string> _scriptedImporterExtensions;
	private static volatile IReadOnlyDictionary<string, AssetTypeMetadata> _inferredTypesByName;

	internal static void PrepareInferredTypeMetadata()
	{
		if (_inferredTypesByName != null)
		{
			return;
		}

		var inferredTypes = new Dictionary<string, AssetTypeMetadata>(StringComparer.Ordinal);
		var ambiguousNames = new HashSet<string>(StringComparer.Ordinal);
		foreach (var type in TypeCache.GetTypesDerivedFrom<UnityEngine.Object>())
		{
			if (type == null || !IsUnityRuntimeOrEditorType(type.Namespace))
			{
				continue;
			}

			var name = type.Name;
			if (ambiguousNames.Contains(name))
			{
				continue;
			}

			if (inferredTypes.ContainsKey(name))
			{
				inferredTypes.Remove(name);
				ambiguousNames.Add(name);
				continue;
			}

			inferredTypes.Add(name, BuildTypeMetadata(type));
		}

		_inferredTypesByName = inferredTypes;
	}

	private static AssetTypeMetadata ResolveInferredTypeMetadata(string assetPath)
	{
		var typeName = AssetExtensionTypeMap.GetTypeNameForPath(assetPath);
		var inferredTypes = _inferredTypesByName;
		return inferredTypes != null
			&& inferredTypes.TryGetValue(typeName, out var metadata)
			? metadata
			: new AssetTypeMetadata(typeName);
	}

	private static bool IsUnityRuntimeOrEditorType(string typeNamespace)
	{
		return !string.IsNullOrEmpty(typeNamespace)
			&& (typeNamespace == "UnityEngine"
				|| typeNamespace.StartsWith("UnityEngine.", StringComparison.Ordinal)
				|| typeNamespace == "UnityEditor"
				|| typeNamespace.StartsWith("UnityEditor.", StringComparison.Ordinal));
	}

	private static HashSet<string> ScriptedImporterExtensions =>
		_scriptedImporterExtensions ??= BuildScriptedImporterExtensions();

	internal static AssetTypeMetadata ResolveMainThreadTypeMetadata(string assetPath)
	{
		ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseTypeQueries);
		var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
		return type == null
			? ResolveInferredTypeMetadata(assetPath)
			: BuildTypeMetadata(type);
	}

	internal static AssetTypeMetadata BuildTypeMetadata(Type type)
	{
		if (type == null)
		{
			return new AssetTypeMetadata(AssetExtensionTypeMap.UnknownTypeName);
		}

		var baseTypeNames = new List<string>();
		for (var baseType = type.BaseType;
			baseType != null && typeof(UnityEngine.Object).IsAssignableFrom(baseType);
			baseType = baseType.BaseType)
		{
			if (baseType == typeof(UnityEngine.Object))
			{
				break;
			}

			baseTypeNames.Add(baseType.Name);
		}

		return new AssetTypeMetadata(type.Name, baseTypeNames);
	}

	internal static bool RequiresAuthoritativeType(
		string assetPath,
		ISet<string> scriptedImporterExtensions)
	{
		var inferred = AssetExtensionTypeMap.GetTypeNameForPath(assetPath);
		var inferredTypes = _inferredTypesByName;
		return string.Equals(inferred, AssetExtensionTypeMap.ScriptableObjectTypeName, StringComparison.Ordinal)
			|| string.Equals(inferred, AssetExtensionTypeMap.UnknownTypeName, StringComparison.Ordinal)
			|| inferredTypes == null
			|| !inferredTypes.ContainsKey(inferred)
			|| scriptedImporterExtensions.Contains(AssetExtensionTypeMap.GetLowerExtension(assetPath));
	}

	private static HashSet<string> BuildScriptedImporterExtensions()
	{
		var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var importerType in TypeCache.GetTypesDerivedFrom<ScriptedImporter>())
		{
			var attribute = Attribute.GetCustomAttribute(
				importerType,
				typeof(ScriptedImporterAttribute),
				inherit: false) as ScriptedImporterAttribute;
			if (attribute == null)
			{
				continue;
			}

			AddExtensions(extensions, attribute.fileExtensions);
			AddExtensions(extensions, attribute.overrideFileExtensions);
		}

		return extensions;
	}

	private static void AddExtensions(HashSet<string> destination, IReadOnlyList<string> extensions)
	{
		if (extensions == null)
		{
			return;
		}

		for (var i = 0; i < extensions.Count; i++)
		{
			var extension = extensions[i];
			if (string.IsNullOrWhiteSpace(extension))
			{
				continue;
			}

			extension = extension.Trim();
			destination.Add(extension[0] == '.' ? extension : "." + extension);
		}
	}

	private static bool IsIndexableAssetPath(string assetPath)
	{
		return !string.IsNullOrEmpty(assetPath)
			&& (assetPath.StartsWith("Assets/", StringComparison.Ordinal)
				|| assetPath.StartsWith("Packages/", StringComparison.Ordinal))
			&& !assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
	}

	private static AssetRecord BuildRecordInternal(
		string assetPath,
		TagDefinitionStore tagSettings,
		TagAssignmentStore tagAssignmentStore,
		IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null)
	{
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
			string guid = AssetDatabase.AssetPathToGUID(assetPath);
		if (string.IsNullOrEmpty(guid))
		{
			return null;
		}
		UnityEngine.Object mainAsset = null;
		Type mainAssetTypeAtPath;
		IReadOnlyList<string> unityLabels;
		if (unityLabelsByGuid != null)
		{
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseTypeQueries);
				mainAssetTypeAtPath = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
			unityLabels = unityLabelsByGuid.TryGetValue(guid, out var resolvedLabels)
				? resolvedLabels
				: Array.Empty<string>();
		}
		else
		{
			mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
			if (mainAsset != null)
			{
				mainAssetTypeAtPath = mainAsset.GetType();
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseLabelQueries);
			}
			else
			{
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseTypeQueries);
				mainAssetTypeAtPath = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
			}
			unityLabels = mainAsset == null
				? Array.Empty<string>()
				: AssetDatabase.GetLabels(mainAsset);
		}
		string assetName = Path.GetFileNameWithoutExtension(assetPath);
		if (string.IsNullOrEmpty(assetName))
		{
			assetName = Path.GetFileName(assetPath);
		}
		string tagSearchText = tagSettings.BuildTagSearchText(tagAssignmentStore.GetTagIds(guid));
		return new AssetRecord(
			guid,
			assetPath,
			assetName,
				(mainAssetTypeAtPath == null) ? "Unknown" : mainAssetTypeAtPath.Name,
				tagSearchText,
				unityLabels,
				BuildTypeMetadata(mainAssetTypeAtPath).BaseTypeNames);
	}

	internal static AssetFolderRecord BuildFolderRecordInternal(
		string folderPath,
		IReadOnlyDictionary<string, IReadOnlyList<string>> unityLabelsByGuid = null)
	{
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
			var guid = AssetDatabase.AssetPathToGUID(folderPath);
		IReadOnlyList<string> unityLabels = Array.Empty<string>();
		if (!string.IsNullOrEmpty(guid)
			&& unityLabelsByGuid != null
			&& unityLabelsByGuid.TryGetValue(guid, out var resolvedLabels))
		{
			unityLabels = resolvedLabels;
		}
		else if (unityLabelsByGuid == null && GUID.TryParse(guid, out var typedGuid))
		{
			// Same GUID-keyed read as the snapshot: a folder's labels never
			// require its DefaultAsset to be loaded.
				ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseLabelQueries);
				unityLabels = AssetDatabase.GetLabels(typedGuid);
		}

		return new AssetFolderRecord(AssetPath.From(folderPath), guid, unityLabels);
	}
}

}
