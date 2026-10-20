#if UNITY_EDITOR
using System;
using System.Globalization;

namespace UnityCli
{
    /// <summary>
    /// debug 域唯一的资源上限与预算声明点：hook 能力预算与统一调试事件存储预算都在这里。
    ///
    /// 调整 debug 行为时只改这里：实现、schema 范围、describe 文本与定向测试都从本类读取，
    /// 不允许在 Tool、host、store 或后端里再写一份数量常量。
    /// 单位为字节、条数、毫秒或秒，均由字段名说明；全部为编译期常量，避免运行时配置漂移。
    /// </summary>
    internal static class DebugLimits
    {
        // ---- 定义与目标 ----

        /// <summary>同一 Editor Session 内允许保留的 hook 定义总数。</summary>
        internal const int MaxDefinitionsPerSession = 128;

        /// <summary>同一 exact target 上允许的逻辑定义数（共用一套 Adapter）。</summary>
        internal const int MaxDefinitionsPerTarget = 16;

        /// <summary>单个定义的 Prefix 或 Postfix 源码上限（UTF-8 字节）。</summary>
        internal const int MaxCallbackSourceBytes = 128 * 1024;

        /// <summary>单个定义允许捕获的 exact 字段数（实例字段与静态字段合计）。</summary>
        internal const int MaxCaptureFields = 64;

        /// <summary>采样步长允许的最大值，避免用一个巨大的步长表达"永不采样"。</summary>
        internal const int MaxSampleEvery = 1000000;

        // ---- 动态回调编译产物 ----

        /// <summary>当前域曾加载过的独特回调编译产物数量上限。</summary>
        internal const int MaxCompiledArtifacts = 64;

        /// <summary>当前域累计加载的回调程序集字节上限。</summary>
        internal const long MaxCompiledArtifactBytes = 128L * 1024 * 1024;

        /// <summary>单个回调编译产物字节上限。</summary>
        internal const long MaxSingleArtifactBytes = 8L * 1024 * 1024;

        /// <summary>当前域允许建立的独特 Adapter 返回类型身份上限。</summary>
        internal const int MaxAdapterIdentities = 256;

        // ---- 回调 State ----

        /// <summary>单个 hook 的回调 State JSON 字节上限；跨 Play 清空。</summary>
        internal const int MaxStateBytes = 32 * 1024;

        /// <summary>State 值投影允许的最大深度。</summary>
        internal const int MaxStateDepth = 16;

        /// <summary>State 单个容器允许的最大条目数。</summary>
        internal const int MaxStateContainerItems = 128;

        /// <summary>单个事件序列化后的字节上限，超出即按边界截断。</summary>
        internal const int MaxEventBytes = 32 * 1024;

        /// <summary>单次 Emit 业务 payload 的字节上限。</summary>
        internal const int MaxEmitBytes = 8 * 1024;

        /// <summary>事件历史缺口记录条数上限；缺口只描述不可读区间，不声称调用未发生。</summary>
        internal const int MaxHistoryGaps = 32;

        // ---- 投影边界 ----

        /// <summary>参数、结果、字段与 Emit 值投影允许的最大深度。</summary>
        internal const int MaxProjectionDepth = 8;

        /// <summary>投影中单个集合允许的最大条目数。</summary>
        internal const int MaxProjectionItems = 128;

        /// <summary>投影中单个字符串允许的最大 UTF-8 字节数。</summary>
        internal const int MaxProjectionStringBytes = 4 * 1024;

        // ---- 统一调试事件存储 ----

        /// <summary>同一 Editor Session 在统一事件存储中允许保留的最大事件条数。</summary>
        internal const int MaxEventsPerSession = 4096;

        /// <summary>同一 Editor Session 在统一事件存储中允许保留的最大估算字节数。</summary>
        internal const long MaxEventBytesPerSession = 8L * 1024 * 1024;

        /// <summary>整个 Unity 进程所有 debug 来源合计允许保留的最大估算字节数。</summary>
        internal const long MaxEventBytesGlobal = 32L * 1024 * 1024;

        /// <summary>单条事件入库后的字节上限（含投影与固定字段）。</summary>
        internal const int MaxStoredEventBytes = 16 * 1024;

        /// <summary>
        /// 入库投影中单个字符串允许的最大 UTF-8 字节数。
        /// 严格小于 hook 捕获投影的 MaxProjectionStringBytes；两者都强制执行，入库边界更紧时以入库边界为准。
        /// </summary>
        internal const int MaxStoredProjectionStringBytes = 2 * 1024;

        /// <summary>
        /// 入库投影允许的最大深度。
        /// 严格小于 hook 捕获投影的 MaxProjectionDepth；事件进入存储时按本上限重新物化。
        /// </summary>
        internal const int MaxStoredProjectionDepth = 4;

        /// <summary>
        /// 入库投影中单个容器允许的最大条目数。
        /// 严格小于 hook 捕获投影的 MaxProjectionItems；事件进入存储时按本上限重新物化。
        /// </summary>
        internal const int MaxStoredProjectionItemsPerLevel = 64;

        /// <summary>debug.events.read 等待新事件的上限（毫秒）；到期返回当前尾部而不是失败。</summary>
        internal const int EventsMaxWaitMs = 30000;

        // ---- 公开分页与快照 ----

        /// <summary>debug.hook.list 默认页大小。</summary>
        internal const int ListDefaultLimit = 32;

        /// <summary>debug.hook.list 允许的最大页大小。</summary>
        internal const int ListMaxLimit = 128;

        /// <summary>debug.events.read 默认页大小。</summary>
        internal const int EventsDefaultLimit = 50;

        /// <summary>
        /// debug.events.read 允许的最大页大小。
        /// 由框架冻结的绝对 getter 预算、单页需要的有预算序列化次数与事件条目的可序列化 member 数推导：
        /// 页上限必须让 EventPageGetters(EventsMaxLimit) 仍落在 AbsoluteMaxGetters 内，否则满页读取会在发布失败。
        /// </summary>
        internal const int EventsMaxLimit = (int)((UnityCliRequestBudget.AbsoluteMaxGetters - 256L) / (EventPageSerializations * (long)EventItemMembers));

        /// <summary>
        /// 同一事件页在框架内需要的有预算序列化次数：
        /// 结果 DTO 序列化、完成校验重读与 terminal envelope 序列化各自读取一次同一批 member。
        /// </summary>
        internal const int EventPageSerializations = 3;

        /// <summary>
        /// DebugEventItem 的可序列化 member 数量。
        /// 事件页声明的 read budget 由它推导，因此 UnityCliMethodHookTests 会用反射验证该常量与实际 DTO 一致。
        /// </summary>
        internal const int EventItemMembers = 30;

        /// <summary>debug.hook.list 单个条目的可序列化 member 数量。</summary>
        internal const int ListItemMembers = 12;

        /// <summary>debug.hook.info 单个回调源码分页的字节上限。</summary>
        internal const int MaxInfoSourcePageBytes = 32 * 1024;

        /// <summary>定义快照文件允许的最大字节数。</summary>
        internal const int MaxSnapshotBytes = 8 * 1024 * 1024;

        /// <summary>
        /// 事件 checkpoint 允许的字节预算。每 session 事件字节上限（8 MiB）高于总快照上限（8 MiB），
        /// 因此落盘时必须从最旧端有界截断，而不是让整个 checkpoint 失败；截断区间作为 history gap 记录。
        /// </summary>
        internal const long MaxCheckpointEventBytes = 6L * 1024 * 1024;

        // ---- session 定义与资源占用 ----

        /// <summary>同一 Editor Session 内允许保留的 session 定义总数。</summary>
        internal const int MaxSessions = 16;

        /// <summary>单个 session 同时允许的活动资源数量。</summary>
        internal const int MaxActiveResourcesPerSession = 4;

        /// <summary>单个 session 允许注册的显式有限清理条目数量。</summary>
        internal const int MaxCleanupStepsPerSession = 32;

        /// <summary>单个 session label 的 UTF-8 字节上限。</summary>
        internal const int MaxSessionLabelBytes = 256;

        /// <summary>单个 session 初始 JSON state 的字节上限。</summary>
        internal const int MaxSessionStateBytes = 32 * 1024;

        /// <summary>
        /// debug 域唯一 session 存储文件的字节上限。
        /// 固定字段加上 MaxSessions × MaxSessionStateBytes 的结构上限仍低于本值，超过即视为不可用文件。
        /// </summary>
        internal const long MaxSessionStoreBytes = 8L * 1024 * 1024;

        /// <summary>debug.session.list 默认页大小。</summary>
        internal const int SessionListDefaultLimit = 50;

        /// <summary>debug.session.list 允许的最大页大小；定义总数上限低于该值，因此任何保留定义都能在一页内读完。</summary>
        internal const int SessionListMaxLimit = 200;

        // ---- program 定义、运行与调度 ----

        /// <summary>单个 session 内允许保留的 program 定义总数。</summary>
        internal const int MaxProgramsPerSession = 32;

        /// <summary>整个 Unity 进程允许保留的 program 定义总数；多个 session 不得绕过该全局限制。</summary>
        internal const int MaxProgramsGlobal = 64;

        /// <summary>整个 Unity 进程同时允许的活动 program 运行数量。</summary>
        internal const int MaxActiveProgramRunsGlobal = 16;

        // ---- async flow（debug.code.executeAsync）----

        /// <summary>单个 session 同时允许的活动流数量。</summary>
        internal const int MaxFlowsPerSession = 4;

        /// <summary>单个 Unity 实例（本进程 AppDomain）同时允许的活动流数量；两级限制同时生效。</summary>
        internal const int MaxFlowsPerInstance = 8;

        /// <summary>流生命周期的默认预算；从首次 tick 起算，不含编译时间。</summary>
        internal const double FlowBudgetDefaultMs = 300000d;

        /// <summary>流生命周期硬上限；与 program 运行时长上限对齐，不引入第三套量级。</summary>
        internal const double FlowBudgetMaxMs = 3600000d;

        /// <summary>提交调用默认等待时长；超过则返回句柄而不取消流。</summary>
        internal const double FlowRequestWaitDefaultMs = 120000d;

        /// <summary>提交与等待调用允许的最大等待时长。</summary>
        internal const double FlowRequestWaitMaxMs = 900000d;

        /// <summary>终态流记录的有界保留数量，供重复查询拿到同一结果。</summary>
        internal const int MaxRetiredFlows = 32;

        /// <summary>Play 模式下等帧的停滞上限：帧计数长期不动时以超时结算，避免流无意义地等到预算耗尽。</summary>
        internal const double FlowFrameWaitStallMs = 4000d;

        /// <summary>流返回值的 UTF-8 字节上限，与一次性执行的返回值预算同一量级。</summary>
        internal const int MaxFlowResultBytes = 262144;

        /// <summary>日志等待的环形采样上限；溢出的命中计数上报，不静默丢失。</summary>
        internal const int MaxFlowLogSamples = 256;

        /// <summary>单条日志在匹配前截断到的字节上限，避免一条巨大日志占满采样环。</summary>
        internal const int MaxFlowLogMessageBytes = 512;

        /// <summary>单个 program 编译单元的源码字节上限（与 code.execute、pause evaluate 共用一个数值）。</summary>
        internal const int MaxProgramSourceBytes = 256 * 1024;

        /// <summary>单个 program 允许声明的命名 entry 数量。</summary>
        internal const int MaxProgramEntries = 16;

        /// <summary>单个 program 允许声明的显式 binding 数量。</summary>
        internal const int MaxProgramBindings = 16;

        /// <summary>单个 program revision 状态及其 schema 各自的 JSON 字节上限。</summary>
        internal const int MaxProgramStateBytes = 32 * 1024;

        /// <summary>单次 program.run 的 JSON 参数字节上限。</summary>
        internal const int MaxProgramArgsBytes = 16 * 1024;

        /// <summary>program.run 的 durationMs 上限；必需字段的合法上界。</summary>
        internal const long MaxProgramRunMaxDurationMs = 3600000L;

        /// <summary>单次 entry 允许登记的命名检查与证据引用条数上限。</summary>
        internal const int MaxProgramChecks = 32;

        /// <summary>跨执行点投递队列的条目上限；超出即丢弃最旧条目并记录丢弃事实。</summary>
        internal const int MaxProgramDeliveries = 64;

        /// <summary>一次投递在未到达目标执行点前的有效时长（毫秒）；过期即标记 expired，永不补执行。</summary>
        internal const double ProgramSignalTimeoutMs = 1000d;

        /// <summary>debug.program.wait 允许的有界等待上限（毫秒）。</summary>
        internal const int ProgramWaitMaxMs = 30000;

        /// <summary>debug.program.list 默认页大小。</summary>
        internal const int ProgramListDefaultLimit = 32;

        /// <summary>debug.program.list 允许的最大页大小。</summary>
        internal const int ProgramListMaxLimit = 64;

        // ---- 证据窗口 run ----

        /// <summary>单个 session 允许保留的 run 摘要条数；超出即淘汰最旧摘要。</summary>
        internal const int MaxRunSummariesPerSession = 16;

        /// <summary>单个 session 允许保留详细证据的 run 窗口数量；更旧的窗口只留摘要。</summary>
        internal const int MaxDetailedRunWindowsPerSession = 4;

        /// <summary>整个 Unity 进程所有 run 窗口详细证据允许占用的字节上限；多个 session 不得绕过它。</summary>
        internal const long MaxDetailedRunBytesGlobal = 32L * 1024 * 1024;

        /// <summary>单个 run.begin 允许声明的必需检查数量。</summary>
        internal const int MaxRunRequiredChecks = 32;

        /// <summary>单个必需检查允许声明的额外覆盖来源数量；加上检查自身来源即覆盖集合。</summary>
        internal const int MaxRunCheckSources = 4;

        /// <summary>单个 run.begin 允许声明的源覆盖要求条数。</summary>
        internal const int MaxRunSourceRequirements = 16;

        /// <summary>单个 run.begin 允许声明的结束条件数量。</summary>
        internal const int MaxRunEndConditions = 8;

        /// <summary>run.begin 允许声明的窗口时长上界（毫秒）；clock 结束条件必须落在该范围内。</summary>
        internal const long MaxRunWindowMs = 3600000L;

        /// <summary>单个必需检查最多保留的匹配证据条数；超出只统计数量。</summary>
        internal const int MaxRunEvidencePerCheck = 8;

        /// <summary>单个 run 窗口最多保留的覆盖缺口条数。</summary>
        internal const int MaxRunCoverageGaps = 32;

        /// <summary>单个 run 窗口允许划分的配置段数量；超过即拒绝分段并如实报告 indeterminate。</summary>
        internal const int MaxRunSegments = 16;

        /// <summary>单条保留证据记录允许的估算字节数（含期望值、实值与证据引用投影）。</summary>
        internal const int MaxRunEvidenceRecordBytes = 12 * 1024;

        /// <summary>
        /// 单个 run 窗口详细证据的字节上界。
        /// 由记录数与单条上限推导，必须不高于 MaxDetailedRunBytesGlobal，否则一次 begin 就可能挤满全局账本。
        /// </summary>
        internal const long MaxRunDetailBytesPerWindow = (long)MaxRunRequiredChecks * MaxRunEvidencePerCheck * MaxRunEvidenceRecordBytes;

        /// <summary>debug.run.wait 允许的有界等待上限（毫秒）。</summary>
        internal const int RunWaitMaxMs = 30000;

        /// <summary>debug.run.list 默认页大小。</summary>
        internal const int RunListDefaultLimit = 8;

        /// <summary>debug.run.list 允许的最大页大小；摘要总数上限低于该值，因此任何保留窗口都能在一页内读完。</summary>
        internal const int RunListMaxLimit = 16;

        // ---- 证据窗口比较 run.compare ----

        /// <summary>run.compare 默认的两侧差异上下文条数；0 表示只要差异事件本身。</summary>
        internal const int RunCompareDefaultContext = 1;

        /// <summary>run.compare 允许请求的两侧差异上下文条数上限。</summary>
        internal const int MaxRunCompareContext = 4;

        /// <summary>单次 run.compare 结果里允许保留的事件视图总量（差异事件加两侧上下文）。</summary>
        internal const int MaxRunCompareEvents = 32;

        /// <summary>单条事件视图里键值与比较值的文本字节上限；超出的文本会被截断并报告。</summary>
        internal const int MaxRunCompareValueBytes = 2 * 1024;

        /// <summary>对齐键与比较值路径允许的最大段数；同时限制嵌套差异下降的深度。</summary>
        internal const int MaxRunComparePathSegments = 8;

        /// <summary>单次 run.compare 允许报告的条件差异条数上限。</summary>
        internal const int MaxRunCompareConditionDifferences = 8;

        // ---- recipe 导出/导入 ----

        /// <summary>每个 session 同时最多保留的不可变导出快照数；旧的同 session 快照被替换并使旧 cursor 失效。</summary>
        internal const int RecipeSnapshotsPerSession = 1;

        /// <summary>单个 recipe 快照的字节上限；完整导出分页不得突破此值。</summary>
        internal const int MaxRecipeBytes = 8 * 1024 * 1024;

        /// <summary>一页 recipe 的默认字节数。</summary>
        internal const int RecipePageDefaultBytes = 512 * 1024;

        /// <summary>一页 recipe 的最大字节数；仍小于单个快照上限。</summary>
        internal const int RecipePageMaxBytes = 2 * 1024 * 1024;

        /// <summary>recipe cursor 生命周期的稳定文本，供 describe 复用同一来源。</summary>
        internal const string RecipeCursorLifetimeText = "valid only while its snapshot publication is the session's current export";

        // ---- 监听式触发、投递与中央调度 ----

        /// <summary>单个 session 计划中与待执行的 listener 注入和输入合计上限；两者共用同一账本。</summary>
        internal const int MaxPlannedInjectionsPerSession = 1024;

        /// <summary>单个 listener 允许声明的命中条件数上限。</summary>
        internal const int MaxListenerConditions = 8;

        /// <summary>有限命中触发允许声明的最大命中次数；1 表示 one-shot。</summary>
        internal const int MaxListenerHits = 1000000;

        /// <summary>host signal 同步派发的递归深度上限；超过即拒绝派发，防止递归链失控。</summary>
        internal const int MaxSignalRecursionDepth = 16;

        /// <summary>中央 scheduler 每个 tick 允许取得执行权的投递数量上限；预算只关闭未来条目。</summary>
        internal const int MaxDeliveriesPerTick = 8;

        /// <summary>中央 scheduler 保留的每-tick 派发耗时样本数量。</summary>
        internal const int SchedulerTickSamples = 32;

        /// <summary>单个 tick 的派发耗时阈值（毫秒）；超过即计为 overrun，属于事后保护而不是抢占。</summary>
        internal const int SchedulerTickOverrunMs = 4;

        /// <summary>日志捕获允许声明的最小等级取值；用于校验调试日志监听配置。</summary>
        internal const string LogCaptureLevelLog = "log";

        // ---- 可选设备后端与输入下发 ----

        /// <summary>单个 session 允许同时拥有的虚拟设备数量：一个键设备与一个指针设备。</summary>
        internal const int MaxInputDevicesPerSession = 2;

        /// <summary>单次键下发允许声明的按键数量。</summary>
        internal const int MaxInputKeysPerDispatch = 8;

        /// <summary>单次指针下发允许声明的按键（左、右、中）数量。</summary>
        internal const int MaxInputPointerButtons = 3;

        /// <summary>单个 session 保留的输入记录数量上限；超出时只能淘汰已确认释放的记录。</summary>
        internal const int MaxInputHoldsPerSession = 16;

        /// <summary>一次按下允许声明的结束边界时长上界（毫秒）；边界必须由调用方显式声明，没有默认值。</summary>
        internal const long MaxInputHoldMs = 60000L;

        /// <summary>等待主线程补完的释放请求数量上限；每个 session 至多一条，因此以 session 上限为界。</summary>
        internal const int MaxInputPendingReleases = MaxSessions;

        /// <summary>输入身份、设备名与原因文本的 UTF-8 字节上限。</summary>
        internal const int MaxInputNameBytes = 128;

        /// <summary>设备后端探测失败时报告的最大缺失成员条数。</summary>
        internal const int MaxInputMissingApiMembers = 8;

        // ---- 命中线程与帧 ----

        /// <summary>允许保留的观察线程记录数（全局）；记录只描述仍在进行的命中帧。</summary>
        internal const int MaxThreadRecords = 256;

        /// <summary>单个线程同时允许的活动帧数；超出时该次调用保持原方法语义并记录覆盖缺口。</summary>
        internal const int MaxFramesPerThread = 64;

        /// <summary>等待主线程补充的命中身份条目上限；超出即丢弃，绝不阻塞线程上的业务调用。</summary>
        internal const int MaxPendingSupplements = 64;

        // ---- 线程观察读取 ----

        /// <summary>debug.thread.list 默认页大小。</summary>
        internal const int ThreadListDefaultLimit = 50;

        /// <summary>debug.thread.list 允许的最大页大小；观察线程记录总数上限低于该值，因此任何一次观察都能在一页内读完。</summary>
        internal const int ThreadListMaxLimit = 200;

        // ---- 值读取与写入 ----

        /// <summary>字段名与数组下标构成的有界路径长度上限（分段数）。</summary>
        internal const int MaxValuePathLength = 16;

        /// <summary>同一 session 允许持有的对象句柄数量上限；每个句柄分别是弱引用或强引用。</summary>
        internal const int MaxHandlesPerSession = 256;

        /// <summary>整个 Unity 进程允许持有的对象句柄数量上限；多个 session 不得绕过该全局限制。</summary>
        internal const int MaxHandlesGlobal = 1024;

        /// <summary>
        /// 命名的项目逻辑时钟条数上限；读取时按名字去重覆盖。
        /// 该上限不是任意值：每次 debug.value.read 都会把当前注册表随结果一起返回，而框架会在同一请求内
        /// 有预算地序列化该结果 ValueReadSerializations 次，因此条数上限必须让最坏形态的读取仍落在默认请求预算内。
        /// 推导方式与 EventsMaxLimit 相同：默认预算的四分之三减去固定 member 与余量，再按一次序列化的时钟 member 数除。
        /// </summary>
        internal const int MaxNamedLogicalClocks = (int)((ValueReadBudgetShareGetters
            - ValueReadBudgetReserveGetters
            - ValueReadSerializations * (long)(ValueReadFixedMembers + ValueReadWorstExtraMembers))
            / (ValueReadSerializations * (long)ValueReadLogicalClockMembers));

        // ---- 暂停现场与 pause mailbox ----

        /// <summary>整个 Unity 进程同时允许存在的暂停现场上限；超出时命中保持原方法并记录 pauseSkipped 与覆盖缺口。</summary>
        internal const int MaxPauseSites = 16;

        /// <summary>单个暂停现场可读取的调用栈帧数上限。</summary>
        internal const int MaxPauseStackFrames = 64;

        /// <summary>单个暂停现场允许排队的等待请求数上限（不含正在执行的那一个操作）。</summary>
        internal const int MaxPauseMailboxWaiting = 8;

        /// <summary>单个暂停现场允许同时执行的操作数量；resume 信号不占用该额度，因此永不排在等待请求之后。</summary>
        internal const int MaxPauseMailboxExecuting = 1;

        /// <summary>暂停线程服务自身 mailbox 的轮询间隔（毫秒）；同时是有限超时的检查粒度。</summary>
        internal const int PauseServicePollMs = 4;

        /// <summary>已结束现场的保留条数；只用于把过期 pauseId 的后续调用映射为 E_DEBUG_PAUSE_STALE，不复活现场。</summary>
        internal const int MaxRetainedPauseSites = 32;

        /// <summary>单次 pauseTimeout 允许的 durationMs 上界。</summary>
        internal const long MaxPauseDurationMs = 3600000L;

        /// <summary>debug.pause.list 默认页大小。</summary>
        internal const int PauseListDefaultLimit = 32;

        /// <summary>debug.pause.list 允许的最大页大小；同时存在的活动现场总数低于该值，因此一页即可读完。</summary>
        internal const int PauseListMaxLimit = 64;

        /// <summary>debug.pause.wait 允许的有界等待上限（毫秒）。</summary>
        internal const int PauseWaitMaxMs = 30000;

        /// <summary>单次现场操作在暂停线程上的执行上限（毫秒）；到期只报告 pending，不抢占用户同步代码。</summary>
        internal const int PauseOperationTimeoutMs = 10000;

        /// <summary>debug.pause.inspect 单次允许读取的字段数上限。</summary>
        internal const int MaxPauseInspectFields = 32;

        /// <summary>现场操作获取共享编译所有权的有界等待上限（毫秒）；取不到时返回忙，现场继续保留。</summary>
        internal const int PauseCompilerAcquireMs = 2000;

        /// <summary>debug.pause.resume 等待现场真正离开 paused 的上限（毫秒）；到期如实报告未释放。</summary>
        internal const int PauseMaxWaitForReleaseMs = 5000;

        /// <summary>session 清理在报告 pending 之前等待现场真正退出的固定窗口（毫秒）。</summary>
        internal const int PauseCleanupWaitMs = 250;

        /// <summary>不重复登记的覆盖缺口与抑制原因种类上限。</summary>
        internal const int MaxPauseCoverageReasons = 16;

        /// <summary>主线程暂停控制通道的轮询间隔（毫秒）；与暂停线程自己的 mailbox 轮询同粒度。</summary>
        internal const int PauseControlPollMs = 4;

        /// <summary>主线程暂停控制通道单次 off-main 等待的硬上限（毫秒）；仍需满足请求自身的剩余预算。</summary>
        internal const int PauseControlMaxWaitMs = 30000;

        /// <summary>
        /// 控制通道归因文本的 UTF-8 字节上限（异常类型名加异常消息）。
        /// 未预期异常必须能在测试输出或 CLI 回复里直接定位，因此保存有界消息而不是只保存类型名。
        /// </summary>
        internal const int MaxDiagnosticTextBytes = 512;

        // ---- 生命周期 ----

        /// <summary>每个 domain generation 总计的恢复预算，不按 hook 续期。</summary>
        internal const int RecoveryBudgetSeconds = 120;

        /// <summary>回调正常返回但超过该毫秒数时记录 overrun 并使定义 faulted（事后保护，非抢占式超时）。</summary>
        internal const int CallbackOverrunMs = 10;

        /// <summary>跨 Domain Reload 的恢复抑制集合条目上限。</summary>
        internal const int MaxSuppressionEntries = 128;

        /// <summary>info 返回的 foreign patch owner 摘要条目上限。</summary>
        internal const int MaxForeignOwnerEntries = 32;

        // ---- 读取型调用的单次读取预算 ----

        /// <summary>
        /// 一次事件页可能需要的最小 read budget。
        /// 同一 terminal 至少被有预算地序列化 EventPageSerializations 次，因此按该倍数计费再加固定余量。
        /// </summary>
        internal static long EventPageGetters(int events)
        {
            return EventPageSerializations * (long)EventItemMembers * Math.Max(1, events) + 256L;
        }

        /// <summary>一次定义页可能需要的最小 read budget。</summary>
        internal static long ListPageGetters(int items)
        {
            return 2L * ListItemMembers * Math.Max(1, items) + 256L;
        }

        /// <summary>
        /// 同一 debug.value.read 结果在框架内需要的有预算序列化次数：
        /// 结果 DTO 序列化、完成校验重读与 terminal envelope 序列化各自读取一次同一批 member。
        /// </summary>
        internal const int ValueReadSerializations = 3;

        /// <summary>DebugValueReadResult 的固定 member 数量（不含句柄视图与命名时钟条目）。</summary>
        internal const int ValueReadResultMembers = 25;

        /// <summary>DebugClockView 的 member 数量；每次读取固定出现 ValueReadClocksWithoutPublish 条。</summary>
        internal const int ValueReadClockMembers = 14;

        /// <summary>未发布本次逻辑时钟时固定出现的时钟条数：单调、Editor tick、Unity frame。</summary>
        internal const int ValueReadClocksWithoutPublish = 3;

        /// <summary>发布了本次逻辑时钟时的时钟条数上限。</summary>
        internal const int ValueReadClocksMax = 4;

        /// <summary>DebugHandleView 的 member 数量；createHandle 非 none 时出现。</summary>
        internal const int ValueReadHandleMembers = 11;

        /// <summary>DebugValueHandleBudgetView 的 member 数量；每次读取都出现。</summary>
        internal const int ValueReadHandleBudgetMembers = 7;

        /// <summary>DebugLogicalClockView 的 member 数量；每条保留的命名逻辑时钟都会随结果返回。</summary>
        internal const int ValueReadLogicalClockMembers = 13;

        /// <summary>值读取结果按每次序列化都读取的固定 member 数：结果 DTO、三条固定时钟与句柄预算视图。</summary>
        internal const int ValueReadFixedMembers = ValueReadResultMembers
            + ValueReadClocksWithoutPublish * ValueReadClockMembers
            + ValueReadHandleBudgetMembers;

        /// <summary>值读取结果之外的固定余量，覆盖选择器解析等少量真实 getter 读取。</summary>
        internal const long ValueReadBudgetReserveGetters = 256L;

        /// <summary>
        /// 值读取结果允许占用的默认请求预算份额。
        /// 命名时钟注册表上限与推导预算都由它决定，因此一次读取不会占满整个请求预算。
        /// </summary>
        internal const long ValueReadBudgetShareGetters = UnityCliRequestBudget.DefaultMaxGetters * 3L / 4L;

        /// <summary>最坏形态下每次序列化额外读取的 member：句柄视图与本次发布的逻辑时钟。</summary>
        internal const int ValueReadWorstExtraMembers = ValueReadHandleMembers
            + (ValueReadClocksMax - ValueReadClocksWithoutPublish) * ValueReadClockMembers;

        /// <summary>
        /// 一次 debug.value.read 结果在框架内可能需要的最小 read budget。
        /// 按最坏形态推导：固定 member、句柄视图、发布的逻辑时钟与整个命名时钟注册表，再乘框架的序列化次数。
        /// 推导值必须不高于 ValueReadBudgetShareGetters，因此也必然低于默认请求预算。
        /// </summary>
        internal static long ValueReadGetters()
        {
            var membersPerPass = ValueReadFixedMembers + ValueReadWorstExtraMembers
                + (long)MaxNamedLogicalClocks * ValueReadLogicalClockMembers;
            return ValueReadSerializations * membersPerPass + ValueReadBudgetReserveGetters;
        }

        /// <summary>DebugValueWriteResult 的固定 member 数量（不含句柄、句柄预算、条件恢复与固定时钟条目）。</summary>
        internal const int ValueWriteResultMembers = 26;

        /// <summary>DebugValueRestoreView 的 member 数量；registerRestore 时出现。</summary>
        internal const int ValueWriteRestoreMembers = 12;

        /// <summary>写入结果固定出现的时钟条数：单调、Editor tick 与 Unity frame；写入不发布命名逻辑时钟。</summary>
        internal const int ValueWriteClocks = ValueReadClocksWithoutPublish;

        /// <summary>值写入结果按每次序列化都读取的固定 member 数：结果 DTO、三条固定时钟与句柄预算视图。</summary>
        internal const int ValueWriteFixedMembers = ValueWriteResultMembers
            + ValueWriteClocks * ValueReadClockMembers
            + ValueReadHandleBudgetMembers;

        /// <summary>最坏形态下每次序列化额外读取的 member：句柄视图与条件恢复视图。</summary>
        internal const int ValueWriteWorstExtraMembers = ValueReadHandleMembers + ValueWriteRestoreMembers;

        /// <summary>值写入结果在框架内需要的有预算序列化次数；写入与读取共用同一组 framework 序列化阶段。</summary>
        internal const int ValueWriteSerializations = ValueReadSerializations;

        /// <summary>
        /// 一次 debug.value.write 结果在框架内需要的最小 read budget。
        /// 按最坏形态推导：结果 DTO、三条固定时钟、句柄视图、句柄预算与条件恢复视图，再乘框架的序列化次数。
        /// 写入结果不含读取那样的命名逻辑时钟注册表，因此本推导值是写入结果形状的完整上界，
        /// 且必然远低于默认请求预算；定向测试据此断言写入路径的真实 getter 用量。
        /// 写入因此不需要收紧自己的账本：收紧反而会把余量降到推导值以下。
        /// </summary>
        internal static long ValueWriteGetters()
        {
            var membersPerPass = ValueWriteFixedMembers + ValueWriteWorstExtraMembers;
            return ValueWriteSerializations * membersPerPass + ValueReadBudgetReserveGetters;
        }

        /// <summary>session 域上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string SessionSummary()
        {
            return "Limits: " + MaxSessions.ToString(CultureInfo.InvariantCulture) + " session definitions, "
                + MaxActiveResourcesPerSession.ToString(CultureInfo.InvariantCulture) + " active resources per session, "
                + (MaxSessionStateBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB initial state per session, one "
                + (MaxSessionStoreBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB store file for the whole debug domain.";
        }

        /// <summary>线程观察读取上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string ThreadSummary()
        {
            return "Limits: " + MaxThreadRecords.ToString(CultureInfo.InvariantCulture) + " observed thread records ("
                + MaxFramesPerThread.ToString(CultureInfo.InvariantCulture) + " active frames per thread), "
                + MaxEventsPerSession.ToString(CultureInfo.InvariantCulture) + " retained debug events per Editor session, page "
                + ThreadListDefaultLimit.ToString(CultureInfo.InvariantCulture) + " by default and "
                + ThreadListMaxLimit.ToString(CultureInfo.InvariantCulture) + " at most.";
        }

        /// <summary>值读取与写入上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string ValueSummary()
        {
            return "Limits: " + MaxValuePathLength.ToString(CultureInfo.InvariantCulture) + " path segments (field names and array indexes), "
                + MaxProjectionDepth.ToString(CultureInfo.InvariantCulture) + " projection depth, "
                + MaxProjectionItems.ToString(CultureInfo.InvariantCulture) + " projection items per level, "
                + (MaxProjectionStringBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per projected string, "
                + (MaxStateBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per submitted JSON value, "
                + MaxHandlesPerSession.ToString(CultureInfo.InvariantCulture) + " handles per session and "
                + MaxHandlesGlobal.ToString(CultureInfo.InvariantCulture) + " handles in the whole process, "
                + MaxNamedLogicalClocks.ToString(CultureInfo.InvariantCulture) + " named project logical clocks per Editor session.";
        }

        /// <summary>监听式触发与中央调度上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string TriggerSummary()
        {
            return "Limits: " + MaxListenerConditions.ToString(CultureInfo.InvariantCulture) + " listener conditions, "
                + MaxListenerHits.ToString(CultureInfo.InvariantCulture) + " hits per finite listener (1 is one-shot), "
                + MaxSignalRecursionDepth.ToString(CultureInfo.InvariantCulture) + " host signal recursion depth, "
                + MaxPlannedInjectionsPerSession.ToString(CultureInfo.InvariantCulture) + " planned injections and inputs per session, "
                + MaxDeliveriesPerTick.ToString(CultureInfo.InvariantCulture) + " deliveries claimed per central tick and "
                + SchedulerTickSamples.ToString(CultureInfo.InvariantCulture) + " retained tick cost samples ("
                + SchedulerTickOverrunMs.ToString(CultureInfo.InvariantCulture) + " ms overrun threshold).";
        }

        /// <summary>program 域上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string ProgramSummary()
        {
            return "Limits: " + MaxProgramsPerSession.ToString(CultureInfo.InvariantCulture) + " programs per session and "
                + MaxProgramsGlobal.ToString(CultureInfo.InvariantCulture) + " programs in the whole process, "
                + MaxActiveProgramRunsGlobal.ToString(CultureInfo.InvariantCulture) + " active runs in the whole process, "
                + (MaxProgramSourceBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per program compile unit, "
                + MaxProgramEntries.ToString(CultureInfo.InvariantCulture) + " entries and "
                + MaxProgramBindings.ToString(CultureInfo.InvariantCulture) + " bindings per program, "
                + (MaxProgramStateBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB revision state, "
                + (MaxProgramArgsBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB run arguments, "
                + MaxProgramRunMaxDurationMs.ToString(CultureInfo.InvariantCulture) + " ms maximum run duration, "
                + MaxProgramDeliveries.ToString(CultureInfo.InvariantCulture) + " queued cross-thread deliveries, "
                + MaxDeliveriesPerTick.ToString(CultureInfo.InvariantCulture) + " deliveries claimed per tick, "
                + MaxSignalRecursionDepth.ToString(CultureInfo.InvariantCulture) + " host signal recursion depth, "
                + ProgramWaitMaxMs.ToString(CultureInfo.InvariantCulture) + " ms bounded wait.";
        }

        /// <summary>run 证据窗口上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string RunSummary()
        {
            return "Limits: " + MaxRunSummariesPerSession.ToString(CultureInfo.InvariantCulture) + " retained run summaries per session and "
                + MaxDetailedRunWindowsPerSession.ToString(CultureInfo.InvariantCulture) + " windows that keep detailed evidence, "
                + (MaxDetailedRunBytesGlobal / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB detailed evidence in the whole process, "
                + MaxRunRequiredChecks.ToString(CultureInfo.InvariantCulture) + " required checks and "
                + MaxRunEndConditions.ToString(CultureInfo.InvariantCulture) + " end conditions per window, "
                + MaxRunEvidencePerCheck.ToString(CultureInfo.InvariantCulture) + " retained evidence records and "
                + MaxRunCoverageGaps.ToString(CultureInfo.InvariantCulture) + " coverage gaps per window, "
                + MaxRunSegments.ToString(CultureInfo.InvariantCulture) + " configuration segments, "
                + MaxRunWindowMs.ToString(CultureInfo.InvariantCulture) + " ms maximum declared window, "
                + RunWaitMaxMs.ToString(CultureInfo.InvariantCulture) + " ms bounded wait, page "
                + RunListDefaultLimit.ToString(CultureInfo.InvariantCulture) + " by default and "
                + RunListMaxLimit.ToString(CultureInfo.InvariantCulture) + " at most.";
        }

        /// <summary>run.compare 上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string RunCompareSummary()
        {
            return "Limits: " + MaxRunCompareContext.ToString(CultureInfo.InvariantCulture) + " difference context events per side ("
                + RunCompareDefaultContext.ToString(CultureInfo.InvariantCulture) + " by default), "
                + MaxRunCompareEvents.ToString(CultureInfo.InvariantCulture) + " retained event views, "
                + MaxRunComparePathSegments.ToString(CultureInfo.InvariantCulture) + " path segments for the alignment key and the compared value, "
                + (MaxRunCompareValueBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per compared value text, "
                + MaxRunCompareConditionDifferences.ToString(CultureInfo.InvariantCulture) + " reported condition differences.";
        }

        /// <summary>可选设备后端与输入下发上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string InputSummary()
        {
            return "Limits: " + MaxInputDevicesPerSession.ToString(CultureInfo.InvariantCulture) + " virtual devices per session (one key device and one pointer device), "
                + MaxInputKeysPerDispatch.ToString(CultureInfo.InvariantCulture) + " keys and "
                + MaxInputPointerButtons.ToString(CultureInfo.InvariantCulture) + " pointer buttons per dispatch, "
                + MaxInputHoldsPerSession.ToString(CultureInfo.InvariantCulture) + " retained input records per session, "
                + MaxInputHoldMs.ToString(CultureInfo.InvariantCulture) + " ms maximum declared hold boundary, "
                + MaxInputPendingReleases.ToString(CultureInfo.InvariantCulture) + " pending main-thread release requests, and the shared "
                + MaxPlannedInjectionsPerSession.ToString(CultureInfo.InvariantCulture) + " planned injections and inputs per session.";
        }

        /// <summary>暂停现场与 mailbox 上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string PauseSummary()
        {
            return "Limits: " + MaxPauseSites.ToString(CultureInfo.InvariantCulture) + " simultaneous pause sites in the whole process, "
                + MaxPauseStackFrames.ToString(CultureInfo.InvariantCulture) + " frames per read stack, "
                + MaxPauseMailboxWaiting.ToString(CultureInfo.InvariantCulture) + " waiting requests and "
                + MaxPauseMailboxExecuting.ToString(CultureInfo.InvariantCulture) + " executing operation per site, "
                + MaxPauseDurationMs.ToString(CultureInfo.InvariantCulture) + " ms maximum bounded pause timeout, "
                + PauseWaitMaxMs.ToString(CultureInfo.InvariantCulture) + " ms bounded wait, "
                + PauseOperationTimeoutMs.ToString(CultureInfo.InvariantCulture) + " ms per on-site operation, "
                + PauseCompilerAcquireMs.ToString(CultureInfo.InvariantCulture) + " ms to acquire the single compiler, "
                + MaxPauseInspectFields.ToString(CultureInfo.InvariantCulture) + " requested fields per inspect, "
                + MaxRetainedPauseSites.ToString(CultureInfo.InvariantCulture) + " retained finished pause ids, "
                + PauseControlPollMs.ToString(CultureInfo.InvariantCulture) + " ms control-channel poll and "
                + PauseControlMaxWaitMs.ToString(CultureInfo.InvariantCulture) + " ms hard bound per off-main control wait while the main thread is paused.";
        }

        /// <summary>recipe 上限的稳定摘要，供 describe 复用同一来源。</summary>
        internal static string RecipeSummary()
        {
            return "Limits: " + RecipeSnapshotsPerSession.ToString(CultureInfo.InvariantCulture) + " retained export snapshot per session, "
                + (MaxRecipeBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB per recipe snapshot (paging never exceeds it), page "
                + (RecipePageDefaultBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB by default and "
                + (RecipePageMaxBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB at most, cursor "
                + RecipeCursorLifetimeText + ".";
        }

        /// <summary>供 describe 复用的稳定上限摘要，公开文本与实现共用同一来源。</summary>
        internal static string Summary()
        {
            return "Limits: " + MaxDefinitionsPerSession.ToString(CultureInfo.InvariantCulture) + " definitions ("
                + MaxDefinitionsPerTarget.ToString(CultureInfo.InvariantCulture) + " per target), "
                + (MaxCallbackSourceBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB per callback source, "
                + MaxCompiledArtifacts.ToString(CultureInfo.InvariantCulture) + " loaded compile artifacts and "
                + (MaxCompiledArtifactBytes / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB per domain across all compile shapes (code execution, hook callbacks, programs, pause evaluate), "
                + MaxEventsPerSession.ToString(CultureInfo.InvariantCulture) + " events / "
                + (MaxEventBytesPerSession / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB per session ("
                + (MaxEventBytesGlobal / (1024 * 1024)).ToString(CultureInfo.InvariantCulture) + " MiB global), "
                + (MaxStateBytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB state per hook, "
                + MaxThreadRecords.ToString(CultureInfo.InvariantCulture) + " observed threads and "
                + MaxFramesPerThread.ToString(CultureInfo.InvariantCulture) + " active frames per thread, "
                + MaxPendingSupplements.ToString(CultureInfo.InvariantCulture) + " pending main-thread supplements, "
                + RecoveryBudgetSeconds.ToString(CultureInfo.InvariantCulture) + " s restore budget per domain generation.";
        }
    }
}
#endif
