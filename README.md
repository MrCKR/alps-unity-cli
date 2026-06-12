# alps-unity-cli

Windows-first npm CLI for Alps Unity automation. The current scope follows the frozen ALP-22 direction: independent CLI implementation, Windows-only runtime target, npm-first installation, and fully open capabilities without default risk gates or secondary confirmation prompts.

## Requirements

- Windows shell environment
- Node.js 20 or newer
- npm global install support

## Install and Build

```powershell
npm install -g alps-unity-cli
alps-unity --help
```

For local development:

```bash
npm install
npm run build
npm test
```

## Job Model

Long-running Unity batchmode operations create durable jobs under `%LOCALAPPDATA%\\AlpsUnityCli` on Windows. Every command emits stable JSON with `ok`, `requestId`, `data` or `error`. Package manifest operations also use the same envelope and can return either a terminal manifest update result or a Unity resolve job.

Job statuses:

- `queued`
- `running`
- `succeeded`
- `failed`
- `cancelled`
- `timeout`

## Commands

```powershell
alps-unity camera list --bridge-endpoint http://127.0.0.1:47392
alps-unity camera info "Main Camera" --bridge-endpoint http://127.0.0.1:47392
alps-unity camera screenshot "Main Camera" --output Artifacts\main-camera.png --width 1920 --height 1080 --format png --transparent --overwrite --bridge-endpoint http://127.0.0.1:47392
alps-unity scene screenshot Artifacts\scene-view.jpg --width 1280 --height 720 --format jpg --quality 90 --overwrite --bridge-endpoint http://127.0.0.1:47392
alps-unity screenshot capture Artifacts\after.png --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab info Assets\Prefabs\Player.prefab --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab create Player --path Assets\Prefabs\Player.prefab --overwrite --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab stage-open Assets\Prefabs\Player.prefab --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab hierarchy Assets\Prefabs\Player.prefab --page-size 100 --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab stage-save --bridge-endpoint http://127.0.0.1:47392
alps-unity prefab stage-close --save --bridge-endpoint http://127.0.0.1:47392
alps-unity ui canvas/create HUD --bridge-endpoint http://127.0.0.1:47392
alps-unity ui text/create Title --parent HUD --text "Start" --width 320 --height 80 --bridge-endpoint http://127.0.0.1:47392
alps-unity ui button/create PlayButton --parent HUD --label "Play" --bridge-endpoint http://127.0.0.1:47392
alps-unity ui image/create Logo --parent HUD --sprite Assets\UI\logo.png --bridge-endpoint http://127.0.0.1:47392
alps-unity ui document/create --name MainUIDocument --uxml Assets\UI\Main.uxml --uss Assets\UI\Main.uss --panel-settings Assets\UI\Panel.asset --overwrite --bridge-endpoint http://127.0.0.1:47392
alps-unity ui document/info MainUIDocument --bridge-endpoint http://127.0.0.1:47392
alps-unity ui document/info Assets\UI\Main.uxml
alps-unity ui hierarchy HUD --page-size 100 --bridge-endpoint http://127.0.0.1:47392
alps-unity package list --project C:\path\UnityProject
alps-unity package info com.unity.textmeshpro --project C:\path\UnityProject
alps-unity package search textmeshpro --registry https://packages.unity.com
alps-unity package install com.acme.tool@1.2.3 --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
alps-unity package remove com.acme.tool --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
alps-unity package resolve --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe" --wait
alps-unity package registry get --project C:\path\UnityProject
alps-unity package registry set --url https://packages.unity.com --project C:\path\UnityProject
alps-unity package scoped-registry add --name Acme --url https://registry.acme.test --scope com.acme --project C:\path\UnityProject
alps-unity package scoped-registry list --project C:\path\UnityProject
alps-unity editor refresh --compile --wait --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe"
alps-unity test run --platform EditMode --wait --project C:\path\UnityProject --unity "C:\Program Files\Unity\Hub\Editor\2022.3.45f1\Editor\Unity.exe"
alps-unity test poll --id <jobId>
alps-unity build run --target StandaloneWindows64 --output Builds\Windows\Game.exe --wait
alps-unity build status --id <jobId>
alps-unity build cancel --id <jobId>
alps-unity job status --id <jobId>
alps-unity job logs --id <jobId> --tail 100
alps-unity job cancel --id <jobId>
```

Use `--wait-timeout-ms <ms>` to limit synchronous waiting. Use `--timeout-ms <ms>` to time out the Unity batchmode process itself.

`camera list` returns Camera summaries including hierarchy path, tag, enabled state, `targetTexture`, clear flags, projection, FOV/orthographic size and pixel dimensions. `camera info <target>` resolves by Camera name, hierarchy path or instance id. `camera screenshot <target>` and `scene screenshot` write PNG/JPG artifacts from the live Unity Editor Bridge and return `screenshotPath`, `artifactPath`, `format`, `width`, `height`, `requestId` and `logPath` in the shared envelope. Relative output paths are resolved under the Unity project root, output directories are created automatically, and existing files require `--overwrite`. Structured errors include `E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved, `E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved, `E_SCENE_VIEW_NOT_AVAILABLE`, `E_OUTPUT_EXISTS`, `E_OUTPUT_PATH_REQUIRED`, `E_SCREENSHOT_FORMAT_UNSUPPORTED`, `E_IO_ERROR`, `E_UNITY_BUSY` and bridge connection diagnostics.

`prefab info <path>` returns Prefab metadata including GUID, root name, asset type, `variant` base/source paths and GUIDs, `dependencies.items`, and `nestedPrefabs.items` rather than counts only. `prefab create <source> --path <path>` creates a Prefab from a scene GameObject target or copies an existing Prefab asset and returns `plannedEffects`. `prefab stage-open`, `prefab stage-save` and `prefab stage-close --save` manage Prefab Stage lifecycle through the live Unity Editor Bridge. `prefab hierarchy <path>` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant/dependency summary, `nestedPrefabs.items` and `logPath` to avoid blocking large Prefabs. Prefab paths must be `Assets/*.prefab`. Structured errors include `E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`.

`ui canvas/create`, `ui text/create`, `ui button/create` and `ui image/create` create uGUI objects through the live Unity Editor Bridge. They return `created`, `rectTransform`, `plannedEffects`, `requestId` and `logPath`; text/button/image commands accept parent/canvas targets, dimensions, anchored position, text/label, color and sprite asset options. `ui document/create` creates or overwrites `.uxml`/`.uss` assets, links them to a `UIDocument`, optionally assigns `PanelSettings`, and returns resource references. `ui document/info` supports live UIDocument inspection and offline `.uxml`/`.uss` file reads without a Bridge endpoint for basic file diagnostics. `ui hierarchy` pages Canvas, RectTransform and UIDocument summaries with `pageInfo`. Ambiguous UI parent, UIDocument and hierarchy targets return `E_TARGET_AMBIGUOUS` with `matchCount` and candidate summaries so callers can retry with hierarchy path or instance id. UI structured errors include `E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`.

`package list` and `package info` read `Packages/manifest.json` and `Packages/packages-lock.json`. `package install`, `package remove`, `package registry` and `package scoped-registry` update `manifest.json` directly. When `--unity` or `UNITY_EXE` is available, `package install/remove/resolve` create a `package` job that invokes Unity batchmode with `AlpsUnityCli.Batch.ResolvePackages`; otherwise install/remove return a terminal `manifest_updated` result with a `nextActions` hint to run Unity resolve.

## Unity Editor Bridge

`package install/remove/resolve` deploys the bundled `com.alps.unity-cli.bridge` package into the target Unity project. When the Editor compiles that package, `AlpsUnityBridge` starts a Windows loopback HTTP listener on `http://127.0.0.1:47392` and serves `POST /bridge/execute` for the currently bundled live Bridge commands only: `camera.list`, `camera.info`, `camera.screenshot`, `scene.screenshot`, `screenshot.capture`, `prefab.info`, `prefab.create`, `prefab.stage-open`, `prefab.stage-save`, `prefab.stage-close`, `prefab.hierarchy`, `ui.canvas.create`, `ui.text.create`, `ui.button.create`, `ui.image.create`, `ui.document.info`, `ui.document.create` and `ui.hierarchy`. Other CLI query/edit commands such as `scene hierarchy`, `go find`, `component set` and `script edit` are not routed by this package yet and return `E_BRIDGE_COMMAND_UNSUPPORTED` if sent to the bundled Bridge endpoint. The bridge executes Unity APIs on the Editor main thread and writes diagnostics to `<project>\Logs\AlpsUnityBridge.log`. If a request waits 60 seconds before Unity starts it, the bridge cancels that queued work before execution and returns `E_UNITY_BUSY` with no side effects applied. If a mutating request already started before the HTTP timeout, the bridge returns a success envelope with `status: "running"`, `sideEffectSemantics: "started-before-http-timeout"`, the original `requestId` and `logPath` so callers do not treat a possible in-flight side effect as a failed/no-op request.

## Batch Automation

`batch run <file.json>` composes registered Alps Unity CLI first-class commands only. It does not provide `raw`, raw MCP pass-through, arbitrary tool calls, or a capability-gap escape hatch.

```powershell
alps-unity batch schema
alps-unity batch run .\automation\camera-test-screenshot.json --preflight
alps-unity batch run .\automation\camera-test-screenshot.json --fail-fast true --parallel 1
```

Example `camera-test-screenshot.json`:

```json
{
  "defaults": {
    "project": "C:/work/Game",
    "unity": "C:/Program Files/Unity/Hub/Editor/2022.3.45f1/Editor/Unity.exe",
    "wait": true,
    "timeoutMs": 600000,
    "waitTimeoutMs": 600000,
    "env": {
      "ALPS_UNITY_BRIDGE_ENDPOINT": "http://127.0.0.1:18080"
    }
  },
  "envAllowlist": ["ALPS_UNITY_BRIDGE_ENDPOINT"],
  "failFast": true,
  "parallel": 1,
  "steps": [
    {
      "id": "list-cameras",
      "command": ["camera", "list"]
    },
    {
      "id": "inspect-main-camera",
      "command": ["camera", "info", "Main Camera"],
      "dependsOn": ["list-cameras"]
    },
    {
      "id": "run-editmode-tests",
      "command": ["test", "run"],
      "args": ["--platform", "EditMode"],
      "dependsOn": ["inspect-main-camera"]
    },
    {
      "id": "capture-after-test",
      "command": ["camera", "screenshot", "Main Camera"],
      "args": ["--output", "Artifacts/after-test.png", "--overwrite"],
      "dependsOn": ["run-editmode-tests"]
    }
  ]
}
```

Batch output includes a batch requestId, each step requestId/envelope, summary counts, aggregated `plannedEffects`, and a `logIndex` that links batch record path, step requestIds, jobIds, and Unity log paths.

`--parallel` is intentionally restricted to commands declared parallel-safe. Mutating/artifact commands such as `screenshot capture`, `camera screenshot`, `scene screenshot`, `prefab create`, `prefab stage-open`, `prefab stage-save`, `prefab stage-close`, `ui canvas/create`, `ui text/create`, `ui button/create`, `ui image/create`, `ui document/create`, `editor refresh`, `test run`, and `build run` must run serially; preflight returns a structured `BATCH_PARALLEL_CONFLICT` if a batch requests unsafe parallelism.

## Windows logs and diagnostics

The default diagnostic data and log directory is:

```text
%LOCALAPPDATA%\AlpsUnityCli
```

`requestId`, `stepRequestId`, `jobId`, batch record paths and log paths are observability fields only. They do not enable risk controls, confirmation prompts, or high-risk command blocking.

## Windows + Unity Editor Follow-up Verification

Automated tests use fake Unity and injected bridge executors to verify CLI behavior without requiring Unity. TypeScript tests cover CLI protocol forwarding, offline UI Toolkit file reads and failure-envelope preservation for Bridge responses; `src/camera-bridge-source.test.ts` is a source-level guard for the bundled C# Bridge implementation. These tests do not instantiate Unity Editor, so C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG/JPG artifacts, Prefab Stage open/save/close, Prefab dirty conflicts, nested/variant Prefab summaries, uGUI object creation, UIDocument/PanelSettings linking and UI hierarchy summaries must still be proven by real Windows + Unity Editor validation before release.

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- Unity Bridge endpoint discovery and live camera/scene/screenshot execution against an open Editor.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
- Unity PackageManager resolve hook `AlpsUnityCli.Batch.ResolvePackages` against a real Unity project after manifest/scoped registry changes.
- Registry reachability and credential behavior for private scoped registries.

Advanced or custom extension commands should enter a later backlog as registered first-class commands. They are not implemented through raw MCP pass-through in this batch.
