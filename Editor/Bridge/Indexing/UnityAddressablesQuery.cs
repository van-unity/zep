using System;
using System.Collections.Generic;
using Zep.Core.Application.Ports;
using Zep.Core.Indexing;

namespace Zep.Editor.Bridge.Indexing
{

internal sealed class UnityAddressablesQuery : IAddressablesQuery
{
	public bool IsInstalled => ZepAddressableUtility.IsInstalled;

	// The utility's subscription is static and lives for the domain; this
	// instance-level event is what the controller can unsubscribe from on
	// Dispose, so a closed panel never keeps its controller alive through
	// the Addressables settings object.
	public event Action Changed
	{
		add => ZepAddressableUtility.SettingsModified += value;
		remove => ZepAddressableUtility.SettingsModified -= value;
	}

	public AddressablesCapture Capture()
	{
		return ZepAddressableUtility.Capture();
	}

	public HashSet<string> Expand(
		AddressablesCapture capture,
		IReadOnlyList<AssetRecord> records)
	{
		return AddressablesCapture.Expand(capture, records);
	}
}

}
