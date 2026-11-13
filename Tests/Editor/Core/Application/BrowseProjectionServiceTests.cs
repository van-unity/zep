using System;
using System.Collections.Generic;
using System.Linq;
using Zep.Core.Application;
using Zep.Core.Domain;
using Zep.Core.FileOperations;
using Zep.Core.Indexing;
using Zep.Core.Search;
using Zep.Core.State;
using NUnit.Framework;

namespace Zep.Tests.Editor.Core.Application
{
    // Behavior tests for the search projection: how BuildAssetEntries combines
    // the trigram engine's file results with folder-name matching, scope
    // restriction, type/tag filters, folder postings, and the lazy
    // entry wrapper. The engine's own invariants live in
    // AssetSearchServiceTests / AssetSearchServiceAdversarialTests.
    public sealed class BrowseProjectionServiceTests
    {
        private InMemoryAssetIndex _index;
        private AssetSearchService _search;
        private TagProjectionService _tagProjection;
        private BrowseProjectionService _service;
        private List<AssetRecord> _records;
        private List<ZepFolderEntry> _standardFolders;

        [SetUp]
        public void SetUp()
        {
            _records = new List<AssetRecord>
            {
                new AssetRecord(
                    "g-hero",
                    "Assets/Art/Hero.png",
                    "Hero",
                    "Texture2D",
                    unityLabels: new[] { "Gameplay" }),
                new AssetRecord("g-heroctrl", "Assets/Scripts/HeroController.cs", "HeroController", "MonoScript"),
                new AssetRecord("g-villain", "Assets/Art/Sub/Villain.png", "Villain", "Texture2D"),
                new AssetRecord("g-pkg", "Packages/com.x/HeroPkg.asset", "HeroPkg", "ScriptableObject"),
            };

            _standardFolders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Art", "Assets/Art", 1, isReadOnly: false),
                new ZepFolderEntry("Sub", "Assets/Art/Sub", 2, isReadOnly: false),
                new ZepFolderEntry("Scripts", "Assets/Scripts", 1, isReadOnly: false),
                new ZepFolderEntry(
                    "HeroFolder",
                    "Assets/HeroFolder",
                    1,
                    isReadOnly: false,
                    guid: "f0123456789abcdef0123456789abcde",
                    unityLabels: new[] { "Folder Workflow" }),
            };

            _index = new InMemoryAssetIndex();
            foreach (var record in _records)
            {
                _index.Upsert(record);
            }
            _index.ReplaceFolders(_standardFolders
                .Select(folder => new AssetFolderRecord(AssetPath.From(folder.Path)))
                .ToList());

            _search = new AssetSearchService(_index);
            _tagProjection = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            _service = new BrowseProjectionService(_index, _search, _tagProjection);
            _service.RebuildCache(_records, _standardFolders);
        }

        [TearDown]
        public void TearDown()
        {
            // PackageDisplayNames is process-wide state; the package-root tests
            // populate it and every other test in the suite assumes it empty.
            PackageDisplayNames.Set(null);
        }

        // The list view's expansion flatten holds children of folders other
        // than the one being browsed, so it needs a signal for "some bucket
        // moved" that costs nothing to read. Everything that writes a bucket
        // has to move it, or an expanded subfolder renders stale rows.
        [Test]
        public void FolderContentsVersion_MovesForEveryBucketWrite_AndStandsStillOtherwise()
        {
            var afterRebuild = _service.FolderContentsVersion;

            Assert.That(_service.GetFolderContentsOrNull("Assets/Art"), Is.Not.Null);
            Assert.That(
                _service.FolderContentsVersion,
                Is.EqualTo(afterRebuild),
                "reading a bucket must not look like a change");

            var delta = _index.ApplyFileChanges(
                new[] { new AssetRecord("g-new", "Assets/Art/New.png", "New", "Texture2D") },
                Array.Empty<string>());
            _service.ApplyCatalogDelta(delta);
            var afterDelta = _service.FolderContentsVersion;
            Assert.That(afterDelta, Is.GreaterThan(afterRebuild));

            _service.RemoveEntry("Assets/Art/New.png");
            var afterRemove = _service.FolderContentsVersion;
            Assert.That(afterRemove, Is.GreaterThan(afterDelta));

            _service.InsertFolderEntry("Assets/Art/Fresh");
            Assert.That(_service.FolderContentsVersion, Is.GreaterThan(afterRemove));

            var beforeFullRebuild = _service.FolderContentsVersion;
            _service.RebuildCache(_records, _standardFolders);
            Assert.That(_service.FolderContentsVersion, Is.GreaterThan(beforeFullRebuild));
        }

        // m-11: the incremental bucket rebuild named package roots by their raw
        // id while the full rebuild named them by their manifest display name.
        // Display names here sort the OPPOSITE way to the ids, so a raw-name
        // rebuild is visible as both wrong text AND wrong order.
        [Test]
        public void ApplyCatalogDelta_RebuildingThePackagesBucket_MatchesTheFullRebuild()
        {
            var incremental = BuildPackagesProjectionIncrementally();
            var full = BuildPackagesProjectionFromScratch();

            Assert.That(
                incremental.Select(entry => entry.Name + " @ " + entry.Path),
                Is.EqualTo(full.Select(entry => entry.Name + " @ " + entry.Path)),
                "an incremental Packages rebuild must produce the same entries, "
                + "in the same order, as a full rebuild");
            Assert.That(
                incremental.Select(entry => entry.Name),
                Is.EqualTo(new[] { "Alpha Tools", "Middle Tools", "Zebra Tools" }));
        }

        [Test]
        public void ApplyCatalogDelta_RebuildingThePackagesBucket_KeepsPackageRootLookupWorking()
        {
            var incremental = BuildPackagesProjectionIncrementally();

            // Reveal/ping resolve a row through this lookup; a display-name
            // mismatch made its binary search miss every package root.
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(incremental, "Packages/com.z"),
                Is.EqualTo(0));
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(incremental, "Packages/com.m"),
                Is.EqualTo(1));
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(incremental, "Packages/com.a"),
                Is.EqualTo(2));
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(incremental, "Packages/com.absent"),
                Is.EqualTo(-1));
        }

        // Two package roots exist at the full rebuild; a third arrives as a
        // catalog delta whose affected parent is "Packages", which is what
        // makes the projection rebuild that bucket incrementally.
        private static IReadOnlyList<ZepAssetEntry> BuildPackagesProjectionIncrementally()
        {
            var service = CreatePackagesProjection(
                out var index,
                new[] { "Packages/com.a", "Packages/com.z" },
                new[]
                {
                    new ZepFolderEntry("Zebra Tools", "Packages/com.a", 1, isReadOnly: true),
                    new ZepFolderEntry("Alpha Tools", "Packages/com.z", 1, isReadOnly: true),
                });

            var delta = index.Apply(
                Array.Empty<string>(),
                Array.Empty<AssetCatalogMove>(),
                Array.Empty<AssetRecord>(),
                new[] { new AssetFolderRecord(AssetPath.From("Packages/com.m")) });
            Assert.That(delta.AffectedParentFolders, Has.Member("Packages"),
                "fixture guard: the delta must be the one that rebuilds the Packages bucket");
            service.ApplyCatalogDelta(delta);

            return service.BuildAssetEntries(
                "Packages",
                string.Empty,
                ZepSearchScope.Packages,
                string.Empty,
                string.Empty);
        }

        private static IReadOnlyList<ZepAssetEntry> BuildPackagesProjectionFromScratch()
        {
            var service = CreatePackagesProjection(
                out _,
                new[] { "Packages/com.a", "Packages/com.m", "Packages/com.z" },
                new[]
                {
                    new ZepFolderEntry("Zebra Tools", "Packages/com.a", 1, isReadOnly: true),
                    new ZepFolderEntry("Middle Tools", "Packages/com.m", 1, isReadOnly: true),
                    new ZepFolderEntry("Alpha Tools", "Packages/com.z", 1, isReadOnly: true),
                });

            return service.BuildAssetEntries(
                "Packages",
                string.Empty,
                ZepSearchScope.Packages,
                string.Empty,
                string.Empty);
        }

        private static BrowseProjectionService CreatePackagesProjection(
            out InMemoryAssetIndex index,
            IReadOnlyList<string> packageRootPaths,
            IReadOnlyList<ZepFolderEntry> packageFolderEntries)
        {
            PackageDisplayNames.Set(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Packages/com.a"] = "Zebra Tools",
                ["Packages/com.m"] = "Middle Tools",
                ["Packages/com.z"] = "Alpha Tools",
            });

            var folders = new List<AssetFolderRecord>
            {
                new AssetFolderRecord(AssetPath.From("Packages")),
            };
            foreach (var path in packageRootPaths)
            {
                folders.Add(new AssetFolderRecord(AssetPath.From(path)));
            }

            index = new InMemoryAssetIndex();
            index.ReplaceFolders(folders);

            var standardFolders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Packages", "Packages", 0, isReadOnly: true),
            };
            standardFolders.AddRange(packageFolderEntries);

            var service = new BrowseProjectionService(
                index,
                new AssetSearchService(index),
                new TagProjectionService(
                    new ThrowingGateway(),
                    new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { })));
            service.RebuildCache(Array.Empty<AssetRecord>(), standardFolders);
            return service;
        }

        [Test]
        public void BuildAssetEntries_NoFilters_ReturnsCachedFolderContents()
        {
            var entries = Build("Assets/Art", string.Empty);

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Sub", "Hero" }),
                "per-folder browse: child folders first, then files");
        }

        [Test]
        public void BuildAssetEntries_NoFilters_ExposesOrderAwarePathLookup()
        {
            var entries = Build("Assets/Art", string.Empty);

            Assert.That(entries, Is.InstanceOf<IAssetPathLookup>());
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(
                    entries,
                    "Assets/Art/Sub"),
                Is.EqualTo(0));
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(
                    entries,
                    "Assets/Art/Hero.png"),
                Is.EqualTo(1));
            Assert.That(
                ZepPathHelpers.FindAssetPathIndex(
                    entries,
                    "Assets/Art/Missing.asset"),
                Is.EqualTo(-1));
        }

        [Test]
        public void InsertFolderEntry_RepeatedImport_DoesNotDuplicateOrEraseChildren()
        {
            _service.InsertFolderEntry("Assets/Art");
            _service.InsertFolderEntry("Assets/Art");

            var rootEntries = Build("Assets", string.Empty);
            var artEntries = Build("Assets/Art", string.Empty);

            Assert.That(rootEntries.Count(e => e.Path == "Assets/Art"), Is.EqualTo(1));
            Assert.That(artEntries.Select(e => e.Path), Is.EqualTo(new[]
            {
                "Assets/Art/Sub",
                "Assets/Art/Hero.png",
            }), "reimporting an existing folder must preserve its cached children");
        }

        [Test]
        public void BuildAssetEntries_Search_CombinesFolderNameMatchesWithEngineResults()
        {
            var entries = Build("Assets", "hero");

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "HeroFolder", "Hero", "HeroController" }),
                "matching folders lead (name-sorted), then engine file results in engine order");
            Assert.That(entries.Select(e => e.Name), Has.None.EqualTo("HeroPkg"),
                "default Assets scope must exclude Packages records");
        }

        [Test]
        public void BuildAssetEntries_Search_MatchingFoldersAreNameSorted()
        {
            _standardFolders.Add(new ZepFolderEntry(
                "AHero",
                "Assets/AHero",
                1,
                isReadOnly: false));
            _service.RebuildCache(_records, _standardFolders);

            var entries = Build("Assets", "hero");

            Assert.That(entries.Select(e => e.Name).Take(2), Is.EqualTo(new[] { "AHero", "HeroFolder" }));
        }

        [Test]
        public void BuildAssetEntries_Search_DoesNotMatchFoldersOrFilesByParentPath()
        {
            // Regression (user-reported): searching a folder name surfaced every DESCENDANT
            // folder AND every asset under it, because the unqualified match folded in the
            // full path — e.g. "Sub" (Assets/Art/Sub) and Hero (Assets/Art/Hero.png) both
            // matched "art". Only the folder actually NAMED "Art" should match.
            var entries = Build("Assets", "art");

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Art" }),
                "only the folder named Art matches; its child folder Sub and files under Assets/Art must not");
        }

        [Test]
        public void BuildAssetEntries_CurrentFolderScope_RestrictsFoldersAndFiles()
        {
            var entries = Build("Assets/Art", "hero", ZepSearchScope.CurrentFolder);

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Hero" }),
                "HeroFolder and HeroController live outside Assets/Art and must not appear");
        }

        [Test]
        public void BuildAssetEntries_RootedScopeClampsExplicitAssetsSearchToRoot()
        {
            var entries = _service.BuildAssetEntries(
                "Assets/Art",
                "hero",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                "Assets/Art");

            Assert.That(entries.Select(entry => entry.Name), Is.EqualTo(new[] { "Hero" }));
            Assert.That(entries.Select(entry => entry.Name), Has.None.EqualTo("HeroFolder"));
            Assert.That(entries.Select(entry => entry.Name), Has.None.EqualTo("HeroController"));
        }

        [Test]
        public void BuildAssetEntries_TwoPaneQueriesRemainIndependent()
        {
            var art = _service.BuildAssetEntries(
                "Assets/Art",
                "hero",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                "Assets/Art");
            var scripts = _service.BuildAssetEntries(
                "Assets/Scripts",
                "hero",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                "Assets/Scripts");

            Assert.That(art.Select(entry => entry.Name), Is.EqualTo(new[] { "Hero" }));
            Assert.That(
                scripts.Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroController" }));
        }

        [Test]
        public void LargeFolderSearch_UsesPostingCandidatesInsteadOfTheWholeFolderSet()
        {
            const int folderCount = 10000;
            var records = new List<AssetRecord>();
            var index = new InMemoryAssetIndex();
            var folders = new List<ZepFolderEntry>(folderCount + 1)
            {
                new ZepFolderEntry(
                    "Assets",
                    "Assets",
                    0,
                    isReadOnly: false)
            };
            for (var i = 0; i < folderCount; i++)
            {
                folders.Add(new ZepFolderEntry(
                    "Folder" + i.ToString("D5"),
                    "Assets/Folder" + i.ToString("D5"),
                    1,
                    isReadOnly: false));
            }

            var search = new AssetSearchService(index);
            var tags = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            var service = new BrowseProjectionService(index, search, tags);
            service.RebuildCache(records, folders);
            search.WarmIndex();

            var results = service.BuildAssetEntries(
                "Assets",
                "folder09999",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty);

            Assert.That(results.Select(entry => entry.Path),
                Is.EqualTo(new[] { "Assets/Folder09999" }));
            Assert.That(service.LastFolderSearchCandidateCount, Is.LessThan(32),
                "the folder matcher must verify the small gram intersection, not all 10,001 folders");
        }

        [Test]
        public void BuildAssetEntries_PackagesScope_ExcludesAssets()
        {
            var entries = Build("Assets", "hero", ZepSearchScope.Packages);

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "HeroPkg" }));
        }

        [Test]
        public void BuildAssetEntries_TypeFilter_CombinesWithSearch()
        {
            var scripts = Build("Assets", "hero", typeFilter: "MonoScript");
            Assert.That(scripts.Select(e => e.Name), Is.EqualTo(new[] { "HeroController" }),
                "type filter drops folders (not MonoScript) and non-matching files");

            var folders = Build("Assets", "hero", typeFilter: "Folder");
            Assert.That(folders.Select(e => e.Name), Is.EqualTo(new[] { "HeroFolder" }),
                "Folder filter keeps matching folders and drops all file records");
        }

        [Test]
        public void BuildAssetEntries_UnityTypeQueriesApplyConsistentlyToFoldersAndFiles()
        {
            var folders = Build("Assets", "name:hero t:folder");
            var files = Build("Assets", "name:hero t:file");
            var mixed = Build("Assets", "name:hero t:folder t:script");

            Assert.That(folders.Select(entry => entry.Name), Is.EqualTo(new[] { "HeroFolder" }));
            Assert.That(files.Select(entry => entry.Name), Is.EqualTo(new[] { "Hero", "HeroController" }));
            Assert.That(mixed.Select(entry => entry.Name), Is.EqualTo(new[] { "HeroFolder", "HeroController" }),
                "repeated t: clauses form one OR-group across both folder and file documents");
            Assert.That(Build("Assets", "name:hero t:object").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder", "Hero", "HeroController" }));
        }

        [Test]
        public void BuildAssetEntries_UnityFolderLabelsAndGuidsUseSharedGrammar()
        {
            Assert.That(
                Build("Assets", "l:\"folder workflow\" t:folder").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder" }));
            Assert.That(
                Build("Assets", "l=\"folder workflow\"").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder" }),
                "exact folder labels compare one complete Unity label");
            Assert.That(Build("Assets", "l=workflow"), Is.Empty,
                "an exact label must not match only part of a folder label");
            Assert.That(
                Build("Assets", "guid:f0123456").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder" }));
            Assert.That(
                Build("Assets", "folder workflow t:folder").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder" }),
                "plain required terms include a folder's Unity labels just like file labels");
            Assert.That(Build("Assets", "l:\"folder workflow\" -t:folder"), Is.Empty);
        }

        [TestCase("guid:f", false)]
        [TestCase("guid:ef", false)]
        [TestCase("guid:cdef", false)]
        [TestCase("guid:6701", false)]
        [TestCase("guid:0123456789", false)]
        [TestCase("guid:0", true)]
        [TestCase("guid:67", true)]
        [TestCase("guid:3456", true)]
        [TestCase("guid:01234567", true)]
        [TestCase("guid:0123456701", true)]
        [TestCase("guid:012345670123456789ABCDEFABCDEFAB", true)]
        [TestCase("guid=01234567", false)]
        [TestCase("guid=012345670123456789abcdefabcdefab", true)]
        [TestCase("guid=012345670123456789abcdefabcdefa0", false)]
        public void BuildAssetEntries_GuidBoundaryAndNegationAgreeForFilesAndFolders(string query, bool matches)
        {
            const string guid = "012345670123456789abcdefabcdefab";
            var record = new AssetRecord(guid, "Assets/Alpha.asset", "Alpha", "Object");
            var folder = new ZepFolderEntry("Alpha", "Assets/Alpha", 1, false, guid: guid);
            _index.Replace(new[] { record }, new[]
            {
                new AssetFolderRecord(AssetPath.From(folder.Path), guid)
            });
            _service.RebuildCache(new[] { record }, new[] { folder });
            var paths = new[] { folder.Path, record.AssetPath };

            Assert.That(Build("Assets", query).Select(entry => entry.Path),
                Is.EqualTo(matches ? paths : Array.Empty<string>()));
            Assert.That(Build("Assets", "-" + query).Select(entry => entry.Path),
                Is.EqualTo(matches ? Array.Empty<string>() : paths));
        }

        [Test]
        public void BuildAssetEntries_UnityLabelsStayDistinctFromFinderFolderTags()
        {
            _tagProjection.ApplyAssignment("Assets/HeroFolder", new[] { "urgent" }, isFolder: true);

            var labelResults = Build("Assets", "l:gameplay");
            var finderTagResults = Build("Assets", "tag:urgent");

            Assert.That(labelResults.Select(entry => entry.Name), Is.EqualTo(new[] { "Hero" }));
            Assert.That(finderTagResults.Select(entry => entry.Name), Is.EqualTo(new[] { "HeroFolder" }));
            Assert.That(Build("Assets", "l:urgent"), Is.Empty,
                "a Finder-style folder tag must never masquerade as a Unity asset label");
        }

        [Test]
        public void BuildAssetEntries_AdvancedFieldsAndMalformedQueriesUseTheSharedGrammar()
        {
            Assert.That(Build("Assets", "glob:**/scripts/*.cs").Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroController" }));
            Assert.That(Build("Assets", "a:packages name:hero", ZepSearchScope.All).Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroPkg" }));
            Assert.That(Build("Assets", "unknown:hero"), Is.Empty,
                "unknown qualifiers remain literal instead of broadening to 'hero'");
            Assert.That(Build("Assets", "t:"), Is.Empty);
            Assert.That(Build("Assets", "a:not-a-real-area"), Is.Empty);
            Assert.That(Build("Assets", "glob:{cs}"), Is.Empty);
        }

        [TestCase("glob:**/*a*")]
        [TestCase("glob:*a*")]
        [TestCase("glob:**/*")]
        [TestCase("glob:{**/*Hero*,**/*a*}")]
        [TestCase("glob:{**/*a*,**/*Hero*}")]
        [TestCase("glob:**/*{Hero,a}*")]
        [TestCase("**/*a*")]
        [TestCase("*a*")]
        [TestCase("**/*")]
        [TestCase("{**/*Hero*,**/*a*}")]
        [TestCase("**/*{Hero,a}*")]
        public void BuildAssetEntries_BroadGlobIsInvalidForFilesAndFolders(string query)
        {
            Assert.That(Build("Assets", query), Is.Empty);
            Assert.That(Build("Assets", "-" + query), Is.Empty,
                "broad excluded patterns must use the same invalid-query rule as positive patterns");
        }

        [TestCase("glob:**/*Hero*")]
        [TestCase("**/*Hero*")]
        [TestCase("glob:**/*{Hero,absent}*")]
        [TestCase("**/*{Hero,absent}*")]
        [TestCase("path:Hero")]
        public void BuildAssetEntries_SelectiveGlobsAndPathFiltersStillMatchFilesAndFolders(string query)
        {
            Assert.That(Build("Assets", query).Select(entry => entry.Name),
                Is.EqualTo(new[] { "HeroFolder", "Hero", "HeroController" }));
            Assert.That(Build("Assets", "-" + query).Select(entry => entry.Name),
                Is.EquivalentTo(new[] { "Art", "Scripts", "Sub", "Villain" }));
        }

        [Test]
        public void BuildAssetEntries_TagFilter_CombinesWithSearch()
        {
            _tagProjection.ApplyAssignment("Assets/Art/Hero.png", new[] { "urgent" }, isFolder: false);

            var entries = Build("Assets", "hero", tagFilterId: "urgent");

            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Hero" }),
                "untagged folder and file matches must drop when a tag filter is active");
        }

        [Test]
        public void BuildAssetEntries_TagOnlyFilter_CannotEscapeRootedScope()
        {
            _tagProjection.ApplyAssignment("Assets/Art/Hero.png", new[] { "urgent" }, isFolder: false);
            _tagProjection.ApplyAssignment("Assets/Scripts/HeroController.cs", new[] { "urgent" }, isFolder: false);

            var entries = Build(
                "Assets/Art",
                string.Empty,
                ZepSearchScope.Assets,
                tagFilterId: "urgent",
                rootFolderPath: "Assets/Art");

            Assert.That(entries.Select(entry => entry.Path),
                Is.EqualTo(new[] { "Assets/Art/Hero.png" }),
                "a rooted pane must remain clamped when a tag is the only active filter");
        }

        [Test]
        public void BuildAssetEntries_TagOnlyFilter_UsesIncrementalTagMembership()
        {
            _tagProjection.ApplyAssignment(
                "Assets/HeroFolder",
                new[] { "urgent" },
                isFolder: true);
            _tagProjection.ApplyAssignment(
                "Assets/Art/Hero.png",
                new[] { "urgent" },
                isFolder: false);

            var entries = Build(
                "Assets",
                string.Empty,
                tagFilterId: "urgent");

            Assert.That(entries.Select(entry => entry.Path), Is.EqualTo(new[]
            {
                "Assets/HeroFolder",
                "Assets/Art/Hero.png",
            }));

            _tagProjection.ClearAssignment("Assets/Art/Hero.png");
            _service.RefreshParentFolders(new[] { "Assets/Art" });
            entries = Build(
                "Assets",
                string.Empty,
                tagFilterId: "urgent");

            Assert.That(
                entries.Select(entry => entry.Path),
                Is.EqualTo(new[] { "Assets/HeroFolder" }));
        }

        [Test]
        public void BuildAssetEntries_RepeatedDirectCalls_DoNotRetainAProjectionMemo()
        {
            var first = Build("Assets", "hero");
            var second = Build("Assets", "hero");

            Assert.That(second, Is.Not.SameAs(first),
                "the projection owner must not hide duplicate callers behind a stale one-slot memo");
            Assert.That(
                second.Select(entry => entry.Path),
                Is.EqualTo(first.Select(entry => entry.Path)));

            _search.ApplyCatalogDelta(_index.ApplyFileChanges(
                new[] { new AssetRecord("g-new", "Assets/HeroNew.png", "HeroNew", "Texture2D") },
                Array.Empty<string>()));
            var third = Build("Assets", "hero");

            Assert.That(third.Select(e => e.Name), Has.Member("HeroNew"));
        }

        [Test]
        public void BuildAssetEntries_SearchResults_AreLazyUntilIndexed()
        {
            var entries = Build("Assets", "hero");
            var lazy = entries as LazyAssetEntryList;

            Assert.That(lazy, Is.Not.Null, "search results must flow through the lazy wrapper");
            Assert.That(lazy.Count, Is.EqualTo(3));

            // PathAt / ContainsPath are the allocation-free lookups used by
            // selection reconciliation — they must not materialize entries.
            Assert.That(lazy.PathAt(0), Is.EqualTo("Assets/HeroFolder"));
            Assert.That(lazy.PathAt(1), Is.EqualTo("Assets/Art/Hero.png"));
            Assert.That(lazy.ContainsPath("Assets/Scripts/HeroController.cs"), Is.True);
            Assert.That(lazy.ContainsPath("Assets/Art/Sub/Villain.png"), Is.False);
            Assert.That(lazy.MaterializedCount, Is.EqualTo(0), "path lookups must stay lazy");

            // Indexer materializes exactly the touched file entry; the folder
            // side is pre-built and does not count.
            Assert.That(lazy[1].Name, Is.EqualTo("Hero"));
            Assert.That(lazy[1].TypeName, Is.EqualTo("Texture2D"));
            Assert.That(lazy.MaterializedCount, Is.EqualTo(1));
            Assert.That(lazy[0].TypeName, Is.EqualTo("Folder"));
            Assert.That(lazy.MaterializedCount, Is.EqualTo(1));
        }

        // A lone "-" is a real keystroke on the way to an exclusion term. It is
        // non-blank (so the projection takes the SEARCH path) but parses to an
        // empty query, and the All scope resolves to no scope path — the one
        // combination where the engine used to hand back its live record list.
        // The wrapper is bound to a ListView, so it must not grow or empty
        // itself when the index is patched afterwards.
        [Test]
        public void BuildAssetEntries_EmptyParsedQueryAtAllScope_IsNotAliasedToTheLiveIndex()
        {
            var entries = Build("Assets", "-", ZepSearchScope.All);
            var countWhenBound = entries.Count;

            Assert.That(countWhenBound, Is.EqualTo(_standardFolders.Count + _records.Count),
                "an empty parsed query at All scope matches every folder and record "
);

            _search.ApplyCatalogDelta(_index.ApplyFileChanges(
                new[] { new AssetRecord("g-new", "Assets/HeroNew.png", "HeroNew", "Texture2D") },
                Array.Empty<string>()));

            Assert.That(entries.Count, Is.EqualTo(countWhenBound),
                "a result already handed to the UI must not change under an index patch");
        }

        // Audit §3.12: FolderSearchIndex posted every 1-, 2- AND 3-gram of five fields
        // (~15 KB per folder). Dropping the 1- and 2-gram levels changes only the
        // CANDIDATE set — the authoritative folder matcher decides the results — so
        // these three queries, whose candidate sets sit on either side of the trigram
        // boundary, must return exactly the same folders as before the change.
        [TestCase("p", new[] { "Alpha", "Help", "Plants", "Playmode", "Sample", "Split" })]
        // "Sample" is in the "pl" row and not in the "pla" row on purpose: sa-m-PL-e.
        [TestCase("pl", new[] { "Plants", "Playmode", "Sample", "Split" })]
        [TestCase("pla", new[] { "Plants", "Playmode" })]
        public void FolderSearch_ForShortTokens_ReturnsTheSameFoldersAsTheTrigramBoundaryAllows(
            string searchText,
            string[] expectedNames)
        {
            var service = CreateFolderSearchFixture();

            var results = service.BuildAssetEntries(
                "Assets",
                searchText,
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty);

            Assert.That(
                results.Select(entry => entry.Name),
                Is.EqualTo(expectedNames),
                "short-token folder results must not depend on which gram lengths are posted");
        }

        // A tag token can only reach a path through one of the path's tags, and
        // the tag→paths relation is already inverted in the projection. These
        // sets are written out by hand from the fixture so they hold whichever
        // side of the lookup answers them.
        [Test]
        public void AppendPathsMatchingSearchToken_MatchesTagNamesAndColours()
        {
            var definitions = new List<TagDefinitionRecord>
            {
                new TagDefinitionRecord { Id = "t-urgent", Name = "Urgentia", Color = ZepTagColor.Red },
                new TagDefinitionRecord { Id = "t-review", Name = "Reviewable", Color = ZepTagColor.Blue },
                new TagDefinitionRecord { Id = "t-idle", Name = "Untouched", Color = ZepTagColor.None },
            };
            var tags = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(definitions, () => { }));

            var expectedUrgent = new List<string>();
            var expectedReview = new List<string>();
            for (var i = 0; i < 500; i++)
            {
                var path = "Assets/Tagged/Item" + i.ToString("D3") + ".asset";
                var assigned = new List<string>();
                if (i % 3 == 0)
                {
                    assigned.Add("t-urgent");
                    expectedUrgent.Add(path);
                }

                if (i % 5 == 0)
                {
                    assigned.Add("t-review");
                    expectedReview.Add(path);
                }

                if (assigned.Count > 0)
                {
                    tags.ApplyAssignment(path, assigned, isFolder: false);
                }
            }

            Assert.That(expectedUrgent, Has.Count.EqualTo(167));
            Assert.That(expectedReview, Has.Count.EqualTo(100));

            // Partial, unqualified: a plain word may match a tag name.
            Assert.That(
                MatchingPaths(tags, SearchField.Any, "urgent"),
                Is.EquivalentTo(expectedUrgent));

            // The colour name is part of the same searchable text, and every
            // red-tagged path answers to it.
            Assert.That(
                MatchingPaths(tags, SearchField.Any, "red"),
                Is.EquivalentTo(expectedUrgent));

            // tag:reviewable is the exact form; it must match the whole name
            // only, and the paths carrying both tags belong to both answers.
            Assert.That(
                MatchingPaths(tags, SearchField.Tag, "reviewable", SearchMatchMode.Exact),
                Is.EquivalentTo(expectedReview));
            Assert.That(
                MatchingPaths(tags, SearchField.Tag, "reviewabl", SearchMatchMode.Exact),
                Is.Empty,
                "an exact tag term must not match a prefix of the name");

            // A tag nobody carries contributes nothing, and neither does a
            // token no tag can satisfy.
            Assert.That(MatchingPaths(tags, SearchField.Any, "untouched"), Is.Empty);
            Assert.That(MatchingPaths(tags, SearchField.Any, "zzz"), Is.Empty);
        }

        // Folder search asks the tag projection about every unqualified term.
        // Building the union set for terms that are nobody's tag was one
        // HashSet per term per keystroke, and filling it copied the large side.
        [Test]
        public void FolderSearch_AnUnqualifiedTermThatIsNobodysTag_AllocatesNoCandidateSet()
        {
            var definitions = new List<TagDefinitionRecord>
            {
                new TagDefinitionRecord { Id = "t-hero", Name = "Heroic", Color = ZepTagColor.Red },
            };
            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            var tags = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(definitions, () => { }));
            var service = new BrowseProjectionService(index, search, tags);
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Sample", "Assets/Sample", 1, isReadOnly: false),
                new ZepFolderEntry("Heroics", "Assets/Heroics", 1, isReadOnly: false),
            };
            service.RebuildCache(new List<AssetRecord>(), folders);
            search.WarmIndex();
            tags.ApplyAssignment("Assets/Sample", new[] { "t-hero" }, isFolder: true);

            var before = service.FolderSearchTokenCandidateSets;
            var noTagMatch = service.BuildAssetEntries(
                "Assets",
                "sample",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty);

            Assert.That(
                noTagMatch.Select(entry => entry.Path),
                Has.Member("Assets/Sample"));
            Assert.That(
                service.FolderSearchTokenCandidateSets,
                Is.EqualTo(before),
                "a term no tag can satisfy must not allocate a union set");

            var tagMatch = service.BuildAssetEntries(
                "Assets",
                "heroic",
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty);

            // The tag-carrying folder is found through the tag, the
            // name-carrying one through the postings — both survive the union.
            Assert.That(
                tagMatch.Select(entry => entry.Path),
                Is.EquivalentTo(new[] { "Assets/Sample", "Assets/Heroics" }));
            Assert.That(
                service.FolderSearchTokenCandidateSets,
                Is.EqualTo(before + 1),
                "a term that IS a tag name allocates exactly one union set");
        }

        private static HashSet<string> MatchingPaths(
            TagProjectionService tags,
            SearchField field,
            string normalizedValue,
            SearchMatchMode matchMode = SearchMatchMode.Partial)
        {
            var destination = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            tags.AppendPathsMatchingSearchToken(
                SearchToken.CreateNormalized(field, normalizedValue, matchMode),
                destination);
            return destination;
        }

        // 500 folders whose names carry NO "p" except the six signal folders, so the
        // expected sets above can be read straight off the fixture.
        private static BrowseProjectionService CreateFolderSearchFixture()
        {
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Sample", "Assets/Sample", 1, isReadOnly: false),
                new ZepFolderEntry("Split", "Assets/Split", 1, isReadOnly: false),
                new ZepFolderEntry("Plants", "Assets/Plants", 1, isReadOnly: false),
                new ZepFolderEntry("Playmode", "Assets/Playmode", 1, isReadOnly: false),
                new ZepFolderEntry("Help", "Assets/Help", 1, isReadOnly: false),
                new ZepFolderEntry("Alpha", "Assets/Alpha", 1, isReadOnly: false),
            };
            for (var i = 0; i < 494; i++)
            {
                var name = "Zone_" + i.ToString("D3");
                folders.Add(new ZepFolderEntry(
                    name,
                    "Assets/" + name,
                    1,
                    isReadOnly: false));
            }

            var index = new InMemoryAssetIndex();
            var search = new AssetSearchService(index);
            var tags = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            var service = new BrowseProjectionService(index, search, tags);
            service.RebuildCache(new List<AssetRecord>(), folders);
            search.WarmIndex();
            return service;
        }

        // §3.12 step 2: the folder path postings were the single biggest part of
        // the folder index (a full path posts ~100 trigrams into HashSet<string>
        // postings) and served only these three fields. They are a candidate
        // pre-filter, never the answer — FolderMatchesSearch decides — so the
        // fallback to "every folder" has to produce identical results. The
        // expectations below were written by hand and validated against the
        // pre-change code first, so this test is an equivalence pin, not a
        // description of the new implementation.
        [Test]
        public void FolderSearch_PathDirAndAreaFields_ResolveTheSameFoldersAcrossAFiveHundredFolderCatalog()
        {
            var service = CreateLargeFolderCatalogProjection();

            Assert.That(
                LargeCatalogPaths(service, "path:zone01"),
                Is.EqualTo(new[]
                {
                    "Assets/Zone010",
                    "Assets/Zone011",
                    "Assets/Zone012",
                    "Assets/Zone013",
                    "Assets/Zone014",
                    "Assets/Zone015",
                    "Assets/Zone016",
                    "Assets/Zone017",
                    "Assets/Zone018",
                    "Assets/Zone019",
                }),
                "path: matches the folder's own path, and only the ten Zone01x folders contain it");

            Assert.That(
                LargeCatalogPaths(service, "dir:com.tools path:zone49"),
                Is.EqualTo(new[]
                {
                    "Packages/com.tools/Zone490",
                    "Packages/com.tools/Zone491",
                    "Packages/com.tools/Zone492",
                    "Packages/com.tools/Zone493",
                    "Packages/com.tools/Zone494",
                    "Packages/com.tools/Zone495",
                    "Packages/com.tools/Zone496",
                    "Packages/com.tools/Zone497",
                    "Packages/com.tools/Zone498",
                    "Packages/com.tools/Zone499",
                }),
                "dir: matches a whole ANCESTOR segment of the path");

            Assert.That(
                LargeCatalogPaths(service, "dir:zone249"),
                Is.Empty,
                "a folder's own last segment is not one of its ancestor directories");

            Assert.That(
                LargeCatalogPaths(service, "a:packages path:zone25"),
                Is.EqualTo(new[]
                {
                    "Packages/com.tools/Zone250",
                    "Packages/com.tools/Zone251",
                    "Packages/com.tools/Zone252",
                    "Packages/com.tools/Zone253",
                    "Packages/com.tools/Zone254",
                    "Packages/com.tools/Zone255",
                    "Packages/com.tools/Zone256",
                    "Packages/com.tools/Zone257",
                    "Packages/com.tools/Zone258",
                    "Packages/com.tools/Zone259",
                }));

            Assert.That(
                LargeCatalogPaths(service, "a:assets path:zone25"),
                Is.Empty,
                "the Zone25x folders live under Packages, so the Assets area must exclude them");

            Assert.That(
                LargeCatalogPaths(service, "path:nosuchfolder"),
                Is.Empty);

            Assert.That(
                LargeCatalogPaths(service, "path:zone").Count,
                Is.EqualTo(500),
                "the shared substring must still reach every Zone folder");
        }

        private static IReadOnlyList<string> LargeCatalogPaths(
            BrowseProjectionService service,
            string searchText)
        {
            return service.BuildAssetEntries(
                    "Assets",
                    searchText,
                    ZepSearchScope.All,
                    string.Empty,
                    string.Empty)
                .Select(entry => entry.Path)
                .ToList();
        }

        private static BrowseProjectionService CreateLargeFolderCatalogProjection()
        {
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Packages", "Packages", 0, isReadOnly: true),
                new ZepFolderEntry("com.tools", "Packages/com.tools", 1, isReadOnly: true),
            };
            for (var i = 0; i < 500; i++)
            {
                var name = "Zone" + i.ToString("D3");
                folders.Add(i < 250
                    ? new ZepFolderEntry(name, "Assets/" + name, 1, isReadOnly: false)
                    : new ZepFolderEntry(
                        name,
                        "Packages/com.tools/" + name,
                        2,
                        isReadOnly: true));
            }

            var index = new InMemoryAssetIndex();
            index.ReplaceFolders(folders
                .Select(folder => new AssetFolderRecord(AssetPath.From(folder.Path)))
                .ToList());
            var search = new AssetSearchService(index);
            var tags = new TagProjectionService(
                new ThrowingGateway(),
                new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { }));
            var service = new BrowseProjectionService(index, search, tags);
            service.RebuildCache(new List<AssetRecord>(), folders);
            search.WarmIndex();
            return service;
        }

        // RebuildFolderBucket's comparator is a direct three-way now instead of
        // asking the boolean ShouldInsertBefore in both directions. The ordering
        // must be bit-for-bit what it was: folders first, then name, then path
        // as the tiebreak — so the fixture is deliberately UNSORTED and carries
        // three entries whose names are equal (a folder and two files all called
        // "Dup"), which is the only place the path tiebreak is observable.
        [Test]
        public void FolderBucket_OrdersFoldersFirstThenNameThenPath()
        {
            var records = new List<AssetRecord>
            {
                new AssetRecord("g-zeta", "Assets/Bucket/Zeta.png", "Zeta", "Texture2D"),
                new AssetRecord("g-duppng", "Assets/Bucket/Dup.png", "Dup", "Texture2D"),
                new AssetRecord("g-dupmat", "Assets/Bucket/Dup.mat", "Dup", "Material"),
                new AssetRecord("g-alphapng", "Assets/Bucket/alpha.png", "alpha", "Texture2D"),
            };
            var folders = new List<ZepFolderEntry>
            {
                new ZepFolderEntry("Assets", "Assets", 0, isReadOnly: false),
                new ZepFolderEntry("Bucket", "Assets/Bucket", 1, isReadOnly: false),
                new ZepFolderEntry("Dup", "Assets/Bucket/Dup", 2, isReadOnly: false),
                new ZepFolderEntry("Alpha", "Assets/Bucket/Alpha", 2, isReadOnly: false),
            };

            var index = new InMemoryAssetIndex();
            foreach (var record in records)
            {
                index.Upsert(record);
            }

            index.ReplaceFolders(folders
                .Select(folder => new AssetFolderRecord(AssetPath.From(folder.Path)))
                .ToList());
            var service = new BrowseProjectionService(
                index,
                new AssetSearchService(index),
                new TagProjectionService(
                    new ThrowingGateway(),
                    new TagDefinitionStore(new List<TagDefinitionRecord>(), () => { })));
            service.RebuildCache(records, folders);

            var bucket = service.BuildAssetEntries(
                "Assets/Bucket",
                string.Empty,
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                null);

            Assert.That(
                bucket.Select(entry => entry.Path).ToArray(),
                Is.EqualTo(new[]
                {
                    "Assets/Bucket/Alpha",
                    "Assets/Bucket/Dup",
                    "Assets/Bucket/alpha.png",
                    "Assets/Bucket/Dup.mat",
                    "Assets/Bucket/Dup.png",
                    "Assets/Bucket/Zeta.png",
                }));
        }

        // GetMetadataCandidates resolves each gram's posting list once per call
        // now instead of re-packing and re-probing every gram for every candidate
        // in the intersection loop. Semantics are unchanged, so this guards the
        // failure the rewrite could introduce: a multi-gram term whose grams all
        // exist separately but never together must still narrow to the folder
        // that has all of them.
        [Test]
        public void FolderSearch_MultiGramTerm_IntersectsAcrossEveryGram()
        {
            Assert.That(
                Build("Assets", "herofolder").Select(entry => entry.Path).ToArray(),
                Is.EqualTo(new[] { "Assets/HeroFolder" }),
                "every trigram of the term must be required, not just the rarest one");

            Assert.That(
                Build("Assets", "folderhero").Select(entry => entry.Path),
                Has.No.Member("Assets/HeroFolder"),
                "a term whose grams exist but not in that order must not match the folder by name");
        }

        private IReadOnlyList<ZepAssetEntry> Build(
            string currentFolderPath,
            string searchText,
            ZepSearchScope scope = ZepSearchScope.Assets,
            string typeFilter = "",
            string tagFilterId = "",
            string rootFolderPath = null)
        {
            return _service.BuildAssetEntries(
                currentFolderPath,
                searchText,
                scope,
                typeFilter,
                tagFilterId,
                rootFolderPath);
        }

        // The projection flows under test must never touch the asset gateway;
        [Test]
        public void FirstPaintMetadata_PendingPresentationDoesNotPromoteCatalog()
        {
            var index = new InMemoryAssetIndex();
            var gateway = new ThrowingGateway { TagIds = new[] { "blue" } };
            var definitions = new TagDefinitionStore(
                new List<TagDefinitionRecord>
                {
                    new TagDefinitionRecord
                    {
                        Id = "blue",
                        Name = "Blue",
                        Color = ZepTagColor.Blue
                    }
                },
                () => { });
            var tags = new TagProjectionService(gateway, definitions);
            var search = new AssetSearchService(index);
            var service = new BrowseProjectionService(index, search, tags);
            service.RebuildCache(Array.Empty<AssetRecord>(), Array.Empty<ZepFolderEntry>());
            tags.RebuildFilterOptions();
            var emptyAuthorityFilterOptionCount = tags.CachedFilterOptions.Count;
            service.ApplyCatalogDelta(index.ApplyDirectObservation(
                new DirectFolderObservation(
                    AssetPath.From("Assets"),
                    new[]
                    {
                        new DirectFolderEntry(
                            AssetPath.From("Assets/PendingFolder"), true, "PendingFolder", "Folder"),
                        new DirectFolderEntry(
                            AssetPath.From("Assets/Pending.asset"), false, "Pending", "ScriptableObject")
                    },
                    isComplete: true),
                index.Version));

            var entries = service.BuildAssetEntries(
                "Assets",
                string.Empty,
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                null);
            var entry = entries.Single(candidate => candidate.Path == "Assets/Pending.asset");
            var folder = entries.Single(candidate => candidate.Path == "Assets/PendingFolder");

            Assert.That(entry.Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
            Assert.That(folder.Tags.Select(tag => tag.Id), Is.EqualTo(new[] { "blue" }));
            Assert.That(folder.FolderTintColor, Is.EqualTo(ZepTagColor.Blue));
            Assert.That(index.TryGetByPath("Assets/Pending.asset", out _), Is.False);
            Assert.That(search.Search("Pending"), Is.Empty);
            Assert.That(search.Search(string.Empty), Is.Empty);
            Assert.That(tags.TagsByPath, Is.Empty);
            Assert.That(tags.EnumeratePathsForTag("blue"), Is.Empty);
            Assert.That(tags.MatchesTagFilter("Assets/Pending.asset", "blue"), Is.False);
            tags.RebuildFilterOptions();
            Assert.That(tags.CachedFilterOptions.Count, Is.EqualTo(emptyAuthorityFilterOptionCount));

            var refreshedEntries = service.BuildAssetEntries(
                "Assets",
                string.Empty,
                ZepSearchScope.Assets,
                string.Empty,
                string.Empty,
                null);
            Assert.That(
                refreshedEntries.Single(candidate => candidate.Path == "Assets/Pending.asset").Tags.Select(tag => tag.Id),
                Is.EqualTo(new[] { "blue" }));
            Assert.That(
                refreshedEntries.Single(candidate => candidate.Path == "Assets/PendingFolder").FolderTintColor,
                Is.EqualTo(ZepTagColor.Blue));
            Assert.That(search.Search("Pending"), Is.Empty);
            Assert.That(tags.TagsByPath, Is.Empty);
            Assert.That(tags.EnumeratePathsForTag("blue"), Is.Empty);
            Assert.That(tags.MatchesTagFilter("Assets/Pending.asset", "blue"), Is.False);
            tags.RebuildFilterOptions();
            Assert.That(tags.CachedFilterOptions.Count, Is.EqualTo(emptyAuthorityFilterOptionCount));
        }

        // every member throws so an unexpected dependency fails loudly.
        private sealed class ThrowingGateway : IProjectAssetGateway
        {
            public ProjectFileOperationResult StoreUndoPayload(AssetPath assetPath, AssetPath payloadPath = default) => throw new NotSupportedException();
            public ProjectFileOperationResult RestoreUndoPayload(AssetPath payloadPath, AssetPath destinationPath) => throw new NotSupportedException();
            public ProjectFileOperationResult DeleteUndoPayload(AssetPath payloadPath) => throw new NotSupportedException();
            public int TagVersion => 0;

            internal IReadOnlyList<string> TagIds { get; set; } = Array.Empty<string>();

            public bool Exists(AssetPath assetPath) => throw new NotSupportedException();

            public bool IsFolder(AssetPath assetPath) => throw new NotSupportedException();

            public bool CanWrite(AssetPath assetPath) => throw new NotSupportedException();

            public bool CanTag(AssetPath assetPath) => throw new NotSupportedException();

            public bool EnsureFolder(AssetPath folderPath) => throw new NotSupportedException();

            public ProjectFileOperationResult CreateFolder(AssetPath parentFolderPath, string folderName) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Move(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Copy(AssetPath sourcePath, AssetPath destinationPath, bool allowOverwriteWithUniquePath) =>
                throw new NotSupportedException();

            public ProjectFileOperationResult Delete(AssetPath assetPath) => throw new NotSupportedException();

            // No identity to re-mint: these fakes hold no .meta files.

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath) => TagIds;

            public IReadOnlyList<string> GetTagIds(AssetPath assetPath, string knownGuid) => GetTagIds(assetPath);

            public ProjectFileOperationResult SetTagIds(IReadOnlyDictionary<AssetPath, IReadOnlyList<string>> tagIdsByAssetPath) =>
                throw new NotSupportedException();

            public bool Open(AssetPath assetPath) => throw new NotSupportedException();

            public void Reveal(AssetPath assetPath) => throw new NotSupportedException();

            public void BeginBatch() => throw new NotSupportedException();

            public void EndBatch() => throw new NotSupportedException();

            public string GetGuid(AssetPath assetPath) => throw new NotSupportedException();
            public string GetPathForGuid(string assetGuid) => throw new NotSupportedException();

            public string ResolveAbsolutePath(AssetPath assetPath) => throw new NotSupportedException();
        }
    }
}
