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
tools group/search       # direct catalog control plane
tools describe <tool>    # exact-name descriptor projection
tools run <tool> --input <json>
```

Descriptions expose the input and execution boundary required for one correct call. Internal output schemas and completion metadata validate terminal business data but are not duplicated into public discovery output. Each Unity domain publishes one immutable executable catalog slot; its descriptor-only projection is derived during the same publication and cannot expose invokers, CLR types, writable metadata, or a second catalog cache. A single candidate, duplicate name or provider fault is isolated inside that one publication and reported by a separate read-only health snapshot; only a shared discovery-infrastructure failure aborts the catalog, leaving the prior slot intact.

## Installation Control Plane

Installation is split by ownership:

- `init package` owns one explicit Unity project's `Packages/com.alpsckr.unitycli` copy.
- `init skill` owns one exact user-selected Skill directory or one `.agents`/Claude preset target.
- `init discover` is a bounded, read-only current-project installation plan with exact Editor running state.
- `init help` is the static, versioned AI setup protocol.

Bridge and Skill installation are independent effects. The CLI is non-interactive; the AI preserves explicit user targets, infers the unique current project/workspace, and asks only unresolved decisions. A running Editor is a normal installation state: the Bridge copy is replaced in place through a staging-directory rename, so installation never requires closing Unity. Closing is an explicit fallback that requires the install-scoped `--close-editor` plus separate user approval, and only after a reported package-directory lock; force termination is never used.

The Bridge source is the npm-local canonical `unitypkg`. `contracts/package-files.json` fixes every managed path, size, SHA-256, and the complete tree hash. Installation validates the source before planning and again before effect, publishes through staging/rename, and proves the target from fresh filesystem readback.

UnityCLI carries no private Roslyn assemblies. `debug.code-execute` and live method-hook callbacks share one dynamic code compiler owner that invokes the current Unity Editor's bounded Mono/csc subprocess, keeps one active compilation at a time, and always names its fixed temporary files under `Library/UnityCli/code-exec-temp`. `contracts/package-assembly-closure.json` fixes this boundary and the single bundled precompiled assembly that the package must own. `UnityCli.Editor` keeps `overrideReferences: true` with no precompiled project reference: the Bridge never binds Newtonsoft types at compile time and resolves them by assembly identity at runtime, so the project or the Editor decides which provider supplies JSON.NET.

The bundled Harmony dependency is declared in the same closure contract together with the Editor-only hook backend asmdef, its exact precompiled reference, its references and its Harmony owner id. The backend assembly is not auto-referenced by project assemblies and contributes no public Tool: it only attaches and removes this CLI's own method patches. A missing or replaced dependency fails closed for the hook capability and does not take down the listener, the Tool catalog or state observation.

## Live Method Hooks

The `debug` group's hook family installs, replaces, enables, disables and removes definitions, plus read-only list, info and bounded event reads. It reuses the existing Tool catalog, dispatcher, admission, completion and envelope; no separate transport or second descriptor exists.

Domain vocabulary is fixed in `CONTEXT.md`. Four boundaries follow from it:

- A Hook Definition is session-scoped intent, not a patch. Definitions carry an exact target key and are discarded when the Editor session changes, so a restart cannot resume previous arbitrary-code authorization.
- A Hook Attachment exists only in the current code domain. Every new domain re-resolves the exact method and re-attaches with the earliest possible initialization, and callbacks that need compilation are recompiled against the current reference identities.
- A Hook Invocation is one intercepted call. Installing a definition and observing a call are different facts, and a managed call site compiled before the patch keeps the original behaviour.
- A Hook Event is a bounded business record with its own cursor semantics. Events are evicted by count and byte budgets, gaps are reported explicitly, and event reads never delete records or consume another reader's cursor.

Definitions, faults and buffered events are checkpointed by the single debug store owner into fixed files under `Library\UnityCli\debug\`, published through the shared atomic replacement helper and accepted by a later domain only for the same Editor session and a matching payload checksum. Recovery suppression and the event sequence floor live in debug-domain session state keys that record the Editor session that wrote them, so they survive a domain reload within one Editor start and disappear with the process. A recipe export is a read-only snapshot of the current definition publication: it writes no file, later cursors read the same snapshot, and the session's earlier snapshot is replaced and its cursors invalidated by the next export.

A hook hit never occupies a request. Installing a definition releases admission, the mutation lease and the request cancellation token as soon as its observed terminal is published, and later callbacks run under the definition's own authorization rather than a completed request.

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

The current Unity writer replaces unusable prior state with its current identity and connection, then publishes `transitioning` before `ready`. CLI calls wait within their existing deadline when the selected Unity process is starting or changing connections. Live catalog discovery freezes exact session/listener provenance for the top-level invocation and root admission; a changed generation before the first business POST fails closed. Historical cleanup remains writer-owned: automatic cleanup and `instances prune` share one globally serialized, bounded retention function.

A live mutation can continue across a reload only while the same Editor session owns the exact request. After the first mutation POST, a changed session forbids redelivery. Timeout or disconnect before a trusted terminal response is outcome unknown.

### Main-Thread Pause Control Channel

A `pause` breakpoint stops the hit thread at its own call boundary. When that thread is the main thread, the normal Bridge queue can no longer take execution, so every business request would wait until its deadline. The pause control channel exists to keep the Editor inspectable and releasable instead:

- The channel is published at bridge startup, before the listener accepts requests and therefore before any pause resource can be installed. A pause breakpoint is refused with `E_DEBUG_CONTROL_PLANE_UNAVAILABLE` while it is unpublished, because such a site would have no reachable release path.
- While a main-thread pause is published, an incoming request is classified by a frozen allowlist. Reachable Read tools, the pause tools, discovery/describe, and `pauseId`-scoped value operations are served on the requesting thread itself, and their success reply is the same `result`-wrapped terminal the normal `catalog.editor.run` path produces, so the CLI projects it identically. Everything else returns `E_DEBUG_MAIN_THREAD_PAUSED` before any effect, with `pauseId`, `timeoutKind`, `remainingMs` and resume/list routing.
- The channel is the only thing the paused thread services: a bounded on-site mailbox that executes one `inspect`, `evaluate` or `pauseId`-scoped value operation at a time on the paused thread and then returns to waiting. It never pumps Unity messages, business frames or general CLI mutations, and it registers no update callback.
- Release and the bounded timeout never depend on the normal Editor update: the paused thread polls its own signal, so resume is never queued behind waiting requests. Only the paused thread's own code returning can end a running synchronous evaluation; otherwise the caller gets `pending` and the site stays held.
- The single compiler owner advances csc on the paused thread through the `PauseService` lane, which is mutually exclusive with the main-thread tick lane. If the compiler is temporarily owned elsewhere, the evaluation returns `E_UNITY_BUSY` within a bounded budget, keeps the site, and still allows `inspect` and `resume`.

The pause publication, transport identity and the normal main-thread pulse are three separate pieces of evidence. Health reports the current live pause publication under `mainThread.debugPause`; the pause never writes the pulse, and no file is ever used to claim a deliberate pause.

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
- `skills/unitycli/`: public AI Skill runtime projection (`SKILL.md`, `references/`, `scripts/`)
- `skill-factory/unitycli/`: Skill governance assets that never install (`manifest.json`, `agents/`, `evals/`, `examples/`, `reports/`, `security/`)
- `unitypkg/`: canonical Unity Bridge package
- `extensions/`: optional extension sources
- `docs/`: public operational and architectural documentation
