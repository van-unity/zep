using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace Zep.Editor.UI.AssetsArea
{
    // Owns the transient ring for an Inspector ping. Rows and tiles are
    // virtualized and recycled, so each realized item keeps at most one pulse;
    // Play restarts it and Reset prevents an in-flight effect following a pooled
    // element to a different asset.
    //
    // The effect is deliberately quiet: one thin ring in the selection accent
    // that holds for a beat and then fades while growing by barely a percent.
    // The entry itself is never scaled and there is no second ring — a ping
    // says "this one", it does not shout.
    internal sealed class AssetPingPulse : VisualElement
    {
        internal const int DurationMs = 700;
        internal const float StartOpacity = 0.9f;
        internal const float EndScale = 1.015f;

        private readonly VisualElement _ring;

        private ValueAnimation<float> _animation;
        private int _playVersion;

        internal AssetPingPulse()
        {
            name = "zep-asset-ping-pulse";
            AddToClassList(ZepUiClasses.AssetPingPulse);
            pickingMode = PickingMode.Ignore;
            style.display = DisplayStyle.None;

            _ring = new VisualElement
            {
                pickingMode = PickingMode.Ignore,
            };
            _ring.AddToClassList(ZepUiClasses.AssetPingRing);
            Add(_ring);
        }

        internal void Play()
        {
            var version = ++_playVersion;
            StopAnimation();

            ApplyFrame(_ring, 0f);
            style.display = DisplayStyle.Flex;
            BringToFront();

            // An explicit-value animation does not depend on an earlier style
            // pass. This matters when PingPath creates the overlay while a
            // virtualized row is binding: a USS transition could otherwise skip
            // its visible starting frame before the first repaint.
            _animation = _ring.experimental.animation
                .Start(0f, 1f, DurationMs, ApplyFrame)
                .Ease(Easing.Linear);
            _animation.OnCompleted(() =>
            {
                if (version == _playVersion)
                {
                    _animation = null;
                    style.display = DisplayStyle.None;
                }
            });
        }

        internal void Reset()
        {
            _playVersion++;
            StopAnimation();
            style.display = DisplayStyle.None;
        }

        // Opacity eases in to its fade — the ring holds, then lets go — while
        // the scale eases out, so the growth is front-loaded and imperceptible
        // by the time the ring is gone.
        private static void ApplyFrame(VisualElement ring, float progress)
        {
            var p = Mathf.Clamp01(progress);
            var fade = p * p;
            var grow = 1f - Mathf.Pow(1f - p, 3f);
            var scale = Mathf.Lerp(1f, EndScale, grow);

            ring.style.opacity = StartOpacity * (1f - fade);
            ring.style.scale = new Scale(new Vector2(scale, scale));
        }

        private void StopAnimation()
        {
            var animation = _animation;
            _animation = null;
            animation?.Stop();
        }
    }
}
