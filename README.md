# PlayLens AI Unity source package

Read [AI_USAGE.md](AI_USAGE.md) before integrating or generating integration code.
This is telemetry, not authentication or account management.

## Install

In Unity Package Manager, choose **Install package from Git URL** and enter:

```text
https://github.com/sebas77/PlayLensAI-Unity.git
```

Pin a published commit with `#<commit-sha>` for reproducible builds. Add
`GameAnalytics.Client` to your consuming assembly definition's `references`.
Remove any older `GameAnalytics.Client.dll` and its `precompiledReferences` entry;
do not load both implementations.

## Compatibility and ownership

- Package ID: `com.playlensai.sdk`; assembly and namespace: `GameAnalytics.Client`.
- Targets Unity 6000.6 with .NET Standard 2.1 and Unity's supplied
  `System.Text.Json` BCL assemblies. No additional JSON package is bundled.
- `Runtime/` is the canonical engine-independent SDK source, also compiled by the
  standalone .NET Standard 2.0 project in AIAnalytics. There is no Unity component,
  player-loop hook, authentication adapter or automatic consent integration.
- The host owns configuration, consent, persistent installation identity,
  account UUIDs, startup buffering, runtime flush opportunities and `HttpClient`.
- Do not block game entry on analytics initialization or delivery. Keep other
  analytics providers independent of PlayLens readiness or failure.
- Editor compilation does not establish IL2CPP, stripping, WebGL or device support;
  validate the intended player target before shipping.

## Maintaining the shared source

In AIAnalytics, initialize the `csharp-sdk/Unity` submodule before building the
standalone SDK (`git submodule update --init csharp-sdk/Unity`). Edit this source,
not a copied DLL. For local Unity development, install this folder through a
relative `file:` package dependency; let Unity generate metadata and commit each
asset with its `.meta`. Publish the package commit before changing consumers back
to its pinned Git URL. Keep standalone build outputs, samples and tests outside
this package.
