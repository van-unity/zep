using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Zep.Core.Domain;
using Zep.Core.Indexing;
using Zep.Core.State;
using Zep.Editor.Bridge.Theming;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Zep.Editor.UI.Tests
{
    // The settings drawer's Finder Tags section can destroy tag definitions —
    // and, with them, every assignment of that tag anywhere in the project. These
    // cover the two guards that stand between a stray click and that loss, plus
    // the refusal path of a rename, which used to keep the rejected text in the
    // row and say nothing.
    //
    // The confirmation is answered through the controller's optional dialog Func:
    // EditorUtility.DisplayDialog cannot be answered in a batchmode run, so
    // "declining destroys nothing" has no proof without it.
    public sealed class ZepTagSettingsGuardTests
    {
        private const string IconPath = "Assets/Icon.png";

        [Test]
        public void RemovingATag_WithTheConfirmationDeclined_TouchesNothing()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: false);
            var definitionsBefore = harness.StoredTagSummaries();
            var statusBefore = harness.Rig.Controller.StatusMessage;

            harness.Invoke("OnTagDefinitionRemoved", "red");

            Assert.That(harness.Prompts.Count, Is.EqualTo(1), "The ✕ must ask before it destroys anything.");
            Assert.That(
                harness.StoredTagSummaries(),
                Is.EqualTo(definitionsBefore),
                "A declined confirmation must leave the tag definitions exactly as they were.");
            Assert.That(
                harness.Rig.Gateway.GetTagIds(AssetPath.From(IconPath)),
                Is.EqualTo(new[] { "red" }),
                "A declined confirmation must leave the tag's assignments in place.");
            Assert.That(
                harness.Rig.Controller.StatusMessage,
                Is.EqualTo(statusBefore),
                "Declining is a clean no-op: no command ran, so no status was produced.");
            Assert.That(harness.Toasts, Is.Empty, "Declining must not report anything.");
        }

        [Test]
        public void RemovingATag_WithTheConfirmationAccepted_RemovesItAndRePushesTheTagsState()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);

            harness.Invoke("OnTagDefinitionRemoved", "red");

            Assert.That(
                harness.StoredTagIds(),
                Is.EqualTo(new[] { "blue" }),
                "A confirmed removal drops the definition, exactly as it did before the guard.");
            Assert.That(
                harness.Rig.Gateway.GetTagIds(AssetPath.From(IconPath)),
                Is.Empty,
                "Removing the definition removes the tag from the assets that carried it.");
            Assert.That(
                harness.ModelTagIds(),
                Is.EqualTo(new[] { "blue" }),
                "The drawer's tag state is re-pushed after a confirmed removal.");
        }

        [Test]
        public void ResettingTags_WithTheConfirmationDeclined_LeavesEveryDefinitionUntouched()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: false);
            var definitionsBefore = harness.StoredTagSummaries();
            var statusBefore = harness.Rig.Controller.StatusMessage;

            harness.Invoke("OnResetTagsRequested");

            Assert.That(harness.Prompts.Count, Is.EqualTo(1), "Reset Defaults must ask first.");
            Assert.That(
                harness.StoredTagSummaries(),
                Is.EqualTo(definitionsBefore),
                "A declined reset must leave the whole tag vocabulary — ids, names, colours, favourites — identical.");
            Assert.That(
                harness.Rig.Gateway.GetTagIds(AssetPath.From(IconPath)),
                Is.EqualTo(new[] { "red" }),
                "A declined reset must not drop any assignment.");
            Assert.That(harness.Rig.Controller.StatusMessage, Is.EqualTo(statusBefore));
            Assert.That(harness.Toasts, Is.Empty);
        }

        [Test]
        public void TheDestructiveTagConfirmations_NameWhatGoesAndThatAssignmentsGoWithIt()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: false);

            harness.Invoke("OnTagDefinitionRemoved", "red");
            var removePrompt = harness.Prompts[0];

            Assert.That(removePrompt.Message, Does.Contain("Red"), "The dialog must name the tag being removed.");
            Assert.That(
                removePrompt.Message,
                Does.Contain("every asset in the project"),
                "The dialog must say the tag's project-wide assignments go with the definition.");
            Assert.That(removePrompt.ConfirmLabel, Is.EqualTo("Remove"));

            harness.Invoke("OnResetTagsRequested");
            var resetPrompt = harness.Prompts[1];

            Assert.That(
                resetPrompt.Message,
                Does.Contain("defaults"),
                "The reset dialog must name what it replaces the tag list with.");
            Assert.That(
                resetPrompt.Message,
                Does.Contain("every asset in the project"),
                "The reset dialog must say the discarded tags leave the assets too.");
            Assert.That(resetPrompt.ConfirmLabel, Is.EqualTo("Reset"));
        }

        [Test]
        public void ARefusedRename_ReportsTheReasonAndRestoresTheStoredTagState()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            // The drawer holds the typed name independently of the store, so put
            // the model in the state the row is in when the rename is committed:
            // "Blue" already renamed to the name "Red" occupies.
            harness.Overlay.SettingsScreenModel.SetTags(new[]
            {
                new ZepTag("red", "Red", ZepTagColor.Red, true),
                new ZepTag("blue", "Red", ZepTagColor.Blue, true)
            });

            harness.Invoke(
                "OnTagDefinitionUpdated",
                new ZepTag("blue", "Red", ZepTagColor.Blue, true));

            Assert.That(
                harness.StoredTagSummaries(),
                Is.EqualTo(new[] { "red|Red|Red|True", "blue|Blue|Blue|True" }),
                "A refused rename must mutate nothing.");
            Assert.That(harness.Toasts.Count, Is.EqualTo(1), "The refusal must be reported, not swallowed.");
            Assert.That(harness.Toasts[0].Kind, Is.EqualTo(ZepToastKind.Error));
            Assert.That(
                harness.Toasts[0].Message,
                Is.EqualTo(harness.Rig.Controller.StatusMessage),
                "The toast must carry the controller's own reason for the refusal.");
            Assert.That(harness.Toasts[0].Message, Does.Contain("already exists"));
            Assert.That(
                harness.ModelTagNames(),
                Is.EqualTo(new[] { "Red", "Blue" }),
                "PushTagsState ran (the model holds the stored tags again). The row's own"
                + " revert is proven on the real screen path below.");
        }

        [Test]
        public void ASuccessfulRename_PushesTheNewStateAndReportsNothing()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);

            harness.Invoke(
                "OnTagDefinitionUpdated",
                new ZepTag("blue", "Azure", ZepTagColor.Blue, true));

            Assert.That(harness.StoredTagNames(), Is.EqualTo(new[] { "Red", "Azure" }));
            Assert.That(harness.ModelTagNames(), Is.EqualTo(new[] { "Red", "Azure" }));
            Assert.That(harness.Toasts, Is.Empty, "A rename that stuck must not report an error.");
        }

        // The three below drive the real row: the drawer's own TextField, committed
        // with Return, exactly as a user commits a rename. The refusal path pushes a
        // tag state that is value-equal to the one the model already holds, so no
        // rebuild follows it — the row itself has to put the stored name back.
        [Test]
        public void ARefusedDuplicateRename_PutsTheStoredNameBackInTheRow()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            var window = ScriptableObject.CreateInstance<TagSettingsTestWindow>();
            try
            {
                var screen = harness.MountSettingsScreen(window);
                var nameField = TagNameField(screen, "blue");
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                CommitTagName(nameField, "Red");

                Assert.That(
                    nameField.value,
                    Is.EqualTo("Blue"),
                    "A name another tag already uses is refused, so the row must show the stored name again.");
                Assert.That(
                    harness.StoredTagNames(),
                    Is.EqualTo(new[] { "Red", "Blue" }),
                    "A refused rename must mutate nothing.");
                Assert.That(harness.Toasts.Count, Is.EqualTo(1), "The refusal is still reported.");
                Assert.That(
                    ZepSettingsScreen.RebuildTagEditorCountForTests,
                    Is.Zero,
                    "The revert is a value write on the one row, not a rebuild of the tag editor.");
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void ARefusedBlankRename_PutsTheStoredNameBackInTheRow()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            var window = ScriptableObject.CreateInstance<TagSettingsTestWindow>();
            try
            {
                var screen = harness.MountSettingsScreen(window);
                var nameField = TagNameField(screen, "blue");

                CommitTagName(nameField, "   ");

                Assert.That(
                    nameField.value,
                    Is.EqualTo("Blue"),
                    "A blank name is refused, so the row must show the stored name again.");
                Assert.That(harness.StoredTagNames(), Is.EqualTo(new[] { "Red", "Blue" }));
                Assert.That(harness.Toasts.Count, Is.EqualTo(1));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void AnAcceptedRename_LeavesTheNewNameInTheRowAndRebuildsTheEditorOnce()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            var window = ScriptableObject.CreateInstance<TagSettingsTestWindow>();
            try
            {
                var screen = harness.MountSettingsScreen(window);
                ZepSettingsScreen.RebuildTagEditorCountForTests = 0;

                CommitTagName(TagNameField(screen, "blue"), "Azure");

                Assert.That(harness.StoredTagNames(), Is.EqualTo(new[] { "Red", "Azure" }));
                Assert.That(
                    TagNameField(screen, "blue").value,
                    Is.EqualTo("Azure"),
                    "A rename that stuck must leave the accepted name in the row.");
                Assert.That(harness.Toasts, Is.Empty);
                Assert.That(
                    ZepSettingsScreen.RebuildTagEditorCountForTests,
                    Is.EqualTo(1),
                    "An accepted rename updates the row exactly once.");
            }
            finally
            {
                window.Close();
            }
        }

        [TestCase("   ")]
        [TestCase("Red")]
        public void ARefusedColorEdit_KeepsTheStoredColorInTheSwatch(string pendingName)
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            var window = ScriptableObject.CreateInstance<TagSettingsTestWindow>();
            try
            {
                var screen = harness.MountSettingsScreen(window);
                var tag = harness.Rig.TagDefinitions.GetOrderedTags().Single(candidate => candidate.Id == "blue");
                var swatch = screen.Q<VisualElement>("zep-settings-tag-swatch-blue");
                Assert.That(swatch, Is.Not.Null);
                var items = screen.BuildTagColorMenuItems(tag, swatch, () => pendingName);

                items.Single(item => item.Label == "Green").OnClick.Invoke();

                Assert.That(harness.StoredTagSummaries(),
                    Is.EqualTo(new[] { "red|Red|Red|True", "blue|Blue|Blue|True" }));
                Assert.That(harness.Overlay.SettingsScreenModel.Tags.Single(candidate => candidate.Id == "blue").Color,
                    Is.EqualTo(ZepTagColor.Blue));
                Assert.That(screen.Q<VisualElement>("zep-settings-tag-swatch-blue"), Is.SameAs(swatch),
                    "a refused edit pushes the same committed model without rebuilding the row");
                Assert.That(swatch.style.backgroundColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Blue)),
                    "the rejected color must not appear as if it was saved");
                Assert.That(harness.Toasts.Count, Is.EqualTo(1));
                Assert.That(harness.Toasts[0].Kind, Is.EqualTo(ZepToastKind.Error));
                Assert.That(harness.Toasts[0].Message, Is.EqualTo(harness.Rig.Controller.StatusMessage));
            }
            finally
            {
                window.Close();
            }
        }

        [Test]
        public void AnAcceptedColorEdit_UpdatesTheStoreModelAndSwatch()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true);
            var window = ScriptableObject.CreateInstance<TagSettingsTestWindow>();
            try
            {
                var screen = harness.MountSettingsScreen(window);
                var tag = harness.Rig.TagDefinitions.GetOrderedTags().Single(candidate => candidate.Id == "blue");
                var swatch = screen.Q<VisualElement>("zep-settings-tag-swatch-blue");
                Assert.That(swatch, Is.Not.Null);
                var items = screen.BuildTagColorMenuItems(tag, swatch, () => tag.Name);

                items.Single(item => item.Label == "Green").OnClick.Invoke();

                Assert.That(harness.StoredTagSummaries(),
                    Is.EqualTo(new[] { "red|Red|Red|True", "blue|Blue|Green|True" }));
                Assert.That(harness.Overlay.SettingsScreenModel.Tags.Single(candidate => candidate.Id == "blue").Color,
                    Is.EqualTo(ZepTagColor.Green));
                Assert.That(screen.Q<VisualElement>("zep-settings-tag-swatch-blue").style.backgroundColor.value,
                    Is.EqualTo(ZepTagColorPalette.GetColor(ZepTagColor.Green)));
                Assert.That(harness.Toasts, Is.Empty);
            }
            finally
            {
                window.Close();
            }
        }

        private static TextField TagNameField(ZepSettingsScreen screen, string tagId)
        {
            var field = screen.Q<TextField>($"zep-settings-tag-name-{tagId}");
            Assert.That(field, Is.Not.Null, $"Expected a name field for tag '{tagId}'.");
            return field;
        }

        // The Enter leg the row registers — the same one a user hits.
        private static void CommitTagName(TextField nameField, string typed)
        {
            nameField.value = typed;
            using (var enter = KeyDownEvent.GetPooled('\0', KeyCode.Return, EventModifiers.None))
            {
                enter.target = nameField;
                nameField.SendEvent(enter);
            }
        }

        private sealed class TagSettingsTestWindow : EditorWindow
        {
        }

        [Test]
        public void WithoutACommandHandler_TheTagHandlersStayInertAndAskNothing()
        {
            using var harness = TagSettingsHarness.Build(confirmAnswer: true, withCommandHandler: false);

            Assert.DoesNotThrow(() => harness.Invoke(
                "OnTagDefinitionUpdated",
                new ZepTag("blue", "Azure", ZepTagColor.Blue, true)));
            Assert.DoesNotThrow(() => harness.Invoke("OnTagDefinitionRemoved", "red"));
            Assert.DoesNotThrow(() => harness.Invoke("OnResetTagsRequested"));

            Assert.That(
                harness.Prompts,
                Is.Empty,
                "A read-only panel must not ask about a destruction it cannot perform.");
            Assert.That(harness.Toasts, Is.Empty);
        }

        private sealed class RecordedPrompt
        {
            internal string Title;
            internal string Message;
            internal string ConfirmLabel;
        }

        private sealed class RecordedToast
        {
            internal string Message;
            internal ZepToastKind Kind;
        }

        // A real ZepController behind a real overlay controller, with the
        // destructive confirmation answered by the test instead of by a modal.
        private sealed class TagSettingsHarness : IDisposable
        {
            internal ZepUiControllerRig Rig;
            internal ZepOverlayPageController Overlay;
            internal readonly List<RecordedPrompt> Prompts = new List<RecordedPrompt>();
            internal readonly List<RecordedToast> Toasts = new List<RecordedToast>();

            internal static TagSettingsHarness Build(bool confirmAnswer, bool withCommandHandler = true)
            {
                var harness = new TagSettingsHarness();
                harness.Rig = ZepUiControllerRig.Build(
                    new[] { new AssetRecord("icon-guid", IconPath, "Icon", "Texture2D") },
                    new[] { "Assets", "Packages" },
                    new[]
                    {
                        new TagDefinitionRecord
                        {
                            Id = "red",
                            Name = "Red",
                            Color = ZepTagColor.Red,
                            IsFavorite = true
                        },
                        new TagDefinitionRecord
                        {
                            Id = "blue",
                            Name = "Blue",
                            Color = ZepTagColor.Blue,
                            IsFavorite = true
                        }
                    });
                harness.Rig.Gateway.SeedTagIds(IconPath, "red");

                var host = new ZepShellView(new ZepState());
                var registry = new ZepThemeRegistry();
                harness.Overlay = new ZepOverlayPageController(
                    withCommandHandler ? harness.Rig.Controller : null,
                    null,
                    registry,
                    new ZepThemeSwitcher(host, registry, null, null),
                    showToast: (message, kind) => harness.Toasts.Add(
                        new RecordedToast { Message = message, Kind = kind }),
                    onCreateTagRequested: () => { },
                    onActiveChanged: () => { },
                    themeCreatorHost: host,
                    askConfirmDestructive: (title, message, confirmLabel) =>
                    {
                        harness.Prompts.Add(new RecordedPrompt
                        {
                            Title = title,
                            Message = message,
                            ConfirmLabel = confirmLabel
                        });
                        return confirmAnswer;
                    });
                return harness;
            }

            // The drawer's own screen, seeded with the stored tags and attached: the
            // row subscribes to the model on attach, and its commit events need a
            // panel to dispatch through.
            internal ZepSettingsScreen MountSettingsScreen(EditorWindow window)
            {
                Overlay.PushTagsState();
                window.position = new Rect(100f, 100f, 700f, 500f);
                window.Show();
                window.rootVisualElement.Add(Overlay.SettingsScreen);
                Assert.That(
                    Overlay.SettingsScreen.panel,
                    Is.Not.Null,
                    "the screen must be attached for its row events to dispatch");
                return Overlay.SettingsScreen;
            }

            // The handlers are the settings screen's event targets; a test cannot
            // raise those events from outside the screen, so it calls the handler
            // the screen would call — the same reflection route the shell tests
            // use for ContextMenuActions.
            internal void Invoke(string handlerName, params object[] arguments)
            {
                var method = typeof(ZepOverlayPageController).GetMethod(
                    handlerName,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Assert.That(method, Is.Not.Null, $"Expected handler '{handlerName}' to exist.");
                try
                {
                    method.Invoke(Overlay, arguments);
                }
                catch (TargetInvocationException exception)
                {
                    throw exception.InnerException ?? exception;
                }
            }

            internal string[] StoredTagIds() =>
                Rig.TagDefinitions.GetOrderedTags().Select(tag => tag.Id).ToArray();

            internal string[] StoredTagNames() =>
                Rig.TagDefinitions.GetOrderedTags().Select(tag => tag.Name).ToArray();

            // Id, name, colour and favourite together: a reset that ran would move
            // at least one of them, and comparing only ids would miss it.
            internal string[] StoredTagSummaries() =>
                Rig.TagDefinitions.GetOrderedTags()
                    .Select(tag => $"{tag.Id}|{tag.Name}|{tag.Color}|{tag.IsFavorite}")
                    .ToArray();

            internal string[] ModelTagIds() =>
                Overlay.SettingsScreenModel.Tags.Select(tag => tag.Id).ToArray();

            internal string[] ModelTagNames() =>
                Overlay.SettingsScreenModel.Tags.Select(tag => tag.Name).ToArray();

            public void Dispose()
            {
                Rig?.Dispose();
                Rig = null;
            }
        }
    }
}
