# Tool Execution

> **Discovery path**: `tools describe → execute → verify`

## Execution Models

Every tool has a `handler` field in its descriptor that determines how it executes. The unified tools contract defines four execution channels:

### 1. `bridge` — Live Editor Bridge

The tool is dispatched as `POST /bridge/execute` to the Unity Editor's loopback HTTP server. The Bridge executes Unity APIs on the Editor main thread and returns results in the unified response envelope.

- **Requires**: a reachable Bridge endpoint (target instance must be `ready`)
- **Examples**: `camera list`, `scene hierarchy`, `asset search`, `editor play`
- **Timeout**: default 60 seconds per Bridge request; `--timeout-ms` overrides
- **Side effects**: executed on the live Editor; mutations are real and immediate

### 2. `hybrid` — Bridge + Job Path

The tool supports both live Bridge execution and a batch/job fallback. The caller chooses the channel:

- **Live channel**: `--channel live` or default (when Bridge is reachable)
- **Job channel**: `--job`, `--channel batchmode`, or when `--project/--unity/--wait` are provided
- **Examples**: `code exec`, `profiler capture`, `package install/remove/resolve`
- **Job lifecycle**: returns `jobId`; poll with `job status/logs`; cancel with `job cancel`

### 3. `job` — Batch/Job Only

The tool runs through Unity batchmode as a durable job. No live Bridge is needed at invocation time; the CLI creates a process that runs Unity in batchmode.

- **Requires**: `--project` and `--unity` (or `UNITY_EXE` env)
- **Examples**: `editor refresh`, `test run`, `build run`
- **Job lifecycle**: `queued → running → succeeded/failed/cancelled/timeout`
- **Cancellation**: `job cancel --id <jobId>` attempts process termination

### 4. `offline` — CLI-Only Local

The tool runs locally without any Unity connection. Reads local files, writes manifests, manages the job store.

- **Requires**: nothing (no Bridge, no Unity process)
- **Examples**: `package list/info/search`, `batch schema/run`, `docs get/search`, `reflect type/member/search`, `job status/logs/cancel`, `init --check`
- **Job commands**: `job status/logs/cancel` read from the local job store under `%LOCALAPPDATA%\UnityCli`

## Target=editor vs runtime vs both

The P3 unified tools contract defines `target` as an internal router concept, not an external command group:

| Target | Meaning | Execution routing |
|--------|---------|-------------------|
| `editor` | Tool operates in the Editor domain (edit-mode APIs, AssetDatabase, scene hierarchy) | Dispatched through `editorBridge` — the live Bridge `/bridge/execute` path |
| `runtime` | Tool operates in the runtime domain (PlayMode APIs, runtime game state) | Dispatched through `runtimeExecutor` — may require PlayMode active |
| `both` | Tool can operate in either domain depending on context | Router checks `editorState` / `playModeState` to decide the execution branch |

### What this means for AI agents

- You **do not** call a separate `runtime` command group. Runtime behavior is expressed through:
  - The `handler` and `supportStatus` fields in the tool descriptor
  - The `requiresPlayMode` / `editorState` context in the response
  - The `availabilityScope` field (global, instance, or playModeSession)
- When `editor.state` returns `playMode: playing`, tools that require PlayMode become available; when `playMode: stopped`, those tools are unavailable and the CLI returns appropriate error codes.
- `code exec --channel live` executes in the current Editor context regardless of PlayMode state; `code exec --channel batchmode` runs in a headless Unity batchmode process.

## Verification After Execution

After executing a tool, verify the result using the appropriate strategy:

### For read-only tools

```powershell
# Verify camera list
unity_cli camera list --target <alias>

# Verify scene hierarchy
unity_cli scene hierarchy --target <alias>

# Verify profiler counters
unity_cli profiler counter --target <alias>
```

Re-call the same read tool and compare the output with expectations.

### For mutating tools with visual output

```powershell
# After creating a material, verify it exists
unity_cli material info Assets/Materials/New.mat --target <alias>

# After modifying a GameObject, inspect it
unity_cli go inspect Player --target <alias>

# After scene changes, capture screenshot
unity_cli camera screenshot "Main Camera" --output Artifacts/verify.png --overwrite --target <alias>
```

### For job-based tools

```powershell
# After test run, check status
unity_cli job status --id <jobId>

# After build run, check logs
unity_cli build logs --id <jobId> --tail 50

# After package install, verify manifest
unity_cli package list --project <path>
```

### Using `plannedEffects` and `--dry-run`

Tools that mutate state often return a `plannedEffects` preview before execution. Tools with `supportsDryRun: true` accept `--dry-run` to preview effects without executing:

```powershell
# Dry-run preview for destructive commands
unity_cli asset delete Assets/Temp.json --dry-run --target <alias>
unity_cli go delete ObsoleteObject --dry-run --target <alias>
unity_cli code exec --code "DeleteAll()" --dry-run --target <alias>
unity_cli batch run automation.json --preflight
```

### Using diagnostics

```powershell
# Doctor checks overall health
unity_cli doctor

# Diagnose preflight validates batch files
unity_cli diagnose preflight batch.json --target <alias>
```

## Cross-Reference

- Discovery: [tool-discovery](./tool-discovery.md)
- Risk and side effects: [risk-and-side-effects](./risk-and-side-effects.md)
- Error recovery: [error-codes](./error-codes.md)
- Availability differences: [availability-differences](./availability-differences.md)
