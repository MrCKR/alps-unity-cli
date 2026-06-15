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
| test.run | `test run [--platform EditMode/PlayMode]` | job (Unity -runTests) | ✅ `test.run` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: test.run metadata | ✅ |
| test.poll | `test poll --id <jobId>` | offline (job status) | ✅ `test.poll` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: test.poll metadata | ✅ |

## Build 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| build.run | `build run [--target] [--output]` | job (UnityCli.UnityCliBatch.BuildProject) | ✅ `build.run` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.run metadata | ✅ |
| build.status | `build status --id <jobId>` | offline (job status) | ✅ `build.status` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.status metadata | ✅ |
| build.cancel | `build cancel --id <jobId>` | offline (job cancel) | ✅ `build.cancel` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.cancel metadata | ✅ |

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
| script.create | `script create <path> --content-file <file>` | bridge (UnityCliBridge) | ✅ `script.create` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.read | `script read <path>` | bridge (UnityCliBridge) | ✅ `script.read` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.edit | `script edit <path> [--patch/--find/--replace]` | bridge (UnityCliBridge) | ✅ `script.edit` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.delete | `script delete <path>` | bridge (UnityCliBridge) | ✅ `script.delete` in SCRIPT_TOOLS (S2.4 新增) | tools.test.ts: script.delete metadata | ✅ |
| script.validate | `script validate <path>` | bridge (UnityCliBridge) | ✅ `script.validate` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |

## Code 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| code.exec | `code exec (--file\|--code\|--stdin) [--channel live\|batchmode] [--job] [--artifact-dir]` | hybrid (bridge live + batch job) | ✅ `code.exec` in CODE_TOOLS, destructiveHint=true, permissions=["executes_code"] (S2.4 增强) | runtime-code.test.ts: live + batchmode + artifact + timeout | ✅ |

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

---

## Camera 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| camera.list | `camera list` | bridge (UnityCliBridge) | ✅ `camera.list` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 metadata coverage | ✅ |
| camera.info | `camera info <target>` | bridge (UnityCliBridge) | ✅ `camera.info` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 metadata coverage | ✅ |
| camera.screenshot | `camera screenshot <target> --output <path>` | bridge (UnityCliBridge) | ✅ `camera.screenshot` in CAMERA_TOOLS | bridge-commands.test.ts: camera commands forward target and screenshot options; tools.test.ts: S3.1 write metadata | ✅ |
| scene.screenshot | `scene screenshot <path>` | bridge (UnityCliBridge) | ✅ `scene.screenshot` in SCENE_TOOLS | bridge-commands.test.ts: scene screenshot forwards output path and overwrite | ✅ |

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

| 功能域 | read 操作 | write/modify 操作 | 验证日期 | 备注 |
|---|---|---|---|---|
| camera | `camera info <target>` | `camera screenshot <target> --output <path>` | 待实机验证 | CLI/handler/metadata 单测已覆盖；需要 Unity 项目环境 |
| material | `material info <path>` | `material create <path> --shader <name>` | 待实机验证 | CLI/handler/metadata 单测已覆盖；需要 Unity 项目环境 |
| shader | `shader info <path>` | `shader create <path> --content-file <file>` | 待实机验证 | CLI/handler/metadata 单测已覆盖；需要 Unity 项目环境 |
| texture | `texture info <path>` | `texture create <path> [--source <file>]` | 待实机验证 | CLI/handler/metadata 单测已覆盖；需要 Unity 项目环境 |
| ui | `ui document/info <target|path>` / `ui hierarchy [target]` | `ui canvas/create <name>` | 待实机验证 | CLI/handler/metadata 单测已覆盖；需要 Unity 项目环境 |
| editor | `editor state` 返回当前状态 | `editor play/pause/stop` 控制 play mode | 待验证 | 需要 Unity 项目环境 |
| package | `package list` 返回列表 | `package install/remove` 修改 manifest | 待验证 | 需要 Unity 项目环境 |
| test | `test.poll` 查询 job 状态 | `test run` 触发测试 | 待验证 | 需要 Unity 项目环境 |
| build | `build.status` 查询 job 状态 | `build run` 触发构建 | 待验证 | 需要 Unity 项目环境 |
| script | `script read <path>` 返回内容 | `script create/delete` 创建/删除脚本 | 待验证 | 需要 Unity 项目环境 |
| asset | `asset search/info` 返回资源列表与 metadata | `asset create/modify/duplicate/move/rename/delete` 修改 AssetDatabase | 待验证 | 需要 Unity 项目环境 |
| prefab | `prefab info/hierarchy` 返回 prefab 摘要与层级 | `prefab create/modify/stage-open/stage-save/stage-close` 管理 prefab 生命周期 | 待验证 | 需要 Unity 项目环境 |
| code | `code exec --code` 返回结果 | `code exec` 执行代码（destructiveHint） | 待验证 | 需要 Unity 项目环境 |
| graphics | `graphics quality` / `graphics pipeline` / `graphics volume` 返回设置、管线和 Volume 摘要 | `graphics settings --render-pipeline` 或 `graphics quality --quality-level` 修改项目设置 | 待验证 | 自动测试覆盖 CLI/handler/metadata；真实 Unity smoke 需 Windows + Unity Editor |
| physics | `physics raycast` 执行 3D/2D 射线检测 | `physics settings --gravity-y` 或 `physics simulation --simulate` 修改/步进物理 | 待验证 | 自动测试覆盖 CLI/handler/metadata；真实 Unity smoke 需 Windows + Unity Editor |
| animation/vfx | `animation state` / `vfx graph` 查询动画和 VFX 资源/组件 | `animation play <target> --state <name>` / `vfx effect <target> --add` 执行动画和 VFX 修改 | 待验证 | 自动测试覆盖 CLI/handler/metadata；真实 Unity smoke 需 Windows + Unity Editor |

---

## 变更日志

- **S2.3** (2026-06-14): 新增 `editor.refresh`、`cli.package.resolve`、`cli.package.registry`、`cli.package.scoped-registry`、`test.run`、`test.poll`、`build.run`、`build.status`、`build.cancel` metadata 条目及覆盖矩阵行
- **S2.4** (2026-06-14): 新增 `script.delete` metadata 条目 + bridge 路由；`code.exec` 增加 `permissions=["executes_code"]`、`outputSchema`、batchmode job 参数；script/code 覆盖矩阵行
- **S2.2** (2026-06-15): 补齐 asset create/modify/delete/duplicate/move/rename 与 prefab modify Bridge handler、metadata 和覆盖矩阵行
- **S3.1** (2026-06-15): 补齐 camera、material、shader、texture、ui 创作域覆盖矩阵；新增 S3.1 metadata/write smoke 单测证据
- **S3.2** (2026-06-15): 新增 Graphics/Physics/Animation/VFX 覆盖矩阵行；补 `graphics.pipeline`、`graphics.volume` 查询命令与 metadata/handler/test 证据
