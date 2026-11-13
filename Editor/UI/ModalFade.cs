using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI
{
    // The scrim-and-card fade every modal in the panel plays: the scrim and its
    // card fade in together while the card lifts into place, and on the way out
    // the card sinks back before the scrim is taken out of layout.
    //
    // The invariant that earns this owner is the version guard. Both halves
    // finish on a scheduled item (16 ms in, 140 ms out) whose work must not run
    // if another Show or Hide started in the meantime — otherwise a Hide during
    // a fade-in repaints the scrim opaque, and a Show during a fade-out is
    // blanked by the previous Hide's display write. A `static` helper cannot
    // hold that: the deferred callback has to compare against a counter that
    // later calls keep bumping, so each host owns one instance of this — exactly
    // the `_animationVersion` field it used to declare itself.
    internal sealed class ModalFade
    {
        private const float ShowLiftPx = 12f;
        private const float HideLiftPx = 10f;
        private const float RestingScale = 0.985f;
        private const long ShowDelayMs = 16;
        private const long HideDelayMs = 140;

        private int _version;

        // onShown is the one thing that differs between the panel's show sites:
        // which control takes the keyboard once the card has landed.
        public void Show(VisualElement root, VisualElement card, Action onShown)
        {
            var version = ++_version;
            root.style.display = DisplayStyle.Flex;
            root.style.opacity = 0f;
            card.style.opacity = 0f;
            card.style.translate = new Translate(0f, ShowLiftPx);
            card.style.scale = new Scale(new Vector3(RestingScale, RestingScale, 1f));
            card.schedule.Execute(() =>
            {
                if (version != _version)
                    return;
                root.style.opacity = 1f;
                card.style.opacity = 1f;
                card.style.translate = new Translate(0f, 0f);
                card.style.scale = new Scale(Vector3.one);
                onShown?.Invoke();
            }).ExecuteLater(ShowDelayMs);
        }

        public void Hide(VisualElement root, VisualElement card)
        {
            var version = ++_version;
            root.style.opacity = 0f;
            card.style.opacity = 0f;
            card.style.translate = new Translate(0f, HideLiftPx);
            card.style.scale = new Scale(new Vector3(RestingScale, RestingScale, 1f));
            card.schedule.Execute(() =>
            {
                if (version != _version)
                    return;
                root.style.display = DisplayStyle.None;
            }).ExecuteLater(HideDelayMs);
        }
    }
}
