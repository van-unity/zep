using NUnit.Framework;

namespace Zep.Editor.UI.Tests
{
    // Behavior of the drag-hover edge auto-scroll policy: where in the
    // viewport a hover scrolls, in which direction, and how the speed ramps
    // toward (and clamps beyond) the edge. Viewport spans y 100..500, so the
    // edge zones are 100..140 (up) and 460..500 (down) with EdgeZonePx = 40.
    public sealed class DragEdgeAutoScrollTests
    {
        private const float Top = 100f;
        private const float Bottom = 500f;

        private static float Delta(float cursorY)
            => DragEdgeAutoScroll.ComputeScrollDeltaPerTick(cursorY, Top, Bottom);

        [Test]
        public void CursorInViewportCenter_DoesNotScroll()
        {
            Assert.That(Delta(300f), Is.Zero);
        }

        [Test]
        public void CursorExactlyOnZoneBoundaries_DoesNotScroll()
        {
            Assert.That(Delta(Top + DragEdgeAutoScroll.EdgeZonePx), Is.Zero);
            Assert.That(Delta(Bottom - DragEdgeAutoScroll.EdgeZonePx), Is.Zero);
        }

        [Test]
        public void CursorInTopZone_ScrollsUp()
        {
            Assert.That(Delta(Top + 10f), Is.Negative);
        }

        [Test]
        public void CursorInBottomZone_ScrollsDown()
        {
            Assert.That(Delta(Bottom - 10f), Is.Positive);
        }

        [Test]
        public void CursorAtZoneMidpoint_ScrollsAtHalfSpeed()
        {
            var half = DragEdgeAutoScroll.MaxSpeedPxPerTick * 0.5f;
            Assert.That(Delta(Top + DragEdgeAutoScroll.EdgeZonePx * 0.5f), Is.EqualTo(-half).Within(0.001f));
            Assert.That(Delta(Bottom - DragEdgeAutoScroll.EdgeZonePx * 0.5f), Is.EqualTo(half).Within(0.001f));
        }

        [Test]
        public void SpeedRampsUpTowardTheEdge()
        {
            Assert.That(Delta(Top + 5f), Is.LessThan(Delta(Top + 35f)));
            Assert.That(Delta(Bottom - 5f), Is.GreaterThan(Delta(Bottom - 35f)));
        }

        [Test]
        public void CursorAtTheExactEdge_ScrollsAtFullSpeed()
        {
            Assert.That(Delta(Top), Is.EqualTo(-DragEdgeAutoScroll.MaxSpeedPxPerTick));
            Assert.That(Delta(Bottom), Is.EqualTo(DragEdgeAutoScroll.MaxSpeedPxPerTick));
        }

        [Test]
        public void CursorBeyondTheEdge_ClampsToFullSpeed()
        {
            Assert.That(Delta(Top - 250f), Is.EqualTo(-DragEdgeAutoScroll.MaxSpeedPxPerTick));
            Assert.That(Delta(Bottom + 250f), Is.EqualTo(DragEdgeAutoScroll.MaxSpeedPxPerTick));
        }
    }
}
