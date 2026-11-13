# Core and the Unity part

The package is built from two parts that depend on Unity very differently.

| | Where | What it is |
| --- | --- | --- |
| **Business logic** | `Editor/Core` | Plain .NET with no Unity reference. Its tests run without an editor. |
| **Unity part** | `Editor/Bridge`, `Editor/UI`, `Editor/InternalBridge` | Everything that talks to the editor. |

Both ship as C# source and are compiled by the user's editor.

`Build~/` is the build and release tooling. Unity ignores folders ending in `~`, so nothing here is imported.

## Day-to-day development

Open a Unity project that references this folder as a `file:` package. Unity compiles `Editor/Core` from source through its asmdef, so a Core edit recompiles like any other script and the EditMode tests run against it.

Core is plain .NET, so most of its tests also run without Unity, in a couple of seconds:

```bash
dotnet test Build~/Core.Tests
```

That project compiles `Editor/Core` together with everything under `Tests/Editor/Core`. The same test files also run in the editor's Test Runner, which is the Mono runtime the package actually runs on.

Where a test goes:

- `Tests/Editor/Core` — tests of Core alone. Its asmdef references Core and NUnit only (`noEngineReferences`), so a test that reaches for Unity, Bridge or UI does not compile. If a test needs a real `AssetDatabase`, a Bridge class or a widget, it is not a Core test.
- `Tests/Editor/Bridge`, `Tests/Editor/UI` — everything that needs the editor. These run in the Test Runner only.

`Editor/Core/AssemblyInfo.cs` lets the two test assemblies see Core internals.

## The boundary rule

Core grants internals access to the test assemblies only. Bridge and UI see exactly Core's `public` API, and the compiler enforces it.

So when adding to Core, default to `internal`. Make something `public` only when Bridge or UI must call it. If Bridge or UI reaches for a Core internal, the editor reports `CS0122`; either make that member public on purpose or move the calling logic into Core.

Core must also stay free of Unity: the asmdef has `noEngineReferences`, and `Build~/Core` builds it as plain `netstandard2.1`.

When a Core class needs a fact only the editor has (the per-frame tick, the console, `SessionState`, an `AssetDatabase` query), it declares a small host interface next to itself and Bridge implements and binds it once per domain. The existing ones are `PersistentAssetIndexCache.BindUnity`, `AssetIndexSynchronizer.BindUnity`, `AssetRecordSourceHost.Bind`, `UndoPayloadStore.BindUnity` and `ExternalAssetImporter.BindUnity`; a class constructed by Bridge or UI can simply take what it needs in its constructor, as `TagStateJournal`, `FolderTreeModel` and `FolderMoveDrop` do. Using a bound class before Bridge has bound it throws.

## Making a release

```bash
Build~/release.sh
```

It needs the .NET SDK and at least one installed Unity 6 editor, and does this:

1. Runs the headless Core tests.
2. Builds `Editor/Core` as plain `netstandard2.1`, C# 9 (`Build~/Core/*.csproj`). The DLL is not shipped; it is the reference for step 4.
3. Assembles `Build~/out/com.van-unity.zep/`: everything under `Editor`, `package.json`, README, changelog, license, third-party notices and `Documentation~/overview.md`. Tests, contributor documents and the rest of `Documentation~` are not copied. The top level is an allow-list, so a new root file is not published until it is added to the script.
4. Compiles the Bridge and UI sources against Core for every installed Unity 6 editor, with that editor's assemblies and version defines. This catches a version-gated API mistake without opening each editor.
5. Writes the tarball.

Output in `Build~/out/` (git-ignored):

- `com.van-unity.zep/` — the package folder.
- `com.van-unity.zep-<version>.tgz` — the same as a UPM tarball.

To try the release build in the editor, point a test project's `Packages/manifest.json` at the output folder:

```json
"com.van-unity.zep": "file:/absolute/path/to/Build~/out/com.van-unity.zep"
```
