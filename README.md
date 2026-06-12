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

## Windows + Unity Editor Follow-up Verification

Automated tests use a fake Unity executable to verify CLI behavior without requiring Unity. Before release, these paths still need real Windows + Unity Editor validation:

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
