# AGENTS.md

本文件是 `alps-unity-cli` 的常驻 AI 开发硬规范，作用范围为整个仓库。除非项目所有者明确允许，任何 AI Agent 都必须遵守。领域细则通过本文件指定的项目 Skill 按任务强制加载。

## 项目定位

`unity_cli` 是面向 AI 的 Windows-first Unity Editor 自动化 CLI，不是 MCP server。

核心目标不是维护供人记忆的传统命令手册，而是提供可发现、可描述、可审计、可验证的能力目录，让陌生 AI 仅凭公开 CLI 一次发现、一次理解、一次安全调用。

本 CLI 是 AI 的“Unity 手”：AI 通过 `describe` 学习接口，通过 `tools run` 执行动作，通过 `context read` 观察状态。设计第一标准是陌生 AI 不读源码、不需要人教、不靠试错也能正确调用；做不到就是接口设计失败。

### 核心原则

1. **职责单一，不聚合**：不得用 action 路由器聚合多种能力。使用 `material.info/create/assign/set` 一类独立 Tool，让 AI 从名称即可路由。
2. **只吸收 Unity 侧独特价值**：AI 能直接读写 `.cs` 等工程文件，不需要 CLI 包装；只有 Editor 内存、资产、场景、物理、渲染等 AI 无法直接完成的操作才进入 CLI。
3. **不包装现有命令行能力**：install-unity、create-project、open/close Editor 等由 AI 直接使用系统命令完成。
4. **上下文消耗是硬指标**：大输出必须支持分页、筛选、深度或 pattern 控制；默认返回摘要，按需展开；`describe` 必须自足；减少不必要轮询。
5. **安全策略最小化**：只拦真实持久化文件操作和任意代码执行。场景内存与会话状态操作不得因为日常编辑而滥用 `--yes`。

### 架构边界

```text
AI
  -> tools describe / tools run / context read
CLI (TypeScript)
  -> bridge spec / security-policy / metadata / handler routing
Unity Bridge (C#)
  -> command registration / descriptor / JSON envelope / domain handlers
Unity Editor
```

内部 Bridge command、job worker 和 registry 可以存在，但不得改变公开 CLI 形态。公开 descriptor 不得泄露内部路由字段。

### 明确不做

- MCP 协议、server、transport、resource 或 prompt。
- 桌面 GUI、setup wizard、工具开关或配置持久化。
- `manage_*` 一类聚合 action 路由器。
- 人类导向的传统命令手册。
- 兼容层、遗留语义或双轨实现。

## 顶层 CLI 形态冻结

用户可见顶层命令只允许以下入口。

能力入口：

- `tools`
- `context`

CLI 控制面：

- `init`
- `extensions`
- `target`
- `instances`
- `jobs`
- `batch`
- `doctor`

硬规则：

- 未经项目所有者明确批准，不得新增用户可见顶层命令。
- `scene`、`asset`、`camera`、`go`、`prefab`、`package`、`build`、`test`、`editor`、`profiler` 等 Unity 能力域不得成为顶层命令。
- 新增或修改 Unity 能力必须通过 `tools run <tool>` 或 `context read <context>` 暴露。
- README、docs、Skill、tests、contract 和源码只能使用当前 CLI 形态，不得保留旧顶层命令、迁移语义或兼容说明。

## 能力基本边界

Tool 用于动作，Context 用于观察。

- 任何可能修改 Unity 状态、项目文件、资源、Package、场景对象、组件、PlayMode 或产生 artifact 的能力都必须是 Tool。
- Context 必须纯只读，不得触发 import、compile、domain reload、PlayMode 切换或其他副作用；只要存在副作用就必须改为 Tool。
- 所有用户可见输出必须是稳定、机器可读的 JSON envelope；错误必须提供稳定 code 和可执行的 `nextActions`。
- 详细的 descriptor、Editor/PlayMode、安全、schema 和输出规则以 `unity-cli-capability` Skill 为唯一领域规范。

## 强制 Skill 路由

项目 Skill 是硬规范，不是可选参考。命中任务后必须在实施前使用 `read` 完整读取对应 `SKILL.md`；系统提示中的 name/description 元数据不能替代正文。一个任务命中多个领域时必须全部加载。

### `unity-cli-capability`

路径：`.pi/skills/unity-cli-capability/SKILL.md`

新增、修改或删除以下内容时必须读取：

- Tool、Context、Bridge handler 或 registry。
- descriptor、group、summary、description、schema、examples、availability。
- 公开 README、docs、Skill、tests 或 contract 中的能力契约与命令语义。
- Editor/PlayMode、session-affecting、runtime-only 行为。
- destructive、`--yes`、`--dry-run`、security-policy。
- JSON envelope、错误码、`nextActions`。
- 项目自定义 `IUnityCliTool` / `IUnityCliContext`。

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

- Tool 名、Context 名、group、字段名和错误码一旦公开即视为契约。
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
- 子代理只接收简短真实任务、公开 CLI 启动前缀、必要的项目/instance/夹具信息和安全边界，自主决定 discovery、Tool/Context 和输入；启动前缀只是环境事实，不得包含目标能力或调用参数。
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

核心判断标准：

> 功能不是给人读 README 后用的，而是给陌生 AI 通过一次自发现就能安全调用的；做不到就是接口设计失败。
