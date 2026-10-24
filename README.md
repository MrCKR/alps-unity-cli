# UnityCLI

Windows-first Unity Editor automation CLI for AI agents. The executable is `unitycli`; the npm package is `@alpsckr/unitycli`; the Unity Package is `com.alpsckr.unitycli`.

## Requirements

- Windows
- Node.js 20 or newer
- Unity 2022.3 or newer
- A Unity project when installing the Bridge
- An opened Unity Editor only when using live Unity capabilities; installation may normally close only the exact target Editor after explicit user approval

## Install

Install the CLI once for the current user:

```powershell
npm install -g @alpsckr/unitycli
```

Start your AI in the directory where you normally work and tell it:

> 执行 unitycli init help，然后照做。

`init help` contains the complete AI installation protocol. Read-only discovery uses the unique current Unity project and workspace without asking you to repeat them, reports the installed and available Bridge versions, and accepts any exact Skill directory you already named. It asks only when a real choice remains, such as which of several discovered Unity projects receives the Bridge. An open Unity Editor is a normal installation state: the Bridge is replaced in place. Closing is a fallback: only after the publish reports a real directory lock may the AI normally close that exact project, and only after the user approves it. It never force-terminates Unity or decides an unsaved-scene prompt.

## Manual Setup

Read the same protocol and inspect the current directory:

```powershell
unitycli init help
unitycli init discover
```

Install or repair the Bridge in one explicit Unity project. Keep the Editor open: an existing package is converged file by file in place, and its directory is never renamed. A blocked file is reported by path, and rerunning the same command converges it. `--close-editor` stays available for an operator's own decision; the installation protocol never asks for a close:

```powershell
unitycli init package --project C:\path\UnityProject
unitycli init package --project C:\path\UnityProject --close-editor
unitycli init package --project C:\path\UnityProject --check
```

Install or check an exact user-selected Skill directory, including Pi:

```powershell
unitycli init skill --target C:\path\Workspace\.pi\skills\unitycli
unitycli init skill --target C:\path\Workspace\.pi\skills\unitycli --check
```

Generic Agent Skills remain available as a preset:

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

Preset workspace and user scope are separate choices. An exact `--target` is final user intent and cannot be combined with preset agent/scope options. Bridge and Skill are independently authorized effects; failure of one does not invalidate or block the other.

## Verify And Diagnose

Package and Skill checks are read-only:

```powershell
unitycli init package --project C:\path\UnityProject --check
unitycli init skill --target C:\path\Workspace\.pi\skills\unitycli --check
unitycli init skill --agent .agents --scope workspace --workspace C:\path\Workspace --check
```

After the user opens or refreshes Unity, inspect registered Editor sessions:

```powershell
unitycli instances list
unitycli instances prune --instance C:\path\UnityProject
unitycli doctor --project C:\path\UnityProject
```

`instances list`默认隐藏offline记录；`instances list --all`仅用于展开审计。Unity启动、编译或重载时，live命令会在本次timeout内等待当前连接ready。旧格式、损坏或不可读记录会逐文件隔离，不阻断合法实例，并通过`invalidRecordCount`返回数量；当前项目由Unity自动恢复。需要清理历史记录时运行`instances prune --instance <name|alias|projectPath>`并检查`removed`、`remaining`和`deferred`，不要手工读取或删除内部文件。

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

Do not guess Tool names or inputs. Discover the current catalog, read one descriptor, then call that exact Tool. `command <name>` is a schema-argument projection of the same live catalog, not a second capability system. 调用方明确要求的业务值优先于schema default和example值；不要把default或example当作连通性、probe或smoke调用。

Optional extensions use their dedicated control plane:

```powershell
unitycli extensions list --project C:\path\UnityProject
unitycli extensions install <id> --project C:\path\UnityProject
unitycli extensions remove <id> --project C:\path\UnityProject
unitycli extensions doctor --project C:\path\UnityProject
```

Discovery lists default to 20 items, allow at most 100, and continue with `pageInfo.nextCursor`:

```powershell
unitycli tools group list <group> --page-size 20 --cursor <nextCursor> --instance <name|alias|projectPath>
unitycli tools search '<k1|k2|...>' --page-size 20 --cursor <nextCursor> --instance <name|alias|projectPath>
unitycli command --project-path C:\path\UnityProject --page-size 20 --cursor <nextCursor>
```

In PowerShell, quote pipe-separated search keywords with single quotes.

## Live Method Hooks

The `debug` group hosts the live interceptor tools: `debug.hook-*` installs interceptors on an exact managed method of the current Editor domain before a Play run, observes calls, modifies behaviour, or executes bounded C# callbacks, and keeps those definitions across Play sessions and domain reloads within one Editor session:

```powershell
unitycli command debug.hook-install --label ai-movement-order --target '{"assemblyName":"Assembly-CSharp","typeName":"Game.AiController","methodName":"BeginMovement","parameterTypes":[],"returnType":"System.Void","isStatic":false}' --capture '{"arguments":true}' --instance C:\path\UnityProject
unitycli command debug.hook-list --instance C:\path\UnityProject
unitycli command debug.events-read --hook-id hook-0123456789abcdef --instance C:\path\UnityProject
```

Boundaries worth knowing before using the group:

- Definitions belong to one Editor session and are discarded on Editor restart; installing a hook is not persistent authorization for arbitrary code.
- Call sites compiled before the patch keep the original behaviour, so an installed definition can legitimately produce no hits. Use `debug.events-read` or a real business readback to prove a hit.
- Callback source is compiled on demand through the editor-bundled C# compiler and loaded into the current domain; this is not a project compile, but it costs a compiler subprocess and cannot be unloaded individually.
- `disable` and `remove` stop future dispatch and restore the original behaviour; they do not roll back effects that earlier invocations already produced.

`unitycli tools describe debug.hook-install` documents the exact target identity, capture options, callback context API, resource budgets and error recovery for the selected instance. Pure observation with capture options starts no compiler, because a definition without callback source does not need one.

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

Follow structured `nextActions`. With `--timeout-ms T`, use a host budget of at least `2 * T + 5000` milliseconds and wait for the unitycli process to exit. `E_EXECUTION_TIMEOUT` means no trusted terminal result; timeout does not stop Unity effects or synchronous C# already running. For `asset.refresh`, player build, Play Mode, Package installation (including exact version changes), Package removal, and `test.run`, discover the matching domain `.status` Tool and pass the original root `requestId`; status reads the same-session active phase or exact terminal without replaying the effect. Otherwise inspect business state before deciding whether to submit a new mutation.

## Update And Remove

Update the global CLI, then let the AI run the installation protocol again:

```powershell
npm install -g @alpsckr/unitycli@latest
unitycli init help
```

To remove a workspace or user Skill, delete only the selected `unitycli` Skill directory:

```text
<explicit-target>/unitycli
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
