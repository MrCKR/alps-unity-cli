# Unity CLI Documentation

> **Discovery path for AI agents**: `instances → select target → tools discovery → tools describe → execute → verify`

This documentation follows the **target-first discovery** model. The only external entry point is the unified `tools` command surface. There is no independent `custom-tool` command group and no independent `runtime` command group; runtime capabilities are expressed through tool descriptors and internal router branches.

## Topic Index

| Topic | Title | Discovery Path |
|-------|-------|----------------|
| [target-and-instance](./target-and-instance.md) | Targets and Instances | Start here |
| [tool-discovery](./tool-discovery.md) | Tool Discovery | `instances → tools list/search/describe` |
| [tool-execution](./tool-execution.md) | Tool Execution | `tools describe → execute → verify` |
| [availability-differences](./availability-differences.md) | Availability Differences | Why different instances show different tools |
| [error-codes](./error-codes.md) | Error Codes and nextActions | Structured error recovery |
| [risk-and-side-effects](./risk-and-side-effects.md) | Risk and Side Effects | `destructiveHint`, `readOnlyHint`, `--yes`, `--dry-run` |
| [coverage-matrix](./coverage-matrix.md) | Coverage Matrix | Function domain coverage status |

## Reading Order for AI Agents

1. **`target-and-instance`** — Learn what a Unity instance is, how to discover available instances, and how to select a target before any tool call.
2. **`tool-discovery`** — Learn the unified `tools list/search/describe` surface, how tool metadata reveals `handler`, `supportStatus`, `inputSchema`, and `target` context.
3. **`tool-execution`** — Learn how to execute tools via CLI commands or the future `tools run`, how `Target=editor|runtime|both` affects execution routing, and how to verify results.
4. **`availability-differences`** — Understand why different Unity instances may show different tool availability (installed packages, editor state, PlayMode session scope).
5. **`error-codes`** — Read the structured error taxonomy, `nextActions` recovery suggestions, and `transient` / `retry_after_ms` fields.
6. **`risk-and-side-effects`** — Understand `destructiveHint`, `readOnlyHint`, `supportsDryRun`, the `--yes` confirmation gate, and `plannedEffects` previews.

## Cross-References

- **Skill**: The `/skills/unity_cli` user-facing skill teaches the same discovery path in a compact, agent-ready format. See [skill](../skills/) for the skill content.
- **Examples**: T1.3 examples are referenced in `tool-discovery` and `tool-execution` topics with concrete command sequences.
- **Schemas**: `/schemas/tool-metadata.schema.json`, `/schemas/error-code.schema.json`, `/schemas/response-envelope.schema.json` define the contract shapes that `tools describe` and error responses follow.
