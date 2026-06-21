# unity_cli

CLI-first Unity 自动化工具，不是 MCP server。通过 `unity_cli` 二进制命令行直接驱动 Unity Editor，无需额外协议层。

npm 包名：`@alps_ckr/unity_cli`  
运行环境：Windows + Node.js 20+  
当前方向：ALP-22 冻结决策 — 独立 CLI 实现、Windows-only、npm-first 安装、开放能力无默认风险门控。

## 快速开始

```powershell
# 安装
npm install -g @alps_ckr/unity_cli

# 初始化项目（安装 Bridge 包 + Skill 文件）
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
unity_cli docs

# 查看特定主题
unity_cli docs topic tool-discovery

# 离线 Unity API 文档
unity_cli docs get UnityEngine.Camera
unity_cli docs search "Render Pipeline"

# 离线类型反射
unity_cli reflect type UnityEngine.Camera
unity_cli reflect member UnityEngine.Camera.fieldOfView
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
unity_cli diagnose preflight
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
unity_cli job status --id <jobId>
unity_cli job logs --id <jobId> --tail 100
unity_cli job cancel --id <jobId>
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
unity_cli scene hierarchy --page-size 100
unity_cli scene active
unity_cli scene screenshot <output-path> --width 1920 --height 1080
unity_cli scene screenshot <output-path> --format jpg --quality 90 --overwrite

unity_cli camera list
unity_cli camera info "Main Camera"
unity_cli camera screenshot "Main Camera" --output shot.png --width 1920 --height 1080

unity_cli screenshot capture <output-path>
```

### 资产与预制体

```powershell
unity_cli asset search <keyword> --type Prefab
unity_cli asset info <path>
unity_cli asset create <path> --content '{}' --overwrite
unity_cli asset delete <path> --yes
unity_cli asset duplicate <path> --to <new-path>
unity_cli asset move <path> --to <new-path>
unity_cli asset rename <path> --name <new-name>
unity_cli asset import <source> --project <project-path>

unity_cli prefab info <path>
unity_cli prefab create <source> --path <prefab-path>
unity_cli prefab modify <path> --name <new-name>
unity_cli prefab stage-open <path>
unity_cli prefab stage-save
unity_cli prefab stage-close --save
unity_cli prefab hierarchy <path> --page-size 100
```

### 材质、图形、物理

```powershell
unity_cli material info <path>
unity_cli material create <path> --shader Standard
unity_cli material assign --target <go> --material <path> --slot 0
unity_cli material set <path> --property _Color --value "#ff00ffff" --type color

unity_cli graphics quality --quality-level 2
unity_cli graphics settings --anti-aliasing 4
unity_cli graphics pipeline
unity_cli graphics volume "Global Volume"
unity_cli graphics screenshot <output-path>

unity_cli physics settings
unity_cli physics raycast --origin-x 0 --origin-y 2 --origin-z -5 --direction-z 1 --distance 100
unity_cli physics simulation --steps 3 --delta-time 0.02 --simulate
```

### 动画、VFX、Shader、纹理

```powershell
unity_cli animation clip <path>
unity_cli animation state <target>
unity_cli animation play <target> --state Run
unity_cli animation stop <target>

unity_cli vfx graph <path>
unity_cli vfx effect <target> --add --graph <vfx-path>

unity_cli shader info <path>
unity_cli shader read <path>
unity_cli shader create <path> --content-file <source>
unity_cli shader update <path> --content-file <source>

unity_cli texture info <path>
unity_cli texture create <path> --source <image>
unity_cli texture sprite <path> --pixels-per-unit 128
unity_cli texture import-settings <path> --readable false --mipmap false
```

### UI

```powershell
unity_cli ui canvas-create <name>
unity_cli ui text-create <name> --parent <canvas> --text "Hello"
unity_cli ui button-create <name> --parent <canvas> --label "Click"
unity_cli ui image-create <name> --parent <canvas> --sprite <sprite-path>
unity_cli ui document-create --name <name> --uxml <uxml-path> --uss <uss-path>
unity_cli ui document-info <target>
unity_cli ui hierarchy <target> --page-size 100
```

### GameObject 与组件

```powershell
unity_cli go find <keyword>
unity_cli go inspect <target>
unity_cli go create <name>
unity_cli go modify <target>
unity_cli go delete <target> --yes

unity_cli component add --target <go> --type <component>
unity_cli component remove --target <go> --type <component> --yes
unity_cli component set --target <go> --type <component> --property <name> --value <value>
```

### 编辑器控制

```powershell
unity_cli editor state
unity_cli editor play --timeout-ms 7000
unity_cli editor pause
unity_cli editor stop --timeout-ms 7000
unity_cli editor menu <menu-path>
unity_cli editor console --count 50
unity_cli editor refresh --compile --wait
```

### 代码执行

```powershell
unity_cli code exec --file <script.csx>
unity_cli code exec --stdin --channel live
unity_cli code exec --code "return UnityEditor.EditorApplication.isPlaying;"
unity_cli code exec --channel batchmode --project <path> --unity <unity-exe> --wait
```

### 包管理

```powershell
unity_cli package list --project <path>
unity_cli package info <package-name> --project <path>
unity_cli package search <keyword>
unity_cli package install <package@version> --project <path> --unity <unity-exe> --wait
unity_cli package remove <package-name> --project <path> --unity <unity-exe> --wait
unity_cli package resolve --project <path> --unity <unity-exe> --wait
unity_cli package registry get --project <path>
unity_cli package registry set --url <url> --project <path>
unity_cli package scoped-registry add --name <name> --url <url> --scope <scope> --project <path>
```

### 测试与构建

```powershell
unity_cli test run --platform EditMode --wait --project <path> --unity <unity-exe>
unity_cli test poll --id <jobId>
unity_cli test logs --id <jobId> --tail 100
unity_cli test cancel --id <jobId>

unity_cli build run --target StandaloneWindows64 --output <output-path> --wait
unity_cli build status --id <jobId>
unity_cli build logs --id <jobId> --tail 100
unity_cli build cancel --id <jobId>
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
unity_cli instances prune

unity_cli target get
unity_cli target set <id|alias>
unity_cli target clear
unity_cli target alias set <instanceId> <alias>
unity_cli target alias unset <alias>
unity_cli target alias list
```

### 诊断与能力

```powershell
unity_cli doctor
unity_cli diagnose preflight
unity_cli capabilities
```

### Context（只读项目状态）

```powershell
unity_cli context list
unity_cli context describe <name>
unity_cli read <name> [--input '<json>']
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
unity_cli docs
unity_cli docs topic <topic>
unity_cli docs get <api-name>
unity_cli docs search <query>

unity_cli reflect type <type-name>
unity_cli reflect member <type.member>
unity_cli reflect search <query>
```

## 初始化（init）

`init` 安装捆绑的 `com.unity_cli` Bridge 包到目标 Unity 项目，并可选安装 AI Agent Skill 文件。

| 标志 | 含义 |
|------|------|
| *(none)* | 安装/更新 Bridge 包（copy 模式） |
| `--pkg-only` | 仅安装 Bridge 包，不安装 Skill |
| `--skill-only --agent <agent>` | 仅安装 Skill 文件 |
| `--check` | 检查安装状态，不修改项目 |
| `--agent <pi\|claude\|.agent,...>` | 指定 Skill 安装目标 |

```powershell
# 完整安装
unity_cli init --project C:\path\MyProject

# 仅 Bridge 包
unity_cli init --project C:\path\MyProject --pkg-only

# 仅安装 Skill 到 pi
unity_cli init --project C:\path\MyProject --skill-only --agent pi

# 检查安装状态
unity_cli init --check --project C:\path\MyProject --agent pi
```

## Unity Editor Bridge

Bridge 包（`com.unity_cli`）在 Unity Editor 中提供 `POST /bridge/execute` HTTP 端点。CLI 通过此端点路由实时命令。

Bridge 生命周期：
1. `init` 安装 Bridge 包到 `Packages/com.unity_cli/`（copy 模式）
2. Unity Editor 打开时自动启动 Bridge HTTP 服务
3. CLI 通过 `instances list` 发现运行中的 Bridge 端点
4. Bridge 自动向 CLI 注册实例（心跳机制）

### Bridge Command Reference

`camera.list`, `camera.info`, `camera.screenshot`, `scene.active`, `scene.hierarchy`, `scene.build-settings`, `scene.screenshot`, `scene.create`, `scene.load`, `scene.save`, `go.find`, `go.inspect`, `go.create`, `go.modify`, `go.delete`, `component.add`, `component.remove`, `component.set`

**Camera & Screenshot:**
- `camera.list`, `camera.info`, `camera.screenshot`, `camera.create`, `camera.target`, `camera.lens`, `camera.multiview`
- `camera.screenshot` captures render texture and returns artifact metadata
- `E_CAMERA_NOT_FOUND` for `camera info` when a target Camera cannot be resolved
- `E_TARGET_NOT_FOUND` for `camera screenshot` when the target is empty or cannot be resolved

**Scene:**
- `scene.active`, `scene.hierarchy`, `scene.build-settings`, `scene.screenshot`, `scene.create`, `scene.load`, `scene.save`

**GameObject & Component:**
- `go.find`, `go.inspect`, `go.create`, `go.modify`, `go.delete`, `component.add`, `component.remove`, `component.set`

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
- `vfx graph <path>` reads or creates `Assets/*.vfx` Visual Effect Graph assets
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
- `script.create`, `script.read`, `script.edit`, `script.validate`, `code.exec`.

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
[UnityCliTool("my.editor-tool", Title = "My Editor Tool", Group = "custom")]
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

### Editor Tool 与 Runtime Tool

- **Editor Tool**：`target: "editor"`，在编辑器主线程执行，适合资产/场景/配置修改
- **Runtime Tool**：`target: "runtime"`，需要 Play Mode 活跃，适合运行时域操作

Runtime Tool 在非 Play Mode 时返回 `E_RUNTIME_NOT_READY`（transient error）。

### 安全机制

- **Descriptor**：每个工具必须有完整的 inputSchema/outputSchema
- **Envelope**：所有输入输出必须通过统一 envelope 格式
- **Fingerprint**：handler 代码 + descriptor 的 SHA256 指纹，mismatch 时默认阻止执行
- **target-scoped availability**：工具可用性依赖目标实例和 Play Mode 状态
- **禁止项**：不允许任意方法调用、反射 escape hatch、绕过 registry 直接执行

## ProBuilder 扩展

ProBuilder 命令需要 `com.unity.probuilder` 包：

```powershell
unity_cli probuilder shape --type Cube --position-x 0 --position-y 1 --position-z 0
unity_cli probuilder create --type Cube
unity_cli probuilder edit <target> --action Extrude --amount 0.5
unity_cli probuilder csg --operation Union --target-a <a> --target-b <b> --yes
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

架构敏感工具（`architectureSensitive: true`）包括所有 `profiler.*` 工具，修改前需审查 `frozenDecision` 字段。

## 扩展能力概览

| 扩展类型 | 当前状态 | 发现方式 |
|----------|---------|---------|
| 项目自定义 Tool (Editor) | ✅ 已实现 | `tools group list custom --include-custom` |
| 项目自定义 Tool (Runtime) | ✅ 已实现 | `tools group list custom --include-custom`（Play Mode 下） |
| 项目自定义 Context | ✅ 已实现 | `context list/describe/read` |
| 扩展包管理 | ✅ 已实现 | `extensions list/install/remove/doctor` |
| ProBuilder 扩展 | ✅ 已实现 | 自动包含（需要 com.unity.probuilder） |
| Cinemachine/VFX/URP/HDRP 扩展 | 📋 Backlog（P8） | 待实现 |

## 更多文档

- [文档索引](docs/README.md) — 按主题组织的详细文档
- [工具发现](docs/tool-discovery.md) — group-first 分层发现详解
- [工具执行](docs/tool-execution.md) — tools run 执行通道详解
- [安全边界](docs/safety-boundaries.md) — 风险模型与破坏性确认
- [错误码](docs/error-codes.md) — 结构化错误与 next_actions
- [可用性差异](docs/availability-differences.md) — 不同实例的工具有效性
- [Target 与 Instance](docs/target-and-instance.md) — 目标选择与实例管理
- [自定义工具指南](docs/custom-tools.md) — 项目侧 Tool 注册开发
- [AI Agent Skill](skills/unity_cli/SKILL.md) — Agent 操作手册
- [示例](examples/README.md) — 编辑器/运行时/自定义工具示例

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
