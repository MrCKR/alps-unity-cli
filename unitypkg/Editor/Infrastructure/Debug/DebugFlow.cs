#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// 流体内可用的等待种类。只能通过 DebugFlow 工厂产生，宿主按种类解释。
    /// </summary>
    public static class DebugFlowWaitKinds
    {
        public const string Frame = "frame";
        public const string Time = "time";
        public const string Condition = "condition";
        public const string Signal = "signal";
        public const string Log = "log";
        public const string All = "all";
        public const string Any = "any";
        public const string Return = "return";
    }

    /// <summary>
    /// 流体内 yield 出的一个等待。构造只发生在工厂方法里；属性只读，宿主不改写调用方对象。
    /// 超时对等待形式是必填项：没有默认值，避免"等待无界"这种静默失效。
    /// </summary>
    public sealed class DebugFlowWait
    {
        public string Kind { get; private set; }
        public long Frames { get; private set; }
        public double DelayMilliseconds { get; private set; }
        public double TimeoutMilliseconds { get; private set; }
        public string SignalName { get; private set; }

        /// <summary>日志等待的子串与等级要求（等级为空表示不限）。</summary>
        public string LogPattern { get; private set; }
        public string LogLevel { get; private set; }
        public Func<bool> Condition { get; private set; }
        public IList<DebugFlowWait> Waits { get; private set; }
        public object ReturnValue { get; private set; }

        private DebugFlowWait(string kind)
        {
            Kind = kind;
            Frames = 0L;
            DelayMilliseconds = 0d;
            TimeoutMilliseconds = 0d;
            SignalName = string.Empty;
            LogPattern = string.Empty;
            LogLevel = string.Empty;
            Condition = null;
            Waits = null;
            ReturnValue = null;
        }

        internal static DebugFlowWait ForFrames(long frames)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Frame) { Frames = frames };
        }

        internal static DebugFlowWait ForDelay(double milliseconds, double timeoutMilliseconds)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Time)
            {
                DelayMilliseconds = milliseconds,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static DebugFlowWait ForCondition(Func<bool> condition, double timeoutMilliseconds)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Condition)
            {
                Condition = condition,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static DebugFlowWait ForSignal(string name, double timeoutMilliseconds)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Signal)
            {
                SignalName = name ?? string.Empty,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static DebugFlowWait ForGroup(string kind, IList<DebugFlowWait> waits, double timeoutMilliseconds)
        {
            return new DebugFlowWait(kind)
            {
                Waits = waits,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static DebugFlowWait ForLog(string pattern, string level, double timeoutMilliseconds)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Log)
            {
                LogPattern = pattern ?? string.Empty,
                LogLevel = level ?? string.Empty,
                TimeoutMilliseconds = timeoutMilliseconds
            };
        }

        internal static DebugFlowWait ForReturn(object value)
        {
            return new DebugFlowWait(DebugFlowWaitKinds.Return) { ReturnValue = value };
        }
    }

    /// <summary>
    /// 一次等待的结算事实。超时不是异常：流体内按 satisfied 与 timedOut 自行分支。
    /// lastObserved 保留超时前最后一次观察到的值，便于报告说清"看到了什么"。
    /// </summary>
    public sealed class DebugFlowWaitResult
    {
        public string Kind { get; internal set; }
        public bool Satisfied { get; internal set; }
        public bool TimedOut { get; internal set; }
        public double ElapsedMs { get; internal set; }
        public long Samples { get; internal set; }
        public object LastObserved { get; internal set; }
    }

    /// <summary>
    /// 流体内唯一可用的等待入口：普通 C# 协程体，等待通过这里表达。
    /// 每个等待都必须声明超时；条件为 C# 委托，本能力不提供字符串表达式。
    /// </summary>
    public static class DebugFlow
    {
        [ThreadStatic] private static DebugFlowWaitResult last;

        /// <summary>最近一次等待的结算事实；宿主在恢复本流之前写入，同一线程内立即读取。</summary>
        public static DebugFlowWaitResult Last { get { return last; } }

        public static DebugFlowWait NextFrame(long frames = 1L)
        {
            return DebugFlowWait.ForFrames(frames < 1L ? 1L : frames);
        }

        public static DebugFlowWait After(double milliseconds, double timeoutMilliseconds)
        {
            RequireBudget(timeoutMilliseconds, "After");
            return DebugFlowWait.ForDelay(milliseconds < 0d ? 0d : milliseconds, timeoutMilliseconds);
        }

        public static DebugFlowWait Until(Func<bool> condition, double timeoutMilliseconds)
        {
            if (condition == null) throw new ArgumentNullException("condition");
            RequireBudget(timeoutMilliseconds, "Until");
            return DebugFlowWait.ForCondition(condition, timeoutMilliseconds);
        }

        public static DebugFlowWait UntilSignal(string signal, double timeoutMilliseconds)
        {
            if (string.IsNullOrEmpty(signal)) throw new ArgumentException("signal is required", "signal");
            RequireBudget(timeoutMilliseconds, "UntilSignal");
            return DebugFlowWait.ForSignal(signal, timeoutMilliseconds);
        }

        public static DebugFlowWait UntilLog(string pattern, double timeoutMilliseconds, string level = null)
        {
            if (string.IsNullOrEmpty(pattern)) throw new ArgumentException("pattern is required", "pattern");
            RequireBudget(timeoutMilliseconds, "UntilLog");
            return DebugFlowWait.ForLog(pattern, level, timeoutMilliseconds);
        }

        public static DebugFlowWait All(params DebugFlowWait[] waits)
        {
            return DebugFlowWait.ForGroup(DebugFlowWaitKinds.All, RequireWaits(waits, "All"), RequireGroupBudget(waits));
        }

        public static DebugFlowWait Any(params DebugFlowWait[] waits)
        {
            return DebugFlowWait.ForGroup(DebugFlowWaitKinds.Any, RequireWaits(waits, "Any"), RequireGroupBudget(waits));
        }

        /// <summary>结束本流并返回一个值；之后 iterator 不再推进。</summary>
        public static DebugFlowWait Return(object value)
        {
            return DebugFlowWait.ForReturn(value);
        }

        internal static void ApplyLast(DebugFlowWaitResult result)
        {
            last = result;
        }

        private static void RequireBudget(double timeoutMilliseconds, string factory)
        {
            if (double.IsNaN(timeoutMilliseconds) || timeoutMilliseconds <= 0d)
                throw new ArgumentOutOfRangeException("timeoutMilliseconds", factory + " requires an explicit positive timeout; there is no default.");
        }

        private static DebugFlowWait[] RequireWaits(DebugFlowWait[] waits, string factory)
        {
            if (waits == null || waits.Length == 0)
                throw new ArgumentException(factory + " requires at least one wait.", "waits");
            foreach (var wait in waits)
            {
                if (wait == null) throw new ArgumentException(factory + " does not accept a null wait.", "waits");
            }
            return waits;
        }

        // 组合等待的截止时间取子等待中最大的超时；每个子等待仍保留自己的超时判定。
        private static double RequireGroupBudget(DebugFlowWait[] waits)
        {
            var budget = 0d;
            foreach (var wait in waits)
            {
                if (wait.TimeoutMilliseconds > budget) budget = wait.TimeoutMilliseconds;
            }
            return budget;
        }
    }
}
#endif
