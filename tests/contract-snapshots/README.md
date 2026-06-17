# Contract Snapshots

This directory contains JSON contract snapshots for key C# bridge commands.
These snapshots capture the expected field names, types, and structure of
command output JSON, enabling before/after comparison when migrating from
hardcoded JSON string concatenation to `UnityCliJsonWriter` builder pattern.

## How to verify

Use these snapshots as review fixtures when changing command output serialization.
Compare changed command output against the matching snapshot and update the
snapshot only when a contract change is intentional and reviewed.

## Snapshot files

Each `.json` file in this directory represents the expected JSON contract
for a specific command's `data` payload (inside the `{"ok":true,"data":...}` envelope).

Key contracts:
- `scene-active.json` — scene.active
- `scene-build-settings.json` — scene.build-settings
- `scene-hierarchy.json` — scene.hierarchy
- `editor-state.json` — editor.state
- `editor-console.json` — editor.console
- `console-items.json` — console items sub-structure
