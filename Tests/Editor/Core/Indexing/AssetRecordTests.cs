using System.Collections.Generic;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Indexing
{
    public sealed class AssetRecordTests
    {
        [Test]
        public void Constructor_NormalizesSearchFields()
        {
            var record = new AssetRecord(
                "ABC-123",
                @"Assets\Art\Player.PNG",
                "Player",
                "Texture2D",
                "Urgent UI",
                new[] { " Rele\u0301ase ", "Gameplay" });

            Assert.That(record.NormalizedGuid, Is.EqualTo("abc-123"));
            Assert.That(record.NormalizedAssetPath, Is.EqualTo("assets/art/player.png"));
            Assert.That(record.NormalizedName, Is.EqualTo("player"));
            Assert.That(record.NormalizedTypeName, Is.EqualTo("texture2d"));
            Assert.That(record.NormalizedExtension, Is.EqualTo("png"));
            Assert.That(record.NormalizedTypeSearchText.Split('\n'),
                Is.SupersetOf(new[] { "texture2d", "file", "object", "image" }));
            Assert.That(record.NormalizedTagSearchText, Is.EqualTo("urgent ui"));
            Assert.That(record.UnityLabels, Is.EqualTo(new[] { "Rele\u0301ase", "Gameplay" }));
            Assert.That(record.NormalizedUnityLabels, Is.EqualTo(new[] { "rel\u00e9ase", "gameplay" }));
        }

        [Test]
        public void Equals_IsValueBased()
        {
            var left = new AssetRecord(
                "guid-1",
                "Assets/A.asset",
                "A",
                "ScriptableObject",
                "Tag",
                new[] { "Label A", "Label B" });
            var right = new AssetRecord(
                "guid-1",
                "Assets/A.asset",
                "A",
                "ScriptableObject",
                "Tag",
                new[] { "Label A", "Label B" });
            var differentLabels = new AssetRecord(
                "guid-1",
                "Assets/A.asset",
                "A",
                "ScriptableObject",
                "Tag",
                new[] { "Label A" });

            Assert.That(left, Is.EqualTo(right));
            Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
            Assert.That(left, Is.Not.EqualTo(differentLabels));
        }

        [Test]
        public void Constructor_DefensivelyCopiesAndFiltersUnityLabels()
        {
            var labels = new List<string> { " First ", string.Empty, null, "Second" };

            var record = new AssetRecord(
                "guid-1",
                "Assets/A.prefab",
                "A",
                "GameObject",
                unityLabels: labels);
            labels[0] = "Mutated";
            labels.Add("Late");

            Assert.That(record.UnityLabels, Is.EqualTo(new[] { "First", "Second" }));
            Assert.That(record.NormalizedUnityLabels, Is.EqualTo(new[] { "first", "second" }));
        }

        [Test]
        public void Constructor_UsesRealBaseTypesInsteadOfAssetExtensionGuessing()
        {
            var scriptable = new AssetRecord(
                "scriptable-guid",
                "Assets/EnemyConfig.custom",
                "EnemyConfig",
                "EnemyConfig",
                baseTypeNames: new[] { "GameConfig", "ScriptableObject" });
            var mesh = new AssetRecord(
                "mesh-guid",
                "Assets/SerializedMesh.asset",
                "SerializedMesh",
                "Mesh");

            Assert.That(
                scriptable.NormalizedTypeSearchText.Split('\n'),
                Is.SupersetOf(new[] { "enemyconfig", "gameconfig", "scriptableobject" }));
            Assert.That(
                mesh.NormalizedTypeSearchText.Split('\n'),
                Does.Not.Contain("scriptableobject"),
                "the .asset suffix alone does not prove a ScriptableObject base type");
        }

        [Test]
        public void TypeMetadata_DefensivelyCopiesAndDeduplicatesBaseTypes()
        {
            var baseTypes = new List<string>
            {
                " ScriptableObject ",
                "scriptableobject",
                "EnemyConfig",
                string.Empty,
            };

            var metadata = new AssetTypeMetadata(" EnemyConfig ", baseTypes);
            baseTypes[0] = "Mutated";

            Assert.That(metadata.TypeName, Is.EqualTo("EnemyConfig"));
            Assert.That(metadata.BaseTypeNames, Is.EqualTo(new[] { "ScriptableObject" }));
        }

        [Test]
        public void FolderRecord_DefensivelyCopiesAndFiltersUnityLabels()
        {
            var labels = new List<string> { " Folder Label ", string.Empty, null };

            var record = new AssetFolderRecord(
                AssetPath.From("Assets/Folder"),
                "folder-guid",
                labels);
            labels[0] = "Mutated";
            labels.Add("Late");

            Assert.That(record.FolderPath, Is.EqualTo(AssetPath.From("Assets/Folder")));
            Assert.That(record.Guid, Is.EqualTo("folder-guid"));
            Assert.That(record.UnityLabels, Is.EqualTo(new[] { "Folder Label" }));
        }

        [TestCase("Assets/Player.prefab", "GameObject", "prefab")]
        [TestCase("Assets/Player.fbx", "GameObject", "model")]
        [TestCase("Assets/Main.unity", "SceneAsset", "scene")]
        [TestCase("Assets/Player.cs", "MonoScript", "script")]
        public void Constructor_BuildsUnityKindAliasesFromPath(
            string path,
            string typeName,
            string expectedAlias)
        {
            var record = new AssetRecord("guid", path, "Name", typeName);

            Assert.That(record.NormalizedTypeSearchText.Split('\n'), Has.Member(expectedAlias));
            Assert.That(record.NormalizedTypeSearchText.Split('\n'), Has.Member("file"));
            Assert.That(record.NormalizedTypeSearchText.Split('\n'), Has.Member("object"));
        }

        [Test]
        public void Normalization_AliasesAlreadyNormalisedInputInsteadOfCopyingIt()
        {
            // Fresh instances, not literals: literal interning would make the
            // reference assertions pass even if normalisation still copied.
            var guid = Duplicate("2f3ab41c9d5e40728a1b6c0fd4e5f6a7");
            var path = Duplicate("assets/art/player.png");
            var name = Duplicate("player");

            var record = new AssetRecord(guid, path, name, "Texture2D");

            Assert.That(record.NormalizedGuid, Is.EqualTo(guid));
            Assert.That(
                ReferenceEquals(record.Guid, record.NormalizedGuid),
                Is.True,
                "a lower-case GUID normalises to itself and must not be copied");
            Assert.That(record.NormalizedAssetPath, Is.EqualTo(path));
            Assert.That(
                ReferenceEquals(record.AssetPath, record.NormalizedAssetPath),
                Is.True,
                "a lower-case forward-slash path normalises to itself");
            Assert.That(
                ReferenceEquals(record.Name, record.NormalizedName),
                Is.True,
                "a lower-case name normalises to itself");
        }

        [Test]
        public void Normalization_StillLowersMixedCaseAndFoldsNonAscii()
        {
            var mixedCaseName = Duplicate("PlayerHUD");
            var record = new AssetRecord(
                Duplicate("A1B2C3D4"),
                Duplicate(@"Assets\Art\Player.PNG"),
                mixedCaseName,
                "Texture2D",
                "Urgent UI");

            Assert.That(record.NormalizedName, Is.EqualTo("playerhud"));
            Assert.That(
                ReferenceEquals(record.Name, record.NormalizedName),
                Is.False,
                "mixed case must produce a lowered copy, not an alias");
            Assert.That(record.NormalizedGuid, Is.EqualTo("a1b2c3d4"));
            Assert.That(record.NormalizedAssetPath, Is.EqualTo("assets/art/player.png"));
            Assert.That(record.NormalizedTagSearchText, Is.EqualTo("urgent ui"));

            // Decomposed "Cafe" + combining acute must still compose to FormC and lower.
            var decomposed = new AssetRecord(
                "guid-nonascii",
                "Assets/Cafe\u0301.asset",
                "Cafe\u0301",
                "ScriptableObject");

            Assert.That(decomposed.NormalizedName, Is.EqualTo("caf\u00e9"));
            Assert.That(decomposed.NormalizedAssetPath, Is.EqualTo("assets/caf\u00e9.asset"));
        }

        [Test]
        public void NormalizedTypeSearchText_IsSharedByRecordsWithTheSameTypeShape()
        {
            var first = new AssetRecord(
                "guid-first",
                "Assets/Art/First.png",
                "First",
                "Texture2D",
                baseTypeNames: new[] { "Texture", "Object" });
            var second = new AssetRecord(
                "guid-second",
                "Assets/Audio/Second.png",
                "Second",
                "Texture2D",
                baseTypeNames: new[] { "Texture", "Object" });
            var differentType = new AssetRecord(
                "guid-third",
                "Assets/Art/Third.png",
                "Third",
                "Sprite",
                baseTypeNames: new[] { "Texture", "Object" });

            Assert.That(
                ReferenceEquals(first.NormalizedTypeSearchText, second.NormalizedTypeSearchText),
                Is.True,
                "the same type/extension/base-type shape must share one interned instance");
            Assert.That(
                differentType.NormalizedTypeSearchText,
                Is.Not.EqualTo(first.NormalizedTypeSearchText),
                "interning must not collapse distinct type shapes");
        }

        [Test]
        public void WithAssetPath_MatchesAFullyConstructedRecordOnEveryProperty()
        {
            var source = BuildSourceRecord();

            // The destination deliberately changes the EXTENSION too (png -> fbx,
            // i.e. the "image" kind alias becomes "model"), so a copy that carried
            // NormalizedExtension or NormalizedTypeSearchText across would fail.
            var moved = AssetRecord.WithAssetPath(source, "Assets/Visuals/Sub/Hero.fbx");
            var expected = new AssetRecord(
                source.Guid,
                "Assets/Visuals/Sub/Hero.fbx",
                source.Name,
                source.TypeName,
                source.TagSearchText,
                source.UnityLabels,
                source.BaseTypeNames);

            AssertEveryPropertyMatches(expected, moved);
            Assert.That(moved.NormalizedTypeSearchText.Split('\n'), Has.Member("model"));
            Assert.That(moved.NormalizedTypeSearchText.Split('\n'), Has.No.Member("image"));
        }

        [Test]
        public void WithTagSearchText_MatchesAFullyConstructedRecordOnEveryProperty()
        {
            var source = BuildSourceRecord();

            var retagged = AssetRecord.WithTagSearchText(source, "Blocker Art");
            var expected = new AssetRecord(
                source.Guid,
                source.AssetPath,
                source.Name,
                source.TypeName,
                "Blocker Art",
                source.UnityLabels,
                source.BaseTypeNames);

            AssertEveryPropertyMatches(expected, retagged);
            Assert.That(retagged.NormalizedTagSearchText, Is.EqualTo("blocker art"));
            Assert.That(
                retagged.NormalizedSearchText.Split('\n'),
                Has.Member("blocker art"),
                "the any-field text carries the new tags");
        }

        private static AssetRecord BuildSourceRecord()
        {
            return new AssetRecord(
                "2f3ab41c9d5e40728a1b6c0fd4e5f6a7",
                "Assets/Art/Sub/Hero.PNG",
                "Hero",
                "Texture2D",
                "Urgent UI",
                new[] { "Gameplay", "Rele\u0301ase" },
                new[] { "Texture", "Object" });
        }

        private static void AssertEveryPropertyMatches(
            AssetRecord expected,
            AssetRecord actual)
        {
            // Internal properties count too: the normalized search fields are
            // internal to Core, and they are exactly what a copy can get wrong.
            var properties = typeof(AssetRecord)
                .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(
                properties.Length,
                Is.EqualTo(18),
                "AssetRecord gained or lost a property - the copy constructor "
                + "decides for every one of them whether it is carried or recomputed");

            for (var i = 0; i < properties.Length; i++)
            {
                var property = properties[i];
                Assert.That(
                    property.GetValue(actual),
                    Is.EqualTo(property.GetValue(expected)),
                    property.Name);
            }
        }

        private static string Duplicate(string value)
        {
            return new string(value.ToCharArray());
        }
    }
}
