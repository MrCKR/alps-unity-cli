---
name: unity_cli
description: Operate Unity Editor via the alps-unity-cli target-first discovery and unified tools workflow. Use when you need to discover, inspect, or control Unity instances, scenes, assets, or any Editor capability through the CLI's unified tool model.
---

# Unity CLI — AI Operator Skill

This skill teaches you how to operate the Unity Editor through `unity_cli` using the **target-first discovery** and **unified tools** workflow. The only execution entry point you need is `tools run <toolName>`. You do not need to memorize domain-specific command groups — the CLI discovers, describes, and runs everything through the unified `tools` interface.

## Core Workflow

Every interaction follows this loop:

```
instances list  →  target set  →  tools search  →  tools describe  →  tools run  →  verify
```

You always start from instances and target selection, then discover and execute tools. Never skip ahead.

---

## Step 1: Discover Available Instances

```bash
unity_cli instances list
```

This returns all registered Unity Editor instances with their status:

| Field | Meaning |
|-------|---------|
| `id` | Unique instance identifier |
| `alias` | Human-readable alias (if set) |
| `project` | Project name |
| `state` | `ready`, `stale`, or `unreachable` |
| `endpoint` | Bridge endpoint URL |

**If no instances are listed or all are stale/unreachable:**
- The Unity Editor is not running or the bridge is not connected.
- Suggest the user open the Unity project and ensure the UnityPkg bridge is active.
- Use `unity_cli instances prune` to clean up stale entries, then retry.

**If multiple ready instances exist**, proceed to Step 2 to select one.

## Step 2: Select a Target

```bash
# Set the active target by id or alias
unity_cli target set <id|alias>

# Or check the current active target
unity_cli target get
```

Target selection is required when multiple instances exist. Once set, all subsequent `tools` commands are scoped to that target.

**Convenience — use --target flag:**

```bash
unity_cli tools list --target <id|alias>
unity_cli tools describe scene.hierarchy --target <id|alias>
unity_cli tools run scene.hierarchy --target <id|alias>
```

**Alias management:**

```bash
unity_cli target alias set <instanceId> <alias>
unity_cli target alias list
unity_cli target alias unset <alias>
```

Use aliases for stable, memorable names instead of raw instance IDs.

## Step 3: Search for Tools

```bash
# Search by keyword across name, title, description, and group
unity_cli tools search <query>

# List all tools, optionally filtered by group
unity_cli tools list
unity_cli tools list --group scene

# List available tool groups
unity_cli tools groups
```

Each search result shows:

| Field | Meaning |
|-------|---------|
| `name` | Dotted tool identifier (e.g. `scene.hierarchy`) |
| `title` | Short human-readable title |
| `group` | Category (scene, camera, editor, etc.) |
| `supportStatusLabel` | Availability status |
| `readOnlyHint` | True = no side effects |
| `destructiveHint` | True = may irreversibly modify/delete |

**Tip:** Use `tools list --include-custom` to discover project-specific Editor tools registered by the connected Unity instance.

## Step 4: Describe a Tool Before Running

```bash
unity_cli tools describe <toolName>
```

**Always describe a tool before running it.** The response contains everything you need to construct the `tools run` invocation:

| Field | Purpose |
|-------|---------|
| `inputSchema` | JSON Schema for parameters — use this to build `--input-json` |
| `outputSchema` | JSON Schema for successful output |
| `target` | Whether this tool targets `editor`, `runtime`, or `both` |
| `execution` | Internal execution path (`editorBridge`, `runtimeExecutor`, `cliLocal`, `cliOrchestrator`) |
| `availability` | Current availability state for the resolved target |
| `requiresPlayMode` | Whether the tool requires Play Mode to be active |
| `readOnlyHint` | Whether the tool mutates state |
| `destructiveHint` | Whether changes are irreversible |
| `longRunning` | Whether to expect delays |
| `supportsDryRun` | Whether `--dry-run` is available |
| `risk` | Risk level, mutating/destructive flags, confirmation requirements |
| `examples` | Ready-to-use command examples |
| `nextActions` | Suggested follow-up actions after running |
| `validation` | Recommended verification checks and success signals |
| `docsTopic` | Topic key for `unity_cli docs topic <key>` for deeper docs |

**Key behavioral hints from describe:**

- **`destructiveHint: true`** or **`risk.requiresConfirmation: true`** → Confirm with the user before executing. Use `--dry-run` if available.
- **`requiresPlayMode: true`** → The tool needs Play Mode active. If the Editor is not in Play Mode, enter it first.
- **`availability.state: unavailable`** → The tool cannot run on this target in the current state. Check `nextActions` for guidance.
- **`longRunning: true`** → May take seconds or minutes. Poll job status with `unity_cli job status --id <jobId>`.
- **`target: runtime`** → This tool executes through the runtime executor in Play Mode. Still use `tools run` — the router handles the execution path internally.
- **`target: both`** → This tool can execute on either the editor or runtime path. The descriptor explains which path is taken.

**About `cliCommand`**: The descriptor may include a `cliCommand` field (e.g. `scene hierarchy`) for backward-compatibility or low-level debugging. **Do not use `cliCommand` as your primary execution path.** Always use `tools run <toolName>` instead.

## Step 5: Execute a Tool with `tools run`

```bash
unity_cli tools run <toolName> [--input-json '<json>'] [--target <id|alias>] [--timeout-ms <n>]
```

This is the **only** execution entry point you need. The invocation router internally selects the correct execution path (editor bridge, runtime executor, CLI local, or orchestrator) based on the tool's descriptor.

### Input Methods

Parameters come from `tools describe.inputSchema`. Pass them using one of:

```bash
# Inline JSON (most common)
unity_cli tools run camera.screenshot --input-json '{"target":"MainCamera","output":"screenshot.png","width":1280,"height":720}'

# From a file
unity_cli tools run code.exec --input-file params.json

# Via stdin
echo '{"code":"Debug.Log(\"hello\");"}' | unity_cli tools run code.exec --stdin

# No parameters needed
unity_cli tools run scene.hierarchy
unity_cli tools run editor.console
unity_cli tools run editor.state
```

### Target Selection

```bash
# Use the active target (set via target set)
unity_cli tools run scene.hierarchy

# Override target for this invocation
unity_cli tools run scene.hierarchy --target <id|alias>
```

### Destructive Tool Confirmation

For tools with `destructiveHint: true` or `risk.requiresConfirmation: true`:

```bash
# The CLI will prompt for confirmation before executing
unity_cli tools run asset.delete --input-json '{"path":"Assets/Old.asset"}'

# Use --dry-run to preview without executing (if supportsDryRun is true)
unity_cli tools run asset.delete --input-json '{"path":"Assets/Old.asset"}' --dry-run
```

Always confirm destructive operations with the user before running them.

### Runtime Tools

Runtime tools (`target: runtime` or `target: both`) also use `tools run` — no separate command:

```bash
# Runtime tool — the router handles Play Mode and session checks
unity_cli tools run <runtimeToolName> --input-json '<params>'
```

If `requiresPlayMode: true` and the Editor is not in Play Mode, `tools run` will return `E_PLAYMODE_NOT_READY` with `nextActions` telling you to enter Play Mode first. Enter Play Mode, then retry:

```bash
unity_cli tools run editor.play
unity_cli tools run <runtimeToolName> --input-json '<params>'
```

### Long-Running Tools and Job Polling

Tools that run as jobs return a `jobId`. Track progress:

```bash
unity_cli job status --id <jobId>
unity_cli job logs --id <jobId> --tail 50
unity_cli job cancel --id <jobId>
```

### Timeout Control

```bash
# Override default timeout
unity_cli tools run code.exec --input-json '{"code":"..."}' --timeout-ms 120000
```

## Step 6: Verify Results

After executing a tool, verify the outcome:

- **Read-only tools:** Check the response data directly.
- **State-changing tools:** Use a corresponding read-only tool to confirm.
  - After `tools run editor.play` → `tools run editor.state`
  - After `tools run scene.load` → `tools run scene.hierarchy`
  - After `tools run <package tool>` → `tools run cli.package.list`
- **Visual verification:** Use `tools run camera.screenshot` or `tools run screenshot.capture` to visually confirm scene changes.
- **Console check:** Use `tools run editor.console` to check for errors or warnings after mutations.
- **Compilation check:** After script changes, wait for compilation, then use `tools run editor.console` to check for errors.
- **Use `validation` from describe:** The `validation.recommendedChecks` and `validation.successSignals` fields in the descriptor tell you exactly what to verify.

---

## Common Patterns

### Starting a Session

```bash
unity_cli instances list
unity_cli target set <id|alias>
unity_cli tools search scene
unity_cli tools describe scene.hierarchy
unity_cli tools run scene.hierarchy
```

### Making a Scene Change and Verifying

```bash
unity_cli tools describe scene.load
unity_cli tools run scene.load --input-json '{"path":"Assets/Scenes/Main.unity"}'
unity_cli tools run scene.hierarchy
unity_cli tools run screenshot.capture --input-json '{"output":"verify.png"}'
unity_cli tools run editor.console
```

### Exploring Available Capabilities

```bash
unity_cli capabilities
unity_cli tools groups
unity_cli tools search camera
unity_cli tools describe camera.screenshot
unity_cli tools run camera.screenshot --input-json '{"target":"MainCamera","output":"shot.png","width":1280,"height":720}'
```

### Running a Runtime Tool

```bash
# Describe first to check requirements
unity_cli tools describe <runtimeToolName>
# If requiresPlayMode, enter Play Mode
unity_cli tools run editor.play
# Then run the runtime tool
unity_cli tools run <runtimeToolName> --input-json '<params>'
```

### Checking Editor State Before Operations

```bash
unity_cli tools describe editor.state
unity_cli tools run editor.state
# If compiling/importing — wait and retry
unity_cli tools run editor.console
```

---

## Error Handling

All CLI output follows a structured envelope:

```json
{
  "ok": true|false,
  "requestId": "req_...",
  "data": { ... },
  "error": {
    "code": "ERROR_CODE",
    "message": "Human-readable message",
    "next_actions": ["Suggested recovery steps"],
    "details": { ... },
    "transient": true,
    "retry_after_ms": 5000
  }
}
```

**Transient errors** (compiling, importing, domain reload, busy):
- `error.transient === true` means the Editor is temporarily unavailable.
- Respect `retry_after_ms` before retrying.
- Common transient codes: `E_UNITY_COMPILING`, `E_UNITY_IMPORTING`, `E_UNITY_DOMAIN_RELOAD`, `E_UNITY_BUSY`.

**Target errors:**
- `E_TARGET_AMBIGUOUS` → Multiple instances; pass `--target` or run `target set`.
- `E_TARGET_STALE` → Instance heartbeat is stale; reopen the Editor.
- `E_TARGET_UNREACHABLE` → Bridge endpoint not responding; check the Editor process.
- `E_TARGET_BUSY` → Target is busy (compiling, etc.); wait and retry.

**Tool discovery errors:**
- `E_TOOL_NOT_FOUND_FOR_TARGET` → Tool not available on this target instance; use `tools list` to see what's available.
- `E_TOOL_SCHEMA_CONFLICT` → Same-named tool has different schemas across targets; specify `--target` to resolve.
- `E_TOOL_AVAILABILITY_STALE` → Tool availability fingerprint is outdated; retry with a fresh `tools describe`.
- `E_TOOL_DESCRIPTOR_INVALID` → Tool descriptor is malformed; this is a CLI/internal issue.

**Runtime/session errors:**
- `E_PLAYMODE_NOT_READY` → Tool requires Play Mode; enter Play Mode first, then retry `tools run`.
- `E_RUNTIME_SESSION_STALE` → Runtime session is no longer valid; re-enter Play Mode and retry.

**Invocation errors:**
- `E_TOOL_INPUT_INVALID` → Input does not match `inputSchema`; check `details.validationErrors` and correct parameters.
- `E_TOOL_RISK_CONFIRMATION_REQUIRED` → Destructive tool requires explicit confirmation; confirm with the user and retry.
- `E_TOOL_TIMEOUT` → Execution exceeded timeout; check `--timeout-ms` or use `job status` for long-running tools.

**Always follow `next_actions`** — the CLI provides actionable recovery steps for every error.

---

## What NOT to Do

- **Do not** use `custom-tool` commands — they have been removed. Use `tools` commands exclusively.
- **Do not** use domain-specific commands (e.g. `unity_cli scene hierarchy`, `unity_cli editor console`, `unity_cli code exec`) as your primary execution path — use `tools run <toolName>` instead. The `cliCommand` field in descriptors exists only for backward-compatibility and debugging, not as the user-facing execution entry point.
- **Do not** use standalone `runtime` commands — runtime capabilities are expressed through tool descriptors (`target=runtime`, `requiresPlayMode`, `nextActions` in `tools describe` output) and executed via `tools run`.
- **Do not** manually ping bridge endpoints or read instance registry files — use `instances list` and `target` commands.
- **Do not** skip `tools describe` before running a tool you haven't used before — always check schema, hints, and examples first.
- **Do not** assume all tools are available on every instance — different Unity projects expose different tool sets. Use `tools list` to discover what's available per target.

---

## Deep Documentation

For detailed parameter schemas, extended examples, and architecture notes, use:

```bash
unity_cli docs                        # List available documentation topics
unity_cli docs topic <topic>          # Read a topic
unity_cli docs search <query>         # Search documentation
```

The `docsTopic` field in `tools describe` output links to the relevant deep documentation topic for each tool.
