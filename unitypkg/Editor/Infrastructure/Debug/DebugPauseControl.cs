#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 当前仍停住的主线程暂停现场的不可变投影。
    ///
    /// 它是 health、editor.state 快照与拒绝 nextActions 共用的唯一来源；
    /// 只由内存中的现场注册表推导，不读取任何文件，也不引用主线程缓存。
    /// </summary>
    internal sealed class DebugMainThreadPauseView
    {
        internal const string BlockerType = "debug-pause";
        internal const string BlockerSource = "unity-debug-pause";

        internal string PauseId = string.Empty;
        internal string State = string.Empty;
        internal int ThreadId;
        internal long PublishedSequence;
        internal string MethodIdentity = string.Empty;
        internal string TargetKey = string.Empty;
        internal string Position = string.Empty;
        internal string SessionId = string.Empty;
        internal string TimeoutKind = string.Empty;
        internal long TimeoutDurationMs;
        internal long RemainingMs;
        internal double PausedForMs;
        internal long ServedOperations;
        internal long RefusedRequests;
        internal string EditorSessionId = string.Empty;

        /// <summary>health 用的有界投影；只包含公开事实，不含内部路由字段。</summary>
        internal Dictionary<string, object> ToData()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "pauseId", PauseId },
                { "state", State },
                { "threadId", (long)ThreadId },
                { "mainThread", true },
                { "publishedSequence", PublishedSequence },
                { "methodIdentity", MethodIdentity },
                { "targetKey", TargetKey },
                { "position", Position },
                { "sessionId", SessionId },
                { "timeoutKind", TimeoutKind },
                { "timeoutDurationMs", TimeoutDurationMs },
                { "remainingMs", RemainingMs },
                { "pausedForMs", PausedForMs },
                { "servedOperations", ServedOperations },
                { "refusedRequests", RefusedRequests },
                { "editorSessionId", EditorSessionId }
            };
        }

        /// <summary>公开 blocker DTO；字段与公开契约的 debug-pause 分支一致。</summary>
        internal Dictionary<string, object> ToBlockerData()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "type", BlockerType },
                { "pauseId", PauseId },
                { "threadId", (long)ThreadId },
                { "mainThread", true },
                { "methodIdentity", MethodIdentity },
                { "targetKey", TargetKey },
                { "position", Position },
                { "sessionId", SessionId },
                { "timeoutKind", TimeoutKind },
                { "remainingMs", RemainingMs },
                { "pausedForMs", PausedForMs },
                { "resumeTool", DebugPauseControl.ResumeTool },
                { "infoTool", DebugPauseControl.ListTool },
                { "requiresUserAction", false },
                { "source", BlockerSource },
                { "confidence", "exact" }
            };
        }
    }

    /// <summary>
    /// 主线程暂停期间唯一可用的控制通道。
    ///
    /// 存在的唯一原因：主线程一旦停在自己的断点上，正常业务队列就再也取不到执行权，
    /// 因此读取型能力、现场操作与解除路径必须由不经过该队列的通道服务，其余业务调用必须在任何 effect 前被拒绝。
    ///
    /// 规则：
    /// - 可达集合是冻结的名单：只有逐条列出的能力才能在暂停期间绕过主线程队列；
    /// - 唯一的条件放宽是 stop 一个 session，且该 session 必须是持有当前暂停现场的 owner：
    ///   它关入口并发出 resume/取消信号，需要主线程的清理条目先报告 pending；
    /// - 通道必须在任何 pause 资源安装之前发布（<see cref="Activate"/>），否则现场不安装；
    /// - 服务在发起请求的 HTTP 线程上完成，不排入主线程队列，不注册 update，也不驱动任何 Unity 主线程工作；
    /// - 目录能力始终读取同一个 live publication；没有 publication 时如实报告不可用，不回退到静态清单；
    /// - 拒绝只描述实际状态与下一步：调用公开 Tool 即授权，这里不做额外确认或意图复核。
    /// </summary>
    internal static class DebugPauseControl
    {
        internal const string ResumeTool = "debug.pause-resume";
        internal const string ListTool = "debug.pause-list";
        internal const string StopTool = "debug.session-stop";
        internal const string RejectionCode = "E_DEBUG_MAIN_THREAD_PAUSED";

        private static volatile bool activated;

        /// <summary>本 domain generation 已发布控制通道；发布前不得安装任何 pause 资源。</summary>
        internal static bool Available { get { return activated; } }

        /// <summary>启动路径发布通道；必须在监听开始接受请求之前调用。</summary>
        internal static void Activate() { activated = true; }

        /// <summary>域重载与定向测试复位；机制状态只对本次启动有效。</summary>
        internal static void Reset() { activated = false; }

        /// <summary>冻结的 off-main 可达命令：全部是只读发布快照的 registry 命令。</summary>
        private static readonly HashSet<string> OffMainCommands = new HashSet<string>(StringComparer.Ordinal)
        {
            "catalog.editor.health",
            "catalog.editor.groups",
            "catalog.editor.summary",
            "catalog.editor.describe",
            "catalog.editor.list"
        };

        /// <summary>冻结的 off-main 可达 Tool：现场读取、现场操作、解除路径与发布状态读取。</summary>
        private static readonly HashSet<string> OffMainTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "debug.pause-list",
            "debug.pause-wait",
            "debug.pause-inspect",
            "debug.pause-evaluate",
            "debug.pause-resume",
            "debug.events-read",
            "debug.thread-list",
            "debug.session-info",
            "debug.session-list",
            "debug.program-info",
            "debug.program-list",
            "debug.hook-info",
            "debug.hook-list"
        };

        /// <summary>只有显式命名 pauseId 的值操作才路由到该现场；普通值操作仍走正常路径。</summary>
        private static readonly HashSet<string> PauseScopedTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "debug.value-read",
            "debug.value-write"
        };

        /// <summary>当前是否发布了一个仍停住的主线程暂停现场。</summary>
        internal static bool TryGetMainThreadPause(out DebugMainThreadPauseView view)
        {
            view = DebugPauseHost.ReadMainThreadPause();
            return view != null;
        }

        /// <summary>
        /// 暂停期间一个真实请求是否可达，并给出从该请求推导出的内层事实。
        ///
        /// 这是控制通道唯一的可达性入口：HTTP 闸门与定向测试都从真实请求走这里。
        /// CLI 提交的外层 payload 是 { name, input }，因此内层 Tool 名只来自外层 name 字段，
        /// 内层业务参数只来自外层 input 字段，二者都不从对方的内容里猜测。
        /// </summary>
        internal static bool IsReachable(UnityCli.Protocol.BridgeRequest request, out string nestedTool, out string nestedInputJson)
        {
            nestedTool = NestedToolName(request);
            nestedInputJson = NestedInputJson(request);
            return IsReachable(request == null ? null : request.Command, nestedTool, nestedInputJson);
        }

        /// <summary>
        /// catalog.editor.run 的内层 Tool 名：外层 name 字段的字符串值。
        /// 其他命令、缺失的 name 或非字符串 name 都表示没有内层能力，按不可达处理，
        /// 由统一拒绝路径给出实际状态与下一步。
        /// </summary>
        internal static string NestedToolName(UnityCli.Protocol.BridgeRequest request)
        {
            if (request == null || !string.Equals(request.Command, "catalog.editor.run", StringComparison.Ordinal)) return string.Empty;
            var outer = request.Input as IDictionary<string, object>;
            object name;
            if (outer == null || !outer.TryGetValue("name", out name)) return string.Empty;
            var text = name as string;
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text;
        }

        /// <summary>catalog.editor.run 的外层 input 字段，即内层业务参数 JSON；只有显式 pauseId 判定读取它。</summary>
        internal static string NestedInputJson(UnityCli.Protocol.BridgeRequest request)
        {
            if (request == null || !string.Equals(request.Command, "catalog.editor.run", StringComparison.Ordinal)) return null;
            return request.RawJsonArg("input");
        }

        /// <summary>暂停期间某个命令是否可达；判定只使用冻结名单、已推导的内层事实与当前发布的暂停现场。</summary>
        internal static bool IsReachable(string command, string nestedTool, string nestedInputJson)
        {
            if (OffMainCommands.Contains(command)) return true;
            if (string.Equals(command, "internal.editor.state.snapshot", StringComparison.Ordinal)) return true;
            if (!string.Equals(command, "catalog.editor.run", StringComparison.Ordinal)) return false;
            if (OffMainTools.Contains(nestedTool)) return true;
            if (string.Equals(nestedTool, StopTool, StringComparison.Ordinal)) return OwnsHeldMainThreadPause(nestedInputJson);
            return PauseScopedTools.Contains(nestedTool) && NamesPause(nestedInputJson);
        }

        /// <summary>
        /// 暂停期间的精确放宽：只有 stop 一个 session、且该 session 正是持有当前暂停现场的 owner 时才可达。
        /// 未命名与命名了其它 session 的 stop 都在 effect 之前被拒，因此这里的放宽不会把普通变更放行。
        /// </summary>
        private static bool OwnsHeldMainThreadPause(string nestedInputJson)
        {
            DebugMainThreadPauseView held;
            if (!TryGetMainThreadPause(out held)) return false;
            var sessionId = SessionIdOf(nestedInputJson);
            return !string.IsNullOrEmpty(sessionId) && string.Equals(sessionId, held.SessionId, StringComparison.Ordinal);
        }

        /// <summary>内层业务参数里显式命名的 sessionId；缺失、非字符串、空串或不可解析都表示没有命名 owner。</summary>
        private static string SessionIdOf(string inputJson)
        {
            if (string.IsNullOrWhiteSpace(inputJson)) return null;
            try
            {
                var value = UnityCliSerializer.Parse(inputJson) as IDictionary<string, object>;
                object sessionId;
                if (value == null || !value.TryGetValue("sessionId", out sessionId)) return null;
                return Convert.ToString(sessionId, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>拒绝信息只描述实际状态与下一步，不把暂停伪装成编译中或排队中。</summary>
        internal static BridgeCommandException MainThreadPaused(string command, string nestedTool, DebugMainThreadPauseView pause)
        {
            var details = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "command", command ?? string.Empty },
                { "toolName", nestedTool ?? string.Empty },
                { "pauseId", pause == null ? string.Empty : pause.PauseId },
                { "threadId", (long)(pause == null ? 0 : pause.ThreadId) },
                { "mainThread", true },
                { "timeoutKind", pause == null ? string.Empty : pause.TimeoutKind },
                { "remainingMs", (long)(pause == null ? 0 : pause.RemainingMs) },
                { "pauseTool", ListTool },
                { "resumeTool", ResumeTool }
            };
            return new BridgeCommandException(
                RejectionCode,
                "The Unity Editor main thread is deliberately paused at a debug hook breakpoint, so this request was refused before any effect instead of waiting behind the paused main-thread queue.",
                details,
                NextActions(pause));
        }

        /// <summary>拒绝的可执行路由：信息、解除，以及持有该现场的 owner 自己的 stop。</summary>
        private static string[] NextActions(DebugMainThreadPauseView pause)
        {
            var actions = new List<string>
            {
                "Run tools run " + ListTool + " to read the pause site that is holding the main thread.",
                pause == null
                    ? "Run tools run " + ResumeTool + " with the pauseId to release the site and let the main thread continue."
                    : "Run tools run " + ResumeTool + " with pauseId " + pause.PauseId + " to release the site and let the main thread continue."
            };
            if (pause != null && !string.IsNullOrEmpty(pause.SessionId))
                actions.Add("Run tools run " + StopTool + " with sessionId " + pause.SessionId + " to close that owner's future dispatch and send it the resume signal; cleanup that needs the main thread is reported as pending and completes when the same stop is repeated after the thread resumes.");
            actions.Add("While the site is held you can still read debug.events-read, debug.hook-list, debug.pause-inspect, debug.session-list, debug.program-list and debug.thread-list, then release the site before submitting business mutations again.");
            return actions.ToArray();
        }

        /// <summary>
        /// 在发起请求的 HTTP 线程上直接服务一个可达请求。
        /// 返回 false 表示本次没有服务任何内容，调用方必须走统一拒绝路径。
        /// </summary>
        internal static bool TryServe(UnityCli.Protocol.BridgeRequest request, string nestedTool, DebugMainThreadPauseView pause, out string envelope)
        {
            envelope = null;
            if (request == null) return false;
            try
            {
                if (string.Equals(request.Command, "internal.editor.state.snapshot", StringComparison.Ordinal))
                {
                    envelope = UnityCliBridge.SerializeOffMainSuccess(request.RequestId, OffMainEditorStateSnapshot(pause));
                    return true;
                }
                if (OffMainCommands.Contains(request.Command))
                {
                    // 目录能力只读同一个 live publication；没有 publication 时由 registry 自己报不可用。
                    var catalogBudgetMs = (int)Math.Min(WaitBudgetMs(UnityCliBridge.OffMainDeadlineMs(request)), int.MaxValue);
                    envelope = UnityCliBridge.SerializeOffMainSuccess(request.RequestId, UnityCliBridge.ExecuteRegistryCommandOffMain(request, true, catalogBudgetMs));
                    return true;
                }
                if (!string.Equals(request.Command, "catalog.editor.run", StringComparison.Ordinal)) return false;
                var inputJson = request.RawJsonArg("input") ?? "{}";
                var data = ServeTool(request, nestedTool, inputJson);
                envelope = UnityCliBridge.SerializeOffMainSuccess(request.RequestId, data);
                return true;
            }
            catch (BridgeCommandException failure)
            {
                envelope = UnityCliBridge.SerializeOffMainFailure(request.RequestId, failure);
                return true;
            }
            catch (Exception exception)
            {
                // 控制通道的答复必须始终与请求相关。未预期的异常若直接冒泡，HTTP 处理器会死在写回复之前，
                // 调用方只会看到空 requestId 的错配响应，既无法判断效果也无法恢复。
                // 只报类型名无法定位现场，因此归因里带上同一份有界异常消息；它是诊断文本，不是新的公开错误码。
                var failure = exception.GetType().Name + ": "
                    + UnityCliBridge.BoundedText(exception.Message ?? string.Empty, DebugLimits.MaxDiagnosticTextBytes);
                envelope = UnityCliBridge.SerializeOffMainFailure(request.RequestId, new BridgeCommandException(
                    "E_BRIDGE_INTERNAL_ERROR",
                    "The pause control channel failed while serving '" + (string.IsNullOrEmpty(nestedTool) ? request.Command : nestedTool) + "': " + failure + ".",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "command", request.Command ?? string.Empty },
                        { "toolName", nestedTool ?? string.Empty },
                        { "pauseId", pause == null ? string.Empty : pause.PauseId },
                        { "failure", failure }
                    },
                    new[]
                    {
                        "Run tools run " + ListTool + " to read the site state that is holding the main thread.",
                        "Run tools run " + ResumeTool + " with the pauseId to release the site, then submit this request again."
                    }));
                return true;
            }
        }

        /// <summary>
        /// 暂停发布之前就已经进入主线程队列的请求的裁决。
        ///
        /// 存在的唯一原因：可达性闸门只在请求进入 Bridge 的那一刻判定，而请求可能在声明命中之前就已入队
        /// （触发调用还在编译要执行的代码）。此后主线程停在自己的断点上就不再推进队列，这个请求永远等不到开始，
        /// 只能等现场释放后才被执行；对有限暂停而言现场此时已经按自己的边界自动解除，于是调用方看到 total:0、
        /// skippedSites:0，误以为声明的暂停从未发生，也无法在窗口内 inspect 或 resume。
        ///
        /// 因此已经在队列里等待的请求必须在暂停发布之后用同一冻结名单重新判定一次：可达请求转入控制通道、
        /// 在发起请求的线程上服务，并取消尚未开始的队列项，使主线程绝不再执行它；不可达请求保持既有排队语义。
        /// 返回 true 表示本次已经给出终态答复（envelope 非空）。
        /// </summary>
        internal static bool TryServeQueuedRequest(UnityCli.Protocol.BridgeRequest request, UnityCliBridge.BridgeWorkItem item, out string envelope)
        {
            envelope = null;
            if (request == null || item == null) return false;
            DebugMainThreadPauseView pause;
            if (!TryGetMainThreadPause(out pause)) return false;
            string nestedTool, nestedInput;
            if (!IsReachable(request, out nestedTool, out nestedInput)) return false;
            // 先取消尚未开始的队列项：只有取消成功才证明本次请求不会同时在主线程上执行。
            if (!item.CancelBeforeStart()) return false;
            if (!TryServe(request, nestedTool, pause, out envelope))
            {
                // 名单与分派同源；走到这里说明二者脱节，按不可达给出稳定拒绝而不是静默取消。
                envelope = UnityCliBridge.SerializeOffMainFailure(request.RequestId, MainThreadPaused(request.Command, nestedTool, pause));
            }
            // 队列项保留同一答复：主线程在自己的 tick 上跳过它并按这份答复收敛，不会重复执行。
            item.Response = envelope;
            return true;
        }

        /// <summary>
        /// 可达 Tool 的 off-main 分派：复用同一 descriptor 校验、同一 owner 核心、同一终态构造点与同一失败路径。
        ///
        /// 成功响应必须是正常 catalog.editor.run 执行路径的同一终态形状：公开 CLI 把该命令的成功响应投影为
        /// data.result，缺少该包装时一个已经生效的响应会被判成 malformed terminal，
        /// 于是调用方看到 "响应非法" 而现场其实已经被改变。
        /// </summary>
        private static object ServeTool(UnityCli.Protocol.BridgeRequest request, string tool, string inputJson)
        {
            // 整个分派都在主线程冻结的 mode 缓存下执行：暂停线程停止推进后，后台线程读取 Unity 的
            // isPlaying 一类主线程专属属性会直接抛异常，从而在服务任何可达 Tool 之前就令请求失去答复。
            return UnityCliBridge.ServeOffMainTool(() =>
            {
                var definition = UnityCliBridge.RequireOffMainTool(tool);
                UnityCliBridge.RequireOffMainToolInput(definition, inputJson);
                return UnityCliBridge.OffMainToolTerminalData(request, definition, ServeToolResult(request, tool, inputJson));
            });
        }

        /// <summary>可达 Tool 的 off-main 业务结果；调用方必须已经进入主线程冻结的 mode 缓存。</summary>
        private static object ServeToolResult(UnityCli.Protocol.BridgeRequest request, string tool, string inputJson)
        {
            var requestId = UnityCliBridge.OffMainRequestId(request);
            var deadlineMs = UnityCliBridge.OffMainDeadlineMs(request);
            switch (tool)
            {
                case "debug.pause-list":
                    return DebugPauseHost.List(UnityCliBridge.DeserializeOffMain<DebugPauseListRequest>(inputJson), UnityCliBridge.EditorSessionId());
                case "debug.pause-wait":
                    return WaitForSite(UnityCliBridge.DeserializeOffMain<DebugPauseWaitRequest>(inputJson), deadlineMs);
                case "debug.pause-inspect":
                    return ServeOperation(requestId, tool, EnqueueInspect(inputJson), deadlineMs);
                case "debug.pause-evaluate":
                    return ServeOperation(requestId, tool, EnqueueEvaluate(inputJson), deadlineMs);
                case "debug.pause-resume":
                    return ServeResume(requestId, inputJson, deadlineMs);
                case "debug.events-read":
                    return DebugEventStore.ReadEvents(UnityCliBridge.DeserializeOffMain<DebugEventsRequest>(inputJson));
                case "debug.thread-list":
                    return DebugThreads.List(UnityCliBridge.DeserializeOffMain<DebugThreadListRequest>(inputJson), UnityCliBridge.EditorSessionId());
                case "debug.session-info":
                    return DebugSessionHost.Info(UnityCliBridge.DeserializeOffMain<DebugSessionInfoRequest>(inputJson));
                case "debug.session-list":
                    return DebugSessionHost.List(UnityCliBridge.DeserializeOffMain<DebugSessionListRequest>(inputJson));
                case "debug.program-info":
                    return DebugProgramHost.Info(UnityCliBridge.DeserializeOffMain<DebugProgramInfoRequest>(inputJson));
                case "debug.program-list":
                    return DebugProgramHost.List(UnityCliBridge.DeserializeOffMain<DebugProgramListRequest>(inputJson));
                case "debug.hook-info":
                    return MethodHookHost.Info(UnityCliBridge.DeserializeOffMain<MethodHookInfoRequest>(inputJson));
                case "debug.hook-list":
                    return MethodHookHost.List(UnityCliBridge.DeserializeOffMain<MethodHookListRequest>(inputJson));
                case "debug.session-stop":
                    return ServeStop(inputJson);
                case "debug.value-read":
                    return ServeOperation(requestId, tool, EnqueueValueRead(inputJson), deadlineMs);
                case "debug.value-write":
                    return ServeOperation(requestId, tool, EnqueueValueWrite(inputJson), deadlineMs);
                default:
                    // 名单与分派必须同源；走到这里说明可达判定与实现脱节，按不可达拒绝而不是静默降级。
                    throw new BridgeCommandException(
                        "E_TOOL_NOT_FOUND",
                        "Tool " + tool + " is not served by the pause control channel.",
                        new Dictionary<string, object> { { "toolName", tool } },
                        new[] { "Run tools run " + ListTool + " to read the pause sites, then release the site before submitting this Tool." });
            }
        }

        /// <summary>
        /// 暂停期间的 session stop：只允许停止持有当前暂停现场的 session。
        ///
        /// 可达性与本判定同源；二者脱节时按统一拒绝答复，绝不静默停止另一个 session 或服务普通变更。
        /// 服务复用 session owner 的同一 stop 入口与同一报告，只把需要主线程的清理条目推迟为 pending。
        /// </summary>
        private static object ServeStop(string inputJson)
        {
            DebugMainThreadPauseView held;
            TryGetMainThreadPause(out held);
            if (!OwnsHeldMainThreadPause(inputJson)) throw MainThreadPaused("catalog.editor.run", StopTool, held);
            return DebugSessionHost.StopFromPauseControl(UnityCliBridge.DeserializeOffMain<DebugSessionStopRequest>(inputJson));
        }

        /// <summary>暂停期间按精确 pauseId 或筛选条件等待出现匹配现场；等不到时如实报告未出现。</summary>
        private static DebugPauseWaitResult WaitForSite(DebugPauseWaitRequest request, long deadlineMs)
        {
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var started = DebugEventClocks.MonotonicMilliseconds;
            if (request != null && !string.IsNullOrEmpty(request.PauseId))
            {
                // 精确 pauseId：已结束或从未发布时立即给出稳定 E_DEBUG_PAUSE_STALE，不伪装成尚未出现。
                return WaitResult(DebugPauseHost.FindSite(request.PauseId), started, editorSessionId);
            }
            var matched = DebugPauseHost.FindMatch(request);
            if (matched == null && request != null && request.WaitMs != null && request.WaitMs.Value > 0)
            {
                // 主线程停住时框架 wait scheduler 不会 tick，因此这里用同一有界轮询重复读取现场注册表；
                // 等待窗口仍然只由声明的上限与本次请求的剩余预算约束，且永不解除现场。
                var bounded = Math.Min(request.WaitMs.Value, WaitBudgetMs(deadlineMs));
                var deadline = DebugEventClocks.MonotonicMilliseconds + bounded;
                while (matched == null && DebugEventClocks.MonotonicMilliseconds < deadline)
                {
                    DebugPauseControlWait.PollSleep();
                    matched = DebugPauseHost.FindMatch(request);
                }
            }
            return WaitResult(matched, started, editorSessionId);
        }

        private static DebugPauseWaitResult WaitResult(DebugPauseSite site, double started, string editorSessionId)
        {
            var waited = DebugEventClocks.MonotonicMilliseconds - started;
            return new DebugPauseWaitResult
            {
                Matched = site != null,
                Site = site == null ? null : DebugPauseHost.View(site),
                WaitedMilliseconds = waited < 0d ? 0d : waited,
                ResumedByWait = false,
                EditorSessionId = editorSessionId,
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }

        private static DebugPauseOperation EnqueueInspect(string inputJson)
        {
            var request = UnityCliBridge.DeserializeOffMain<DebugPauseInspectRequest>(inputJson);
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            return DebugPauseHost.EnqueueInspect(site, request, "control:debug.pause-inspect");
        }

        private static DebugPauseOperation EnqueueEvaluate(string inputJson)
        {
            var request = UnityCliBridge.DeserializeOffMain<DebugPauseEvaluateRequest>(inputJson);
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            var hasCode = request != null && (!string.IsNullOrWhiteSpace(request.SourceText) || !string.IsNullOrWhiteSpace(request.Code));
            var hasEntry = request != null && request.ProgramEntry != null && !string.IsNullOrWhiteSpace(request.ProgramEntry.ProgramId);
            if (hasCode == hasEntry)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field evaluate requires exactly one of sourceText/code or programEntry.",
                    new Dictionary<string, object> { { "path", "$.programEntry" }, { "hasCode", hasCode }, { "hasProgramEntry", hasEntry } },
                    new[] { "Submit explicit debug code with sourceText, or name an installed program entry with programEntry." });
            return DebugPauseHost.EnqueueEvaluate(site, request, "control:debug.pause-evaluate");
        }

        private static DebugPauseOperation EnqueueValueRead(string inputJson)
        {
            var request = UnityCliBridge.DeserializeOffMain<DebugValueReadRequest>(inputJson);
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            return DebugPauseHost.EnqueueValueRead(site, request, "control:debug.value-read");
        }

        private static DebugPauseOperation EnqueueValueWrite(string inputJson)
        {
            var request = UnityCliBridge.DeserializeOffMain<DebugValueWriteRequest>(inputJson);
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            return DebugPauseHost.EnqueueValueWrite(site, request, "control:debug.value-write");
        }

        /// <summary>
        /// 现场操作的 off-main 等待：不依赖主线程 tick，也不占用主线程。
        /// 只有真正在暂停线程上返回的操作才会终结；用户同步代码不返回时只能如实报告 pending。
        /// </summary>
        private static object ServeOperation(string requestId, string tool, DebugPauseOperation operation, long deadlineMs)
        {
            var waitMs = WaitBudgetMs(deadlineMs);
            var completed = DebugPauseControlWait.Await(operation, waitMs);
            switch (operation.Kind)
            {
                case DebugPauseOperationKinds.Evaluate:
                    if (operation.Completed && operation.Failure != null) throw operation.Failure;
                    if (operation.Completed) return operation.EvaluateResult;
                    return DebugPauseHost.PendingEvaluateResult(operation.Site, operation, operation.EvaluateRequest != null && operation.EvaluateRequest.ProgramEntry != null);
                case DebugPauseOperationKinds.Inspect:
                    if (operation.Failure != null) throw operation.Failure;
                    if (!completed) throw OperationNotServed(operation, waitMs);
                    return operation.InspectResult;
                case DebugPauseOperationKinds.ValueRead:
                    if (operation.Failure != null) throw operation.Failure;
                    if (!completed) throw OperationNotServed(operation, waitMs);
                    return operation.ValueReadResult;
                case DebugPauseOperationKinds.ValueWrite:
                    if (operation.Failure != null) throw operation.Failure;
                    if (!completed) throw OperationNotServed(operation, waitMs);
                    return operation.ValueWriteResult;
                default:
                    throw new BridgeCommandException("E_BRIDGE_INTERNAL_ERROR", "Unsupported on-site operation kind: " + operation.Kind + " for " + tool + ".");
            }
        }

        private static BridgeCommandException OperationNotServed(DebugPauseOperation operation, long waitMs)
        {
            return new BridgeCommandException(
                "E_EXECUTION_TIMEOUT",
                "The paused thread did not serve this request inside the bounded wait, so no value was returned and the site is still held.",
                new Dictionary<string, object>
                {
                    { "kind", operation.Kind },
                    { "waitMs", waitMs },
                    { "siteStillPaused", operation.Site == null || operation.Site.IsPaused }
                },
                new[]
                {
                    "Run tools run " + ListTool + " to read the current site state and its executing operation.",
                    "Run tools run " + ResumeTool + " when the on-site work is no longer needed; resume releases the wait and does not preempt running code."
                });
        }

        /// <summary>解除路径：先置解除信号，再在有界窗口内确认现场真的离开 paused。</summary>
        private static DebugPauseResumeResult ServeResume(string requestId, string inputJson, long deadlineMs)
        {
            var request = UnityCliBridge.DeserializeOffMain<DebugPauseResumeRequest>(inputJson);
            var site = DebugPauseHost.FindSite(request == null ? null : request.PauseId);
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? DebugPauseReleaseReasons.Resume : request.Reason;
            var waitMs = request.WaitMs != null ? request.WaitMs.Value : DebugLimits.PauseMaxWaitForReleaseMs;
            var bounded = Math.Max(0L, Math.Min(waitMs, WaitBudgetMs(deadlineMs)));
            DebugPauseHost.RequestResume(site.PauseId, reason);
            var deadline = DebugEventClocks.MonotonicMilliseconds + bounded;
            while (!site.IsTerminal && DebugEventClocks.MonotonicMilliseconds < deadline) DebugPauseControlWait.PollSleep();
            var released = site.IsTerminal;
            var view = DebugPauseHost.View(site);
            return new DebugPauseResumeResult
            {
                PauseId = site.PauseId,
                State = view.State,
                Released = released,
                ReleaseReason = released ? view.ReleaseReason : string.Empty,
                ActiveSitesRemaining = DebugPauseHost.ActiveSiteCount,
                ExecutingOperation = view.ExecutingOperation,
                ReplayedPriorOperations = false,
                Preemption = false,
                Note = released
                    ? "The site left the paused state; the current invocation continues from its own boundary and no earlier operation was replayed."
                    : "The site is still held: the paused thread is inside a synchronous operation that cannot be interrupted. It will leave the paused state as soon as that code returns, because the release signal is already set.",
                EditorSessionId = UnityCliBridge.EditorSessionId()
            };
        }

        /// <summary>本次 off-main 服务的有界等待上限；同时受声明的控制通道上限与请求自身剩余预算约束。</summary>
        private static long WaitBudgetMs(long deadlineMs)
        {
            var remaining = deadlineMs > 0L ? deadlineMs : DebugLimits.PauseControlMaxWaitMs;
            return Math.Max(1L, Math.Min(remaining, DebugLimits.PauseControlMaxWaitMs));
        }

        /// <summary>
        /// 主线程暂停期间的 editor.state 快照：字段一律 unknown，并只发布本次独立观察到的 debug-pause blocker。
        /// 主线程停住时无法读取 Unity 状态，因此这里如实报告 unknown，绝不回填上一次 publication 的字段。
        /// </summary>
        internal static Dictionary<string, object> OffMainEditorStateSnapshot(DebugMainThreadPauseView pause)
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var name in new[] { "compile", "import", "build", "playMode", "prefabStage", "activeScene", "runtime" })
                fields[name] = new Dictionary<string, object> { { "known", false } };
            var blockers = new List<object>();
            if (pause != null) blockers.Add(pause.ToBlockerData());
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "snapshot", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "schemaVersion", 1L },
                        { "observedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                        { "sourceEditorSessionId", UnityCliBridge.EditorSessionId() },
                        { "sourceListenerGeneration", UnityCliBridge.OffMainListenerGeneration() },
                        { "fields", fields }
                    }
                },
                { "progress", new List<object>() },
                { "blockers", blockers }
            };
        }

        /// <summary>内层业务参数是否显式命名 pauseId；只有命名了才把值操作路由到该现场。</summary>
        private static bool NamesPause(string inputJson)
        {
            if (string.IsNullOrWhiteSpace(inputJson)) return false;
            try
            {
                var value = UnityCliSerializer.Parse(inputJson) as IDictionary<string, object>;
                object pauseId;
                return value != null && value.TryGetValue("pauseId", out pauseId) && !string.IsNullOrWhiteSpace(Convert.ToString(pauseId, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// off-main 现场操作的有界等待。
    ///
    /// 它不经过框架 wait scheduler：主线程被暂停时该 scheduler 不会 tick，
    /// 因此这里只在声明的轮询间隔内读取操作完成标记，不在暂停线程上调度任何工作。
    /// </summary>
    internal static class DebugPauseControlWait
    {
        /// <summary>在不超过 budgetMs 的窗口内等待操作完成；返回操作是否已完成。</summary>
        internal static bool Await(DebugPauseOperation operation, long budgetMs)
        {
            if (operation == null) return false;
            if (operation.Completed) return true;
            var deadline = DebugEventClocks.MonotonicMilliseconds + Math.Max(0L, budgetMs);
            while (!operation.Completed)
            {
                if (DebugEventClocks.MonotonicMilliseconds >= deadline) return false;
                PollSleep();
            }
            return true;
        }

        /// <summary>有界的服务轮询间隔；与暂停线程自己的 mailbox 轮询共用同一声明点。</summary>
        internal static void PollSleep()
        {
            Thread.Sleep(DebugLimits.PauseControlPollMs);
        }
    }
}
#endif
