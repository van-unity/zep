# Contributing

Thanks for taking the time to help. This page covers how to report a problem, how to set the package up for development, and what a pull request needs before it can be merged.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Security problems go through [SECURITY.md](SECURITY.md), not the public issue tracker.

## Reporting a bug or asking for a feature

Use the [issue forms](https://github.com/van-unity/zep/issues/new/choose). For a bug, the things that make it fixable are:

- the package commit SHA (from your project's `Packages/packages-lock.json`) or version;
- the Unity version and operating system;
- exact steps, what you expected and what happened;
- the Console output or stack trace.

The panel moves, renames and deletes real files. If a bug touched assets or `.meta` files, say so up front.

## Development setup

You need Unity 6.0 or newer and, for the headless tests, the .NET 8 SDK.

1. Fork and clone this repository.
2. Reference the clone from a Unity project's `Packages/manifest.json` as a local package, and list it under `testables` so its tests appear in the Test Runner:

   ```json
   {
     "dependencies": {
       "com.van-unity.zep": "file:/absolute/path/to/com.van-unity.zep"
     },
     "testables": [
       "com.van-unity.zep"
     ]
   }
   ```

3. Open the panel from **Window > Zep**.

Use a throwaway or source-controlled Unity project. File-operation bugs under development can damage assets.

## How the code is laid out

| Folder | What it holds |
| --- | --- |
| `Editor/Core` | Business logic as plain .NET. No Unity reference; the compiler enforces it. |
| `Editor/Bridge` | Everything that talks to the Unity editor on Core's behalf (AssetDatabase, Undo, settings). |
| `Editor/UI` | The UI Toolkit window, widgets and stylesheets. |
| `Editor/InternalBridge` | A small assembly that reaches Unity-internal APIs on Unity 6.3+. |
| `Tests/Editor` | Tests, split the same way: `Core`, `Bridge`, `UI`. |
| `Build~` | Headless test project and release script. Unity ignores folders ending in `~`. |

Read these before a non-trivial change:

- [Build~/README.md](Build~/README.md): the Core/Unity boundary rule and where a test belongs.
- [coding-standards.md](coding-standards.md): how code is structured here. The short version is to make the smallest change in the module that owns the behavior, and not to add layers, interfaces or dependencies that nothing needs yet.
- [styling-standards.md](styling-standards.md): required reading before touching any `.uss` file or inline style.
- [PROJECT_GUIDANCE.md](PROJECT_GUIDANCE.md): deliberate design decisions that look like bugs but are not.

## Tests

There are two suites, and a change should leave both passing.

**Core tests** run without Unity in a couple of seconds:

```bash
dotnet test Build~/Core.Tests
```

**EditMode tests** run in Unity: **Window > General > Test Runner > EditMode**. They cover Bridge and UI, and run the Core tests again on Unity's Mono runtime.

What we expect from tests:

- A behavior change comes with a test that fails without it.
- Tests assert observable behavior, not which internal methods were called.
- Use real temporary files at the filesystem boundary; do not add an interface only so something can be mocked.
- A test that needs `AssetDatabase`, a Bridge class or a widget belongs in `Tests/Editor/Bridge` or `Tests/Editor/UI`, not in `Tests/Editor/Core`.

A few EditMode tests depend on editor focus and report as inconclusive in batch mode. The [README](README.md#verification-status) lists the suite's known state.

### Manual smoke test

Automated tests do not replace using the panel on real assets. After a change to browsing, file operations or window behavior, run the part of this list it could affect, in a source-controlled project:

1. Browse large `Assets` and `Packages` trees, then search and change type and tag filters.
2. Create, rename, duplicate, move and delete files and folders, then Undo and Redo, and check that their `.meta` files stay intact.
3. Drag files and directories from the operating system into the panel.
4. Open prefabs, scenes, scripts, materials, textures, audio, models and folders through Quick Look and the normal open actions.
5. Restart Unity and confirm that navigation, tags, themes and indexed search recover.
6. Dock, undock, maximize, resize and reopen the panel with both built-in and custom themes.
7. Open folder and tag splits; check active-pane commands, rooted navigation, divider resizing by mouse and keyboard, replacement at the one-split limit, and restoration after a script reload.

The full checklist, with case ids per area, is [Documentation~/Manual-QA-Test-Plan.md](Documentation~/Manual-QA-Test-Plan.md).

## Pull requests

- Keep a pull request to one change. Unrelated cleanup makes review slower and regressions harder to trace.
- Describe the behavior that changes and how you verified it, including the Unity version you ran.
- Add an entry under `[Unreleased]` in [CHANGELOG.md](CHANGELOG.md) for anything a user can notice.
- Do not hand-write or edit `.meta` files. Let the Unity editor create them, and commit the `.meta` next to every new file or folder under `Editor/` and `Tests/` and at the package root.
- UI changes should work in every built-in theme. Colors come from theme tokens, never literals.
- The package supports Unity 6.0 through 6.5. API differences between those versions are handled with `UNITY_6000_x_OR_NEWER` guards; `Build~/release.sh` compiles the sources against every Unity 6 editor installed on your machine if you want to check before pushing.

Continuous integration runs the Core tests on every pull request. The Unity EditMode workflow needs license secrets that pull requests from forks do not receive, so a maintainer runs that suite before merging.

## Releasing (maintainers)

1. Move the `[Unreleased]` entries in `CHANGELOG.md` under a new version heading and set the same version in `package.json`.
2. Run `Build~/release.sh`. It runs the Core tests, compiles the package for each installed Unity 6 editor and writes the UPM tarball to `Build~/out/`.
3. Run the EditMode suite on the oldest and newest supported Unity versions.
4. Commit, tag the commit `v<version>`, push the tag, and create a GitHub release with the changelog section and the tarball.

## License

Contributions are accepted under the project's [MIT License](LICENSE.md). By opening a pull request you confirm that you have the right to submit the work under that license. Third-party material needs a compatible license and an entry in [Third Party Notices.md](Third%20Party%20Notices.md).
