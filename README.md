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

`package list` and `package info` read `Packages/manifest.json` and `Packages/packages-lock.json`. `package install`, `package remove`, `package registry` and `package scoped-registry` update `manifest.json` directly. When `--unity` or `UNITY_EXE` is available, `package install/remove/resolve` create a `package` job that invokes Unity batchmode with `AlpsUnityCli.Batch.ResolvePackages`; otherwise install/remove return a terminal `manifest_updated` result with a `nextActions` hint to run Unity resolve.

## Unity Editor Bridge

`package install/remove/resolve` deploys the bundled `com.alps.unity-cli.bridge` package into the target Unity project. When the Editor compiles that package, `AlpsUnityBridge` starts a Windows loopback HTTP listener on `http://127.0.0.1:47392` and serves `POST /bridge/execute` for the currently bundled live Bridge commands only: `camera.list`, `camera.info`, `camera.screenshot`, `scene.screenshot` and `screenshot.capture`. Other CLI query/edit commands such as `scene hierarchy`, `go find`, `component set` and `script edit` are not routed by this package yet and return `E_BRIDGE_COMMAND_UNSUPPORTED` if sent to the bundled Bridge endpoint. The bridge executes Unity APIs on the Editor main thread and writes diagnostics to `<project>\Logs\AlpsUnityBridge.log`.

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

`--parallel` is intentionally restricted to commands declared parallel-safe. Mutating/artifact commands such as `screenshot capture`, `camera screenshot`, `scene screenshot`, `editor refresh`, `test run`, and `build run` must run serially; preflight returns a structured `BATCH_PARALLEL_CONFLICT` if a batch requests unsafe parallelism.

## Windows logs and diagnostics

The default diagnostic data and log directory is:

```text
%LOCALAPPDATA%\AlpsUnityCli
```

`requestId`, `stepRequestId`, `jobId`, batch record paths and log paths are observability fields only. They do not enable risk controls, confirmation prompts, or high-risk command blocking.

## Windows + Unity Editor Follow-up Verification

Automated tests use fake Unity and injected bridge executors to verify CLI behavior without requiring Unity. TypeScript tests cover CLI protocol forwarding and failure-envelope preservation for Bridge responses; `src/camera-bridge-source.test.ts` is a source-level guard for the bundled C# Bridge implementation. These tests do not instantiate Unity Editor, so C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures and actual PNG/JPG artifacts must still be proven by real Windows + Unity Editor validation before release.

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- Unity Bridge endpoint discovery and live camera/scene/screenshot execution against an open Editor.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
- Unity PackageManager resolve hook `AlpsUnityCli.Batch.ResolvePackages` against a real Unity project after manifest/scoped registry changes.
- Registry reachability and credential behavior for private scoped registries.

Advanced or custom extension commands should enter a later backlog as registered first-class commands. They are not implemented through raw MCP pass-through in this batch.
