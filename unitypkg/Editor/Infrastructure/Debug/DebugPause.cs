#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>断点种类：snapshot 只捕获后继续，pause 发布 pauseId 并停住命中线程。</summary>
    internal static class DebugPauseBreakpointKinds
    {
        internal const string Snapshot = "snapshot";
        internal const string Pause = "pause";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Snapshot, StringComparison.Ordinal) || string.Equals(value, Pause, StringComparison.Ordinal);
        }

        internal static string[] All() { return new[] { Snapshot, Pause }; }
    }

    /// <summary>断点位置：prefix 在原方法前，postfix 在原方法返回后。</summary>
    internal static class DebugPausePositions
    {
        internal const string Prefix = "prefix";
        internal const string Postfix = "postfix";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Prefix, StringComparison.Ordinal) || string.Equals(value, Postfix, StringComparison.Ordinal);
        }

        internal static string[] All() { return new[] { Prefix, Postfix }; }
    }

    /// <summary>pauseTimeout.kind 的闭集；没有默认值，缺失即拒绝。</summary>
    internal static class DebugPauseTimeoutKinds
    {
        internal const string Bounded = "bounded";
        internal const string Unbounded = "unbounded";

        internal static bool IsDefined(string value)
        {
            return string.Equals(value, Bounded, StringComparison.Ordinal) || string.Equals(value, Unbounded, StringComparison.Ordinal);
        }

        internal static string[] All() { return new[] { Bounded, Unbounded }; }
    }

    /// <summary>暂停现场的公开状态闭集。</summary>
    internal static class DebugPauseStates
    {
        internal const string Paused = "paused";
        internal const string Resuming = "resuming";
        internal const string Resumed = "resumed";
        internal const string TimedOut = "timed-out";
        internal const string Released = "released";

        internal static bool IsTerminal(string value)
        {
            return string.Equals(value, Resumed, StringComparison.Ordinal)
                || string.Equals(value, TimedOut, StringComparison.Ordinal)
                || string.Equals(value, Released, StringComparison.Ordinal);
        }
    }

    /// <summary>现场离开 paused 状态的稳定原因；resume 不重放之前操作。</summary>
    internal static class DebugPauseReleaseReasons
    {
        internal const string Resume = "resume";
        internal const string Timeout = "pause-timeout";
        internal const string SessionStop = "session-stop";
        internal const string HookDisabled = "hook-disabled";
        internal const string HookRemoved = "hook-removed";
        internal const string HookReplaced = "hook-replaced";
        internal const string PlayExit = "play-exit";
        internal const string DomainReload = "domain-reload";
        internal const string EditorShutdown = "editor-shutdown";

        /// <summary>reload/quit 类清理；与显式 resume 和 timeout 一起构成无限暂停的合法解除路径。</summary>
        internal static bool IsReachableCleanup(string value)
        {
            return string.Equals(value, SessionStop, StringComparison.Ordinal)
                || string.Equals(value, HookDisabled, StringComparison.Ordinal)
                || string.Equals(value, HookRemoved, StringComparison.Ordinal)
                || string.Equals(value, HookReplaced, StringComparison.Ordinal)
                || string.Equals(value, PlayExit, StringComparison.Ordinal)
                || string.Equals(value, DomainReload, StringComparison.Ordinal)
                || string.Equals(value, EditorShutdown, StringComparison.Ordinal);
        }
    }

    /// <summary>现场操作的公开执行状态；同步用户代码不返回时只能是 pending，绝不是 forced-resume。</summary>
    internal static class DebugPauseExecutionStates
    {
        internal const string Completed = "completed";
        internal const string Pending = "pending";
    }

    /// <summary>pause mailbox 的操作种类。value 只用于显式命名 pauseId 的值读写。</summary>
    internal static class DebugPauseOperationKinds
    {
        internal const string Inspect = "inspect";
        internal const string Evaluate = "evaluate";
        internal const string ValueRead = "value.read";
        internal const string ValueWrite = "value.write";
    }

    /// <summary>
    /// 暂停现场操作。只在暂停线程上执行，结果与异常通过 <see cref="Completed"/> 发布给等待方。
    /// 本对象不注册任何 update 回调，也不参与派发；等待方由框架 wait 驱动。
    /// </summary>
    internal sealed class DebugPauseOperation
    {
        internal readonly string Kind;
        /// <summary>请求来源；记录 pauseSuppressed 与抑制归因必须使用同一字符串。</summary>
        internal readonly string Origin;
        /// <summary>本操作所属的现场；终结后仍可用它回答"哪一现场没服务完"，不复活现场。</summary>
        internal DebugPauseSite Site;
        /// <summary>
        /// 本操作自己的取消源：现场终结（resume、超时、session stop、Play 退出、域重载、Editor 关闭）时取消，
        /// 使在途的现场编译不在终结后继续占用编译器。它不会中断已经开始运行的用户同步代码。
        /// </summary>
        internal readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        internal DebugPauseInspectRequest InspectRequest;
        internal DebugPauseEvaluateRequest EvaluateRequest;
        internal DebugValueReadRequest ValueReadRequest;
        internal DebugValueWriteRequest ValueWriteRequest;
        internal DebugPauseInspectResult InspectResult;
        internal DebugPauseEvaluateResult EvaluateResult;
        internal DebugValueReadResult ValueReadResult;
        internal DebugValueWriteResult ValueWriteResult;
        internal BridgeCommandException Failure;
        internal double StartedMonotonicMs;
        internal double CompletedMonotonicMs;
        private volatile bool completed;

        internal DebugPauseOperation(string kind, string origin)
        {
            Kind = kind;
            Origin = origin ?? string.Empty;
        }

        /// <summary>操作是否已在暂停线程上返回；等待方只读该标记，不做阻塞等待。</summary>
        internal bool Completed { get { return completed; } }

        internal void Complete()
        {
            CompletedMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
            completed = true;
        }

        internal void Fail(BridgeCommandException failure)
        {
            Failure = failure;
            Complete();
        }
    }

    /// <summary>
    /// 一个已经停住的调用现场。site 只由 pause owner 创建与终结；
    /// 命中线程独占 mailbox 的服务权，任何其他线程只能入队有界请求或设置 resume 信号。
    ///
    /// 字段语义：
    /// - ThreadId/InvocationId/RuntimeId/DomainGeneration 是 pauseId 的绑定身份，resume/超时后不可复用。
    /// - Unbounded/DurationMs/DeadlineMonotonicMs 来自安装时冻结的 breakpoint 配置，命中路径不改写。
    /// - Invocation/Instance/Arguments/Binding 是命中线程上的真实帧，其他线程不得读取其中的用户对象。
    /// </summary>
    internal sealed class DebugPauseSite
    {
        internal readonly object Gate = new object();
        internal readonly ManualResetEventSlim Signal = new ManualResetEventSlim(false);
        internal readonly Queue<DebugPauseOperation> Waiting = new Queue<DebugPauseOperation>();

        internal string PauseId = string.Empty;
        internal int ThreadId;
        internal bool MainThread;
        internal long InvocationId;
        internal long Revision;
        internal string HookId = string.Empty;
        internal string TargetKey = string.Empty;
        internal string MethodIdentity = string.Empty;
        internal string Position = DebugPausePositions.Prefix;
        internal string RuntimeId = string.Empty;
        internal string DomainGeneration = string.Empty;
        internal string SessionId = string.Empty;
        internal string ResourceId = string.Empty;
        internal bool Unbounded;
        internal long DurationMs;
        internal double DeadlineMonotonicMs;
        internal string State = DebugPauseStates.Paused;
        internal string TerminalReason = string.Empty;
        internal double StartedMonotonicMs;
        internal double TerminalMonotonicMs;
        internal bool ResumeRequested;
        internal string ResumeReason = string.Empty;
        internal DebugPauseOperation Executing;
        internal long ServedOperations;
        internal long RefusedRequests;
        internal long SuppressedNestedHits;
        internal double LastServedMonotonicMs;

        // 命中线程上的真实帧；只在暂停线程上读取。
        internal IUnityCliHookInvocation Invocation;
        internal object Instance;
        internal object[] Arguments;
        internal MethodHookBinding Binding;
        internal int EmittedOffset;

        internal bool IsPaused
        {
            get { lock (Gate) return string.Equals(State, DebugPauseStates.Paused, StringComparison.Ordinal); }
        }

        /// <summary>现场是否已经离开 paused；清理委托据此决定是否还需要报告 pending。</summary>
        internal bool IsTerminal
        {
            get { lock (Gate) return DebugPauseStates.IsTerminal(State); }
        }

        internal long WaitingCount
        {
            get { lock (Gate) return Waiting.Count; }
        }

        /// <summary>有限暂停的剩余时间；unbounded 与已终结现场返回 0。</summary>
        internal double RemainingMs
        {
            get
            {
                if (Unbounded) return 0d;
                var remaining = DeadlineMonotonicMs - DebugEventClocks.MonotonicMilliseconds;
                return remaining < 0d ? 0d : remaining;
            }
        }

        /// <summary>有效时间边界的稳定表述；存在 session deadline 时必须报告该边界而不是"无限"。</summary>
        internal string EffectiveBound
        {
            get { return Unbounded ? "unbounded" : "bounded"; }
        }
    }

    /// <summary>
    /// 暂停现场 mailbox 的有界入队与终结规则。
    ///
    /// 规则（与 spec §10 冻结一致）：
    /// - 每现场最多 MaxPauseMailboxWaiting 个等待请求与 MaxPauseMailboxExecuting 个正在执行的操作；
    /// - resume 信号是独立标记，绝不排在等待请求之后；
    /// - 终结时必须答复所有仍在排队的请求（stale 失败），否则等待方会挂到自己的请求超时。
    /// </summary>
    internal static class DebugPauseMailbox
    {
        /// <summary>有界入队；resume/终结后不再接受新请求。</summary>
        internal static BridgeCommandException TryEnqueue(DebugPauseSite site, DebugPauseOperation operation, out bool enqueued)
        {
            enqueued = false;
            lock (site.Gate)
            {
                if (!string.Equals(site.State, DebugPauseStates.Paused, StringComparison.Ordinal) || site.ResumeRequested)
                    return Stale(site, "the pause site already left the paused state");
                if (site.Waiting.Count >= DebugLimits.MaxPauseMailboxWaiting)
                {
                    site.RefusedRequests++;
                    return new BridgeCommandException(
                        "E_DEBUG_LIMIT_EXCEEDED",
                        "The pause site mailbox already holds the declared number of waiting requests, so this request was refused before any effect.",
                        new Dictionary<string, object>
                        {
                            { "limit", "pause-mailbox-waiting" },
                            { "pauseId", site.PauseId },
                            { "used", (long)site.Waiting.Count },
                            { "max", (long)DebugLimits.MaxPauseMailboxWaiting },
                            { "executing", site.Executing == null ? string.Empty : site.Executing.Kind }
                        },
                        new[]
                        {
                            "Wait for the site's current requests to drain, or read debug.pause-list to see the site state.",
                            "Resume the site with debug.pause-resume when the on-site work is no longer needed."
                        });
                }
                site.Waiting.Enqueue(operation);
                enqueued = true;
            }
            site.Signal.Set();
            return null;
        }

        /// <summary>终结现场并把仍在排队的请求标记为 stale，使任何等待方都能立即得到确定结论。</summary>
        internal static void Terminalize(DebugPauseSite site, string state, string reason)
        {
            List<DebugPauseOperation> stranded = null;
            lock (site.Gate)
            {
                if (!DebugPauseStates.IsTerminal(site.State))
                {
                    site.State = state;
                    site.TerminalReason = reason ?? string.Empty;
                    site.TerminalMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
                }
                if (site.Waiting.Count > 0)
                {
                    stranded = new List<DebugPauseOperation>(site.Waiting);
                    site.Waiting.Clear();
                }
            }
            if (stranded != null)
            {
                foreach (var operation in stranded)
                {
                    Cancel(operation);
                    operation.Fail(Stale(site, "the pause site left the paused state before the request was served"));
                }
            }
            site.Signal.Set();
        }

        /// <summary>取消一个现场操作的进行中工作；只取消框架自己的现场工作，不抢占用户同步代码。</summary>
        internal static void Cancel(DebugPauseOperation operation)
        {
            if (operation == null) return;
            try { operation.Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* 已经终结的操作无需再取消 */ }
        }

        /// <summary>现场过期/终结后的稳定错误；不复活现场。</summary>
        internal static BridgeCommandException Stale(DebugPauseSite site, string detail)
        {
            return new BridgeCommandException(
                "E_DEBUG_PAUSE_STALE",
                "This pauseId is not an active pause site: " + detail + ".",
                new Dictionary<string, object>
                {
                    { "pauseId", site == null ? string.Empty : site.PauseId },
                    { "state", site == null ? "unknown" : site.State },
                    { "reason", site == null ? string.Empty : site.TerminalReason },
                    { "resumedAtMonotonicMs", site == null ? 0d : site.TerminalMonotonicMs }
                },
                new[]
                {
                    "Run tools run debug.pause-list to read the sites that are paused right now.",
                    "Install the hook again if a new pause is required; a resumed pauseId is never reused."
                });
        }

        internal static BridgeCommandException Unknown(string pauseId)
        {
            return new BridgeCommandException(
                "E_DEBUG_PAUSE_STALE",
                "No pause site with that pauseId was ever published in the current Editor session and domain generation.",
                new Dictionary<string, object>
                {
                    { "pauseId", pauseId ?? string.Empty },
                    { "state", "unknown" },
                    { "reason", "unknown-pause-id" },
                    { "activeSites", (long)DebugPauseHost.ActiveSiteCount }
                },
                new[]
                {
                    "Run tools run debug.pause-list to read the currently published pause sites.",
                    "Run tools run debug.pause-wait to wait for a matching site before operating on it."
                });
        }

        /// <summary>有限超时是否已经到达；使用单调 wall time，不受 Unity pause、timeScale 或 CLI 请求超时影响。</summary>
        internal static bool DeadlineReached(DebugPauseSite site)
        {
            return !site.Unbounded && DebugEventClocks.MonotonicMilliseconds >= site.DeadlineMonotonicMs;
        }

        /// <summary>把毫秒格式化为稳定字符串，供 describe 与错误 details 复用。</summary>
        internal static string Milliseconds(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 暂停现场在命中线程上可见的调用 ABI。
    ///
    /// pause.evaluate 提交的源码与 hook 回调共用同一编译形状；为了让显式调试代码能修改本次调用边界，
    /// 本类只暴露命中线程上当前正在服务的调用身份。它不提供读取其他帧 locals 的能力，
    /// 也不允许从其他线程取得现场：<see cref="Current"/> 只对正在执行现场操作的线程非 null。
    /// </summary>
    public static class UnityCliPauseContext
    {
        [ThreadStatic]
        private static IUnityCliHookInvocation current;

        /// <summary>当前暂停现场调用；不在暂停服务中执行时返回 null。</summary>
        public static IUnityCliHookInvocation Current { get { return current; } }

        internal static void Enter(IUnityCliHookInvocation invocation) { current = invocation; }

        internal static void Exit() { current = null; }
    }
}
#endif
