# unity_cli

CLI-first Unity 自动化工具，不是 MCP server。通过 `unity_cli` 二进制命令行直接驱动 Unity Editor，无需额外协议层。

npm 包名：`@alpsckr/unity_cli`
运行环境：Windows + Node.js 20+  
当前方向：冻结决策 - 独立 CLI 实现、Windows-only、npm-first 安装；公开 Tool 与 CLI 控制面调用即授权。

## Project Baseline

本节是项目基线，优先级高于本文档其他章节、历史实现、旧示例和当前代码状态。AI 和后续实现不得自行修改本节含义；如需调整，必须由项目所有者明确确认。

### 1. Clean Project Semantics

本项目是开发中项目，不承担旧版本兼容义务。

项目代码、文档、命令形态和注释必须表达当前项目规范。发现旧语义、阶段性语义或兼容性语义时，应统一改为当前规范；无法干净迁移时，优先删除，而不是保留多套说法。

### 2. Top-Level CLI Shape

用户可见顶层命令分为两类：能力入口和 CLI 控制面。

能力入口只允许：

- `tools`
- `command`

CLI控制面只允许：

- `init`
- `extensions`
- `target`
- `instances`
- `doctor`

Unity项目内容与Editor能力域必须进入唯一Tool catalog，例如scene、asset、game-object、prefab、editor、screenshot、package、docs、reflect、build、test、code、profiler、physics、graphics等。`tools run <tool> --input <json>`与`command <name> <business-args>`只是同一descriptor和dispatcher的两种调用语法。

`target`、`instances`、`doctor` 是 CLI 自身的状态、路由和诊断控制面；它们可以作为顶层命令存在，但不得重新引入 Unity 能力域的顶层入口。

内部 Bridge、协调器和 registry 可以作为实现细节存在，但不得定义超出上述清单的用户可见顶层命令形态。

本产品只吸收必须由已打开 Unity Editor 或 development Player 提供的项目内自动化价值。auth、Unity Hub、Editor 安装或打开、Cloud、License 和 proxy 配置属于外部控制面，必须直接使用系统或官方控制面工具，不进入 Tool catalog，也不新增顶层命令。

### 3. AI-First Tool Interface

本 CLI 主要提供给 AI 使用。项目哲学不是维护一套面向人工记忆的传统命令手册，而是提供可发现、可描述、可审计的工具目录。

AI 的标准流程是：

```text
unity_cli tools group/search
unity_cli tools describe <tool>
unity_cli tools run <tool> --input <json>
unity_cli command
unity_cli command <name> --field <value>

unity_cli target alias list/set/unset
unity_cli instances list/prune
unity_cli doctor

unity_cli extensions list/install/remove/doctor
unity_cli init
```

具体有哪些工具、工具参数是什么、风险边界是什么，应由AI通过本CLI内置的`tools group/search/describe`自发现。省略name的`command`只返回同一live catalog的command投影，不是第二份能力目录。

## 快速开始

```powershell
# 安装
npm install -g @alpsckr/unity_cli

# 初始化项目（安装 Bridge 包；若项目已有 .pi/.claude/.agents，则自动安装 Skill）
unity_cli init --project C:\path\MyProject

# Bridge AutoStart默认开启；项目级开关位于Unity的Project Settings > Unity CLI
# 修改后从下一次Domain Reload或Editor启动生效

# 查看帮助
unity_cli --help
unity_cli --version
```

## 核心使用流程

### 1. 发现目标实例

```powershell
# 默认只列非 offline 实例；hiddenOfflineCount 表示被隐藏数量
# invalidRecordCount 出现时表示旧/坏记录已隔离，合法v3实例仍会返回
unity_cli instances list
# 需要审计历史/offline 合法记录时显式展开
unity_cli instances list --all

# 已知实例时，在后续每条 Bridge 命令上显式传入
unity_cli tools group --instance <instanceId>
# 可先创建稳定别名，再继续显式传入
unity_cli target alias set <instanceId> my-project
unity_cli tools group --instance my-project
```

### 2. 发现工具能力（group-first 路径）

推荐按组逐层发现，避免一次展开全量工具：

```powershell
# 第一步：查看工具分组
unity_cli tools group

# 第二步：按组查看工具摘要
unity_cli tools group list camera
# 或搜索关键词（使用 | 分隔多关键词，OR 匹配；PowerShell必须单引号包住整段）
unity_cli tools search 'screenshot|camera'

# 第三步：查看工具完整描述（执行前必做）
unity_cli tools describe camera.screenshot

# 第四步：执行工具
unity_cli tools run camera.screenshot --input '{"target":"Main Camera","output":"Artifacts/shot.png"}'
```

### 3. 获取只读项目信息

当前版本通过 `docs.get/search` 查询 Unity API 文档，通过 `reflect.*` 查询类型缓存；这些 Tool 仍从已连接实例的统一 catalog 发现和调用：

```powershell
# Unity API 文档
unity_cli tools run docs.get UnityEngine.Camera
unity_cli tools run docs.search "Render Pipeline"

# 类型反射
unity_cli tools run reflect.type UnityEngine.Camera
unity_cli tools run reflect.member UnityEngine.Camera.fieldOfView
```

> **注意**：项目与 Editor 状态同样通过 `tools group/search/describe/run` 发现和读取；读取型 Tool 明确声明无副作用。

### 4. 项目注册能力

Unity 项目可通过异步 C# `IUnityCliTool<TReq,TRes>` 注册能力；完成编译后自动进入该实例的完整目录，与 package 内置和 Extension 能力使用同一路径：

```powershell
# 从当前实例目录发现真实 group
unity_cli tools group --instance <id|projectPath|alias>

# 描述并执行项目 Tool
unity_cli tools describe my.custom-tool --instance <id|projectPath|alias>
unity_cli tools run my.custom-tool --input '{"key":"value"}' --instance <id|projectPath|alias>
```

> **注意**：不要假设项目能力位于 `custom` 分组。可通过 `extensions list` 查看已安装扩展，`extensions install <id>` / `extensions remove <id>` 管理扩展，`extensions doctor` 诊断扩展状态。

### 5. 使用command语法

`command`按目标Tool的live `inputSchema`解析named、attached、boolean裸开关和声明顺序positional参数，然后进入与`tools run`相同的dispatcher和JSON envelope：

```powershell
# 列出当前实例可调用名称
unity_cli command --project-path C:\path\MyProject

# native Tool使用完整Tool名
unity_cli command editor.compile --refresh true --includeWarnings true

# [CliCommand("test")]在tools中名为UnityPipeline_test，command可用attribute原名
unity_cli command test --mode EditMode
unity_cli command UnityPipeline_test --mode EditMode
```

命令名和业务字段exact匹配，不做snake_case、camelCase、相似名称或语义映射。native完整精确名优先；重名attribute仍通过`UnityPipeline_`完整名调用。

`com.unity.pipeline` package自身的attribute命令已由native能力矩阵吸收，不进入live目录；项目和其他Package声明的`[CliCommand]`继续正常导入。本package首次检测到Pipeline时只执行一次`AutoStart=false`持久化和当前server停服，成功后不再覆盖用户后续手动操作。

### 6. 诊断与健康检查

```powershell
# 运行完整诊断
unity_cli doctor

# 诊断预检（通过 Bridge）
unity_cli tools run diagnose.preflight
```

## 安全边界

| 规则 | 说明 |
|------|------|
| Tool 可有副作用 | 调用即授权；`destructiveHint`、`sideEffects` 与 `supportsDryRun` 仅描述风险和可选预览 |
| 读取型 Tool 纯只读 | 读取型 Tool 不触发写入、import、compile、domain reload 或 PlayMode 变化 |
| Extension 不能绕过 descriptor/envelope | 项目工具必须可描述、可审计、可阻断 |
| 内部完整性不匹配默认阻止 | 自定义工具的 handler 代码与 descriptor 不一致时，默认阻止执行 |

## 安装与构建

```powershell
# 全局安装
npm install -g @alpsckr/unity_cli

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
    "code": "E_INSTANCE_CONFLICT",
    "message": "...",
    "transient": false,
    "nextActions": ["Close duplicate Editor sessions for the selected project."],
    "retryAfterMs": 0
  }
}
```

## Live Editor 执行

需要 Unity 执行的 Tool 只连接已打开的 Unity Editor，并在同一次 direct 调用中返回最终业务结果。`build.run`、`test.run`、`editor.refresh`、`code.exec` 以及需要 Editor 刷新的 Package Tool 都会等待终态；它们不创建可查询的后台任务。调用CLI的外层进程必须等待CLI退出，host等待预算至少设为传给CLI的`--timeout-ms`加5000毫秒，不能把CLI放到后台后提前返回。CLI在自身deadline到达时返回一个JSON timeout envelope，并在stdout写完或最多再等待1秒后退出。

`code.exec`保留完整同步C#执行能力：它不会拒绝死循环、阻塞等待或长期占用Unity主线程的代码。提交这类代码前应先判断是否确有必要；一旦开始执行，timeout只能停止CLI等待，不能中止代码，结果可能未知且Unity可能需要结束进程才能恢复。CLI进程退出也不证明Unity中的C#已停止。完整边界见`tools describe code.exec`和`docs/safety-boundaries.md`。

## 能力发现与执行

公开能力不维护静态命令清单。Tool 的当前输入 schema、可用性、示例与调用边界以 CLI discovery 为准；结果 schema 留在内部 contract 中校验实际业务 data：

```powershell
unity_cli tools group
unity_cli tools group list <group>
unity_cli tools search <k1|k2|...>
unity_cli tools describe <tool-name>
unity_cli tools run <tool-name> --input '<json>' [--instance <id|projectPath|alias>]
```

Tool discovery 必须绑定已连接实例；未连接时返回稳定 instance error，不返回静态或空目录。连接后完整显示 package 内置、Extension、项目注册、Editor 与 Runtime 能力，不按 `source` 或当前模式过滤。`source` 只作内部描述/审计。`examples[].input` 是结构化业务 JSON，按需 `examples[].execution` 承载 dryRun/timeoutMs；调用方明确要求的业务值优先于schema default和example值。业务参数全部放入 `--input`、`--input-file` 或 `--stdin`，命令控制项不混入业务 JSON。

Bridge-backed Tool在Unity主线程执行。CLI使用Registry/Health v3与只读内存pulse，在一个不可变deadline内处理compile、import、domain reload和listener generation交接。mutation只允许在同一`editorSessionId`内用完全相同的请求续接；首次POST后session改变时禁止向新session重投，并返回outcome unknown的`E_EXECUTION_TIMEOUT`。`unity_cli tools run editor.state --input '{}'`、`editor.dialogs`与`editor.dialog.click`使用exact frozen本地descriptor进入同一dispatcher，因此统一状态观察和弹窗恢复不依赖被阻断的Live catalog；其他Tool仍严格依赖Live catalog。

### 实例与目标

```powershell
# 默认隐藏 offline，使用 --all 展开完整集合
unity_cli instances list
unity_cli instances list --all
unity_cli instances prune
```

实例文件逐个按Registry v3校验。旧格式、损坏或不可读记录不会阻断其他合法实例，也不会被兼容解析或自动删除；存在时list/prune返回`invalidRecordCount`，`doctor`报告warn。显式选择对应坏记录仍严格失败。

```powershell
# target 只管理 canonical project identity 的别名
unity_cli target alias set <projectPath|instanceId> <alias>
unity_cli target alias unset <alias>
unity_cli target alias list
```

实例与目标属于 CLI 控制面，只使用顶层 `instances` / `target`；不要通过 `tools run` 包装这些控制面状态。

`doctor`、`instances`、`target alias` 和 `extensions` 属于 CLI 控制面。扩展源码以 `extensions/<id>/` 组织，安装后投影到 `Packages/com.unity_cli/Extensions/<id>/`；Extension 贡献的 Tool 仍通过统一 discovery 入口发现。

## 初始化（init）

`init` 安装捆绑的 `com.unity_cli` Bridge 包到目标 Unity 项目；如果项目根目录已经存在 `.pi`、`.claude` 或 `.agents`，会自动安装对应 AI Agent Skill。也可以用 `--agent` 显式指定目标。Skill 以 `SKILL.md`、`references/**`、`scripts/**` 完整 runtime bundle 安装，`--check` 校验全部 managed 文件；更新会保留已安装 `SKILL.md` 中合法的 `FeedbackMode: off|on`。

| 标志 | 含义 |
|------|------|
| *(none)* | 安装/更新 Bridge 包（copy 模式），并为已存在的 Agent 目录安装 Skill |
| `--pkg-only` | 仅安装 Bridge 包，不安装 Skill |
| `--skill-only [--agent <agent>]` | 仅安装 Skill；未传 `--agent` 时使用已存在的 Agent 目录 |
| `--check` | 检查安装状态，不修改项目 |
| `--agent <pi\|claude\|.agents,...>` | 显式指定 Skill 安装目标 |

```powershell
# 默认安装 Bridge；若项目已有 .pi/.claude/.agents，则自动安装 Skill
unity_cli init --project C:\path\MyProject

# 仅 Bridge 包
unity_cli init --project C:\path\MyProject --pkg-only

# 显式安装 Skill 到 pi
unity_cli init --project C:\path\MyProject --skill-only --agent pi

# 检查安装状态；未传 --agent 时同样会检查已存在的 Agent 目录
unity_cli init --check --project C:\path\MyProject
```

`FeedbackMode` 默认 `off`。改为 `on` 后，使用该 Skill 的 Agent 只在实际遇到失败、歧义、缺失、误导或成功绕过的坑点时，调用 bundle 内 recorder 写入 `%LOCALAPPDATA%\UnityCli\feedback\` 的有界 JSONL；正常调用不记录，也不联网。

## Unity Editor Bridge

`init` 以 copy 模式把 `com.unity_cli` 安装到 `Packages/com.unity_cli/`。已打开的 Unity Editor 自动注册 canonical project identity 和 Live listener；CLI 通过 `instances list` 与 `--instance` 选择唯一会话，公开接口不接受 endpoint 或 port。

Bridge command、handler、journal、fingerprint、listener generation和completion contract都是内部实现，不构成第二套用户入口。mutation在首个真实effect前持久化effect barrier；effect后只有fresh business readback或artifact proof能完成。同一次调用只在`editorSessionId`不变时续接完全相同的请求；首次POST后session改变则禁止重投。无法证明终态时返回`E_EXECUTION_TIMEOUT`，调用方先观察业务状态，再作全新提交决策。

当前能力、Extension 依赖和 Editor/PlayMode 可用性只通过 `tools` discovery 获取，不在 README 冻结能力名清单。

## 项目自定义工具（Custom Tools）

Unity 项目 C# 代码可通过异步 `IUnityCliTool<TReq,TRes>` 注册自定义能力。schema 由 DTO 派生；Tool 必须声明完整 direct completion contract，并在全部可预见校验后、首个 mutation 前调用一次 `ctx.CommitEffect()`。当前接口与完整示例见 [Custom Tools](skills/unity_cli/references/custom-tools.md)。

```powershell
unity_cli tools group --instance <id|projectPath|alias>
unity_cli tools group list <real-group> --instance <id|projectPath|alias>
unity_cli tools describe my.editor-tool --instance <id|projectPath|alias>
unity_cli tools run my.editor-tool --input '{"message":"hello"}' --instance <id|projectPath|alias>
```

### 统一 Tool 与 target

所有能力都以 Tool 呈现。`target: "editor" | "runtime" | "both"` 只是可用性与路由约束，不是两套用户入口。

`runtime` 表示 Unity gameplay/runtime layer；具体 tool 是否需要 Play Mode，由 `availabilityScope` 和 `nextActions` 说明。

### 安全机制

- **Descriptor**：公开 describe 提供完整 inputSchema 与调用/风险信息；内部 metadata 保留 outputSchema 并校验业务结果
- **Envelope**：所有输入输出必须通过统一 envelope 格式
- **内部完整性校验**：handler 与 descriptor 不一致时默认阻止执行
- **target-scoped availability**：工具可用性依赖目标实例和 Play Mode 状态
- **禁止项**：不允许任意方法调用、反射 escape hatch、绕过 registry 直接执行

## ProBuilder 扩展

ProBuilder 工具需要项目先安装精确版本 `com.unity.probuilder@5.2.4`，再安装 CLI 扩展目录；其他版本不执行基于 5.2.4 源码闭合的 mutation proof：

```powershell
unity_cli extensions install probuilder --project <project>
unity_cli tools group list probuilder --instance <id|projectPath|alias>
unity_cli tools describe probuilder.create-shape --instance <id|projectPath|alias>
unity_cli tools run probuilder.create-shape --input '{"shapeType":"Cube","name":"MyCube"}' --instance <id|projectPath|alias>
```

## 风险与副作用

公开 Tool 与 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 和 `supportsDryRun` 让调用方了解效果与可选预览，但不会触发额外确认、预定义按钮列表或意图复核。支持 dry-run 的 Tool 可用 `--dry-run` 返回 `plannedEffects` 而不执行实际 mutation；不使用它不会阻止调用。

## 扩展能力概览

| 扩展类型 | 当前状态 | 发现方式 |
|----------|---------|---------|
| 项目自定义 Tool (`target=editor`) | ✅ 已实现 | 当前实例 `tools group/list/describe/run` |
| 项目自定义 Tool (`target=runtime`) | ✅ 已实现 | 当前实例 `tools group/list/describe/run`；EditMode 仍显示，执行通常需 PlayMode |
| 扩展包管理 | ✅ 已实现 | `extensions list/install/remove/doctor` |
| ProBuilder 扩展 | ✅ 已实现 | `tools group/list/describe/run`（需要 `com.unity.probuilder@5.2.4` + `extensions install probuilder`） |
| Cinemachine/VFX/URP/HDRP 扩展 | ✅ 已实现 | `tools group/list/describe/run`（需要对应 Unity package + `extensions install <id>`） |

## 更多文档

- [文档索引](docs/README.md) — 当前使用、扩展与诊断文档入口
- [Target 与 Instance](docs/target-and-instance.md) — 目标选择与实例管理
- [工具发现](docs/tool-discovery.md) — group-first 分层发现详解
- [工具执行](docs/tool-execution.md) — tools run 执行通道详解
- [安全边界](docs/safety-boundaries.md) — 调用授权、风险元数据与正确性边界
- [风险与副作用](docs/risk-and-side-effects.md) — destructive/dry-run 描述与可选预览
- [错误码](docs/error-codes.md) — 结构化错误与 nextActions
- [可用性差异](docs/availability-differences.md) — 不同实例的工具有效性
- [扩展包](docs/extensions.md) — 可选扩展安装、卸载与诊断
- [自定义 Tool](skills/unity_cli/references/custom-tools.md) — 项目侧 Tool 注册开发
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
