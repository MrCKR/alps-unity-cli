# Availability Differences

> **Why different Unity instances may return different tool availability**

## Core Principle

Tool availability is **per-instance**, not global. `tools list` and `tools describe` reflect the capabilities of the resolved target instance. Different instances may show different tool sets for the following reasons.

## Reasons for Availability Differences

### 1. Installed Packages

Tools backed by specific Unity packages are only available when the target project has those packages installed:

| Tool | Package Dependency | Unavailable Behavior |
|------|-------------------|---------------------|
| `probuilder.shape/create/edit/csg` | `com.unity.probuilder` | Returns `E_PROBUILDER_PACKAGE_NOT_FOUND` |
| `vfx.graph/vfx.effect` | `com.unity.visualeffectgraph` | Returns `E_VFX_PACKAGE_MISSING` |

When `tools list --include-custom` is called against an instance whose project lacks these packages, the corresponding tools still appear in the built-in metadata (because built-in metadata is shipped with the CLI), but executing them against that instance returns a package-not-found error.

### 2. Project Tool Registration

Project tools (`[UnityCliTool]` C# attributes) are discovered by querying the Bridge at runtime. Different projects register different project tools. `tools list --include-custom` against Instance A may show `sample.echo` while Instance B shows `acme.custom-builder` — or no project tools at all.

Project tool discovery requires a reachable Bridge. Without a Bridge connection, only built-in tools are discoverable.

### 3. Bridge Reachability

| Bridge State | Available Tools |
|-------------|-----------------|
| Reachable (`/health` OK) | All built-in tools + project tools (with `--include-custom`) |
| Not reachable | Only `offline` and `cli-only` tools |
| Stale (heartbeat expired) | No Bridge commands; CLI returns `E_TARGET_STALE` |
| Unreachable (`/health` failed) | No Bridge commands; CLI returns `E_TARGET_UNREACHABLE` |

Offline tools (`handler: "offline"`, `supportStatus: "cli-only"`) always work regardless of Bridge state. This includes:
- `package list/info/search/install/remove`
- `batch schema/run`
- `docs get/search`, `reflect type/member/search`
- `job status/logs/cancel`
- `init --check`, `doctor`, `instances list/prune`
- `target get/set/clear/alias`

### 4. Editor State (PlayMode) and Runtime Tool Availability

Tools with `target: "runtime"` and `availabilityScope: "playModeSession"` are only available when the Editor is in Play Mode. The `available` field in the tool descriptor reflects this:

| `editor.state` | `target: "editor"` tools | `target: "runtime"` tools |
|----------------|--------------------------|---------------------------|
| Edit mode | `available: true` | `available: false` — returns `E_RUNTIME_NOT_READY` (transient) |
| Play mode | `available: true` | `available: true` |

To make a runtime tool available:
1. Call `tools run editor.play` to enter Play Mode
2. Verify with `tools run editor.state` that `playMode: playing`
3. Retry the runtime tool

Some tools behave differently depending on PlayMode state:
- `code exec --channel live` executes in the current context (edit mode or play mode)
- Profiler tools (`profiler.start/stop/capture`) may behave differently in play mode vs edit mode
- `physics.simulation --simulate` requires the physics system to be in a valid state

### 5. Domain Reload

During Unity domain reload (e.g., after script compilation), the Bridge marks the target as stale and stops serving requests. The CLI returns `E_UNITY_COMPILING` or `E_UNITY_BUSY` during this window. After reload completes, the Bridge re-registers with updated capabilities.

### 6. Tool Catalog Fingerprint

Each instance carries a `toolCatalogFingerprint` that captures the current tool catalog state. If the fingerprint changes between discovery and execution (e.g., because a package was installed or a domain reload occurred), the CLI may return a fingerprint mismatch warning. The `doctor` command checks fingerprint consistency.

## How to Handle Availability Differences

### As an AI Agent

1. **Always resolve target first.** Run `instances list` and `target set` before any tool call.
2. **Use `tools describe` before executing.** The descriptor shows `handler`, `supportStatus`, `target`, `availabilityScope`, and `available`.
3. **Handle errors gracefully.** Package-not-found, Bridge-unreachable, and `E_RUNTIME_NOT_READY` errors include `next_actions` that suggest recovery steps.
4. **Re-discover after state changes.** After installing packages or domain reload, re-run `tools list --include-custom` to see updated availability.
5. **Check `editor.state`** before calling tools that are PlayMode-sensitive. Enter Play Mode first if `target: "runtime"`.

### Error Response Pattern

When a tool is unavailable for the resolved target, the response follows the standard error envelope:

```json
{
  "ok": false,
  "requestId": "req_...",
  "error": {
    "code": "E_PROBUILDER_PACKAGE_NOT_FOUND",
    "message": "ProBuilder package is not installed in the target project.",
    "transient": false,
    "next_actions": [
      "Install the com.unity.probuilder package in the Unity project.",
      "Use unity_cli package install com.unity.probuilder --project <path>."
    ]
  }
}
```

The `next_actions` field provides concrete recovery steps specific to the error.

## Cross-Reference

- Target selection: [target-and-instance](./target-and-instance.md)
- Discovery: [tool-discovery](./tool-discovery.md)
- Error codes: [error-codes](./error-codes.md)
- Tool metadata schema: [../schemas/tool-metadata.schema.json](../schemas/tool-metadata.schema.json)
