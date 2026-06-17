# Targets and Instances

> **Discovery path**: `instances list → target set/select → proceed to tool discovery`

## What is a Unity Instance?

A **Unity instance** is a running Unity Editor process that has loaded the `com.unity_cli` bridge package. When the Editor compiles the package, `UnityCliBridge` starts a Windows loopback HTTP listener (default `http://127.0.0.1:47392`, probing later ports) and writes an instance registration file under `%LOCALAPPDATA%\UnityCli\instances\<instanceId>.json`.

Each instance registration contains:

| Field | Meaning |
|-------|---------|
| `instanceId` | Unique identifier for this Editor process |
| `projectPath` | Root path of the loaded Unity project |
| `projectHash` | Derived hash for quick matching |
| `processId` | Windows PID of the Unity Editor process |
| `bridgeEndpoint` | HTTP endpoint where `/bridge/execute` and `/health` are served |
| `status` | `ready`, `stale`, or `unreachable` |
| `editorState` | Current editor state snapshot (play mode, pause, etc.) |
| `lastSeenAt` | Timestamp of last heartbeat (refreshed every ~5 seconds) |
| `toolCatalogFingerprint` | Fingerprint of the tool catalog for this instance |

## Instance Status Lifecycle

| Status | Meaning | How to resolve |
|--------|---------|----------------|
| `ready` / `healthy` | Heartbeat recent, `/health` reachable | — |
| `stale` | Heartbeat older than the stale threshold (domain reload or Editor closed) | Reopen Unity Editor; run `instances prune` |
| `unreachable` | Registration present but `/health` not responding | Check if Editor process still running; run `instances prune` |

## Discovering Available Instances

```powershell
unity_cli instances list
```

Returns a list of all registered instances with their `instanceId`, `projectPath`, `processId`, `bridgeEndpoint`, and `status`.

```powershell
unity_cli instances prune
```

Removes stale and unreachable registrations. Stale process PIDs are attempted to be killed.

## Selecting a Target

Before executing any bridge tool, the CLI must know **which Unity instance** to target. There are several resolution strategies, in priority order:

### 1. Explicit `--target <id|alias>`

```powershell
unity_cli tools list --target <instanceId|alias>
unity_cli camera list --target <instanceId|alias>
```

The `--target` flag accepts an instance ID, an alias name, or a project hash prefix. This is the most deterministic resolution.

### 2. Active target (`target set`)

```powershell
unity_cli target set <instanceId|alias>
```

Stores the active target in `config.json`. Subsequent commands without `--target` will use this active target.

```powershell
unity_cli target get      # Show current active target
unity_cli target clear    # Clear active target
```

### 3. CWD match

If neither `--target` nor active target is set, the CLI looks for a ready instance whose `projectPath` contains the current working directory.

### 4. Single ready instance

If exactly one instance is `ready`, it is used automatically.

### 5. Ambiguous (error)

When multiple ready instances exist and no selector is provided, the CLI returns **`E_TARGET_AMBIGUOUS`** instead of guessing. The error includes `targets` listing each candidate so the caller can retry with `--target`.

### Target aliases

Aliases map human-readable names to instance IDs:

```powershell
unity_cli target alias set <instanceId> <alias>     # Create alias
unity_cli target alias list                           # List aliases
unity_cli target alias unset <alias>                  # Remove alias
```

Aliases are stored in `aliases.json` and persist across Editor restarts.

## Target Resolution and Tool Discovery

Target resolution happens **before** tool discovery. The resolved target determines:
- Which `bridgeEndpoint` is used for live Bridge commands
- Which project tools are discoverable (project tools are per-instance)
- Which tool availability fingerprint is checked

If no target is resolved and no `--bridge-endpoint` is provided, only offline and CLI-only tools are available in discovery.

## Key Principle

**The CLI never guesses the target.** When multiple ready instances exist, it returns `E_TARGET_AMBIGUOUS` with actionable `nextActions`. The caller (AI agent or user) must explicitly select a target before executing bridge tools.
