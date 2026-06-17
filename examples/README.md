# Examples

This directory contains runnable examples for the Unity CLI `tools run` unified entry point.

## Prerequisites

All examples assume:
- A Unity project with the Bridge package installed (`unity_cli init --check` passes)
- A running Unity Editor with the Bridge listening (check with `unity_cli doctor`)
- A target instance selected (`unity_cli target set <id>`)

## Example Index

| File | Category | Description |
|------|----------|-------------|
| `01-editor-tool-echo.md` | Editor tool | Run a built-in editor-domain tool via `tools run` |
| `02-editor-tool-camera.md` | Editor tool | List cameras and capture a screenshot via `tools run` |
| `03-runtime-tool-echo.md` | Runtime tool | Run a runtime-domain tool in Play Mode via `tools run` |
| `04-runtime-tool-verify.md` | Runtime tool | Execute runtime code and verify the result |
| `05-verify-workflow.md` | Verification | Full discovery → describe → run → verify workflow |
| `06-batch-editor-only.json` | Batch | Batch automation using only editor-domain tools |
| `07-batch-runtime-only.json` | Batch | Batch automation using runtime-domain tools in Play Mode |
| `08-batch-editor-plus-runtime.json` | Batch | Batch combining editor and runtime tools in sequence |
| `09-batch-parallel-readonly.json` | Batch | Parallel batch of read-only editor queries |

## Key Principles

- **All tool invocations use `tools run`** as the unified entry point.
- **No `custom-tool` commands** — project tools are discovered and invoked through `tools run`.
- **No standalone `runtime` command group** — runtime tools are accessed through `tools run` with `target=runtime` semantics discovered via `tools describe`.
- **Discovery first** — always run `tools list` or `tools search` before `tools run` to verify the tool exists and is available for the current target.
