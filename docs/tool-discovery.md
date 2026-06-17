# Tool Discovery

> **Discovery path**: `tools list → tools search → tools describe → execute`

## The Unified Tools Surface

The CLI exposes a single, unified discovery surface through the `tools` command. There is no separate `custom-tool` command group, no independent `runtime` command group, and no per-domain registry. All tools — built-in and project-local — share the same descriptor schema and the same discovery commands.

```powershell
unity_cli tools list                         # All built-in tools
unity_cli tools list --include-custom        # Built-in + project tools (requires Bridge)
unity_cli tools list --group scene           # Filter by group
unity_cli tools groups                       # List all groups
unity_cli tools search camera                # Search by keyword
unity_cli tools search echo --include-custom # Include project tools in search
unity_cli tools describe scene.hierarchy     # Full descriptor for one tool
unity_cli tools describe sample.echo         # Project tool descriptor
```

## What `tools describe` Returns

`tools describe <name>` is the single source of truth for any tool. It returns the full [tool-metadata schema](../schemas/tool-metadata.schema.json):

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
| `handler` | Execution mode: `bridge`, `job`, `offline`, or `hybrid` |
| `supportStatus` | User-facing label: `live-bridge`, `bridge-job`, `batch-job`, `cli-only`, or `planned` |
| `cliCommand` | The CLI argv prefix, e.g. `scene hierarchy` |
| `architectureSensitive` | `true` if tied to a frozen architecture decision |
| `frozenDecision` | Reference to the frozen decision (e.g. `ALP-165-Q4`) |
| `coverageScope` | Frozen coverage facets this tool contributes to |
| `cancellationSemantics` | How cancellation works for long-running tools |
| `timeoutMs` | Suggested timeout |
| `permissions` | Permission tokens required |
| `examples` | Representative invocations with argv and expected output |
| `docsTopic` | Key for `unity_cli docs topic <key>` |

## Handler and Support Status

Every tool has a `handler` (internal execution path) and a `supportStatus` (user-facing label). The mapping is:

| `handler` | `supportStatus` | Label | Meaning |
|-----------|-----------------|-------|---------|
| `bridge` | `live-bridge` | 已路由可执行 | Live Bridge `/bridge/execute` |
| `hybrid` | `bridge-job` | 已路由可执行 + 可 batch/job | Live Bridge + batch/job channels |
| `job` | `batch-job` | 仅 batch/job | Unity batchmode job only |
| `offline` | `cli-only` | 仅 CLI 离线 | No Unity connection needed |
| — | `planned` | 规划中 | Declared but not yet implemented |

## Target Context in Discovery

`tools list` and `tools search` include an optional `target` field when a target instance is resolved. This shows which instance's tool catalog is being reported.

When `--include-custom` is used and a Bridge is reachable, project tools registered by `[UnityCliTool]` C# attributes in the target Unity project are merged into the discovery results. Project tools share the same schema as built-in tools — they are not a separate category.

## How to Use Discovery as an AI Agent

1. **List tools** — `unity_cli tools list --include-custom` gives you all available tools for the resolved target.
2. **Search by keyword** — `unity_cli tools search <keyword> --include-custom` when you know the domain but not the exact tool name.
3. **Describe before executing** — Always run `unity_cli tools describe <name>` before executing any tool. The descriptor tells you:
   - What parameters are required (`inputSchema`)
   - Whether the tool is safe to call (`readOnlyHint`, `destructiveHint`)
   - How the tool executes (`handler`, `supportStatus`)
   - What to expect in the result (`outputSchema`, `examples`)
   - What to do if something goes wrong (see [error-codes](./error-codes.md))
   - How to verify the result (see [tool-execution](./tool-execution.md))

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
