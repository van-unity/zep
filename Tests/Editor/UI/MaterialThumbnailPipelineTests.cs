using Zep.Core.Application;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Zep.Core.Indexing;
using Zep.Editor.Bridge.Indexing;
using Zep.Editor.UI.AssetsArea.MaterialThumbnails;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zep.Tests.Editor.UI
{
    // Behavior of the material-thumbnail pipeline's owned pieces: the bounded
    // LRU (eviction order, eviction callback contract, dependency-based
    // invalidation), the disk-store format (roundtrip, corruption and cap
    // rejection, atomicity by construction), the CPU backdrop composite, and
    // the provider's bind-path scope guarantees. Scene rendering and the
    // EditorApplication.update pump need a live editor loop and are verified
    // in-editor, not here.
    public sealed class MaterialThumbnailPipelineTests
    {
        // ---------------------------------------------------------------
        // LRU
        // ---------------------------------------------------------------

        private static Texture2D MakeTexture()
        {
            return new Texture2D(1, 1, TextureFormat.RGBA32, mipChain: false)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
        }

        private static void DestroyAll(List<Texture2D> textures)
        {
            foreach (var texture in textures)
            {
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        [Test]
        public void Lru_EvictsLeastRecentlyUsed_AndHandsTextureToCallback()
        {
            var evicted = new List<Texture2D>();
            var lru = new MaterialThumbnailLru(2, evicted.Add);
            var a = MakeTexture();
            var b = MakeTexture();
            var c = MakeTexture();
            try
            {
                lru.Add("Assets/A.mat", a, Array.Empty<string>());
                lru.Add("Assets/B.mat", b, Array.Empty<string>());

                // Touch A so B becomes the LRU entry.
                Assert.That(lru.TryGet("Assets/A.mat"), Is.SameAs(a));

                lru.Add("Assets/C.mat", c, Array.Empty<string>());

                Assert.That(evicted, Is.EqualTo(new[] { b }));
                Assert.That(lru.TryGet("Assets/B.mat"), Is.Null);
                Assert.That(lru.TryGet("Assets/A.mat"), Is.SameAs(a));
                Assert.That(lru.TryGet("Assets/C.mat"), Is.SameAs(c));
                Assert.That(lru.Count, Is.EqualTo(2));
            }
            finally
            {
                lru.Clear();
                DestroyAll(evicted);
                DestroyAll(new List<Texture2D> { a, b, c });
            }
        }

        [Test]
        public void Lru_AddSamePathTwice_EvictsTheOldTexture()
        {
            var evicted = new List<Texture2D>();
            var lru = new MaterialThumbnailLru(4, evicted.Add);
            var first = MakeTexture();
            var second = MakeTexture();
            try
            {
                lru.Add("Assets/A.mat", first, Array.Empty<string>());
                lru.Add("Assets/A.mat", second, Array.Empty<string>());

                Assert.That(evicted, Is.EqualTo(new[] { first }));
                Assert.That(lru.TryGet("Assets/A.mat"), Is.SameAs(second));
                Assert.That(lru.Count, Is.EqualTo(1));
            }
            finally
            {
                lru.Clear();
                DestroyAll(evicted);
                DestroyAll(new List<Texture2D> { first, second });
            }
        }

        [Test]
        public void Lru_TryGetDestroyedTexture_ReportsMissAndDropsEntry()
        {
            var evicted = new List<Texture2D>();
            var lru = new MaterialThumbnailLru(4, evicted.Add);
            var texture = MakeTexture();
            lru.Add("Assets/A.mat", texture, Array.Empty<string>());

            // Simulates Unity-side destruction (fake-null): the entry must
            // read as a miss so the provider re-renders instead of serving a
            // destroyed texture.
            UnityEngine.Object.DestroyImmediate(texture);

            Assert.That(lru.TryGet("Assets/A.mat"), Is.Null);
            Assert.That(lru.Count, Is.EqualTo(0));
            lru.Clear();
            DestroyAll(evicted);
        }

        private static HashSet<string> Changed(params string[] paths)
        {
            return new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        }

        [Test]
        public void Lru_RemoveWhereDependsOnAny_MatchesMaterialAndDependencyPaths()
        {
            var evicted = new List<Texture2D>();
            var lru = new MaterialThumbnailLru(8, evicted.Add);
            var withDep = MakeTexture();
            var withoutDep = MakeTexture();
            try
            {
                lru.Add("Assets/WithDep.mat", withDep, new[] { "Assets/Textures/albedo.png" });
                lru.Add("Assets/WithoutDep.mat", withoutDep, new[] { "Assets/Textures/other.png" });

                // Dependency hit (case-insensitive, matching Unity path
                // semantics) — one pass over the whole change batch.
                Assert.That(
                    lru.RemoveWhereDependsOnAny(Changed("Assets/Textures/Albedo.PNG", "Assets/Unrelated.cs")),
                    Is.EqualTo(1));
                Assert.That(lru.TryGet("Assets/WithDep.mat"), Is.Null);
                Assert.That(lru.TryGet("Assets/WithoutDep.mat"), Is.SameAs(withoutDep));

                // Direct material-path hit.
                Assert.That(lru.RemoveWhereDependsOnAny(Changed("Assets/WithoutDep.mat")), Is.EqualTo(1));
                Assert.That(lru.Count, Is.EqualTo(0));
                Assert.That(evicted, Is.EquivalentTo(new[] { withDep, withoutDep }));

                // No-op on unknown paths and on an empty batch.
                Assert.That(lru.RemoveWhereDependsOnAny(Changed("Assets/Unknown.png")), Is.EqualTo(0));
                Assert.That(lru.RemoveWhereDependsOnAny(Changed()), Is.EqualTo(0));
            }
            finally
            {
                lru.Clear();
                DestroyAll(evicted);
                DestroyAll(new List<Texture2D> { withDep, withoutDep });
            }
        }

        // ---------------------------------------------------------------
        // Disk store
        // ---------------------------------------------------------------

        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "bct-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static MaterialThumbnailDiskStore.Record MakeRecord(int width = 4, int height = 4)
        {
            var pixels = new byte[width * height * 4];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)(i % 251);
            }

            return new MaterialThumbnailDiskStore.Record(
                "hash-1|builtin",
                new[] { "Assets/Textures/albedo.png", "Assets/Shaders/lit.shader" },
                new[] { "dephash-a", "dephash-b" },
                width,
                height,
                pixels);
        }

        [Test]
        public void DiskStore_Roundtrip_PreservesEveryField()
        {
            var filePath = Path.Combine(_tempDir, "abc123.bct");
            var record = MakeRecord();

            MaterialThumbnailDiskStore.Write(filePath, record);

            Assert.That(MaterialThumbnailDiskStore.TryRead(filePath, out var loaded), Is.True);
            Assert.That(loaded.MaterialHash, Is.EqualTo(record.MaterialHash));
            Assert.That(loaded.DependencyPaths, Is.EqualTo(record.DependencyPaths));
            Assert.That(loaded.DependencyHashes, Is.EqualTo(record.DependencyHashes));
            Assert.That(loaded.Width, Is.EqualTo(record.Width));
            Assert.That(loaded.Height, Is.EqualTo(record.Height));
            Assert.That(loaded.Pixels, Is.EqualTo(record.Pixels));

            // The atomic temp file must not survive a successful write.
            Assert.That(Directory.GetFiles(_tempDir, "*.tmp"), Is.Empty);
        }

        [Test]
        public void DiskStore_MissingFile_ReadsFalse()
        {
            Assert.That(
                MaterialThumbnailDiskStore.TryRead(Path.Combine(_tempDir, "nope.bct"), out _),
                Is.False);
        }

        [Test]
        public void DiskStore_WrongMagicOrTruncated_ReadsFalse()
        {
            var wrongMagic = Path.Combine(_tempDir, "wrong.bct");
            File.WriteAllBytes(wrongMagic, new byte[] { (byte)'N', (byte)'O', (byte)'P', (byte)'E', 1 });
            Assert.That(MaterialThumbnailDiskStore.TryRead(wrongMagic, out _), Is.False);

            var valid = Path.Combine(_tempDir, "valid.bct");
            MaterialThumbnailDiskStore.Write(valid, MakeRecord());
            var bytes = File.ReadAllBytes(valid);
            var truncated = Path.Combine(_tempDir, "truncated.bct");
            File.WriteAllBytes(truncated, new ArraySegment<byte>(bytes, 0, bytes.Length / 2).ToArray());
            Assert.That(MaterialThumbnailDiskStore.TryRead(truncated, out _), Is.False);
        }

        [Test]
        public void DiskStore_RejectsInsaneDimensionsAndPixelLengthMismatch()
        {
            // A record whose declared pixel length disagrees with its
            // dimensions must be rejected on read even though Write would
            // happily serialize it (Write trusts its caller; Read trusts
            // nothing).
            var filePath = Path.Combine(_tempDir, "bad-dims.bct");
            var bad = new MaterialThumbnailDiskStore.Record(
                "h", Array.Empty<string>(), Array.Empty<string>(), 4, 4, new byte[7]);
            MaterialThumbnailDiskStore.Write(filePath, bad);
            Assert.That(MaterialThumbnailDiskStore.TryRead(filePath, out _), Is.False);
        }

        [Test]
        public void DiskStore_DeleteIsIdempotent()
        {
            var filePath = Path.Combine(_tempDir, "gone.bct");
            MaterialThumbnailDiskStore.Write(filePath, MakeRecord());
            MaterialThumbnailDiskStore.Delete(filePath);
            Assert.That(File.Exists(filePath), Is.False);
            MaterialThumbnailDiskStore.Delete(filePath);
        }

        [Test]
        public void DiskStore_ReadIntoProvidedBuffer_ReusesItWithoutAllocating()
        {
            var filePath = Path.Combine(_tempDir, "buffered.bct");
            var record = MakeRecord();
            MaterialThumbnailDiskStore.Write(filePath, record);

            var buffer = new byte[record.Pixels.Length];
            Assert.That(MaterialThumbnailDiskStore.TryRead(filePath, buffer, out var loaded), Is.True);
            Assert.That(loaded.Pixels, Is.SameAs(buffer), "matching buffer must be used in place");
            Assert.That(buffer, Is.EqualTo(record.Pixels));

            // A wrong-size buffer falls back to a fresh allocation rather
            // than failing the read.
            var wrongSize = new byte[3];
            Assert.That(MaterialThumbnailDiskStore.TryRead(filePath, wrongSize, out var reloaded), Is.True);
            Assert.That(reloaded.Pixels, Is.Not.SameAs(wrongSize));
            Assert.That(reloaded.Pixels, Is.EqualTo(record.Pixels));
        }

        [Test]
        public void DiskStore_RefusesRecordsThatCannotRoundtrip()
        {
            // Over the dependency cap: Write must refuse (a written-but-
            // unreadable record would re-render AND rewrite every session).
            var depCount = MaterialThumbnailDiskStore.MaxDependencies + 1;
            var depPaths = new string[depCount];
            var depHashes = new string[depCount];
            for (var i = 0; i < depCount; i++)
            {
                depPaths[i] = "Assets/t" + i + ".png";
                depHashes[i] = "h" + i;
            }

            var filePath = Path.Combine(_tempDir, "toomanydeps.bct");
            var record = new MaterialThumbnailDiskStore.Record(
                "h", depPaths, depHashes, 2, 2, new byte[2 * 2 * 4]);
            MaterialThumbnailDiskStore.Write(filePath, record);
            Assert.That(File.Exists(filePath), Is.False);
            Assert.That(Directory.GetFiles(_tempDir, "*.tmp"), Is.Empty);
        }

        [Test]
        public void DiskStore_GarbageBytes_ReadFalseNotThrow()
        {
            // Correct magic/version followed by garbage exercises the corrupt
            // string/length decode paths — the contract is false, never throw.
            var filePath = Path.Combine(_tempDir, "garbage.bct");
            var bytes = new byte[512];
            bytes[0] = (byte)'B'; bytes[1] = (byte)'C'; bytes[2] = (byte)'M'; bytes[3] = (byte)'T';
            bytes[4] = 1;
            for (var i = 5; i < bytes.Length; i++)
            {
                bytes[i] = 0xFF;
            }
            File.WriteAllBytes(filePath, bytes);

            Assert.That(MaterialThumbnailDiskStore.TryRead(filePath, out _), Is.False);
        }

        [Test]
        public void DiskStore_Prune_SweepsOldTempFiles_AndBoundsTotalSize()
        {
            // Three thumbnails, oldest first; cap chosen so only the newest
            // two fit. Plus one stale temp file and one fresh temp file.
            var oldest = Path.Combine(_tempDir, "a.bct");
            var middle = Path.Combine(_tempDir, "b.bct");
            var newest = Path.Combine(_tempDir, "c.bct");
            foreach (var (path, age) in new[] { (oldest, 30), (middle, 20), (newest, 10) })
            {
                File.WriteAllBytes(path, new byte[100]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-age));
            }

            var staleTemp = Path.Combine(_tempDir, "x.bct.deadbeef.tmp");
            File.WriteAllBytes(staleTemp, new byte[10]);
            File.SetLastWriteTimeUtc(staleTemp, DateTime.UtcNow.AddDays(-1));
            var freshTemp = Path.Combine(_tempDir, "y.bct.cafebabe.tmp");
            File.WriteAllBytes(freshTemp, new byte[10]);

            MaterialThumbnailDiskStore.Prune(_tempDir, maxTotalBytes: 250, TimeSpan.FromHours(1));

            Assert.That(File.Exists(oldest), Is.False, "oldest entry evicted to meet the size cap");
            Assert.That(File.Exists(middle), Is.True);
            Assert.That(File.Exists(newest), Is.True);
            Assert.That(File.Exists(staleTemp), Is.False, "crashed-write temp swept");
            Assert.That(File.Exists(freshTemp), Is.True, "in-progress temp untouched");
        }

        // ---------------------------------------------------------------
        // Backdrop composite
        // ---------------------------------------------------------------

        [Test]
        public void Composite_OpaquePixelsUntouched_TransparentPixelsBecomeBackdrop()
        {
            const int size = 8;
            var rgba = new byte[size * size * 4];
            // Pixel 0: fully opaque red. Pixel 1: fully transparent.
            rgba[0] = 200; rgba[3] = 255;

            MaterialThumbnailPixels.CompositeOverBackground(rgba, size, size);

            Assert.That(rgba[0], Is.EqualTo(200));
            Assert.That(rgba[3], Is.EqualTo(255));

            // Transparent pixel took the backdrop color and became opaque.
            Assert.That(rgba[7], Is.EqualTo(255));
            Assert.That(rgba[4] + rgba[5] + rgba[6], Is.GreaterThan(0));
        }

        [Test]
        public void Composite_PartialAlpha_BlendsPremultipliedOver()
        {
            const int size = 2;
            var rgba = new byte[size * size * 4];
            // Premultiplied half-covered white edge pixel: rgb already scaled
            // by alpha (127), backdrop contributes the other half.
            rgba[0] = 127; rgba[1] = 127; rgba[2] = 127; rgba[3] = 127;

            var reference = new byte[size * size * 4];
            MaterialThumbnailPixels.CompositeOverBackground(reference, size, size);

            MaterialThumbnailPixels.CompositeOverBackground(rgba, size, size);

            // out = src + bg * (1 - a): strictly brighter than the src
            // contribution alone, strictly brighter than the bg contribution
            // alone would have left it, and fully opaque.
            Assert.That(rgba[3], Is.EqualTo(255));
            Assert.That(rgba[0], Is.GreaterThan(127));
            Assert.That(rgba[0], Is.GreaterThan(reference[0] / 2));
        }

        [Test]
        public void RoundedCornerMask_FeathersCornersLeavesCenterAndSmallSizesAlone()
        {
            const int size = 64;
            var rgba = new byte[size * size * 4];
            for (var i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }

            MaterialThumbnailPixels.ApplyRoundedCornerMask(rgba, size, size);

            // Exact corner: outside the rounded rect, fully feathered out.
            Assert.That(rgba[3], Is.LessThan(32), "corner pixel must be masked");
            // Center: untouched.
            var center = ((size / 2) * size + size / 2) * 4 + 3;
            Assert.That(rgba[center], Is.EqualTo(255));
            // Edge midpoint (straight side): untouched.
            var edgeMid = ((size / 2) * size) * 4 + 3;
            Assert.That(rgba[edgeMid], Is.EqualTo(255));

            // Sub-64px buffers (tests, hypothetical tiny renders) skip the
            // mask entirely rather than degenerate.
            var tiny = new byte[8 * 8 * 4];
            for (var i = 3; i < tiny.Length; i += 4)
            {
                tiny[i] = 255;
            }
            MaterialThumbnailPixels.ApplyRoundedCornerMask(tiny, 8, 8);
            Assert.That(tiny[3], Is.EqualTo(255));
        }

        [Test]
        public void ToneCurve_PinsEndpointsLiftsMidtonesKeepsAlpha()
        {
            var rgba = new byte[] { 0, 100, 255, 77 };
            MaterialThumbnailPixels.ApplyToneCurve(rgba);
            Assert.That(rgba[0], Is.EqualTo(0));
            Assert.That(rgba[2], Is.EqualTo(255));
            Assert.That(rgba[1], Is.GreaterThan(100), "midtones lift");
            Assert.That(rgba[3], Is.EqualTo(77), "alpha is coverage, never graded");
        }

        // Core cannot call Unity, so it carries its own sRGB transfer function.
        // It has to agree with the engine's for every input a pixel can hold, or
        // thumbnails rendered before and after would differ.
        [Test]
        public void EncodeLinearToSrgb_MatchesUnitysTransferFunctionForEveryByte()
        {
            var rgba = new byte[256 * 4];
            for (var i = 0; i < 256; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = rgba[i * 4 + 3] = (byte)i;
            }

            MaterialThumbnailPixels.EncodeLinearToSrgb(rgba);

            for (var i = 0; i < 256; i++)
            {
                var expected = (byte)Mathf.Clamp(
                    Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 255f) * 255f), 0, 255);
                Assert.That(rgba[i * 4], Is.EqualTo(expected), "red at " + i);
                Assert.That(rgba[i * 4 + 1], Is.EqualTo(expected), "green at " + i);
                Assert.That(rgba[i * 4 + 2], Is.EqualTo(expected), "blue at " + i);
                Assert.That(rgba[i * 4 + 3], Is.EqualTo((byte)i), "alpha is coverage, never encoded");
            }
        }

        [Test]
        public void EncodeToSrgbIfLinear_MatchesActiveColorSpaceContract()
        {
            var rgba = new byte[] { 0, 55, 255, 128 };
            var original = (byte[])rgba.Clone();

            MaterialThumbnailProvider.EncodeToSrgbIfLinear(rgba);

            if (QualitySettings.activeColorSpace == ColorSpace.Linear)
            {
                // sRGB encode: endpoints fixed, midtones brighten, alpha
                // (coverage) untouched.
                Assert.That(rgba[0], Is.EqualTo(0));
                Assert.That(rgba[2], Is.EqualTo(255));
                Assert.That(rgba[1], Is.GreaterThan(original[1]));
                Assert.That(rgba[3], Is.EqualTo(original[3]));
            }
            else
            {
                // Gamma projects: pixels are already display-ready.
                Assert.That(rgba, Is.EqualTo(original));
            }
        }

        // ---------------------------------------------------------------
        // Provider bind-path scope
        // ---------------------------------------------------------------

        [Test]
        public void TryGet_NonMaterialTypeName_ReturnsNullAndCedesToStockPipeline()
        {
            // The scope boundary: every non-material bind must fall through
            // to the stock pipeline via a single string compare, with
            // pendingCustomThumb=false so the stock preview upgrade stays on.
            Assert.That(MaterialThumbnailProvider.TryGet("Assets/A.png", "Texture2D", out var pending), Is.Null);
            Assert.That(pending, Is.False);
            Assert.That(MaterialThumbnailProvider.TryGet("Assets/A.cs", "MonoScript", out pending), Is.Null);
            Assert.That(pending, Is.False);
            Assert.That(MaterialThumbnailProvider.TryGet("Assets/Folder", "Folder", out pending), Is.Null);
            Assert.That(pending, Is.False);
            Assert.That(MaterialThumbnailProvider.TryGet(null, null, out pending), Is.Null);
            Assert.That(pending, Is.False);
        }

        [Test]
        public void TryGet_MaterialTypeNameWithNonMatPath_CedesToStockPipeline()
        {
            // FBX sub-assets and ghost rows can surface TypeName "Material"
            // without a .mat source file — they stay on the stock pipeline.
            Assert.That(MaterialThumbnailProvider.TryGet("Assets/Model.fbx", "Material", out var pending), Is.Null);
            Assert.That(pending, Is.False);
            Assert.That(MaterialThumbnailProvider.TryGet(null, "Material", out pending), Is.Null);
            Assert.That(pending, Is.False);
            Assert.That(MaterialThumbnailProvider.TryGet(string.Empty, "Material", out pending), Is.Null);
            Assert.That(pending, Is.False);
        }

        [Test]
        public void TryGet_MaterialPath_ReturnsNullImmediately_AndShutdownResets()
        {
            // First sight of a real material path can only enqueue — never
            // render, load, or block — so the bind returns null with
            // pendingCustomThumb=true (mini icon shows, stock upgrade
            // suppressed). Shutdown() must leave no pump or owned state
            // behind (it is the assembly-reload/quit handler).
            try
            {
                Assert.That(
                    MaterialThumbnailProvider.TryGet("Assets/DoesNotExist-ForTests.mat", "Material", out var pending),
                    Is.Null);
                Assert.That(pending, Is.True, "eligible material must claim the path while queued");
            }
            finally
            {
                MaterialThumbnailProvider.Shutdown();
            }
        }

        // ---------------------------------------------------------------
        // Pump backlog policy
        // ---------------------------------------------------------------
        //
        // Mirrors the production constants; reading them from the provider
        // would make the assertions unfalsifiable.
        private const int MaxItemsPerTick = 16;
        private const int MaxPending = 512;
        private const int ThumbnailsReadyEveryTicks = 8;

        private static string MaterialPathAt(int index)
        {
            return "Assets/MatPump/Item" + index + ".mat";
        }

        private static FieldInfo ProviderField(string name) =>
            typeof(MaterialThumbnailProvider).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static (object supported, object tag) CapturePipelinePolicy() =>
            (ProviderField("_pipelineSupported").GetValue(null), ProviderField("_pipelineTag").GetValue(null));

        private static void SimulatePipelineChange(bool supported)
        {
            typeof(MaterialThumbnailProvider).GetMethod("OnPipelineChanged", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
            // This replaces the GraphicsSettings query, not the invalidation
            // being tested, and does not alter the project's render settings.
            ProviderField("_pipelineSupported").SetValue(null, supported);
        }

        private static void RestorePipelinePolicy((object supported, object tag) previous)
        {
            MaterialThumbnailProvider.ProcessOverrideForTests = null;
            MaterialThumbnailProvider.Shutdown();
            ProviderField("_pipelineSupported").SetValue(null, previous.supported);
            ProviderField("_pipelineTag").SetValue(null, previous.tag);
        }

        [Test]
        public void PipelineChange_QueuedWorkBecomesStockFallback_AndSignalsWithoutAnLruEntry()
        {
            var previous = CapturePipelinePolicy();
            var processed = 0;
            var signals = 0;
            Action onReady = () => signals++;
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            MaterialThumbnailProvider.ProcessOverrideForTests = _ => { processed++; return true; };
            try
            {
                ProviderField("_pipelineSupported").SetValue(null, true);
                MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out var pending);
                Assert.That(pending, Is.True);

                SimulatePipelineChange(supported: false);
                Assert.That(signals, Is.Zero, "pipeline notifications use the existing coalesced pump");
                MaterialThumbnailProvider.TickForTests();

                Assert.That(processed, Is.Zero, "queued work must not render in the unsupported pipeline");
                Assert.That(signals, Is.EqualTo(1));
                Assert.That(MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out pending), Is.Null);
                Assert.That(pending, Is.False, "the visible row must now allow stock previews");
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                RestorePipelinePolicy(previous);
            }
        }

        [Test]
        public void PipelineChange_FailedOnlyState_RebindsAndCanRequestFreshSupportedWork()
        {
            var previous = CapturePipelinePolicy();
            var signals = 0;
            Action onReady = () => signals++;
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            try
            {
                ((Dictionary<string, string[]>)ProviderField("Failed").GetValue(null))[MaterialPathAt(0)] =
                    Array.Empty<string>();
                SimulatePipelineChange(supported: true);
                MaterialThumbnailProvider.TickForTests();

                Assert.That(signals, Is.EqualTo(1));
                MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out var pending);
                Assert.That(pending, Is.True, "the old pipeline's terminal verdict must be gone");
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                RestorePipelinePolicy(previous);
            }
        }

        [Test]
        public void PipelineChange_SupportedConsumerRebind_QueuesFreshWorkUnderCurrentPolicy()
        {
            var previous = CapturePipelinePolicy();
            var requested = false;
            var processed = 0;
            Action onReady = () =>
            {
                if (requested) return;
                requested = true;
                MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out _);
            };
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            MaterialThumbnailProvider.ProcessOverrideForTests = _ => { processed++; return true; };
            try
            {
                ProviderField("_pipelineSupported").SetValue(null, true);
                MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out _);
                SimulatePipelineChange(supported: true);
                for (var tick = 0; tick < 50 && processed == 0; tick++)
                    MaterialThumbnailProvider.TickForTests();

                Assert.That(requested, Is.True, "the transition must cause the visible consumer to rebind");
                Assert.That(processed, Is.EqualTo(1));
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                RestorePipelinePolicy(previous);
            }
        }

        [Test]
        public void PipelineChange_UnsupportedRequeueCannotRestoreACustomClaim()
        {
            var previous = CapturePipelinePolicy();
            try
            {
                SimulatePipelineChange(supported: false);
                typeof(MaterialThumbnailProvider).GetMethod("Requeue", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { MaterialPathAt(0) });

                Assert.That((IList)ProviderField("Pending").GetValue(null), Is.Empty);
            }
            finally
            {
                RestorePipelinePolicy(previous);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PipelineChange_AirbornePixelsAreDiscardedAfterTheRealReadbackDrains(bool supported)
        {
            Assume.That(SystemInfo.supportsAsyncGPUReadback, Is.True,
                "The platform must support real asynchronous GPU readback for this boundary check.");
            var previous = CapturePipelinePolicy();
            var output = new RenderTexture(256, 256, 0, RenderTextureFormat.ARGB32);
            var requests = (IList)ProviderField("InFlight").GetValue(null);
            var inFlightPaths = (HashSet<string>)ProviderField("InFlightPaths").GetValue(null);
            var path = MaterialPathAt(0);
            var processed = 0;
            var signals = 0;
            Action onReady = () => signals++;
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            MaterialThumbnailProvider.ProcessOverrideForTests = _ => { processed++; return true; };
            try
            {
                output.Create();
                var request = AsyncGPUReadback.Request(output, 0, TextureFormat.RGBA32);
                // The real request replaces only the rig's render step. Its
                // lifetime is then owned/drained by the unchanged provider pump.
                var entryType = typeof(MaterialThumbnailProvider).GetNestedType(
                    "InFlightReadback", BindingFlags.NonPublic);
                var entry = Activator.CreateInstance(entryType);
                entryType.GetField("Path").SetValue(entry, path);
                entryType.GetField("Request").SetValue(entry, request);
                entryType.GetField("DepPaths").SetValue(entry, Array.Empty<string>());
                requests.Add(entry);
                inFlightPaths.Add(path);

                SimulatePipelineChange(supported);
                Assert.That(requests.Count, Is.EqualTo(1), "invalidation cannot orphan an unfinished request");
                Assert.That(entryType.GetField("Stale").GetValue(requests[0]), Is.EqualTo(true));
                for (var tick = 0; tick < 50 && (requests.Count > 0 || supported && processed == 0); tick++)
                    MaterialThumbnailProvider.TickForTests();

                Assert.That(requests, Is.Empty);
                Assert.That(inFlightPaths, Is.Empty);
                Assert.That(request.done, Is.True);
                Assert.That(request.hasError, Is.False);
                Assert.That(MaterialThumbnailProvider.TryGet(path, "Material", out var pending), Is.Null,
                    "pre-change pixels cannot repopulate the LRU after invalidation");
                Assert.That(pending, Is.EqualTo(supported));
                Assert.That(processed, Is.EqualTo(supported ? 1 : 0));
                Assert.That(signals, Is.GreaterThan(0), "in-flight-only transitions still rebind visible consumers");
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                RestorePipelinePolicy(previous);
                output.Release();
                UnityEngine.Object.DestroyImmediate(output);
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedMaterial_RelevantUnityImportRevivesOnlyItsVerdict_AndSignalsWithoutAnLruEntry(
            bool dependencyOnly)
        {
            var previous = CapturePipelinePolicy();
            var root = CreateFailedMaterialFixture(out var materials, out var shaders);
            var imports = new List<string>();
            var processed = new List<string>();
            var signals = 0;
            Action<ProjectAssetChanges> onChanges = changes => imports.AddRange(changes.Imported);
            Action onReady = () => signals++;
            try
            {
                ProviderField("_pipelineSupported").SetValue(null, true);
                // Hook the real postprocessor, then represent the drained failed
                // jobs without dispatching a render in this dependency test.
                MaterialThumbnailProvider.TryGet(materials[0], "Material", out _);
                MaterialThumbnailProvider.Shutdown();
                var markFailed = typeof(MaterialThumbnailProvider).GetMethod(
                    "MarkFailed", BindingFlags.Static | BindingFlags.NonPublic);
                foreach (var material in materials) markFailed.Invoke(null, new object[] { material });
                foreach (var material in materials)
                {
                    MaterialThumbnailProvider.TryGet(material, "Material", out var pending);
                    Assert.That(pending, Is.False, "both verdicts must be terminal before the import");
                }
                Assert.That(AssetDatabase.GetDependencies(materials[0], recursive: true), Does.Contain(shaders[0]));

                ZepAssetPostprocessor.Changed += onChanges;
                MaterialThumbnailProvider.ThumbnailsReady += onReady;
                var changedPath = dependencyOnly ? shaders[0] : materials[0];
                AssetDatabase.ImportAsset(changedPath,
                    ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

                Assert.That(imports, Does.Contain(changedPath), "the real Unity callback must reach the provider");
                if (dependencyOnly)
                {
                    Assert.That(imports, Does.Not.Contain(materials[0]),
                        "the dependency case cannot pass by relying on an implicit .mat import");
                }
                Assert.That(signals, Is.EqualTo(1), "a revived failure needs a visible rebind without an LRU entry");
                MaterialThumbnailProvider.TryGet(materials[0], "Material", out var revived);
                MaterialThumbnailProvider.TryGet(materials[1], "Material", out var unrelated);
                Assert.That(revived, Is.True);
                Assert.That(unrelated, Is.False, "an unrelated shader's failure must remain terminal");

                MaterialThumbnailProvider.ProcessOverrideForTests = path => { processed.Add(path); return true; };
                for (var tick = 0; tick < 50 && processed.Count == 0; tick++)
                    MaterialThumbnailProvider.TickForTests();
                Assert.That(processed, Is.EqualTo(new[] { materials[0] }));
            }
            finally
            {
                ZepAssetPostprocessor.Changed -= onChanges;
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                RestorePipelinePolicy(previous);
                AssetDatabase.DeleteAsset(root);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedDependencyAssociations_AreClearedWithPipelineOrShutdown(bool shutdown)
        {
            var previous = CapturePipelinePolicy();
            try
            {
                var failed = (Dictionary<string, string[]>)ProviderField("Failed").GetValue(null);
                failed[MaterialPathAt(0)] = new[] { "Assets/Old.shader" };
                failed[MaterialPathAt(1)] = new[] { "Assets/Other.shader" };

                if (shutdown) MaterialThumbnailProvider.Shutdown();
                else SimulatePipelineChange(supported: true);

                Assert.That(failed, Is.Empty, "dependency identity must leave with the owning failed verdict");
            }
            finally
            {
                RestorePipelinePolicy(previous);
            }
        }

        private static string CreateFailedMaterialFixture(out string[] materials, out string[] shaders)
        {
            var name = "BCTFailedDependencyTests_" + Guid.NewGuid().ToString("N");
            var root = "Assets/" + name;
            materials = new string[2];
            shaders = new string[2];
            AssetDatabase.CreateFolder("Assets", name);
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    shaders[i] = root + "/Shader" + i + ".shader";
                    File.WriteAllText(Path.GetFullPath(shaders[i]),
                        "Shader \"Hidden/" + name + "/" + i + "\" { SubShader { Pass { } } }");
                    AssetDatabase.ImportAsset(shaders[i], ImportAssetOptions.ForceSynchronousImport);
                    var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaders[i]);
                    Assert.That(shader, Is.Not.Null);
                    materials[i] = root + "/Material" + i + ".mat";
                    AssetDatabase.CreateAsset(new Material(shader), materials[i]);
                }
                AssetDatabase.SaveAssets();
                return root;
            }
            catch
            {
                AssetDatabase.DeleteAsset(root);
                throw;
            }
        }

        [Test]
        public void Pump_AfterAFastScroll_ServesTheNewestRequestFirst()
        {
            const int requested = 1500;
            var order = new List<string>();
            MaterialThumbnailProvider.ProcessOverrideForTests = path =>
            {
                order.Add(path);
                return true;
            };
            try
            {
                for (var i = 0; i < requested; i++)
                {
                    MaterialThumbnailProvider.TryGet(MaterialPathAt(i), "Material", out var pending);
                    Assert.That(pending, Is.True, "an eligible material must claim the path while queued");
                }

                // The drain loop is also bounded by a 2 ms stopwatch that starts
                // at the top of the tick, and the very first tick in a fresh
                // domain can spend that budget on JIT alone. Tick until one
                // actually drains, then assert on THAT batch — the order claim
                // is about the first items served, whichever tick serves them.
                for (var tick = 0; tick < 50 && order.Count == 0; tick++)
                {
                    MaterialThumbnailProvider.TickForTests();
                }

                Assert.That(
                    order.Count,
                    Is.InRange(1, MaxItemsPerTick),
                    "one tick must respect the per-tick item budget");
                Assert.That(
                    order[0],
                    Is.EqualTo(MaterialPathAt(requested - 1)),
                    "the newest request is the tile on screen, so it must be rendered first");
            }
            finally
            {
                MaterialThumbnailProvider.ProcessOverrideForTests = null;
                MaterialThumbnailProvider.Shutdown();
            }
        }

        [Test]
        public void Pump_ALongBacklog_CoalescesTheReadySignal()
        {
            const int ticks = 100;
            var processed = 0;
            var readySignals = 0;
            Action onReady = () => readySignals++;
            MaterialThumbnailProvider.ProcessOverrideForTests = _ =>
            {
                processed++;
                return true;
            };
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            try
            {
                for (var i = 0; i < 1500; i++)
                {
                    MaterialThumbnailProvider.TryGet(MaterialPathAt(i), "Material", out _);
                }

                for (var i = 0; i < ticks; i++)
                {
                    MaterialThumbnailProvider.TickForTests();
                }

                // One per tick is what the uncoalesced pump did, and each one
                // is a full rebind of every realized tile.
                Assert.That(
                    readySignals,
                    Is.LessThanOrEqualTo((ticks / ThumbnailsReadyEveryTicks) + 1),
                    "a still-draining backlog must batch its rebind signal");
                Assert.That(
                    readySignals,
                    Is.GreaterThan(0),
                    "landed thumbnails must still reach the view");
                Assert.That(
                    processed,
                    Is.EqualTo(MaxPending),
                    "the backlog must be bounded — everything kept drains within these ticks");
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                MaterialThumbnailProvider.ProcessOverrideForTests = null;
                MaterialThumbnailProvider.Shutdown();
            }
        }

        [Test]
        public void Pump_ADeferredDestroyQueuedWithTheReadySignal_SurvivesOnePumpPastTheRaise()
        {
            // The shell answers ThumbnailsReady by deferring its rebind through
            // EditorApplication.delayCall, so the pump immediately after the
            // raise can still meet tiles holding the evicted texture. Killing
            // it on that pump is what blanks a tile for a frame; killing it one
            // pump later is the margin. It must not become an indefinite hold.
            var texture = MakeTexture();
            var readySignals = 0;
            var queued = false;
            Action onReady = () => readySignals++;
            MaterialThumbnailProvider.ProcessOverrideForTests = _ =>
            {
                if (!queued)
                {
                    queued = true;
                    MaterialThumbnailProvider.QueueDeferredDestroyForTests(texture);
                }

                return true;
            };
            MaterialThumbnailProvider.ThumbnailsReady += onReady;
            try
            {
                MaterialThumbnailProvider.TryGet(MaterialPathAt(0), "Material", out _);

                // Tick T: the queue drains, the entry is deferred, and the
                // drained backlog raises on this very tick.
                MaterialThumbnailProvider.TickForTests();
                Assert.That(queued, Is.True, "the single queued path must have been served on this tick");
                Assert.That(readySignals, Is.EqualTo(1), "a drained backlog raises on the tick that drained it");
                Assert.That(texture == null, Is.False, "the raising tick itself never destroys");

                // Tick T+1: the pump the deferred rebind can still lose to.
                MaterialThumbnailProvider.TickForTests();
                Assert.That(
                    texture == null,
                    Is.False,
                    "the first pump after the raise must leave the texture alive for the deferred rebind");

                // Tick T+2: the rebind has had its turn.
                MaterialThumbnailProvider.TickForTests();
                Assert.That(
                    texture == null,
                    Is.True,
                    "one pump of margin, not an indefinite hold");
            }
            finally
            {
                MaterialThumbnailProvider.ThumbnailsReady -= onReady;
                MaterialThumbnailProvider.ProcessOverrideForTests = null;
                MaterialThumbnailProvider.Shutdown();
                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
