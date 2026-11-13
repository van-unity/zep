using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Application;
using Zep.Core.Application.Ports;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using UnityEditor;

namespace Zep.Editor.Bridge.Indexing
{

internal static class ZepAddressableUtility
{
	// The default-object class sits in the package's ROOT editor namespace
	// (UnityEditor.AddressableAssets), one level above the .Settings namespace
	// that holds AddressableAssetSettings and the groups. Naming it under
	// .Settings resolved to null on every Addressables release, which read as
	// "package absent": no asset was ever reported addressable.
	private static readonly Type SettingsDefaultObjectType = Type.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject, Unity.Addressables.Editor");

	private static readonly Type SettingsType = Type.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings, Unity.Addressables.Editor");

	private static readonly PropertyInfo SettingsProperty = SettingsDefaultObjectType?.GetProperty("Settings", BindingFlags.Static | BindingFlags.Public);

	private static event Action SettingsModifiedCore;

	private static bool _modificationHookAttempted;

	internal static bool IsInstalled => SettingsDefaultObjectType != null;

	// Fires on the editor thread for every AddressableAssetSettings
	// modification event. The package's static event is hooked once, on the
	// first subscriber, and never unhooked: the hook is a static method, so it
	// keeps nothing alive.
	internal static event Action SettingsModified
	{
		add
		{
			EnsureModificationHook();
			SettingsModifiedCore += value;
		}
		remove => SettingsModifiedCore -= value;
	}

	private static void EnsureModificationHook()
	{
		if (_modificationHookAttempted)
		{
			return;
		}

		_modificationHookAttempted = true;
		var modificationEvent = SettingsType?.GetEvent("OnModificationGlobal", BindingFlags.Static | BindingFlags.Public);
		var handlerType = modificationEvent?.EventHandlerType;
		if (handlerType == null || !handlerType.IsGenericType)
		{
			return;
		}

		// Action<AddressableAssetSettings, ModificationEvent, object>: two of
		// the three argument types are the package's own, so the handler is a
		// generic method closed over whatever the event declares.
		var argumentTypes = handlerType.GetGenericArguments();
		var handlerTemplate = typeof(ZepAddressableUtility).GetMethod(
			nameof(OnSettingsModified),
			BindingFlags.Static | BindingFlags.NonPublic);
		if (argumentTypes.Length != 3 || handlerTemplate == null)
		{
			return;
		}

		try
		{
			var handler = Delegate.CreateDelegate(
				handlerType,
				handlerTemplate.MakeGenericMethod(argumentTypes));
			modificationEvent.AddEventHandler(null, handler);
		}
		catch (ArgumentException)
		{
			// The event's shape drifted: the badge stays as captured at
			// startup, which is the pre-hook behaviour.
		}
	}

	private static void OnSettingsModified<TSettings, TEvent, TData>(TSettings settings, TEvent modificationEvent, TData data)
	{
		SettingsModifiedCore?.Invoke();
	}

	internal static AddressablesCapture Capture()
	{
		var directGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var addressableFolderPaths = new List<string>();
		if (SettingsDefaultObjectType == null)
		{
			// The Addressables editor package is absent: empty is authoritative.
			return new AddressablesCapture(
				Array.Empty<string>(),
				Array.Empty<string>(),
				isComplete: true);
		}

		if (SettingsProperty == null)
		{
			return new AddressablesCapture(
				Array.Empty<string>(),
				Array.Empty<string>(),
				isComplete: false);
		}

		object settings = GetSettings();
		if (settings == null)
		{
			return new AddressablesCapture(
				Array.Empty<string>(),
				Array.Empty<string>(),
				isComplete: true);
		}

		var complete = TryCollectAddressableEntries(
			settings,
			directGuids,
			addressableFolderPaths);
		return new AddressablesCapture(
			directGuids.ToArray(),
			addressableFolderPaths
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray(),
			complete);
	}

	private static object GetSettings()
	{
		if (SettingsProperty == null)
		{
			return null;
		}
		try
		{
			return SettingsProperty.GetValue(null, null);
		}
		catch
		{
			return null;
		}
	}

	private static bool TryCollectAddressableEntries(object settings, HashSet<string> addressableGuids, List<string> addressableFolderPaths)
	{
		if (!TryGetEnumerableMember(settings, out var values, "groups", "Groups"))
		{
			return false;
		}
		bool result = false;
		foreach (object item in values)
		{
			if (item == null)
			{
				continue;
			}
			if (!TryGetEnumerableMember(item, out var values2, "entries", "Entries"))
			{
				return false;
			}
			result = true;
			foreach (object item2 in values2)
			{
				if (item2 == null)
				{
					continue;
				}
				string entryGuid = GetEntryGuid(item2);
				if (string.IsNullOrWhiteSpace(entryGuid))
				{
					continue;
				}
				string entryAssetPath = GetEntryAssetPath(item2, entryGuid);
				if (!string.IsNullOrWhiteSpace(entryAssetPath))
				{
					addressableGuids.Add(entryGuid);
					if (AssetDatabase.IsValidFolder(entryAssetPath))
					{
						addressableFolderPaths.Add(NormalizeAssetPath(entryAssetPath));
					}
				}
			}
		}
		return result;
	}

	private static bool TryGetEnumerableMember(object instance, out IEnumerable values, params string[] memberNames)
	{
		values = GetMemberValue(instance, memberNames) as IEnumerable;
		return values != null;
	}

	private static object GetMemberValue(object instance, params string[] memberNames)
	{
		if (instance == null || memberNames == null || memberNames.Length == 0)
		{
			return null;
		}
		Type type = instance.GetType();
		foreach (string text in memberNames)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			PropertyInfo property = type.GetProperty(text, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (property != null)
			{
				try
				{
					return property.GetValue(instance, null);
				}
				catch
				{
					return null;
				}
			}
			FieldInfo field = type.GetField(text, BindingFlags.IgnoreCase | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				try
				{
					return field.GetValue(instance);
				}
				catch
				{
					return null;
				}
			}
		}
		return null;
	}

	private static string GetEntryGuid(object entry)
	{
		return (GetMemberValue(entry, "guid", "Guid") as string) ?? string.Empty;
	}

	private static string GetEntryAssetPath(object entry, string guid)
	{
		string entryAssetPath = GetMemberValue(entry, "AssetPath", "assetPath") as string;
		if (!string.IsNullOrWhiteSpace(entryAssetPath))
		{
			return entryAssetPath;
		}
		if (!string.IsNullOrWhiteSpace(guid))
		{
			ZepPerfLog.Count(ZepPerfCounter.AssetDatabaseGuidQueries);
		}
		return string.IsNullOrWhiteSpace(guid) ? string.Empty : AssetDatabase.GUIDToAssetPath(guid);
	}

	private static string NormalizeAssetPath(string assetPath)
	{
		return AssetPathFormat.Normalize(assetPath);
	}

}

}
