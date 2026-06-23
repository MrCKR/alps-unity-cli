# unity_cli

CLI-first Unity 自动化工具，不是 MCP server。通过 `unity_cli` 二进制命令行直接驱动 Unity Editor，无需额外协议层。

npm 包名：`@alps_ckr/unity_cli`  
运行环境：Windows + Node.js 20+  
当前方向：冻结决策 — 独立 CLI 实现、Windows-only、npm-first 安装、开放能力无默认风险门控。

## Project Baseline

本节是项目基线，优先级高于本文档其他章节、历史实现、旧示例和当前代码状态。AI 和后续实现不得自行修改本节含义；如需调整，必须由项目所有者明确确认。

### 1. Clean Project Semantics

本项目是开发中项目，不承担旧版本兼容义务。

项目代码、文档、命令形态和注释必须表达当前项目规范。发现旧语义、阶段性语义或兼容性语义时，应统一改为当前规范；无法干净迁移时，优先删除，而不是保留多套说法。

### 2. Top-Level CLI Shape

用户可见顶层命令分为两类：能力入口和 CLI 控制面。

能力入口只允许：

- `tools`
- `context`

CLI 控制面只允许：

- `init`
- `extensions`
- `target`
- `instances`
- `jobs`
- `batch`
- `doctor`

Unity 项目内容与 Editor 能力域必须继续通过 `tools run <tool>` 或 `context read <context>` 暴露，例如 scene、asset、game-object、prefab、editor、screenshot、package、docs、reflect、build、test、code、profiler、physics、graphics 等。

`target`、`instances`、`jobs`、`batch`、`doctor` 是 CLI 自身的状态、路由、队列、编排和诊断控制面；它们可以作为顶层命令存在，但不得重新引入 Unity 能力域的顶层入口。

内部 bridge command、job runner、worker、registry 可以作为实现细节存在，但不得定义超出上述清单的用户可见顶层命令形态。

### 3. AI-First Tool Interface

本 CLI 主要提供给 AI 使用。项目哲学不是维护一套面向人工记忆的传统命令手册，而是提供可发现、可描述、可审计的工具目录。

AI 的标准流程是：

```text
unity_cli tools group/search
unity_cli tools describe <tool>
unity_cli tools run <tool> --input <json>

unity_cli context list/describe/read

unity_cli target current/set/clear/alias
unity_cli instances list/prune
unity_cli jobs status/logs/describe/cancel
unity_cli batch run/schema
unity_cli doctor

unity_cli extensions list/install/remove/doctor
unity_cli init
```

具体有哪些工具、工具参数是什么、风险边界是什么，应由 AI 通过本 CLI 内置的 `tools describe`、`context describe` 等能力自发现。

## 快速开始

```powershell
# 安装
npm install -g @alps_ckr/unity_cli

# 初始化项目（安装 Bridge 包；若项目已有 .pi/.claude/.agent，则自动安装 Skill）
unity_cli init --project C:\path\MyProject

# 查看帮助
unity_cli --help
unity_cli --version
```

## 核心使用流程

### 1. 发现目标实例

```powershell
# 列出运行中的 Unity 实例
unity_cli instances list

# 设置活动目标
unity_cli target set <instanceId>
# 或使用别名
unity_cli target alias set <instanceId> my-project
unity_cli target set my-project
```

### 2. 发现工具能力（group-first 路径）

推荐按组逐层发现，避免一次展开全量工具：

```powershell
# 第一步：查看工具分组
unity_cli tools group

# 第二步：按组查看工具摘要
unity_cli tools group list camera
# 或搜索关键词
unity_cli tools search screenshot

# 第三步：查看工具完整描述（执行前必做）
unity_cli tools describe camera.screenshot

# 第四步：执行工具
unity_cli tools run camera.screenshot --input '{"target":"Main Camera","output":"Artifacts/shot.png"}'
```

### 3. 获取只读项目信息

当前版本通过 `docs` 和 `reflect` 命令访问离线文档和反射缓存：

```powershell
# 列出内置文档主题
unity_cli tools run docs.list

# 查看特定主题
unity_cli tools run docs.topic tool-discovery

# 离线 Unity API 文档
unity_cli tools run docs.get UnityEngine.Camera
unity_cli tools run docs.search "Render Pipeline"

# 离线类型反射
unity_cli tools run reflect.type UnityEngine.Camera
unity_cli tools run reflect.member UnityEngine.Camera.fieldOfView
```

> **注意**：可通过 `context list` / `context describe <name>` / `context read <name>` 命令获取只读项目状态。也可通过 `tools group list` 输出中 `readOnlyHint: true` 的工具以及 `docs`/`reflect` 命令获取。

### 4. 项目自定义工具

Unity 项目可通过 C# `[UnityCliTool]` 特性注册自定义工具：

```powershell
# 发现项目自定义工具（需要 Bridge 连接）
unity_cli tools group list custom --include-custom

# 查看自定义工具描述
unity_cli tools describe my.custom-tool

# 执行自定义工具
unity_cli tools run my.custom-tool --input '{"key":"value"}'
```

> **注意**：可通过 `extensions list` 查看已安装扩展，`extensions install <id>` / `extensions remove <id>` 管理扩展，`extensions doctor` 诊断扩展状态。扩展包也可通过 `init` 命令安装 Bridge 包来启用。

### 5. 诊断与健康检查

```powershell
# 运行完整诊断
unity_cli doctor

# 诊断预检（通过 Bridge）
unity_cli tools run diagnose.preflight
```

## 安全边界

| 规则 | 说明 |
|------|------|
| Tool 可有副作用 | `destructiveHint: true` 的工具需要 `--yes` 确认或 `--dry-run` 预览 |
| Context 必须只读 | `readOnlyHint: true` 的工具不会修改 Unity 状态 |
| Extension 不能绕过 descriptor/envelope | 项目工具必须可描述、可审计、可阻断 |
| fingerprint mismatch 默认 block | 自定义工具的 handler 代码与 descriptor 指纹不匹配时，默认阻止执行 |

## 安装与构建

```powershell
# 全局安装
npm install -g @alps_ckr/unity_cli

# 本地开发
npm install
npm run build
npm test
npm link
```

## 输出格式

所有命令输出统一 JSON envelope：

```json
{
  "ok": true,
  "requestId": "req-xxx",
  "data": { ... }
}
```

错误输出：

```json
{
  "ok": false,
  "requestId": "req-xxx",
  "error": {
    "code": "E_TARGET_AMBIGUOUS",
    "message": "...",
    "transient": false,
    "next_actions": ["Run unity_cli instances list to discover targets."],
    "retry_after_ms": 0
  }
}
```

## Job 模型

长时间运行的 Unity batchmode 操作在 `%LOCALAPPDATA%\UnityCli` 下创建持久 job。

Job 状态：`queued` → `running` → `succeeded` / `failed` / `cancelled` / `timeout`

```powershell
unity_cli jobs status <jobId>
unity_cli jobs logs <jobId> --tail 100
unity_cli jobs cancel <jobId>
```

## 命令支持状态

| 状态 | 含义 |
|------|------|
| 已路由可执行 | 通过 Bridge `POST /bridge/execute` 实时执行 |
| 已路由可执行 + 可 batch/job | 支持 Bridge 实时 + batch/job 双通道 |
| 仅 batch/job | 通过 Unity batchmode/job 执行 |
| 仅 CLI 离线 | 本地 CLI 执行，无需 Unity 连接 |
| 规划中 | CLI 命令合约已声明，Bridge 处理程序待实现 |

## 全部命令参考

### 工具发现与执行

```powershell
# 列出工具分组
unity_cli tools group

# 按组列出工具
unity_cli tools group list camera

# 包含项目自定义工具
unity_cli tools group list custom --include-custom

# 搜索工具
unity_cli tools search <keyword>
unity_cli tools search screenshot --include-custom

# 工具完整描述
unity_cli tools describe <tool-name>

# 执行工具
unity_cli tools run <tool-name> --input '<json>'
unity_cli tools run <tool-name> --input-file params.json
unity_cli tools run <tool-name> --stdin
```

### 场景与截图

```powershell
unity_cli tools run scene.hierarchy --page-size 100
unity_cli tools run scene.active
unity_cli tools run scene.list-opened
unity_cli tools run scene.unload <path> --yes
unity_cli tools run scene.set-active <path>
unity_cli tools run scene.data --max-depth 3 --include-components
unity_cli tools run scene.view-frame --target Player
unity_cli tools run scene.screenshot <output-path> --width 1920 --height 1080

unity_cli tools run camera.list
unity_cli tools run camera.info "Main Camera"
unity_cli tools run camera.screenshot "Main Camera" --output shot.png --width 1920 --height 1080

unity_cli tools run screenshot.capture <output-path>
unity_cli tools run screenshot.game-view <output-path> --include-diagnostics
unity_cli tools run screenshot.scene-view <output-path>
unity_cli tools run screenshot.isolated-object --target Player --output iso.png
unity_cli tools run screenshot.orbit --target Player --output-dir Assets/Shots --count 8
```

### 对象、组件与反射

```powershell
# 通用 Object 深读写（对齐 IvanMurzak）
unity_cli tools run object.get-data --input '{"instanceId":12345}'
unity_cli tools run object.get-data --input '{"path":"Main Camera","paths":["localPosition"]}'
unity_cli tools run object.modify --input '{"instanceId":123,"pathPatches":[{"path":"m_Color","value":{"r":1}}]}'
unity_cli tools run object.modify --input '{"instanceId":123,"jsonPatch":{"m_Color":{"r":1}}}' --dry-run

# Component 深读写
unity_cli tools run component.get --input '{"path":"Player","component":"Transform"}'
unity_cli tools run component.modify --input '{"path":"Player","component":"Transform","pathPatches":[{"path":"localPosition","value":{"x":1}}]}'
unity_cli tools run component.list-types --input '{"keyword":"Rigidbody"}'

# Live 反射
unity_cli tools run reflection.method.find --input '{"typeName":"UnityEngine.Transform","methodName":"Translate"}'
unity_cli tools run reflection.method.call --input '{"typeName":"UnityEngine.Transform","methodName":"Translate","args":[1,0,0],"instanceRef":{"instanceId":123},"security":{"confirmed":true,"destructiveFingerprint":"..."}}'
unity_cli tools run type.schema --input '{"typeName":"UnityEngine.Rigidbody"}'
```

> patch 路径语法对齐 IvanMurzak ReflectorNet：`fieldName`、`nested/field`、`arrayField/[i]`、`dictField/[key]`，同时兼容 `.` 分隔。执行顺序：`jsonPatch` → `pathPatches` → `objectDiff/componentDiff`。

### 资产与预制体

```powershell
unity_cli tools run asset.search <keyword> --type Prefab
unity_cli tools run asset.info <path>
unity_cli tools run asset.create <path> --content '{}' --overwrite
unity_cli tools run asset.delete <path> --yes
unity_cli tools run asset.duplicate <path> --to <new-path>
unity_cli tools run asset.move <path> --to <new-path>
unity_cli tools run asset.rename <path> --name <new-name>
unity_cli tools run asset.import <source> --project <project-path>

unity_cli tools run prefab.info <path>
unity_cli tools run prefab.create <source> --path <prefab-path>
unity_cli tools run prefab.modify <path> --name <new-name>
unity_cli tools run prefab.stage-open <path>
unity_cli tools run prefab.stage-save
unity_cli tools run prefab.stage-close --save
unity_cli tools run prefab.hierarchy <path> --page-size 100
unity_cli tools run prefab.instantiate <path> --parent Enemies --name Enemy_1
unity_cli tools run prefab.unpack --target Player --yes

unity_cli tools run asset.search-built-in --keyword Icon --type Texture
unity_cli tools run asset.get-data <asset-path> --max-depth 3
unity_cli tools run asset.preview <asset-path>
```

### 材质、图形、物理

```powershell
unity_cli tools run material.info <path>
unity_cli tools run material.create <path> --shader Standard
unity_cli tools run material.assign --target <go> --material <path> --slot 0
unity_cli tools run material.set <path> --property _Color --value "#ff00ffff" --type color

unity_cli tools run graphics.quality --quality-level 2
unity_cli tools run graphics.settings --anti-aliasing 4
unity_cli tools run graphics.pipeline
unity_cli tools run graphics.volume "Global Volume"
unity_cli tools run graphics.screenshot <output-path>

unity_cli tools run physics.settings
unity_cli tools run physics.raycast --origin-x 0 --origin-y 2 --origin-z -5 --direction-z 1 --distance 100
unity_cli tools run physics.raycast-all --origin-x 0 --origin-y 2 --origin-z -5 --direction-z 1
unity_cli tools run physics.linecast --start-x 0 --start-y 0 --start-z 0 --end-x 10 --end-y 0 --end-z 0
unity_cli tools run physics.shapecast --shape sphere --origin-x 0 --origin-y 5 --origin-z 0 --direction-z -1 --radius 0.5
unity_cli tools run physics.shapecast --shape capsule --origin-x 0 --origin-y 5 --origin-z 0 --direction-z -1 --height 2 --radius 0.5
unity_cli tools run physics.validate --target Player
unity_cli tools run physics.force --target Ball --x 0 --y 500 --z 0 --mode Impulse
unity_cli tools run physics.force --target Crate --force-type explosion --explosion-force 1000 --explosion-radius 10
unity_cli tools run physics.simulation --steps 3 --delta-time 0.02 --simulate

unity_cli tools run texture.generate Assets/Tex/Checker.png --pattern checkerboard --pattern-size 8 --color "#000000" --color2 "#ffffff"
unity_cli tools run texture.generate Assets/Tex/Grad.png --pattern radial --color "#ff0000" --color2 "#0000ff"
```

### 控制台、包与构建

```powershell
unity_cli tools run console.get --type error --count 20
unity_cli tools run console.clear --yes

unity_cli tools run package.embed --package com.unity.inputsystem --yes

unity_cli tools run build.scenes
unity_cli tools run build.scenes --action add --path Assets/Scenes/Main.unity
unity_cli tools run build.platform
unity_cli tools run build.platform --action set --target Android
unity_cli tools run build.settings
```

### Profiler

```powershell
unity_cli tools run profiler.status
unity_cli tools run profiler.start
unity_cli tools run profiler.modules
unity_cli tools run profiler.areas
unity_cli tools run profiler.object-memory --target Player
unity_cli tools run profiler.rendering-stats
unity_cli tools run profiler.script-stats
unity_cli tools run profiler.clear --yes
unity_cli tools run profiler.load --path Assets/profiler.raw
```

### 动画、VFX、Shader、纹理

```powershell
unity_cli tools run animation.clip <path>
unity_cli tools run animation.state <target>
unity_cli tools run animation.play <target> --state Run
unity_cli tools run animation.stop <target>

unity_cli tools run vfx.graph --input '{"path":"<path>"}'
unity_cli tools run vfx.effect --input '{"target":"<target>","add":true,"graph":"<vfx-path>"}'

unity_cli tools run shader.info <path>
unity_cli tools run shader.read <path>
unity_cli tools run shader.create <path> --content-file <source>
unity_cli tools run shader.update <path> --content-file <source>

unity_cli tools run texture.info <path>
unity_cli tools run texture.create <path> --source <image>
unity_cli tools run texture.sprite <path> --pixels-per-unit 128
unity_cli tools run texture.import-settings <path> --readable false --mipmap false
```

### UI

```powershell
unity_cli tools run ui.canvas.create <name>
unity_cli tools run ui.text.create <name> --parent <canvas> --text "Hello"
unity_cli tools run ui.button.create <name> --parent <canvas> --label "Click"
unity_cli tools run ui.image.create <name> --parent <canvas> --sprite <sprite-path>
unity_cli tools run ui.document.create --name <name> --uxml <uxml-path> --uss <uss-path>
unity_cli tools run ui.document.info <target>
unity_cli tools run ui.hierarchy <target> --page-size 100
```

### GameObject 与组件

```powershell
unity_cli tools run go.find <keyword>
unity_cli tools run go.inspect <target>
unity_cli tools run go.create <name>
unity_cli tools run go.modify <target>
unity_cli tools run go.delete <target> --yes

unity_cli tools run component.add --target <go> --type <component>
unity_cli tools run component.remove --target <go> --type <component> --yes
unity_cli tools run component.set --target <go> --type <component> --property <name> --value <value>
```

### 编辑器控制

```powershell
unity_cli tools run editor.state
unity_cli tools run editor.play --timeout-ms 7000
unity_cli tools run editor.pause
unity_cli tools run editor.stop --timeout-ms 7000
unity_cli tools run editor.menu <menu-path>
unity_cli tools run editor.console --count 50
unity_cli tools run editor.refresh --compile --wait
```

### 代码执行

```powershell
unity_cli tools run code.exec --file <script.csx>
unity_cli tools run code.exec --stdin --channel live
unity_cli tools run code.exec --code "return UnityEditor.EditorApplication.isPlaying;"
unity_cli tools run code.exec --channel batchmode --project <path> --unity <unity-exe> --wait
```

### 包管理

```powershell
unity_cli tools run package.list --project <path>
unity_cli tools run package.info <package-name> --project <path>
unity_cli tools run package.search <keyword>
unity_cli tools run package.install <package@version> --project <path> --unity <unity-exe> --wait
unity_cli tools run package.remove <package-name> --project <path> --unity <unity-exe> --wait
unity_cli tools run package.resolve --project <path> --unity <unity-exe> --wait
unity_cli tools run package.registry get --project <path>
unity_cli tools run package.registry set --url <url> --project <path>
unity_cli tools run package.scoped-registry add --name <name> --url <url> --scope <scope> --project <path>
```

### 测试与构建

```powershell
unity_cli tools run test.run --platform EditMode --wait --project <path> --unity <unity-exe>
unity_cli jobs status <jobId>
unity_cli jobs logs <jobId> --tail 100
unity_cli jobs cancel <jobId> --yes

unity_cli tools run build.run --target StandaloneWindows64 --output <output-path> --wait
unity_cli jobs status <jobId>
unity_cli jobs logs <jobId> --tail 100
unity_cli jobs cancel <jobId> --yes
```

### Batch 自动化

```powershell
unity_cli batch schema
unity_cli batch run <batch-file.json>
unity_cli batch run <file> --preflight --parallel 4 --fail-fast false
```

### 实例与目标

```powershell
unity_cli instances list
unity_cli instances prune --yes

unity_cli target current
unity_cli target set <id|alias>
unity_cli target clear
unity_cli target alias set <instanceId> <alias>
unity_cli target alias unset <alias>
unity_cli target alias list
```

实例与目标属于 CLI 控制面，只使用顶层 `instances` / `target`；不要通过 `context read` 或 `tools run` 访问这些状态。

### Jobs、诊断与能力

```powershell
unity_cli jobs status <jobId>
unity_cli jobs logs <jobId> --tail 100
unity_cli jobs describe <jobId>
unity_cli jobs cancel <jobId>

unity_cli doctor
unity_cli tools run diagnose.preflight
unity_cli tools run diagnose.capabilities
```

### Context（只读项目状态）

```powershell
unity_cli context list
unity_cli context describe <name>
unity_cli context read <name> [--input '<json>']

# Resource 对齐 Context 示例
unity_cli context read scene.gameobject.components --input '{"instanceId":123}'
unity_cli context read prefab.info --input '{"path":"Assets/Prefabs/Hero.prefab"}'
unity_cli context read tools.groups
```

### 扩展包

```powershell
unity_cli extensions list
unity_cli extensions install <id>
unity_cli extensions remove <id>
unity_cli extensions doctor
```

### 离线文档与反射

```powershell
unity_cli tools run docs.list
unity_cli tools run docs.topic <topic>
unity_cli tools run docs.get <api-name>
unity_cli tools run docs.search <query>

unity_cli tools run reflect.type <type-name>
unity_cli tools run reflect.member <type.member>
unity_cli tools run reflect.search <query>
```

## 初始化（init）

`init` 安装捆绑的 `com.unity_cli` Bridge 包到目标 Unity 项目；如果项目根目录已经存在 `.pi`、`.claude` 或 `.agent`，会自动安装对应 AI Agent Skill。也可以用 `--agent` 显式指定目标。

| 标志 | 含义 |
|------|------|
| *(none)* | 安装/更新 Bridge 包（copy 模式），并为已存在的 Agent 目录安装 Skill |
| `--pkg-only` | 仅安装 Bridge 包，不安装 Skill |
| `--skill-only [--agent <agent>]` | 仅安装 Skill；未传 `--agent` 时使用已存在的 Agent 目录 |
| `--check` | 检查安装状态，不修改项目 |
| `--agent <pi\|claude\|.agent,...>` | 显式指定 Skill 安装目标 |

```powershell
# 默认安装 Bridge；若项目已有 .pi/.claude/.agent，则自动安装 Skill
unity_cli init --project C:\path\MyProject

# 仅 Bridge 包
unity_cli init --project C:\path\MyProject --pkg-only

# 显式安装 Skill 到 pi
unity_cli init --project C:\path\MyProject --skill-only --agent pi

# 检查安装状态；未传 --agent 时同样会检查已存在的 Agent 目录
unity_cli init --check --project C:\path\MyProject
```

## Unity Editor Bridge

Bridge 包（`com.unity_cli`）在 Unity Editor 中提供 `POST /bridge/execute` HTTP 端点。CLI 通过此端点路由实时命令。

Bridge 生命周期：
1. `init` 安装 Bridge 包到 `Packages/com.unity_cli/`（copy 模式）
2. Unity Editor 打开时自动启动 Bridge HTTP 服务
3. Bridge 自动向 CLI 注册实例（心跳机制）
4. CLI 通过 `instances list` 发现运行中的 Bridge 端点

### Bridge Command Reference

`camera.list`, `camera.info`, `camera.screenshot`, `scene.active`, `scene.hierarchy`, `scene.build-settings`, `scene.screenshot`, `scene.create`, `scene.load`, `scene.save`, `go.find`, `go.inspect`, `go.create`, `go.modify`, `go.duplicate`, `go.move-relative`, `go.look-at`, `go.delete`, `component.add`, `component.remove`, `component.set`

**Camera & Screenshot:**
- `camera.list`, `camera.info`, `camera.screenshot`, `camera.create`, `camera.target`, `camera.lens`, `camera.multiview`
- `camera.screenshot` captures render texture and returns artifact metadata
- `E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved
- `E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved

**Scene:**
- `scene.active`, `scene.hierarchy`, `scene.build-settings`, `scene.screenshot`, `scene.create`, `scene.load`, `scene.save`

**GameObject & Component:**
- `go.find`, `go.inspect`, `go.create`, `go.modify`, `go.duplicate`, `go.move-relative`, `go.look-at`, `go.delete`, `component.add`, `component.remove`, `component.set`

**Asset:**
- `asset.search`, `asset.info`, `asset.create`, `asset.import`, `asset.modify`, `asset.delete`, `asset.duplicate`, `asset.move`, `asset.rename`

**Prefab:**
- `prefab info <path>` returns Prefab metadata including GUID, root name, asset type, `variant` base/source paths and GUIDs, `dependencies.items`, and `nestedPrefabs.items` rather than counts only
- `prefab hierarchy <path>` pages hierarchy items with `pageInfo`, component summaries, the same Prefab variant/dependency summary, `nestedPrefabs.items` and `logPath`
- `E_PREFAB_PATH_REQUIRED`, `E_PREFAB_PATH_INVALID`, `E_PREFAB_NOT_FOUND`, `E_ASSET_NOT_PREFAB`, `E_PREFAB_OUTPUT_EXISTS`, `E_PREFAB_MODIFY_NOOP`, `E_PREFAB_CHILD_EXISTS`, `E_PREFAB_CHILD_NOT_FOUND`, `E_PREFAB_DIRTY_CONFLICT`, `E_PREFAB_STAGE_NOT_OPEN`, `E_PREFAB_STAGE_MISMATCH` and `E_UNITY_BUSY`

**UI:**
- `ui canvas/create`, `ui text/create`, `ui button/create` and `ui image/create` create uGUI objects
- `ui document/info` supports live UIDocument inspection and offline `.uxml`/`.uss` file reads
- `E_UI_PARENT_NOT_FOUND`, `E_UI_SPRITE_NOT_FOUND`, `E_UI_UXML_NOT_FOUND`, `E_UI_USS_NOT_FOUND`, `E_UI_PANEL_SETTINGS_NOT_FOUND`, `E_TARGET_NOT_FOUND`, `E_TARGET_AMBIGUOUS` and `E_UNITY_BUSY`

**Material:**
- `material info <path>` returns shader, keywords, ShaderUtil property metadata, current values, texture references, dependencies and `logPath`
- `material assign --target <go> --material <path>` assigns to a Renderer shared material slot or a UI Graphic `material` property
- `E_MATERIAL_PATH_REQUIRED`, `E_MATERIAL_PATH_INVALID`, `E_MATERIAL_NOT_FOUND`, `E_SHADER_NOT_FOUND`, `E_MATERIAL_PROPERTY_NOT_FOUND`, `E_MATERIAL_PROPERTY_TYPE_MISMATCH`, `E_TARGET_AMBIGUOUS`, `E_MATERIAL_SLOT_INVALID`, `E_TEXTURE_NOT_FOUND` and `E_UNITY_BUSY`

**Graphics & Physics:**
- `graphics quality` reads and updates `QualitySettings`
- `physics raycast` supports 3D and 2D ray queries
- `physics.raycast` supports 3D and 2D ray queries

**Shader & Texture:**
- `shader info/read/create/update`
- `texture info/create/sprite/import-settings`
- `E_SHADER_NOT_FOUND`, `E_TEXTURE_NOT_FOUND`, `E_IMPORT_FAILED`

**Animation & VFX:**
- `animation clip <path>` reads or creates `Assets/*.anim` AnimationClip assets
- `tools run vfx.graph --input '{"path":"<path>"}'` reads or creates `Assets/*.vfx` Visual Effect Graph assets
- Windows-style `Assets\VFX\Explosion.vfx` paths are normalized to Unity asset paths
- `E_VFX_PACKAGE_MISSING`, `E_VFX_GRAPH_NOT_FOUND`

**Editor:**
- `editor state` returns playMode, pause, busy, activeScene and Prefab Stage status
- `editor play`, `editor pause` and `editor stop` control Editor PlayMode
- `editor menu <path>` executes a Unity Editor menu item
- `editor console --count <n>` returns recent Unity Console entries
- `E_UNITY_BUSY`, `E_PLAYMODE_TIMEOUT`, `E_MENU_NOT_FOUND`, `E_MENU_TIMEOUT`

**Bridge HTTP timeout & side-effect safety:**
- If a request times out at the HTTP layer while the command is still queued, the bridge cancels that queued work before execution and returns `E_UNITY_BUSY` with no side effects applied
- If a request times out at the HTTP layer but the command has already started, it returns a success envelope with `status: "running"`, `sideEffectSemantics: "started-before-http-timeout"`, the original `requestId` and `logPath`

**Runtime validation disclaimer:**
- C# runtime behavior for empty camera targets, missing Cameras, screenshot IO failures, actual PNG/JPG artifacts, Prefab Stage open/save/close, Prefab dirty conflicts, nested/variant Prefab summaries, Material asset creation, Renderer/UI Graphic assignment, Material property writes, GraphicsSettings/QualitySettings persistence, render pipeline asset resolution, Physics/Physics2D project setting persistence, raycast collision hits, explicit simulation steps, AnimationClip asset creation/binding summaries, Animator state/play/stop behavior, VFX Graph asset creation, VisualEffect assignment, Shader import diagnostics, TextureImporter mutations, uGUI object creation, UIDocument/PanelSettings linking, UI hierarchy summaries and Runtime Code live execution must still be proven by real Windows + Unity Editor validation

**Script & Code:**
- `script.create`, `script.read`, `script.hash`, `script.edit`, `script.apply-edits`, `script.validate`, `code.exec`.

**Context & Extension:**
- `context.list`, `context.describe`, `context.read` — read-only context observations
- `extension.status` — extension availability check

**ProBuilder Extension:**
- Requires `com.unity.probuilder` package
- 36 tools: `probuilder.ping` through `probuilder.repair-mesh`

**Cinemachine, URP, HDRP Extensions:**
- Cinemachine: `cinemachine.create`, `cinemachine.target`, `cinemachine.lens`, etc.
- URP: `urp.feature`, `urp.volume`, `urp.settings`
- HDRP: `hdrp.volume`, `hdrp.settings`

## 项目自定义工具（Custom Tools）

Unity 项目 C# 代码通过 `[UnityCliTool]` 特性注册自定义工具：

```csharp
[UnityCliTool("my.editor-tool", Title = "My Editor-targeted Tool", Group = "custom")]
public static class MyEditorTool
{
    public static object Execute(Dictionary<string, object> input)
    {
        // 工具实现
        return new { result = "done" };
    }
}
```

特性参数：
- `name`：工具唯一标识（如 `my.editor-tool`）
- `Title`：人类可读标题
- `Group`：工具分组（如 `custom`）
- `Description`：工具描述
- `ReadOnlyHint`：是否只读（默认 false）
- `DestructiveHint`：是否有破坏性（默认 false）

发现与执行：
```powershell
unity_cli tools group list custom --include-custom
unity_cli tools describe my.editor-tool
unity_cli tools run my.editor-tool --input '{"key":"value"}'
```

### 统一 Tool 与 target

所有能力都以 Tool 呈现。`target: "editor" | "runtime" | "both"` 只是可用性与路由约束，不是两套用户入口。

`runtime` 表示 Unity gameplay/runtime layer；具体 tool 是否需要 Play Mode，由 `availabilityScope` 和 `next_actions` 说明。

### 安全机制

- **Descriptor**：每个工具必须有完整的 inputSchema/outputSchema
- **Envelope**：所有输入输出必须通过统一 envelope 格式
- **Fingerprint**：handler 代码 + descriptor 的 SHA256 指纹，mismatch 时默认阻止执行
- **target-scoped availability**：工具可用性依赖目标实例和 Play Mode 状态
- **禁止项**：不允许任意方法调用、反射 escape hatch、绕过 registry 直接执行

## ProBuilder 扩展

ProBuilder 工具需要项目先安装 `com.unity.probuilder`，再安装 CLI extension bundle：

```powershell
unity_cli extensions install probuilder --project <project>
unity_cli tools group list probuilder --project <project>
unity_cli tools run probuilder.create-shape --project <project> --input '{"shapeType":"Cube","name":"MyCube"}'
unity_cli tools run probuilder.extrude-faces --project <project> --input '{"target":"<target>","faceIndices":[1,2,3],"distance":0.5}'
unity_cli tools run probuilder.delete-faces --project <project> --input '{"target":"<target>","faceIndices":[4,5]}' --yes
```

## 风险与破坏性工具

`destructiveHint: true` 的工具执行前需要确认：

```powershell
# 预览（如工具支持 dry-run）
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --dry-run

# 确认执行
unity_cli tools run asset.delete --input '{"path":"Assets/Temp.mat"}' --yes
```

支持 `--dry-run` 的破坏性工具：`asset.delete`、`prefab.save`、`go.delete`、`component.remove`、`probuilder.delete-faces` 等。

## 扩展能力概览

| 扩展类型 | 当前状态 | 发现方式 |
|----------|---------|---------|
| 项目自定义 Tool (`target=editor`) | ✅ 已实现 | `tools group list custom --include-custom` |
| 项目自定义 Tool (`target=runtime`) | ✅ 已实现 | `tools group list custom --include-custom`（通常需 Play Mode） |
| 项目自定义 Context | ✅ 已实现 | `context list/describe/read` |
| 扩展包管理 | ✅ 已实现 | `extensions list/install/remove/doctor` |
| ProBuilder 扩展 | ✅ 已实现 | `tools group/list/describe/run`（需要 `com.unity.probuilder` + `extensions install probuilder`） |
| Cinemachine/VFX/URP/HDRP 扩展 | ✅ 已实现 | `tools group/list/describe/run`（需要对应 Unity package + `extensions install <id>`） |

## 更多文档

- [文档索引](docs/README.md) — 当前使用、扩展与诊断文档入口
- [Target 与 Instance](docs/target-and-instance.md) — 目标选择与实例管理
- [工具发现](docs/tool-discovery.md) — group-first 分层发现详解
- [工具执行](docs/tool-execution.md) — tools run 执行通道详解
- [安全边界](docs/safety-boundaries.md) — 风险模型与破坏性确认
- [风险与副作用](docs/risk-and-side-effects.md) — readOnly/destructive/dry-run 规则
- [错误码](docs/error-codes.md) — 结构化错误与 next_actions
- [可用性差异](docs/availability-differences.md) — 不同实例的工具有效性
- [扩展包](docs/extensions.md) — 可选扩展安装、卸载与诊断
- [自定义 Tool](docs/custom-tools.md) — 项目侧 Tool 注册开发
- [自定义 Context](docs/custom-contexts.md) — 项目侧只读 Context 注册开发
- [AI Agent Skill](skills/unity_cli/SKILL.md) — Agent 最小入口卡片

## 本地开发

```bash
npm install
npm run build
npm test
npm link
unity_cli --help
```

## 许可证

MIT
