# alps-unity-cli

Windows-first npm CLI foundation for Alps Unity automation.

## Requirements

- Windows shell environment
- Node.js 20 or newer
- npm global install support

## Install

```powershell
npm install -g alps-unity-cli
alps-unity --help
```

For local development:

```powershell
npm install
npm test
node ./bin/alps-unity.js --help
```

## Command shape

```powershell
alps-unity <domain> <verb> [options]
```

Registered domains for the first CLI foundation are:

- `connect`
- `query`
- `edit`
- `job`
- `automation`

This foundation registers command entry points only. Unity business behavior is intentionally not implemented in this batch.

## Global options

- `--project <path>`: Unity project path, normalized with Windows path rules.
- `--instance <id>`: Unity instance identifier.
- `--format <text|json>`: Output format. Defaults to `text`.
- `--timeout <ms>`: Positive integer timeout in milliseconds.
- `--verbose`: Include verbose diagnostics flag in runtime context.
- `-h, --help`: Show root or domain help.

Invalid global arguments return `E_INVALID_ARGS` and exit code `2`.

## JSON envelope

With `--format json`, success responses use:

```json
{
  "ok": true,
  "requestId": "...",
  "command": "...",
  "data": {},
  "diagnostics": {
    "requestId": "...",
    "logDirectory": "C:\\Users\\<user>\\AppData\\Local\\AlpsUnityCli\\logs",
    "verbose": false
  }
}
```

Errors use:

```json
{
  "ok": false,
  "requestId": "...",
  "command": "...",
  "error": {
    "code": "E_INVALID_ARGS",
    "message": "...",
    "details": {}
  },
  "diagnostics": {
    "requestId": "...",
    "logDirectory": "C:\\Users\\<user>\\AppData\\Local\\AlpsUnityCli\\logs",
    "verbose": false
  }
}
```

## Windows logs and diagnostics

The default diagnostic log directory is:

```text
%LOCALAPPDATA%\AlpsUnityCli\logs
```

`requestId` and the log path are observability fields only. They do not enable risk controls, confirmation prompts, or high-risk command blocking.
