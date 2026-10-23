# Local Development

## Requirements

- Windows
- Node.js 20 or newer
- PowerShell 7
- Unity 2022.3 for the Dev validation project

## Build And Test

```powershell
npm install
npm run build:test
npm test
npm run architecture:validate
```

`build:test` regenerates extension projections, TypeScript registries, completion contracts, command contracts, protocol output, Tool schemas, and the canonical Package file manifest before compiling production and test code.

Package integrity checks can be run directly:

```powershell
npm run package-closure:validate
npm run init-contract:validate
```

The Package closure audit enforces:

- exact canonical `unitypkg` file paths, sizes, per-file SHA-256, and tree hash;
- zero package-owned Roslyn DLLs;
- Unity-provided precompiled references;
- the Unity Editor compiler path used by `debug.code-execute`;
- bounded assembly-conflict policy.

## Run The Source CLI

```powershell
node dist/cli.js --help
node dist/cli.js init help
node dist/cli.js init discover
```

For global local development:

```powershell
npm link
unitycli --version
```

## Dev Unity Isolation

Only `Dev/UnityProject` is controlled by automated validation. Launch both Unity and the source CLI with the process-local environment variable:

```powershell
$env:UNITYCLI_DATA_DIR = Join-Path $env:LOCALAPPDATA 'UnityCli-Dev'
```

Do not set this variable globally. Before and after validation, record:

- the formal `%LOCALAPPDATA%\UnityCli` file set;
- existing Unity process IDs and project command lines;
- Dev registry and temporary files;
- every changed Dev fixture and its restoration state.

Use the current source installation route before Unity validation:

```powershell
$project = (Resolve-Path 'Dev/UnityProject').Path
node dist/cli.js init package --project $project
node dist/cli.js init package --project $project --check
```

## Validation Order

1. Finish source, tests, contracts, generated outputs, public Skill, and docs.
2. Run focused build, type checks, tests, and contract audits.
3. Run the affected isolated Dev Unity matrix.
4. Audit the final diff and npm artifact.

A failed validation step stops the current validation stage. Classify it as product, harness, environment, specification, or test-scope failure; fix the owner and rerun every affected check fresh.

## Packaging

Inspect the npm artifact without publishing:

```powershell
npm pack --dry-run --json
```

The artifact must contain the canonical `unitypkg`, contracts, schemas, public Skill source, docs, and README. Publishing, tagging, pushing, installing a release artifact, or changing versions requires the repository release procedure and explicit user authorization.
