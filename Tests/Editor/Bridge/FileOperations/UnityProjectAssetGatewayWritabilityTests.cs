using System;
using Zep.Core.Domain;
using Zep.Editor.Bridge.FileOperations;
using NUnit.Framework;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using PackageSource = UnityEditor.PackageManager.PackageSource;

namespace Zep.Editor.Tests.Bridge.FileOperations
{
    // Pins the package write policy against Unity's own PackageSource values.
    // The policy used to exist twice in divergent styles — one copy spelling the
    // allowlist as the magic ints `source == 3 || source == 4` — so these tests
    // assert the MEANING (local/embedded packages are the user's own editable
    // files; registry, Git and built-in ones are a shared cache) rather than the
    // numbers, and cover both entry points so the copies cannot drift apart.
    public sealed class UnityProjectAssetGatewayWritabilityTests
    {
        // Nothing is ever created here. A path that does not exist keeps the test
        // safe if the guard under test regresses: the store's later existence
        // check refuses it with a DIFFERENT message, which is exactly what the
        // read-only assertion distinguishes.
        private const string ProbeFileName = "__ZepWritabilityProbe__.txt";

        private UnityProjectAssetGateway _gateway;

        [SetUp]
        public void SetUp()
        {
            _gateway = new UnityProjectAssetGateway();
        }

        [Test]
        public void CanWrite_ProjectAssets_AreWritable()
        {
            Assert.That(_gateway.CanWrite(AssetPath.From("Assets")), Is.True);
            Assert.That(_gateway.CanWrite(AssetPath.From("Assets/" + ProbeFileName)), Is.True);
        }

        [Test]
        public void CanWrite_LocalOrEmbeddedPackageContents_AreWritable()
        {
            var packageName = FindPackageName(
                source => source == PackageSource.Local || source == PackageSource.Embedded);
            if (packageName == null)
            {
                Assert.Ignore("This project registers no local or embedded package to probe.");
            }

            Assert.That(
                _gateway.CanWrite(AssetPath.From("Packages/" + packageName + "/" + ProbeFileName)),
                Is.True,
                "a local/embedded package holds the user's own editable files");
        }

        [Test]
        public void CanWrite_RegistryPackageContents_AreReadOnly()
        {
            var packageName = FindPackageName(source => source == PackageSource.Registry);
            if (packageName == null)
            {
                Assert.Ignore("This project registers no registry package to probe.");
            }

            Assert.That(
                _gateway.CanWrite(AssetPath.From("Packages/" + packageName + "/" + ProbeFileName)),
                Is.False,
                "registry package contents live in a shared cache and must stay read-only");
        }

        [Test]
        public void StoreUndoPayload_RegistryPackageContents_IsRefusedByTheSamePolicy()
        {
            var packageName = FindPackageName(source => source == PackageSource.Registry);
            if (packageName == null)
            {
                Assert.Ignore("This project registers no registry package to probe.");
            }

            var probePath = AssetPath.From("Packages/" + packageName + "/" + ProbeFileName);
            var result = _gateway.StoreUndoPayload(probePath);

            Assert.That(result.Succeeded, Is.False);
            // The undo storage boundary reaches this guard BEFORE it checks existence, so
            // this exact message proves the refusal came from the write policy
            // and not from the probe path being absent.
            Assert.That(
                result.ErrorMessage,
                Does.Contain("read-only packages"),
                "the undo storage boundary must refuse through the same policy CanWrite applies");
        }

        private static string FindPackageName(Func<PackageSource, bool> matchesSource)
        {
            var packages = PackageInfo.GetAllRegisteredPackages();
            for (var i = 0; i < packages.Length; i++)
            {
                if (matchesSource(packages[i].source))
                {
                    return packages[i].name;
                }
            }

            return null;
        }
    }
}
