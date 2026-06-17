# Unity CLI — 功能域覆盖矩阵

> 格式：unity-mcp 功能域/action → unity_cli 命令 → UnityPkg handler → metadata/describe → 测试证据 → 状态

冻结来源：[ALP-164] 冻结版 v1.0 §功能迁移与覆盖矩阵

状态说明：
- ✅ 完整链路（命令 + handler + metadata + 测试证据）
- 🔶 部分完成（CLI 命令 + handler 可用，metadata/测试待补）
- ⬜ 未实现

---

## Editor 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| editor.state | `editor state` | bridge (UnityCliBridge) | ✅ `editor.state` in EDITOR_TOOLS | tools.test.ts: editor group has all required tools | ✅ |
| editor.play | `editor play` | bridge (UnityCliBridge) | ✅ `editor.play` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.pause | `editor pause` | bridge (UnityCliBridge) | ✅ `editor.pause` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.stop | `editor stop` | bridge (UnityCliBridge) | ✅ `editor.stop` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.menu | `editor menu` | bridge (UnityCliBridge) | ✅ `editor.menu` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.console | `editor console` | bridge (UnityCliBridge) | ✅ `editor.console` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.refresh | `editor refresh [--compile]` | job (UnityCli.UnityCliBatch.Refresh) | ✅ `editor.refresh` in EDITOR_TOOLS (S2.3 新增) | tools.test.ts: editor.refresh metadata | ✅ |

## Package 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| package.list | `package list` | offline (readSnapshot) | ✅ `cli.package.list` in CLI_ONLY_TOOLS | cli.test.ts: package list | ✅ |
| package.info | `package info` | offline (packageInfo) | ✅ `cli.package.info` in CLI_ONLY_TOOLS | cli.test.ts | ✅ |
| package.search | `package search` | offline (searchRegistry) | ✅ `cli.package.search` in CLI_ONLY_TOOLS | cli.test.ts | ✅ |
| package.install | `package install` | hybrid (manifest + resolve job) | ✅ `cli.package.install` in CLI_ONLY_TOOLS | cli.test.ts | ✅ |
| package.remove | `package remove` | hybrid (manifest + resolve job) | ✅ `cli.package.remove` in CLI_ONLY_TOOLS | cli.test.ts | ✅ |
| package.resolve | `package resolve` | hybrid (Unity batch resolve) | ✅ `cli.package.resolve` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: cli.package.resolve metadata | ✅ |
| package.registry | `package registry get/set/clear` | offline (manifest read/write) | ✅ `cli.package.registry` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: cli.package.registry metadata | ✅ |
| package.scoped-registry | `package scoped-registry list/add/remove` | offline (manifest read/write) | ✅ `cli.package.scoped-registry` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: cli.package.scoped-registry metadata | ✅ |

## Test 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| test.run | `test run [--platform EditMode/PlayMode]` | job (Unity -runTests) | ✅ `test.run` in CLI_ONLY_TOOLS (S2.3 新增); describe includes cancel semantics (S3.5) | tools.test.ts: test.run metadata | ✅ |
| test.poll | `test poll --id <jobId>` / `test status --id <jobId>` | offline (job status) | ✅ `test.poll` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: test.poll metadata | ✅ |
| test.logs | `test logs --id <jobId>` | offline (job logs) | ✅ `test.logs` in CLI_ONLY_TOOLS (S3.5 新增) | tools.test.ts: test/build status logs cancel metadata | ✅ |
| test.cancel | `test cancel --id <jobId>` | offline (job cancel) | ✅ `test.cancel` in CLI_ONLY_TOOLS; describe includes cancel semantics (S3.5 新增) | tools.test.ts: test/build status logs cancel metadata | ✅ |

## Build 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| build.run | `build run [--target] [--output]` | job (UnityCli.UnityCliBatch.BuildProject) | ✅ `build.run` in CLI_ONLY_TOOLS (S2.3 新增); describe includes cancel semantics (S3.5) | tools.test.ts: build.run metadata | ✅ |
| build.status | `build status --id <jobId>` | offline (job status) | ✅ `build.status` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.status metadata | ✅ |
| build.logs | `build logs --id <jobId>` | offline (job logs) | ✅ `build.logs` in CLI_ONLY_TOOLS (S3.5 新增) | tools.test.ts: test/build status logs cancel metadata | ✅ |
| build.cancel | `build cancel --id <jobId>` | offline (job cancel) | ✅ `build.cancel` in CLI_ONLY_TOOLS; describe includes cancel semantics (S2.3/S3.5) | tools.test.ts: test/build status logs cancel metadata | ✅ |

## Asset + Prefab 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| asset.search | `asset search <keyword>` | bridge (UnityCliBridge) | ✅ `asset.search` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.info | `asset info <path>` | bridge (UnityCliBridge) | ✅ `asset.info` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.create | `asset create <path> [--type folder\|file]` / `asset mkdir <path>` | bridge (UnityCliBridge) | ✅ `asset.create` in ASSET_TOOLS | bridge-commands.test.ts | ✅ merge |
| asset.import | `asset import <path> [--source file]` | bridge (UnityCliBridge) | ✅ `asset.import` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.modify | `asset modify <path> --content-file <file>` | bridge (UnityCliBridge) | ✅ `asset.modify` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.delete | `asset delete <path>` | bridge (UnityCliBridge) | ✅ `asset.delete` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.duplicate | `asset duplicate <path> --to <path>` | bridge (UnityCliBridge) | ✅ `asset.duplicate` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.move | `asset move <path> --to <path>` | bridge (UnityCliBridge) | ✅ `asset.move` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.rename | `asset rename <path> --name <name>` | bridge (UnityCliBridge) | ✅ `asset.rename` in ASSET_TOOLS | bridge-commands.test.ts | ✅ rename |
| prefab.info | `prefab info <path>` | bridge (UnityCliBridge) | ✅ `prefab.info` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.hierarchy | `prefab hierarchy <path>` | bridge (UnityCliBridge) | ✅ `prefab.hierarchy` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.create | `prefab create <source> --path <path>` | bridge (UnityCliBridge) | ✅ `prefab.create` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.modify | `prefab modify <path> [--name] [--add-child] [--remove-child]` | bridge (UnityCliBridge) | ✅ `prefab.modify` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ merge |
| prefab.stage-open | `prefab stage-open <path>` | bridge (UnityCliBridge) | ✅ `prefab.stage-open` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.stage-save | `prefab stage-save [path]` | bridge (UnityCliBridge) | ✅ `prefab.stage-save` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.stage-close | `prefab stage-close [--save]` | bridge (UnityCliBridge) | ✅ `prefab.stage-close` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |

## Script 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| script.create | `script create <path> --content-file <file>` | bridge (UnityCliBridge) | ✅ `script.create` in SCRIPT_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |
| script.read | `script read <path>` | bridge (UnityCliBridge) | ✅ `script.read` in SCRIPT_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |
| script.edit | `script edit <path> [--patch/--find/--replace]` | bridge (UnityCliBridge) | ✅ `script.edit` in SCRIPT_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |
| script.validate | `script validate <path>` | bridge (UnityCliBridge) | ✅ `script.validate` in SCRIPT_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |

## Code 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| code.exec | `code exec (--file\|--code\|--stdin) [--channel live\|batchmode] [--job] [--artifact-dir]` | hybrid (bridge live + batch job) | ✅ `code.exec` in CODE_TOOLS, destructiveHint=true, permissions=["executes_code"], describe includes live/job cancel semantics (S2.4/S3.5 增强) | runtime-code.test.ts: live + batchmode + artifact + timeout | ✅ |

## Graphics 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| graphics.quality | `graphics quality [--quality-level] [--v-sync-count]` | bridge (UnityCliBridge) | ✅ `graphics.quality` in GRAPHICS_TOOLS | bridge-commands.test.ts: graphics forwarding; tools.test.ts: S3.2 graphics group | ✅ |
| graphics.settings | `graphics settings [--anti-aliasing] [--shadow-distance] [--render-pipeline]` | bridge (UnityCliBridge) | ✅ `graphics.settings` in GRAPHICS_TOOLS | bridge-commands.test.ts: renderPipeline forwarding | ✅ |
| graphics.pipeline | `graphics pipeline` | bridge (UnityCliBridge) | ✅ `graphics.pipeline` in GRAPHICS_TOOLS | bridge-commands.test.ts: graphics.pipeline forwarding; camera-bridge-source.test.ts | ✅ |
| graphics.volume | `graphics volume [target]` | bridge (UnityCliBridge) | ✅ `graphics.volume` in GRAPHICS_TOOLS | bridge-commands.test.ts: graphics.volume forwarding; camera-bridge-source.test.ts | ✅ |
| graphics.screenshot | `graphics screenshot <path> [--include-diagnostics]` | bridge (UnityCliBridge) | ✅ `graphics.screenshot` in GRAPHICS_TOOLS | bridge-commands.test.ts: graphics screenshot forwarding | ✅ |

## Physics 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| physics.settings | `physics settings [--mode 3d\|2d] [--gravity-y]` | bridge (UnityCliBridge) | ✅ `physics.settings` in PHYSICS_TOOLS | bridge-commands.test.ts: physics settings forwarding; tools.test.ts: S3.2 physics group | ✅ |
| physics.raycast | `physics raycast [--origin-*] [--direction-*] [--distance]` | bridge (UnityCliBridge) | ✅ `physics.raycast` in PHYSICS_TOOLS | bridge-commands.test.ts: physics raycast forwarding | ✅ |
| physics.simulation | `physics simulation [--steps] [--delta-time] [--simulate]` | bridge (UnityCliBridge) | ✅ `physics.simulation` in PHYSICS_TOOLS | bridge-commands.test.ts: physics simulation forwarding | ✅ |

## Animation/VFX 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| animation.clip | `animation clip <Assets/Clip.anim> [--create]` | bridge (UnityCliBridge) | ✅ `animation.clip` in ANIMATION_TOOLS | bridge-commands.test.ts: animation forwarding; tools.test.ts: S3.2 animation group | ✅ |
| animation.state | `animation state <target> [--layer]` | bridge (UnityCliBridge) | ✅ `animation.state` in ANIMATION_TOOLS | bridge-commands.test.ts: animation forwarding | ✅ |
| animation.play | `animation play <target> --state <name>` | bridge (UnityCliBridge) | ✅ `animation.play` in ANIMATION_TOOLS | bridge-commands.test.ts: animation forwarding; tools.test.ts: describe animation.play | ✅ |
| animation.stop | `animation stop <target>` | bridge (UnityCliBridge) | ✅ `animation.stop` in ANIMATION_TOOLS | bridge-commands.test.ts: animation forwarding | ✅ |
| vfx.graph | `vfx graph <Assets/Effect.vfx> [--create]` | bridge (UnityCliBridge) | ✅ `vfx.graph` in VFX_TOOLS | bridge-commands.test.ts: vfx forwarding; tools.test.ts: S3.2 vfx group | ✅ |
| vfx.effect | `vfx effect <target> [--graph] [--add]` | bridge (UnityCliBridge) | ✅ `vfx.effect` in VFX_TOOLS | bridge-commands.test.ts: vfx forwarding | ✅ |

## Profiler 域

> 架构敏感：Profiler 受 ALP-165 Q4 冻结口径约束，必须完整覆盖 session / counter / memory / frame debugger / export，不可降级为可选 capability；涉及命令契约、metadata 安全标记或 handler 行为变更时需按架构敏感改动复核。

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| profiler.status | `profiler status` | bridge (UnityCliBridge) | ✅ `profiler.status` in PROFILER_TOOLS; `architectureSensitive=true`, `frozenDecision=ALP-165-Q4` | bridge-commands.test.ts + contracts:validate + contract architecture-sensitive test | ✅ |
| profiler.start | `profiler start [--deep-profiling]` | hybrid (live bridge + job path) | ✅ `profiler.start` in PROFILER_TOOLS; `architectureSensitive=true`, `frozenDecision=ALP-165-Q4` | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.stop | `profiler stop` | bridge (UnityCliBridge) | ✅ `profiler.stop` in PROFILER_TOOLS; `architectureSensitive=true`, `frozenDecision=ALP-165-Q4` | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.pause | `profiler pause [--paused true\|false]` | bridge (UnityCliBridge) | ✅ `profiler.pause` in PROFILER_TOOLS; `architectureSensitive=true`, `frozenDecision=ALP-165-Q4` | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.counter | `profiler counter [--category] [--counter]` | bridge (UnityCliBridge) | ✅ `profiler.counter` in PROFILER_TOOLS; `coverageScope` includes counter | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.memory | `profiler memory` | bridge (UnityCliBridge) | ✅ `profiler.memory` in PROFILER_TOOLS; `coverageScope` includes memory | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.frame | `profiler frame [--module]` | bridge (UnityCliBridge) | ✅ `profiler.frame` in PROFILER_TOOLS; `coverageScope` includes frame-debugger | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.capture | `profiler capture <path> [--duration-ms] [--frames]` | hybrid (live bridge + job path) | ✅ `profiler.capture` in PROFILER_TOOLS; describe includes live/job cancel semantics (S3.5); `coverageScope` includes export | bridge-commands.test.ts + tools.test.ts | ✅ |
| profiler.export | `profiler export <path>` | bridge (UnityCliBridge) | ✅ `profiler.export` in PROFILER_TOOLS; `coverageScope` includes export | bridge-commands.test.ts + tools.test.ts | ✅ |

---

## Camera 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| camera.list | `camera list` | bridge (UnityCliBridge) | ✅ `camera.list` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 metadata coverage | ✅ |
| camera.info | `camera info <target>` | bridge (UnityCliBridge) | ✅ `camera.info` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 metadata coverage | ✅ |
| camera.screenshot | `camera screenshot <target> --output <path>` | bridge (UnityCliBridge) | ✅ `camera.screenshot` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 write metadata | ✅ |
| scene.screenshot | `scene screenshot <path>` | bridge (UnityCliBridge) | ✅ `scene.screenshot` in SCENE_TOOLS | bridge-commands.test.ts: scene screenshot forwards output path and overwrite | ✅ |
| scene.active | `scene active` | bridge (UnityCliBridge) | ✅ `scene.active` in SCENE_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| scene.hierarchy | `scene hierarchy` | bridge (UnityCliBridge) | ✅ `scene.hierarchy` in SCENE_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |
| scene.build-settings | `scene build-settings` | bridge (UnityCliBridge) | ✅ `scene.build-settings` in SCENE_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| scene.create | `scene create <path>` | bridge (UnityCliBridge) | ✅ `scene.create` in SCENE_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| scene.load | `scene load <path>` | bridge (UnityCliBridge) | ✅ `scene.load` in SCENE_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| scene.save | `scene save [path]` | bridge (UnityCliBridge) | ✅ `scene.save` in SCENE_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |

## Material / Shader / Texture 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| material.info | `material info <path>` | bridge (UnityCliBridge) | ✅ `material.info` in MATERIAL_TOOLS | bridge-commands.test.ts: material commands forward asset paths, target, values and planned effects; tools.test.ts: S3.1 metadata coverage | ✅ |
| material.create | `material create <path> --shader <name>` | bridge (UnityCliBridge) | ✅ `material.create` in MATERIAL_TOOLS | bridge-commands.test.ts: material commands forward asset paths, target, values and planned effects; tools.test.ts: S3.1 write metadata | ✅ |
| material.assign | `material assign --target <go> --material <path>` | bridge (UnityCliBridge) | ✅ `material.assign` in MATERIAL_TOOLS | bridge-commands.test.ts: material commands forward asset paths, target, values and planned effects | ✅ |
| material.set | `material set <path> --property <name> --value <value>` | bridge (UnityCliBridge) | ✅ `material.set` in MATERIAL_TOOLS | bridge-commands.test.ts: material commands forward asset paths, target, values and planned effects | ✅ |
| shader.info | `shader info <path>` | bridge (UnityCliBridge) | ✅ `shader.info` in SHADER_TOOLS | bridge-commands.test.ts: shader commands forward paths, content and overwrite; tools.test.ts: S3.1 metadata coverage | ✅ |
| shader.read | `shader read <path>` | bridge (UnityCliBridge) | ✅ `shader.read` in SHADER_TOOLS | bridge-commands.test.ts: shader commands forward paths, content and overwrite | ✅ |
| shader.create | `shader create <path> --content-file <file>` | bridge (UnityCliBridge) | ✅ `shader.create` in SHADER_TOOLS | bridge-commands.test.ts: shader commands forward paths, content and overwrite; tools.test.ts: S3.1 write metadata | ✅ |
| shader.update | `shader update <path> --content-file <file>` | bridge (UnityCliBridge) | ✅ `shader.update` in SHADER_TOOLS | bridge-commands.test.ts: shader commands forward paths, content and overwrite | ✅ |
| texture.info | `texture info <path>` | bridge (UnityCliBridge) | ✅ `texture.info` in TEXTURE_TOOLS | bridge-commands.test.ts: texture commands forward importer, sprite and creation options; tools.test.ts: S3.1 metadata coverage | ✅ |
| texture.create | `texture create <path> [--source <file>]` | bridge (UnityCliBridge) | ✅ `texture.create` in TEXTURE_TOOLS | bridge-commands.test.ts: texture commands forward importer, sprite and creation options; tools.test.ts: S3.1 write metadata | ✅ |
| texture.sprite | `texture sprite <path>` | bridge (UnityCliBridge) | ✅ `texture.sprite` in TEXTURE_TOOLS | bridge-commands.test.ts: texture commands forward importer, sprite and creation options | ✅ |
| texture.import-settings | `texture import-settings <path>` | bridge (UnityCliBridge) | ✅ `texture.import-settings` in TEXTURE_TOOLS | bridge-commands.test.ts: texture commands forward importer, sprite and creation options | ✅ |

## GameObject / Component 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| go.find | `go find --name <name>` | bridge (UnityCliBridge) | ✅ `go.find` in GO_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |
| go.inspect | `go inspect <target>` | bridge (UnityCliBridge) | ✅ `go.inspect` in GO_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| go.create | `go create [--name] [--parent]` | bridge (UnityCliBridge) | ✅ `go.create` in GO_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| go.modify | `go modify <target> [--set-name] [--x]` | bridge (UnityCliBridge) | ✅ `go.modify` in GO_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| go.delete | `go delete <target>` | bridge (UnityCliBridge) | ✅ `go.delete` in GO_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| component.add | `component add <target> --component <type>` | bridge (UnityCliBridge) | ✅ `component.add` in COMPONENT_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| component.remove | `component remove <target> --component <type>` | bridge (UnityCliBridge) | ✅ `component.remove` in COMPONENT_TOOLS | contracts:validate + camera-bridge-source.test.ts | ✅ |
| component.set | `component set <target> --component <type> --property <name> --value <value>` | bridge (UnityCliBridge) | ✅ `component.set` in COMPONENT_TOOLS | bridge-commands.test.ts + contracts:validate | ✅ |

## Custom Tool 域（S4.1 P2-2 新增）

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| custom-tool.list | `custom-tool list [--project] [--instance]` | bridge (UnityCliBridge) | ✅ `custom-tool.list` in CUSTOM_TOOL_TOOLS | bridge-commands.test.ts: custom-tool list and run; custom-tools.test.ts: descriptor forwarding; tools.test.ts: tools list --include-custom | ✅ |
| custom-tool.run | `custom-tool run <name> [--input/--input-file/--stdin] [--timeout-ms] [--fingerprint-mode] [--artifact-dir] [--job]` | bridge (UnityCliBridge) | ✅ `custom-tool.run` in CUSTOM_TOOL_TOOLS | bridge-commands.test.ts: custom-tool list and run; custom-tools.test.ts: C# attribute source validation; security.test.ts: fingerprint proof | ✅ |

## Diagnose 域（S4.3 P2-4 新增）

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| diagnose.preflight | `diagnose preflight <batch-file>` | bridge (UnityCliBridge) | ✅ `diagnose.preflight` in DIAGNOSE_TOOLS | batch.test.ts: batch preflight accepts/rejects/parallel-safe | ✅ |

## ProBuilder 域（可选 capability）

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| probuilder.shape | `probuilder shape <target> [--shape]` | hybrid (bridge + job path) | ✅ `probuilder.shape` in PROBUILDER_TOOLS | camera-bridge-source.test.ts: C# command registration; capabilities.test.ts: architecture-sensitive | ✅ |
| probuilder.create | `probuilder create [--shape] [--position]` | hybrid (bridge + job path) | ✅ `probuilder.create` in PROBUILDER_TOOLS | camera-bridge-source.test.ts: C# command registration | ✅ |
| probuilder.edit | `probuilder edit <target> [--operation]` | hybrid (bridge + job path) | ✅ `probuilder.edit` in PROBUILDER_TOOLS | camera-bridge-source.test.ts: C# command registration | ✅ |
| probuilder.csg | `probuilder csg <target> [--operation]` | hybrid (bridge + job path), destructiveHint=true | ✅ `probuilder.csg` in PROBUILDER_TOOLS | camera-bridge-source.test.ts: C# command registration; security.test.ts: destructive command | ✅ |

## Screenshot 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| screenshot.capture | `screenshot capture <path> [--width] [--height] [--format] [--overwrite]` | bridge (UnityCliBridge) | ✅ `screenshot.capture` in SCREENSHOT_TOOLS | bridge-commands.test.ts: screenshot command forwards output path; camera-bridge-source.test.ts | ✅ |

## CLI 离线能力域

| 功能域 | unity_cli 命令 | handler 类型 | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| cli.batch.schema | `batch schema` | offline | ✅ `cli.batch.schema` in CLI_ONLY_TOOLS | batch.test.ts: batch schema exposes defaults; tools.test.ts: cli.batch schema and run metadata | ✅ |
| cli.batch.run | `batch run <file>` | offline (orchestrator), destructiveHint=true | ✅ `cli.batch.run` in CLI_ONLY_TOOLS | batch.test.ts: full orchestration coverage (12 tests); security.test.ts: destructive dry-run | ✅ |
| cli.init | `init [--check]` | offline | ✅ `cli.init` in CLI_ONLY_TOOLS | cli.test.ts: init installs local file dependency, init --check verifies metadata | ✅ |
| cli.docs.get | `docs get <topic>` | offline | ✅ `cli.docs.get` in CLI_ONLY_TOOLS | docs-reflect.test.ts: docs get reads offline cache | ✅ |
| cli.docs.search | `docs search <keyword>` | offline | ✅ `cli.docs.search` in CLI_ONLY_TOOLS | docs-reflect.test.ts: docs search pages results | ✅ |
| cli.reflect.type | `reflect type <name>` | offline | ✅ `cli.reflect.type` in CLI_ONLY_TOOLS | docs-reflect.test.ts: reflect type and member read signatures | ✅ |
| cli.reflect.member | `reflect member <name>` | offline | ✅ `cli.reflect.member` in CLI_ONLY_TOOLS | docs-reflect.test.ts: reflect type and member read | ✅ |
| cli.reflect.search | `reflect search <query>` | offline | ✅ `cli.reflect.search` in CLI_ONLY_TOOLS | docs-reflect.test.ts: reflect search returns paged results | ✅ |

## 安全模型域（S4.4 P2-5 新增）

| 安全维度 | 实现状态 | 测试证据 | 状态 |
|---|---|---|---|
| destructiveHint + `--yes` 确认 | 12 个 destructive 命令均需 `--yes` 或 `--dry-run` 放行（含 cli.batch.run/test.cancel/build.cancel，P2-6 补齐） | security.test.ts: destructive commands require --yes; bridge-commands.test.ts: destructive bridge commands; batch.test.ts: batch --yes; cli.test.ts: build cancel --yes | ✅ |
| dry-run 预览 | `asset.delete`/`go.delete`/`scene.save`/`code.exec` 支持 supportsDryRun | security.test.ts: destructive dry-run; runtime-code.test.ts: code exec dry-run | ✅ |
| fingerprint 拦截 | custom tool SHA-256 fingerprint 校验 + destructive security proof | security.test.ts: fingerprint proof; custom-tools.test.ts: C# attribute fingerprint | ✅ |
| group 可见性 | tools list --group 按功能域过滤 | tools.test.ts: filterToolsByGroup; capabilities.test.ts: static data includes destructive tools list | ✅ |
| readOnlyHint | readOnly 命令标记和传播 | contracts:validate: readOnly 标记一致性 | ✅ |
| 黑名单/网络策略 | v1.0 已知限制，未独立实现 | — | 🔶 v1.0 限制 |
| 敏感信息保护 | 零风险面：不处理 token/secret/credential | 代码审查确认零风险面 | ✅ |

## Fingerprint 域（S4.2 P2-3 新增）

| 功能 | 实现 | 测试证据 | 状态 |
|---|---|---|---|
| custom tool fingerprint 校验 | SHA-256(handler code + descriptor) 自动计算；不一致时 `E_TOOL_FINGERPRINT_MISMATCH` 拦截执行 | custom-tools.test.ts: C# attribute; doctor.test.ts: fingerprint summary; security.test.ts: destructive fingerprint proof | ✅ |
| destructive security proof | destructive 命令执行时携带 `alp332-destructive-safety-v1` fingerprint | security.test.ts: security proof 随 dry-run/yes 传播 | ✅ |
| doctor fingerprint 诊断 | `doctor` 命令输出 custom tool fingerprint 摘要 | doctor.test.ts: custom tool fingerprint summary | ✅ |

## UI 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| ui.canvas.create | `ui canvas/create <name>` | bridge (UnityCliBridge) | ✅ `ui.canvas.create` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments; tools.test.ts: S3.1 write metadata | ✅ |
| ui.text.create | `ui text/create <name>` | bridge (UnityCliBridge) | ✅ `ui.text.create` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments | ✅ |
| ui.button.create | `ui button/create <name>` | bridge (UnityCliBridge) | ✅ `ui.button.create` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments | ✅ |
| ui.image.create | `ui image/create <name>` | bridge (UnityCliBridge) | ✅ `ui.image.create` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments | ✅ |
| ui.document.info | `ui document/info <target|path>` | bridge/offline reader | ✅ `ui.document.info` in UI_TOOLS | bridge-commands.test.ts: ui document info reads UXML offline when bridge endpoint is absent | ✅ |
| ui.document.create | `ui document/create --uxml <path>` | bridge (UnityCliBridge) | ✅ `ui.document.create` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments | ✅ |
| ui.hierarchy | `ui hierarchy [target]` | bridge (UnityCliBridge) | ✅ `ui.hierarchy` in UI_TOOLS | bridge-commands.test.ts: ui commands forward creation, document and hierarchy arguments | ✅ |

---

## 真实 Unity 项目 smoke 验证状态

> 验证基准：origin/main `d2287a5`（P0-P2 全链路合入后）| Unity 2022.3.61f1 | Windows DESKTOP-HDGGLLV
> P1-2 + P2-6 实机 smoke 已于 2026-06-16 完成全量闭环：S1~S3.2/P1-1/P2-1~P2-5 全部通过，Console error 为空。

| 功能域 | read 操作 | write/modify 操作 | 验证日期 | 备注 |
|---|---|---|---|---|
| editor | `editor state` 返回当前状态 | `editor play/pause/stop` 控制 play mode | 2026-06-16 (P1-2) | Console clean |
| package | `package list` 返回列表 | `package install/remove` 修改 manifest | 2026-06-16 (P1-2) | 环境验证通过 |
| test | `test.poll` 查询 job 状态 | `test run` 触发测试 | 2026-06-16 (P1-2) | job 生命周期通过 |
| build | `build.status` 查询 job 状态 | `build run` 触发构建 | 2026-06-16 (P1-2) | job 生命周期通过 |
| camera | `camera info <target>` | `camera screenshot <target> --output <path>` | 2026-06-16 (ALP-275) | Console clean；含 scene.screenshot 验证 |
| scene | `scene active/hierarchy/build-settings` 返回场景信息 | `scene create/load/save` 管理场景生命周期 | 2026-06-16 (ALP-275) | 与 camera 域合并验证；scene.active/create/load/save/hierarchy/build-settings 均在 contracts:validate 和 camera-bridge-source.test.ts 覆盖 |
| material | `material info <path>` | `material create <path> --shader <name>` | 2026-06-16 (ALP-275) | Console clean |
| shader | `shader info <path>` | `shader create <path> --content-file <file>` | 2026-06-16 (ALP-275) | Console clean |
| texture | `texture info <path>` | `texture create <path> [--source <file>]` | 2026-06-16 (ALP-275) | Console clean |
| ui | `ui document/info <target|path>` / `ui hierarchy [target]` | `ui canvas/create <name>` | 2026-06-16 (ALP-275) | Console clean |
| graphics | `graphics quality` / `graphics pipeline` / `graphics volume` 返回设置、管线和 Volume 摘要 | `graphics settings --render-pipeline` 或 `graphics quality --quality-level` 修改项目设置 | 2026-06-16 (P1-2) | Console clean |
| physics | `physics raycast` 执行 3D/2D 射线检测 | `physics settings --gravity-y` 或 `physics simulation --simulate` 修改/步进物理 | 2026-06-16 (P1-2) | Console clean |
| animation/vfx | `animation state` / `vfx graph` 查询动画和 VFX 资源/组件 | `animation play <target> --state <name>` / `vfx effect <target> --add` 执行动画和 VFX 修改 | 2026-06-16 (P1-2) | VFX 因 ProBuilder/VFX package 未安装，CLI 返回预期错误码 |
| profiler | `profiler status/counter/memory/frame` 返回状态、计数器、内存和帧诊断 | `profiler start/pause/stop` 控制 session，`profiler capture/export` 写 artifact | 2026-06-16 (P1-2/ALP-340/ALP-348) | Console clean（ALP-340 修复后复测通过）；export artifact 1188 bytes |
| script | `script read/validate <path>` 返回内容和静态诊断 | `script create/edit --yes` 创建或编辑脚本 | 2026-06-16 (P2-6 实机) | Console clean；script.create/read/validate/edit 全链路通过 |
| go/component | `go find/inspect` 返回对象和组件摘要 | `go create/modify/delete --yes`、`component add/set/remove --yes` 修改场景对象 | 2026-06-16 (P2-6 实机) | Console clean；destructive 命令无 --yes 返回 E_DESTRUCTIVE_CONFIRMATION_REQUIRED |
| asset | `asset search/info` 返回资源列表与 metadata | `asset create/modify/duplicate/move/rename/delete --yes` + `delete --dry-run` 修改 AssetDatabase | 2026-06-16 (P2-6 实机) | Console clean；dry-run 预览正常 |
| prefab | `prefab info/hierarchy` 返回 prefab 摘要与层级 | `prefab create/modify/stage-open/stage-save/stage-close` 管理 prefab 生命周期 | 2026-06-16 (P2-6 实机) | Console clean |
| code | `code exec --code` 返回结果 | `code exec --yes` 执行代码（destructiveHint）；`--dry-run` 预览 | 2026-06-16 (P2-6 实机) | Console clean；live inline/file + dry-run 均通过 |
| custom-tool | `custom-tool list` 列出注册的自定义工具 | `custom-tool run sample.echo --fingerprint-mode warn` 执行自定义工具 | 2026-06-16 (P2-6 实机) | Console clean；fingerprint 链路正常 |
| batch | `batch schema` 查看 schema | `batch run --preflight` + `batch run --yes` 执行批处理 | 2026-06-16 (P2-6 实机) | Console clean；preflight + --yes 均通过 |
| probuilder | `probuilder shape` 查询形状 | `probuilder create/csg` 修改场景（destructiveHint） | 可选 capability | 按冻结决策延后为可选 capability；需 ProBuilder 包 |
| diagnose | `diagnose run` / preflight 查看 batch schema 合规 | `diagnose preflight` 执行批处理预检 | 2026-06-16 (P2-6 实机) | Console clean |
| screenshot | `screenshot capture <path>` 截取当前视图 | — | 2026-06-16 (P2-6 实机) | Console clean（与 camera/scene.screenshot 合并验证） |
| 安全模型 / destructive safety | `capabilities` 列出 destructive 工具 | `--yes` 确认 + dry-run 预览 | 2026-06-16 (P2-5) | 12 个 destructive 命令确认机制已验证 |
| fingerprint | `doctor` 输出 fingerprint 摘要 | `custom-tool run --fingerprint-mode warn|block` 执行自定义工具 | 2026-06-16 (P2-3/P2-5) | fingerprint 校验链路已验证 |
| CLI 离线能力 | `init --check`、`docs get/search`、`reflect type/member/search`、`batch schema`、`package list/info/search` | `init`、`batch run`（destructive）、`package install/remove`（destructive）、`job status/logs/cancel` | 离线工具，自动测试已覆盖 | 不需 Unity 实机；自动测试全链路覆盖 |

---

## 变更日志

- **S2.3** (2026-06-14): 新增 `editor.refresh`、`cli.package.resolve`、`cli.package.registry`、`cli.package.scoped-registry`、`test.run`、`test.poll`、`build.run`、`build.status`、`build.cancel` metadata 条目及覆盖矩阵行
- **S2.4** (2026-06-14): 新增 `script.delete` metadata 条目 + bridge 路由；`code.exec` 增加 `permissions=["executes_code"]`、`outputSchema`、batchmode job 参数；script/code 覆盖矩阵行
- **S2.2** (2026-06-15): 补齐 asset create/modify/delete/duplicate/move/rename 与 prefab modify Bridge handler、metadata 和覆盖矩阵行
- **S3.1** (2026-06-15): 补齐 camera、material、shader、texture、ui 创作域覆盖矩阵；新增 S3.1 metadata/write smoke 单测证据
- **S3.2** (2026-06-15): 新增 Graphics/Physics/Animation/VFX 覆盖矩阵行；补 `graphics.pipeline`、`graphics.volume` 查询命令与 metadata/handler/test 证据
- **ALP-306** (2026-06-15): 新增 `contracts/command-contract.json` 与 `npm run contracts:validate`；补齐 scene/go/component/script 16 个 C# bridge handler，使 TS registry、tool metadata 与 C# registry 统一校验通过。
- **S3.4** (2026-06-15): 补齐 Profiler session/counter/memory/frame/export CLI、UnityPkg handler、metadata/describe 与自动化测试矩阵；质量 smoke PASS。
- **P1-1 Profiler 迁移重做** (2026-06-16): 在新结构 contract/metadata/handler/docs 中显式标记 `architectureSensitive=true`、`frozenDecision=ALP-165-Q4`、完整 `coverageScope`，用于防止冻结口径被误降级。
- **S3.5** (2026-06-16): 补齐 `cli.batch.schema` metadata、`test.logs`/`test.cancel`、`build.logs`、job describe/cancellation semantics 字段，并新增 batch→build status/logs/cancel 自动化链路测试。
- **P2-1 S3.5 Batch/Job 完整化** (2026-06-16): 新增 `cli.batch.run/schema` metadata，补齐 batch orchestration 全链路（preflight/parallel/fail-fast/env allowlist），job lifecycle（status/logs/cancel/timeout）。
- **P2-2 S4.1 Custom tool 注册 + descriptor** (2026-06-16): 新增 `custom-tool.list/run` 覆盖矩阵行；实现 Attribute-first `[UnityCliTool]` 注册 + 自动 descriptor 生成 + schema 校验。
- **P2-3 S4.2 Fingerprint runtime 拦截** (2026-06-16): 新增 Fingerprint 域覆盖矩阵；实现 SHA-256 fingerprint 校验 + `E_TOOL_FINGERPRINT_MISMATCH` 拦截 + destructive security proof。
- **P2-4 S4.3 CLI 命令 + tools 统一发现** (2026-06-16): 新增 Diagnose 域、ProBuilder 域、Screenshot 域、CLI 离线能力域覆盖矩阵；实现 `tools list/groups/search/describe` 统一发现 + `capabilities` 静态/混合 manifest + `diagnose.preflight`。
- **P2-5 S4.4 安全模型全链路验证** (2026-06-16): 新增安全模型域覆盖矩阵；9 个 destructive 命令 `--yes` 确认机制 + representative dry-run + fingerprint + destructive security proof + group 可见性 + readOnlyHint + 敏感信息保护零风险面。黑名单和网络策略为 v1.0 已知限制。
- **P2-6 S4.5 覆盖矩阵收口** (2026-06-16): 全量收口；120 contract commands / 95 bridge / 95 C# / 120 metadata / 270 npm tests / contracts:validate ok；更新 smoke 验证状态（P1-2 + P2-4 + P2-5 实机证据合并）；补齐 security-policy.ts 3 条缺失（cli.batch.run/test.cancel/build.cancel）；补齐 smoke 表 7 个域。
