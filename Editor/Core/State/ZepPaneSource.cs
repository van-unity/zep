using System;
using Zep.Core.Domain;
using Zep.Core.FileOperations;

namespace Zep.Core.State
{
    public enum ZepPaneSourceKind
    {
        Primary,
        Folder,
        Tag
    }

    public readonly struct ZepPaneSource : IEquatable<ZepPaneSource>
    {
        internal static ZepPaneSource Primary { get; } =
            new ZepPaneSource(ZepPaneSourceKind.Primary, default, string.Empty);

        private ZepPaneSource(
            ZepPaneSourceKind kind,
            AssetPath rootFolderPath,
            string tagId)
        {
            Kind = kind;
            RootFolderPath = rootFolderPath;
            TagId = tagId ?? string.Empty;
        }

        public ZepPaneSourceKind Kind { get; }

        public AssetPath RootFolderPath { get; }

        public string TagId { get; }

        public bool IsPrimary => Kind == ZepPaneSourceKind.Primary;

        public bool IsAuxiliary => !IsPrimary;

        public static ZepPaneSource RootedFolder(AssetPath rootFolderPath)
        {
            if (!AssetPathClassification.IsProjectAssetPath(rootFolderPath))
            {
                throw new ArgumentException(
                    "A rooted pane requires an Assets or Packages folder path.",
                    nameof(rootFolderPath));
            }

            return new ZepPaneSource(
                ZepPaneSourceKind.Folder,
                rootFolderPath,
                string.Empty);
        }

        public static ZepPaneSource FixedTag(string tagId)
        {
            if (string.IsNullOrWhiteSpace(tagId))
            {
                throw new ArgumentException("A tag pane requires a tag id.", nameof(tagId));
            }

            return new ZepPaneSource(
                ZepPaneSourceKind.Tag,
                default,
                tagId);
        }

        public bool Equals(ZepPaneSource other)
        {
            return Kind == other.Kind
                && RootFolderPath == other.RootFolderPath
                && string.Equals(TagId, other.TagId, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object obj)
        {
            return obj is ZepPaneSource other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Kind;
                hash = (hash * 397) ^ RootFolderPath.GetHashCode();
                hash = (hash * 397)
                    ^ (TagId != null ? StringComparer.OrdinalIgnoreCase.GetHashCode(TagId) : 0);
                return hash;
            }
        }

        public static bool operator ==(ZepPaneSource left, ZepPaneSource right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ZepPaneSource left, ZepPaneSource right)
        {
            return !left.Equals(right);
        }
    }
}
