using System.Linq;
using Zep.Core.Application;
using Zep.Core.Indexing;
using Zep.Core.Search;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Search
{
    internal static class SearchCatalogTestMutations
    {
        internal static void Upsert(
            InMemoryAssetIndex index,
            AssetSearchService search,
            AssetRecord record)
        {
            search.ApplyCatalogDelta(index.ApplyFileChanges(
                new[] { record },
                System.Array.Empty<string>()));
        }

        internal static void Remove(
            InMemoryAssetIndex index,
            AssetSearchService search,
            string guid)
        {
            if (!index.TryGet(guid, out var record))
            {
                return;
            }

            search.ApplyCatalogDelta(index.ApplyFileChanges(
                System.Array.Empty<AssetRecord>(),
                new[] { record.AssetPath }));
        }
    }

    public sealed class AssetSearchServiceTests
    {
        [Test]
        public void Search_MatchesNameTypeAndTag_ButNotTheParentPath()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Characters/Player.prefab", "Player", "Prefab"));
            index.Upsert(new AssetRecord("b", "Assets/UI/HUD.uxml", "HUD", "VisualTreeAsset"));
            index.Upsert(new AssetRecord("c", "Assets/Data/GameSettings.asset", "GameSettings", "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("player").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            // Unqualified search matches an asset's own NAME, NOT its parent folders:
            // "characters" is only in a's PATH (Assets/Characters/Player.prefab), so it must
            // NOT match. This is the fix for the user-reported "searching plugins returns
            // everything under Plugins/" bug. Use `path:` to search the path explicitly.
            Assert.That(search.Search("characters"), Is.Empty);
            Assert.That(search.Search("path:characters").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("visualtreeasset").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
            Assert.That(search.Search("scriptableobject").Select(x => x.Guid), Is.EqualTo(new[] { "c" }));
            Assert.That(search.Search("type:prefab").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("name:hud path:ui").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void Search_DoesNotMatchAnAssetByItsParentFolderName()
        {
            // Regression (user-reported): searching "plugins" surfaced every asset living
            // under a Plugins/ folder because the unqualified match folded in the full path.
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("under", "Assets/Plugins/Android/Thing.cs", "Thing", "MonoScript"));
            index.Upsert(new AssetRecord("named", "Assets/PluginsConfig.asset", "PluginsConfig", "ScriptableObject"));

            var search = new AssetSearchService(index);

            // "plugins" is only in "under"'s PATH → must NOT match. It IS in "named"'s NAME → matches.
            Assert.That(search.Search("plugins").Select(x => x.Guid), Is.EqualTo(new[] { "named" }));
            // Explicit path search still finds every asset whose path contains the term.
            Assert.That(search.Search("path:plugins").Select(x => x.Guid).OrderBy(g => g),
                Is.EqualTo(new[] { "named", "under" }));
        }

        [Test]
        public void Search_IsCaseInsensitive()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Characters/Player.prefab", "Player", "Prefab"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("PLAYER").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("path:ASSETS/CHARACTERS").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
        }

        [Test]
        public void Search_MatchesFinderTagsAcrossAnyFieldAndTagField()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Characters/Player.prefab", "Player", "Prefab", "Red Urgent"));
            index.Upsert(new AssetRecord("b", "Assets/UI/PlayerHUD.uxml", "PlayerHUD", "VisualTreeAsset", "Blue UI"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("urgent").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("tag:blue").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void Search_RequiresAllTokensToMatch()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Characters/Player.prefab", "Player", "Prefab"));
            index.Upsert(new AssetRecord("b", "Assets/UI/PlayerHUD.uxml", "PlayerHUD", "VisualTreeAsset"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("player prefab").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            // Both tokens must match. a fails (path lacks "ui"); b matches (name
            // "PlayerHUD" contains "player", path "Assets/UI/..." contains "ui").
            // The substring semantics on `name:` are confirmed by the
            // Search_MatchesNameTypeAndTag_ButNotTheParentPath test above ("name:hud" → b).
            Assert.That(search.Search("name:player path:ui").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void Search_EmptyQuery_ReturnsAllRecordsAlphabetically()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/A.asset", "A", "ScriptableObject"));
            index.Upsert(new AssetRecord("b", "Assets/B.asset", "B", "Texture2D"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search(string.Empty).Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(search.Search("   ").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Search_UsesScopedFolderAndTypeFilters()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Art/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("b", "Assets/Art/Sub/Icon.prefab", "Icon", "GameObject"));
            index.Upsert(new AssetRecord("c", "Assets/Scripts/IconBuilder.cs", "IconBuilder", "MonoScript"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("icon", "Assets/Art", "Texture2D").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search(string.Empty, "Assets/Art", "GameObject").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void Search_TracksIndexChangesWithoutRecreatingService()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Characters/Player.prefab", "Player", "Prefab"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("player").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));

            index.Upsert(new AssetRecord("b", "Assets/UI/PlayerHUD.uxml", "PlayerHUD", "VisualTreeAsset"));
            Assert.That(search.Search("player").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));

            index.Remove("a");
            Assert.That(search.Search("player").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void Search_SupportsShortTokensAndGuidMatches()
        {
            // Real 32-hex GUIDs, because since 2026-09-02 the engine reads a GUID's
            // HEAD: an unqualified term matches a prefix of at least four characters,
            // and only the first eight characters are gram-indexed. A synthetic GUID
            // whose meaningful part sits at its tail would test nothing the product does.
            const string playerGuid = "7a1c93b40de5f2681a4c9d8e3b7f0526";
            const string menuGuid = "c4e8102f5b63a97d0e2f81c6b4a35d90";
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(playerGuid, "Assets/Characters/Player.prefab", "Player", "Prefab"));
            index.Upsert(new AssetRecord(menuGuid, "Assets/UI/Menu.uxml", "Menu", "VisualTreeAsset"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("pl").Select(x => x.Guid), Is.EqualTo(new[] { playerGuid }));
            Assert.That(search.Search("7a1c").Select(x => x.Guid), Is.EqualTo(new[] { playerGuid }));
            Assert.That(
                search.Search("93b4"),
                Is.Empty,
                "an unqualified GUID term is a prefix, not a substring");
            Assert.That(
                search.Search("guid:93b4").Select(x => x.Guid),
                Is.EqualTo(new[] { playerGuid }),
                "`guid:` still matches inside the indexed head");
        }

        // §3.11(a). A 32-hex GUID used to be folded into every record's any-field search
        // text, so any two hex-ish characters matched about half a project (measured:
        // "ab" hit 43% of a 100k-record corpus). An unqualified term now reads the GUID
        // as a PREFIX of at least four characters and nothing else; `guid:` is unchanged.
        [Test]
        public void Search_UnqualifiedTerm_ReadsAGuidOnlyAsAPrefixOfAtLeastFourCharacters()
        {
            const string prefixedGuid = "2f3ab41c9d0e4f5a8b7c6d5e4f3a2b1c";
            var index = new InMemoryAssetIndex();
            // "ab" at the head of a GUID, and nowhere in the record's own text.
            index.Upsert(new AssetRecord(
                "ab77c0de1234567890abcdef01234567",
                "Assets/Art/Rock.png",
                "Rock",
                "Texture2D"));
            // "ab" in the middle of a GUID, and nowhere in the record's own text.
            index.Upsert(new AssetRecord(
                "77ab5c1d2e3f40516273849506172839",
                "Assets/Art/Stone.png",
                "Stone",
                "Texture2D"));
            // The three legs that must still answer "ab": name, Zep tag, Unity label.
            index.Upsert(new AssetRecord(
                "11223344556677889900aabbccddeeff",
                "Assets/Props/Table.prefab",
                "Table",
                "GameObject"));
            index.Upsert(new AssetRecord(
                "3344556677889900aabbccddeeff1122",
                "Assets/Art/Cloth.png",
                "Cloth",
                "Texture2D",
                "Fabric"));
            index.Upsert(new AssetRecord(
                "5566778899001122334455667788990a",
                "Assets/Art/Beaker.png",
                "Beaker",
                "Texture2D",
                string.Empty,
                new[] { "Lab" }));
            index.Upsert(new AssetRecord(
                prefixedGuid,
                "Assets/Audio/Chime.wav",
                "Chime",
                "AudioClip"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("ab").Select(x => x.Name),
                Is.EquivalentTo(new[] { "Table", "Cloth", "Beaker" }),
                "a two-character term must read name, type, tag and label text only");
            Assert.That(
                search.Search("2f3a").Select(x => x.Guid),
                Is.EqualTo(new[] { prefixedGuid }),
                "four characters is enough to name a record by its GUID head");
            Assert.That(
                search.Search("2f3"),
                Is.Empty,
                "three characters is below the unqualified GUID prefix minimum");
            Assert.That(
                search.Search("b41c"),
                Is.Empty,
                "an unqualified GUID term is a prefix, not a substring");
            Assert.That(
                search.Search("guid:2f").Select(x => x.Guid),
                Is.EqualTo(new[] { prefixedGuid }),
                "`guid:` keeps its partial behaviour at any length");
            Assert.That(
                search.Search("guid:b41c").Select(x => x.Guid),
                Is.EqualTo(new[] { prefixedGuid }),
                "`guid:` keeps matching inside the GUID, not only at its head");
            Assert.That(
                search.Search("guid:" + prefixedGuid).Select(x => x.Guid),
                Is.EqualTo(new[] { prefixedGuid }));
        }

        // §3.11(b)(c). The GUID gram index now carries only the first eight characters
        // of each GUID, and the type field's dedicated one- and two-gram indices are
        // gone — a 1- or 2-character `t:` scans the distinct type texts instead. Both
        // legs must keep producing the same answers they produced from the removed
        // postings, and the deliberate narrowing on the GUID side is pinned here too.
        [Test]
        public void Search_ShortTypeQueriesAndGuidPrefixes_ResolveWithoutTheRemovedIndices()
        {
            const string chimeGuid = "2f3ab41c9d0e4f5a8b7c6d5e4f3a2b1c";
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                chimeGuid,
                "Assets/Audio/Chime.wav",
                "Chime",
                "AudioClip"));
            index.Upsert(new AssetRecord(
                "1122334455667788990aabbccddeeff0",
                "Assets/Art/Rock.png",
                "Rock",
                "Texture2D"));
            index.Upsert(new AssetRecord(
                "99887766554433221100ffeeddccbbaa",
                "Assets/Data/Config.asset",
                "Config",
                "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("t:cl").Select(x => x.Name),
                Is.EqualTo(new[] { "Chime" }),
                "a two-character t: must still resolve (AudioClip)");
            Assert.That(
                search.Search("t:2").Select(x => x.Name),
                Is.EqualTo(new[] { "Rock" }),
                "a one-character t: must still resolve (Texture2D)");
            Assert.That(search.Search("t:zq"), Is.Empty);
            Assert.That(
                search.Search("t:texture2d").Select(x => x.Name),
                Is.EqualTo(new[] { "Rock" }),
                "the trigram type leg is untouched");

            Assert.That(
                search.Search("guid:2f3ab41c9d").Select(x => x.Guid),
                Is.EqualTo(new[] { chimeGuid }),
                "a guid: prefix longer than the indexed head resolves from the head and "
                    + "is verified against the whole GUID");
            Assert.That(
                search.Search("guid:" + chimeGuid).Select(x => x.Guid),
                Is.EqualTo(new[] { chimeGuid }),
                "a full 32-character guid: still resolves");
            // DELIBERATE NARROWING, pinned so it cannot change silently: a `guid:`
            // fragment that lies past the first eight characters no longer resolves.
            Assert.That(
                search.Search("guid:6d5e4f3a"),
                Is.Empty,
                "only the head of a GUID is gram-indexed");
        }

        [TestCase("cold")]
        [TestCase("delta")]
        [TestCase("worker")]
        public void Search_GuidBoundaryAndNegationAgreeAcrossIndexLifecycles(string lifecycle)
        {
            const string guid = "012345670123456789abcdefabcdefab";
            var record = new AssetRecord(guid, "Assets/Alpha.asset", "Alpha", "Object");
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            if (lifecycle == "delta")
            {
                index.Upsert(new AssetRecord(guid, record.AssetPath, "Before", "Object"));
                search.WarmIndex();
                SearchCatalogTestMutations.Upsert(index, search, record);
            }
            else
            {
                index.Upsert(record);
                if (lifecycle == "worker")
                {
                    Assert.That(search.TryAdoptWorkerBuild(AssetSearchService.BuildOnWorker(
                        new[] { record }, index.RecordVersion)), Is.True);
                }
            }

            foreach (var (query, matches) in new[]
            {
                ("guid:f", false),
                ("guid:ef", false),
                ("guid:cdef", false),
                ("guid:6701", false),
                ("guid:0123456789", false),
                ("guid:0", true),
                ("guid:67", true),
                ("guid:3456", true),
                ("guid:01234567", true),
                ("guid:0123456701", true),
                ("guid:" + guid.ToUpperInvariant(), true),
                ("guid=01234567", false),
                ("guid=" + guid, true),
                ("guid=" + guid.Substring(0, 31) + "0", false),
            })
            {
                Assert.That(search.Search(query).Select(asset => asset.Guid),
                    Is.EqualTo(matches ? new[] { guid } : System.Array.Empty<string>()), lifecycle + ": " + query);
                Assert.That(search.Search("-" + query).Select(asset => asset.Guid),
                    Is.EqualTo(matches ? System.Array.Empty<string>() : new[] { guid }), lifecycle + ": -" + query);
            }
        }

        // 4.36. Search fills a REUSED ordinal buffer, so the array each result list is
        // handed has to be that list's own. A split view keeps two panes' result lists
        // live over one AssetSearchService, and either pane can repaint after the other
        // pane has searched — an implementation that always returned the shared buffer
        // would rewrite the first list's results under it. The three searches below
        // cover both hand-off branches: the first fills the buffer (it takes it), the
        // second and third are sparse enough to be copied out of it, and the third
        // rents the very buffer the second one filled.
        [Test]
        public void Search_EarlierResultLists_SurviveLaterSearchesThatReuseTheBuffer()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 200; i++)
            {
                var n = i.ToString("D3");
                index.Upsert(new AssetRecord(
                    "guid-" + n,
                    "Assets/Art/alpha_" + n + ".png",
                    "alpha_" + n,
                    "Texture2D"));
            }

            index.Upsert(new AssetRecord(
                "guid-zebra",
                "Assets/Art/Zebra.png",
                "Zebra",
                "Texture2D"));

            var search = new AssetSearchService(index);

            var all = search.Search("alpha");
            var excluded = search.Search("alpha -alpha_1 -alpha_09");
            var single = search.Search("zebra");

            Assert.That(all.Count, Is.EqualTo(200));
            Assert.That(excluded.Count, Is.EqualTo(90));
            Assert.That(single.Count, Is.EqualTo(1));

            // Read every list AFTER all three exist: this is the split-view repaint.
            Assert.That(all[0].Name, Is.EqualTo("alpha_000"));
            Assert.That(all[199].Name, Is.EqualTo("alpha_199"));
            Assert.That(excluded[0].Name, Is.EqualTo("alpha_000"));
            Assert.That(excluded[89].Name, Is.EqualTo("alpha_089"));
            Assert.That(single[0].Name, Is.EqualTo("Zebra"));
            Assert.That(
                excluded.Select(x => x.Name).Last(),
                Is.EqualTo("alpha_089"),
                "the second list must not have been rewritten by the third search");
        }

        // Name/path/tag values shorter than the gram length cannot use the trigram
        // index, so they take the scan-all-and-verify fallback. Type and label have
        // dedicated short-gram indices because t:/l: are common Unity query shapes.
        [Test]
        public void Search_FieldScopedShortQuery_KeepsSubstringSemantics()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("g1", "Assets/x/one.png", "ab_one", "Texture2D"));
            index.Upsert(new AssetRecord("g2", "Assets/ab/two.png", "two", "Texture2D"));
            index.Upsert(new AssetRecord("g3", "Assets/y/three.png", "three", "Texture2D", "UI Blue"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("name:ab").Select(x => x.Guid), Is.EqualTo(new[] { "g1" }));
            Assert.That(search.Search("path:ab").Select(x => x.Guid), Is.EqualTo(new[] { "g2" }));
            Assert.That(search.Search("tag:ui").Select(x => x.Guid), Is.EqualTo(new[] { "g3" }));
            Assert.That(search.Search("name:zz"), Is.Empty);
        }

        [Test]
        public void Search_UnityLabelsAndFinderTagsAreDistinctFields()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "a",
                "Assets/One.prefab",
                "One",
                "GameObject",
                "Red Urgent",
                new[] { "Gameplay", "Release Candidate" }));
            index.Upsert(new AssetRecord(
                "b",
                "Assets/Two.png",
                "Two",
                "Texture2D",
                "Gameplay",
                new[] { "Urgent", "Release" }));
            index.Upsert(new AssetRecord(
                "c",
                "Assets/Three.cs",
                "Three",
                "MonoScript",
                unityLabels: new[] { "Gameplay" }));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("l:urgent").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
            Assert.That(search.Search("label:gameplay").Select(x => x.Guid), Is.EqualTo(new[] { "a", "c" }));
            Assert.That(search.Search("tag:urgent").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("tag:gameplay").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
            Assert.That(search.Search("l=release").Select(x => x.Guid), Is.EqualTo(new[] { "b" }),
                "exact labels compare a complete Unity label, not a substring");
            Assert.That(search.Search("l:release").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
        }

        [Test]
        public void Search_Ordering_RemainsAlphabeticalAfterIncrementalUpsertAndRename()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("g-c", "Assets/c.asset", "Charlie", "Prefab"));
            index.Upsert(new AssetRecord("g-a", "Assets/a.asset", "Alpha", "Prefab"));
            index.Upsert(new AssetRecord("g-b", "Assets/b.asset", "Bravo", "Prefab"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search(string.Empty).Select(x => x.Name),
                Is.EqualTo(new[] { "Alpha", "Bravo", "Charlie" }));

            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("g-aa", "Assets/aa.asset", "AlphaAlpha", "Prefab"));

            Assert.That(search.Search(string.Empty).Select(x => x.Name),
                Is.EqualTo(new[] { "Alpha", "AlphaAlpha", "Bravo", "Charlie" }));

            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("g-b", "Assets/aaron.asset", "Aaron", "Prefab"));

            Assert.That(search.Search(string.Empty).Select(x => x.Name),
                Is.EqualTo(new[] { "Aaron", "Alpha", "AlphaAlpha", "Charlie" }));
            Assert.That(search.Search("prefab").Select(x => x.Name),
                Is.EqualTo(new[] { "Aaron", "Alpha", "AlphaAlpha", "Charlie" }));
        }

        [Test]
        public void Search_BroadResultsRemainAlphabeticalAcrossRepeatedIncrementalQueries()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 128; i++)
            {
                var name = "Item" + i.ToString("D3");
                index.Upsert(new AssetRecord("g-" + i, "Assets/" + name + ".prefab", name, "GameObject"));
            }

            var search = new AssetSearchService(index);
            search.WarmIndex();
            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("g-first", "Assets/AFirst.prefab", "AFirst", "GameObject"));
            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("g-64", "Assets/ZLast.prefab", "ZLast", "GameObject"));

            var expected = new[] { "AFirst" }
                .Concat(Enumerable.Range(0, 128)
                    .Where(i => i != 64)
                    .Select(i => "Item" + i.ToString("D3")))
                .Concat(new[] { "ZLast" })
                .ToArray();

            Assert.That(search.Search("t:file").Select(record => record.Name), Is.EqualTo(expected));
            Assert.That(search.Search("t:object").Select(record => record.Name), Is.EqualTo(expected),
                "a second broad query must use the same live alphabetical order after incremental changes");
            Assert.That(search.Search(string.Empty).Select(record => record.Name), Is.EqualTo(expected));
        }

        [Test]
        public void Search_UnityTypeAliasesSeparatePrefabsFromImportedModels()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Player.prefab", "Player", "GameObject"));
            index.Upsert(new AssetRecord("b", "Assets/Robot.fbx", "Robot", "GameObject"));
            index.Upsert(new AssetRecord("c", "Assets/Arena.unity", "Arena", "SceneAsset"));
            index.Upsert(new AssetRecord("d", "Assets/PlayerController.cs", "PlayerController", "MonoScript"));
            index.Upsert(new AssetRecord("e", "Assets/Settings.asset", "Settings", "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("t:Prefab").Select(x => x.Guid), Is.EqualTo(new[] { "a" }));
            Assert.That(search.Search("type:MODEL").Select(x => x.Guid), Is.EqualTo(new[] { "b" }));
            Assert.That(search.Search("t:scene").Select(x => x.Guid), Is.EqualTo(new[] { "c" }));
            Assert.That(search.Search("t:script").Select(x => x.Guid), Is.EqualTo(new[] { "d" }));
            Assert.That(search.Search("t:scrip").Select(x => x.Guid), Is.EqualTo(new[] { "d", "e" }),
                "incomplete type values retain partial matching across aliases and concrete types");
            Assert.That(search.Search("t:gameobject").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(search.Search("t=gameobject").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(search.Search("t:!gameobject").Select(x => x.Guid), Is.EqualTo(new[] { "a", "b" }));
            Assert.That(search.Search("prefab"), Is.Empty,
                "extension-derived kind aliases are explicit t: filters, not unqualified keywords");
            Assert.That(search.Search("model"), Is.Empty,
                "plain terms retain the established name/real-type/tag/label/guid fields");
            Assert.That(search.Search("t=game"), Is.Empty,
                "type equality compares a complete real type or Unity kind alias");
            Assert.That(search.Search("t:file").Count, Is.EqualTo(5));
            Assert.That(search.Search("t:object").Count, Is.EqualTo(5));
        }

        [Test]
        public void Search_RepeatedTypesAndLabelsAreOrGroups_AndGroupsCombineWithAnd()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "prefab-gameplay",
                "Assets/Player.prefab",
                "Player",
                "GameObject",
                unityLabels: new[] { "Gameplay" }));
            index.Upsert(new AssetRecord(
                "script-gameplay",
                "Assets/PlayerController.cs",
                "PlayerController",
                "MonoScript",
                unityLabels: new[] { "Gameplay" }));
            index.Upsert(new AssetRecord(
                "texture-urgent",
                "Assets/PlayerIcon.png",
                "PlayerIcon",
                "Texture2D",
                unityLabels: new[] { "Urgent" }));
            index.Upsert(new AssetRecord(
                "scene-release",
                "Assets/PlayerArena.unity",
                "PlayerArena",
                "SceneAsset",
                unityLabels: new[] { "Release" }));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("player t:prefab t:script").Select(x => x.Guid),
                Is.EqualTo(new[] { "prefab-gameplay", "script-gameplay" }),
                "positive t: clauses are alternatives, while the plain term remains required");
            Assert.That(
                search.Search("l:gameplay l:urgent t:prefab t:script").Select(x => x.Guid),
                Is.EqualTo(new[] { "prefab-gameplay", "script-gameplay" }),
                "the label OR-group and type OR-group are combined with AND");
            Assert.That(search.Search("t:prefab t:not-a-real-type").Select(x => x.Guid),
                Is.EqualTo(new[] { "prefab-gameplay" }),
                "an empty OR branch must not erase a matching type branch");
        }

        [Test]
        public void Search_QuotedEscapedAndUnclosedPhrasesAreHandledDeterministically()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("exact", "Assets/Player HUD.prefab", "Player HUD", "GameObject"));
            index.Upsert(new AssetRecord("mobile", "Assets/Player HUD Mobile.prefab", "Player HUD Mobile", "GameObject"));
            index.Upsert(new AssetRecord("quoted", "Assets/Quoted.prefab", "Player \"HUD\"", "GameObject"));
            index.Upsert(new AssetRecord("split", "Assets/HUD Player.prefab", "HUD Player", "GameObject"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("\"player hud\"").Select(x => x.Guid), Is.EqualTo(new[] { "exact", "mobile" }));
            Assert.That(search.Search("name:\"player hud\"").Select(x => x.Guid), Is.EqualTo(new[] { "exact", "mobile" }));
            Assert.That(search.Search("name=\"player hud\"").Select(x => x.Guid), Is.EqualTo(new[] { "exact" }));
            Assert.That(search.Search("name:\"player \\\"hud\\\"\"").Select(x => x.Guid), Is.EqualTo(new[] { "quoted" }));
            Assert.That(search.Search("name:\"player hud").Select(x => x.Guid), Is.EqualTo(new[] { "exact", "mobile" }),
                "an unclosed quote is treated as a phrase through the end of the query");
            Assert.That(search.Search("player hud").Count, Is.EqualTo(4),
                "unquoted terms remain independent required substrings");
        }

        [Test]
        public void Search_ExactAndExcludedTermsCompose()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "prefab",
                "Assets/Player.prefab",
                "Player",
                "GameObject",
                unityLabels: new[] { "Gameplay" }));
            index.Upsert(new AssetRecord(
                "script",
                "Assets/Player.cs",
                "Player Script",
                "MonoScript",
                unityLabels: new[] { "Tools" }));
            index.Upsert(new AssetRecord("hud", "Assets/PlayerHUD.png", "PlayerHUD", "Texture2D"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("!Player").Select(x => x.Guid), Is.EqualTo(new[] { "prefab" }));
            Assert.That(search.Search("name=player").Select(x => x.Guid), Is.EqualTo(new[] { "prefab" }));
            Assert.That(search.Search("player -t:prefab").Select(x => x.Guid), Is.EqualTo(new[] { "script", "hud" }));
            Assert.That(search.Search("player -name:hud").Select(x => x.Guid), Is.EqualTo(new[] { "prefab", "script" }));
            Assert.That(search.Search("-l:tools").Select(x => x.Guid), Is.EqualTo(new[] { "prefab", "hud" }));
            Assert.That(search.Search("t:prefab t:script -t:prefab").Select(x => x.Guid), Is.EqualTo(new[] { "script" }));
        }

        [Test]
        public void Search_UnityProjectFieldsMatchNamePathDirectoryExtensionAreaGuidAndGlob()
        {
            const string assetGuid = "0123456789abcdef0123456789abcdef";
            const string packageGuid = "fedcba9876543210fedcba9876543210";
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                assetGuid,
                "Assets/UI/Editor/Player HUD.prefab",
                "Player HUD",
                "GameObject"));
            index.Upsert(new AssetRecord(
                packageGuid,
                "Packages/com.example/Runtime/Settings.asset",
                "Settings",
                "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("n:player").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("path=\"Assets/UI/Editor/Player HUD.prefab\"").Select(x => x.Guid),
                Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("dir:editor").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("dir:edit"), Is.Empty, "dir: matches a complete directory segment");
            Assert.That(search.Search("ext:.PREFAB").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("a:assets").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("a:packages").Select(x => x.Guid), Is.EqualTo(new[] { packageGuid }));
            Assert.That(search.Search("a:all").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid, packageGuid }));
            Assert.That(search.Search("guid:01234567").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search(assetGuid).Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }),
                "a raw 32-character hexadecimal GUID is recognized without a qualifier");
            Assert.That(search.Search("glob:**/editor/*.prefab").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
            Assert.That(search.Search("glob:**/*.{prefab,asset}").Select(x => x.Guid),
                Is.EqualTo(new[] { assetGuid, packageGuid }));
            Assert.That(search.Search("*.prefab").Select(x => x.Guid), Is.EqualTo(new[] { assetGuid }));
        }

        [Test]
        public void Search_UnknownAndMalformedQualifiersNeverBroadenTheQuery()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("plain", "Assets/Needle.asset", "Needle", "ScriptableObject"));
            index.Upsert(new AssetRecord("literal", "Assets/Literal.asset", "unknown:needle", "ScriptableObject"));
            index.Upsert(new AssetRecord("extensionless", "Assets/LICENSE", "LICENSE", "TextAsset"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("unknown:needle").Select(x => x.Guid), Is.EqualTo(new[] { "literal" }),
                "unknown fields stay literal instead of silently becoming an unqualified value search");
            Assert.That(search.Search("t:"), Is.Empty);
            Assert.That(search.Search("name:"), Is.Empty);
            Assert.That(search.Search("a:not-a-real-area"), Is.Empty);
            Assert.That(search.Search("a:"), Is.Empty);
            Assert.That(search.Search("t:\"\""), Is.Empty);
            Assert.That(search.Search("l:\" \""), Is.Empty);
            Assert.That(search.Search("name:\"   \""), Is.Empty);
            Assert.That(search.Search("glob:\"\""), Is.Empty);
            Assert.That(search.Search("ext:."), Is.Empty,
                "a leading-dot convenience form still requires an extension value");
            Assert.That(search.Search("ext:\".\""), Is.Empty);
            Assert.That(search.Search("glob:{prefab}"), Is.Empty);
            Assert.That(search.Search("glob:{prefab,}"), Is.Empty);
        }

        [Test]
        public void Search_GlobstarDirectoryMatchesZeroOneOrManyDirectoryLevels()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("zero", "Assets/Foo.prefab", "Foo", "GameObject"));
            index.Upsert(new AssetRecord("one", "Assets/Sub/Foo.prefab", "Foo", "GameObject"));
            index.Upsert(new AssetRecord("many", "Assets/A/B/Foo.prefab", "Foo", "GameObject"));
            index.Upsert(new AssetRecord("wrong", "Packages/Foo.prefab", "Foo", "GameObject"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("glob:Assets/**/Foo.prefab").Select(x => x.Guid),
                Is.EqualTo(new[] { "many", "zero", "one" }));
        }

        [Test]
        public void Search_GlobstarDirectoryZeroLevelRuleOnlyAppliesToAWholeSegment()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("direct", "Assets/EditorFoo.cs", "EditorFoo", "MonoScript"));
            index.Upsert(new AssetRecord("folder", "Assets/EditorTools/Tool.cs", "Tool", "MonoScript"));
            index.Upsert(new AssetRecord("nested", "Assets/Editor/Sub/Tool.cs", "Tool", "MonoScript"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("glob:Assets/Editor**/*.cs").Select(record => record.Guid),
                Is.EqualTo(new[] { "nested", "folder" }),
                "an adjacent ** still requires the literal slash; only a complete **/ segment may omit it");
        }

        // The per-candidate MatchesFolderScope call was hoisted: the scope's shape
        // is resolved once per Search and the separator character now decides
        // membership before any prefix comparison runs. The predicate is
        // unchanged, so this pins the two ways a restructured prefix test goes
        // wrong — a sibling folder that shares the scope's characters, and a
        // candidate that IS the scope.
        [Test]
        public void Search_FolderScopeIsAPathPrefixNotAStringPrefix()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("inside", "Assets/Art/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("deeper", "Assets/Art/Sub/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("sibling", "Assets/Artwork/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("elsewhere", "Assets/UI/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("package", "Packages/com.x/Icon.png", "Icon", "Texture2D"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("icon", "Assets/Art").Select(x => x.Guid),
                Is.EquivalentTo(new[] { "inside", "deeper" }),
                "Assets/Artwork shares the scope's characters but is not under it");
            Assert.That(
                search.Search("icon", "Assets").Select(x => x.Guid),
                Is.EquivalentTo(new[] { "inside", "deeper", "sibling", "elsewhere" }),
                "the corpus-wide root scope still excludes Packages");
            Assert.That(
                search.Search("icon", "Packages").Select(x => x.Guid),
                Is.EquivalentTo(new[] { "package" }));
            Assert.That(
                search.Search("icon").Select(x => x.Guid),
                Is.EquivalentTo(new[] { "inside", "deeper", "sibling", "elsewhere", "package" }),
                "an empty scope is All and skips the check entirely");
            Assert.That(
                search.Search("icon", "Assets/Art/Icon.png").Select(x => x.Guid),
                Is.EqualTo(new[] { "inside" }),
                "a candidate that IS the scope still matches it");
        }

        // A glob whose literal runs are all shorter than a trigram has no posting
        // list to start from, so it used to hand the whole corpus to the DP
        // matcher — ~180M cells at 500k, on a keystroke. It is answered as "too
        // broad" (no results) now; the rule is documented on the glob line in
        // Documentation~/overview.md.
        [Test]
        public void Search_GlobWithNoThreeCharacterLiteralRunIsRejectedAsTooBroad()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("a", "Assets/Alpha.prefab", "Alpha", "GameObject"));
            index.Upsert(new AssetRecord("b", "Assets/Beta.asset", "Beta", "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("glob:**/*a*"), Is.Empty);
            Assert.That(search.Search("glob:*a*"), Is.Empty);
            Assert.That(search.Search("glob:**/*"), Is.Empty);
            Assert.That(
                search.Search("glob:**/*.prefab").Select(x => x.Guid),
                Is.EqualTo(new[] { "a" }),
                "one literal run of three or more characters is all a glob needs");
        }

        [TestCase("glob:**/*a*")]
        [TestCase("glob:*a*")]
        [TestCase("glob:**/*")]
        [TestCase("glob:{**/*Alpha*,**/*a*}")]
        [TestCase("glob:{**/*a*,**/*Alpha*}")]
        [TestCase("glob:**/*{Alpha,a}*")]
        [TestCase("**/*a*")]
        [TestCase("*a*")]
        [TestCase("**/*")]
        [TestCase("{**/*Alpha*,**/*a*}")]
        [TestCase("**/*{Alpha,a}*")]
        public void Search_BroadGlobIsInvalidForPositiveAndExcludedQueries(string query)
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("alpha", "Assets/Alpha.asset", "Alpha", "Object"));
            index.Upsert(new AssetRecord("echo", "Assets/Echo.txt", "Echo", "TextAsset"));
            var search = new AssetSearchService(index);

            Assert.That(search.Search(query), Is.Empty);
            Assert.That(search.Search("-" + query), Is.Empty,
                "an excluded invalid glob must reject the query, not remove matches from a corpus scan");
        }

        [Test]
        public void Search_PathAndQuotedNameWildcardsRemainLiteral()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("alpha", "Assets/Alpha.asset", "Alpha", "Object"));
            var search = new AssetSearchService(index);

            foreach (var query in new[] { "path:*a*", "name:*a*", "\"*a*\"" })
            {
                Assert.That(search.Search(query), Is.Empty, query);
                Assert.That(search.Search("-" + query).Select(asset => asset.Guid),
                    Is.EqualTo(new[] { "alpha" }), "recognized fields and quoted text are not implicit globs: " + query);
            }
        }

        // The candidate source stays the LONGEST literal run (the perf harness
        // refuted using every run: resolving a short leading run merges the whole
        // corpus). Brace ALTERNATIVES are ORs and stay unioned; one alternative
        // with no usable run makes the whole token too broad.
        [Test]
        public void Search_GlobUsesTheLongestLiteralRunAndUnionsBraceAlternatives()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("editor", "Assets/Editor/Tool.prefab", "Tool", "GameObject"));
            index.Upsert(new AssetRecord("runtime", "Assets/Runtime/Tool.prefab", "Tool", "GameObject"));
            index.Upsert(new AssetRecord("asset", "Assets/Editor/Tool.asset", "Tool", "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("glob:Assets/Editor/*.prefab").Select(x => x.Guid),
                Is.EqualTo(new[] { "editor" }),
                "the candidate superset must still be verified by the glob matcher");
            Assert.That(
                search.Search("glob:Assets/Editor/*.{prefab,asset}").Select(x => x.Guid),
                Is.EquivalentTo(new[] { "editor", "asset" }));
            Assert.That(
                search.Search("glob:{Assets/Editor/*.prefab,**/*a*}"),
                Is.Empty,
                "an alternative with no usable literal run makes the whole pattern too broad");
        }

        [Test]
        public void Search_FolderScopeNormalizesCaseSlashesAndTrailingSeparators()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("art", "Assets/Art/Sub/Icon.png", "Icon", "Texture2D"));
            index.Upsert(new AssetRecord("other", "Assets/Artwork/Icon.png", "Icon", "Texture2D"));

            var search = new AssetSearchService(index);

            Assert.That(search.Search("icon", @"ASSETS\ART\").Select(x => x.Guid), Is.EqualTo(new[] { "art" }));
            Assert.That(search.Search("icon", "Assets/Art/").Select(x => x.Guid), Is.EqualTo(new[] { "art" }));
            Assert.That(search.Search("icon", "Assets/Ar"), Is.Empty,
                "folder scope is a path boundary, not a raw prefix");
        }

        [Test]
        public void Search_IncrementalRecordReplacementRemovesOldTypeAndLabelPostings()
        {
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                "same-guid",
                "Assets/Player.prefab",
                "Player",
                "GameObject",
                unityLabels: new[] { "Old Label" }));

            Assert.That(search.Search("t:prefab").Select(x => x.Guid), Is.EqualTo(new[] { "same-guid" }));
            Assert.That(search.Search("l:old").Select(x => x.Guid), Is.EqualTo(new[] { "same-guid" }));

            SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                "same-guid",
                "Assets/Player.fbx",
                "Player",
                "GameObject",
                unityLabels: new[] { "New Label" }));

            Assert.That(search.Search("t:prefab"), Is.Empty);
            Assert.That(search.Search("l:old"), Is.Empty);
            Assert.That(search.Search("t:model").Select(x => x.Guid), Is.EqualTo(new[] { "same-guid" }));
            Assert.That(search.Search("l:new").Select(x => x.Guid), Is.EqualTo(new[] { "same-guid" }));
        }

        [Test]
        public void Search_WarmIncrementalImportAtSamePathRetiresDisplacedGuid()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "old-guid",
                "Assets/Settings.asset",
                "OldSettings",
                "OldSettingsType",
                unityLabels: new[] { "Old Label" }));
            var search = new AssetSearchService(index);
            search.WarmIndex();

            SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                "new-guid",
                "Assets/Settings.asset",
                "NewSettings",
                "NewSettingsType",
                unityLabels: new[] { "New Label" }));

            Assert.That(index.Count, Is.EqualTo(1));
            Assert.That(index.Contains("old-guid"), Is.False);
            Assert.That(search.Search("name:oldsettings"), Is.Empty);
            Assert.That(search.Search("t:oldsettingstype"), Is.Empty);
            Assert.That(search.Search("l:old"), Is.Empty);
            Assert.That(search.Search("name:newsettings").Select(record => record.Guid),
                Is.EqualTo(new[] { "new-guid" }));
            Assert.That(search.Search("t:newsettingstype").Select(record => record.Guid),
                Is.EqualTo(new[] { "new-guid" }));
            Assert.That(search.Search("l:new").Select(record => record.Guid),
                Is.EqualTo(new[] { "new-guid" }));
        }

        [Test]
        public void BroadOrdinalResult_RemainsStableAcrossMirrorCompaction()
        {
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            for (var i = 0; i < 4; i++)
            {
                SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                    "hero-" + i,
                    "Assets/Hero" + i + ".asset",
                    "Hero" + i,
                    "ScriptableObject"));
            }

            search.WarmIndex();
            var published = search.Search("hero");

            // One tombstone in four reaches the 25% compaction threshold and
            // replaces the search index's record generation. The already-published
            // ordinal view must keep resolving against its original generation.
            SearchCatalogTestMutations.Remove(index, search, "hero-0");

            Assert.That(published.Select(record => record.Guid),
                Is.EqualTo(new[] { "hero-0", "hero-1", "hero-2", "hero-3" }));
            Assert.That(search.Search("hero").Select(record => record.Guid),
                Is.EqualTo(new[] { "hero-1", "hero-2", "hero-3" }));
        }

        [Test]
        public void WorkerBuild_AdoptsOnlyTheMatchingCatalogGeneration()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "worker-guid",
                "Assets/Worker.asset",
                "Worker",
                "ScriptableObject"));
            var search = new AssetSearchService(index);
            var records = index.Enumerate().ToArray();
            var build = AssetSearchService.BuildOnWorker(
                records,
                index.RecordVersion);

            Assert.That(search.TryAdoptWorkerBuild(build), Is.True);
            Assert.That(search.Search("worker").Select(record => record.Guid),
                Is.EqualTo(new[] { "worker-guid" }));

            var staleBuild = AssetSearchService.BuildOnWorker(
                records,
                index.RecordVersion);
            index.Upsert(new AssetRecord(
                "newer-guid",
                "Assets/Newer.asset",
                "Newer",
                "ScriptableObject"));

            Assert.That(search.TryAdoptWorkerBuild(staleBuild), Is.False,
                "a background posting snapshot must never overwrite newer catalog state");
        }

        [Test]
        public void Suggest_PrefixMatchesAreRankedAheadOfLaterSubstrings()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("substring", "Assets/BestPlayer.asset", "BestPlayer", "ScriptableObject"));
            index.Upsert(new AssetRecord("exact-prefix", "Assets/Player.asset", "Player", "ScriptableObject"));
            index.Upsert(new AssetRecord("long-prefix", "Assets/PlayerController.cs", "PlayerController", "MonoScript"));
            var search = new AssetSearchService(index);

            var suggestions = search.Suggest("play");

            Assert.That(suggestions.Select(suggestion => suggestion.Asset.Guid),
                Is.EqualTo(new[] { "exact-prefix", "long-prefix", "substring" }));
            Assert.That(suggestions.All(suggestion => !suggestion.IsFuzzy), Is.True);
        }

        [Test]
        public void Suggest_UsesDamerauDistanceForMisspellingsAndTranspositions()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("player", "Assets/Player.prefab", "Player", "GameObject"));
            index.Upsert(new AssetRecord("controller", "Assets/PlayerController.cs", "PlayerController", "MonoScript"));
            index.Upsert(new AssetRecord("cat", "Assets/Cat.asset", "Cat", "ScriptableObject"));
            var search = new AssetSearchService(index);

            var transposed = search.Suggest("plaeyr");
            var missingLetter = search.Suggest("controler");
            var shortSubstitution = search.Suggest("czt");

            Assert.That(transposed.Select(suggestion => suggestion.Asset.Guid), Does.Contain("player"));
            Assert.That(transposed.Single(suggestion => suggestion.Asset.Guid == "player").IsFuzzy, Is.True);
            Assert.That(missingLetter.Select(suggestion => suggestion.Asset.Guid), Does.Contain("controller"));
            Assert.That(missingLetter.Single(suggestion => suggestion.Asset.Guid == "controller").IsFuzzy, Is.True);
            Assert.That(shortSubstitution.Select(suggestion => suggestion.Asset.Guid), Does.Contain("cat"));
        }

        [Test]
        public void Suggest_LongNamesAllowTwoEditsButShortQueriesStayPrefixOnly()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("config", "Assets/PlayerConfig.asset", "PlayerConfig", "ScriptableObject"));
            var search = new AssetSearchService(index);

            Assert.That(search.Suggest("plyerconfg").Select(suggestion => suggestion.Asset.Guid),
                Does.Contain("config"));
            Assert.That(search.Suggest("px"), Is.Empty,
                "one- and two-character input must not trigger broad fuzzy matching");
        }

        [Test]
        public void Suggest_RespectsFolderAndTypeFiltersAndSkipsStructuredSyntax()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("art", "Assets/Art/Player.png", "Player", "Texture2D"));
            index.Upsert(new AssetRecord("code", "Assets/Code/Player.cs", "Player", "MonoScript"));
            var search = new AssetSearchService(index);

            var suggestions = search.Suggest("plaeyr", "Assets/Art", "Texture2D");

            Assert.That(suggestions.Select(suggestion => suggestion.Asset.Guid), Is.EqualTo(new[] { "art" }));
            Assert.That(search.Suggest("t:prefb"), Is.Empty,
                "structured qualifiers retain their deterministic grammar instead of being fuzzied");
        }

        [Test]
        public void Suggest_IncrementalRenameRetiresTheOldName()
        {
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                "same-guid",
                "Assets/OldPlayer.asset",
                "OldPlayer",
                "ScriptableObject"));
            search.WarmIndex();

            SearchCatalogTestMutations.Upsert(index, search, new AssetRecord(
                "same-guid",
                "Assets/NewPlayer.asset",
                "NewPlayer",
                "ScriptableObject"));

            Assert.That(search.Suggest("oldplay"), Is.Empty);
            Assert.That(search.Suggest("newplay").Select(suggestion => suggestion.Asset.Guid),
                Is.EqualTo(new[] { "same-guid" }));
        }

        [Test]
        public void SuggestionCompletion_QuotesNamesThatWouldChangeTheQueryGrammar()
        {
            var suggestion = new AssetSearchSuggestion(
                new AssetRecord(
                    "hud",
                    "Assets/Player HUD.prefab",
                    "Player HUD",
                    "GameObject"),
                isFuzzy: false);

            Assert.That(suggestion.CompletionText, Is.EqualTo("name=\"Player HUD\""));
        }

        // M-3: the candidate scan walks the prefix run alphabetically, but the score model
        // prefers the SHORTEST completion. "Players" sorts after eight longer
        // PlayerAbility*Controller names, so a scan that stopped once the top list was
        // full never even scored the best answer.
        [Test]
        public void Suggest_KeepsTheBestCompletionWhenEarlierPrefixMatchesFillTheLimit()
        {
            var index = new InMemoryAssetIndex();
            foreach (var letter in "ABCDEFGH")
            {
                var name = "PlayerAbility" + letter + "Controller";
                index.Upsert(new AssetRecord(
                    "ability-" + letter,
                    "Assets/" + name + ".cs",
                    name,
                    "MonoScript"));
            }

            index.Upsert(new AssetRecord("players", "Assets/Players.cs", "Players", "MonoScript"));
            var search = new AssetSearchService(index);

            var suggestions = search.Suggest("player");

            Assert.That(
                suggestions.Select(suggestion => suggestion.Asset.Name),
                Does.Contain("Players"));
            Assert.That(
                suggestions[0].Asset.Name,
                Is.EqualTo("Players"),
                "the shortest completion outranks the longer same-prefix family");
        }

        // m-5: an unquoted 32-hex token parses as an exact GUID query, so a hash-named
        // asset (lightmaps, bundles, generated caches) was suggested and then found
        // nothing the moment the suggestion was accepted.
        [TestCase("0123456789abcdef0123456789abcdef")]
        [TestCase("0123456789ABCDEF0123456789ABCDEF")]
        public void SuggestionCompletion_ForAGuidShapedName_FindsTheAssetWhenAccepted(
            string assetName)
        {
            var completion = SuggestCompletionFor(assetName, "0123456789ab", out var search);

            Assert.That(completion, Is.EqualTo("name=\"" + assetName + "\""));
            Assert.That(
                search.Search(completion).Select(record => record.Name),
                Does.Contain(assetName));
        }

        [Test]
        public void SuggestionCompletion_ForANonHexThirtyTwoCharacterName_StaysUnquoted()
        {
            const string assetName = "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz";

            var completion = SuggestCompletionFor(assetName, "zzzzzzzzzzzz", out var search);

            Assert.That(completion, Is.EqualTo(assetName));
            Assert.That(
                search.Search(completion).Select(record => record.Name),
                Does.Contain(assetName));
        }

        // m-6: the parser trims inside quotes, so `name="..."` can never match a name whose
        // own edges are whitespace. The completion must drop the exactness, not the quotes.
        [TestCase(" padded ", "padded")]
        [TestCase("trailing ", "trailing")]
        [TestCase(" leading", "leading")]
        public void SuggestionCompletion_ForANameWithEdgeWhitespace_FindsTheAssetWhenAccepted(
            string assetName,
            string typedQuery)
        {
            var completion = SuggestCompletionFor(assetName, typedQuery, out var search);

            Assert.That(completion, Is.EqualTo("name:\"" + assetName + "\""));
            Assert.That(
                search.Search(completion).Select(record => record.Name),
                Does.Contain(assetName));
        }

        // Audit §3.10: one incremental upsert used to leave the alphabetical ordinals
        // dirty, so the next broad query re-sorted every live ordinal in the project
        // (measured 0.49 ms -> 36.4 ms at 100k records) and the next delta re-armed it.
        [Test]
        public void BroadSearch_AfterOneIncrementalUpsert_KeepsRebuildOrderWithoutResorting()
        {
            const int RecordCount = 10000;
            var previousEnabled = ZepPerfLog.IsEnabled;
            var previousSink = ZepPerfLog.Sink;
            try
            {
                ZepPerfLog.Sink = null;
                ZepPerfLog.IsEnabled = true;
                var index = new InMemoryAssetIndex();
                for (var i = 0; i < RecordCount; i++)
                {
                    index.Upsert(BulkRecord(i));
                }

                var search = new AssetSearchService(index);
                search.WarmIndex();

                // Sorts into the middle of the corpus, so a merge that appended
                // instead of placing it would be visible in the order assertion.
                var inserted = new AssetRecord(
                    "guid-inserted",
                    "Assets/Bulk/Asset_4444_inserted.asset",
                    "Asset_4444_inserted",
                    "ScriptableObject");

                ZepPerfLog.Reset();
                SearchCatalogTestMutations.Upsert(index, search, inserted);
                var afterDelta = search.Search("asset_").Select(record => record.Guid).ToArray();
                var resorts = ZepPerfLog
                    .CaptureSnapshot()
                    .GetCount(ZepPerfCounter.SearchAlphabeticalResorts);

                Assert.That(
                    resorts,
                    Is.EqualTo(0L),
                    "one upsert must not cost a full-corpus alphabetical re-sort");

                var rebuilt = new AssetSearchService(index);
                rebuilt.WarmIndex();
                var expected = rebuilt.Search("asset_").Select(record => record.Guid).ToArray();

                Assert.That(
                    expected.Length,
                    Is.EqualTo(RecordCount + 1),
                    "the reference order must cover the whole corpus, not a subset");
                Assert.That(
                    afterDelta,
                    Is.EqualTo(expected),
                    "the incrementally merged order must equal a freshly rebuilt index's order");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Sink = previousSink;
            }
        }

        // The merge has to drop tombstoned ordinals from both sides: the ordinals
        // already placed in the maintained order, and the ordinals this very delta
        // appended (a second upsert of one GUID retires the first one's new ordinal).
        [Test]
        public void BroadSearch_AfterADeltaThatRemovesAndRenames_DropsRetiredOrdinalsFromTheOrder()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("d", "Assets/Bulk/Delta.asset", "Delta", "ScriptableObject"));
            index.Upsert(new AssetRecord("b", "Assets/Bulk/Bravo.asset", "Bravo", "ScriptableObject"));
            index.Upsert(new AssetRecord("e", "Assets/Bulk/Echo.asset", "Echo", "ScriptableObject"));
            index.Upsert(new AssetRecord("a", "Assets/Bulk/Alpha.asset", "Alpha", "ScriptableObject"));
            var search = new AssetSearchService(index);
            search.WarmIndex();

            // One atomic delta: Delta is deleted, Bravo is renamed to Zulu (same GUID,
            // so its old ordinal is tombstoned), and Charlie arrives.
            search.ApplyCatalogDelta(index.ApplyFileChanges(
                new[]
                {
                    new AssetRecord("b", "Assets/Bulk/Zulu.asset", "Zulu", "ScriptableObject"),
                    new AssetRecord("c", "Assets/Bulk/Charlie.asset", "Charlie", "ScriptableObject"),
                },
                new[] { "Assets/Bulk/Delta.asset" }));

            Assert.That(
                search.Search("path:assets/bulk").Select(record => record.Name),
                Is.EqualTo(new[] { "Alpha", "Charlie", "Echo", "Zulu" }),
                "the retired name must be gone and the renamed one must sit at its new place");
        }

        private static AssetRecord BulkRecord(int ordinal)
        {
            // Digit-reversed names: the insertion order the ordinals are appended in
            // is deliberately NOT the alphabetical order the search must return.
            var digits = ordinal.ToString("D4").ToCharArray();
            System.Array.Reverse(digits);
            var name = "Asset_" + new string(digits);
            return new AssetRecord(
                "guid-" + ordinal.ToString("D5"),
                "Assets/Bulk/" + name + ".asset",
                name,
                "ScriptableObject");
        }

        private static string SuggestCompletionFor(
            string assetName,
            string typedQuery,
            out AssetSearchService search)
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                "target",
                "Assets/" + assetName.Trim() + ".asset",
                assetName,
                "ScriptableObject"));
            index.Upsert(new AssetRecord("decoy", "Assets/Decoy.asset", "Decoy", "ScriptableObject"));
            search = new AssetSearchService(index);

            var suggestions = search.Suggest(typedQuery);
            var match = suggestions.FirstOrDefault(
                suggestion => suggestion.Asset.Name == assetName);
            Assert.That(
                match.Asset,
                Is.Not.Null,
                "'" + assetName + "' must be suggested for '" + typedQuery + "'");
            return match.CompletionText;
        }

        // T3.21 (audit 4.37). Compaction used to answer itself synchronously
        // inside ApplyCatalogDelta — i.e. inside Unity's import callback, where
        // a full gram-index rebuild costs seconds on a large project. It now
        // records a request that the controller answers on its existing worker.
        [Test]
        public void CompactionOverTheThreshold_IsRequested_NotRebuiltInsideApplyCatalogDelta()
        {
            var index = new InMemoryAssetIndex();
            for (var i = 0; i < 8; i++)
            {
                index.Upsert(new AssetRecord(
                    "guid-" + i,
                    "Assets/Item" + i + ".asset",
                    "Item" + i,
                    "ScriptableObject"));
            }

            var search = new AssetSearchService(index);
            search.WarmIndex();
            Assert.That(search.TombstoneCount, Is.Zero);

            var previousEnabled = ZepPerfLog.IsEnabled;
            try
            {
                ZepPerfLog.IsEnabled = true;
                ZepPerfLog.Reset();

                // Re-upserting three of eight retires three ordinals: 3/11 is
                // over the 25% threshold.
                for (var i = 0; i < 3; i++)
                {
                    SearchCatalogTestMutations.Upsert(
                        index,
                        search,
                        new AssetRecord(
                            "guid-" + i,
                            "Assets/Item" + i + ".asset",
                            "Item" + i + "Renamed",
                            "ScriptableObject"));
                }

                Assert.That(
                    ZepPerfLog.CaptureSnapshot().GetCount(
                        ZepPerfCounter.SearchFullBuilds),
                    Is.Zero,
                    "no synchronous full rebuild may happen inside ApplyCatalogDelta");
            }
            finally
            {
                ZepPerfLog.IsEnabled = previousEnabled;
                ZepPerfLog.Reset();
            }

            Assert.That(search.TombstoneCount, Is.EqualTo(3));
            Assert.That(search.IsCompactionRequested, Is.True);

            // Results stay correct while the request waits: the retired ordinals
            // are still in the postings, and the tombstone probe is what keeps
            // the superseded record out of the answer.
            Assert.That(
                search.Search("Item0").Select(record => record.Name),
                Is.EqualTo(new[] { "Item0Renamed" }));

            // What the controller does with the request.
            var records = new System.Collections.Generic.List<AssetRecord>(index.Enumerate());
            Assert.That(
                search.TryAdoptWorkerBuild(
                    AssetSearchService.BuildOnWorker(records, index.RecordVersion)),
                Is.True);
            Assert.That(
                search.TombstoneCount,
                Is.Zero,
                "the adopted build carries only live ordinals");
            Assert.That(search.IsCompactionRequested, Is.False);
            Assert.That(
                search.Search("Item0").Select(record => record.Name),
                Is.EqualTo(new[] { "Item0Renamed" }),
                "results are unchanged by the compaction");
        }

        // Suggestion cases the parsed-query overload must answer identically.
        // Written as literal expectations (not "both overloads agree", which a
        // pair of broken overloads would also satisfy) and pinned against the
        // string overload before the overload existed.
        [TestCase("hero t:Texture path:\"Assets/My Art\" custom:value", "hero   custom:value")]
        [TestCase("\"t:Texture\" -t:Material name:Hero", "\"t:Texture\"  name:Hero")]
        [TestCase("hero -name:Villain l:Blue a:assets", "hero -name:Villain")]
        [TestCase("hero type=Texture2D", "hero")]
        public void RemoveFilters_PreservesLiteralNamesUnknownQualifiersAndQuotes(string query, string expected)
        {
            Assert.That(SearchQuery.RemoveFilters(query), Is.EqualTo(expected));
        }

        [Test]
        public void ToggleType_PreservesOtherRestrictionsAndRoundTrips()
        {
            const string query = "hero path:\"Assets/My Art\" -t:Material";
            var toggled = SearchQuery.ToggleTypeFilter(query, "Texture2D");
            Assert.That(toggled, Does.StartWith(query));
            Assert.That(SearchQuery.Parse(toggled).TypeTerms.Single().Value, Is.EqualTo("texture2d"));
            Assert.That(SearchQuery.ToggleTypeFilter(toggled, "Texture2D"), Is.EqualTo(query));
            Assert.That(SearchQuery.FilterSpans(toggled).Select(span => toggled.Substring(span.Start, span.Length)),
                Is.EqualTo(new[] { "path:\"Assets/My Art\"", "-t:Material", "t:\"Texture2D\"" }));
        }

        [TestCase("plaeyr")]
        [TestCase("play")]
        public void Suggestions_KeepExplicitTypePathAndExclusionRestrictions(string name)
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("allowed", "Assets/Art/Player.png", "Player", "Texture2D"));
            index.Upsert(new AssetRecord("wrong-type", "Assets/Art/Player.cs", "Player", "MonoScript"));
            index.Upsert(new AssetRecord("wrong-path", "Assets/Code/Player.png", "Player", "Texture2D"));
            index.Upsert(new AssetRecord("excluded", "Assets/Art/PlayerOld.png", "PlayerOld", "Texture2D"));
            var search = new AssetSearchService(index);
            Assert.That(search.Suggest(name + " t:Texture path:Art -name:PlayerOld").Select(s => s.Asset.Guid),
                Is.EqualTo(new[] { "allowed" }));
            Assert.That(search.Suggest(name + " t:Texture path:Missing"), Is.Empty);
        }

        [TestCase("Hero", 0)]
        [TestCase("HeroMaterial", 1)]
        [TestCase("PlayerHero", 2)]
        [TestCase("Superhero", 3)]
        [TestCase("Other", 4)]
        public void NameRelevance_DistinguishesExactPrefixWordSubstringAndPath(string name, int rank)
        {
            Assert.That(SearchQuery.NameMatchRank(name, SearchQuery.Parse("hero")), Is.EqualTo(rank));
        }

        private static readonly (string Query, string[] Guids, bool[] Fuzzy)[] SuggestionExpectations =
        {
            // Prefix ranking: shorter tail first.
            ("play", new[] { "art", "cfg", "code" }, new[] { false, false, false }),
            // A transposition only the fuzzy pass can reach: every Player*
            // name is one Damerau edit away, and equal scores keep them in
            // ordinal order.
            ("plaeyr", new[] { "art", "cfg", "code" }, new[] { true, true, true }),
            // A structured qualifier is grammar, never a suggestion token.
            ("t:prefb", new string[0], new bool[0]),
            // Two required terms is no longer a single completion token.
            ("player controller", new string[0], new bool[0]),
            // An excluded term likewise.
            ("-play", new string[0], new bool[0]),
            // Nothing typed.
            ("", new string[0], new bool[0])
        };

        [Test]
        public void Suggest_WithAStringQuery_AnswersTheSuggestionExpectations()
        {
            var search = BuildSuggestionFixture();

            for (var i = 0; i < SuggestionExpectations.Length; i++)
            {
                var expectation = SuggestionExpectations[i];
                var suggestions = search.Suggest(expectation.Query);

                Assert.That(
                    suggestions.Select(suggestion => suggestion.Asset.Guid),
                    Is.EqualTo(expectation.Guids),
                    "string overload, query \"" + expectation.Query + "\"");
                Assert.That(
                    suggestions.Select(suggestion => suggestion.IsFuzzy),
                    Is.EqualTo(expectation.Fuzzy),
                    "string overload fuzzy flags, query \"" + expectation.Query + "\"");
            }

            Assert.That(
                search.Suggest("plaeyr", "Assets/Art", "Texture2D")
                    .Select(suggestion => suggestion.Asset.Guid),
                Is.EqualTo(new[] { "art" }),
                "folder and type scope still narrow the fuzzy pass");
        }

        [Test]
        public void Suggest_WithTheQueryTheProjectionAlreadyParsed_AnswersTheSameSuggestions()
        {
            var search = BuildSuggestionFixture();

            for (var i = 0; i < SuggestionExpectations.Length; i++)
            {
                var expectation = SuggestionExpectations[i];
                var suggestions = search.Suggest(SearchQuery.Parse(expectation.Query));

                Assert.That(
                    suggestions.Select(suggestion => suggestion.Asset.Guid),
                    Is.EqualTo(expectation.Guids),
                    "parsed overload, query \"" + expectation.Query + "\"");
                Assert.That(
                    suggestions.Select(suggestion => suggestion.IsFuzzy),
                    Is.EqualTo(expectation.Fuzzy),
                    "parsed overload fuzzy flags, query \"" + expectation.Query + "\"");
            }

            Assert.That(
                search.Suggest(SearchQuery.Parse("plaeyr"), "Assets/Art", "Texture2D")
                    .Select(suggestion => suggestion.Asset.Guid),
                Is.EqualTo(new[] { "art" }),
                "folder and type scope still narrow the fuzzy pass");
            Assert.That(
                search.Suggest((ParsedSearchQuery)null),
                Is.Empty,
                "no parsed query is no suggestion token");
        }

        [Test]
        public void Search_FieldQualifiers_ResolveEveryAliasInWhateverCaseTheUserTyped()
        {
            const string assetGuid = "0123456789abcdef0123456789abcdef";
            const string packageGuid = "fedcba9876543210fedcba9876543210";
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord(
                assetGuid,
                "Assets/UI/Editor/Player HUD.prefab",
                "Player HUD",
                "GameObject",
                "Urgent",
                new[] { "Gameplay" }));
            index.Upsert(new AssetRecord(
                packageGuid,
                "Packages/com.example/Runtime/Settings.asset",
                "Settings",
                "ScriptableObject"));

            var search = new AssetSearchService(index);
            var asset = new[] { assetGuid };

            // The qualifier is resolved from the raw query text, so a user who
            // capitalises it (or types the long form) must land on the same field
            // as the lowercase short form pinned by the tests above.
            Assert.That(search.Search("N:player").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("NAME:player").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Path:ui").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("D:editor").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Dir:editor").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("DIRECTORY:editor").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("T:gameobject").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Type:gameobject").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("L:gameplay").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("LABEL:gameplay").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Tag:urgent").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("E:.prefab").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Ext:.PREFAB").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("EXTENSION:.prefab").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("A:packages").Select(x => x.Guid), Is.EqualTo(new[] { packageGuid }));
            Assert.That(search.Search("Area:assets").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("GUID:01234567").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Id:01234567").Select(x => x.Guid), Is.EqualTo(asset));
            Assert.That(search.Search("Glob:**/editor/*.prefab").Select(x => x.Guid), Is.EqualTo(asset));

            // An unrecognised qualifier is still not a field: it stays literal
            // rather than broadening to the whole corpus.
            Assert.That(search.Search("nam:player"), Is.Empty);
            Assert.That(search.Search("names:player"), Is.Empty);
            Assert.That(search.Search("direct:editor"), Is.Empty);
        }

        [Test]
        public void Search_CorpusWideQuery_FollowsAppendedAndRetiredRecords()
        {
            // Inserted out of alphabetical order on purpose: the answer is the
            // projection's own ordering, not the order the records arrived in.
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("zeta", "Assets/Zeta.asset", "Zeta", "ScriptableObject"));
            index.Upsert(new AssetRecord("alpha", "Assets/Alpha.asset", "Alpha", "ScriptableObject"));
            index.Upsert(new AssetRecord("mid", "Assets/Mid.asset", "Mid", "ScriptableObject"));

            var search = new AssetSearchService(index);

            Assert.That(
                search.Search("a:assets").Select(x => x.Guid),
                Is.EqualTo(new[] { "alpha", "mid", "zeta" }));

            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("beta", "Assets/Beta.asset", "Beta", "ScriptableObject"));
            Assert.That(
                search.Search("a:assets").Select(x => x.Guid),
                Is.EqualTo(new[] { "alpha", "beta", "mid", "zeta" }),
                "an appended record joins the corpus-wide candidate set");

            SearchCatalogTestMutations.Remove(index, search, "mid");
            Assert.That(
                search.Search("a:assets").Select(x => x.Guid),
                Is.EqualTo(new[] { "alpha", "beta", "zeta" }),
                "a retired record leaves it");

            SearchCatalogTestMutations.Upsert(
                index,
                search,
                new AssetRecord("gamma", "Assets/Gamma.asset", "Gamma", "ScriptableObject"));
            Assert.That(
                search.Search("a:assets").Select(x => x.Guid),
                Is.EqualTo(new[] { "alpha", "beta", "gamma", "zeta" }),
                "appending while a tombstone is outstanding must not resurrect it");
        }

        private static AssetSearchService BuildSuggestionFixture()
        {
            var index = new InMemoryAssetIndex();
            index.Upsert(new AssetRecord("art", "Assets/Art/Player.png", "Player", "Texture2D"));
            index.Upsert(new AssetRecord("cfg", "Assets/Config/PlayerConfig.asset", "PlayerConfig", "ScriptableObject"));
            index.Upsert(new AssetRecord("code", "Assets/Code/PlayerController.cs", "PlayerController", "MonoScript"));
            index.Upsert(new AssetRecord("cat", "Assets/Cat.asset", "Cat", "ScriptableObject"));
            return new AssetSearchService(index);
        }
    }
}
