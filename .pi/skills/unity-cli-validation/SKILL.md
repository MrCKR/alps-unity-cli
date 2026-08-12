---
name: unity-cli-validation
description: unity_cli 的验证规划、契约校验、自动化测试、Dev Unity 实测矩阵、安装同步和干净子代理门禁硬规范。任何 Tool、Extension、Bridge、descriptor、安全、wait、公开行为或项目治理规范修改在设计、实施、验证和宣称完成前必须完整读取并执行。
compatibility: 适用于 alps-unity-cli；Unity 真实验证使用 ./Dev，子代理使用项目级 unity-cli-gate。
---

# Unity CLI Validation

本 Skill 规定主 Agent 在设计前必须明确的验证计划和最终完成门槛。它不能传给 `unity-cli-gate`，也不能作为子代理的背景信息；门禁子代理只能从公开 CLI 自发现。

## 计划方向门禁

计划任务必须同时遵守 `unity-cli-plan`。自动化、Live 或 gate 失败准备转为代码修改前，先在当前 direction 与 execution log 中完成分类：

- `PRODUCT_DEFECT`：产品违反已批准 spec，可以修产品。
- `HARNESS_DEFECT`：测试、fixture 或断言错误，只修验证 owner。
- `ENVIRONMENT_BLOCKED`：记录解锁条件，不修改产品。
- `SPEC_GAP`：source of truth 没有定义，等待项目所有者决定。
- `TEST_OVERREACH`：测试强于 spec，禁止反向强化产品。

失败无法映射到当前 direction outcome，或修复会改变公开契约、能力范围、版本支持、proof 语义、文件 owner 或验证范围时必须硬阻断。等价类可以压缩共享验证，但计划已经明确要求逐项覆盖的内容，未经项目所有者修改计划不得自行缩窄。

## 契约与自动化测试

新增或修改能力时必须检查：

- Tool metadata 与 Bridge registry 一致。
- `contracts/command-contract.json` 与源码一致。
- C# handler 注册与 TypeScript metadata 一致。
- JSON Schema 与实际输入输出一致。
- README、docs、Skill 使用当前 CLI 形态并与实现一致。
- 超时或终态前断连使用 `E_EXECUTION_TIMEOUT`，由调用方判断是否提交新的调用；不得恢复请求级日志或防重放契约。跨 reload 的同次调用续接与 session 级 append 审计应保持可验证。
- 测试覆盖成功路径、错误路径、Editor/PlayMode 差异和 extension availability。
- discovery、describe 或 offline visibility 语义变化时，tests、docs、skills 和 descriptor governance 必须整体切换到新语义。

自动化测试不能替代真实 Unity 调用：

- 新增或修改Tool / Extension时，必须建立真实调用矩阵；单元测试、契约测试和源码断言只能补充。
- 矩阵必须覆盖每类公开用法：默认调用、主要可选参数、筛选/分页/精读/续读、边界值、错误路径、权限、可选 `--dry-run`、Editor/PlayMode 差异。
- 组合较多时可以按等价类压缩，但不能跳过公开行为类别，且必须说明等价依据。
- 正向用例必须验证关键业务字段，不得只检查 `ok: true`。
- 反向用例必须验证稳定的 `error.code`、`message`、`transient`、`retryAfterMs`、`nextActions`。
- 每次真实调用记录完整命令、输入 JSON、输出 envelope、前置状态、测试夹具、目标 Unity instance/endpoint 和持久化副作用。
- 不得为了让测试通过而降低 descriptor 质量或放宽契约。

## Dev 沙盒

`./Dev` 是真实 Unity 验证的专用沙盒：

- 可以安装 Bridge 和 Extension、运行 Unity、创建/修改/删除测试资源；Dev dirty scene 是沙盒状态，不应当作用户业务资产要求人工决策。
- 可以验证破坏性操作、PlayMode、package 依赖和 artifact 输出。
- 临时实验结果不得作为产品代码提交，除非任务明确要求。
- 需要验证 Unity 行为时优先使用 `./Dev`，不得只依赖单元测试或静态推断。

真实调用前必须保证安装与源码一致：

- 确认 `./Dev/UnityProject` 已安装当前源码对应的 Bridge、Extension 和测试夹具；旧安装结果不能作为当前实现证据。
- 修改 `unitypkg/`、Extension `unitypkg/`、Bridge C# handler、Unity 安装投影或 Dev 夹具后，先通过当前 CLI 的 init/install 流程同步。
- 修改影响 init/install 的 TypeScript CLI 后，先完成必要 build，再同步到 Dev 项目。
- 同步后等待 Unity import、compile、domain reload 完成，并通过 `editor.state` 或等价只读检查确认 Editor 非 busy。

Dev 调用矩阵：

- 凡影响 Unity 侧行为，必须真实调用所有受影响能力，不得只抽样一个 happy path。
- 覆盖正向、反向、边界、Editor/PlayMode 差异和持久化安全行为。
- 破坏性持久化能力优先以 `--dry-run` 验证 planned effects；真实写入或删除只能发生在 Dev 专用夹具路径，并记录清理或保留原因。
- 无法执行的矩阵项必须记录精确 blocker、不可替代原因、已覆盖的邻近证据和解锁条件；不得把未调用项写成通过。

## 干净子代理门禁

每个新增或修改的功能完成后，必须使用项目级 `unity-cli-gate` 子代理验证陌生 AI 可用性。该门禁不替代本 Skill 前文规定的自动化测试和 Dev 真实调用矩阵。

### 固定配置

- 主 Agent 必须指定 `subagent_type: "unity-cli-gate"`；不得以其他代理替代。
- `.pi/agents/unity-cli-gate.md` 是本节规范的可执行投影，必须保持 `prompt_mode: replace`、`inherit_context: false`、`skills: false`，并且只暴露 `windows-pwsh/pwsh`。
- `windows-pwsh` 不可用时判定为环境阻塞，不得回退到其他 shell 或代理。
- 子代理必须实际运行在 `LMD/deepseek-v4-pro`；模型不可用、解析失败或 fallback 时门禁无效。
- 正式门禁必须使用全新会话，禁止继承父上下文。产品修正后的重跑也必须创建新会话。
- 修改本规范或 agent 定义时必须同步更新，并执行隔离自检。

### 职责分离

主 Agent 负责：

- 全量正向、反向、边界、分页、Editor/PlayMode、安全和扰动矩阵。
- 具体断言、环境恢复、完整 transcript 与证据归档。
- 事后判断子代理的发现路径、首次选择和首次调用是否合理。

门禁子代理只负责：

- 像第一次接触项目的 AI 一样完成一个简短、真实的用户任务。
- 仅凭公开help、discovery、describe、examples和错误输出自主选择Tool、调用语法、参数与调用顺序。
- schema default与example只表示合法默认行为和调用形状；任务明确业务值优先，禁止把default/example作为连通性、smoke或probe调用。
- 在实际调用前说明选择依据来自哪些公开信息，并把任务literal映射到schema业务字段。
- 多个实际业务调用必须严格串行，每次等待并验证业务结果后才能提交下一次；禁止在同一assistant turn并行或批量提交。
- 成功后检查关键业务结果，并清理自己产生的副作用。

不得要求门禁子代理执行全量测试矩阵、固定 case 顺序、recorder、case 编号、assertion spec、audit 或主 Agent 的验收脚本。这些内容会污染被测模型，使门禁退化为指令遵循测试。

### 任务设计

- 默认只给一个可独立完成的自然语言业务任务，不提供期望工具名。
- 只有当验证目标就是某个已公开名称本身时，才可给出能力名称；仍不得给命令、参数或输入。
- capability gate 使用单领域真实任务，观察能否发现正确能力。
- task gate 使用跨能力真实目标，观察能否自主组合。
- perturbation gate 把并发、reload、Prefab Stage 或 UI cache 写成任务环境或约束，不拆成测试脚本步骤。
- blocker gate 在已由主 Agent 建立 modal 环境中给出自然任务。陌生 AI 从原错误的 blocker 自主读取 `editor.dialogs`，并可用 `editor.dialog.click` 点击任意当前可见 modal 的 enabled button；第一次错误规则仍适用，但不得因 modal 陌生而禁止点击。主 Agent 仍负责真实状态和证据。
- 完整行为类别覆盖由主 Agent 的矩阵承担，不要求一个弱模型重复全仓验证。

主 Agent 只能提供：

- 简短任务目标。
- 公开 CLI 启动前缀；本仓库固定为 `node dist/cli.js`，它只是进程入口，不是能力提示。
- 必要的项目、instance、夹具事实。
- 允许和禁止的副作用、安全边界。

不得提供具体发现链、目标Tool、启动前缀之外的最终命令、输入JSON、预期输出、源码路径、实现细节、参数解释或失败后的纠正方案。子代理不得为定位入口枚举或读取工作区；获得启动前缀后必须直接从公开 CLI help 开始。需要其他信息才能完成任务，说明公开接口不自足。

### 首次失败与原因追问

- 子代理任何公开 CLI 调用（包括 help、discovery、describe、业务调用和 cleanup）第一次出现非预期失败后必须立即停止；即使 envelope 为 `ok:true`，紧随其后的首次业务验证若证明预期效果未发生，也必须把前一调用判为失败。不得重试、纠参、换工具或根据主 Agent 提示恢复；不得用预先并行提交的后续业务调用绕过停止边界。
- transient Unity/instance 环境错误判定为 BLOCKED；选择、接口理解或调用错误判定为 FAIL。原正式门禁结论不得用后续解释或成功调用改写。
- descriptor 调用失败也视为已经使用过一次，不得再次读取同一 descriptor。
- 主 Agent 可以恢复同一个 agent ID，且只发送一次诊断问题，要求说明首次选择的公开依据、误解来源和缺失信息。
- 诊断阶段禁止任何工具调用。若子代理继续调用工具，该诊断无效，但不改变原门禁结论。
- 诊断问题不得包含正确工具、参数、命令或恢复提示。
- 修正产品后必须启动全新会话重跑，禁止 resume 原门禁完成正式验证。

推荐诊断问题：

> 停止调用和重试。说明你第一次选择该工具和参数分别依据了哪些公开 summary、description、schema、example 或 nextActions；哪一处信息使你形成当前理解；还缺少什么公开信息才能第一次做对？

### 判定标准

PASS 必须同时满足：

- 使用合理的公开发现入口定位到适合任务的能力。
- descriptor 只学习一次即可形成正确理解。
- 每个任务操作的第一次实际调用正确，无非预期纠参或替代调用。
- 验证了关键业务结果，不只检查 `ok`。
- 正确理解安全、PlayMode 和副作用，并恢复任务前状态。

FAIL 后由主 Agent 结合原始 transcript 和诊断回答分类：

- 接口缺陷：名称、group、summary、description、schema、example 或错误信息导致错误理解。
- 模型能力问题：公开信息充分，但模型忽略、臆造或错误推理。
- 环境阻塞：instance、Unity、Package、权限或外部状态不可用。

错误恢复能力如需验证，应作为新的独立自然任务使用 fresh agent；不得在首次调用门禁中通过 recovery 把 FAIL 改成 PASS。

### 门禁执行与证据

- 为保留 agent ID 和完整过程，主 Agent 应后台启动门禁，并通过 `get_subagent_result(verbose: true)` 获取完整 transcript。
- 被测子代理不得承担证据格式化职责。主 Agent 从原始 prompt、tool calls、stdout/stderr、最终回答和 Unity 前后状态独立生成 evidence。
- 证据必须记录实际模型、agent ID、原始提示词、发现链、读取的 descriptor、首次选择依据、第一次实际调用、关键业务验证、清理结果和禁止信息检查。
- 失败时额外记录诊断问题与原样回答；诊断回答只能用于归因。
- capability、task、perturbation、blocker 四类门禁分别保存 fresh 会话证据；旧会话、自评摘要或恢复后成功不得替代。

## 完成条件

只有同时满足以下条件才能宣称完成：

- 代码实现完成。
- 相关自动化测试通过。
- 契约校验通过。
- `./Dev` 真实验证通过，除非能力确实不依赖 Unity 实例。
- 干净子代理门禁通过。
- 涉及临时文件或机制状态的改动，必须实测验证 AGENTS.md《临时文件与机制状态》三条铁律：重启后机制状态不影响新启动；临时文件数量与大小有界（现场盘点）；位置在所属根的临时区（用户级 `.temp\`、项目内 `Library\UnityCli\`）。

任一必测项未执行、证据不足、存在 unresolved error 或环境 blocker 时，功能保持未完成。
