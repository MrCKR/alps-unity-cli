# UnityCLI

Windows-first Unity Editor automation CLI for AI agents. The executable is `unitycli`; the npm package is `@alpsckr/unitycli`; the Unity Package is `com.alpsckr.unitycli`.

## Requirements

- Windows
- Node.js 20 or newer
- Unity 2022.3 or newer
- A Unity project when installing the Bridge
- An opened Unity Editor only when using live Unity capabilities; installation does not start or stop Unity

## Install

Install the CLI once for the current user:

```powershell
npm install -g @alpsckr/unitycli
```

Start your AI in the directory where you normally work and tell it:

> 执行 unitycli init help，然后照做。

`init help` contains the complete AI installation protocol. The AI first runs read-only discovery, then asks you:

- which discovered Unity projects should receive the Bridge;
- whether the Skill target is generic Agent Skills (`.agents`) or Claude;
- whether the Skill belongs to one workspace or to the current user;
- which directory is your normal AI workspace when workspace scope is selected.

The AI does not silently choose these values. Multiple Bridge projects are installed one at a time. Installation does not start, close, restart, or terminate Unity.

## Manual Setup

Read the same protocol and inspect the current directory:

```powershell
unitycli init help
unitycli init discover
```

Install or repair the Bridge in one explicit Unity project:

```powershell
unitycli init package --project C:\path\UnityProject
unitycli init package --project C:\path\UnityProject --check
```

Install or check generic Agent Skills in one workspace:

```powershell
unitycli init skill --agent .agents --scope workspace --workspace C:\path\Workspace
unitycli init skill --agent .agents --scope workspace --workspace C:\path\Workspace --check
```

Install or check Claude Skills in one workspace:

```powershell
unitycli init skill --agent claude --scope workspace --workspace C:\path\Workspace
unitycli init skill --agent claude --scope workspace --workspace C:\path\Workspace --check
```

Install or check one user-level Skill:

```powershell
unitycli init skill --agent .agents --scope user
unitycli init skill --agent .agents --scope user --check

unitycli init skill --agent claude --scope user
unitycli init skill --agent claude --scope user --check
```

Workspace and user scope are separate choices. UnityCLI does not install both automatically. A workspace can be the parent of one or more Unity projects.

## Verify And Diagnose

Package and Skill checks are read-only:

```powershell
unitycli init package --project C:\path\UnityProject --check
unitycli init skill --agent .agents --scope workspace --workspace C:\path\Workspace --check
```

After the user opens or refreshes Unity, inspect registered Editor sessions:

```powershell
unitycli instances list
unitycli instances prune --instance C:\path\UnityProject
unitycli doctor --project C:\path\UnityProject
```

`instances list`默认隐藏offline记录；`instances list --all`仅用于展开审计。Unity启动、编译或重载时，live命令会在本次timeout内等待当前连接ready。旧格式、损坏或不可读记录会逐文件隔离，不阻断合法实例，并通过`invalidRecordCount`返回数量；当前项目由Unity自动恢复。需要清理历史记录时运行`instances prune --instance <id|projectPath|alias>`并检查`removed`、`remaining`和`deferred`，不要手工读取或删除内部文件。

To create a stable selector:

```powershell
unitycli target alias set C:\path\UnityProject my-project
unitycli target alias list
```

## First Unity Call

With the selected Unity project open and ready:

```powershell
unitycli tools group --instance C:\path\UnityProject
unitycli tools search 'editor|state' --instance C:\path\UnityProject
unitycli tools describe editor.state --instance C:\path\UnityProject
unitycli tools run editor.state --input '{}' --instance C:\path\UnityProject
```

Do not guess Tool names or inputs. Discover the current catalog, read one descriptor, then call that exact Tool. `command <name>` is a schema-argument projection of the same live catalog, not a second capability system.

Optional extensions use their dedicated control plane:

```powershell
unitycli extensions list --project C:\path\UnityProject
unitycli extensions install <id> --project C:\path\UnityProject
unitycli extensions remove <id> --project C:\path\UnityProject
unitycli extensions doctor --project C:\path\UnityProject
```

Discovery lists default to 20 items, allow at most 100, and continue with `pageInfo.nextCursor`:

```powershell
unitycli tools group list <group> --page-size 20 --instance <id|projectPath|alias>
unitycli tools search '<k1|k2|...>' --page-size 20 --instance <id|projectPath|alias>
unitycli command --project-path C:\path\UnityProject --page-size 20
```

In PowerShell, quote pipe-separated search keywords with single quotes.

## Output

Every command returns one JSON envelope. stdout is compact single-line JSON by default; use `--pretty` only for indented output.

Success:

```json
{"ok":true,"requestId":"req-...","data":{}}
```

Failure:

```json
{"ok":false,"requestId":"req-...","error":{"code":"...","message":"...","nextActions":[],"transient":false,"retryAfterMs":0}}
```

Follow structured `nextActions`. With `--timeout-ms T`, use a host budget of at least `2 * T + 5000` milliseconds and wait for the unitycli process to exit. `E_EXECUTION_TIMEOUT` means no trusted terminal result; timeout does not stop Unity effects or synchronous C# already running. Inspect business state before deciding whether to submit a new mutation.

## Update And Remove

Update the global CLI, then let the AI run the installation protocol again:

```powershell
npm install -g @alpsckr/unitycli@latest
unitycli init help
```

To remove a workspace or user Skill, delete only the selected `unitycli` Skill directory:

```text
<workspace>/.agents/skills/unitycli
<workspace>/.claude/skills/unitycli
~/.agents/skills/unitycli
~/.claude/skills/unitycli
```

To remove the managed Bridge, first inspect the current `package.bridge-remove` descriptor and follow it:

```powershell
unitycli tools describe package.bridge-remove --instance C:\path\UnityProject
```

Remove the global CLI with:

```powershell
npm uninstall -g @alpsckr/unitycli
```

## Documentation

- [Documentation index](docs/README.md)
- [Architecture and public boundaries](docs/architecture.md)
- [Local development](docs/development.md)
- [Tool discovery](docs/tool-discovery.md)
- [Tool execution](docs/tool-execution.md)
- [Command execution](docs/command-execution.md)
- [Target and instance selection](docs/target-and-instance.md)
- [Safety boundaries](docs/safety-boundaries.md)
- [Errors](docs/error-codes.md)
- [Extensions](docs/extensions.md)
- [Public AI Skill](skills/unitycli/SKILL.md)

## License

MIT
