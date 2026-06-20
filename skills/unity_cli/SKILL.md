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
| `target` | Execution target: `editor`, `runtime`, or `both` |
| `availabilityScope` | `editorSession` or `playModeSession` |
| `available` | Whether the tool is currently available for this target |

**Tip:** Use `tools list --include-custom` to discover project-specific Editor tools registered by the connected Unity instance.

## Step 4: Describe a Tool Before Running

```bash
unity_cli tools describe <toolName>
```

**Always describe a tool before running it.** The response contains everything you need to construct the `tools run` invocation:

| Field | Purpose |
|-------|---------|
| `inputSchema` | JSON Schema for parameters — use this to build `--input` |
| `outputSchema` | JSON Schema for successful output |
| `target` | Whether this tool targets `editor`, `runtime`, or `both` |
| `handler` | Execution mode: `bridge`, `bridge-runtime`, `job`, `offline`, or `hybrid` |
| `supportStatus` | Availability label: `live-bridge`, `live-bridge-runtime`, `bridge-job`, `batch-job`, `cli-only`, or `planned` |
| `availabilityScope` | `editorSession` or `playModeSession` |
| `available` | Whether the tool is currently available for this target |
| `readOnlyHint` | Whether the tool mutates state |
| `destructiveHint` | Whether changes are irreversible |
| `supportsDryRun` | Whether `--dry-run` is available |
| `longRunning` | Whether to expect delays |
| `examples` | Ready-to-use command examples |
| `next_actions` | Suggested follow-up actions after running (JSON field: `next_actions`, prose: "next actions") |
| `docsTopic` | Topic key for `unity_cli docs topic <key>` for deeper docs |

**Key behavioral hints from describe:**

- **`destructiveHint: true`** → Confirm with the user before executing. Use `--dry-run` if `supportsDryRun: true`.
- **`target: runtime`** and **`availabilityScope: playModeSession`** → The tool needs Play Mode active. If `available: false`, enter Play Mode first.
- **`available: false`** → The tool cannot run on this target in the current state. Check `next_actions` for guidance.
- **`longRunning: true`** → May take seconds or minutes. Poll job status with `unity_cli job status --id <jobId>`.
- **`target: runtime`** → This tool executes through the runtime executor in Play Mode. Still use `tools run` — the router handles the execution path internally.
- **`target: both`** → This tool can execute on either the editor or runtime path. Use `--runtime` to force the runtime path.

**About `cliCommand`**: The descriptor may include a `cliCommand` field (e.g. `scene hierarchy`) for backward-compatibility or low-level debugging. **Do not use `cliCommand` as your primary execution path.** Always use `tools run <toolName>` instead.

## Step 5: Execute a Tool with `tools run`

```bash
unity_cli tools run <toolName> [--input '<json>'] [--input-file <path>] [--target <id|alias>] [--timeout-ms <n>] [--fingerprint-mode warn|block] [--runtime]
```

This is the **only** execution entry point you need. The invocation router internally selects the correct execution path (editor bridge, runtime executor, CLI local, or job runner) based on the tool's descriptor.

### Input Methods

Parameters come from `tools describe.inputSchema`. Pass them using one of:

```bash
# Inline JSON (most common) — use --input
unity_cli tools run camera.screenshot --input '{"target":"MainCamera","output":"screenshot.png","width":1280,"height":720}'

# From a file — use --input-file
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

For tools with `destructiveHint: true`:

```bash
# The CLI will prompt for confirmation before executing
unity_cli tools run asset.delete --input '{"path":"Assets/Old.asset"}'

# Use --dry-run to preview without executing (if supportsDryRun is true)
unity_cli tools run asset.delete --input '{"path":"Assets/Old.asset"}' --dry-run

# Add --yes to confirm in non-TTY / CI environments
unity_cli tools run asset.delete --input '{"path":"Assets/Old.asset"}' --yes
```

Always confirm destructive operations with the user before running them.

### Runtime Tools

Runtime tools (`target: runtime` or `target: both`) also use `tools run` — no separate command:

```bash
# Runtime tool — the router handles Play Mode and session checks
unity_cli tools run <runtimeToolName> --input '<params>'
```

If `available: false` for a runtime tool (Editor not in Play Mode), `tools run` will return `E_RUNTIME_NOT_READY` (transient) with `next_actions` telling you to enter Play Mode first. Enter Play Mode, then retry:

```bash
unity_cli tools run editor.play
unity_cli tools run <runtimeToolName> --input '<params>'
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
unity_cli tools run code.exec --input '{"code":"..."}' --timeout-ms 120000
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

## Optional Package Extensions

Some tools require optional Unity packages to be installed in the target project. These tools declare `requiresPackages` in their descriptor and will return `E_*_PACKAGE_NOT_FOUND` with `unavailable + next_actions` when the package is missing.

### Discovering Extension Tools

Extension tools are exposed through the same group-first discovery workflow:

```bash
unity_cli tools groups                       # includes cinemachine, vfx, urp, hdrp, probuilder groups
unity_cli tools list --group cinemachine     # list Cinemachine tools
unity_cli tools list --group vfx             # list VFX Graph tools
unity_cli tools list --group urp            # list URP tools
unity_cli tools list --group hdrp           # list HDRP tools
unity_cli tools describe cinemachine.create  # inspect a specific extension tool
```

### Cinemachine (com.unity.cinemachine)

Requires the Cinemachine package. When missing, tools return `E_CINEMACHINE_PACKAGE_NOT_FOUND`.

```bash
# Create a follow camera preset (configures Body/Aim pipeline components)
unity_cli tools run cinemachine.create --input '{"preset":"follow","name":"PlayerCam","follow":"Player"}'

# Set lens properties via SerializedObject (survives internal field renames)
unity_cli tools run cinemachine.lens --input '{"target":"PlayerCam","fieldOfView":60}'

# Configure the Brain's default blend
unity_cli tools run cinemachine.blend --input '{"target":"Main Camera","style":"ease_in_out","duration":2}'

# Swap Body/Aim pipeline components
unity_cli tools run cinemachine.body --input '{"target":"PlayerCam","body":"CinemachineFollow"}'
unity_cli tools run cinemachine.aim --input '{"target":"PlayerCam","aim":"CinemachineRotationComposer"}'

# Add/list/remove CinemachineExtension components
unity_cli tools run cinemachine.extension --input '{"target":"PlayerCam","action":"list"}'
```

### VFX Graph (com.unity.visualeffectgraph)

Requires the Visual Effect Graph package. When missing, tools return `E_VFX_PACKAGE_MISSING`.

```bash
# Create a VFX asset from a built-in template
unity_cli tools run vfx.template --input '{"path":"Assets/VFX/Sparkle.vfx","template":"AR/AugmentedRealityTemplate"}'
unity_cli tools run vfx.template --input '{"template":"list"}'   # discover available templates

# Set exposed parameters (float, int, bool, vector2/3/4, color, texture, mesh)
unity_cli tools run vfx.parameter --input '{"target":"VFXFire","name":"Intensity","type":"float","value":"2.5"}'
unity_cli tools run vfx.parameter --input '{"target":"VFXFire","name":"Velocity","type":"vector3","x":0,"y":1,"z":0}'

# Control playback
unity_cli tools run vfx.control --input '{"target":"VFXFire","action":"play"}'
unity_cli tools run vfx.control --input '{"target":"VFXFire","action":"rate","rate":0.5}'

# Send named events with attribute payloads
unity_cli tools run vfx.event --input '{"target":"VFXFire","eventName":"Explode","attributeName":"position","attributeX":1,"attributeY":2,"attributeZ":3}'
```

### URP (com.unity.render-pipelines.universal)

Requires URP to be the active render pipeline. When not active, tools return `E_URP_PACKAGE_NOT_FOUND`.

```bash
# List/add/remove/toggle/configure Renderer Features
unity_cli tools run urp.feature --input '{"action":"list"}'
unity_cli tools run urp.feature --input '{"action":"add","featureType":"ScreenSpaceAmbientOcclusion"}'
unity_cli tools run urp.feature --input '{"action":"toggle","index":0,"enabled":false}'

# Create/list/configure Volumes
unity_cli tools run urp.volume --input '{"action":"create","name":"GlobalVolume","isGlobal":true}'
unity_cli tools run urp.volume --input '{"action":"add-effect","target":"GlobalVolume","effectType":"ColorAdjustments"}'

# Get/set pipeline asset settings
unity_cli tools run urp.settings --input '{}'
unity_cli tools run urp.settings --input '{"property":"renderScale","value":"1.5"}'
```

### HDRP (com.unity.render-pipelines.high-definition)

Requires HDRP to be the active render pipeline. When not active, tools return `E_HDRP_PACKAGE_NOT_FOUND`.

```bash
# Create/list/configure Volumes
unity_cli tools run hdrp.volume --input '{"action":"create","name":"GlobalVolume","isGlobal":true}'
unity_cli tools run hdrp.volume --input '{"action":"add-effect","target":"GlobalVolume","effectType":"HDShadowSettings"}'

# Get/set pipeline asset settings
unity_cli tools run hdrp.settings --input '{}'
unity_cli tools run hdrp.settings --input '{"property":"shadowDistance","value":"500"}'
```

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
unity_cli tools run scene.load --input '{"path":"Assets/Scenes/Main.unity"}'
unity_cli tools run scene.hierarchy
unity_cli tools run screenshot.capture --input '{"output":"verify.png"}'
unity_cli tools run editor.console
```

### Exploring Available Capabilities

```bash
unity_cli capabilities
unity_cli tools groups
unity_cli tools search camera
unity_cli tools describe camera.screenshot
unity_cli tools run camera.screenshot --input '{"target":"MainCamera","output":"shot.png","width":1280,"height":720}'
```

### Running a Runtime Tool

```bash
# Describe first to check requirements
unity_cli tools describe <runtimeToolName>
# If available: false (requires Play Mode), enter Play Mode
unity_cli tools run editor.play
# Then run the runtime tool
unity_cli tools run <runtimeToolName> --input '<params>'
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

**Note:** JSON field names are `next_actions` and `retry_after_ms` (snake_case), matching the error envelope schema. In prose we write "next actions" for readability, but always use `next_actions` when parsing error responses.

**Transient errors** (compiling, importing, domain reload, busy, runtime not ready):
- `error.transient === true` means the Editor is temporarily unavailable.
- Respect `retry_after_ms` before retrying.
- Common transient codes: `E_UNITY_COMPILING`, `E_UNITY_IMPORTING`, `E_UNITY_DOMAIN_RELOAD`, `E_UNITY_BUSY`, `E_RUNTIME_NOT_READY`.

**Target errors:**
- `E_TARGET_AMBIGUOUS` → Multiple instances; pass `--target` or run `target set`.
- `E_TARGET_STALE` → Instance heartbeat is stale; reopen the Editor.
- `E_TARGET_UNREACHABLE` → Bridge endpoint not responding; check the Editor process.
- `BRIDGE_CONNECTION_FAILED` → Cannot connect to Bridge; ensure Editor is running.

**Runtime/session errors:**
- `E_RUNTIME_NOT_READY` → Runtime tool requires Play Mode; enter Play Mode first (`tools run editor.play`), then retry.
- `E_RUNTIME_SESSION_STALE` → Runtime session is no longer valid; re-enter Play Mode and retry.
- `E_RUNTIME_TIMEOUT` → Runtime tool execution timed out; increase `--timeout-ms`.

**Tool discovery errors:**
- `E_TOOL_NOT_FOUND_FOR_TARGET` → Tool not available on this target instance; use `tools list` to see what's available.

**Execution errors:**
- `E_DESTRUCTIVE_CONFIRMATION_REQUIRED` → Destructive tool requires `--yes`; use `--dry-run` first if supported.
- `E_EXECUTION_TIMEOUT` → Execution exceeded timeout; increase `--timeout-ms`.
- `E_CODE_SOURCE_INVALID` → Invalid code source; use exactly one of `--file`, `--stdin`, or `--code`.

**Always follow `next_actions`** — the CLI provides actionable recovery steps for every error.

---

## What NOT to Do

- **Do not** use `custom-tool` commands — they have been removed. Use `tools` commands exclusively.
- **Do not** use domain-specific commands (e.g. `unity_cli scene hierarchy`, `unity_cli editor console`, `unity_cli code exec`) as your primary execution path — use `tools run <toolName>` instead. The `cliCommand` field in descriptors exists only for backward-compatibility and debugging, not as the user-facing execution entry point.
- **Do not** use standalone `runtime` commands — runtime capabilities are expressed through tool descriptors (`target: runtime`, `availabilityScope: playModeSession`, `available`) and executed via `tools run`.
- **Do not** manually ping bridge endpoints or read instance registry files — use `instances list` and `target` commands.
- **Do not** skip `tools describe` before running a tool you haven't used before — always check schema, hints, and examples first.
- **Do not** assume all tools are available on every instance — different Unity projects expose different tool sets. Use `tools list` to discover what's available per target.
- **Do not** use `--input-json` — the CLI parameter is `--input`. Use `--input-file` for file input.

---

## Deep Documentation

For detailed parameter schemas, extended examples, and architecture notes, use:

```bash
unity_cli docs                        # List available documentation topics
unity_cli docs topic <topic>          # Read a topic
unity_cli docs search <query>         # Search documentation
```

The `docsTopic` field in `tools describe` output links to the relevant deep documentation topic for each tool.
