# Alps Unity CLI

Windows-first Unity CLI connection foundation for Alps.

This repository intentionally implements its own CLI Core, Local Agent state, and Unity Editor Bridge package. It does not use unity-mcp, raw MCP passthrough, or a forked MCP runtime as the product base.

## Commands

```bash
alps-unity status --project <unity-project>
alps-unity project info --project <unity-project>
alps-unity instance list
alps-unity instance current
alps-unity instance use <instanceId>
alps-unity agent start
```

All commands return stable JSON envelopes with `ok`, `requestId`, `logs`, `nextActions`, and either `data` or `error`.

## Local Agent and Bridge

The CLI starts a loopback Local Agent on demand under `%LOCALAPPDATA%/AlpsUnityCLI` or `ALPS_UNITY_HOME` when set. The agent exposes `GET /health` and `POST /bridge/heartbeat`; Unity Editor Bridge instances register heartbeat data through that local HTTP contract. If the agent cannot be started or reached, commands return `E_AGENT_UNAVAILABLE` with restart/log next actions. For tests and bridge integration, the CLI exposes:

```bash
alps-unity bridge register --project <unity-project> --unityVersion 2022.3.1f1 --bridgeVersion 1.0.0
```

The Unity package scaffold lives in `unity-package/` and posts Editor heartbeat details to the Local Agent, including bridge version, log path, compiling/importing/building/PlayMode/domain reload/Prefab Stage busy state. It also writes a bridge log for connection diagnostics.

## Errors

Connection-layer errors are structured and include next actions:

- `E_CONNECTION_FAILED`
- `E_INSTANCE_AMBIGUOUS`
- `E_INSTANCE_NOT_FOUND`
- `E_INSTANCE_PROJECT_MISMATCH`
- `E_PROJECT_NOT_FOUND`
- `E_AGENT_UNAVAILABLE`
- `E_BRIDGE_VERSION_UNSUPPORTED`

## Development

```bash
npm install
npm test
```

The test suite covers no instance, one busy instance, multiple instances, project mismatch, incompatible bridge version, `E_AGENT_UNAVAILABLE`, Local Agent heartbeat registration, and the CLI `bridge register` contract.

## Query commands

The first query batch returns stable JSON envelopes with `ok`, `requestId`, `mode`, `logs`, `nextActions`, and `data`.

- `alps-unity scene active --project <path>`
- `alps-unity scene hierarchy --project <path> --page-size 100 --cursor 0 --filter Camera`
- `alps-unity scene build-settings --project <path>`
- `alps-unity go find --project <path> --name Player --component Rigidbody`
- `alps-unity go inspect --project <path> --path Root/Player`
- `alps-unity asset search --project <path> --query Player --type Prefab --page-size 100`
- `alps-unity asset info --project <path> --path Assets/Prefabs/Player.prefab`
- `alps-unity editor console --project <path> --type error --count 50 --filter NullReference`
- `alps-unity editor console --project <path> --clear`

Live query commands require a registered Unity Editor Bridge instance for the selected project. Use `alps-unity status`, `alps-unity instance list`, and `alps-unity bridge register` for connection diagnostics.
