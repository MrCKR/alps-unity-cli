# AGENTS.md

本文件是 `alps-unity-cli` 的常驻 AI 开发硬规范，作用范围为整个仓库。除非项目所有者明确允许，任何 AI Agent 都必须遵守。领域细则通过本文件指定的项目 Skill 按任务强制加载。

## 项目定位

`unity_cli` 是面向 AI 的 Windows-first Unity Editor 自动化 CLI，不是 MCP server。

核心目标不是维护供人记忆的传统命令手册，而是提供可发现、可描述、可审计、可验证的能力目录，让陌生 AI 仅凭公开 CLI 一次发现、一次理解、一次安全调用。

本CLI是AI的“Unity手”：AI通过`tools describe`学习接口，通过`tools run`提交结构化JSON，或通过`command`按同一live schema提交业务参数。设计第一标准是陌生AI不读源码、不需要人教、不靠试错也能正确调用；做不到就是接口设计失败。

### 核心原则

1. **职责单一，不聚合**：不得用 action 路由器聚合多种能力。使用 `material.info/create/assign/set` 一类独立 Tool，让 AI 从名称即可路由。
2. **只吸收 Unity 侧独特价值**：AI 能直接读写 `.cs` 等工程文件，不需要 CLI 包装；只有 Editor 内存、资产、场景、物理、渲染等 AI 无法直接完成的操作才进入 CLI。
3. **不包装现有命令行能力**：install-unity、create-project、open/close Editor 等由 AI 直接使用系统命令完成。
4. **上下文消耗是硬指标**：大输出必须支持分页、筛选、深度或 pattern 控制；默认返回摘要，按需展开；`describe` 必须自足；减少不必要轮询。
5. **调用即授权**：公开 Tool 和 CLI 控制面调用本身即授权。`destructiveHint`、`sideEffects` 与 `supportsDryRun` 只描述行为，不能作为额外确认、预定义按钮列表或意图复核的执行门。

### 架构边界

```text
AI
  -> tools describe / tools run / command
CLI (TypeScript)
  -> bridge spec / security-policy / metadata / handler routing
Unity Bridge (C#)
  -> command registration / descriptor / JSON envelope / domain handlers
Unity Editor
```

内部 Bridge command、coordinator 和 registry 可以存在，但不得改变公开 CLI 形态。公开 descriptor 不得泄露内部路由字段。

超时/断连由调用方负责判断重试，系统不做重放拦截；审计为 session 级 append 日志。

### 明确不做

- MCP 协议、server、transport、resource 或 prompt。
- auth、Unity Hub、Editor安装或打开、Cloud、License、proxy配置等外部控制面。
- 桌面 GUI、setup wizard、工具开关或配置持久化。
- `manage_*` 一类聚合 action 路由器。
- 人类导向的传统命令手册。
- 兼容层、遗留语义或双轨实现。

## 顶层 CLI 形态冻结

用户可见顶层命令只允许以下入口。

能力入口：

- `tools`
- `command`

CLI控制面：

- `init`
- `extensions`
- `target`
- `instances`
- `doctor`

硬规则：

- 未经项目所有者明确批准，不得新增用户可见顶层命令。
- `scene`、`asset`、`camera`、`go`、`prefab`、`package`、`build`、`test`、`editor`、`profiler` 等 Unity 能力域不得成为顶层命令。
- 新增或修改Unity能力必须进入唯一Tool catalog；结构化调用使用`tools run <tool>`，schema参数调用使用`command <name>`，二者必须复用同一descriptor、dispatcher、安全、completion和JSON envelope。
- `[CliCommand("name")]`只以`UnityPipeline_name`、group `UnityPipeline`进入`tools`；`command name`可选择该Tool，native完整精确名重名时native优先，attribute仍可用完整前缀名调用。禁止相似名称或语义映射。
- README、docs、Skill、tests、contract和源码只能使用当前CLI形态，不得保留旧顶层命令、迁移语义或兼容说明。

## 能力基本边界

所有动作与观察能力统一为 Tool。

- 任何可能修改 Unity 状态、项目文件、资源、Package、场景对象、组件、PlayMode 或产生 artifact 的能力都必须准确声明副作用与风险。
- 读取型 Tool 必须纯只读，不得触发 import、compile、domain reload、PlayMode 切换或其他副作用。
- 所有用户可见输出必须是稳定、机器可读的 JSON envelope；错误必须提供稳定 code 和可执行的 `nextActions`。
- 详细的 descriptor、Editor/PlayMode、安全、schema 和输出规则以 `unity-cli-capability` Skill 为唯一领域规范。

## 临时文件与机制状态

三条铁律，产品与验证共同遵守：

1. **机制状态重启作废**：CLI 机制使用的临时状态（互斥锁、进行中标记等）只对 Unity 实例的本次启动有效，重启后一律作废，不得影响新启动；实现上此类状态必须携带本次启动标识，不得以进程存活猜测替代。业务效果（资源、场景、包状态）不属于机制状态，持久保留。
2. **临时文件增长有界**：CLI 产生的临时文件，数量与大小不得随使用次数和时间无限增长；必须是常数上限（如一类协调器一个固定文件、用完即删）或滚动/保留窗口（如日志滚动、artifacts 保留最新 100 个）。禁止一请求一文件的积累型记录。
3. **临时文件位置**：所有临时文件必须位于所属根的临时区——用户级为 `%LOCALAPPDATA%\UnityCli\.temp\`（artifacts、logs、requests 等）；项目内以 `Library\UnityCli\` 为临时根（Bridge 日志、transactions、active operation、指纹、扩展锁等），不得在项目根新建临时目录。实例注册表（`instances.json`、`config.json`、`instances\`）不是临时文件，留在用户级根。

## 强制 Skill 路由

项目 Skill 是硬规范，不是可选参考。命中任务后必须在实施前使用 `read` 完整读取对应 `SKILL.md`；系统提示中的 name/description 元数据不能替代正文。一个任务命中多个领域时必须全部加载。

### `unity-cli-plan`

路径：`.pi/skills/unity-cli-plan/SKILL.md`

创建、更新、迁移、恢复或继续任何 `.plan` 计划，以及任务预计跨多个文件、多个步骤或一个上下文窗口时必须读取。验证失败准备转为产品修改、计划范围或公开语义可能变化时也必须读取。

- 计划必须是 `.plan/<stable-short-name>/` 文件夹执行包，不得新建活动单文件计划。
- 创建或激活执行任务时必须同步创建一个上下文窗口大小的 direction checklist；具体 Todo 必须映射到其中一个 outcome。
- 未登记文件、未授权公开契约、能力范围、版本支持、proof 语义或验证范围变化必须硬阻断，由项目所有者决定。
- execution log 只追加工程事件；动态上下文必须 fresh 核验，旧摘要、PID、session、stamp 或历史 PASS 不能替代当前事实。

### `unity-cli-capability`

路径：`.pi/skills/unity-cli-capability/SKILL.md`

新增、修改或删除以下内容时必须读取：

- Tool、Bridge handler 或 registry。
- descriptor、group、summary、description、schema、examples、availability。
- 公开 README、docs、Skill、tests 或 contract 中的能力契约与命令语义。
- Editor/PlayMode、session-affecting、runtime-only 行为。
- destructive、`--dry-run`、security-policy。
- JSON envelope、错误码、`nextActions`。
- 项目自定义 `IUnityCliTool`。

### `unity-cli-extension`

路径：`.pi/skills/unity-cli-extension/SKILL.md`

新增、修改或删除 `extensions/<id>`、Extension manifest、Unity package 依赖、安装投影或扩展贡献能力时必须读取；同时必须读取 `unity-cli-capability`。

### `unity-cli-wait`

路径：`.pi/skills/unity-cli-wait/SKILL.md`

涉及 `ValueTask`、`Promise`、`ctx.Wait`、callback、`EditorApplication.update`、timeout/cancel、跨帧执行或命令层 wait 时必须读取；涉及公开能力时同时读取 `unity-cli-capability`。

### `unity-cli-validation`

路径：`.pi/skills/unity-cli-validation/SKILL.md`

任何产品代码或公开行为修改都必须在设计或实施前读取，并在验证、Dev Unity 调用和完成阶段继续遵守。修改本文件、项目 `unity-cli-*` Skill 或 `unity-cli-gate` agent 时也必须读取。该 Skill 规定契约、自动化测试、真实调用矩阵、Dev 同步和门禁。

未加载命中的 Skill，不得开始实现或宣称完成。无法判断时加载范围更大的相关 Skill，不得用普通 docs 或已有记忆替代。

## 命名与兼容性

项目仍处于开发期，不承担旧版本兼容义务，但当前公开契约必须保持一致。

这里的“不兼容旧版本”和维护洁癖针对本项目内部已经废弃的数据格式、命令、路由、字段、行为、重复实现、过渡代码和死代码；不指 Unity、官方 Package 或第三方工具的版本适配。manifest、descriptor 或产品要求声明支持的工具版本属于当前产品能力，即使适配代码位于本仓库，也不得以清理兼容层为由删除。版本分派本身仍必须集中、边界明确、无重复、无未声明兜底且可测试。确需缩小支持范围时必须由项目所有者明确决定，并同步更新全部公开契约。

- Tool 名、group、字段名和错误码一旦公开即视为契约。
- 命名变更必须同步更新 metadata、docs、Skill、schema、contract 和 tests。
- 只保留当前命令形态与当前语义，不得提交双轨实现、legacy fallback 或兼容 adapter。

## AI 开发方式

- 先理解现有设计，再做边界清晰的修改。
- 不新增需求外的抽象、兼容层或框架，不顺手重构无关模块。
- 普通方法添加简要中文注释；关键方法、复杂逻辑、状态流转和跨层入口必须说明时机、约束、输入输出或副作用。
- 代码标识符使用英文，代码注释使用中文。
- 修改后必须说明验证方式；无法完成任何门禁时必须报告精确 blocker，不能把部分完成写成完成。

### 陌生 AI 黑盒门禁

`unity-cli-gate` 的目的，是验证陌生 AI 能否仅凭公开 CLI 自主发现、理解并一次正确使用能力，不是代替主 Agent 执行自动化测试脚本。

- 主 Agent 负责全量正反矩阵、扰动编排、断言和证据归档；不得把这些步骤、目标工具、参数或预期结果写进子代理提示词。
- 子代理只接收简短真实任务、公开 CLI 启动前缀、必要的项目/instance/夹具信息和安全边界，自主决定 discovery、Tool 和输入；启动前缀只是环境事实，不得包含目标能力或调用参数。schema default与example只代表合法默认行为和调用形状，任务明确业务值优先；禁止把default/example作为连通性、smoke或probe调用。
- 多个实际业务调用必须严格串行，每次等待并验证业务结果后才能提交下一次；禁止在同一assistant turn并行或批量提交，避免绕过first-failure停止边界。
- 第一次非预期调用失败后，正式门禁立即结束；`ok:true` 后的首次业务验证若证明效果未发生，也算前一调用失败。接口、实现或理解问题保持 FAIL，transient Unity/instance 环境问题保持 BLOCKED，不得指导重试、纠参或换工具补成 PASS。
- 为定位接口问题，可以在同一子代理会话中只追问其首次选择依据和误解来源；诊断追问不得执行工具、不得改变原门禁结论。
- 门禁证据由主 Agent 从原始 prompt、完整子代理 transcript、Unity 前后状态和诊断回答中独立归档，不得要求被测模型执行 recorder、case 编号、assertion spec 或 audit 流程。

### 目标优先与范围控制

“最小改动”表示严格控制需求边界，不表示追求最少行数、保留旧实现或接受过渡状态。

- 最终目标决定改动规模；目标架构需要跨文件重构、删除或整体替换时必须完整实施。
- 当前确认的最终语义优先于旧代码、测试、契约和文档；冲突内容必须同步修改或删除。
- 阶段可运行不能成为保留新旧双轨、临时旁路、空壳、兼容 adapter 或阶段性注释的理由。
- 只控制需求外改动；目标范围内必须一次性达到确认的最终形态。
- 客观条件阻塞最终形态时必须停下并报告，不得以“可编译”“测试通过”或“改动更小”掩盖中间态。

### 维护洁癖

- 文档、注释、descriptor 和 examples 只描述当前代码现状；除日志、审计或迁移记录外，不写历史形态、迁移过程和阶段性说明。
- 废弃功能必须删除干净，包括代码、注释、示例、测试、文档、配置、路由、空壳文件和无效 helper。
- 临时桥接、过渡路径、“后续再删”“先保留”“兼容一下”等阶段性代码和说明不得进入最终交付。
- 已确认需求必须完整实现；做不到最终形态时明确报告 blocker，不得伪装为完成。
- 不得有阶段性代码与注释,如 "阶段","兼容","V1","V2" 等

核心判断标准：

> 功能不是给人读 README 后用的，而是给陌生 AI 通过一次自发现就能安全调用的；做不到就是接口设计失败。
