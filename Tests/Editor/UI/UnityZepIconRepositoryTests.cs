using System.Collections.Generic;
using System.Reflection;
using Zep.Core.FileOperations;
using Zep.Editor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Zep.Tests.Editor.UI
{
    // The preview-upgrade pump's backlog policy. Every bind of a row without a
    // cached icon queues an upgrade, and every PreviewsReady rebinds the whole
    // realized window of the active assets feature — so a fast scroll over a
    // large folder used to serve the on-screen tiles LAST while rebinding on
    // every editor tick until the backlog ran out. What is asserted here is
    // the policy: which end the backlog is drained from and how often the
    // ready signal is raised. The work the pump budgets (an asset load plus
    // preview generation) is replaced by UpgradeOverrideForTests — that is the
    // Unity boundary, not the behaviour under test.
    public sealed class UnityZepIconRepositoryTests
    {
        // Mirrors the production constants; reading them from the repository
        // would make the assertions unfalsifiable.
        private const int MaxPreviewUpgradesPerTick = 8;
        private const int MaxPendingPreviews = 512;
        private const int PreviewsReadyEveryTicks = 8;
        private const string CachedIconPath =
            "Packages/com.van-unity.zep/Editor/UI/Icons/folder-icon.png";

        private UnityZepIconRepository _repository;
        private List<string> _upgradeOrder;
        private int _readySignals;

        [SetUp]
        public void SetUp()
        {
            _repository = new UnityZepIconRepository();
            _upgradeOrder = new List<string>();
            _readySignals = 0;
            _repository.PreviewsReady += OnPreviewsReady;
            _repository.UpgradeOverrideForTests = path =>
            {
                _upgradeOrder.Add(path);
                return UnityZepIconRepository.PreviewUpgrade.Applied;
            };
        }

        [TearDown]
        public void TearDown()
        {
            _repository.PreviewsReady -= OnPreviewsReady;
            _repository.UpgradeOverrideForTests = null;
            _repository.Teardown();
        }

        private void OnPreviewsReady(System.Collections.Generic.IReadOnlyList<string> appliedPaths)
        {
            _readySignals++;
        }

        private static string PathAt(int index)
        {
            return "Assets/IconPump/Item" + index + ".asset";
        }

        private void Request(int count)
        {
            for (var i = 0; i < count; i++)
            {
                _repository.GetAssetIcon(PathAt(i), "TextAsset");
            }
        }

        // -------------------------------------------------------------------
        // Folder glyph (I3 / AC2). Emptiness is a fact the repository is TOLD,
        // not one it assumes: with nothing to ask, every folder is unknown and
        // unknown paints the ordinary folder. The fixture's own repository is
        // the preview pump's, so these build their own.
        // -------------------------------------------------------------------

        [Test]
        public void FolderGlyph_WithNoKnownEmptinessSource_PaintsTheRegularFolder()
        {
            var repository = new UnityZepIconRepository();
            try
            {
                var regular = repository.GetFolderGlyph(false);
                var empty = repository.GetFolderGlyph(true);
                Assert.That(regular, Is.Not.Null);
                Assert.That(empty, Is.Not.SameAs(regular),
                    "the two glyphs must be distinguishable in this environment, or "
                    + "the assertion below could not tell them apart");

                var painted = repository.GetFolderIcon("Assets/NeverHeardOf");

                Assert.That(painted, Is.SameAs(regular),
                    "a repository with no emptiness source knows nothing about this "
                    + "folder, and unknown is not empty - painting the empty glyph "
                    + "here is exactly what makes every folder look empty for "
                    + "seconds after a domain reload");
            }
            finally
            {
                repository.Teardown();
            }
        }

        [Test]
        public void FolderGlyph_FollowsTheKnownEmptinessSourcePerFolder()
        {
            var asked = new List<string>();
            var repository = new UnityZepIconRepository(
                null,
                folderPath =>
                {
                    asked.Add(folderPath);
                    return folderPath == "Assets/Empty";
                });
            try
            {
                var regular = repository.GetFolderGlyph(false);
                var empty = repository.GetFolderGlyph(true);
                Assert.That(empty, Is.Not.SameAs(regular));

                Assert.That(
                    repository.GetFolderIcon("Assets/Empty"),
                    Is.SameAs(empty),
                    "a folder the source reports as known empty paints the empty glyph");
                Assert.That(
                    repository.GetFolderIcon("Assets/Full"),
                    Is.SameAs(regular),
                    "and one it does not paints the ordinary folder - which is also "
                    + "what an inverted forwarder behind this delegate would break");
                Assert.That(
                    asked,
                    Is.EqualTo(new[] { "Assets/Empty", "Assets/Full" }),
                    "the repository asks per folder instead of holding a copy of a "
                    + "set, so the answer cannot go stale between refresh signals");
            }
            finally
            {
                repository.Teardown();
            }
        }

        [Test]
        public void CachedMini_AllowsALaterPreviewRequest_WithoutDuplicatePendingWork()
        {
            Assert.That(AssetDatabase.GetCachedIcon(CachedIconPath), Is.Not.Null,
                "the regression requires a real cached mini, not an uncached fallback");
            var mini = _repository.GetAssetIcon(CachedIconPath, "Texture2D", allowPreviewUpgrade: false);
            _repository.PollPendingPreviewsForTests();
            Assert.That(_upgradeOrder, Is.Empty, "the first caller opted out of stock previews");

            for (var i = 0; i < 3; i++)
            {
                Assert.That(_repository.GetAssetIcon(CachedIconPath, "Texture2D"), Is.SameAs(mini),
                    "requesting an upgrade still returns the cached mini immediately");
            }
            _repository.PollPendingPreviewsForTests();

            Assert.That(_upgradeOrder, Is.EqualTo(new[] { CachedIconPath }));
            Assert.That(_readySignals, Is.EqualTo(1));
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(false, false)]
        public void TerminalPreviewResult_DoesNotRequeueOnLaterBinds(bool applied, bool cachedMini)
        {
            var assetPath = cachedMini ? CachedIconPath : PathAt(0);
            _repository.UpgradeOverrideForTests = path =>
            {
                _upgradeOrder.Add(path);
                return applied
                    ? UnityZepIconRepository.PreviewUpgrade.Applied
                    : UnityZepIconRepository.PreviewUpgrade.Dropped;
            };
            _repository.GetAssetIcon(assetPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            for (var i = 0; i < 3; i++)
            {
                _repository.GetAssetIcon(assetPath, "Texture2D");
                _repository.PollPendingPreviewsForTests();
            }

            Assert.That(_upgradeOrder, Is.EqualTo(new[] { assetPath }));
            Assert.That(_readySignals, Is.EqualTo(applied ? 1 : 0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RealNullCachedIcon_RetainsItsAttemptState(bool complete)
        {
            var cache = (Dictionary<string, (Texture2D Icon, bool PreviewUpgradeComplete)>)
                typeof(UnityZepIconRepository).GetField(
                    "_assetIconCache", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_repository);
            // Represents Unity returning no mini and no fallback texture. Keep
            // this boundary result deterministic even in skins with a fallback.
            cache[PathAt(0)] = (null, complete);

            for (var i = 0; i < 3; i++)
            {
                Assert.That(ReferenceEquals(_repository.GetAssetIcon(PathAt(0), "TextAsset"), null), Is.True);
                _repository.PollPendingPreviewsForTests();
            }

            Assert.That(_upgradeOrder.Count, Is.EqualTo(complete ? 0 : 1));
        }

        [Test]
        public void DestroyedPreview_RefetchesTheMiniAndAllowsAFreshAttempt()
        {
            var texture = new Texture2D(1, 1);
            try
            {
                typeof(UnityZepIconRepository).GetMethod(
                    "StoreAssetIcon", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(_repository, new object[] { CachedIconPath, texture });
                Assert.That(_repository.GetAssetIcon(CachedIconPath, "Texture2D"), Is.SameAs(texture));
                _repository.PollPendingPreviewsForTests();
                Object.DestroyImmediate(texture);
                Assert.That(texture == null && !ReferenceEquals(texture, null), Is.True,
                    "the destroyed preview must exercise Unity fake-null rather than CLR null");

                var replacement = _repository.GetAssetIcon(CachedIconPath, "Texture2D");
                Assert.That(replacement != null, Is.True);
                Assert.That(ReferenceEquals(replacement, texture), Is.False);
                _repository.PollPendingPreviewsForTests();

                Assert.That(_upgradeOrder, Is.EqualTo(new[] { CachedIconPath, CachedIconPath }));
            }
            finally
            {
                if (texture != null) Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void CapacityDroppedPreview_CanBeRequestedAgainForACachedMini()
        {
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            Request(MaxPendingPreviews);

            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            Assert.That(_upgradeOrder[0], Is.EqualTo(CachedIconPath),
                "backlog eviction is not a terminal preview outcome; a visible mini can retry");
            Assert.That(_upgradeOrder.Count, Is.EqualTo(MaxPreviewUpgradesPerTick));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Invalidation_AllowsANewAttemptAfterATerminalResult(bool entireCache)
        {
            _repository.UpgradeOverrideForTests = path =>
            {
                _upgradeOrder.Add(path);
                return UnityZepIconRepository.PreviewUpgrade.Dropped;
            };
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            if (entireCache) _repository.InvalidateAssetIconCache();
            else _repository.InvalidateAssetIcon(CachedIconPath);
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            Assert.That(_upgradeOrder, Is.EqualTo(new[] { CachedIconPath, CachedIconPath }));
        }

        [Test]
        public void CacheCapacityEviction_DropsTheTerminalAttemptWithItsIcon()
        {
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();
            // Missing assets still cache their fallback mini and attempt state.
            // Overflow that cache without adding work to the preview backlog.
            for (var i = 0; i < 8192; i++)
            {
                _repository.GetAssetIcon(PathAt(i), "TextAsset", allowPreviewUpgrade: false);
            }

            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            Assert.That(_upgradeOrder, Is.EqualTo(new[] { CachedIconPath, CachedIconPath }));
        }

        [Test]
        public void Teardown_DropsTerminalAndPendingAttempts_AndAllowsReuse()
        {
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();
            _repository.GetAssetIcon(PathAt(0), "TextAsset");

            _repository.Teardown();
            _repository.PollPendingPreviewsForTests();
            _repository.GetAssetIcon(CachedIconPath, "Texture2D");
            _repository.PollPendingPreviewsForTests();

            Assert.That(_upgradeOrder, Is.EqualTo(new[] { CachedIconPath, CachedIconPath }),
                "reuse starts a fresh attempt and cannot revive the old pending path");
        }

        [Test]
        public void Polling_AfterAFastScroll_ServesTheNewestRequestFirst()
        {
            const int requested = 1500;
            Request(requested);

            _repository.PollPendingPreviewsForTests();

            Assert.That(
                _upgradeOrder.Count,
                Is.EqualTo(MaxPreviewUpgradesPerTick),
                "one tick must respect the per-tick upgrade budget");
            Assert.That(
                _upgradeOrder[0],
                Is.EqualTo(PathAt(requested - 1)),
                "the newest request is the tile on screen, so it must be upgraded first");
            Assert.That(
                _upgradeOrder[MaxPreviewUpgradesPerTick - 1],
                Is.EqualTo(PathAt(requested - MaxPreviewUpgradesPerTick)),
                "the whole first batch must come off the back, not just its first entry");
        }

        [Test]
        public void Polling_ALongBacklog_CoalescesTheReadySignal()
        {
            const int ticks = 100;
            Request(1500);

            for (var i = 0; i < ticks; i++)
            {
                _repository.PollPendingPreviewsForTests();
            }

            // One per tick is what the uncoalesced poll did, and each one is a
            // full rebind of every realized row.
            Assert.That(
                _readySignals,
                Is.LessThanOrEqualTo((ticks / PreviewsReadyEveryTicks) + 1),
                "a still-draining backlog must batch its rebind signal");
            Assert.That(
                _readySignals,
                Is.GreaterThan(0),
                "upgraded icons must still reach the view");
            Assert.That(
                _upgradeOrder.Count,
                Is.EqualTo(MaxPendingPreviews),
                "the backlog must be bounded — everything kept drains within these ticks");
        }

        [Test]
        public void Polling_APathThatIsNotReadyYet_RetriesItBehindTheOnScreenOnes()
        {
            const string stuck = "Assets/IconPump/Stuck.asset";
            _repository.UpgradeOverrideForTests = path =>
            {
                _upgradeOrder.Add(path);
                return path == stuck
                    ? UnityZepIconRepository.PreviewUpgrade.Pending
                    : UnityZepIconRepository.PreviewUpgrade.Applied;
            };

            _repository.GetAssetIcon(stuck, "TextAsset");
            for (var i = 0; i < MaxPreviewUpgradesPerTick; i++)
            {
                _repository.GetAssetIcon(PathAt(i), "TextAsset");
            }

            _repository.PollPendingPreviewsForTests();

            Assert.That(
                _upgradeOrder,
                Does.Not.Contain(stuck),
                "the oldest, still-generating path must not be retried ahead of the newer ones");

            // Nothing else is left, so the retry finally gets its turn — the
            // starvation guard the old FIFO existed for still holds.
            _repository.PollPendingPreviewsForTests();

            Assert.That(
                _upgradeOrder[_upgradeOrder.Count - 1],
                Is.EqualTo(stuck),
                "a retried path must still be served once the newer work is done");
        }
    }
}
