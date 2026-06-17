# Tool Execution

> **Discovery path**: `tools describe → tools run → verify`

## The Unified Execution Entry Point

`tools run <name>` is the unified entry point for tool execution. It replaces all domain-specific invocation patterns — you do not need to memorize separate command groups. The router determines the execution channel based on the tool's `handler`, `target`, and `availabilityScope`.

```powershell
unity_cli tools run scene.hierarchy --target <alias>
unity_cli tools run sample.echo --target <alias>
unity_cli tools run camera.list --target <alias>
unity_cli tools run runtime.echo --target <alias>  # Runtime tool (requires Play Mode)
```

`tools run` accepts:
- `--input <json>` — Input parameters as JSON string
- `--input-file <path>` or `--stdin` — Input from file or stdin
- `--target <id|alias>` — Explicit target selection
- `--timeout-ms <ms>` — Override default timeout
- `--fingerprint-mode warn|block` — Fingerprint mismatch handling (project tools)
- `--yes` — Confirm destructive tools
- `--dry-run` — Preview effects (if `supportsDryRun: true`)

## Execution Channels

Every tool has a `handler` field in its descriptor that determines how it executes. The unified tools contract defines these execution channels:

### 1. `bridge` — Live Editor Bridge

The tool is dispatched as `POST /bridge/execute` to the Unity Editor's loopback HTTP server. The Bridge executes Unity APIs on the Editor main thread and returns results in the unified response envelope.

- **Requires**: a reachable Bridge endpoint (target instance must be `ready`)
- **Target**: `editor`
- **Examples**: `camera list`, `scene hierarchy`, `asset search`, `editor play`
- **Timeout**: default 60 seconds per Bridge request; `--timeout-ms` overrides
- **Side effects**: executed on the live Editor; mutations are real and immediate

### 2. `bridge-runtime` — Runtime Bridge

The tool is dispatched through the Bridge to the `RuntimeExecutor`, which runs the tool in the Unity Play Mode context. The tool is only available when the Editor is in Play Mode.

- **Requires**: a reachable Bridge endpoint AND Editor in Play Mode
- **Target**: `runtime`
- **Availability scope**: `playModeSession`
- **`available`**: `true` only when Play Mode is active
- **Examples**: runtime-domain tools registered by `[UnityCliTool]` with `target = "runtime"`
- **Error when not in Play Mode**: `E_RUNTIME_NOT_READY` (transient; retry after entering Play Mode)

### 3. `hybrid` — Bridge + Job Path

The tool supports both live Bridge execution and a batch/job fallback. The caller chooses the channel:

- **Live channel**: `--channel live` or default (when Bridge is reachable)
- **Job channel**: `--job`, `--channel batchmode`, or when `--project/--unity/--wait` are provided
- **Target**: `editor` or `both`
- **Examples**: `code exec`, `profiler capture`, `package install/remove/resolve`
- **Job lifecycle**: returns `jobId`; poll with `job status/logs`; cancel with `job cancel`

### 4. `job` — Batch/Job Only

The tool runs through Unity batchmode as a durable job. No live Bridge is needed at invocation time; the CLI creates a process that runs Unity in batchmode.

- **Requires**: `--project` and `--unity` (or `UNITY_EXE` env)
- **Target**: `editor`
- **Examples**: `editor refresh`, `test run`, `build run`
- **Job lifecycle**: `queued → running → succeeded/failed/cancelled/timeout`
- **Cancellation**: `job cancel --id <jobId>` attempts process termination

### 5. `offline` — CLI-Only Local

The tool runs locally without any Unity connection. Reads local files, writes manifests, manages the job store.

- **Requires**: nothing (no Bridge, no Unity process)
- **Target**: none
- **Examples**: `package list/info/search`, `batch schema/run`, `docs get/search`, `reflect type/member/search`, `job status/logs/cancel`, `init --check`
- **Job commands**: `job status/logs/cancel` read from the local job store

## Target=editor vs runtime vs both

The P3 unified tools contract defines `target` as a descriptor field that controls internal routing, not as an external command group:

| Target | Meaning | Execution routing |
|--------|---------|-------------------|
| `editor` | Tool operates in the Editor domain (edit-mode APIs, AssetDatabase, scene hierarchy) | Dispatched through `editorBridge` — the live Bridge `/bridge/execute` path |
| `runtime` | Tool operates in the runtime domain (PlayMode APIs, runtime game state) | Dispatched through `runtimeExecutor` — requires Play Mode active |
| `both` | Tool can operate in either domain depending on context | Router checks `editorState` / `playModeState` to decide the execution branch; use `--runtime` flag to force runtime path |

### What this means for AI agents

- You **do not** call a separate `runtime` command group. Runtime behavior is expressed through:
  - The `target` field in the tool descriptor (`"editor"`, `"runtime"`, or `"both"`)
  - The `availabilityScope` field (`"editorSession"` or `"playModeSession"`)
  - The `available` field (`true`/`false` based on current session state)
  - The `handler` field (`bridge-runtime` for runtime tools)
- When `editor.state` returns `playMode: playing`, tools with `target: "runtime"` become available; when `playMode: stopped`, those tools return `E_RUNTIME_NOT_READY` (transient).
- `code exec --channel live` executes in the current Editor context regardless of PlayMode state; `code exec --channel batchmode` runs in a headless Unity batchmode process.

## Verification After Execution

After executing a tool, verify the result using the appropriate strategy:

### For read-only tools

```powershell
# Verify camera list
unity_cli tools run camera.list --target <alias>

# Verify scene hierarchy
unity_cli tools run scene.hierarchy --target <alias>
```

Re-call the same read tool and compare the output with expectations. See `examples/05-verify-workflow.md` for the full discovery → run → verify cycle.

### For mutating tools with visual output

```powershell
# After creating a material, verify it exists
unity_cli tools run material.info --input '{"path":"Assets/Materials/New.mat"}' --target <alias>

# After scene changes, capture screenshot
unity_cli tools run camera.screenshot --input '{"target":"Main Camera","output":"Artifacts/verify.png"}' --target <alias>
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
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.json"}' --dry-run --target <alias>
unity_cli tools run go.delete --input '{"target":"ObsoleteObject"}' --dry-run --target <alias>
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
- Examples (runnable): [/examples/](../examples/) — see `05-verify-workflow.md` for the full verify cycle
