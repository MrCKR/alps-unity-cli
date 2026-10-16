#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 标记一个 program entry 容器类型。带该标记的类型必须只声明一个无参构造，
    /// 宿主在挂载时建立一次 wrapper 实例；未标记的类型只接受 static entry，绝不实例化用户代码。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class UnityCliDebugProgramWrapperAttribute : Attribute
    {
    }

    /// <summary>step 返回的宿主等待计划种类；程序不能自行注册 update、timer 或请求生命周期。</summary>
    public static class UnityCliDebugStepWaitKinds
    {
        public const string Frame = "frame";
        public const string Time = "time";
        public const string Signal = "signal";
        public const string Condition = "condition";

        public static bool IsDefined(string value)
        {
            return string.Equals(value, Frame, StringComparison.Ordinal)
                || string.Equals(value, Time, StringComparison.Ordinal)
                || string.Equals(value, Signal, StringComparison.Ordinal)
                || string.Equals(value, Condition, StringComparison.Ordinal);
        }
    }

    /// <summary>框架拥有的等待计划；由中央 scheduler 推进，step 只描述“等什么”。</summary>
    public sealed class UnityCliDebugStepWait
    {
        private UnityCliDebugStepWait() { }

        public string Kind { get; private set; } = UnityCliDebugStepWaitKinds.Frame;
        public long Frames { get; private set; } = 1L;
        public double DelayMilliseconds { get; private set; }
        public string Signal { get; private set; } = string.Empty;
        public string Condition { get; private set; } = string.Empty;
        public double TimeoutMilliseconds { get; private set; }

        public static UnityCliDebugStepWait NextFrame(long frames = 1L)
        {
            return new UnityCliDebugStepWait { Kind = UnityCliDebugStepWaitKinds.Frame, Frames = frames < 1L ? 1L : frames };
        }

        public static UnityCliDebugStepWait After(double milliseconds)
        {
            return new UnityCliDebugStepWait { Kind = UnityCliDebugStepWaitKinds.Time, DelayMilliseconds = milliseconds < 0d ? 0d : milliseconds };
        }

        public static UnityCliDebugStepWait UntilSignal(string signal, double timeoutMilliseconds)
        {
            return new UnityCliDebugStepWait { Kind = UnityCliDebugStepWaitKinds.Signal, Signal = signal ?? string.Empty, TimeoutMilliseconds = timeoutMilliseconds };
        }

        public static UnityCliDebugStepWait UntilCondition(string condition, double timeoutMilliseconds)
        {
            return new UnityCliDebugStepWait { Kind = UnityCliDebugStepWaitKinds.Condition, Condition = condition ?? string.Empty, TimeoutMilliseconds = timeoutMilliseconds };
        }

        internal string Describe()
        {
            switch (Kind)
            {
                case UnityCliDebugStepWaitKinds.Frame: return "frame x" + Frames.ToString(CultureInfo.InvariantCulture);
                case UnityCliDebugStepWaitKinds.Time: return DelayMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
                case UnityCliDebugStepWaitKinds.Signal: return "signal " + Signal;
                case UnityCliDebugStepWaitKinds.Condition: return "condition " + Condition;
                default: return Kind;
            }
        }
    }

    /// <summary>step entry 的返回值：等待计划、是否完成、该 step 之后的 revision 状态与本次证据。</summary>
    public sealed class UnityCliDebugStepResult
    {
        /// <summary>该 step 请求的等待计划；Completed 为 true 时忽略。</summary>
        public UnityCliDebugStepWait Wait { get; internal set; }

        /// <summary>true 表示该 step 主动结束整个 driver。</summary>
        public bool Completed { get; internal set; }

        /// <summary>该 step 结束时的 revision 状态候选；null 表示不改状态。</summary>
        public object State { get; internal set; }

        /// <summary>该 step 结束后请求的 CAS epoch；State 非空时必须与当前 epoch 一致才发布。</summary>
        public long StateEpoch { get; internal set; } = -1L;

        /// <summary>该 step 声明的下一个 entry；为空表示同一个 entry 继续。</summary>
        public string NextEntry { get; internal set; } = string.Empty;

        public static UnityCliDebugStepResult Continue(UnityCliDebugStepWait wait)
        {
            return new UnityCliDebugStepResult { Wait = wait ?? UnityCliDebugStepWait.NextFrame(), Completed = false };
        }

        public static UnityCliDebugStepResult Continue(UnityCliDebugStepWait wait, object state, long stateEpoch)
        {
            return new UnityCliDebugStepResult { Wait = wait ?? UnityCliDebugStepWait.NextFrame(), Completed = false, State = state, StateEpoch = stateEpoch };
        }

        public static UnityCliDebugStepResult Finish()
        {
            return new UnityCliDebugStepResult { Completed = true };
        }

        public static UnityCliDebugStepResult Finish(object state, long stateEpoch)
        {
            return new UnityCliDebugStepResult { Completed = true, State = state, StateEpoch = stateEpoch };
        }
    }

    /// <summary>一条命名检查；宿主只记录调用方声明的事实，不生成自动通过。</summary>
    public sealed class UnityCliDebugCheck
    {
        public string Name { get; set; } = string.Empty;
        public object Expected { get; set; }
        public object Actual { get; set; }
        public bool Passed { get; set; }
        public string Coverage { get; set; } = string.Empty;
        public readonly List<string> Evidence = new List<string>();
    }

    /// <summary>程序上下文必须向宿主提供的能力；只有宿主能实现它，程序无法自行注册 scheduler。</summary>
    internal interface IUnityCliDebugProgramRuntime
    {
        string SessionId { get; }
        string ProgramId { get; }
        string ProgramRunId { get; }
        string Entry { get; }
        long Revision { get; }
        string ExecutionSite { get; }
        int ThreadId { get; }
        bool MainThread { get; }
        string RuntimeId { get; }
        string DomainGeneration { get; }
        object StateSchema { get; }
        object StateSnapshot();
        long StateEpoch { get; }
        string TryUpdateState(long expectedEpoch, object candidate, long candidateBytes);
        void Emit(string name, object value);
        void AddCheck(UnityCliDebugCheck check);
        void AddEvidence(string reference);
        string AcquireHandle(object target, bool strong);
        bool ReleaseHandle(string handleId);
        void PublishClock(string name, double value, string rawText);
        void ScheduleSignal(string signal, object payload);
        void RegisterCleanup(string name, Action cleanup);
    }

    /// <summary>
    /// 一个 entry 可见的宿主上下文。它只暴露能力，不暴露 host 内部状态、owner 身份或 scheduler 细节。
    /// 状态读写走 snapshot 与 CAS，绝不返回可并发修改的普通字典。
    /// </summary>
    public class UnityCliDebugProgramContext
    {
        private readonly IUnityCliDebugProgramRuntime runtime;

        internal UnityCliDebugProgramContext(IUnityCliDebugProgramRuntime runtime)
        {
            this.runtime = runtime ?? throw new ArgumentNullException("runtime");
        }

        public string SessionId { get { return runtime.SessionId; } }
        public string ProgramId { get { return runtime.ProgramId; } }
        public string ProgramRunId { get { return runtime.ProgramRunId; } }
        public string Entry { get { return runtime.Entry; } }
        public long Revision { get { return runtime.Revision; } }
        public string ExecutionSite { get { return runtime.ExecutionSite; } }
        public int ThreadId { get { return runtime.ThreadId; } }
        public bool MainThread { get { return runtime.MainThread; } }
        public string RuntimeId { get { return runtime.RuntimeId; } }
        public string DomainGeneration { get { return runtime.DomainGeneration; } }

        /// <summary>声明的 revision 状态 schema；只读副本。</summary>
        public IDictionary<string, object> StateSchema { get { return Snapshot(runtime.StateSchema); } }

        /// <summary>当前 revision 状态的只读快照；调用方修改它不会改变宿主状态。</summary>
        public IDictionary<string, object> StateSnapshot { get { return Snapshot(runtime.StateSnapshot()); } }

        /// <summary>当前 revision 状态的 CAS epoch；TryUpdateState 必须携带它。</summary>
        public long StateEpoch { get { return runtime.StateEpoch; } }

        /// <summary>
        /// CAS 更新 revision 状态。epoch 不匹配时返回稳定失败原因且不发布，调用方保留旧状态。
        /// 提交整个候选状态，不做局部合并。
        /// </summary>
        public string TryUpdateState(long expectedEpoch, IDictionary<string, object> candidate)
        {
            var bytes = candidate == null ? 0L : Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(new Dictionary<string, object>(candidate, StringComparer.Ordinal)));
            return runtime.TryUpdateState(expectedEpoch, candidate == null ? null : new Dictionary<string, object>(candidate, StringComparer.Ordinal), bytes);
        }

        /// <summary>写入一条有界业务事件；投影在入库边界物化。</summary>
        public void Emit(string name, object value)
        {
            runtime.Emit(name, value);
        }

        /// <summary>登记一条命名检查；宿主只记录，不自动判定业务成功。</summary>
        public void Check(string name, object expected, object actual, bool passed, string coverage = null, params string[] evidence)
        {
            var record = new UnityCliDebugCheck
            {
                Name = name ?? string.Empty,
                Expected = expected,
                Actual = actual,
                Passed = passed,
                Coverage = coverage ?? string.Empty
            };
            if (evidence != null) foreach (var reference in evidence) if (!string.IsNullOrEmpty(reference)) record.Evidence.Add(reference);
            runtime.AddCheck(record);
        }

        /// <summary>登记一个证据引用；不复制或假装拥有外部证据。</summary>
        public void AddEvidence(string reference)
        {
            runtime.AddEvidence(reference);
        }

        /// <summary>取得一个有界对象句柄；默认弱引用，强引用是改变对象存活的干预。预算拒绝时返回空串。</summary>
        public string AcquireHandle(object target, bool strong = false)
        {
            return runtime.AcquireHandle(target, strong);
        }

        public bool ReleaseHandle(string handleId)
        {
            return runtime.ReleaseHandle(handleId);
        }

        /// <summary>发布一个命名项目逻辑时钟；不是 Unity frame 推断值。</summary>
        public void PublishClock(string name, double value, string rawText = null)
        {
            runtime.PublishClock(name, value, rawText);
        }

        /// <summary>
        /// 非阻塞投递一个信号到已声明的执行点。这里只排队：投递与当场执行分别记录，
        /// 目标未到达时不补执行、不换线程，也不把本次调用变成同步替代结果。
        /// </summary>
        public void ScheduleSignal(string signal, object payload = null)
        {
            runtime.ScheduleSignal(signal, payload);
        }

        /// <summary>注册一个宿主管理的有限清理条目；delegate 只用于清理，不可能被伪装成可保存状态。</summary>
        public void RegisterCleanup(string name, Action cleanup)
        {
            runtime.RegisterCleanup(name, cleanup);
        }

        private static IDictionary<string, object> Snapshot(object value)
        {
            var source = value as IDictionary<string, object>;
            return source == null ? new Dictionary<string, object>(StringComparer.Ordinal) : new Dictionary<string, object>(source, StringComparer.Ordinal);
        }
    }

    /// <summary>step entry 的输入：一个有限同步步骤的宿主上下文。</summary>
    public sealed class UnityCliDebugStepContext : UnityCliDebugProgramContext
    {
        internal UnityCliDebugStepContext(IUnityCliDebugProgramRuntime runtime) : base(runtime) { }

        /// <summary>
        /// 该 step 的序号；一次运行的第一个真正执行的 step 为 1，之后每个执行的 step 加一。
        /// driver 的每个 step 都记录自己的 revision，因此序号与 stepCount、stepRevisions 一一对应。
        /// </summary>
        public long StepIndex { get; internal set; }
    }

    /// <summary>oneshot entry 的输入：一次同步调用，不产生跨帧 continuation。</summary>
    public sealed class UnityCliDebugOneShotContext : UnityCliDebugProgramContext
    {
        private readonly IDictionary<string, object> args;

        internal UnityCliDebugOneShotContext(IUnityCliDebugProgramRuntime runtime, IDictionary<string, object> args) : base(runtime)
        {
            this.args = args ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>本次运行的 JSON 参数只读副本。</summary>
        public IDictionary<string, object> Args { get { return new Dictionary<string, object>(args, StringComparer.Ordinal); } }

        public object Arg(string name)
        {
            object value;
            return name != null && args.TryGetValue(name, out value) ? value : null;
        }
    }

    /// <summary>listener entry 的输入：一次信号事件上下文，当场同步执行。</summary>
    public sealed class UnityCliDebugEventContext : UnityCliDebugProgramContext
    {
        internal UnityCliDebugEventContext(IUnityCliDebugProgramRuntime runtime, string signal, object payload, int triggeredThreadId, double triggeredAtMonotonicMs) : base(runtime)
        {
            Signal = signal ?? string.Empty;
            Payload = payload;
            TriggeredThreadId = triggeredThreadId;
            TriggeredAtMonotonicMs = triggeredAtMonotonicMs;
        }

        public string Signal { get; private set; }
        public object Payload { get; private set; }
        public int TriggeredThreadId { get; private set; }
        public double TriggeredAtMonotonicMs { get; private set; }
    }

    /// <summary>cleanup entry 的输入：清理原因与取消状态。</summary>
    public sealed class UnityCliDebugCleanupContext : UnityCliDebugProgramContext
    {
        internal UnityCliDebugCleanupContext(IUnityCliDebugProgramRuntime runtime, string reason, bool cancellationRequested) : base(runtime)
        {
            Reason = reason ?? string.Empty;
            CancellationRequested = cancellationRequested;
        }

        public string Reason { get; private set; }
        public bool CancellationRequested { get; private set; }
    }

    /// <summary>
    /// method callback 的输入：当前 invocation 加上本次调用的 program 身份。
    /// prefix 可以改参数并跳过原方法，postfix 可以改结果，异常结束时原异常继续传播。
    /// </summary>
    public sealed class UnityCliDebugInvocation
    {
        private readonly UnityCliDebugProgramContext program;
        private readonly UnityCliHookContext context;

        internal UnityCliDebugInvocation(UnityCliDebugProgramContext program, UnityCliHookContext context)
        {
            this.program = program;
            this.context = context;
        }

        /// <summary>本次调用的 program 上下文；状态访问与证据登记都走它。</summary>
        public UnityCliDebugProgramContext Program { get { return program; } }

        public object Instance { get { return context.Instance; } }
        public object[] Arguments { get { return context.Arguments; } }
        public object Result { get { return context.Result; } set { context.Result = value; } }
        public string MethodIdentity { get { return context.MethodIdentity; } }
        public bool IsPrefix { get { return context.IsPrefix; } }
        public double ElapsedMilliseconds { get { return context.ElapsedMilliseconds; } }
        public int Frame { get { return context.Frame; } }
        public IDictionary<string, object> State { get { return context.State; } }

        public void SetArgument(int index, object value) { context.SetArgument(index, value); }
        public void SetResult(object value) { context.Result = value; }
        public void SkipOriginal() { context.SkipOriginal(); }
        public void SkipOriginal(object result) { context.SkipOriginal(result); }

        public object ReadField(string fieldName) { return context.ReadField<object>(fieldName); }
        public object ReadField(string declaringType, string fieldName) { return context.ReadField<object>(declaringType, fieldName); }
        public void WriteField(string fieldName, object value) { context.WriteField(fieldName, value); }
        public void WriteField(string declaringType, string fieldName, object value) { context.WriteField(declaringType, fieldName, value); }
    }

    /// <summary>ABI 校验与 handler 类别签名的唯一声明点。</summary>
    internal static class UnityCliDebugProgramAbi
    {
        /// <summary>program 事件在统一事件存储中的来源标识；事件存储的 hook/lifecycle 语义不变。</summary>
        internal const string EventSource = "program";

        /// <summary>该类别 entry 的精确参数类型；返回值约束另行判定。</summary>
        internal static Type[] ParameterTypes(string category)
        {
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Step, StringComparison.Ordinal)) return new[] { typeof(UnityCliDebugStepContext) };
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Method, StringComparison.Ordinal)) return new[] { typeof(UnityCliDebugInvocation) };
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Listener, StringComparison.Ordinal)) return new[] { typeof(UnityCliDebugEventContext) };
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal)) return new[] { typeof(UnityCliDebugOneShotContext) };
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Cleanup, StringComparison.Ordinal)) return new[] { typeof(UnityCliDebugCleanupContext) };
            return null;
        }

        /// <summary>类别签名与例子共用的自足文本；descriptor 直接引用它。</summary>
        internal static string SignatureOf(string category)
        {
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Step, StringComparison.Ordinal)) return "public static UnityCliDebugStepResult " + "Entry(UnityCliDebugStepContext context)";
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Method, StringComparison.Ordinal)) return "public static void Entry(UnityCliDebugInvocation invocation)";
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Listener, StringComparison.Ordinal)) return "public static void Entry(UnityCliDebugEventContext context)";
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal)) return "public static object Entry(UnityCliDebugOneShotContext context)";
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Cleanup, StringComparison.Ordinal)) return "public static void Entry(UnityCliDebugCleanupContext context)";
            return string.Empty;
        }

        /// <summary>
        /// step 入口上下文与示例依赖成员的准确 ABI；descriptor 直接引用它，陌生人不需要探测即可写出可编译的 step 代码。
        /// 只列签名事实：不重复讲语义、不引导调用顺序。
        /// </summary>
        internal static string StepContextAbi()
        {
            return "Step entry context (UnityCli.UnityCliDebugStepContext, a UnityCli.UnityCliDebugProgramContext) members: "
                + "void Emit(string name, object value); "
                + "void Check(string name, object expected, object actual, bool passed, string coverage = null, params string[] evidence); "
                + "long StepIndex { get; }; string SessionId { get; }; string ProgramId { get; }; long Revision { get; }; int ThreadId { get; }; bool MainThread { get; }; "
                + "System.Collections.Generic.IDictionary<string, object> StateSnapshot { get; }; long StateEpoch { get; }; "
                + "string TryUpdateState(long expectedEpoch, System.Collections.Generic.IDictionary<string, object> candidate); "
                + "string AcquireHandle(object target, bool strong = false); bool ReleaseHandle(string handleId); "
                + "void ScheduleSignal(string signal, object payload = null); void PublishClock(string name, double value, string rawText = null); "
                + "void AddEvidence(string reference); void RegisterCleanup(string name, System.Action cleanup). "
                + "Step result factories (on UnityCliDebugStepResult, each returns UnityCliDebugStepResult): Continue(UnityCliDebugStepWait wait), Continue(UnityCliDebugStepWait wait, object state, long stateEpoch), Finish(), Finish(object state, long stateEpoch). "
                + "Step wait factories (on UnityCliDebugStepWait, each returns UnityCliDebugStepWait): NextFrame(long frames = 1), After(double milliseconds), UntilSignal(string signal, double timeoutMilliseconds), UntilCondition(string condition, double timeoutMilliseconds). "
                + "Oneshot example members (UnityCliDebugOneShotContext): object Arg(string name); System.Collections.Generic.IDictionary<string, object> Args { get; }. "
                + "Method example members (UnityCliDebugInvocation): object Instance { get; }; object[] Arguments { get; }; object Result { get; set; }; string MethodIdentity { get; }; UnityCliDebugProgramContext Program { get; }.";
        }

        /// <summary>全部类别的签名文本；descriptor 与测试共用同一来源。</summary>
        internal static string SignatureTable()
        {
            var builder = new StringBuilder();
            var first = true;
            foreach (var category in UnityCliDebugProgramEntryCategories.All())
            {
                if (!first) builder.Append(" | ");
                first = false;
                builder.Append(category).Append(": ").Append(SignatureOf(category));
            }
            return builder.ToString();
        }

        /// <summary>
        /// descriptor 自足引用的最小合法程序。它展示一个 step entry、一个 oneshot entry 与一个
        /// method entry，并说明 entry 只能用声明类别对应的精确签名。
        /// </summary>
        internal static string ExampleSource()
        {
            var builder = new StringBuilder();
            builder.Append("using UnityCli;").Append('\n');
            builder.Append("public static class Probe").Append('\n');
            builder.Append('{').Append('\n');
            builder.Append("    public static UnityCliDebugStepResult Step(UnityCliDebugStepContext context)").Append('\n');
            builder.Append("    {").Append('\n');
            builder.Append("        context.Emit(\"tick\", context.StepIndex);").Append('\n');
            builder.Append("        context.Check(\"alive\", true, true, true, \"this step\");").Append('\n');
            // 例子必须让读者数得出步数：基数写在例子自身，且用精确相等而不是开区间比较。
            builder.Append("        // StepIndex is 1 on the first executed step of a run and increases by one per executed step,").Append('\n');
            builder.Append("        // so this entry finishes on its third step and that run reports stepCount 3.").Append('\n');
            builder.Append("        if (context.StepIndex == 3) return UnityCliDebugStepResult.Finish();").Append('\n');
            builder.Append("        return UnityCliDebugStepResult.Continue(UnityCliDebugStepWait.NextFrame());").Append('\n');
            builder.Append("    }").Append('\n');
            builder.Append("    public static object ProbeOnce(UnityCliDebugOneShotContext context)").Append('\n');
            builder.Append("    {").Append('\n');
            builder.Append("        return context.Arg(\"name\");").Append('\n');
            builder.Append("    }").Append('\n');
            builder.Append("    public static void OnHit(UnityCliDebugInvocation invocation)").Append('\n');
            builder.Append("    {").Append('\n');
            builder.Append("        invocation.Program.Emit(\"hit\", invocation.MethodIdentity);").Append('\n');
            builder.Append("    }").Append('\n');
            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// 按 metadata 精确解析并校验声明的 entry。这里不实例化用户的 attribute 或构造逻辑，
        /// 只读类型与方法签名；静态初始化只在首次调用时发生，属于运行故障而不是安装故障。
        /// </summary>
        internal static bool TryResolve(Assembly assembly, IReadOnlyList<UnityCliDebugProgramEntry> entries, out List<ResolvedProgramEntry> resolved, out string failure, out string detail)
        {
            resolved = new List<ResolvedProgramEntry>();
            failure = string.Empty;
            detail = string.Empty;
            if (assembly == null)
            {
                failure = UnityCliDebugProgramFailures.EntryInvalid;
                detail = "the compiled program assembly is unavailable";
                return false;
            }
            if (entries == null || entries.Count == 0)
            {
                failure = UnityCliDebugProgramFailures.EntryInvalid;
                detail = "at least one named entry is required";
                return false;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var name = entry == null ? string.Empty : entry.Name;
                if (string.IsNullOrEmpty(name))
                {
                    failure = UnityCliDebugProgramFailures.EntryInvalid;
                    detail = "an entry name must be non-empty";
                    return false;
                }
                if (!seen.Add(name))
                {
                    failure = UnityCliDebugProgramFailures.EntryInvalid;
                    detail = "entry name " + name + " is declared more than once";
                    return false;
                }
                var category = entry.Category;
                if (!UnityCliDebugProgramEntryCategories.IsDefined(category))
                {
                    failure = UnityCliDebugProgramFailures.EntryInvalid;
                    detail = "entry " + name + " declares unknown category " + (category ?? "null") + "; expected one of " + string.Join(", ", UnityCliDebugProgramEntryCategories.All());
                    return false;
                }
                Type type = null;
                if (!string.IsNullOrEmpty(entry.DeclaringType))
                {
                    type = assembly.GetType(entry.DeclaringType, false);
                    if (type == null)
                    {
                        failure = UnityCliDebugProgramFailures.EntryInvalid;
                        detail = "entry " + name + " declares type " + entry.DeclaringType + ", which the compiled program assembly does not contain";
                        return false;
                    }
                }
                else
                {
                    type = FindDeclaringType(assembly, name, category);
                    if (type == null)
                    {
                        failure = UnityCliDebugProgramFailures.EntryInvalid;
                        detail = "entry " + name + " has no unique declaring type in the compiled program assembly; declare declaringType explicitly";
                        return false;
                    }
                }
                var method = ResolveMethod(type, name, category, out detail);
                if (method == null)
                {
                    failure = UnityCliDebugProgramFailures.EntryInvalid;
                    if (detail.Length == 0) detail = "entry " + name + " does not match " + SignatureOf(category);
                    return false;
                }
                var instance = !method.IsStatic;
                if (instance && !IsLegalWrapper(type, out detail))
                {
                    failure = UnityCliDebugProgramFailures.EntryInvalid;
                    return false;
                }
                var signature = SignatureText(method);
                entry.Signature = signature;
                resolved.Add(new ResolvedProgramEntry { Entry = entry, Method = method, Instance = instance, Type = type, Signature = signature });
            }
            return true;
        }

        /// <summary>为全部 wrapper entry 建立一次实例；没有 wrapper entry 时返回空表。</summary>
        internal static Dictionary<Type, object> CreateWrappers(List<ResolvedProgramEntry> resolved)
        {
            var wrappers = new Dictionary<Type, object>();
            if (resolved == null) return wrappers;
            foreach (var entry in resolved)
            {
                if (!entry.Instance) continue;
                if (wrappers.ContainsKey(entry.Type)) continue;
                wrappers[entry.Type] = Activator.CreateInstance(entry.Type);
            }
            return wrappers;
        }

        /// <summary>wrapper 必须带标记且只声明一个无参构造，避免执行用户初始化程序。</summary>
        private static bool IsLegalWrapper(Type type, out string detail)
        {
            detail = string.Empty;
            if (type.IsAbstract || type.IsGenericTypeDefinition)
            {
                detail = "wrapper type " + type.FullName + " must be a concrete non-generic type";
                return false;
            }
            if (!type.GetCustomAttributes(typeof(UnityCliDebugProgramWrapperAttribute), false).Any())
            {
                detail = "instance entry type " + type.FullName + " must be marked with UnityCliDebugProgramWrapperAttribute, or its entry must be static";
                return false;
            }
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (constructors.Length != 1 || constructors[0].GetParameters().Length != 0)
            {
                detail = "wrapper type " + type.FullName + " must declare exactly one parameterless constructor and no other instance constructor";
                return false;
            }
            return true;
        }

        private static Type FindDeclaringType(Assembly assembly, string name, string category)
        {
            Type match = null;
            foreach (var type in SafeTypes(assembly))
            {
                if (type == null || type.IsGenericTypeDefinition) continue;
                var method = ResolveMethod(type, name, category, out _);
                if (method == null) continue;
                if (match != null && !ReferenceEquals(match, type)) return null;
                match = type;
            }
            return match;
        }

        /// <summary>精确解析：同名方法必须只有一个候选匹配该类别签名，否则视为歧义。</summary>
        private static MethodInfo ResolveMethod(Type type, string name, string category, out string detail)
        {
            detail = string.Empty;
            var expected = ParameterTypes(category);
            var candidates = new List<MethodInfo>();
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal)) continue;
                candidates.Add(method);
            }
            if (candidates.Count == 0)
            {
                detail = "type " + type.FullName + " does not declare a method named " + name;
                return null;
            }
            var matches = candidates.Where(method => ParametersMatch(method, expected)).ToList();
            if (matches.Count != 1)
            {
                detail = matches.Count == 0
                    ? "no overload of " + name + " in " + type.FullName + " matches " + SignatureOf(category)
                    : "overloads of " + name + " in " + type.FullName + " are ambiguous for category " + category + "; declare exactly one matching signature";
                return null;
            }
            var match = matches[0];
            if (RejectsReturn(match, category, out detail)) return null;
            if (HasAsyncStateMachine(match, out detail)) return null;
            return match;
        }

        private static bool ParametersMatch(MethodInfo method, Type[] expected)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != expected.Length) return false;
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.IsOut || parameter.ParameterType.IsByRef) return false;
                if (!ReferenceEquals(parameter.ParameterType, expected[index])) return false;
            }
            return true;
        }

        private static bool RejectsReturn(MethodInfo method, string category, out string detail)
        {
            detail = string.Empty;
            var returnsVoid = method.ReturnType == typeof(void);
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.Step, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(method.ReturnType, typeof(UnityCliDebugStepResult)))
                {
                    detail = "category step requires the exact return type UnityCliDebugStepResult; " + method.Name + " returns " + method.ReturnType.Name;
                    return true;
                }
                return false;
            }
            if (string.Equals(category, UnityCliDebugProgramEntryCategories.OneShot, StringComparison.Ordinal))
            {
                if (returnsVoid)
                {
                    detail = "a oneshot entry must return a value; void is not a projectable result";
                    return true;
                }
                return false;
            }
            if (!returnsVoid)
            {
                detail = "category " + category + " requires a void return; " + method.Name + " returns " + method.ReturnType.Name;
                return true;
            }
            return false;
        }

        private static bool HasAsyncStateMachine(MethodInfo method, out string detail)
        {
            detail = string.Empty;
            if (!method.GetCustomAttributes(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), false).Any()) return false;
            detail = "an async state machine is not a bounded synchronous entry; entry " + method.Name + " must return synchronously";
            return true;
        }

        /// <summary>参与 entry 解析的类型集合；部分可加载的程序集不能吞掉本域真实的方法。</summary>
        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                types = exception.Types.Where(type => type != null).ToArray();
            }
            return types;
        }

        private static string SignatureText(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var builder = new StringBuilder();
            builder.Append(method.ReturnType.FullName).Append(' ').Append(method.DeclaringType == null ? string.Empty : method.DeclaringType.FullName).Append('.').Append(method.Name).Append('(');
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index > 0) builder.Append(", ");
                builder.Append(parameters[index].ParameterType.FullName).Append(' ').Append(parameters[index].Name);
            }
            builder.Append(')');
            return builder.ToString();
        }
    }

    /// <summary>一个已解析的 entry：方法身份、是否 wrapper 实例、精确签名。</summary>
    internal sealed class ResolvedProgramEntry
    {
        internal UnityCliDebugProgramEntry Entry;
        internal MethodInfo Method;
        internal Type Type;
        internal bool Instance;
        internal string Signature = string.Empty;
    }

    /// <summary>
    /// 动态编译的 program binding 调用入口。生成的 callback 源码只调用这里，
    /// 因此 program-owned binding 与直接 Hook 共用同一 detour 后端与同一派发实现。
    /// 这些方法必须 public：生成的 adapter 位于另一个程序集。
    /// </summary>
    public static class DebugProgramDispatch
    {
        /// <summary>exact 方法 prefix 命中时的入口；当场同步执行，返回是否继续执行原方法。</summary>
        public static bool Prefix(string programId, string entry, UnityCliHookContext context)
        {
            return DebugProgramHost.DispatchMethod(programId, entry, context, true);
        }

        /// <summary>exact 方法 postfix 命中时的入口；当场同步执行。</summary>
        public static void Postfix(string programId, string entry, UnityCliHookContext context)
        {
            DebugProgramHost.DispatchMethod(programId, entry, context, false);
        }

        /// <summary>已绑定执行点上的信号投递入口；只执行已经排队且尚未过期的投递。</summary>
        public static void Signal(string programId, string entry, string signal, string payloadJson)
        {
            DebugProgramHost.DispatchSignal(programId, entry, signal, payloadJson, Thread.CurrentThread.ManagedThreadId);
        }
    }
}
#endif
