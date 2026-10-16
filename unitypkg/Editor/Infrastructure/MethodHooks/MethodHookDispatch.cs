#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;

namespace UnityCli
{
    /// <summary>发布时已解析的捕获字段；派发热路径不再做反射解析。</summary>
    internal sealed class MethodHookFieldBinding
    {
        internal string Key;
        internal FieldInfo Field;
    }

    /// <summary>
    /// 一次发布内固定的捕获视图：构造后不再修改，可被任意线程按引用读取。
    /// 字段解析发生在发布时（主线程），因此命中路径只做 GetValue 与有界投影。
    /// </summary>
    internal sealed class MethodHookCaptureView
    {
        internal static readonly MethodHookCaptureView Empty = new MethodHookCaptureView(false, false, 1, null, null);

        internal readonly bool Arguments;
        internal readonly bool Result;
        internal readonly int SampleEvery;
        internal readonly MethodHookFieldBinding[] InstanceFields;
        internal readonly MethodHookFieldBinding[] StaticFields;

        internal MethodHookCaptureView(bool arguments, bool result, int sampleEvery, MethodHookFieldBinding[] instanceFields, MethodHookFieldBinding[] staticFields)
        {
            Arguments = arguments;
            Result = result;
            SampleEvery = sampleEvery < 1 ? 1 : sampleEvery;
            InstanceFields = instanceFields ?? new MethodHookFieldBinding[0];
            StaticFields = staticFields ?? new MethodHookFieldBinding[0];
        }
    }

    /// <summary>
    /// 一个 hook 定义的断点命中行为。断点不是平行引擎：target resolver、revision、捕获配置与事件存储都由 hook host 持有，
    /// 本对象只冻结一个定义在发布时的断点意图。安装与替换时（主线程）建立，命中路径只按引用读取。
    /// </summary>
    internal sealed class MethodHookBreakpoint
    {
        internal string Kind = DebugPauseBreakpointKinds.Snapshot;
        internal string Position = DebugPausePositions.Prefix;
        internal bool Unbounded;
        internal long DurationMs;
        internal int StackDepth;
        internal string SessionId = string.Empty;
        internal string ResourceId = string.Empty;

        internal bool IsPause { get { return string.Equals(Kind, DebugPauseBreakpointKinds.Pause, StringComparison.Ordinal); } }

        internal bool IsPrefix { get { return string.Equals(Position, DebugPausePositions.Prefix, StringComparison.Ordinal); } }

        internal MethodHookBreakpoint Clone()
        {
            return new MethodHookBreakpoint
            {
                Kind = Kind,
                Position = Position,
                Unbounded = Unbounded,
                DurationMs = DurationMs,
                StackDepth = StackDepth,
                SessionId = SessionId,
                ResourceId = ResourceId
            };
        }
    }

    /// <summary>
    /// 一个 Hook Definition 在一次发布中的不可变绑定。
    /// 命中路径只读取本对象，绝不重新查询可变的定义或定义集合；配置变更必须发布新的绑定。
    /// </summary>
    internal sealed class MethodHookBinding
    {
        internal MethodHookDefinition Definition;
        internal string HookId = string.Empty;
        internal long Revision;
        internal bool EmitHitEvents;
        internal bool HasCallbacks;
        internal IUnityCliMethodHookCallback Callback;
        /// <summary>发布时解析的 exact 方法；命中路径不再重新解析。</summary>
        internal MethodInfo Resolved;
        internal MethodHookCaptureView Capture = MethodHookCaptureView.Empty;
        /// <summary>该定义的 listener 过滤与有限命中状态；未声明 listener 时为 null。</summary>
        internal MethodHookListenerState Listener;
        /// <summary>该定义的断点命中行为；未声明断点时为 null。</summary>
        internal MethodHookBreakpoint Breakpoint;
    }

    /// <summary>listener 条件支持的比较算子；复杂条件用显式调试代码表达，不引入 DSL。</summary>
    internal static class MethodHookConditionOperators
    {
        internal const string Equals = "equals";
        internal const string NotEquals = "not-equals";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Equals, StringComparison.Ordinal) || string.Equals(value, NotEquals, StringComparison.Ordinal);
        }

        internal static string[] All() { return new[] { Equals, NotEquals }; }
    }

    /// <summary>listener 条件支持的取值来源。</summary>
    internal static class MethodHookConditionKinds
    {
        internal const string Argument = "argument";
        internal const string Field = "field";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Argument, StringComparison.Ordinal) || string.Equals(value, Field, StringComparison.Ordinal);
        }

        internal static string[] All() { return new[] { Argument, Field }; }
    }

    /// <summary>
    /// 一个 listener 命中条件。比较在命中线程上完成：实际值按同一投影边界物化后与字面量的规范 JSON 比较，
    /// 因此条件不依赖类型具体形状，也不会把未投影的对象暴露给读取端。
    /// Field 只在发布时（主线程）解析，命中路径不做反射解析。
    /// </summary>
    internal sealed class MethodHookCondition
    {
        internal string Kind = MethodHookConditionKinds.Argument;
        internal string Name = string.Empty;
        internal int ArgumentIndex = -1;
        internal string Operator = MethodHookConditionOperators.Equals;
        internal string DeclaringType = string.Empty;
        internal string LiteralCanonical = string.Empty;
        internal FieldInfo Field;

        internal string Describe()
        {
            var source = string.Equals(Kind, MethodHookConditionKinds.Argument, StringComparison.Ordinal)
                ? "argument[" + ArgumentIndex.ToString(CultureInfo.InvariantCulture) + "]"
                : (string.IsNullOrEmpty(DeclaringType) ? Name : DeclaringType + "." + Name);
            return source + " " + Operator + " " + LiteralCanonical;
        }
    }

    /// <summary>
    /// 一个 listener 的过滤与有限命中状态。
    ///
    /// 计数使用原子操作，因此任意托管线程的命中都能安全消费有限命中次数；
    /// 超过声明次数后的命中被计为 filtered，如实区分“未匹配”与“未发生”。
    /// </summary>
    internal sealed class MethodHookListenerState
    {
        internal string SessionId = string.Empty;
        internal string Trigger = DebugTriggerSources.MethodHit;

        /// <summary>trigger 为 player-loop-phase 时声明的具名 phase；否则为空串。</summary>
        internal string Phase = string.Empty;

        internal long MaxHits;
        internal long Hits;
        internal long Filtered;
        internal int ThreadId;
        internal string InstanceIdentity = string.Empty;
        internal MethodHookCondition[] Conditions = new MethodHookCondition[0];

        internal bool HasInstanceFilter { get { return InstanceIdentity.Length > 0; } }

        /// <summary>线程、实例身份与字段/参数条件必须全部满足；不匹配的命中被计为 filtered。</summary>
        internal bool Matches(int hitThreadId, string instanceIdentity, object[] arguments, object instance, bool allowUnityIdentity)
        {
            if (ThreadId != 0 && hitThreadId != ThreadId)
            {
                Interlocked.Increment(ref Filtered);
                return false;
            }
            if (HasInstanceFilter && !string.Equals(InstanceIdentity, instanceIdentity ?? string.Empty, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref Filtered);
                return false;
            }
            for (var index = 0; index < Conditions.Length; index++)
            {
                if (MethodHookListenerEvaluator.Matches(Conditions[index], arguments, instance, allowUnityIdentity)) continue;
                Interlocked.Increment(ref Filtered);
                return false;
            }
            return true;
        }

        /// <summary>消费一个有限命中次数；无上限时只计数。</summary>
        internal bool TryCountHit()
        {
            if (MaxHits <= 0L)
            {
                Interlocked.Increment(ref Hits);
                return true;
            }
            while (true)
            {
                var current = Interlocked.Read(ref Hits);
                if (current >= MaxHits)
                {
                    Interlocked.Increment(ref Filtered);
                    return false;
                }
                if (Interlocked.CompareExchange(ref Hits, current + 1L, current) == current) return true;
            }
        }

        internal bool Exhausted { get { return MaxHits > 0L && Interlocked.Read(ref Hits) >= MaxHits; } }
    }

    /// <summary>
    /// 一个 exact target 的不可变派发快照：定义顺序固定为 priority 降序、安装顺序升序。
    /// 读路径只按引用读取 Ordered；发布变更时整体替换本对象，而不是就地修改数组。
    /// </summary>
    internal sealed class MethodHookDispatch
    {
        internal static readonly MethodHookDispatch Empty = new MethodHookDispatch(new MethodHookBinding[0]);

        internal readonly MethodHookBinding[] Ordered;

        internal MethodHookDispatch(MethodHookBinding[] ordered)
        {
            Ordered = ordered ?? new MethodHookBinding[0];
        }
    }

    /// <summary>
    /// prefix 为一次真实调用固定的帧：参与定义、每个定义的调用 id、采样决定与拦截时间戳。
    /// postfix 与 finalizer 只能读取本帧，因此一次调用永远按同一 revision 完成。
    /// </summary>
    internal sealed class MethodHookFrame
    {
        internal MethodBase Original;
        internal long[] InvocationIds;
        internal MethodHookBinding[] Bindings;
        internal bool[] Record;
        internal MethodHookInterceptionStamp Stamp;
        internal object Instance;
        internal object[] Arguments;
    }

    /// <summary>
    /// 单个观察线程的有界帧栈。只有该线程访问自己的记录，
    /// 因此帧的内存顺序由栈语义保证，不需要跨线程协调；释放记录由 host 在空栈时执行。
    /// </summary>
    internal sealed class MethodHookThreadFrames
    {
        private readonly List<MethodHookFrame> frames = new List<MethodHookFrame>();
        private readonly object gate = new object();

        internal readonly int ThreadId;

        internal MethodHookThreadFrames(int threadId) { ThreadId = threadId; }

        internal int Count { get { lock (gate) return frames.Count; } }

        internal void Push(MethodHookFrame frame) { lock (gate) frames.Add(frame); }

        /// <summary>弹出该 original 的最内层帧；返回 null 表示本线程没有它的活动帧。</summary>
        internal MethodHookFrame Pop(MethodBase original)
        {
            lock (gate)
            {
                for (var index = frames.Count - 1; index >= 0; index--)
                {
                    if (!ReferenceEquals(frames[index].Original, original)) continue;
                    var frame = frames[index];
                    frames.RemoveAt(index);
                    return frame;
                }
                return null;
            }
        }

        internal bool IsEmpty { get { lock (gate) return frames.Count == 0; } }

        /// <summary>本线程是否已有该 original 的活动帧；用于递归下保持 prefix/postfix 配对一致。</summary>
        internal bool ContainsOriginal(MethodBase original)
        {
            lock (gate)
            {
                for (var index = 0; index < frames.Count; index++)
                    if (ReferenceEquals(frames[index].Original, original)) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// 线程上产生的、只允许主线程收尾的故障请求。
    /// worker 线程不做持久化与 detach，只把结论交给中央 scheduler tick；
    /// 每个 hook 只保留一条（后者覆盖前者），因此队列天然有界。
    /// </summary>
    internal sealed class MethodHookThreadFault
    {
        internal string HookId = string.Empty;
        internal long Revision;
        internal string Code = string.Empty;
        internal string Message = string.Empty;
        internal long InvocationId;
        internal string Owner = string.Empty;
    }

    /// <summary>
    /// worker 命中时无法读取的 Unity 显示身份；只登记弱引用，
    /// 主线程在中央 tick 上把它作为独立、带时间戳的补充事件追加，绝不回填命中时刻的值。
    /// 队列有界：超限时丢弃最新补充项，丢弃数量由 info 如实报告。
    /// </summary>
    internal sealed class MethodHookPendingSupplement
    {
        internal string HookId = string.Empty;
        internal long Revision;
        internal long InvocationId;
        internal int HitThreadId;
        internal double HitMilliseconds;
        internal string MethodIdentity = string.Empty;
        internal WeakReference Instance;
    }
}
#endif
