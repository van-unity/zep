# Folder-navigation parity benchmark

The folder-navigation success criterion is measured parity with Unity's
default Project window in the same editor process and project. The previous
absolute “under 16 ms” objective is retired: it allowed the package to pass
while remaining materially slower than Unity on the same fixture.

## Criterion

After 12 paired warm-ups, collect 51 alternating samples for two imported
sibling folders. Measure the synchronous folder-switch boundary through the
real mounted windows:

- Zep: `ZepPaneState.TrySetFolderSelection`;
- Unity Project: `ProjectBrowser.ShowFolderContents`.

Instrumentation must be disabled during the timed samples. The gate passes
when Zep's P95 is no more than **2.0 ms slower** than Unity's P95.
Both raw percentiles and the gap remain in the result; the absolute gap is the
gate because Unity's sub-millisecond boundary makes a ratio unstable and
misleading.

Use [`qa-harness/FolderNavigationParityProbe.cs`](qa-harness/FolderNavigationParityProbe.cs).
Copy it into `Assets/Editor` in the project being measured, choose two real
folders, and run the same installed Unity version that owns the project:

```sh
ZEP_NAV_FOLDER_A='Assets/Fixture/F00001' \
ZEP_NAV_FOLDER_B='Assets/Fixture/F00002' \
ZEP_NAV_RESULT='/tmp/zep-folder-navigation.tsv' \
/Applications/Unity/Hub/Editor/6000.3.10f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath "$PROJECT" \
  -executeMethod FolderNavigationParityProbe.Run \
  -logFile /tmp/zep-folder-navigation.log -forgetProjectPath
```

Do not run batch mode against a project already open in another Unity process.
The probe waits for both windows to initialize, alternates the panel and Unity
paths to reduce drift, writes a TSV, and exits nonzero when the parity gate
fails. This is a synchronous UI-publication comparison, not a first-pixel
paint benchmark; the same boundary is used for both windows.

## 2026-08-08 remediation evidence

Environment: Apple M3 Pro, macOS 26.5.1, Unity 6000.3.10f1, 128,753 imported
assets, 10,016 folders, 51 samples after 12 warm-ups. The paired folders were
`Assets/Phase01Fixture/F00001` and `F00002`.

| Revision | Zep P50 / P95 | Unity P50 / P95 | P95 gap | Gate |
| --- | ---: | ---: | ---: | --- |
| Before task 3 | 1.822 / 1.973 ms | 0.158 / 0.196 ms | 1.777 ms | PASS |
| After task 3 (final checked-in harness run) | 1.824 / 1.975 ms | 0.160 / 0.199 ms | 1.775 ms | PASS |

The profile behind the change showed one pane projection and asset-list
publication but two shell/sidebar refreshes per switch. The second refresh was
raised only to publish navigation history after the useful pane paint. The
history notification now updates navigation buttons directly, and an already
authoritatively indexed folder no longer repeats the bounded pre-hydration
filesystem observation. Pre-hydration first-paint observation remains intact.
