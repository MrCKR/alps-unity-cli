---
name: unity_cli
description: Operate Unity Editor via the alps-unity-cli target-first discovery and unified tools workflow. Use when you need to discover, inspect, or control Unity instances, scenes, assets, or any Editor capability through the CLI's unified tool model.
---

# Unity CLI — AI Operator Skill

This skill teaches you how to operate the Unity Editor through `unity_cli` using the **target-first discovery** and **unified tools** workflow. You do not need to memorize command groups — the CLI discovers and describes everything for you.

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

**Convenience — use --target flag instead of target set:**

```bash
unity_cli tools list --target <id|alias>
unity_cli tools describe scene.hierarchy --target <id|alias>
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
| `cliCommand` | The CLI command to execute this tool |

**Tip:** Use `tools list --include-custom` to discover project-specific Editor tools registered by the connected Unity instance.

## Step 4: Describe a Tool Before Running

```bash
unity_cli tools describe <toolName>
```

**Always describe a tool before running it.** The response contains everything you need:

| Field | Purpose |
|-------|---------|
| `inputSchema` | JSON Schema for parameters |
| `outputSchema` | JSON Schema for successful output |
| `readOnlyHint` | Whether the tool mutates state |
| `destructiveHint` | Whether changes are irreversible |
| `longRunning` | Whether to expect delays |
| `supportsDryRun` | Whether `--dry-run` is available |
| `examples` | Ready-to-use command examples |
| `cliCommand` | The exact CLI command to execute |
| `supportStatusDescription` | How this tool is executed (bridge, job, offline, etc.) |
| `docsTopic` | Topic key for `unity_cli docs topic <key>` for deeper docs |

**Key behavioral hints from describe:**

- **`destructiveHint: true`** → Confirm with the user before executing. Use `--dry-run` if available.
- **`longRunning: true`** → May take seconds or minutes. Poll job status with `unity_cli job status --id <jobId>`.
- **`supportStatus` values:**
  - `live-bridge` — Immediate via Editor bridge
  - `bridge-job` — Bridge + batch/job fallback
  - `batch-job` — Batch mode only (slower)
  - `cli-only` — No Unity connection needed
  - `planned` — Declared but not yet implemented

## Step 5: Execute a Tool

```bash
# Use the cliCommand from tools describe
unity_cli <cliCommand> [options]

# Examples:
unity_cli scene hierarchy
unity_cli editor console
unity_cli camera screenshot MainCamera --output screenshot.png --width 1280 --height 720
unity_cli code exec --code 'Debug.Log("hello");'
```

The `cliCommand` field from `tools describe` tells you the exact CLI invocation. Parameters come from `inputSchema`.

**For destructive tools:** The CLI prompts for confirmation. Pass `--yes` to auto-confirm (use cautiously) or `--dry-run` to preview.

**For tools that run as jobs:** The command returns a `jobId`. Track progress:

```bash
unity_cli job status --id <jobId>
unity_cli job logs --id <jobId> --tail 50
unity_cli job cancel --id <jobId>
```

## Step 6: Verify Results

After executing a tool, verify the outcome:

- **Read-only tools:** Check the response data directly.
- **State-changing tools:** Use a corresponding read-only tool to confirm.
  - After `editor play` → check `editor state`
  - After `scene load` → check `scene hierarchy`
  - After `package install` → check `package list`
- **Visual verification:** Use `camera screenshot` or `screenshot capture` to visually confirm scene changes.
- **Console check:** Use `editor console` to check for errors or warnings after mutations.
- **Compilation check:** After script changes, wait for compilation, then check `editor console` for errors.

---

## Common Patterns

### Starting a Session

```bash
unity_cli instances list
unity_cli target set <id|alias>
unity_cli tools list --group scene
unity_cli tools describe scene.hierarchy
unity_cli scene hierarchy
```

### Making a Scene Change and Verifying

```bash
unity_cli tools describe scene.load
unity_cli scene load "Assets/Scenes/Main.unity"
unity_cli scene hierarchy
unity_cli screenshot capture verify.png
unity_cli editor console
```

### Exploring Available Capabilities

```bash
unity_cli capabilities
unity_cli tools groups
unity_cli tools search camera
unity_cli tools describe camera.screenshot
```

### Checking Editor State Before Operations

```bash
unity_cli tools describe editor.state
unity_cli editor state
# If compiling/importing — wait and retry
unity_cli editor console
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

**Tool errors:**
- `E_TOOL_NOT_FOUND_FOR_TARGET` → Tool not available on this instance; use `tools list` to see what's available.
- `E_TOOL_AVAILABILITY_STALE` → Tool catalog may be outdated; retry after bridge reconnects.

---

## What NOT to Do

- **Do not** use `custom-tool` commands — they have been removed. Use `tools` commands exclusively.
- **Do not** use standalone `runtime` commands — runtime capabilities are expressed through tool descriptors (`target`, `requiresPlayMode`, `nextActions` in `tools describe` output).
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
