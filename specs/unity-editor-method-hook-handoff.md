# UnityCLI 内置方法 Hook：开工交接

本文件是交给下一个 AI 的交接词。你有独立会话，没有先前上下文；以下信息自足。

## 1. 任务

在 `D:\workspace\alps-unity-cli` 实现 **内置动态方法 hook（Harmony）**：让 AI 能对当前 Editor 域中的游戏方法安装拦截，观察/修改行为、执行回调、跨 Play 与 Domain Reload 恢复、多实例隔离，并复用现有 CLI 形态（唯一 Tool catalog、统一 dispatcher、准入、completion、JSON envelope）。

设计已经冻结在计划的 source of truth 中，**不要重新设计已经决定的部分**；你可以质疑并改进细节，但改变公开契约前必须先说明冲突点。

## 2. 开工前必须完整读取（顺序即优先级）

1. `AGENTS.md` — 仓库硬规范。
2. `.pi/skills/unity-cli-capability/SKILL.md` — Tool/descriptor/schema/安全/输出规范。
3. `.pi/skills/unity-cli-wait/SKILL.md` — 异步与中央 scheduler 规范。
4. `.pi/skills/unity-cli-validation/SKILL.md` — 验证范围、时序与门禁规范。
5. `specs/unity-editor-method-hook-plan.md` — **实施计划，公开契约 source of truth**。
6. `specs/unity-editor-method-hook-verification-findings.md` — **已实测结论与原始证据**，含必须遵守的实现约束。
7. `CONTEXT.md` — 领域术语（Hook Definition / Hook Attachment / Hook Invocation / Hook Event 等的既定含义）。
8. `specs/unity-editor-dynamic-hook-research.md`、`specs/unity-editor-live-field-observation-research.md` — 背景研究。

涉及 Extension、release 或 gate 时再按 `AGENTS.md` 的技能路由加载对应 Skill。

## 3. 仓库当前状态

已完成的实现切片：**Harmony 依赖闭包**。

- 新增 `unitypkg/Editor/Plugins/0Harmony.dll`（2461696 字节，sha256 `7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c`，标识 `0Harmony, Version=2.4.2.0, PublicKeyToken=null`，来源 NuGet `Lib.Harmony` 2.4.2 `lib/net472`）。
- 新增 `unitypkg/Editor/Plugins/0Harmony.dll.meta`：PluginImporter，Editor-only，`isExplicitlyReferenced: 1`（禁止 auto-reference）。
- 新增 `unitypkg/Third Party Notices.md`：MIT 原文、版本、来源、哈希。
- 修改 `contracts/package-assembly-closure.json`：新增 `bundledAssemblies` 条目；`conflictPolicy.protectedAssemblyFileNames` 现为 `["0Harmony.dll","Newtonsoft.Json.dll"]`。
- 修改 `scripts/audit-package-assembly-closure.mjs`：受保护文件名改为由**随包 DLL ∪ Editor 提供 DLL** 推导；新增随包 DLL 来源字段与 sha256 校验。
- 重新生成 `contracts/package-files.json`（408 项）。

当前 `npm run architecture:validate`、closure 审计、package-files 审计**全部通过**。除上述三处外无其他已跟踪文件改动。

**尚未实现**：后端程序集、hook host、定义/revision 状态机、store、事件缓冲与 cursor、回调上下文、共享动态编译层、八个 Tool、completion 契约与生成投影、测试、文档。因此该能力**目前不可调用，也尚未在 Dev 验证**。

已知待补缺口（我引入，必须在交付前关闭）：`src/package-manager.ts` 的 `inspectPackageAssemblyConflicts` 只扫 `Packages` 与 `Library/PackageCache`，**不扫 `Assets`**。计划要求覆盖 Assets。包自带的 DLL 已被正确排除（扫描时跳过 bridge 包自身），但用户项目在 `Assets` 下自带 `0Harmony.dll` 不会被拦下。

## 4. 已实测、必须遵守的实现约束

以下均已真实运行验证，除非有反证否则按既定事实实现：

1. **补丁生效条件是调用点编译时机。** 补丁安装之后才首次编译的调用点会被拦截（含体量极小、可被 Mono 内联的方法）；补丁安装之前已编译的调用点**静默无效、无错误返回**。因此 `Patch` 成功、替换方法存在、`GetPatchInfo` 可读回都不等于业务命中；"无命中记录"不能反推"方法未被调用"。
2. **重挂必须发生在新域最早时机**（等价 `[InitializeOnLoadMethod]`）。放在 `EditorApplication.update` 实测晚约 300 ms，会漏掉 `Awake`/`Start` 与首批帧；改到静态初始化后默认设置下 `awake`/`start`/全部 `Update` 均被覆盖。
3. **默认设置下进入 Play 会重载域**，Edit 模式安装的补丁不存活，旧域补丁确认消失；必须按新域重挂。
4. **域重载清空静态字段且不重跑 `Awake`。** 不要把重挂状态放在跨域静态字段；热路径要对已清空引用做防御。
5. **产品后端不得编译期引用用户游戏程序集。** asmdef 程序集不能引用预定义程序集 `Assembly-CSharp`；目标按程序集名与签名运行时解析。
6. 第三方 Harmony owner 不会被本 CLI 的 `UnpatchAll(ownId)` 移除；跨 Play 与重载边界双重应用次数为 0。
7. 单个 `0Harmony.dll` 零传递依赖；本包**不提供** netstandard2.0 实体构建，Unity 侧必须用 `net472`。

## 5. 建议实施顺序

按计划的"工作项 C"从依赖闭包之后继续：

1. **共享动态编译层**：从 `unitypkg/Editor/Infrastructure/CodeExecSupport.cs` 抽取"引用发现、固定临时文件、Unity 自带 mono/csc 子进程、诊断、assembly bytes"为共享 owner；`debug.code.execute` 的 wrapper、Run 入口、stdout、completion probe 语义**不得改变**。同步更新 closure 审计从新 owner 校验编译器不变式。
2. **后端程序集** `unitypkg/Hooks/UnityCli.Hooks.Editor.asmdef`：Editor-only、`overrideReferences: true`、`precompiledReferences: ["0Harmony.dll"]`、单向引用 `UnityCli.Editor`。实现 exact target 适配、owner 化 patch 安装与撤销、Prefix/Postfix/内部 Finalizer、类型化参数与结果桥接。核心程序集定义窄接口，按固定程序集与类型身份创建后端，不扫描项目找 provider。
3. **host / store / 事件缓冲**：定义与 revision 状态机、跨域快照（固定文件、原子发布、启动标识作废）、有界事件环与 cursor、回调上下文 API、资源上限。
4. **八个 Tool**（独立文件、closed DTO）：`debug.hook.install/replace/enable/disable/remove/list/info/events`，group 固定 `method-hook`。
5. **completion 与生成投影**：`contracts/tool-completion-contract.json`、`src/tools/tools/group-summaries.ts` 的 `GROUP_SUMMARIES` 新增 exact `method-hook` 摘要、重新生成 registry/schema/command contract/package inventory。
6. **测试与文档**，然后才进入验证。

## 6. 硬约束（违反即返工）

- 不新增顶层 CLI 命令；能力只经 `tools run <tool>` 与 `command <name>`。
- 不做 Extension、不做第二套 descriptor/schema/dispatcher/安全/completion/envelope。
- Tool 作者不得自行注册 `EditorApplication.update`；只经框架 `ctx.Wait` 与中央 scheduler。
- 临时文件只在 `%LOCALAPPDATA%\UnityCli\.temp\`（用户级）或 `<project>\Library\UnityCli\`（项目内）；数量与大小有界，机制状态携带本次启动标识、重启作废。
- 不提交阶段性代码、stub、双轨、兼容层或"后续再删"的注释。
- `skills/unitycli/` 是受保护内容：未经用户针对明确文件的单次授权不得修改。
- **不得启动、关闭、重启或终止用户真实项目的 Unity Editor。** 当前运行中的用户真实项目（Play 模式）只可读取基线，不得操作。
- 自动化验证只可代管 `Dev/UnityProject`；需要使用第二个项目做多实例验证时，先取得针对准确项目与进程动作的明确授权。
- 主机递归删除前必须解析真实路径、确认位于任务专用临时/缓存根之下、列出精确目标并 dry-run；非临时目标需本轮明确批准。

## 7. Dev 环境事实

- Unity：`C:\Program Files\Unity\Unity-2022.3.61f1\Editor\Unity.exe`（非 Hub 目录布局）。
- Dev 项目：`D:\workspace\alps-unity-cli\Dev\UnityProject`（Unity 2022.3.61f1）。
- **Dev 项目内安装的 UnityCLI 包是 0.5.0，仓库是 0.5.3，已过期。** 用作证据前必须先用当前 CLI 的 `init package` 重新同步。
- 隔离根：所有 Dev Editor 与配套 CLI 进程必须**逐进程**注入 `UNITYCLI_DATA_DIR=%LOCALAPPDATA%\UnityCli-Dev`；禁止 `setx` 或用户级环境变量。
- 批处理启动约定：`-batchmode -nographics -projectPath <dev> -logFile <path>`。
- **陷阱**：`-executeMethod` 配异步状态机时**不能加 `-quit`**，否则 Unity 在方法返回后立刻退出，状态机不会运行；应由状态机自己调用 `EditorApplication.Exit`，并加进程内看门狗。
- Dev 夹具已保留，可直接复用：
  - `Dev/UnityProject/Assets/HookProbe/`：`ProbeTarget.cs`（public/private/static/ref/inlinable/guarded 目标）、`ProbeStage2Runtime.cs`（Play 驱动器 + 信号记录）、`Plugins/0Harmony.dll`（Unity 受管插件形态）。
  - `Dev/UnityProject/Assets/Editor/HookProbe/`：`ProbeRunner.cs`（阶段 1 机制）、`ProbeStage2.cs`（阶段 2 生命周期状态机）、`PluginTest/`（asmdef + `PluginProbe.cs`，编译期引用与开销）。
  - 夹具通过 `-probeStage2` 自定义参数与 `%UNITYCLI_DATA_DIR%\.temp\hook-probe\` 下的状态/信号/结果文件跨域延续。

## 8. 验证纪律

- 实现阶段先一次性完成产品代码、测试、契约、文档与（获授权的）公开 Skill；**实现未收敛前不得运行正式自动化测试、Dev 矩阵或 `unity-cli-gate`**。
- 收敛后冻结 `AffectedValidationScope`，再按"受影响自动化与契约 → 受影响 Dev 真实矩阵 → 适用的 fresh `unity-cli-gate`"顺序验证，前序未过不得启动后序。
- 正式验证只用精确文件/test-name/capability/group/fixture 选择器；禁止无筛选全仓 `npm test`、全 Tool Live 或全部 gate。
- 已知会影响本次工作的既有闸口：`npm run architecture:validate` 要求 built-in Tool 实现位于 `UnityCli.Editor` 程序集，且每个 built-in Tool 文件路径必须严格等于 `unitypkg/Editor/Tools/<group>/<name 去掉 group 前缀>.cs`。新增 Tool 必须满足该布局，否则架构审计失败。
- 任一失败即结束当前验证阶段，分类（PRODUCT_DEFECT / HARNESS_DEFECT / ENVIRONMENT_BLOCKED / SPEC_GAP / TEST_OVERREACH）后回实现阶段，收敛后重跑被失效的切片。

## 9. 需要先确认的开放问题

1. `Assets` 目录的受保护程序集冲突扫描如何加入、上限与错误码如何定义（见第 3 节缺口）。
2. 插件被 Unity 导入后是否需要在安装/卸载流程中做 PluginImporter 的后置校验与回读。
3. 除 2022.3 最低线外的版本支持范围与验证方式（需要额外项目时先取授权）。
4. 事件缓冲与定义快照的具体上限、cursor 语义与跨域 checkpoint 的最终数值（计划有建议值，需在实现时与 schema、描述、测试一起冻结）。
