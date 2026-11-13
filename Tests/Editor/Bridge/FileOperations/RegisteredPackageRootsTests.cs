using System;
using Zep.Core.Indexing;
using Zep.Core.FileOperations;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;

namespace Zep.Tests.Editor.Bridge.FileOperations
{
    // 4.9: Refresh() ran on every projectChanged — every import batch — and
    // stat-ed each of the project's 40-90 registered packages (GetFullPath +
    // Directory.Exists) only to rebuild a byte-identical snapshot. The package
    // list itself is the freshness test.
    //
    // The existing query override models unavailable Package Manager responses.
    // Successful responses reuse real captured PackageInfo objects; registration
    // gating must still avoid rebuilding an unchanged complete mapping.
    public sealed class RegisteredPackageRootsTests
    {
        // Refresh() runs on every projectChanged. It used to ask Package Manager
        // for the whole registered list each time; now only a registration event
        // (or a domain reload) re-arms that query, and an event that turns out to
        // change nothing must disarm it again.
        [Test]
        public void Refresh_QueriesPackageManagerOnlyAfterARegistrationEvent()
        {
            var first = RegisteredPackageRoots.Refresh();
            Assert.That(first.IsComplete, Is.True, "fixture guard: Package Manager must have answered");
            var queriesAfterFirst = RegisteredPackageRoots.PackageManagerQueriesForTests;

            RegisteredPackageRoots.Refresh();
            RegisteredPackageRoots.Refresh();
            Assert.That(
                RegisteredPackageRoots.PackageManagerQueriesForTests,
                Is.EqualTo(queriesAfterFirst),
                "a projectChanged without a registration event must not query Package Manager");

            RegisteredPackageRoots.MarkRegistrationChangedForTests();
            var afterEvent = RegisteredPackageRoots.Refresh();
            Assert.That(
                RegisteredPackageRoots.PackageManagerQueriesForTests,
                Is.EqualTo(queriesAfterFirst + 1),
                "a registration event re-arms exactly one query");
            Assert.That(afterEvent, Is.SameAs(first), "an unchanged list keeps the snapshot");

            RegisteredPackageRoots.Refresh();
            Assert.That(
                RegisteredPackageRoots.PackageManagerQueriesForTests,
                Is.EqualTo(queriesAfterFirst + 1),
                "an event that changed nothing must disarm the query again");
        }

        // CanWrite used to call PackageInfo.FindForAssetPath per asked path; the
        // snapshot now carries each package's writability, so the policy is a
        // prefix lookup. Pinned against a fabricated snapshot so the rig's real
        // package list cannot make the boundary cases vanish.
        [Test]
        public void CanWrite_AnswersFromTheSnapshotsWritabilityByPackagePrefix()
        {
            var original = RegisteredPackageRoots.Current;
            try
            {
                RegisteredPackageRoots.SetForTests(new RegisteredPackageRootSnapshot(
                    new[]
                    {
                        new RegisteredPackageRoot("Packages/com.example.local", "/tmp/local", "Local", isWritable: true),
                        new RegisteredPackageRoot("Packages/com.example.registry", "/tmp/reg", "Registry", isWritable: false),
                    },
                    isComplete: true));

                Assert.That(UnityProjectAssetGateway.CanWriteAssetPath("Packages/com.example.local"), Is.True);
                Assert.That(UnityProjectAssetGateway.CanWriteAssetPath("Packages/com.example.local/Runtime/A.cs"), Is.True);
                Assert.That(UnityProjectAssetGateway.CanWriteAssetPath("Packages/com.example.registry/A.cs"), Is.False);
                Assert.That(
                    UnityProjectAssetGateway.CanWriteAssetPath("Packages/com.example.localized/A.cs"),
                    Is.False,
                    "a prefix match must stop at the package boundary");
                Assert.That(
                    UnityProjectAssetGateway.CanWriteAssetPath("Packages/manifest.json"),
                    Is.False,
                    "a Packages path outside every registered package is not writable");
            }
            finally
            {
                RegisteredPackageRoots.SetForTests(original);
            }
        }

        [Test]
        public void Refresh_WithAnUnchangedPackageList_ReturnsTheVerySameSnapshot()
        {
            var first = RegisteredPackageRoots.Refresh();
            Assert.That(
                first.IsComplete,
                Is.True,
                "fixture guard: Package Manager must have answered, or this proves nothing");
            Assert.That(
                first.Roots.Count,
                Is.GreaterThan(0),
                "fixture guard: the rig has at least this package registered");

            var second = RegisteredPackageRoots.Refresh();

            Assert.That(
                second,
                Is.SameAs(first),
                "an unchanged package list must not rebuild or re-stat anything");
            Assert.That(RegisteredPackageRoots.Current, Is.SameAs(first));
        }

        // The reuse must be keyed on the package list, not on "a snapshot
        // exists": a snapshot the class did not build from a package list has no
        // signature to match, so the next Refresh has to do the real work.
        [Test]
        public void Refresh_AfterASnapshotItDidNotBuild_RebuildsInsteadOfReusingIt()
        {
            var built = RegisteredPackageRoots.Refresh();
            RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);

            var rebuilt = RegisteredPackageRoots.Refresh();

            Assert.That(
                rebuilt,
                Is.Not.SameAs(RegisteredPackageRootSnapshot.IncompleteEmpty),
                "the installed snapshot must not be handed back as if it were current");
            Assert.That(rebuilt.IsComplete, Is.True);
            Assert.That(
                rebuilt,
                Is.Not.SameAs(built),
                "a rebuild produces a new snapshot instance");
            Assert.That(rebuilt.Roots.Count, Is.EqualTo(built.Roots.Count));
        }

        [Test]
        public void PackageRoots_UnavailableRefreshPreservesCompleteness()
        {
            var original = RegisteredPackageRoots.Current;
            var originalQuery = RegisteredPackageRoots.PackageQueryOverrideForTests;
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                Assert.That(packages, Is.Not.Null);
                Assert.That(packages, Is.Not.Empty);
                foreach (var throws in new[] { false, true })
                {
                    RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                    var queries = 0;
                    RegisteredPackageRoots.PackageQueryOverrideForTests = () =>
                    {
                        queries++;
                        if (throws) throw new InvalidOperationException("unavailable");
                        return null;
                    };
                    var queriesBefore = RegisteredPackageRoots.PackageManagerQueriesForTests;
                    Assert.That(RegisteredPackageRoots.Refresh(), Is.SameAs(RegisteredPackageRootSnapshot.IncompleteEmpty));
                    Assert.That(RegisteredPackageRoots.Current.IsComplete, Is.False);
                    Assert.That(queries, Is.EqualTo(1));
                    Assert.That(RegisteredPackageRoots.PackageManagerQueriesForTests, Is.EqualTo(queriesBefore + 1));

                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                    RegisteredPackageRoots.MarkRegistrationChangedForTests();
                    var complete = RegisteredPackageRoots.Refresh();
                    Assert.That(complete.IsComplete, Is.True);
                    Assert.That(complete.Roots, Is.Not.Empty);

                    RegisteredPackageRoots.PackageQueryOverrideForTests = () =>
                    {
                        queries++;
                        if (throws) throw new InvalidOperationException("unavailable");
                        return null;
                    };
                    RegisteredPackageRoots.MarkRegistrationChangedForTests();
                    Assert.That(RegisteredPackageRoots.Refresh(), Is.SameAs(complete));
                    Assert.That(RegisteredPackageRoots.Current.IsComplete, Is.True);
                    Assert.That(queries, Is.EqualTo(2));

                    RegisteredPackageRoots.PackageQueryOverrideForTests = () => packages;
                    RegisteredPackageRoots.MarkRegistrationChangedForTests();
                    Assert.That(RegisteredPackageRoots.Refresh(), Is.SameAs(complete));
                    var queriesAfterRecovery = RegisteredPackageRoots.PackageManagerQueriesForTests;
                    Assert.That(RegisteredPackageRoots.Refresh(), Is.SameAs(complete));
                    Assert.That(RegisteredPackageRoots.PackageManagerQueriesForTests, Is.EqualTo(queriesAfterRecovery),
                        "successful recovery disarms the package query until a registration event");
                }
            }
            finally
            {
                RegisteredPackageRoots.PackageQueryOverrideForTests = originalQuery;
                RegisteredPackageRoots.SetForTests(original);
            }
        }

        [Test]
        public void PackageRoots_CompleteEmptyIsAuthoritative()
        {
            var original = RegisteredPackageRoots.Current;
            var originalQuery = RegisteredPackageRoots.PackageQueryOverrideForTests;
            try
            {
                RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                RegisteredPackageRoots.PackageQueryOverrideForTests = () => Array.Empty<UnityEditor.PackageManager.PackageInfo>();
                RegisteredPackageRoots.MarkRegistrationChangedForTests();

                var snapshot = RegisteredPackageRoots.Refresh();
                Assert.That(snapshot.IsComplete, Is.True);
                Assert.That(snapshot.Roots, Is.Empty);
                Assert.That(Zep.Core.Indexing.HybridAssetRecordSource.GetRegisteredPackageRoots(), Is.Empty,
                    "checked acquisition must accept authoritative empty mappings");
                RegisteredPackageRoots.SetForTests(RegisteredPackageRootSnapshot.IncompleteEmpty);
                RegisteredPackageRoots.PackageQueryOverrideForTests = () => null;
                Assert.That(Assert.Throws<InvalidOperationException>(() =>
                    Zep.Core.Indexing.HybridAssetRecordSource.GetRegisteredPackageRoots()).Message,
                    Is.EqualTo("Package metadata was unavailable; authoritative enumeration requires a complete package mapping."));
            }
            finally
            {
                RegisteredPackageRoots.PackageQueryOverrideForTests = originalQuery;
                RegisteredPackageRoots.SetForTests(original);
            }
        }
    }
}
