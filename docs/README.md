# Unity CLI Documentation

> **Discovery path for AI agents**: `instances → select target → tools search/describe → tools run → verify`

This documentation follows the **target-first discovery** model. The only external entry point is the unified `tools` command surface. There is no independent `custom-tool` command group and no independent `runtime` command group; runtime capabilities are expressed through tool descriptors and internal router branches.

## Topic Index

| Topic | Title | Discovery Path |
|-------|-------|----------------|
| [target-and-instance](./target-and-instance.md) | Targets and Instances | Start here |
| [tool-discovery](./tool-discovery.md) | Tool Discovery | `instances → tools list/search/describe` |
| [tool-execution](./tool-execution.md) | Tool Execution | `tools describe → tools run → verify` |
| [availability-differences](./availability-differences.md) | Availability Differences | Why different instances show different tools |
| [error-codes](./error-codes.md) | Error Codes and next_actions | Structured error recovery |
| [risk-and-side-effects](./risk-and-side-effects.md) | Risk and Side Effects | `destructiveHint`, `readOnlyHint`, `--yes`, `--dry-run` |

## v1.0 Archive (Engineering Reference Only)

The following documents are **v1.0 engineering archives**, not current user-side guides. They contain pre-P3 legacy terminology (e.g. `custom-tool` command group, independent `runtime` command references). **Do not use them as AI calling guides**; use the topic index above instead.

| Archive | Title | Scope |
|---------|-------|-------|
| [coverage-matrix](./coverage-matrix.md) | Coverage Matrix (v1.0 Archive) | P0-P2 function domain coverage status — contains legacy `project-tool list/run` and pre-unified-command terminology |
| [risk-register-v1.0](./risk-register-v1.0.md) | Risk Register (v1.0 Archive) | P0-P2 known risks and limitations — contains pre-P3 framing |

## Reading Order for AI Agents

1. **`target-and-instance`** — Learn what a Unity instance is, how to discover available instances, and how to select a target before any tool call.
2. **`tool-discovery`** — Learn the unified `tools list/search/describe` surface, how tool metadata reveals `handler`, `supportStatus`, `target`, `availabilityScope`, `inputSchema`, and risk context.
3. **`tool-execution`** — Learn how to execute tools via `tools run <name>`, how `target=editor|runtime|both` affects execution routing, and how to verify results.
4. **`availability-differences`** — Understand why different Unity instances may show different tool availability (installed packages, editor state, PlayMode session scope).
5. **`error-codes`** — Read the structured error taxonomy, `next_actions` recovery suggestions, and `transient` / `retry_after_ms` fields.
6. **`risk-and-side-effects`** — Understand `destructiveHint`, `readOnlyHint`, `supportsDryRun`, the `--yes` confirmation gate, and `plannedEffects` previews.

## Cross-References

- **Skill**: `/skills/unity_cli/SKILL.md` ([ALP-417](https://github.com/MrCKR/alps-unity-cli/tree/agent/agent/8c59c737/skills/unity_cli/SKILL.md) on branch `agent/agent/8c59c737`) teaches the same discovery path in a compact, agent-ready format. See that file for the canonical AI skill.
- **Examples**: `/examples/` ([ALP-419](https://github.com/MrCKR/alps-unity-cli/tree/agent/agent/c7f7740a/examples/) on branch `agent/agent/c7f7740a`) contains 9 runnable examples covering editor tool, runtime tool, verify workflow, and batch scenarios. Key files:
  - `examples/01-editor-tool-echo.md` — Editor tool via `tools run`
  - `examples/03-runtime-tool-echo.md` — Runtime tool via `tools run`
  - `examples/05-verify-workflow.md` — Full discovery → run → verify cycle
  - `examples/08-batch-editor-plus-runtime.json` — Batch combining editor + runtime
  - See `examples/README.md` for the full index
- **Schemas**: `/schemas/tool-metadata.schema.json`, `/schemas/error-code.schema.json`, `/schemas/response-envelope.schema.json` define the contract shapes that `tools describe` and error responses follow. See [tool-discovery](./tool-discovery.md) §Descriptor field coverage for the current vs. P3 contract gap.
