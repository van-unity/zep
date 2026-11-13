using System;
using System.Collections.Generic;

namespace Zep.Core.Indexing
{
    /// <summary>
    /// The searchable Unity type identity of one asset. <see cref="TypeName"/> is the
    /// concrete main-object type; <see cref="BaseTypeNames"/> contains its real CLR base
    /// classes, so broad type filters never have to guess inheritance from a file extension.
    /// </summary>
    public sealed class AssetTypeMetadata
    {
        public AssetTypeMetadata(string typeName, IReadOnlyList<string> baseTypeNames = null)
        {
            TypeName = string.IsNullOrWhiteSpace(typeName)
                ? "Unknown"
                : typeName.Trim();
            BaseTypeNames = CopyDistinctBaseTypes(TypeName, baseTypeNames);
        }

        internal string TypeName { get; }

        public IReadOnlyList<string> BaseTypeNames { get; }

        private static string[] CopyDistinctBaseTypes(
            string concreteTypeName,
            IReadOnlyList<string> baseTypeNames)
        {
            if (baseTypeNames == null || baseTypeNames.Count == 0)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>(baseTypeNames.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                concreteTypeName,
            };
            for (var i = 0; i < baseTypeNames.Count; i++)
            {
                var value = baseTypeNames[i];
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                value = value.Trim();
                if (seen.Add(value))
                {
                    result.Add(value);
                }
            }

            return result.Count == 0
                ? Array.Empty<string>()
                : result.ToArray();
        }
    }
}
