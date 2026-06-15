# Alps Unity CLI — 功能域覆盖矩阵

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
| editor.state | `editor state` | bridge (AlpsUnityBridge) | ✅ `editor.state` in EDITOR_TOOLS | tools.test.ts: editor group has all required tools | ✅ |
| editor.play | `editor play` | bridge (AlpsUnityBridge) | ✅ `editor.play` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.pause | `editor pause` | bridge (AlpsUnityBridge) | ✅ `editor.pause` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.stop | `editor stop` | bridge (AlpsUnityBridge) | ✅ `editor.stop` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.menu | `editor menu` | bridge (AlpsUnityBridge) | ✅ `editor.menu` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.console | `editor console` | bridge (AlpsUnityBridge) | ✅ `editor.console` in EDITOR_TOOLS | bridge-commands.test.ts | ✅ |
| editor.refresh | `editor refresh [--compile]` | job (AlpsUnityCli.Batch.Refresh) | ✅ `editor.refresh` in EDITOR_TOOLS (S2.3 新增) | tools.test.ts: editor.refresh metadata | ✅ |

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
| build.run | `build run [--target] [--output]` | job (AlpsUnityCli.Batch.BuildProject) | ✅ `build.run` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.run metadata | ✅ |
| build.status | `build status --id <jobId>` | offline (job status) | ✅ `build.status` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.status metadata | ✅ |
| build.cancel | `build cancel --id <jobId>` | offline (job cancel) | ✅ `build.cancel` in CLI_ONLY_TOOLS (S2.3 新增) | tools.test.ts: build.cancel metadata | ✅ |

## Asset + Prefab 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| asset.search | `asset search <keyword>` | bridge (AlpsUnityBridge) | ✅ `asset.search` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.info | `asset info <path>` | bridge (AlpsUnityBridge) | ✅ `asset.info` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.create | `asset create <path> [--type folder\|file]` / `asset mkdir <path>` | bridge (AlpsUnityBridge) | ✅ `asset.create` in ASSET_TOOLS | bridge-commands.test.ts | ✅ merge |
| asset.import | `asset import <path> [--source file]` | bridge (AlpsUnityBridge) | ✅ `asset.import` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.modify | `asset modify <path> --content-file <file>` | bridge (AlpsUnityBridge) | ✅ `asset.modify` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.delete | `asset delete <path>` | bridge (AlpsUnityBridge) | ✅ `asset.delete` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.duplicate | `asset duplicate <path> --to <path>` | bridge (AlpsUnityBridge) | ✅ `asset.duplicate` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.move | `asset move <path> --to <path>` | bridge (AlpsUnityBridge) | ✅ `asset.move` in ASSET_TOOLS | bridge-commands.test.ts | ✅ keep |
| asset.rename | `asset rename <path> --name <name>` | bridge (AlpsUnityBridge) | ✅ `asset.rename` in ASSET_TOOLS | bridge-commands.test.ts | ✅ rename |
| prefab.info | `prefab info <path>` | bridge (AlpsUnityBridge) | ✅ `prefab.info` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.hierarchy | `prefab hierarchy <path>` | bridge (AlpsUnityBridge) | ✅ `prefab.hierarchy` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.create | `prefab create <source> --path <path>` | bridge (AlpsUnityBridge) | ✅ `prefab.create` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.modify | `prefab modify <path> [--name] [--add-child] [--remove-child]` | bridge (AlpsUnityBridge) | ✅ `prefab.modify` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ merge |
| prefab.stage-open | `prefab stage-open <path>` | bridge (AlpsUnityBridge) | ✅ `prefab.stage-open` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.stage-save | `prefab stage-save [path]` | bridge (AlpsUnityBridge) | ✅ `prefab.stage-save` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |
| prefab.stage-close | `prefab stage-close [--save]` | bridge (AlpsUnityBridge) | ✅ `prefab.stage-close` in PREFAB_TOOLS | bridge-commands.test.ts | ✅ keep |

## Script 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| script.create | `script create <path> --content-file <file>` | bridge (AlpsUnityBridge) | ✅ `script.create` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.read | `script read <path>` | bridge (AlpsUnityBridge) | ✅ `script.read` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.edit | `script edit <path> [--patch/--find/--replace]` | bridge (AlpsUnityBridge) | ✅ `script.edit` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |
| script.delete | `script delete <path>` | bridge (AlpsUnityBridge) | ✅ `script.delete` in SCRIPT_TOOLS (S2.4 新增) | tools.test.ts: script.delete metadata | ✅ |
| script.validate | `script validate <path>` | bridge (AlpsUnityBridge) | ✅ `script.validate` in SCRIPT_TOOLS | bridge-commands.test.ts | ✅ |

## Code 域

| unity-mcp 功能域/action | unity_cli 命令 | UnityPkg handler | metadata/describe | 测试证据 | 状态 |
|---|---|---|---|---|---|
| code.exec | `code exec (--file\|--code\|--stdin) [--channel live\|batchmode] [--job] [--artifact-dir]` | hybrid (bridge live + batch job) | ✅ `code.exec` in CODE_TOOLS, destructiveHint=true, permissions=["executes_code"] (S2.4 增强) | runtime-code.test.ts: live + batchmode + artifact + timeout | ✅ |

---

## 真实 Unity 项目 smoke 验证状态

| 功能域 | read 操作 | write/modify 操作 | 验证日期 | 备注 |
|---|---|---|---|---|
| editor | `editor state` 返回当前状态 | `editor play/pause/stop` 控制 play mode | 待验证 | 需要 Unity 项目环境 |
| package | `package list` 返回列表 | `package install/remove` 修改 manifest | 待验证 | 需要 Unity 项目环境 |
| test | `test.poll` 查询 job 状态 | `test run` 触发测试 | 待验证 | 需要 Unity 项目环境 |
| build | `build.status` 查询 job 状态 | `build run` 触发构建 | 待验证 | 需要 Unity 项目环境 |
| script | `script read <path>` 返回内容 | `script create/delete` 创建/删除脚本 | 待验证 | 需要 Unity 项目环境 |
| asset | `asset search/info` 返回资源列表与 metadata | `asset create/modify/duplicate/move/rename/delete` 修改 AssetDatabase | 待验证 | 需要 Unity 项目环境 |
| prefab | `prefab info/hierarchy` 返回 prefab 摘要与层级 | `prefab create/modify/stage-open/stage-save/stage-close` 管理 prefab 生命周期 | 待验证 | 需要 Unity 项目环境 |
| code | `code exec --code` 返回结果 | `code exec` 执行代码（destructiveHint） | 待验证 | 需要 Unity 项目环境 |

---

## 变更日志

- **S2.3** (2026-06-14): 新增 `editor.refresh`、`cli.package.resolve`、`cli.package.registry`、`cli.package.scoped-registry`、`test.run`、`test.poll`、`build.run`、`build.status`、`build.cancel` metadata 条目及覆盖矩阵行
- **S2.4** (2026-06-14): 新增 `script.delete` metadata 条目 + bridge 路由；`code.exec` 增加 `permissions=["executes_code"]`、`outputSchema`、batchmode job 参数；script/code 覆盖矩阵行
- **S2.2** (2026-06-15): 补齐 asset create/modify/delete/duplicate/move/rename 与 prefab modify Bridge handler、metadata 和覆盖矩阵行
