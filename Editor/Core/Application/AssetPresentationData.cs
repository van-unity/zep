using System;

namespace Zep.Core.Application
{

public readonly struct AssetPresentationData
{
	public DateTime? ModifiedDate { get; }

	public long? Size { get; }

	public bool IsAddressable { get; }

	internal AssetPresentationData(DateTime? modifiedDate, long? size, bool isAddressable)
	{
		ModifiedDate = modifiedDate;
		Size = size;
		IsAddressable = isAddressable;
	}
}

}
