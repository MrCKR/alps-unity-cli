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

`package list` and `package info` read `Packages/manifest.json` and `Packages/packages-lock.json`. `package install`, `package remove`, `package registry` and `package scoped-registry` update `manifest.json` directly. When `--unity` or `UNITY_EXE` is available, `package install/remove/resolve` create a `package` job that invokes Unity batchmode with `AlpsUnityCli.Batch.ResolvePackages`; otherwise install/remove return a terminal `manifest_updated` result with a `nextActions` hint to run Unity resolve.

## Windows + Unity Editor Follow-up Verification

Automated tests use a fake Unity executable to verify CLI behavior without requiring Unity. Before release, these paths still need real Windows + Unity Editor validation:

- Unity Hub / `Unity.exe` discovery across installed Editor versions.
- `editor refresh --compile --wait` against a real project compilation cycle.
- EditMode and PlayMode result XML parsing against a real Unity project.
- Build output path creation and module-missing diagnostics for `StandaloneWindows64`.
- Process cancellation behavior for Unity batchmode under Windows shells and CI.
- Unity PackageManager resolve hook `AlpsUnityCli.Batch.ResolvePackages` against a real Unity project after manifest/scoped registry changes.
- Registry reachability and credential behavior for private scoped registries.
