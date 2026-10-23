# AGENTS.md

本文件是 `alps-unity-cli` 的常驻 AI 开发硬规范，作用范围为整个仓库。除非项目所有者明确允许，任何 AI Agent 都必须遵守。领域细则通过本文件指定的项目 Skill 按任务强制加载。

## 项目定位

`unitycli` 是面向 AI 的 Windows-first Unity Editor 自动化 CLI，不是 MCP server。

核心目标不是维护供人记忆的传统命令手册，而是提供可发现、可描述、可审计、可验证的能力目录，让陌生 AI 仅凭公开 CLI 一次发现、一次理解、一次安全调用。

本CLI是AI的“Unity手”：AI通过`tools describe`学习接口，通过`tools run`提交结构化JSON，或通过`command`按同一live schema提交业务参数。设计第一标准是陌生AI不读源码、不需要人教、不靠试错也能正确调用；做不到就是接口设计失败。

### 核心原则

1. **职责单一，不聚合**：不得用 action 路由器聚合多种能力。使用 `material.info/create/assign/set` 一类独立 Tool，让 AI 从名称即可路由。
2. **只吸收 Unity 侧独特价值**：AI 能直接读写 `.cs` 等工程文件，不需要 CLI 包装；只有 Editor 内存、资产、场景、物理、渲染等 AI 无法直接完成的操作才进入 CLI。
3. **不包装现有命令行能力**：install-unity、create-project、普通 open/close Editor 等由 AI 直接使用系统命令完成。唯一例外是 `init package --close-editor` 在用户已针对准确目标项目明确同意后，为释放 Bridge 安装占用而正常关闭该项目；不得把此例外扩展为通用 Editor 进程控制。
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
- `scene`、`asset`、`camera`、`game-object`、`prefab`、`package`、`build`、`test`、`editor`、`profiler` 等 Unity 能力域不得成为顶层命令。
- 新增或修改Unity能力必须进入唯一Tool catalog；结构化调用使用`tools run <tool>`，schema参数调用使用`command <name>`，二者必须复用同一descriptor、dispatcher、安全、completion和JSON envelope。
- `[CliCommand("name")]`只以`UnityPipeline_name`、group `UnityPipeline`进入`tools`；`command name`可选择该Tool，native完整精确名重名时native优先，attribute仍可用完整前缀名调用。禁止相似名称或语义映射。
- README、docs、Skill、tests、contract和源码只能使用当前CLI形态，不得保留旧顶层命令、迁移语义或兼容说明。
- `skills/unitycli/` 的职责是引导已经具备本 Skill 的 AI 使用已安装的公开 `unitycli` CLI：负责直接文件处理与 Live Unity 调用之间的路由、Tool catalog 的按需发现、少量跨 Tool 通用调用规则、必要 reference 的条件分流，以及开发期 `FeedbackMode` 反馈入口。它不是安装/配置协议、项目开发规范、发布或测试门禁、内部 Bridge 实现手册、完整 Tool 清单或逐能力操作手册。
- Skill 主入口应只描述稳定的运行时路由和通用不变量；安装生命周期使用 CLI 自身的 `init help` 协议，具体协议和实现细节不得复制到 `SKILL.md`。新增 Unity 机制只有在确实改变 AI 的公开运行时路由或跨 Tool 调用规则时，才评估是否进入 Skill；单个 Tool 的参数、能力细节和内部实现应留在 live descriptor、CLI help 或按需 reference。
- `skills/unitycli/` 是受保护的用户授权内容。Agent可以读取、审计和验证，但除非用户在当前请求中对明确的单次修改作出明确授权，不得修改、重写、删除、生成或自动同步其中任何内容，包括`SKILL.md`、`references/`和`scripts/`。同一保护范围覆盖`skill-factory/unitycli/`下的`manifest.json`、`agents/`、`evals/`、`examples/`、`reports/`和`security/`。普通功能开发、测试失败、版本发布或“保持同步”等泛化授权不构成该授权；授权仅覆盖用户指定的文件和本次变更，不得扩展到其他Skill文件或后续任务。

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

## Dev Unity 与正式环境隔离

1. **正式根专用**：默认 `%LOCALAPPDATA%\UnityCli\` 只允许已发布 CLI、与其匹配的已发布 Unity Package 和用户真实项目使用。`./Dev/UnityProject`、源码构建 CLI、未发布 Unity Package、Live matrix、gate 或其他开发验证不得向该根写入实例、日志、请求或 artifact。
2. **固定 Dev 根**：所有 `./Dev` 真实 Unity 验证统一使用 `%LOCALAPPDATA%\UnityCli-Dev\`；Unity Editor 进程与对应源码 CLI 必须在启动时逐进程注入同一个 `UNITYCLI_DATA_DIR`。禁止按请求、日期或版本创建无限多个 Dev 根；Dev 根内临时文件仍放在其 `.temp\` 下并遵守数量与大小上限。
3. **禁止全局环境污染**：不得用 `setx`、用户级或系统级环境变量切换 `UNITYCLI_DATA_DIR`。变量只能注入本次 Dev Unity 进程和与其配套的 CLI 进程；正式版 CLI 必须保持默认根。通过已有 Unity Hub 普通启动 Dev 项目不能证明继承了隔离变量，禁止作为验证启动方式；启动后必须核对 Dev 记录只出现在 Dev 根，正式根文件集合未变化。
4. **未发布格式不得进入正式根**：实例、health 或其他共享文件格式发生未发布变化时，即使 `package.json` 版本号尚未变化，也必须视为与正式版不同的开发实现并保持隔离。只有完成版本同步、发布门禁和正式发布后，匹配的 CLI 与 Unity Package 才能使用正式根。
5. **故障清理精确到单文件**：清理错误实例记录前必须先确认写入它的 Unity 进程已退出，再列出文件绝对路径、所属项目、格式版本与备份目标；只允许移动或删除已确认的单个文件，禁止删除整个 `instances\`、整个数据根或其他项目记录。备份使用固定有界位置，不得形成逐次累积。
6. **正式 Unity 进程不可代管**：自动化验证只可管理 `./Dev/UnityProject`。未经用户针对明确项目和明确动作的本轮授权，不得启动、关闭、重启或终止用户真实项目的 Unity Editor；“继续处理”“后面的你操作”等泛化授权不得解释为进程控制授权。安装发现明确报告准确项目正在运行后，用户对“正常关闭该项目并安装”的肯定回答可授权一次 `init package --close-editor`；该调用只能正常关闭同一 canonical 项目的交互式 Editor，遇到未保存或 modal 阻断必须停止，禁止强制终止、处理其他项目或把授权扩展到后续调用。
7. **隔离验证前后核验**：Dev 启动前记录正式根文件清单与正式 Unity 进程；Dev 运行后确认其记录只写入 Dev 根；结束后确认正式根、正式进程和正式版 `instances list` 未因 Dev 验证发生变化。任何越界写入立即停止验证，先恢复隔离，不得继续执行 Live 调用。

## 强制 Skill 路由

项目 Skill 是硬规范，不是可选参考。命中任务后必须在实施前使用 `read` 完整读取对应 `SKILL.md`；系统提示中的 name/description 元数据不能替代正文。一个任务命中多个领域时必须全部加载。

`.pi/skills/unity-cli-*` 只约束本仓库内的开发、验证与发布，不是安装产物。`skills/unitycli/SKILL.md` 与由其安装投影决定的 references/scripts 才是 `init` 安装后提供给陌生 AI 的公开 `unitycli` Skill；`skill-factory/unitycli/` 保存其治理资产，不进入安装投影。公开调用、等待、错误恢复或 host 预算变化时，必须在产品实现完成后同步更新该 Skill，禁止提前描述尚未实现的行为。

### `unity-cli-issue-lifecycle`

路径：`.pi/skills/unity-cli-issue-lifecycle/SKILL.md`

准备修复 UnityCLI 产品缺陷并维护问题登记、`in-progress`/`completed` 状态、根因、验证与归档闭环时必须读取。默认从 canonical 产品源码实施最终语义，再安装到授权测试环境验证；用户明确要求对指定真实或 Dev 项目做紧急已安装 Package 热修时也必须读取。

- 第一次修改 canonical 产品源码前必须先登记到 `.issues/in-progress/`；紧急例外则必须在第一次修改指定项目 Package 前登记。Skill 启用前已经修改的现场必须立即补登记并如实标注。
- 默认先完整修改 `unitypkg`、TypeScript 源码、tests、contracts、docs 及获明确授权的公开 Skill 投影，再完成受影响 build、自动化与契约验证，然后安装或同步到授权测试环境；不得为目标项目保留特例或临时兼容路径。
- 真实用户项目 Package 不是默认开发面，必须取得精确授权。紧急目标优先实验验证后，仍须在 canonical 源码重新实现最终语义、重新构建、从 canonical 源码安装并重跑最终 Package 验证。
- 只有 canonical 产品源码已验证、涉及 Package 时安装测试副本也已验证、全部必需自动化/契约/Live/门禁通过且无 blocker，才能移动到 `.issues/completed/`。
- 状态目录只使用英文；不得复制问题文件形成双份状态，不得把部分 PASS 写成完成。问题记录流程独立于普通任务跟踪。

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

### `unity-cli-validation`

路径：`.pi/skills/unity-cli-validation/SKILL.md`

任何产品代码或公开行为修改都必须在设计或实施前读取，并在验证、Dev Unity 调用和完成阶段继续遵守。修改本文件、项目 `unity-cli-*` Skill 或 `unity-cli-gate` agent 时也必须读取。该 Skill 规定契约、自动化测试、真实调用矩阵、Dev 同步和门禁。

未加载命中的 Skill，不得开始实现或宣称完成。无法判断时加载范围更大的相关 Skill，不得用普通 docs 或已有记忆替代。

### `unity-cli-release`

路径：`.pi/skills/unity-cli-release/SKILL.md`

升版本号、打 tag、npm publish、push 或本地安装验证发布产物时必须读取；判断一次改动应升大版本（b）还是小版本（c）、判断是否达到正式发布标准（a）时也必须读取。该 Skill 规定版本号语义 `a.b.c`（a=正式版本号，b=大版本，c=小版本）、版本同步点、发布前置门禁与 commit/tag/publish/push 顺序。

## 命名与兼容性

项目仍处于开发期，不承担旧版本兼容义务，但当前公开契约必须保持一致。

这里的“不兼容旧版本”和维护洁癖针对本项目内部已经废弃的数据格式、命令、路由、字段、行为、重复实现、过渡代码和死代码；不指 Unity、官方 Package 或第三方工具的版本适配。manifest、descriptor 或产品要求声明支持的工具版本属于当前产品能力，即使适配代码位于本仓库，也不得以清理兼容层为由删除。版本分派本身仍必须集中、边界明确、无重复、无未声明兜底且可测试。确需缩小支持范围时必须由项目所有者明确决定，并同步更新全部公开契约。

- Tool 名、group、字段名和错误码一旦公开即视为契约。
- native Tool 名固定两段：`<group>.<tool>`，只允许一个点号。点前必须等于该 Tool 的 group；点后的 tool 段内部细分只用 `-`（`editor.play-mode-start`、`asset.import-settings-set`、`debug.code-flow-run`）。公开查找只有 group 与 tool 两层，不得用点号暗示不存在的子 group。官方 `UnityPipeline_<name>` 属性 Tool 保留外部作者原名，不受本规则约束。
- 命名变更必须同步更新 metadata、docs、Skill、schema、contract 和 tests。
- 只保留当前命令形态与当前语义，不得提交双轨实现、legacy fallback 或兼容 adapter。

## AI 开发方式

- Agent 或开发脚本执行或编写主机文件系统递归删除时，必须解析目标与允许根的真实路径，并确认目标严格位于任务专用临时、缓存或 worktree 根目录之下；拒绝 HOME、USERPROFILE、盘符根、允许根本身、链接逃逸和通配符。非临时目标必须先列出精确目标、完成 dry-run，并通过 `ask_user` 获得本轮明确批准；本规则不改变公开 Unity Tool 的“调用即授权”语义。
- 先理解现有设计，再做边界清晰的修改。
- 不新增需求外的抽象、兼容层或框架，不顺手重构无关模块。
- 普通方法添加简要中文注释；关键方法、复杂逻辑、状态流转和跨层入口必须说明时机、约束、输入输出或副作用。
- 代码标识符使用英文，代码注释使用中文。
- 修改后必须说明验证方式；无法完成任何门禁时必须报告精确 blocker，不能把部分完成写成完成。

### 实现与验证时序

- 实现阶段必须先一次性完成本次增删改涉及的产品代码、测试代码、契约、文档和公开 Skill；实现未收敛前不得运行正式自动化测试、真实 Unity 矩阵或 `unity-cli-gate`，不得把门禁当成边写边试的调试器。
- 实现完成后先冻结本次增删改清单与直接影响面，再进入独立验证阶段。测试与门禁只覆盖本次增删改及其直接受影响的契约、调用路径和行为类别，不默认运行无关的全仓、全 Tool 或全门禁矩阵。
- “完整验证”表示受影响范围内的正向、反向、边界、状态差异和恢复语义完整，不表示全项目无差别全量。共享基础设施改动可以产生较宽影响面，但必须根据真实调用链说明范围，不能因方便任意扩大或缩小。
- 验证阶段先完成受影响自动化与契约检查，再完成受影响真实 Unity 矩阵，最后执行适用的陌生 AI 门禁。任何一步失败都立即结束当前验证阶段，完成失败分类并返回实现阶段；修复全部收敛后，重新执行被该修复失效的受影响验证，不得把修复前的部分 PASS 拼成最终通过。

### 陌生 AI 黑盒门禁

`unity-cli-gate` 的目的，是验证陌生 AI 能否仅凭公开 CLI 自主发现、理解并一次正确使用能力，不是代替主 Agent 执行自动化测试脚本。

- 主 Agent 负责本次增删改影响面的完整正反矩阵、扰动编排、断言和证据归档；不得把这些步骤、目标工具、参数或预期结果写进子代理提示词。
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
