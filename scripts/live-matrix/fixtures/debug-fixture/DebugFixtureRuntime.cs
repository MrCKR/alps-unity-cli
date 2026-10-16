// Dev 专用运行时调试夹具（T-18）。它是普通 Unity 项目脚本，不属于产品 Package，
// 不带 [CliCommand]，因此不会出现在公开 Tool catalog 中。
//
// 模型（models）：
//   UnityCliDebugFixture.DebugCounterModel —— 普通托管业务状态模型（非 ScriptableObject、非 MonoBehaviour），
//   由一个静态 holder 持有。字段：Value、TargetValue、AppliedIncrements、LogicalSequence、LastUpdateIdentity。
//   逻辑时钟（project logical clock）：DebugCounterModel.LogicalSequence 由模型自己发布，
//   只在业务变更内自增，与 Time.frameCount 无关。
//   权威实例是 Holder.Model；表现/显示副本是 Holder.Shadow（DebugShadowView），只能在主线程刷新，允许合法过期。
//   两个独立真实 managed 线程（System.Threading.Thread）：WorkerA（identity "worker-a"）与 WorkerB（identity "worker-b"），
//   各自按固定周期调用同一个 DebugCounterModel.Update(string,int) 更新入口，用 identity 与线程 id 区分来源。
//
// 入口点（fully qualified）：
//   UnityCliDebugFixture.DebugFixture.BindMainThread()          —— 绑定主线程，返回主线程 id
//   UnityCliDebugFixture.DebugFixture.StartWorkers()            —— 启动两个 worker（要求已绑定主线程，幂等）
//   UnityCliDebugFixture.DebugFixture.StopWorkers()             —— 停止两个 worker（幂等，等待退出）
//   UnityCliDebugFixture.DebugFixture.RefreshShadow()           —— 主线程刷新显示副本（非主线程抛异常）
//   UnityCliDebugFixture.DebugFixture.ResetToInitialState()     —— 恢复入口，回到文档化初态
//   UnityCliDebugFixture.DebugFixture.ReadState()               —— 读回入口，返回 DebugFixtureState 快照
//   UnityCliDebugFixture.DebugFixture.CheckExpectedValue(out int, out int) —— 命名期望检查
//   UnityCliDebugFixture.DebugFixture.ApplyControlCommandSequence()   —— 控制序列（不变量保持）
//   UnityCliDebugFixture.DebugFixture.ApplyFailingCommandSequence()   —— 失败序列（不变量被违反）
//   UnityCliDebugFixture.DebugCounterModel.RecurseDepth(int)     —— 有界业务递归入口（0..8，每层一次真实嵌套调用）
//                                                                  用于区分“业务递归（每层各自成帧）”与“回调自我重入（被抑制）”
//   UnityCliDebugFixture.DebugFixtureMutableState               —— 可写静态状态（MutableCounter / MutableReference / ResetMutableState）
//                                                                  供字段写入、条件恢复与弱句柄失效路径验证
//   UnityCliDebugFixture.DebugCounterModel.Update(string,int)   —— 业务更新入口（hook 目标）
//   UnityCliDebugFixture.DebugCounterModel.SetTargetValue(int)  —— 业务命令入口 1
//   UnityCliDebugFixture.DebugCounterModel.IncrementBy(int)     —— 业务命令入口 2
//   UnityCliDebugFixture.LegacyInputAdapter.ReadMoveAxis()      —— 项目输入 wrapper（Legacy 风格轴值）
//   UnityCliDebugFixture.LegacyInputAdapter.PollAndApply(DebugCounterModel) —— 项目输入入口
//   UnityCliDebugFixture.LegacyInputAdapter.DeliverDeviceEvent(string,int)  —— 设备风格事件投递
//   UnityCliDebugFixture.DebugFixtureEditor.Start/Stop/RefreshShadow        —— Editor 侧装配入口
//
// 文档化初态（ResetToInitialState 后的状态）：
//   Value=0、TargetValue=0、AppliedIncrements=0、LogicalSequence=0；显示副本等于初态且刷新计数归零；
//   worker 运行状态不变（worker 计数归零需重建进程级对象，故 ResetToInitialState 不重置 worker 计数）；
//   输入轴来源=0、设备事件计数=0、最后设备事件码为空。
//
// 命名不变量：DebugCounterModel.InvariantName = "applied-increments-equal-target"，
//   即命令序列结束后 AppliedIncrements 必须等于 TargetValue。该不变量只由命令入口变更，worker 线程不会影响它。
//
// 确定性失败序列（先 StopWorkers 或直接忽略 worker，因为 worker 不触碰这两个字段）：
//   ApplyFailingCommandSequence() 等价于
//   ResetToInitialState(); SetTargetValue(3); IncrementBy(1); IncrementBy(1);
//   => AppliedIncrements=2、TargetValue=3，不变量被违反，首个反例即第二次 IncrementBy(1)（expected=3、actual=2）。
// 控制序列（不变量保持）：
//   ApplyControlCommandSequence() 等价于
//   ResetToInitialState(); SetTargetValue(3); IncrementBy(1); IncrementBy(1); IncrementBy(1);
//   => AppliedIncrements=3、TargetValue=3，CheckExpectedValue 返回 true。
//
// 不被接受的输入场景（接收 != 业务已接受）：
//   LegacyInputAdapter.DeliverDeviceEvent("key-space", 1) 只增加 DeviceEventCount 并记录 LastDeviceEventCode；
//   业务从不读取该队列，所以不改变 Value、TargetValue、AppliedIncrements、LogicalSequence。
//   只有 LegacyInputAdapter.PollAndApply(model) 会通过 ReadMoveAxis() 把轴值喂给模型。
//
// 自足性：不依赖场景内容、外部资产、随机数或 Input System package；
// 时间来源只有固定周期 Sleep 与模型发布逻辑序号，因此同一命令序列可复现。

using System;
using System.Threading;

namespace UnityCliDebugFixture
{
    /// <summary>
    /// 普通托管业务状态模型。不是 ScriptableObject，也不是 MonoBehaviour，由静态 holder 持有。
    /// 权威字段只在 lock 内变更；logicalSequence 由模型自身发布，不是 Unity frame 的映射。
    /// </summary>
    public sealed class DebugCounterModel
    {
        /// <summary>文档化初值。</summary>
        public const int InitialValue = 0;
        /// <summary>文档化初始目标值。</summary>
        public const int InitialTargetValue = 0;
        /// <summary>文档化初始已应用增量。</summary>
        public const int InitialAppliedIncrements = 0;
        /// <summary>文档化初始逻辑序号。</summary>
        public const long InitialLogicalSequence = 0L;

        /// <summary>命名不变量：命令序列结束后 AppliedIncrements 必须等于 TargetValue。</summary>
        public const string InvariantName = "applied-increments-equal-target";

        /// <summary>控制序列使用的目标值。</summary>
        public const int DocumentedControlTargetValue = 3;
        /// <summary>文档化增量步长。</summary>
        public const int DocumentedIncrementDelta = 1;
        /// <summary>控制序列的增量次数，不变量保持。</summary>
        public const int DocumentedControlIncrementCount = 3;
        /// <summary>失败序列的增量次数，不变量被违反。</summary>
        public const int DocumentedFailingIncrementCount = 2;

        private readonly object gate = new object();
        private int value = InitialValue;
        private int targetValue = InitialTargetValue;
        private int appliedIncrements = InitialAppliedIncrements;
        private long logicalSequence = InitialLogicalSequence;
        private string lastUpdateIdentity = string.Empty;
        private int lastUpdateThreadId;
        private int updateCount;
        /// <summary>有界递归入口实际进入的层数计数，供客户端独立校验递归确实发生。</summary>
        private int recursionCallCount;

        /// <summary>当前数值（权威）。</summary>
        public int Value { get { lock (gate) { return value; } } }
        /// <summary>命令设定的目标值（权威）。</summary>
        public int TargetValue { get { lock (gate) { return targetValue; } } }
        /// <summary>已应用增量累计，只由 IncrementBy 变更。</summary>
        public int AppliedIncrements { get { lock (gate) { return appliedIncrements; } } }
        /// <summary>有界递归入口累计进入的层数（含 depth=0 的叶子层）。</summary>
        public int RecursionCallCount { get { lock (gate) { return recursionCallCount; } } }
        /// <summary>项目逻辑时钟：由模型发布的业务逻辑序号，与 Time.frameCount 无关。</summary>
        public long LogicalSequence { get { lock (gate) { return logicalSequence; } } }
        /// <summary>最后一次更新入口的调用者身份（worker-a/worker-b/project-input）。</summary>
        public string LastUpdateIdentity { get { lock (gate) { return lastUpdateIdentity; } } }
        /// <summary>最后一次更新入口的 managed 线程 id。</summary>
        public int LastUpdateThreadId { get { lock (gate) { return lastUpdateThreadId; } } }
        /// <summary>更新入口被调用的总次数。</summary>
        public int UpdateCount { get { lock (gate) { return updateCount; } } }

        /// <summary>
        /// 业务更新入口：worker 线程与主线程都调用这里，identity 用于区分调用来源。
        /// 只改权威数值与逻辑序号；不触碰 AppliedIncrements/TargetValue，因此不变量不受并发影响。
        /// </summary>
        public void Update(string identity, int delta)
        {
            lock (gate)
            {
                value += delta;
                logicalSequence += 1L;
                lastUpdateIdentity = identity ?? string.Empty;
                lastUpdateThreadId = Thread.CurrentThread.ManagedThreadId;
                updateCount += 1;
            }
        }

        /// <summary>业务命令入口 1：确定性设置目标值。</summary>
        public void SetTargetValue(int target)
        {
            lock (gate)
            {
                targetValue = target;
                logicalSequence += 1L;
            }
        }

        /// <summary>业务命令入口 2：确定性累加已应用增量与数值。</summary>
        public void IncrementBy(int delta)
        {
            lock (gate)
            {
                appliedIncrements += delta;
                value += delta;
                logicalSequence += 1L;
            }
        }

        /// <summary>有界业务递归的最大允许深度；超过即报参数错误，不得无限递归。</summary>
        public const int MaxRecursionDepth = 8;

        /// <summary>
        /// 业务命令入口 3：有界递归入口。depth 从给定值递减到 0，每一层都是一次真实的嵌套业务调用，
        /// 因此测试可区分“业务递归（每层各自成帧）”与“回调自我重入（被抑制）”。
        /// 返回每层累加的逻辑递增值，便于客户端独立校验递归确实执行了 depth+1 层。
        /// </summary>
        public int RecurseDepth(int depth)
        {
            if (depth < 0 || depth > MaxRecursionDepth) throw new ArgumentOutOfRangeException(nameof(depth));
            lock (gate)
            {
                logicalSequence += 1L;
                recursionCallCount += 1;
            }
            if (depth == 0) return 1;
            return 1 + RecurseDepth(depth - 1);
        }

        /// <summary>命名期望检查：AppliedIncrements 是否等于 TargetValue。</summary>
        public bool CheckExpectedValue(out int expected, out int actual)
        {
            lock (gate)
            {
                expected = targetValue;
                actual = appliedIncrements;
                return actual == expected;
            }
        }

        /// <summary>恢复入口使用的原子复位；只重置业务字段，不改变 worker 运行状态。</summary>
        internal void ResetToInitialState()
        {
            lock (gate)
            {
                value = InitialValue;
                targetValue = InitialTargetValue;
                appliedIncrements = InitialAppliedIncrements;
                logicalSequence = InitialLogicalSequence;
                lastUpdateIdentity = string.Empty;
                lastUpdateThreadId = 0;
                updateCount = 0;
            }
        }
    }

    /// <summary>
    /// 一个独立真实 managed 线程。线程按固定周期调用 DebugCounterModel.Update，
    /// 并使用自己的 identity 区分来源。线程内只访问普通托管模型，不访问任何 Unity API。
    /// </summary>
    public sealed class DebugFixtureWorker
    {
        private readonly string identity;
        private readonly int deltaPerTick;
        private readonly int intervalMs;
        private Thread thread;
        private int updateCount;
        private int lastThreadId;
        private volatile bool running;
        /// <summary>停止等待上限（毫秒），避免停止入口无限阻塞。</summary>
        private const int StopTimeoutMs = 2000;

        /// <summary>创建 worker；构造时不创建也不启动任何线程。</summary>
        public DebugFixtureWorker(string identity, int deltaPerTick, int intervalMs)
        {
            this.identity = identity ?? string.Empty;
            this.deltaPerTick = deltaPerTick;
            this.intervalMs = intervalMs < 1 ? 1 : intervalMs;
        }

        /// <summary>worker 身份（区分同一更新入口的不同调用线程）。</summary>
        public string Identity { get { return identity; } }
        /// <summary>本 worker 已完成调用的次数。</summary>
        public int UpdateCount { get { return Volatile.Read(ref updateCount); } }
        /// <summary>最近一次运行线程的 managed 线程 id；从未启动时为 0。</summary>
        public int ThreadId { get { return Volatile.Read(ref lastThreadId); } }
        /// <summary>是否正在运行。</summary>
        public bool IsRunning { get { return running; } }

        /// <summary>幂等启动。Thread 不可重启，因此每次启动都新建一个真实线程。</summary>
        public void Start(DebugCounterModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "unitycli-debug-fixture-" + identity };
            thread.Start(model);
        }

        /// <summary>幂等停止：置位后在有限时等待内等线程退出；从 worker 自身线程调用时直接返回，不做自连接。</summary>
        public void Stop()
        {
            if (thread == null) return;
            running = false;
            var current = thread;
            if (current.ManagedThreadId == Thread.CurrentThread.ManagedThreadId) return;
            current.Join(StopTimeoutMs);
        }

        private void Loop(object state)
        {
            var model = (DebugCounterModel)state;
            Volatile.Write(ref lastThreadId, Thread.CurrentThread.ManagedThreadId);
            while (running)
            {
                model.Update(identity, deltaPerTick);
                Interlocked.Increment(ref updateCount);
                Thread.Sleep(intervalMs);
            }
            running = false;
        }
    }

    /// <summary>
    /// 表现/显示副本：只在 Unity 主线程刷新的权威快照，允许合法过期。
    /// 它用于区分“权威模型变了”和“显示副本变了”。
    /// </summary>
    public sealed class DebugShadowView
    {
        private int value = DebugCounterModel.InitialValue;
        private int targetValue = DebugCounterModel.InitialTargetValue;
        private int appliedIncrements = DebugCounterModel.InitialAppliedIncrements;
        private long logicalSequence = DebugCounterModel.InitialLogicalSequence;
        private long refreshCount;
        private int lastRefreshThreadId;

        /// <summary>副本数值。</summary>
        public int Value { get { return Volatile.Read(ref value); } }
        /// <summary>副本目标值。</summary>
        public int TargetValue { get { return Volatile.Read(ref targetValue); } }
        /// <summary>副本已应用增量。</summary>
        public int AppliedIncrements { get { return Volatile.Read(ref appliedIncrements); } }
        /// <summary>副本逻辑序号。</summary>
        public long LogicalSequence { get { return Volatile.Read(ref logicalSequence); } }
        /// <summary>副本刷新次数。</summary>
        public long RefreshCount { get { return Volatile.Read(ref refreshCount); } }
        /// <summary>最近一次刷新的 managed 线程 id；从未刷新时为 0。</summary>
        public int LastRefreshThreadId { get { return Volatile.Read(ref lastRefreshThreadId); } }

        /// <summary>
        /// 只在主线程刷新。传入的主线程 id 不匹配时抛异常，因此“非主线程刷新”是可证明的失败场景。
        /// </summary>
        public void RefreshFromAuthority(DebugCounterModel model, int mainThreadId)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            int currentThreadId = Thread.CurrentThread.ManagedThreadId;
            if (currentThreadId != mainThreadId)
                throw new InvalidOperationException(
                    "Debug shadow refresh must run on the bound Unity main thread; current thread " + currentThreadId +
                    " is not the bound main thread " + mainThreadId + ".");
            Volatile.Write(ref value, model.Value);
            Volatile.Write(ref targetValue, model.TargetValue);
            Volatile.Write(ref appliedIncrements, model.AppliedIncrements);
            Volatile.Write(ref logicalSequence, model.LogicalSequence);
            Volatile.Write(ref refreshCount, RefreshCount + 1L);
            Volatile.Write(ref lastRefreshThreadId, currentThreadId);
        }

        /// <summary>显示副本相对当前权威是否已过期。</summary>
        public bool IsStale(DebugCounterModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            return Value != model.Value
                || TargetValue != model.TargetValue
                || AppliedIncrements != model.AppliedIncrements
                || LogicalSequence != model.LogicalSequence;
        }

        /// <summary>恢复入口使用的复位。</summary>
        internal void ResetToInitialState()
        {
            Volatile.Write(ref value, DebugCounterModel.InitialValue);
            Volatile.Write(ref targetValue, DebugCounterModel.InitialTargetValue);
            Volatile.Write(ref appliedIncrements, DebugCounterModel.InitialAppliedIncrements);
            Volatile.Write(ref logicalSequence, DebugCounterModel.InitialLogicalSequence);
            Volatile.Write(ref refreshCount, 0L);
            Volatile.Write(ref lastRefreshThreadId, 0);
        }
    }

    /// <summary>
    /// 项目输入入口（Legacy 风格），不依赖 Input System package。
    /// ReadMoveAxis() 是可被调试代码拦截的 managed wrapper；PollAndApply 是业务读取输入的唯一边界。
    /// DeliverDeviceEvent 记录设备风格事件，但业务从不读取它。
    /// </summary>
    public static class LegacyInputAdapter
    {
        /// <summary>输入来源身份，写入 DebugCounterModel.LastUpdateIdentity。</summary>
        public const string InputIdentity = "project-input";

        private static readonly object gate = new object();
        private static float axisSource;
        private static int deviceEventCount;
        private static string lastDeviceEventCode = string.Empty;

        /// <summary>当前 Legacy 风格轴值来源。</summary>
        public static float AxisSource { get { lock (gate) { return axisSource; } } }
        /// <summary>已被“接收”的设备风格事件数量。</summary>
        public static int DeviceEventCount { get { lock (gate) { return deviceEventCount; } } }
        /// <summary>最后一次设备风格事件代码；业务不读取，仅作为接收证据。</summary>
        public static string LastDeviceEventCode { get { lock (gate) { return lastDeviceEventCode; } } }

        /// <summary>注入点：设置 Legacy 风格轴值来源。</summary>
        public static void SetAxisSource(float axis)
        {
            lock (gate) { axisSource = axis; }
        }

        /// <summary>可被拦截的 managed wrapper：读取当前轴值。</summary>
        public static float ReadMoveAxis()
        {
            lock (gate) { return axisSource; }
        }

        /// <summary>
        /// 设备风格事件投递：只记录“已接收”的事实，业务不读取，因此不改变模型。
        /// 用于证明“事件被接收”不等于“业务已接受输入”。
        /// </summary>
        public static void DeliverDeviceEvent(string deviceCode, int value)
        {
            lock (gate)
            {
                deviceEventCount += 1;
                lastDeviceEventCode = (deviceCode ?? string.Empty) + ":" + value;
            }
        }

        /// <summary>
        /// 项目输入入口：经 ReadMoveAxis() 读取轴值并按确定性规则折算成步进，喂给模型。
        /// 它不排空设备事件队列，所以设备事件不会改变业务状态。
        /// </summary>
        public static int PollAndApply(DebugCounterModel model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            float axis = ReadMoveAxis();
            int steps = axis >= 0.5f ? 1 : (axis <= -0.5f ? -1 : 0);
            model.Update(InputIdentity, steps);
            return steps;
        }

        /// <summary>恢复入口使用的复位。</summary>
        internal static void ResetToInitialState()
        {
            lock (gate)
            {
                axisSource = 0f;
                deviceEventCount = 0;
                lastDeviceEventCode = string.Empty;
            }
        }
    }

    /// <summary>读回 DTO：DebugFixture.ReadState() 返回的普通托管快照。</summary>
    public sealed class DebugFixtureState
    {
        /// <summary>权威数值。</summary>
        public int Value;
        /// <summary>权威目标值。</summary>
        public int TargetValue;
        /// <summary>权威已应用增量。</summary>
        public int AppliedIncrements;
        /// <summary>项目逻辑序号。</summary>
        public long LogicalSequence;
        /// <summary>最后一次更新入口身份。</summary>
        public string LastUpdateIdentity;
        /// <summary>最后一次更新入口线程 id。</summary>
        public int LastUpdateThreadId;
        /// <summary>更新入口调用总次数。</summary>
        public int UpdateCount;
        /// <summary>有界递归入口累计进入的层数。</summary>
        public int RecursionCallCount;
        /// <summary>命名不变量名称。</summary>
        public string InvariantName;
        /// <summary>不变量当前是否满足。</summary>
        public bool InvariantSatisfied;
        /// <summary>不变量期望值（TargetValue）。</summary>
        public int ExpectedValue;
        /// <summary>不变量实值（AppliedIncrements）。</summary>
        public int ActualValue;
        /// <summary>已绑定主线程 id；未绑定为 0。</summary>
        public int MainThreadId;
        /// <summary>显示副本是否已过期。</summary>
        public bool ShadowIsStale;
        /// <summary>显示副本数值。</summary>
        public int ShadowValue;
        /// <summary>显示副本目标值。</summary>
        public int ShadowTargetValue;
        /// <summary>显示副本刷新次数。</summary>
        public long ShadowRefreshCount;
        /// <summary>显示副本最近刷新线程 id。</summary>
        public int ShadowLastRefreshThreadId;
        /// <summary>worker A 身份。</summary>
        public string WorkerAIdentity;
        /// <summary>worker A 完成调用次数。</summary>
        public int WorkerAUpdateCount;
        /// <summary>worker A 运行线程 id。</summary>
        public int WorkerAThreadId;
        /// <summary>worker A 是否运行中。</summary>
        public bool WorkerARunning;
        /// <summary>worker B 身份。</summary>
        public string WorkerBIdentity;
        /// <summary>worker B 完成调用次数。</summary>
        public int WorkerBUpdateCount;
        /// <summary>worker B 运行线程 id。</summary>
        public int WorkerBThreadId;
        /// <summary>worker B 是否运行中。</summary>
        public bool WorkerBRunning;
        /// <summary>项目输入身份。</summary>
        public string InputIdentity;
        /// <summary>当前输入轴来源值。</summary>
        public float InputAxisSource;
        /// <summary>已接收的设备风格事件数量。</summary>
        public int DeviceEventCount;
        /// <summary>最后接收的设备风格事件代码。</summary>
        public string LastDeviceEventCode;
    }

    /// <summary>
    /// 夹具静态 holder：拥有普通模型、显示副本和两个 worker 线程。
    /// 不依赖场景内容或外部资产，首次访问才创建；没有 [InitializeOnLoad]，不会隐式启动线程。
    /// </summary>
    public static class DebugFixture
    {
        /// <summary>worker A 身份。</summary>
        public const string WorkerAIdentity = "worker-a";
        /// <summary>worker B 身份。</summary>
        public const string WorkerBIdentity = "worker-b";
        /// <summary>worker 周期（毫秒）。</summary>
        public const int WorkerIntervalMs = 20;
        /// <summary>worker 每周期增量。</summary>
        public const int WorkerDeltaPerTick = 1;

        private static readonly object gate = new object();
        private static readonly DebugCounterModel model = new DebugCounterModel();
        private static readonly DebugShadowView shadow = new DebugShadowView();
        private static readonly DebugFixtureWorker workerA = new DebugFixtureWorker(WorkerAIdentity, WorkerDeltaPerTick, WorkerIntervalMs);
        private static readonly DebugFixtureWorker workerB = new DebugFixtureWorker(WorkerBIdentity, WorkerDeltaPerTick, WorkerIntervalMs);
        private static int mainThreadId;

        /// <summary>权威模型实例。</summary>
        public static DebugCounterModel Model { get { return model; } }
        /// <summary>显示副本实例。</summary>
        public static DebugShadowView Shadow { get { return shadow; } }
        /// <summary>worker A 实例。</summary>
        public static DebugFixtureWorker WorkerA { get { return workerA; } }
        /// <summary>worker B 实例。</summary>
        public static DebugFixtureWorker WorkerB { get { return workerB; } }
        /// <summary>已绑定主线程 id；未绑定为 0。</summary>
        public static int MainThreadId { get { lock (gate) { return mainThreadId; } } }
        /// <summary>当前线程是否为已绑定的主线程；未绑定时恒为 false。</summary>
        public static bool IsMainThread { get { return Thread.CurrentThread.ManagedThreadId == MainThreadId; } }

        /// <summary>主线程绑定入口：必须在 Unity 主线程调用；重复调用以最后一次为准。</summary>
        public static int BindMainThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            lock (gate) { mainThreadId = current; }
            return current;
        }

        /// <summary>启动入口：要求已绑定主线程；两个 worker 幂等启动。</summary>
        public static void StartWorkers()
        {
            RequireMainThread("DebugFixture.StartWorkers");
            workerA.Start(model);
            workerB.Start(model);
        }

        /// <summary>停止入口：幂等，等待两个 worker 在其周期内退出。</summary>
        public static void StopWorkers()
        {
            workerA.Stop();
            workerB.Stop();
        }

        /// <summary>主线程刷新入口：把权威模型快照写入显示副本；非主线程抛异常。</summary>
        public static void RefreshShadow()
        {
            RequireMainThread("DebugFixture.RefreshShadow");
            shadow.RefreshFromAuthority(model, MainThreadId);
        }

        /// <summary>
        /// 恢复入口：模型、显示副本和输入来源回到文档化初态。
        /// worker 的运行状态与累计计数保持不变；需要确定性的命令序列时先调用 StopWorkers。
        /// </summary>
        public static void ResetToInitialState()
        {
            model.ResetToInitialState();
            shadow.ResetToInitialState();
            LegacyInputAdapter.ResetToInitialState();
        }

        /// <summary>命名期望检查入口：DebugCounterModel.InvariantName 对应的不变量。</summary>
        public static bool CheckExpectedValue(out int expected, out int actual)
        {
            return model.CheckExpectedValue(out expected, out actual);
        }

        /// <summary>
        /// 控制命令序列：ResetToInitialState -> SetTargetValue(3) -> IncrementBy(1) 三次。
        /// 不变量保持：ExpectedValue=3、ActualValue=3、InvariantSatisfied=true。
        /// </summary>
        public static void ApplyControlCommandSequence()
        {
            ResetToInitialState();
            model.SetTargetValue(DebugCounterModel.DocumentedControlTargetValue);
            for (int index = 0; index < DebugCounterModel.DocumentedControlIncrementCount; index += 1)
                model.IncrementBy(DebugCounterModel.DocumentedIncrementDelta);
        }

        /// <summary>
        /// 失败命令序列：ResetToInitialState -> SetTargetValue(3) -> IncrementBy(1) 两次。
        /// 不变量被违反：ExpectedValue=3、ActualValue=2，首个反例是第二次 IncrementBy 调用。
        /// </summary>
        public static void ApplyFailingCommandSequence()
        {
            ResetToInitialState();
            model.SetTargetValue(DebugCounterModel.DocumentedControlTargetValue);
            for (int index = 0; index < DebugCounterModel.DocumentedFailingIncrementCount; index += 1)
                model.IncrementBy(DebugCounterModel.DocumentedIncrementDelta);
        }

        /// <summary>读回入口：返回一次性状态快照，包含权威、显示副本、worker 与输入接收事实。</summary>
        public static DebugFixtureState ReadState()
        {
            int expected;
            int actual;
            bool satisfied = model.CheckExpectedValue(out expected, out actual);
            return new DebugFixtureState
            {
                Value = model.Value,
                TargetValue = model.TargetValue,
                AppliedIncrements = model.AppliedIncrements,
                LogicalSequence = model.LogicalSequence,
                LastUpdateIdentity = model.LastUpdateIdentity,
                LastUpdateThreadId = model.LastUpdateThreadId,
                UpdateCount = model.UpdateCount,
                RecursionCallCount = model.RecursionCallCount,
                InvariantName = DebugCounterModel.InvariantName,
                InvariantSatisfied = satisfied,
                ExpectedValue = expected,
                ActualValue = actual,
                MainThreadId = MainThreadId,
                ShadowIsStale = shadow.IsStale(model),
                ShadowValue = shadow.Value,
                ShadowTargetValue = shadow.TargetValue,
                ShadowRefreshCount = shadow.RefreshCount,
                ShadowLastRefreshThreadId = shadow.LastRefreshThreadId,
                WorkerAIdentity = workerA.Identity,
                WorkerAUpdateCount = workerA.UpdateCount,
                WorkerAThreadId = workerA.ThreadId,
                WorkerARunning = workerA.IsRunning,
                WorkerBIdentity = workerB.Identity,
                WorkerBUpdateCount = workerB.UpdateCount,
                WorkerBThreadId = workerB.ThreadId,
                WorkerBRunning = workerB.IsRunning,
                InputIdentity = LegacyInputAdapter.InputIdentity,
                InputAxisSource = LegacyInputAdapter.AxisSource,
                DeviceEventCount = LegacyInputAdapter.DeviceEventCount,
                LastDeviceEventCode = LegacyInputAdapter.LastDeviceEventCode
            };
        }

        private static void RequireMainThread(string entryPoint)
        {
            if (IsMainThread) return;
            throw new InvalidOperationException(
                entryPoint + " must run on the bound Unity main thread; call " +
                "UnityCliDebugFixture.DebugFixture.BindMainThread from the main thread first.");
        }
    }

    /// <summary>
    /// 可写静态状态夹具：普通可变静态字段，供调试设施验证字段写入与条件恢复、句柄生命周期。
    /// 与 DebugCounterModel 不同，这里的字段既不是 const 也不是 readonly，也不属于带静态构造的类型，
    /// 因此可以安全地被受管理写入，并可由客户端独立读回校验。
    /// </summary>
    public static class DebugFixtureMutableState
    {
        /// <summary>可写静态计数；初态为 0。</summary>
        public static int MutableCounter;

        /// <summary>可写静态引用槽；默认 null，可由调试代码注入一个普通托管对象以验证弱句柄失效路径。</summary>
        public static object MutableReference;

        /// <summary>恢复初态：计数归零、引用槽置空、命名时镟释放。</summary>
        public static void ResetMutableState()
        {
            MutableCounter = 0;
            MutableReference = null;
        }
    }
}
