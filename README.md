# Alps Unity CLI

Windows-first npm CLI for Unity automation. The current scope follows the frozen ALP-22 direction: independent CLI implementation, Windows-only runtime target, npm-first installation, and fully open capabilities without default risk gates or secondary confirmation prompts.

## Install and Build

```powershell
npm install -g @alps/unity-cli
alps-unity --help
```

For local development:

```bash
npm install
npm run build
npm test
```

## Job Model

Long-running Unity batchmode operations create durable jobs under `%LOCALAPPDATA%\\AlpsUnityCli` on Windows. Every command emits stable JSON with `ok`, `requestId`, `data` or `error`.

Job statuses:

- `queued`
- `running`
- `succeeded`
- `failed`
- `cancelled`
- `timeout`

## Commands

```powershell
alps-unity scene hierarchy --page-size 100 --bridge-endpoint http://127.0.0.1:18080
alps-unity go find --name Player --bridge-endpoint http://127.0.0.1:18080
alps-unity component set Player --component Camera --property enabled --value false --bridge-endpoint http://127.0.0.1:18080
alps-unity script edit Assets/Scripts/Player.cs --find "speed = 1" --replace "speed = 2" --bridge-endpoint http://127.0.0.1:18080
alps-unity screenshot capture Artifacts\after.png --bridge-endpoint http://127.0.0.1:18080
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

## Batch Automation

`batch run <file.json>` composes registered Alps Unity CLI first-class commands only. It does not provide `raw`, raw MCP pass-through, arbitrary tool calls, or a capability-gap escape hatch.

```powershell
alps-unity batch schema
alps-unity batch run .\automation\query-edit-test-screenshot.json --preflight
alps-unity batch run .\automation\query-edit-test-screenshot.json --fail-fast true --parallel 1
```

Example `query-edit-test-screenshot.json`:

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
      "id": "query-player",
      "command": ["go", "find"],
      "args": ["--name", "Player"]
    },
    {
      "id": "edit-player",
      "command": ["component", "set", "Player"],
      "args": ["--component", "Camera", "--property", "enabled", "--value", "false"],
      "dependsOn": ["query-player"]
    },
    {
      "id": "run-editmode-tests",
      "command": ["test", "run"],
      "args": ["--platform", "EditMode"],
      "dependsOn": ["edit-player"]
    },
    {
      "id": "capture-after-edit",
      "command": ["screenshot", "capture", "Artifacts/after-edit.png"],
      "dependsOn": ["run-editmode-tests"]
    }
  ]
}
```

Batch output includes a batch requestId, each step requestId/envelope, summary counts, aggregated `plannedEffects`, and a `logIndex` that links batch record path, step requestIds, jobIds, and Unity log paths.

`--parallel` is intentionally restricted to commands declared parallel-safe. Mutating commands such as `component set`, `script edit`, `screenshot capture`, `editor refresh`, `test run`, and `build run` must run serially; preflight returns a structured `BATCH_PARALLEL_CONFLICT` if a batch requests unsafe parallelism.

## Windows + Unity Editor Follow-up Verification

Automated tests use fake Unity and injected bridge executors to verify CLI behavior without requiring Unity. Before release, these paths still need real Windows + Unity Editor validation:

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- Unity Bridge endpoint discovery and live query/edit/screenshot execution against an open Editor.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
