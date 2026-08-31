# Architecture And Public Boundaries

## Product Boundary

UnityCLI is a Windows-first CLI, not an MCP server. Public top-level commands are frozen to:

```text
tools
command
init
extensions
target
instances
doctor
```

Unity capability domains such as scene, asset, camera, prefab, build, test, package, profiler, physics, and graphics are Tools in one live catalog. They are not top-level commands.

External control planes remain external: Unity Hub, Editor installation or launch, auth, Cloud, License, and proxy configuration are handled by their system or vendor interfaces.

## Layers

```text
AI
  -> unitycli process
CLI (TypeScript)
  -> catalog, descriptor, schema, routing, safety, package/Skill installers
Unity Bridge (C# package)
  -> listener, registry, completion, Unity main-thread handlers
Unity Editor or development Player
```

Every public process writes one JSON envelope and exits. Tool calls are direct and terminal; no public background-job protocol exists.

## Tool Catalog

`tools run <tool> --input <json>` and `command <name> <business-args>` share the same live descriptor, schema, dispatcher, safety metadata, completion contract, and result envelope.

A normal AI flow is:

```text
tools group/search
tools describe <tool>
tools run <tool> --input <json>
```

Descriptions expose the input and execution boundary required for one correct call. Internal output schemas and completion metadata validate terminal business data but are not duplicated into public discovery output.

## Installation Control Plane

Installation is split by ownership:

- `init package` owns one explicit Unity project's `Packages/com.alpsckr.unitycli` copy.
- `init skill` owns one explicit `.agents` or Claude workspace/user Skill target.
- `init discover` is a bounded, read-only filesystem observation.
- `init help` is the static, versioned AI setup protocol.

Bridge and Skill installation are separate effects. The CLI is non-interactive; the AI asks the user for required project, agent, scope, and workspace decisions.

The Bridge source is the npm-local canonical `unitypkg`. `contracts/package-files.json` fixes every managed path, size, SHA-256, and the complete tree hash. Installation validates the source before planning and again before effect, publishes through staging/rename, and proves the target from fresh filesystem readback.

UnityCLI carries no private Roslyn assemblies. `code.exec` invokes the current Unity Editor's bounded Mono/csc compiler subprocess. `contracts/package-assembly-closure.json` fixes this boundary and defines protected precompiled assembly names and allowed providers.

## Editor Sessions

Registry v3 publishes canonical project identity and listener lifecycle. Health v3 combines transport identity with a read-only main-thread pulse. Public Editor status is normalized to:

```text
ready
transitioning
blocked
unresponsive
unverifiable
offline
conflict
```

The current Unity writer replaces unusable prior state with its current identity and connection, then publishes `transitioning` before `ready`. CLI calls wait within their existing deadline when the selected Unity process is starting or changing connections. Historical cleanup remains writer-owned: automatic cleanup and `instances prune` share one globally serialized, bounded retention function.

A live mutation can continue across a reload only while the same Editor session owns the exact request. After the first mutation POST, a changed Editor session forbids redelivery. Timeout or disconnect before a trusted terminal response is outcome unknown.

## Safety And Authorization

Calling a public Tool or control-plane effect is authorization. `destructiveHint`, `sideEffects`, and `supportsDryRun` describe behavior; they do not add confirmation gates.

Read-only Tools do not write files or Unity state and do not trigger import, compile, domain reload, or Play Mode transitions.

Mechanism state and temporary files follow fixed, bounded roots:

- user-level: `%LOCALAPPDATA%\UnityCli\.temp\`
- Unity project: `Library\UnityCli\`

Dev Unity validation uses `%LOCALAPPDATA%\UnityCli-Dev` and must not change the formal root or unrelated Unity processes.

## Source Of Truth

- `AGENTS.md`: repository engineering constraints
- `contracts/`: generated and hand-governed machine contracts
- `schemas/`: public and internal JSON schemas
- `skills/unitycli/`: public AI Skill source
- `unitypkg/`: canonical Unity Bridge package
- `extensions/`: optional extension sources
- `docs/`: public operational and architectural documentation
