using System;
using Zep.Core.Domain;

namespace Zep.Core.State
{

[Serializable]
public sealed class TagDefinitionRecord
{
	public string Id = string.Empty;

	public string Name = string.Empty;

	public ZepTagColor Color = ZepTagColor.None;

	public bool IsFavorite = true;
}

}
