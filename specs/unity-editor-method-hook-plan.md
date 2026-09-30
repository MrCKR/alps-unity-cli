# UnityCLI 内置动态方法 Hook 接入计划

## 1. 文档状态与目标

本文是待批准的完整实施计划，不是已实现能力的说明。当前仓库没有 `method-hook.*` Tool、Harmony 后端或下述持久化格式；本轮只编写计划，不安装依赖、不修改产品、不启动 Unity 或执行产品门禁。

已确认方向：能力内置在 UnityCLI Bridge 中，不做 Extension、不要求用户安装另一套 Unity Package；支持动态观察、修改和触发代码；同一次 Editor 启动内跨多次 Play 和 Domain Reload 恢复；多个 Editor 实例相互隔离；尽量避免为了 hook 重编译游戏脚本。

本文提出具体的 Tool 名称、契约、资源上限和异常策略供批准。Harmony 的依赖闭包、机制可用性、Play 模式时序与 Domain Reload 重挂已由真实验证确认（见[验证结果](unity-editor-method-hook-verification-findings.md)）；Bridge/CLI 集成与多实例公开层隔离仍属未实现，因此无从验证。

最终目标：陌生 AI 仅凭公开 discovery 和一次 describe，就能在准确实例上安装一个方法 hook，知道它何时生效、能修改什么、怎样读取命中事件、怎样停止，以及跨 Play/重载时哪些状态会保留。

## 2. 交付范围

### 2.1 本次实现

- 拦截 Editor 进程当前域中的托管 C# 方法，包含可解析的 public/private、static/instance 方法。
- Prefix/Postfix 观察参数、返回值、指定实例字段或静态字段；提供无自定义代码的预编译捕获路径。
- 动态 C# 同步回调：Prefix 修改参数、设置替代结果、跳过原方法；Postfix 修改返回结果；两种回调都可读取或修改游戏状态并执行用户代码。
- 方法 hook 定义的安装、替换、启用、停用、删除、摘要读取、精读和事件续读。
- 同一 Editor Session 中跨 Play、场景对象重建、脚本 Domain Reload 重新解析并重挂。
- 复用现有实例路由、单一 live Tool catalog、统一 dispatcher、准入、mutation lease、completion 和 JSON envelope。
- 明确的恢复缺口、失效状态、回调异常、重入与有界资源行为。

### 2.2 明确不实现

- 不向未安装 Bridge 的已运行 Editor 做外部进程注入。
- 不覆盖 `Assembly-CSharp.dll`，不修改游戏 `.cs`，不修改方法所在的磁盘程序集。
- 不自动打开、关闭、重启用户真实项目的 Editor。
- 不在 CLI 内建立常驻 hook daemon、跨实例广播或全局 hook 目录。
- 不覆盖 Development Player、IL2CPP/AOT、native/extern、无方法体、开放泛型、构造器、类型初始化器及无法可靠适配的特殊签名。
- 不提供任意用户 Transpiler、全程序集 IL 重写、任意字段写屏障、属性 getter 自动执行或无损永久事件流。
- 不把 async/coroutine 的整个逻辑生命周期冒充一次方法调用；相关状态机方法本次不作为可支持目标。
- 不保证 Domain Reload 期间持续捕获，也不保证重挂前已经执行的 `Awake`/`Start` 能被追溯。
- 不提供任意代码沙箱、强制终止回调或业务副作用回滚。

精准字段写入插桩属于不同的拦截位置和验证契约。若要覆盖某个 `stfld`/`stsfld`，必须另行批准限定方法、字段身份、IL 指纹、匹配数量和失败关闭规则；不能把方法 Prefix/Postfix 宣称为赋值瞬间监听。

## 3. 源码事实与接入位置

| 现有事实 | 对实施的约束 |
| --- | --- |
| `RegistryOwnershipResolver.Resolve` 要求 built-in Tool 的实现程序集就是 `UnityCli.Editor` | 公开 Tool 留在核心 Editor 程序集，不为独立后端扩展整个 owner 模型 |
| `scripts/lib/public-tool-inventory.mjs` 从 `unitypkg/Editor/Tools` 收集 built-in 实现 | 每个新 Tool 独立文件，进入同一生成 inventory |
| `CodeExecSupport.cs` 使用 Unity 自带 mono/csc 子进程，临时文件固定在 `Library/UnityCli/code-exec-temp` | 抽取并复用编译层，不再加入包内 Roslyn 或第二个编译器 |
| `EditorSessionId()` 由实例、PID 和进程启动身份确定 | 跨域稳定，重启变化，直接用作定义和机制状态的启动边界 |
| `UnityCliBridge.cs` 已有启动、重载、Play 与 quitting 入口 | 复用其生命周期入口，不由每个 Tool 订阅 Editor 回调 |
| `UnityCliWaitScheduler` 集中处理框架等待和 coordinator tick | 恢复、编译等待与清理交给中央推进，不新增私有 update 循环 |
| `contracts/package-assembly-closure.json` 当前 `bundledAssemblies` 为空 | 添加 Harmony 必须同步依赖闭包、hash、许可证、安装冲突与 package 文件清单 |
| `runtime-reflection.method.find` 已提供分页方法发现 | 复用它学习目标签名，不重复实现通用反射浏览器 |

研究材料：

- [动态拦截研究](unity-editor-dynamic-hook-research.md)。
- [字段观察边界研究](unity-editor-live-field-observation-research.md)。
- [领域术语](../CONTEXT.md)。

## 4. 总体架构

```text
AI
  -> tools discovery / tools describe
  -> tools run method-hook.* / command method-hook.*
CLI
  -> existing target selection and admission
  -> existing live catalog / dispatcher / security / completion
UnityCli.Editor
  -> one public Tool per file
  -> MethodHookHost: definitions, revisions, lifecycle, callback contexts
  -> MethodHookStore: same-session snapshots
  -> MethodHookEventBuffer: bounded business events
  -> shared dynamic-code compiler
  -> exact built-in backend interface
UnityCli.Hooks.Editor
  -> Harmony target adapters and patch ownership
  -> Prefix / Postfix / internal exception-preserving Finalizer
  -> target method in the current Editor domain
```

`UnityCli.Hooks.Editor` 是同一 `com.alpsckr.unitycli` Package 内的实现程序集，不是 Extension、不注册独立公开 Tool、不启动 transport，不依赖单独安装步骤。

核心程序集定义窄的内部 backend 接口、Invocation DTO 与 host。后端单向引用 `UnityCli.Editor`；核心按唯一固定程序集和类型身份创建后端，不扫描项目寻找替代 provider。按需要授予 exact `InternalsVisibleTo`，不创建 asmdef 循环。后端缺失/加载失败只隔离 hook 功能，基础 catalog 与状态观察不能被拖垮。

Harmony 类型只进入后端及其显式 precompiled references；核心 Tool 和公开 schema 不出现 Harmony API、程序集路由或 patch method 名称。

## 5. 依赖与可行性决策

### 5.1 依赖闭包

实施开始先选择并锁定一个官方 Harmony 2.x 发行产物：精确版本、目标 framework、上游 URL、许可证、各 DLL assembly identity、SHA-256 和传递依赖全部记录。根据实际产物选择完整的分发形态，不假设只有一个 `0Harmony.dll`，也不把 Harmony/HarmonyX/其他分发混用。

阶段 1 已按此规则确认候选为官方 NuGet `Lib.Harmony 2.4.2` 的 `lib/net472/0Harmony.dll`：单个自包含 DLL，2461696 字节，SHA-256 `7b9e756306fa3d7620e02a857c8927a6ab04973f9bd8a77d3866700a6deac55c`，标识 `0Harmony, Version=2.4.2.0, PublicKeyToken=null`，`net472` 依赖组为空。该包不提供 `netstandard2.0` 实体构建，Unity 侧必须使用 `net472`。

依赖置于 Package 的 Editor 插件目录，提交稳定 `.meta`；PluginImporter 限定 Editor、关闭面向普通项目程序集的 auto-reference，并由后端 asmdef 显式引用。是否需要 unsafe、native 组件或额外 runtime 文件，以真实闭包为准；需要超出当前支持边界时先取得批准，不能默默引入。

更新 closure 审计，让它校验后端 asmdef 的精确 references、bundled 文件集合和 hash，而不是只比较核心 Editor 的 `precompiledReferences`。编译层抽取后，审计从新 owner 校验 Unity 自带 compiler 与固定临时目录，不能继续对旧文件位置做失效字符串断言。

### 5.2 冲突策略

- 安装前检查受保护 assembly identity 和文件名，覆盖 Assets、已解析 Packages 及实际可见依赖的有界扫描。
- 发现项目已有冲突 Harmony/传递依赖时明确报错并给出冲突路径与解决动作；不删除项目依赖、不默认复用未知版本。
- 已安装后运行时按 exact loaded identity 和 Package 来源再次核对；不使用 `Assembly.LoadFrom` 猜测另一份 DLL。
- 即使文件名不同也要检测相同 assembly identity；文件名匹配只是早期检查，不是类型身份保证。
- 不为冲突改名 DLL 后声称隔离成功。若正式支持的 Unity/官方 Package 天然携带冲突依赖，必须重新决策可验证的隔离方案或报告 blocker，不得静默缩小已声明版本支持。

### 5.3 可行性退出条件

在最终依赖方案批准前，先完成独立、受控的最小 Dev 实验方案和夹具，再冻结其范围进行验证。它只回答依赖和补丁机制是否可行，不计作产品验收，也不能在实验通过前写入公开能力描述。

最小可行性实验已完成并归档（见[验证结果](unity-editor-method-hook-verification-findings.md)），覆盖：依赖闭包、插件导入形态、Prefix/Postfix 真正触发、私有/静态/ref 目标、改参与改结果、跳过原方法、撤销与重复安装、补丁生效条件、Play 模式真实 MonoBehaviour 调用链、进入 Play 的域重载、跨 Play 存活、Play 中强制重载后的重挂与去重、第三方 owner 共存、插件 asmdef 编译期引用与单次调用开销。

由该实验确定的实现约束：

- **重挂必须发生在新域最早时机**（等价于 `[InitializeOnLoadMethod]`），不能等中心 update 循环；后者实测晚约 300ms，会错过 `Awake`/`Start` 和首批帧。
- 重挂逻辑不得依赖跨域静态状态，且热路径必须对已清空的引用做防御（探针自身因此抛过 8001 次 NRE）。
- 产品后端不得编译期引用用户游戏程序集；目标按程序集名与签名运行时解析。
- asmdef 程序集不能引用预定义程序集 `Assembly-CSharp`，产品后端与用户游戏代码的分层必须遵守这一点。

当前 minimum Unity 是 2022.3，已在 2022.3.61f1 实测通过；若其他受支持版本需要单独项目，先列明固定项目路径和启动/关闭动作取得治理规则要求的明确授权；不盲目降级现有 Dev 项目，更不能操作真实用户项目。某版本安装失败、补丁无效、类型绑定冲突或重挂顺序不可证明时停止并记录精确 blocker，不能继续把 Harmony 视为已确定可交付依赖。

## 6. 公开 Tool 目录

### 6.1 工具组与职责边界

本计划采用独立的 `method-hook` 工具组，专门承载当前 Editor 域的方法拦截、命中观察、行为干预和 hook 生命周期。八个 Tool 直接归入该组，不新增泛化调试组，不扩大本次能力范围。

| group | 职责 | 本次处理 |
| --- | --- | --- |
| `code` | 一次性执行任意 C#；调用完成后不承担由专用 Tool 管理的长期 hook 生命周期 | 保留 `code.execute`，不迁移、不增加 hook action 路由 |
| `runtime-reflection` | 从 live 内存发现方法和类型 schema，或单次调用 exact 方法 | 保留现有 find/invoke/schema；用于学习 hook 目标签名 |
| `type-inspection` | 查询离线类型/成员缓存 | 保持 offline 边界，不负责当前域 target 存在性或可拦截性 |
| `console` | 读取/清空 Unity Console 日志 | 保留；hook 业务事件有独立 cursor，不冒充 Console 日志 |
| `profiler` | 性能、内存和渲染帧采集与分析 | 保留；与方法命中、行为修改区分 |
| `editor` | Editor 状态、Play 切换、窗口、菜单与会话控制 | 保留；hook 安装不新增进程控制或修改 Play 设置 |
| `method-hook` | 当前域方法插桩、命中观察、行为干预和 hook 生命周期 | 新增下述八个 `method-hook.*` Tool；本次只承诺已定义的方法 hook 能力 |

该组与 `code` 的关键区别是请求结束后仍由专用 host 管理拦截定义；与 `runtime-reflection` 的区别是持续拦截后续自然调用，而不是主动调用一次目标方法。查询日志、性能和 Editor 状态仍路由到各自现有组。

本次不迁移已有公开 Tool，也不提供 alias/双注册。若要进一步合并或重命名现有组，需单独列出受影响 Tool、group/名称契约、真实 callers、文档/Skill 授权和验证切片，再由所有者决定；不能将 hook 接入扩成未经确认的全 catalog 重构。

### 6.2 Group Metadata 与发现入口

- 精确 group id：`method-hook`。
- Hook Tool 名前缀：`method-hook.`。
- Group summary 的源码 owner：`src/tools/tools/group-summaries.ts` 的 `GROUP_SUMMARIES`。
- 一句话 summary：`Live Editor method hooks: install/replace, enable/disable, remove, list/info, and bounded events for observing calls, modifying behavior, and executing C# callbacks.`
- `tools group` 用该 summary 路由到方法拦截；`tools group list method-hook` 返回本组实际注册的八个 Tool；搜索和 describe 使用同一 live catalog。
- `tools describe method-hook.install` 自足说明长期效果、编译/安装成本、授权与恢复边界，不让 AI 靠 group summary 猜接口。
- group 只在发现入口表达，不新增顶层 `method-hook` 命令、不增加独立 transport 或嵌套 group 协议。
- 未实现或未验证的断点、单步、watchpoint、任意字段写入订阅不进入 summary/能力承诺。

上述是实施后的公开契约设计，不是当前 CLI 已经能发现 `method-hook` 的说明。summary 与 live 实现、schema、tests 一起完成后才能进入产品。

### 6.3 本组 Tool

| Tool | 单一职责 | 主要输入 | 主要业务结果 |
| --- | --- | --- | --- |
| `method-hook.install` | 创建并激活一个定义 | target、activation、capture、callbacks、label | hookId、revision、state、eventCursor、目标摘要 |
| `method-hook.replace` | 替换同一 hook 的捕获和回调定义 | hookId、editorSessionId、expectedRevision、新 capture/callbacks | 新 revision 与实际生效定义摘要 |
| `method-hook.enable` | 激活已保留定义 | hookId、editorSessionId、expectedRevision | 新 revision、实际 armed/dormant 状态 |
| `method-hook.disable` | 关闭之后的 hook 效果 | 同上 | 新 revision、disabled 状态 |
| `method-hook.remove` | 删除定义及自己的 Attachment | 同上 | removed、已删除 hookId |
| `method-hook.list` | 分页只读摘要 | state/label 筛选、limit、cursor | items、nextCursor |
| `method-hook.info` | 只读精查一个定义 | hookId、可选源码分页 | target、revision、state、捕获配置、回调 hash、事件摘要、失效原因 |
| `method-hook.events` | 只读有界事件续读 | hookId/kind/playSessionId 筛选、limit、cursor | events、nextCursor、oldestAvailableCursor、hasMore、gaps |

不提供 `action`、通用 `manage`、隐式 upsert、全局 clear-all 或 broadcast。`replace` 不更换 target/activation：更换拦截对象或生效范围使用 remove 加 install，防止回调更新悄悄扩大原授权。

读取 Tool 必须纯读取：不能通过 list/info/events 顺手恢复、编译、补挂、清理文件或自动重试。恢复由框架生命周期执行，用户需要主动恢复 faulted 定义时使用 enable。

所有 mutation 只返回该动作的终态。install/enable 的成功证明当前定义已持久化并已建立应有 Attachment，或明确处于因 activation 不匹配而 dormant；它不证明目标方法调用过、回调成功执行过或游戏业务效果已经发生。

## 7. 输入契约与动态回调

### 7.1 精确目标

Target 至少包含：`assemblyName`、`typeName`、`methodName`、必填 `parameterTypes`（无参数也传 `[]`）、`returnType`、`isStatic`。大小写精确；嵌套类型、by-ref 参数使用公开反射结果的规范全名。同名程序集或签名歧义直接失败，不使用“第一个匹配”。

复用现有内存反射快照与 read budget，但 hook resolver 不沿用仅按参数数量或空列表猜重载的调用逻辑。它解析真实 declaring type 的 exact declared method；重挂重新解析新域身份，旧 `Type`/`MethodInfo`/delegate/metadata token 不直接复用。

对 native/extern/abstract、无 IL、开放或构造泛型、值类型 instance、ref-return、pointer/by-ref-like 等不能验证适配的签名明确拒绝。普通 reference-type instance、static、void、值/引用返回及常见 ref/out 参数必须列入正向适配矩阵。不能为了实现省事拒绝 private 方法。

即使 managed method 可安装，**已编译的调用点也不会看到补丁**。阶段 1 实测确认：补丁安装之后才首次编译的调用点会被拦截（含体量极小、可被 Mono 内联的目标，同程序集与跨程序集均验证通过）；补丁安装之前已经编译的调用点静默无效。分界是**调用点编译时机**，不是方法体量。因此 `Patch` 成功、替换方法存在和 `GetPatchInfo` 可读回都不等于业务命中。

### 7.2 生效范围与捕获

- `activation` 为 `play` 或 `editor-and-play`，默认 `play`。Play-only 定义可在 Edit Mode 安装，返回 dormant，在后续 Play 重挂后 armed。
- 默认开启自动 Invocation 事件；可关闭自动 hit 事件，仅用 `ctx.Emit` 发业务事件，但错误和生命周期记录不能关闭。
- `capture` 明确参数、返回结果、实例字段和静态字段选择，以及正整数采样步长；默认不深度遍历对象、不调用属性 getter。
- 字段按声明类型和字段名精确解析；限直接字段读取，不自动执行访问器。私有字段允许反射读取，访问失败给出具体字段错误。
- `label` 用于人类可读业务检索，不是身份、不要求唯一，不用来去重请求。
- 不在输入里绑定长期裸 instanceId。方法补丁覆盖该方法的调用，回调可根据当前 `Instance` 和参数筛选对象；跨 Play 不保留旧 Unity 对象强引用。

### 7.3 回调接口

`callbacks` 提供可选的 `prefixCode` 与 `postfixCode` C# body，由固定 wrapper 编译为同步 `void` 回调。至少一种捕获或回调行为有效；无需自定义代码时使用预编译路径，不能为了无代码观察启动 compiler。

回调上下文至少提供以下稳定语义：

| API/数据 | 约束 |
| --- | --- |
| `Instance`、`Arguments`、`Result`、方法身份 | 当前 Invocation 的对象/值；不自动序列化整个对象图 |
| `SetArgument(index, value)` | Prefix 可用，按真实参数类型检查后暂存；Postfix 不伪称能够改变已完成调用的普通输入 |
| `SetResult(value)`、`SkipOriginal(value)` | 按返回类型检查；void 跳过不需要替代值；不能恢复已经决定跳过的原方法 |
| `ReadField<T>` / `WriteField<T>` | 精确反射字段辅助接口；写字段属于用户代码副作用，不保证自动 Undo/回滚 |
| `Emit(name, value)` | 有界业务事件；仅允许安全值投影，不调用用户 `ToString`/getter |
| `State` | 有界 JSON 值字典，属于该 hook 的当前 Play Session 或 Edit 运行段；不接受 delegate、Type 或 Unity 对象 |
| 当前 Play 身份、单调时间和 frame | 主线程命中时在拦截点采集；不是 CLI 收到响应的时间 |

参数/结果变更先暂存，回调正常返回后才提交给当前 patch 链；回调抛异常时不提交该回调尚未提交的参数/结果变更。但用户对场景、字段、文件的直接写入不能回滚，必须明确报告可能已发生副作用。

回调按目标原本调用线程触发。当前支持的用户回调限 Unity 主线程：worker 命中不调用 Unity API 或用户回调，保持原方法语义，记录有界 thread fault 并使受影响定义 faulted；主线程由中央推进清理 Attachment。线程限制写入 describe 和错误恢复动作，不是假设所有 C# 方法都在主线程执行。

公开接口不提供 `Task`/`async void` 回调或回调里的 `ctx.Wait`。确需跨帧任务必须另行定义其完成和取消契约，不能挂到已返回的 install request。用户任意代码仍可能自行启动任务或阻塞；不做关键字过滤，也不宣称能替用户管理其任意线程/静态事件。

### 7.4 调用形状示例

以下目标名仅用于说明计划的输入形状，不是当前项目可直接运行的 example；正式 descriptor 必须根据已实现契约给出自足示例并用 Dev 夹具验证。

```json
{
  "label": "ai-movement-order",
  "target": {
    "assemblyName": "Assembly-CSharp",
    "typeName": "Example.AiController",
    "methodName": "BeginMovement",
    "parameterTypes": [],
    "returnType": "System.Void",
    "isStatic": false
  },
  "activation": "play",
  "capture": {
    "arguments": false,
    "result": false,
    "instanceFields": [
      { "declaringType": "Example.AiController", "fieldName": "aiMove" },
      { "declaringType": "Example.AiController", "fieldName": "isPlayCG" }
    ]
  },
  "callbacks": {
    "prefixCode": "ctx.Emit(\"movement-enter\", new System.Collections.Generic.Dictionary<string, object> { { \"aiMove\", ctx.ReadField<int>(\"aiMove\") }, { \"isPlayCG\", ctx.ReadField<bool>(\"isPlayCG\") } });"
  }
}
```

Callback 代码不需要 `using HarmonyLib`，也不要求 AI 编写 patch class、注册 `EditorApplication.update` 或操纵内部 owner id。

## 8. 编译与缓存边界

抽取 `CodeExecSupport.cs` 的“参考程序集发现、固定临时文件、csc 子进程、诊断、assembly bytes”到共享编译 owner。`code.execute` 的 source wrapping、Run 入口、completion probe、stdout 收集和原执行语义不改变；hook 使用自己的 wrapper 和回调 ABI。

三个成本必须公开区分：

1. 首次安装包含新程序集的 Bridge Package 可能触发 Unity 导入、项目编译和 Domain Reload，这是安装成本，不能称作零编译。
2. 已安装 Bridge 后，无代码捕获、启停、删除和事件读取不触发项目编译或新回调编译。
3. 新 C# callback 使用 Unity 自带 mono/csc 编译临时程序集，再 `Assembly.Load(bytes)` 进入当前域。不改 Assets，不主动触发 AssetDatabase.Refresh 或项目脚本编译，但有 compiler 子进程和短暂落盘，不是完全纯内存编译。

编译过程使用现有两层预算：取得执行授权后只使用一次 execution absolute deadline，不因编译重取预算。编译等待不可新增私有 update；复用框架 wait/callback publication，compiler 可在超时/取消时终止，返回 `E_EXECUTION_TIMEOUT`。

compiler 先只产出 bytes/诊断，用户程序集加载和 patch 安装发生在唯一 effect barrier 之后。Patch 建立可能触发目标类型初始化；用户程序集加载也不能被视为纯读取。不得在 dry-run 或只读 Tool 中加载/执行这些程序集。

共享 compiler owner 统一串行化普通 code.execute、公开 hook mutation 和生命周期恢复：最多一个 active compiler，pending 数量受当前 128 个定义与既有 admission 上限共同约束，同一 session/generation/hook/revision 不重复排队。等待队列与进程都携带不可续期的原 deadline，reload/quitting 统一取消。准备引用快照在主线程完成，耗时子进程等待通过框架推进，不占用私有 update；不能因恢复与公开调用竞争而并发写同一临时目录。

callback wrapper 的 namespace/type 和程序集简单名按完整编译 key 派生唯一身份，不能把所有 bytes 都生成为同名 `assembly` 后假设 Mono 绑定安全。编译器输出文件可用该稳定 key 命名，但同一时刻至多一套 source/rsp/output/pdb，读取 bytes 后 finally 删除，无按 hash 累积目录或文件。code.execute 保持当前命名和执行契约，隔离 hook 的类型身份。

cache key 包含源码 hash、wrapper ABI、compiler 参数及引用程序集身份/MVID。相同域相同 key 复用，跨域重新建立引用；不假设原 bytes 能适配新游戏程序集。恢复时自动编译属于同一 session 已授权定义的重新激活，不发起新的公开请求。

动态程序集在同一 Mono AppDomain 内不能逐个卸载。删除 hook 释放 host 引用和业务定义，但不能声称回收已加载程序集。必须按“当前域曾加载的不同编译产物”计数；达到预算后拒绝新的独特回调，停用/删除仍可用，不自动强制 Domain Reload。

## 9. Hook 身份、状态与多实例隔离

### 9.1 身份

逻辑所有权为 `(instanceId, editorSessionId, hookId)`；`hookId` 在成功创建时生成，不能由 label 推导。`revision` 只随显式定义/启停变更递增，不因生命周期自动重挂递增。Attachment 另有当前域 generation，不作为公开协议路由字段。

replace/enable/disable/remove 必须携带 install/info 返回的 `editorSessionId` 和 `expectedRevision`。这是 stale target 与并发 CAS 校验，不是额外授权确认，也不是请求重放拦截。同一公共请求的 attach/recovery 仍遵循既有 exact request 语义。

enable 已经 armed/dormant 的定义、disable 已经 disabled 的定义属于有 fresh readback 的 no-op，返回 `changed:false`，revision 不增加；其他成功变更返回 `changed:true` 和新 revision。对 faulted 的 enable 是重新激活尝试，不是 no-op。remove 的不存在目标仍返回 `E_HOOK_NOT_FOUND`，不伪造第二次成功。

### 9.2 状态

| state | 含义 |
| --- | --- |
| `armed` | 当前域的自己的 Adapter/dispatcher 已安装，定义可在匹配调用中生效；不等于已命中 |
| `dormant` | 定义启用，但当前 Edit/Play 状态不匹配 activation，无活动 callback |
| `restoring` | 正在新域重新解析、编译或安装，不能声称观察连续 |
| `disabled` | 用户显式停用，重载/进入 Play 不自动重新启用 |
| `faulted` | 目标、编译、回调、线程或存储错误已使定义停止执行，保留可诊断配置 |

删除后的定义不在 list 中，events 仍可读取缓冲区内的历史事件。新 install 即使 label 相同，也生成不同 hookId。

### 9.3 多实例

- 只使用现有 `--instance`、target 和 verified live session 选择一个 Editor，不新增 hook 专用选择器。
- 多个实例时沿用当前 CLI 的歧义处理，禁止隐式选第一个；每次调用的会话绑定必须通过既有 attestation。
- 每个 Editor 有独立 host、事件缓冲区和 compiler 生命周期；CLI 退出不移除已安装定义。
- A 实例返回的 hookId/cursor/session 不能在 B 实例使用。共享同一开发数据根也不共享 hook 文件。
- 同一项目正常不并行打开两个交互 Editor；不支持靠多个进程共同写同一个 Library 快照的隐式共享。
- 不污染正式 `%LOCALAPPDATA%\UnityCli`。Dev 测试所有 Editor/CLI 逐进程注入固定 `%LOCALAPPDATA%\UnityCli-Dev`。

## 10. 生命周期与恢复

### 10.1 install

1. 在 effect 前验证严格 schema、exact target、可支持签名、捕获字段、revision/容量和当前 deadline；用户 callback 仅编译为 bytes，不运行目标方法。
2. `ctx.CommitEffect()` 后加载所需 callback 和 backend Adapter。默认 gate 关闭，建立本 CLI 的 patch 并读回自己的 PatchInfo。
3. 原子持久化定义，读回同 session 的精确配置，再打开逻辑 gate；activation 不匹配则保持 dormant。
4. 返回业务 DTO 与事件起始 cursor。不得为了证明命中而自行调用游戏方法或执行示例 callback。
5. 失败只撤销自己的新 Attachment 并关闭 gate；若类型初始化或用户代码已有副作用，错误明确说明业务状态需观察，不伪称事务回滚。

### 10.2 replace / enable / disable / remove

- replace 的新编译/适配准备失败时旧定义仍有效；提交时冻结并校验 revision，替换回调指针和持久定义。目标不变，无需每次修改源码都重新对目标安装 detour。replace 保持原启停意图：disabled 不自动 enable，faulted 的配置可修正但仍需显式 enable；不以更新代码扩大 activation。
- enable 对 faulted 定义做一次完整重解析和重新编译；恢复失败返回稳定错误，不隐藏为成功。
- disable 先关闭逻辑 gate，再确认本 CLI 的目标处理链不再派发该 hook，持久化 disabled。目标仍有其他本 CLI 定义时可保留共用 Adapter。
- remove 先关闭 gate，移除定义；只有最后一个本 CLI 定义离开该 target 才撤销自己的 Adapter。disable/remove 不要求已消失或已变更的方法重新解析成功，否则故障定义将无法清理。
- 并发 revision 不匹配在 effect 前失败。持久化失败必须停止后续派发并报告无法证明终态，不能返回 `ok:true`。
- 不撤销已完成游戏调用的效果，不保证终止已进入的任意用户 callback。主线程 callback 不返回时，CLI 的 disable/remove 也可能无法执行。

### 10.3 Play 进入与退出

- `play` 定义退出 Play 后 dormant，进入下一次 Play 后恢复为 armed；`editor-and-play` 定义按其范围继续有效。
- 在 existing Play lifecycle 入口建立 Play Session 身份。禁用 Domain Reload 时也显式重置 Invocation state、运行时对象引用和采样基线，避免前一局污染后一局。
- 切换 `editor-and-play` 的 Edit/Play 运行段同样重置 callback State；此处不保存静态业务对象。
- 不修改 Enter Play Mode Settings，不擅自关闭 Domain Reload。

### 10.4 Domain Reload

- before reload 关闭 gates，保存当前 session 的定义、有限 JSON State、Play 身份和有界事件 checkpoint，注销/清理旧域 Adapter 与 pending work。
- 新域在 Bridge 初始化中按 current `EditorSessionId` 接受快照；会话不匹配立即忽略，禁止 PID 存活猜测。
- 从定义重新解析 exact 方法，清除旧反射、委托与 Unity 对象引用，按引用 MVID 重编译必要 callback，在安全时机重挂。
- 恢复由中央 lifecycle/coordinator 推进。单个定义失败只 fault 该定义，不封锁基本状态观察或其他 Tool。
- 自动恢复具有每个 generation 总计 120 秒的有界预算，不按每个 hook 重置整体窗口；到期未恢复项 faulted，之后只在新的生命周期事件或显式 enable 时重新处理，不无限重试。
- disabled 定义永不自动重启。恢复失败原因、恢复完成事件、缺口开始/结束必须可读。
- 同一 Play Session 内的 Domain Reload 可以恢复 JSON State；跨 Play 必须重置。业务时钟连续性无法证明时建立新 clock segment 并记录 gap，不能拼出伪连续曲线。

重挂生效起点以 `armed` 生命周期事件为准。实测已确认：默认设置下进入 Play 会重载域，Edit 模式的补丁不存活；重挂只有发生在**新域的最早时机**（等价于 `[InitializeOnLoadMethod]`）才能覆盖 `Awake` 与 `Start`，依赖 update 循环会晚约 300ms 并错过早期回调。因此实现不得把重挂排到 update tick，也不得依赖跨域静态状态。

### 10.5 Editor 重启与退出

正常 quitting 关闭 gate、清理自己的 Adapter 与 pending compilation，并清空本 session 快照。异常退出后文件可能仍在，但下一次启动 session 必然变化，必须作废；不能恢复上次任意代码授权。

新 Editor Session 的第一个只读调用也不得因发现旧快照触发清理或恢复。固定位置失效文件由框架启动机制处理，不能影响新实例业务。

## 11. Patch 所有权、顺序和异常

### 11.1 所有权与顺序

每个 exact target 只建立一套本 CLI 的 Prefix/Postfix/内部 Finalizer Adapter，按 stable target key 与当前 Editor Session 形成 exact Harmony owner。一个 dispatcher 承载该 target 下的多个逻辑定义。

Adapter 使用 Harmony 支持的注入参数和按 exact signature 生成的 runtime IL 桥接，不要求用户 C# 能访问目标的 private 声明类型。生成策略必须分别验证 void、返回值、ref/out、私有参数类型的装箱/回写与 visibility；当前域的 emit 容器和目标 Adapter cache 共用第 14 节上限。不能靠 `ref object __result` 等单一通用签名猜测所有返回类型均能工作，也不能用生成磁盘游戏脚本解决适配。

本 CLI 定义按显式 priority（默认 0）和持久安装顺序形成确定性顺序；每个 phase 内先高 priority，平级先安装者。Prefix 中后一个定义看到前一个已提交的参数变更；只要已决定跳过原方法，后一个定义不能把它重新启用。Postfix 按同一已公布顺序处理结果。

只能按 exact target、自己的 patch MethodInfo 和 owner 撤销。不调用无 owner 的 `UnpatchAll`，不移除第三方补丁，不覆盖其他实例的 owner。

第三方 owner 可能影响原方法是否执行、参数和返回结果。info 给出有界共存摘要；事件说明观察位于本 CLI 的 patch 链位置，不能声称是所有外部补丁之后的绝对最终结果。内部 Finalizer 保持原异常，不提供吞异常或替换原异常的能力。

### 11.2 回调失败

默认固定策略为“记录并停用受影响定义”，不自动重试、不把 callback 异常抛入正常游戏调用。原方法原本抛出的异常照常传播；不能将它误判成 callback fault。

错误事件包含稳定 code、有界原因、hookId/revision 和 Invocation 身份；info 保留最后一次原因。参数/结果暂存失败不提交，但已经写出的游戏字段、对象或文件保持真实状态，不能承诺恢复。

默认 callback 超过 10 ms 且正常返回时记录 overrun 并 fault，后续命中不再派发。该数字只是事后保护阈值，不是抢占式超时；无限循环、长睡眠或死锁可能永久阻塞 Editor。沿用 `code.execute` 对不可强制终止代码的风险披露，不新增安全确认按钮或代码关键字过滤。

### 11.3 重入与资源错误

同一逻辑 hook 的 callback 再次调用自己的目标时，按线程局部重入 guard 绕过该 hook 的再次派发，仍允许原方法执行；累计 bypass 计数，不无限生成递归事件。其他 hook 按其自身规则处理。原方法本身的正常递归不等于 callback 重入，须分开测试。

采样和事件环溢出只能影响记录，不能偷偷修改参数或阻止原方法；cursor 必须显示数据已经不可读。序列化不执行用户 getter/ToString，不把事件投影异常传播到游戏调用。

## 12. 事件与完成语义

### 12.1 事件内容

事件是 hook 的业务结果，不是通用请求日志。包含：session/hook/revision、单调 sequence、Invocation id、事件 kind（prefix/postfix/custom/fault/lifecycle）、Play 身份、clock segment、拦截时刻、主线程 frame、选定对象身份和有界捕获值。

不包含 transport endpoint、listener id、内部 request ledger、通用 completion proof、内部耗时指标或请求级审计。游戏测量的 monotonic timestamp 是本能力业务字段，和通用框架耗时应明确区分。

Prefix/Postfix 使用同一 Invocation id 关联；原方法抛异常时内部 Finalizer 记录 exception 结束，不补造成功 Postfix。对象标识只表示当次观察到的对象，不能跨 Play 当作稳定对象句柄。

### 12.2 续读

- 默认 `limit=100`，硬上限 128；没有 cursor 时读当前最后一页，按 sequence 升序返回。
  默认值与上限由实现时实测并经项目所有者裁定冻结：终端在框架 read budget 内被序列化 3 次，单个事件约计 66 getters，
  绝对上限 10000 下实测 150 条可返回、200 条被 getters 预算拒绝，因此上限取 128（安全余量）、默认保持 100。
- install 返回的 eventCursor 可作为捕获起点；提供 cursor 时读其之后的记录。
- cursor 绑定 Editor Session 和已规范化的筛选条件；换实例、重启或换筛选直接 `E_CONTINUATION_STALE`。
- continuation 推进到最后检查的 sequence，而不是最后匹配的事件，避免筛选导致重复扫描；消费 snapshot 有界，不能追逐不断写入的尾部。
- cursor 早于保留窗口返回 `E_HOOK_EVENT_CURSOR_EXPIRED`，提供新的 oldestAvailableCursor 和缺失范围。不得把丢失区间无声当作完整观察。
- 普通读取不删除事件，不消费共享 cursor；多个 AI 独立续读，互不影响。
- events 不长轮询、不偷偷启用 autotick，也不要求 CLI 留在运行中。

### 12.3 CLI completion

新增 mutations 使用既有 direct-terminal、`observed-state` completion：定义快照与本 CLI Attachment/gate 的 fresh readback 证明安装/启停/删除终态。纯读取使用 `atomic` typed-result，复用 read budget。completion contract 不新增特殊 hook delivery mode。

install 请求完成后不占 admission root、mutation lease 或原 request cancellation token。长期效果的 owner 是 session 内定义，不是未完成请求；callback 不调用已经完成的 `ctx.CommitEffect`。

后续游戏中的 callback 由已授权 Attachment 触发，可能与其他 CLI mutation 在游戏侧交错；本次不发明跨帧全局业务事务。descriptor 必须披露，调用方需要隔离观察时先 disable hook。

超时/终态前断连仍为 `E_EXECUTION_TIMEOUT`。只在同一 Editor Session 内续接同一 exact request；会话改变禁止重投 mutation。新的调用前用 info/list 和业务状态判断，系统不增加 label 幂等、自动重放或跨重启恢复请求。

## 13. 风险、dry-run 与错误

install/replace/enable 因可能激活任意代码，统一准确声明 `destructiveHint:true` 与 `execute-user-code` 内部权限；即使某次输入仅捕获，也使用该 Tool 的最强实际风险描述。disable/remove 不执行用户回调，不标 arbitrary-code destructive，但声明 method-patch/session-definition 副作用。

它们不主动切换 Play、不触发项目 compile/reload，因此 `target=editor`，`playModeSupport=supported`；Play-only activation 是业务状态，不是整个 Tool runtime-only。任意 callback 自己触发的持久化、刷新或 Play 效果按 arbitrary-code 风险披露，不能宣称只会改内存。

五个 mutation 支持 dry-run：只做该动作需要的有界输入、身份、签名、容量和 planned effects 检查，不编译、不加载程序集、不安装补丁、不初始化目标类型、不写定义/事件/State。disable/remove 不以目标方法已经消失为由拒绝预览或清理。计划 DTO 明确没有验证 callback 可编译或业务行为有效。实际执行不得要求事先 dry-run 或额外确认。

| 错误码 | 原因与 nextActions 原则 |
| --- | --- |
| `E_SCHEMA_INVALID` | DTO/cross-field 不合法；指出字段与有效输入形状 |
| `E_HOOK_BACKEND_UNAVAILABLE` | 后端/Unity 机制不可用；提供版本/依赖观察与修复动作 |
| `E_HOOK_DEPENDENCY_CONFLICT` | 已加载依赖身份冲突；指出 exact 冲突，不建议删除整个目录 |
| `E_HOOK_TARGET_NOT_FOUND` / `E_HOOK_TARGET_AMBIGUOUS` | exact target 不存在或歧义；指向公开方法发现和完整签名 |
| `E_HOOK_METHOD_UNSUPPORTED` | native、特殊签名或已知无法拦截情况；给出具体 reason，不提供假 fallback |
| `E_HOOK_NOT_FOUND` | 当前 session 没有该定义；读取同一目标的 list |
| `E_HOOK_SESSION_STALE` / `E_HOOK_REVISION_STALE` | 旧 session 或 revision；重新观察当前定义，禁止静默覆盖 |
| `E_CODE_COMPILE_FAILED` / `E_REFERENCE_RESOLUTION_FAILED` | 复用编译层现有 code，返回有界诊断 |
| `E_HOOK_PATCH_FAILED` / `E_HOOK_STORE_FAILED` | 建立/保存失败；说明 gate 与可能副作用，先观察状态 |
| `E_HOOK_CALLBACK_FAILED` / `E_HOOK_CALLBACK_OVERRUN` | 运行时 fault 事件与最后失效原因；修正后显式 replace/enable |
| `E_HOOK_THREAD_UNSUPPORTED` | 不支持的调用线程；选择主线程业务入口，不擅自把回调排到下一帧 |
| `E_HOOK_LIMIT_EXCEEDED` / `E_HOOK_COMPILE_BUDGET_EXHAUSTED` | 定义/代码加载预算耗尽；解释可释放与不可逐个卸载的区别 |
| `E_HOOK_EVENT_CURSOR_EXPIRED` / `E_CONTINUATION_STALE` | 保留窗口或身份/筛选失效；提供具体重新读取起点 |
| `E_UNITY_BUSY` / `E_EXECUTION_TIMEOUT` | 保持现有准入前无 effect 与执行后 outcome-unknown 区分 |

callback 错误发生在 install 已终态之后，不能回写原 envelope。它们出现在业务 fault 事件/info；新的 enable 操作自身失败时才返回该次调用的 error envelope。所有 error 仍是闭集，提供 `transient`、`retryAfterMs`、可执行 `nextActions`，不能只输出 Console 文本。

## 14. 持久化与资源上限

以下是已冻结的固定硬上限，与 schema、describe、测试和实现（`MethodHookLimits`）使用同一组值：项目所有者在实现期间要求全部预算乘 2，下表已按该决定同步。

| 资源 | 上限/规则 |
| --- | --- |
| 每个 Editor Session 的 hook 定义 | 128 个；每个 target 最多 16 个逻辑定义 |
| Prefix/Postfix 源码 | 各 128 KiB UTF-8；同一输入仍受 Bridge 总 payload 上限 |
| 当前域加载过的独特 callback 编译产物 | 64 个；assembly bytes 累计最多 128 MiB，单产物最多 8 MiB |
| 当前域 Adapter 身份 | 最多 256 个独特目标签名；删除不伪称 unload，已存在签名可复用 |
| callback State | 每 hook 32 KiB JSON，单层数量/深度有界；跨 Play 清空 |
| 事件环 | 16384 条或 16 MiB，先到者限制，淘汰最旧记录 |
| 单事件 | 32 KiB，单次 Emit payload 最多 8 KiB，字段/数组/字符串投影有界 |
| 自动捕获字段 | 每个定义最多 64 个 exact 字段，不遍历 getter 或任意对象图 |
| list/info | list 默认 32、上限 128；info 默认不返回源码，按回调分别最多 32 KiB 文本分页 |
| 总定义快照 | 8 MiB；预留最低输出空间，容量不足在 effect 前失败 |
| 生命周期恢复预算 | 每 domain generation 总计 120 秒，超时后明确 fault，不无限轮询 |

assembly bytes 上限控制已加载产物数量与输入规模，不等于 CLR/JIT 的精确 RSS 上限，不能以此宣称整个 Editor 内存被严格限制。

固定文件布局：

```text
Library/UnityCli/method-hooks/
  definitions.json
  definitions.json.tmp
  events.json
  events.json.tmp
Library/UnityCli/code-exec-temp/
  source.cs
  references.rsp
  assembly.dll / UnityCli.HookCallback.<compile-key>.dll
  assembly.pdb / UnityCli.HookCallback.<compile-key>.pdb
```

definitions 只在配置/启停变更和生命周期 checkpoint 写入；events 平时留在内存，仅重载前作有界 checkpoint，不能每次 hit 重写 8 MiB 文件。不为每个 hook、请求、回调版本或 reload 建立独立累积文件。

快照必须携带 schema format、Editor Session、定义 revision 和 publication sequence。按现有单写者/原子 replace 模式发布，reader 只读取正式路径，不读 tmp/backup 或扫描“最新文件”。fault/disabled 状态也必须可靠保存，避免重载再次执行已失败代码。

配置 publication 与 reload checkpoint 是不同序列。before reload 给 definitions/events 同一 checkpoint identity，并先把当前事件 sequence 下界存到 current-session 的固定 SessionState 键；新域只接受同一 session、匹配 checkpoint 的事件快照。任一文件缺失、损坏或 checkpoint 不匹配时记录明确 history gap，不把旧事件快照冒充最新状态，也不从零重复使用 sequence；sequence 至少从该会话下界继续。读取结果必须显式包含与当前范围有关的 gaps，即使 kind 筛选不包含 lifecycle，也不能把历史丢失无声过滤掉。

若 future format 不匹配，只处理同一明确 owner 的固定文件，不提供 legacy fallback。恢复无效快照时保留有界诊断并 fail closed；未能保存 fault/disabled 状态时必须关闭当前 gate，并把受影响 hookId 和有界原因存入带 current EditorSessionId 的固定 `SessionState` 恢复抑制键。该键跨 Domain Reload 保留、Editor 退出自动作废；新域先检查抑制再读定义，不得因旧磁盘定义为 enabled 而重新执行。抑制集合最多 128 项，不形成另一份定义库；只有显式 enable 成功持久化新状态后才清除对应抑制。

CLI 审计沿用 session 级 append/轮转；hook hit 不灌入通用审计日志。事件导出不是本次新增能力。验证证据若产生 artifact，沿用最新 100 个的保留机制。

## 15. 主要文件改动清单

下述新增文件名是建议布局，实施时可在不改变 owner/职责的前提下细化；不得以此引入另一个 catalog 或通用插件框架。

| owner | 预计改动 |
| --- | --- |
| `unitypkg/Editor/Tools/method-hook/` | install/replace/enable/disable/remove/list/info/events 八个独立 Tool、closed Request/Result DTO、descriptor 与 examples |
| `unitypkg/Editor/Infrastructure/MethodHooks/` | Host、exact resolver、session store、event buffer、Invocation context、code wrapper、backend 接口 |
| `unitypkg/Hooks/`（asmdef + 后端源码） | Editor-only Harmony Adapter（按 exact 签名 emit，含 by-ref 类型化回写）、exact owner 操作、异常保持 Finalizer |
| `unitypkg/Editor/Plugins/`、第三方 notices | pinned 官方依赖及传递闭包、稳定 importer meta、许可证/来源/hash |
| `unitypkg/Editor/Infrastructure/CodeExecSupport.cs` 与 `unitypkg/Editor/Infrastructure/DynamicCodeCompiler.cs` | 抽取编译层为唯一共享 owner（引用发现、固定临时文件、mono/csc 子进程、诊断、bytes、唯一 active compiler）；保留 code.execute 执行/输出/completion probe 语义 |
| `unitypkg/Editor/Infrastructure/MethodHooks/MethodHookLimits.cs` | 全部资源上限与预算的唯一可调配置点；describe 文本、schema 范围与测试都从它读取 |
| `unitypkg/Editor/UnityCliBridge.cs` | 接入既有 startup、before/after reload、Play state 与 quitting，不重复订阅 |
| `unitypkg/Editor/Infrastructure/UnityCliWaiter.cs` | 在中央 coordinator 推进中挂接恢复/cleanup；不增加第二个 update scheduler |
| `unitypkg/Editor/Properties/AssemblyInfo.cs` | 必要时增加 exact 后端内部访问，测试访问同样最小化 |
| `src/package-manager.ts`、`src/assembly-identity.ts` 与 closure loader/types | 新依赖闭包的安装冲突检查（Assets/Packages/PackageCache、文件名与 assembly identity）、来源核对与后端闭包声明；不改安装授权/进程控制语义 |
| `contracts/package-assembly-closure.json` 与审计脚本 | DLL/hash、后端 asmdef、compiler 新 owner、identity 冲突规则，删除失效位置断言 |
| `contracts/tool-completion-contract.json` | 八个 Tool 的 direct-terminal、driver、predicate/proof、reload/outcome-unknown 契约 |
| 生成的 command contract/TS registry/C# registry/schema/package inventory | 从唯一声明生成，不手工制造第二份 metadata |
| `src/security-policy.ts` | 按现有机制补足风险/profile；仅在当前 owner 确需声明时改动 |
| `src/tools/tools/group-summaries.ts` | 新增 exact `method-hook` 路由 summary，保持相邻组边界，禁止用组名 fallback 代替正式摘要 |
| `README.md`、`docs/architecture.md` 和相关现有文档 | 只在实现完成后反映已验证的公开能力和安装边界，不写未来承诺 |
| tests、Unity 测试、Dev 夹具、定向 runner | 与下面的影响面一同收敛，不靠 smoke 代替行为验证 |

公开 Tool 主要由 C# owner 提供。不得为了八个 Tool 再复制一套 `src/tools/method-hook/*` TS-local descriptor；只有现有生成/bridge spec 必须投影的内容才更新。

`skills/unitycli/` 是受保护内容。本轮未获任何单次修改授权，不修改或生成其中任何文件。实施结束要评估是否改变公开运行时路由/跨 Tool 规则：若仅增加一个可由 catalog 发现的 group，不复制完整 Tool 清单到 Skill；若确需修改，先列出具体文件和单次内容取得明确授权，未获授权的必需同步项记录为 blocker。

## 16. 实施顺序与退出条件

### 工作项 A：冻结设计与依赖选择

交付批准后的 scope、八个 Tool/DTO/错误、状态机、资源上限、Harmony closure 候选、Dev 隔离与实验计划。解决源码文件 ownership、编译 ABI、最低支持线和 conflict 策略。未批准或存在 SPEC_GAP 时不写产品实现。

### 工作项 B：完成最小可行性实验

先一次性写好受控实验代码、依赖和断言，再冻结实验影响面，运行限定 Dev 正反矩阵。禁止把安装正式功能和实验一边写一边试混合；失败先分类再修改实验。结果包含二进制来源、Unity/Mono、patch ownership、reload 时间线、实际 hit/mutation 证据。

阶段 1/2（机制与生命周期）已完成并归档，结论见[验证结果](unity-editor-method-hook-verification-findings.md)：依赖闭包、插件形态、观察/改参/改结果/跳过原方法、私有与静态与 ref 目标、撤销与重复安装、补丁生效条件、Play 真实调用链、跨 Play、域重载重挂与去重、第三方 owner 共存、开销均已取得真实证据。

尚未取得的证据：Bridge/CLI 集成、事件缓冲与 cursor、定义持久化上限、多实例公开层隔离、临时文件盘点与容量上限。这些都依赖产品实现，尚未编写，因此不能计入已完成的可行性实验。

### 工作项 C：一次性完成产品实现

按依赖闭包 -> 共享 compiler -> backend/host -> store/lifecycle -> 八个 Tool -> completion/生成投影 -> 文档与测试/夹具的顺序完成。期间可做源码检查和必要构建以确认结构，但不启动正式自动化/Live/gate。不遗留双轨、stub 或“以后清理”的路径。

产品缺陷若在实施/验证中被确认，先按 issue lifecycle 在 canonical 源码修改前登记；不把授权 Dev 项目中的安装 Package 当最终开发面，也不对真实用户项目做热修。

### 工作项 D：冻结 AffectedValidationScope

记录所有改动 owner、真实直接 callers、公开 Tool/字段/错误、选中测试与 test-name、Unity/Play 状态切片、适用 gate 及明确排除项。输入固定 build/source hash，后续修改使相应证据失效。

### 工作项 E：受影响自动化与契约

先执行精确文件/test-name 范围和增量契约；必须通过后才能安装当前 canonical 产物并做正式 Live。需要编译全依赖图不等于运行全仓行为测试。

### 工作项 F：canonical 安装与 Dev Live

使用当前公开 init/install 流程同步 Bridge、依赖和夹具到授权 Dev，待 import/compile/reload 结束确认 ready。逐项执行下面的 Live 矩阵，验证业务 readback 和副作用，不仅验证 `ok:true`。

### 工作项 G：fresh 陌生 AI gate 与完成审计

只在 E/F 全部通过后启动适用的 fresh `unity-cli-gate`，由主 Agent 归档完整证据。审核资源盘点、restart 作废、多实例隔离、文档一致与未授权 Skill blocker；所有必需项都通过才宣称功能完成。

本计划不包含发布、升版本、npm publish、tag、push 或本地正式安装。将来提出发布时另行加载 release 规范，不能把计划批准解释为发布授权。

## 17. 定向自动化与契约验证

### 17.1 新增测试

- `tests/method-hook-contract.test.ts`：八个 Tool inventory、summary 路由与组归属、strict schema 与风险 metadata、唯一 effect barrier、completion 分类、command contract 复用、closure 与 `.meta`/运行核对一致性。
- `tests/package-assembly-identity.test.ts`：assembly identity 解析与规范化、异名同 identity 冲突识别。
- `tests/summary-routing.test.ts`：method-hook exact summary、八个 Tool 的组归属、相邻组不迁移与发现边界。
- `tests/code-exec-contract.test.ts`与`tests/runtime-code.test.ts`：共享 compiler 抽取后的 code.execute 编译、deadline、临时文件、Run/stdout/exception 与 completion probe 回归。
- `unitypkg/Tests/Editor/UnityCliMethodHookTests.cs`（12 项）：exact 方法解析、字段选择与投影边界、typed 参数/结果、State、事件环/采样、重入、事件页预算与实际 DTO member 数一致。
- `unitypkg/Tests/Editor/UnityCliMethodHookIntegrationTests.cs`（14 项）：真实 Harmony patch 与自己的 Adapter readback、foreign owner 保留、callback fault 与启停删除、revision/CAS、callbackPending、事件 checkpoint 截断、插件导入后置核对。
- 受影响的既有 catalog/completion 夹具（`UnityCliToolCatalogTests`、`tests/tool-completion-contract.test.ts`、`tests/tool-completion-commit-effect-audit.test.ts`、`tests/tool-schema-generator.test.ts`）只覆盖 built-in owner、新 Tool DTO/投影条目与 completion 分类，不把增量验证扩成全 Tool 测试。

### 17.2 既有回归切片

共享 compiler 抽取影响 `tests/code-exec-contract.test.ts`、`tests/runtime-code.test.ts` 中的编译、deadline、临时文件、Run/stdout/exception 和 completionProbe 类别；Unity 的异常投影和实际 code.execute 夹具选择相应 test-name。不要重跑无关 Profiler、资产、Package 操作矩阵。

catalog/completion 改动选择 `tests/tool-completion-contract.test.ts`、`tests/tool-completion-commit-effect-audit.test.ts`、`tests/tool-schema-generator.test.ts` 及 `UnityCliToolCatalogTests` 中 built-in owner、新 Tool DTO/投影条目。`tests/summary-routing.test.ts` 补足 method-hook 的 exact summary、完整八个 Tool 的 group 归属、相邻组不迁移及发现边界断言，选择新增 case 与直接受影响的现有 case。Bridge/scheduler 只覆盖 hook 生命周期接入和既有 wait cleanup 同一入口的等价类，不自动宣布所有 coordinator 受影响。

installer 冲突检查只覆盖新 bundled dependency 与现有 Newtonsoft 允许 provider 的不变式；不用所有 Extension 的实际安装来证明一条 DLL 规则。

### 17.3 选择器前置

当前 command/completion/architecture 审计中的若干入口无 Tool/owner 选择器。正式验证前，为需要的 owner/file/tool 增加精确 selector，并独立验证 selector 不隐藏本次直接影响；不得直接执行无筛选全仓命令绕过 scope。

package assembly closure 与文件 inventory 的 exact-set/hash 是新增二进制的直接契约，需要检查同一 Package 的依赖集合完整性；这不是全 Tool 行为验证。compiler/生成器可读完整依赖图，但行为测试仍必须精确选择。

不运行无筛选 `npm test`、`npm run build:test`、全 Tool Live、全 Dev EditMode/Player 或所有 gate 类型。测试编译走精确受影响 harness；现有构建链若隐含无关行为审计，也应先补所需 selector，而不是把方便当授权。

## 18. Dev Unity 真实验证矩阵

所有 case 由主 Agent 编排并保存命令、input、envelope、前后状态、build hash、Unity 版本、准确 instance 与业务 readback。各行覆盖的是必需行为类别，参数组合可按注明的等价类压缩，不能删掉类别。

| 类别 | 必测状态/操作 | 验收断言 |
| --- | --- | --- |
| 自发现 | group/search/describe；tools run 与 command 两种入口 | exact method-hook summary 与八个 method-hook.* 一致，相邻组无迁移；同一 catalog/DTO/dispatcher；无新顶层命令或内部字段 |
| 基础观察 | Edit 的 editor-and-play；Play 的 play；无代码捕获 | exact 方法实际 hit、Prefix/Postfix 对应同 Invocation；无 compiler/项目 compile |
| target 精度 | public/private、static/instance、零参数、重载、继承声明类型 | exact 匹配；错误重载/程序集不会挂到另一个方法 |
| 修改 | 参数、ref/out、void skip、value/reference 结果、Postfix 替换 | 原方法实际调用次数与返回结果逐项证明，不只看事件 |
| 执行代码 | Emit、读/写私有字段、有限 JSON State | 游戏状态实际改变，副作用与 payload 截断准确 |
| lifecycle | 至少三轮 Play；Domain Reload on/off；scene reload on/off 四组合 | 同一定义、不重复 Adapter/hit、State 跨 Play 重置、旧对象引用消失 |
| 脚本重载 | 同签名新 MVID、目标删除/签名变化、重新 enable | 正确重解析；不误挂；失败 faulted，可明确修复 |
| 起点与缺口 | Awake/Start/首帧相对 armed；Play 内 reload | 捕获起点与 gap 可见；不追溯/补造丢失调用 |
| callbacks 异常 | Prefix/Postfix 抛出、暂存变更失败、原方法抛出 | 自己的 fault 关闭；原异常保持；直接副作用不被假装回滚 |
| 重入与线程 | callback 调自身方法、正常原方法递归、worker 命中 | 不无限递归；原方法语义保持；thread fault 无越线程 Unity API |
| coexistence | 同 target 两个本 CLI 定义、多个 priority、foreign owner | 定义顺序稳定；移除一个不影响其他；foreign 补丁完整保留 |
| 启停/更新 | armed/dormant/disabled/faulted 下 replace/enable/disable/remove | revision、store 与 gate fresh readback 一致；失败不隐藏 |
| dry-run | 五个 mutations 的正常/错误预览 | 无该动作引起的 compile/load/patch/callback/hook 文件/State/事件副作用 |
| 分页事件 | 无 cursor、从 install cursor、filter、换 filter、多个 reader、溢出 | 进度单调、身份绑定、丢失范围明确、读取不消费共享状态 |
| 大输出/边界 | 源码、字节产物、64 定义、8 同 target、32 编译 hash、事件/State | 上限前后稳定，达到上限仍可 disable/remove/info |
| 编译回归 | code.execute 的正常/错误/超时/probe；hook 新/重复 source | 共享 compiler 不改变原语义；重复 key 不重复加载 |
| 崩溃/重启 | normal quit 与专用 Dev 异常退出 | 新 session 不恢复旧定义/代码；旧 cursor/session 不能命中 |
| 存储故障 | 有界夹具注入 publish/checkpoint failure、损坏/异格式 | gate fail closed；只处理本 owner 固定文件；读取仍纯只读 |
| 断连/超时 | compiler 前、effect 前后、reload listener generation、CLI 提前退出 | 既有 deadline/outcome-unknown；无新 session 重投；CLI 退出 hook 不被误取消 |
| 多实例/多 CLI | A/B Dev Editor、同名 label、错误 session/cursor、同 revision 竞争 | A/B 严格隔离；CAS 一个成功另一个 stale；无广播 |
| 临时资源 | 高次数 install/replace/remove、reload、restart，盘点文件/bytes | 固定路径/常数文件数/硬上限，机制状态重启作废 |
| 安装闭包 | 干净 Dev 安装、缺 DLL/hash 错、冲突 identity | 不完整安装不启用 hook；不破坏其他正式项目/依赖 |

多实例需要两个真实 Dev Editor。现有治理只允许自动化代管 `Dev/UnityProject`，不能因为另一个项目也在 Dev 目录就默认拥有进程控制授权。建议把 B 固定为 `D:/workspace/alps-unity-cli/Dev/UnityProject-HookPeer`；在实施前须由所有者明确允许这一准确项目以及本次所需启动、正常关闭/重启动作。计划批准本身不代替该授权。

取得明确授权后，B 的创建/安装仍用公开流程和同一隔离根，不用真实用户项目，不按日期生成无限副本；未取得授权或缺少可隔离的 B 时，多实例 Live 项保持 BLOCKED，不能以假 registry 或单进程单元测试替代。最低 Unity 版本若另需固定 baseline 项目，同样执行准确项目/动作授权规则。

Live 副作用断言区分本次 Tool 引起的变更、既有游戏自然推进和正常框架审计；dry-run 不产生新的 hook 效果不等于整个运行中 Editor 的所有文件/事件都静止。

Dev 启动前记录正式根文件集合、正式进程与正式版 instances list。每个 Dev Editor 和源码 CLI 都逐进程注入同一固定 Dev data dir，不用 setx/用户环境变量或 Unity Hub 普通启动证明隔离。结束核对正式文件集合、正式进程和正式版发现未被 Dev 改动；发现越界立即停止。

主机递归清理必须真实路径校验严格位于任务专用临时/缓存根下，拒绝链接逃逸、通配符和允许根本身。固定 Dev 项目不是可随意递归删除的临时根；非临时删除先精确列出、dry-run 并获得本轮批准。正式实例错误记录只在确认写入进程退出后精确处理单文件。

## 19. 陌生 AI 门禁

适用 fresh gate 分别覆盖：

- capability：在指定 Dev 游戏中观察实际 AI 开始运动时的人类锁定状态，并读出足够判断先后顺序的事件。
- task：临时改变一个 Dev 行为，再恢复原行为，验证方法 hook 与读取/恢复工具能被正确组合。
- perturbation：跨两轮 Play 和一次正常代码重载继续同一观察，或者仅在指定 A 实例生效而不影响 B。

没有 modal/Windows blocker 行为改动时，不为凑类型运行 blocker gate。各 gate 只选一个明确受影响业务目标；完整矩阵仍由主 Agent 承担。

代理必须是全新 `unity-cli-gate`、实际 `LMD/deepseek-v4-flash`、不继承上下文、仅 windows-pwsh。提示只给短业务目标、公开启动前缀 `node dist/cli.js`、准确项目/instance/夹具事实与允许副作用。不得提供 Tool 名、发现链、JSON、参数或脚本步骤，也不能让被测代理记录主 Agent 的审计格式。

首次 help/discovery/describe/业务/cleanup 非预期失败即停止；`ok:true` 后首次业务验证无效果也算失败。不得指导重试、纠参、换工具或把并行后续调用补成 PASS。必要诊断只问首次选择依据，禁止工具调用；修正后 fresh 会话重新验证。

主 Agent 独立归档原 prompt、实际模型/agentId、完整 transcript、初次选择、关键业务 readback、清理与 Unity 前后状态。gate 不替代第 18 节。

## 20. 动机案例的验收方式

针对“AI 是否在人类约 4 秒解锁前已经移动”，先建立各信号的业务含义和所属对象：`aiMove`、`lim`、`FuncDelayTime`、`isPlayCG` 的数值变化不能未经确认就等同于运动/解锁。

选实际运动开始的业务方法和实际解锁的业务方法，分别建立观察 hook，记录同一 Play Session/clock segment 下的拦截时间、必要字段与对象身份。若只有 Update 等边界方法可用，明确报告“在该方法边界观察到状态”，不声称就是字段赋值时间。

验收至少证明：

- 可在已运行且已安装 Bridge 的 Play 中安装，目标游戏源文件和程序集磁盘内容未被 hook 修改，无项目脚本编译/reload 被安装动作主动触发。
- 真实 AI 运动与人类解锁分别有可信业务标记，可用单调时间比较先后；不能把 CLI 响应时间或模糊逻辑累计时间当信号时间。
- 多轮 Play 自动生效且事件分局；代码 reload 后出现明确恢复/gap，无法连续比较的区间不作结论。
- 若希望纠正提前运动，可以用 Prefix 在经确认的业务条件下跳过/改参或执行代码，实际游戏 readback 证明变化；随后 disable/remove 使原行为恢复。
- 指定 B 实例或另一项目完全不出现 A 的定义、事件、状态变化和依赖写入。

此前 `aiMove 0→9`、`lim 1→0`、`FuncDelayTime→0`、`isPlayCG true→false` 与逻辑 +5.6 秒观察仅是待核验线索，不构成 Harmony 功能或“约 4 秒”测量已被证明。

## 21. 完成标准与当前未决项

功能完成必须同时具备：

- pinned 依赖在最低支持 Unity 与 Dev Unity 上可用，闭包/许可证/冲突策略已验证。
- 八个 Tool、schema、descriptor、group、安全、completion、生成契约和文档一致，没有新顶层命令、Extension 旁路、双轨或 stub。
- 观察、修改、同步代码执行、启停删除都通过真实业务 readback；armed 只表示安装状态，业务命中单独证明。
- 跨 Play/Domain Reload 恢复、早期捕获边界、fault/disabled 保留、重启作废有现场证据。
- 多实例与多 CLI 隔离、事件缺失/续读、有界文件及动态产物预算通过正反边界验证。
- 所有受影响自动化/契约、Dev 矩阵、适用 fresh gate 通过；未授权而必须同步的公开 Skill 项已解决。
- 无 unresolved PRODUCT_DEFECT/HARNESS_DEFECT/SPEC_GAP 或必测 ENVIRONMENT_BLOCKED。失败立刻结束当前验证，分类后回实现，收敛后重跑失效切片，不能拼接不同源码的局部 PASS。

### 当前状态

八个 `method-hook.*` Tool、共享 compiler、Harmony closure 与冲突防护、原子快照与恢复抑制、Play 与 Domain Reload 恢复、事件环与 cursor 续读、多实例隔离和临时资源上限均已实现，并按第 17 节的定向自动化/契约与第 18 节的 Dev 矩阵完成验证；逐轮证据、实测数值与期间修复记录见[验证结果](unity-editor-method-hook-verification-findings.md)。Harmony 侧已确认的部分（`Lib.Harmony 2.4.2` `net472` 单文件零传递依赖、插件导入与 asmdef 编译期引用、补丁生效条件、Play 真实调用链、跨 Play 与域重载重挂及去重、第三方 owner 共存）在该文件中保持原样。Dev peer 项目 `Dev/UnityProject-HookPeer` 的创建、包安装与启动/正常关闭已由所有者按准确项目与动作授权，A/B 隔离实测已完成。

仍未完成的部分只有两项，且都不写作已完成。其一是第 19 节的陌生 AI 门禁：本轮会话因缺少 `windows-pwsh` 扩展而环境阻塞（属环境问题，不是产品缺陷，替代 shell 被仓库规范禁止）；项目所有者已就本轮裁定：接受该门禁为环境阻塞、不阻断本轮完成，依据已收集的定向功能证据结项，并在报告中如实记录门禁未执行及原因。其二是 Unity 支持线仍只覆盖 minimum 2022.3.61f1，其他受支持版本需要另行授权的固定项目才能验证。

公开 Skill `skills/unitycli/` 经所有者裁定本轮无需修改：它只承担稳定的运行时路由、按需发现、跨 Tool 通用规则与 FeedbackMode，而 method-hook 只是新增一个工具组，发现仍走既有的 group/search/describe，未改变公开路由、envelope、错误恢复或 host 预算，能力细节留在 live descriptor 与 describe。

