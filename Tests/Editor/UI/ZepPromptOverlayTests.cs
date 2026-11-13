using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Editor.UI.ContextMenu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace Zep.Editor.UI.Tests
{
    // What the prompt overlay does with a name it cannot submit, and what New
    // Tag does when the controller refuses one.
    //
    // Submit has always refused a blank value, but it refused it invisibly: the
    // primary button stayed enabled, so the click and the Return both read as a
    // dead control with no message and no way out but Escape. New Tag threw the
    // refusal away entirely — it dropped the typed name with the prompt and
    // reported only through the status bar, which drops a message identical to
    // the one it is already showing, so a second try at the same name said
    // nothing at all.
    //
    // Typing is driven in a real editor window wherever a test depends on a
    // change notification: a field only raises its change event through a
    // panel's dispatcher, which is also the only way a user ever reaches it.
    public sealed class ZepPromptOverlayTests
    {
        private const string TakenTagName = "PromptRefusalOccupant";

        [Test]
        public void APromptWithNothingTyped_OpensWithARefusingSubmitButton()
        {
            var prompt = new ZepPromptOverlayWidget();

            prompt.Show("New Tag", "Name", string.Empty, "Create", _ => { });

            Assert.That(SubmitButton(prompt).enabledSelf, Is.False,
                "an empty field has nothing to submit, and the button must say so");
        }

        [Test]
        public void APromptSeededWithAName_OpensReadyToSubmit()
        {
            var prompt = new ZepPromptOverlayWidget();

            // The refusing state must not leak across uses: Hide leaves the
            // widget ready for the next Show.
            prompt.Show("New Tag", "Name", string.Empty, "Create", _ => { });
            prompt.Hide();
            prompt.Show("Rename Folder", "Name", "Materials", "Rename", _ => { });

            Assert.That(SubmitButton(prompt).enabledSelf, Is.True);
        }

        [UnityTest]
        public IEnumerator WhitespaceOnly_KeepsTheButtonRefusingAndSubmitDoesNothing()
        {
            var prompt = new ZepPromptOverlayWidget();
            string submitted = null;
            prompt.Show("New Folder", "Name", "Folder", "Create", value => submitted = value);

            var window = ShowInWindow(prompt);
            yield return null;
            try
            {
                Assume.That(SubmitButton(prompt).enabledSelf, Is.True, "precondition: it opens submittable");

                Field(prompt).value = "   ";

                Assert.That(SubmitButton(prompt).enabledSelf, Is.False,
                    "clearing the name down to whitespace must disable the button again");

                Submit(prompt);

                Assert.That(prompt.IsVisible, Is.True, "the prompt stays open");
                Assert.That(submitted, Is.Null, "and nothing is submitted");
            }
            finally
            {
                CloseWindow(window);
            }
        }

        [UnityTest]
        public IEnumerator TypingANonBlankName_EnablesTheButtonAndSubmitDeliversItTrimmed()
        {
            var prompt = new ZepPromptOverlayWidget();
            string submitted = null;
            prompt.Show("New Folder", "Name", string.Empty, "Create", value => submitted = value);

            var window = ShowInWindow(prompt);
            yield return null;
            try
            {
                Assume.That(SubmitButton(prompt).enabledSelf, Is.False, "precondition: an empty name refuses");

                Field(prompt).value = "  Materials  ";

                Assert.That(SubmitButton(prompt).enabledSelf, Is.True,
                    "one non-blank character brings the button back");

                Submit(prompt);

                Assert.That(prompt.IsVisible, Is.False);
                Assert.That(submitted, Is.EqualTo("Materials"));
            }
            finally
            {
                CloseWindow(window);
            }
        }

        [Test]
        public void AConfirmPrompt_KeepsItsButtonEnabledWithNoField()
        {
            var prompt = new ZepPromptOverlayWidget();
            prompt.Show("New Folder", "Name", string.Empty, "Create", _ => { });
            Assume.That(SubmitButton(prompt).enabledSelf, Is.False, "precondition: the text prompt refuses");

            var confirmed = false;
            prompt.ShowConfirm("Compress", "Compress 2 items?", "Compress", () => confirmed = true);

            Assert.That(Field(prompt).style.display.value, Is.EqualTo(DisplayStyle.None),
                "a confirm prompt has no text input");
            Assert.That(SubmitButton(prompt).enabledSelf, Is.True,
                "and therefore nothing to type — its button must stay enabled");

            Submit(prompt);

            Assert.That(confirmed, Is.True);
            Assert.That(prompt.IsVisible, Is.False);
        }

        // The Return leg, driven through the field's own key handler. The
        // typed-name half is the control: it proves the key actually reaches
        // the handler, so the blank half is a refusal rather than a dispatch
        // that went nowhere.
        [UnityTest]
        public IEnumerator ReturnOnABlankField_DoesNothing_ButReturnOnATypedNameSubmits()
        {
            var prompt = new ZepPromptOverlayWidget();
            string submitted = null;
            prompt.Show("New Folder", "Name", string.Empty, "Create", value => submitted = value);

            var window = ShowInWindow(prompt);
            yield return null;
            try
            {
                SendReturn(Field(prompt));

                Assert.That(prompt.IsVisible, Is.True, "Return on a blank name keeps the prompt open");
                Assert.That(submitted, Is.Null, "and submits nothing");

                Field(prompt).value = "Materials";
                SendReturn(Field(prompt));

                Assert.That(submitted, Is.EqualTo("Materials"),
                    "Return on a typed name still submits — the key path is live");
                Assert.That(prompt.IsVisible, Is.False);
            }
            finally
            {
                CloseWindow(window);
            }
        }

        [Test]
        public void ARefusedNewTag_ReopensThePromptWithWhatWasTyped_AndSaysWhy()
        {
            var errors = new List<string>();
            using var rig = BuildRigWithTakenTag();
            var prompt = new ZepPromptOverlayWidget();
            var actions = NewTagActions(rig, prompt, errors);
            BeginCreateTag(actions, rig, prompt);

            SubmitTyped(prompt, TakenTagName);

            Assert.That(TagCount(rig, TakenTagName), Is.EqualTo(1), "a refused create writes nothing");
            Assert.That(errors.Count, Is.EqualTo(1), "the toast is the channel that reports the refusal");
            Assert.That(errors[0], Is.EqualTo(rig.Controller.StatusMessage),
                "and it carries the panel's own reason");
            Assert.That(errors[0], Is.Not.Empty);
            Assert.That(prompt.IsVisible, Is.True,
                "a refused name leaves the user in the prompt, the way the sidebar rename does");
            Assert.That(Field(prompt).value, Is.EqualTo(TakenTagName),
                "the field re-opens with what was typed");
            Assert.That(prompt.Q<Label>("zep-prompt-title").text, Is.EqualTo("New Tag"),
                "it is the same prompt, not a second one stacked on top");
        }

        [Test]
        public void TwoSubmitsOfTheSameRefusedTagName_SayItTwice()
        {
            var errors = new List<string>();
            using var rig = BuildRigWithTakenTag();
            var prompt = new ZepPromptOverlayWidget();
            var actions = NewTagActions(rig, prompt, errors);
            BeginCreateTag(actions, rig, prompt);

            SubmitTyped(prompt, TakenTagName);
            SubmitTyped(prompt, TakenTagName);

            Assert.That(errors.Count, Is.EqualTo(2),
                "the second attempt at the same name must not be deduplicated away");
            Assert.That(prompt.IsVisible, Is.True);
            Assert.That(TagCount(rig, TakenTagName), Is.EqualTo(1));
        }

        [UnityTest]
        public IEnumerator TheReopenedTagPrompt_KeepsTheColorThatWasPicked()
        {
            var errors = new List<string>();
            using var rig = BuildRigWithTakenTag();
            var prompt = new ZepPromptOverlayWidget();
            var actions = NewTagActions(rig, prompt, errors);
            BeginCreateTag(actions, rig, prompt);

            var window = ShowInWindow(prompt);
            yield return null;
            try
            {
                ColorField(prompt).value = ZepTagColor.Red;

                SubmitTyped(prompt, TakenTagName);

                Assert.That(prompt.IsVisible, Is.True);
                Assert.That(ColorField(prompt).value, Is.EqualTo(ZepTagColor.Red),
                    "the re-shown prompt keeps the chosen color instead of resetting it to Blue");
            }
            finally
            {
                CloseWindow(window);
            }
        }

        [Test]
        public void AnAcceptedTagName_CreatesTheTagAndClosesThePromptWithoutAToast()
        {
            var errors = new List<string>();
            using var rig = BuildRigWithTakenTag();
            var prompt = new ZepPromptOverlayWidget();
            var actions = NewTagActions(rig, prompt, errors);
            BeginCreateTag(actions, rig, prompt);

            SubmitTyped(prompt, "PromptRefusalAccepted");

            Assert.That(prompt.IsVisible, Is.False, "an accepted name closes the prompt");
            Assert.That(errors, Is.Empty, "and reports nothing");
            Assert.That(TagCount(rig, "PromptRefusalAccepted"), Is.EqualTo(1), rig.Controller.StatusMessage);
        }

        private static ZepUiControllerRig BuildRigWithTakenTag()
        {
            var rig = ZepUiControllerRig.Build(
                Array.Empty<AssetRecord>(),
                new[] { "Assets", "Packages" });
            Assume.That(
                rig.Controller.CreateTag(TakenTagName, ZepTagColor.Blue),
                Is.True,
                "precondition: the name is taken by a tag that exists");
            return rig;
        }

        private static ContextMenuActions NewTagActions(
            ZepUiControllerRig rig,
            ZepPromptOverlayWidget prompt,
            ICollection<string> errors)
        {
            return new ContextMenuActions(
                rig.Controller,
                null,
                prompt,
                rig.State,
                null,
                null,
                null,
                showError: errors.Add);
        }

        private static void BeginCreateTag(
            ContextMenuActions actions,
            ZepUiControllerRig rig,
            ZepPromptOverlayWidget prompt)
        {
            actions.PromptCreateTag(rig.State.PrimaryPane);
            Assume.That(prompt.IsVisible, Is.True, "precondition: New Tag opens the prompt");
            Assume.That(Field(prompt).value, Is.Empty, "precondition: it opens with an empty name");
        }

        private static int TagCount(ZepUiControllerRig rig, string tagName)
        {
            return rig.Controller.GetSharedTags().Count(tag => tag.Name == tagName);
        }

        // The keyboard's own leg is covered above; this is the value the user
        // left in the field when the submit ran.
        private static void SubmitTyped(ZepPromptOverlayWidget prompt, string typed)
        {
            Field(prompt).value = typed;
            Submit(prompt);
        }

        private static EditorWindow ShowInWindow(ZepPromptOverlayWidget prompt)
        {
            var window = ScriptableObject.CreateInstance<PromptOverlayTestWindow>();
            window.position = new Rect(100f, 100f, 480f, 320f);
            window.rootVisualElement.Add(prompt);
            window.Show();
            return window;
        }

        private static void CloseWindow(EditorWindow window)
        {
            window.Close();
            Object.DestroyImmediate(window);
        }

        private static void SendReturn(VisualElement target)
        {
            using var evt = KeyDownEvent.GetPooled(new Event
            {
                type = EventType.KeyDown,
                keyCode = KeyCode.Return
            });
            evt.target = target;
            target.SendEvent(evt);
        }

        private static void Submit(ZepPromptOverlayWidget prompt)
        {
            var submit = typeof(ZepPromptOverlayWidget).GetMethod(
                "Submit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(submit, Is.Not.Null, "the overlay's submit leg");
            submit.Invoke(prompt, null);
        }

        private static TextField Field(ZepPromptOverlayWidget prompt)
        {
            return prompt.Q<TextField>("zep-prompt-field");
        }

        private static PopupField<ZepTagColor> ColorField(ZepPromptOverlayWidget prompt)
        {
            var field = prompt.Q<PopupField<ZepTagColor>>("zep-prompt-popup-field");
            Assert.That(field, Is.Not.Null, "the prompt's color picker");
            return field;
        }

        // The submit button carries no name; the primary-action class is how the
        // panel identifies it.
        private static Button SubmitButton(ZepPromptOverlayWidget prompt)
        {
            var button = prompt.Query<Button>()
                .ToList()
                .FirstOrDefault(candidate =>
                    candidate.ClassListContains(ZepUiClasses.ActionButtonPrimary));
            Assert.That(button, Is.Not.Null, "the prompt's primary action button");
            return button;
        }

        private sealed class PromptOverlayTestWindow : EditorWindow
        {
        }
    }
}
