# Tool Discovery

> **Discovery path**: `tools group` → `tools group list <group>` → `tools describe <tool>` → `tools run <tool>`

## The Group-First Discovery Surface

The CLI exposes a **group-first** discovery surface through the `tools` command. Instead of listing all tools at once, you start by discovering capability groups, then drill into a specific group, then describe a tool before running it.

```powershell
unity_cli tools group                            # List all capability groups
unity_cli tools group list scene                 # List tools in the "scene" group
unity_cli tools group list scene --include-custom # Include project tools in the scene group
unity_cli tools search camera                    # Search by keyword across all tools
unity_cli tools describe scene.hierarchy         # Full descriptor for one tool
unity_cli tools run scene.hierarchy              # Execute a tool
```

### Why Group-First?

The old `tools list` returned every tool in a flat list — potentially hundreds of entries. Group-first discovery makes it easy to find relevant tools by starting from capability domains (scene, camera, editor, etc.) and narrowing down.

## Discovery Commands

### `tools group`

List all available tool capability groups.

```powershell
unity_cli tools group
unity_cli tools group --include-custom   # Include groups from project tools
```

Returns a list of group names like `scene`, `camera`, `editor`, `probuilder`, etc.

### `tools group list <group>`

List all tools within a specific capability group.

```powershell
unity_cli tools group list scene
unity_cli tools group list scene --include-custom
unity_cli tools group list scene --format text
```

Each result includes the tool name, title, group, support status, read-only/destructive hints, target, and availability scope.

### `tools search <keyword>`

Search across all tools by keyword (name, title, description, group).

```powershell
unity_cli tools search camera
unity_cli tools search echo --include-custom
```

### `tools describe <tool>`

Full descriptor for a single tool. **Always describe before running.**

```powershell
unity_cli tools describe scene.hierarchy
unity_cli tools describe sample.echo
```

### `tools run <tool>`

Execute a tool by its dotted name. See [tool-execution](./tool-execution.md) for details.

```powershell
unity_cli tools run scene.hierarchy
unity_cli tools run camera.screenshot --input '{"target":"MainCamera","output":"shot.png"}'
```

## Deprecated Commands

The following commands are retained as compat paths but should not be used in new workflows:

| Deprecated Command | Replacement |
|---|---|
| `tools list` | `tools group list <group>` (or `tools group` to see groups first) |
| `tools list --group <group>` | `tools group list <group>` |
| `tools groups` | `tools group` |

`tools list` without `--group` performs flat full discovery of all tools, which does not follow the group-first discovery model. Use `tools group` to list groups, then `tools group list <group>` to list tools within a specific group.

## What `tools describe` Returns

`tools describe <name>` is the single source of truth for any tool. It returns the tool descriptor based on [tool-metadata.schema.json](../schemas/tool-metadata.schema.json).

### Current descriptor fields

| Field | Meaning |
|-------|---------|
| `name` | Dotted identifier, e.g. `scene.hierarchy`, `sample.echo` |
| `title` | Human-readable short title |
| `description` | One-paragraph explanation |
| `group` | Logical grouping, e.g. `scene`, `camera`, `probuilder` |
| `inputSchema` | JSON Schema for input parameters |
| `outputSchema` | JSON Schema for successful output (when available) |
| `readOnlyHint` | `true` if the tool never mutates Unity state |
| `destructiveHint` | `true` if the tool may irreversibly modify or delete data |
| `supportsDryRun` | `true` if `--dry-run` is supported |
| `handler` | Execution mode: `bridge`, `bridge-runtime`, `job`, `offline`, or `hybrid` |
| `supportStatus` | User-facing label: `live-bridge`, `live-bridge-runtime`, `bridge-job`, `batch-job`, `cli-only`, or `planned` |
| `cliCommand` | The CLI argv prefix, e.g. `scene hierarchy` |
| `architectureSensitive` | `true` if tied to a frozen architecture decision |
| `frozenDecision` | Reference to the frozen decision (e.g. `ALP-165-Q4`) |
| `coverageScope` | Frozen coverage facets this tool contributes to |
| `cancellationSemantics` | How cancellation works for long-running tools |
| `timeoutMs` | Suggested timeout |
| `permissions` | Permission tokens required |
| `examples` | Representative invocations with argv and expected output |
| `docsTopic` | Key for `unity_cli docs topic <key>` |
| `target` | Execution target: `editor` (default), `runtime`, or `both` |
| `availabilityScope` | `editorSession` (default) or `playModeSession` (runtime tools) |
| `available` | Whether the tool is currently available based on session state |

### Descriptor field coverage vs. P3 contract

The P3 frozen contract (`ALP-369`) defines additional descriptor fields that are not yet in the current `tool-metadata.schema.json` or `ToolMetadata` interface. The current fields map to P3 concepts as follows:

| P3 field | Current mapping | Gap |
|----------|----------------|-----|
| `source` | `builtin` if in shipped metadata; `project` if discovered via `--include-custom` | Not an explicit field; inferred from discovery mode |
| `target` | `target` field on `ToolMetadata` | ✅ Implemented |
| `execution` | `handler` field maps directly: `bridge` → `editorBridge`, `bridge-runtime` → `runtimeExecutor`, `hybrid` → both | Naming differs from P3; semantics match |
| `availabilityScope` | `availabilityScope` field on `ToolMetadata` | ✅ Implemented |
| `requiresPlayMode` | Implied by `target: "runtime"` and `availabilityScope: "playModeSession"` | Not a separate boolean; derived from `target` + `availabilityScope` |
| `risk` | `destructiveHint` + `readOnlyHint` + `supportsDryRun` + `permissions` | Structured differently; same information |
| `validation` | Not yet in schema | P3 field not implemented |
| `fingerprint` | Project tools carry `fingerprint` at runtime; not in static metadata | Partially implemented (project tools only) |
| `version` | Not in schema | P3 field not implemented |

**Do not assume the current descriptor fully matches the P3 contract.** Always use `tools describe <name>` to see the actual fields returned for a specific tool. The mapping above is provided for architecture review purposes only.

## Handler and Support Status

Every tool has a `handler` (internal execution path) and a `supportStatus` (user-facing label). The mapping is:

| `handler` | `supportStatus` | Label | Meaning |
|-----------|-----------------|-------|---------|
| `bridge` | `live-bridge` | 已路由可执行 | Live Bridge `/bridge/execute` (editor domain) |
| `bridge-runtime` | `live-bridge-runtime` | 已路由可执行 (Runtime) | Runtime tool via Bridge with Play Mode session validation |
| `hybrid` | `bridge-job` | 已路由可执行 + 可 batch/job | Live Bridge + batch/job channels |
| `job` | `batch-job` | 仅 batch/job | Unity batchmode job only |
| `offline` | `cli-only` | 仅 CLI 离线 | No Unity connection needed |
| — | `planned` | 规划中 | Declared but not yet implemented |

## Target Context in Discovery

`tools group list` and `tools search` include an optional `target` field when a target instance is resolved. This shows which instance's tool catalog is being reported. They also include `target`, `availabilityScope`, and `available` per tool so callers can determine whether a tool is currently usable.

When `--include-custom` is used and a Bridge is reachable, project tools registered by `[UnityCliTool]` C# attributes in the target Unity project are merged into the discovery results. Project tools share the same schema as built-in tools — they are not a separate category.

## How to Use Discovery as an AI Agent

1. **List groups** — `unity_cli tools group` gives you all capability domains.
2. **List tools in a group** — `unity_cli tools group list <group> --include-custom` gives you all available tools for that group on the resolved target, including `target` and `availabilityScope` per tool.
3. **Search by keyword** — `unity_cli tools search <keyword> --include-custom` when you know the domain but not the exact tool name.
4. **Describe before executing** — Always run `unity_cli tools describe <name>` before executing any tool. The descriptor tells you:
   - What parameters are required (`inputSchema`)
   - Whether the tool is safe to call (`readOnlyHint`, `destructiveHint`)
   - How the tool executes (`handler`, `supportStatus`, `target`)
   - Whether the tool is currently available (`available`, `availabilityScope`)
   - What to expect in the result (`outputSchema`, `examples`)
   - What to do if something goes wrong (see [error-codes](./error-codes.md))
   - How to verify the result (see [tool-execution](./tool-execution.md))
5. **Run the tool** — `unity_cli tools run <name>` executes the tool through the unified entry point (see [tool-execution](./tool-execution.md)).

## Built-in Documentation Topics

`unity_cli docs` lists built-in topic summaries derived from tool metadata. `unity_cli docs topic <key>` returns a topic with its summary and the list of tools it contains. This is an offline command and does not require a Unity instance.

## Capabilities Manifest

```powershell
unity_cli capabilities
unity_cli capabilities --bridge-endpoint http://127.0.0.1:47392
```

Returns a merged manifest of:
- **Static capabilities**: tool counts, group breakdown, destructive/read-only/long-running tool lists, schema features
- **Runtime capabilities** (when Bridge is reachable): bridge health and diagnostics

The `capabilities` command is useful for quickly checking what a target instance supports without listing every tool individually.

## Cross-Reference

- Target selection: [target-and-instance](./target-and-instance.md)
- Availability differences across instances: [availability-differences](./availability-differences.md)
- Execution model and verification: [tool-execution](./tool-execution.md)
- Tool metadata schema: [../schemas/tool-metadata.schema.json](../schemas/tool-metadata.schema.json)
- AI skill (compact version): [/skills/unity_cli/SKILL.md](../skills/unity_cli/SKILL.md)
- Examples (runnable): [/examples/](../examples/)
