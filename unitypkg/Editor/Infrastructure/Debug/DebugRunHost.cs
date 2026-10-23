#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.SceneManagement;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// run 证据窗口域的唯一 owner。
    ///
    /// 职责：
    /// - 建立窗口、冻结必需检查与结束条件声明、在封闭时计算三态 verdict；
    /// - 把统一事件存储里真实存在的事件归约为 per-check 证明、首个反例与覆盖缺口；
    /// - 守住 run 摘要与详细证据预算，并在预算不足时淘汰最旧窗口的详细证据。
    ///
    /// 规则：
    /// - 事件真值只有 DebugEventStore 一份；窗口只保留边界、声明、判定与有界证据摘要。
    /// - begin 只建立定义与捕获起点，不执行准备、不注入输入、不调度任何程序。
    /// - 判定不把断言失败表达为错误码：ok 为 true，业务结论在 verdict 字段里。
    /// - 无法取得可信状态（未知 runId、session 不存在或非 open、预算拒绝）才返回 ok:false。
    /// - 本类不注册任何 update 回调，也不持有线程或文件句柄；窗口状态只对当前 domain 有效。
    /// </summary>
    internal static class DebugRunHost
    {
        private const int MaxPositionBytes = 128;
        private const int MaxKindBytes = 64;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DebugRunWindow> Windows = new Dictionary<string, DebugRunWindow>(StringComparer.Ordinal);
        private static readonly Dictionary<string, List<string>> RunsBySession = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, long> RunCounters = new Dictionary<string, long>(StringComparer.Ordinal);
        private static long detailedBytes;

        /// <summary>当前 domain 仍保留的窗口数量；测试与自检读取它，不参与判定。</summary>
        internal static int WindowCount { get { lock (Sync) return Windows.Count; } }

        /// <summary>详细证据全局账本的当前占用；供预算自检读取。</summary>
        internal static long DetailedBytes { get { lock (Sync) return detailedBytes; } }

        /// <summary>新窗口的详细证据预留份额；seal 时按真实序列化字节调整。</summary>
        private static long DetailReservation { get { return DebugLimits.MaxRunDetailBytesPerWindow; } }

        // ------------------------------------------------------------------
        // begin
        // ------------------------------------------------------------------

        /// <summary>
        /// begin 的 effect 前提炼：session、检查、覆盖要求、结束条件与范围全部在这里判定，
        /// 同时记录捕获起点与配置指纹；失败时不产生任何窗口。
        /// </summary>
        internal static DebugRunBeginPlan PrepareBegin(DebugRunBeginRequest request)
        {
            if (request == null) throw SchemaInvalid("request", "a debug.run-begin body", "missing");
            // session 身份用 session owner 的规范错误返回，不建立第二套 session 错误语义。
            var session = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = request.SessionId });
            if (!string.Equals(session.Session.State, "open", StringComparison.Ordinal))
            {
                throw new BridgeCommandException(
                    "E_DEBUG_SESSION_NOT_OPEN",
                    "The owning session is not open, so it does not accept a new evidence window.",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "sessionId", request.SessionId ?? string.Empty },
                        { "state", session.Session.State ?? string.Empty },
                        { "cleanupPending", session.Session.CleanupPending }
                    },
                    new[] { "Run tools run debug.session-info to read the session state, and create a new session for this investigation." });
            }

            var checks = ValidateChecks(request.RequiredChecks);
            var requirements = ValidateSourceRequirements(request.SourceRequirements);
            var conditions = ValidateConditions(request.EndConditions, request.MaxWindowMs);
            ValidateCheckCoverage(checks, requirements);

            var runtimeId = DebugSessionHost.RuntimeId();
            var domainGeneration = DebugSessionHost.DomainGeneration();
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var environment = EnvironmentSnapshot(request, runtimeId, domainGeneration, editorSessionId);
            List<string> configuration;
            string fingerprint;
            HashSet<string> sessionPrograms;
            var boundary = CaptureBoundaryAndQueue();
            var capture = boundary.View;
            lock (Sync)
            {
                configuration = ConfigurationSnapshot(checks, out fingerprint, out sessionPrograms);
            }
            return new DebugRunBeginPlan
            {
                SessionId = request.SessionId,
                Checks = checks,
                SourceRequirements = requirements,
                Conditions = conditions,
                AllowSegments = request.AllowSegments.HasValue && request.AllowSegments.Value,
                MaxWindowMs = request.MaxWindowMs,
                Capture = capture,
                RuntimeId = runtimeId,
                DomainGeneration = domainGeneration,
                EditorSessionId = editorSessionId,
                Environment = environment,
                DefinitionHash = DefinitionHash(request),
                Configuration = configuration,
                ConfigurationFingerprint = fingerprint,
                SessionPrograms = sessionPrograms,
                StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                StartedAtMonotonicMs = DebugEventClocks.MonotonicMilliseconds
            };
        }

        /// <summary>
        /// begin 的唯一 effect：预留详细证据预算，发布一个新窗口并返回它的 runId 与捕获起点。
        /// 这里不执行任何准备、输入或调度动作，只登记调用方声明的定义。
        /// </summary>
        internal static DebugRunBeginResult ApplyBegin(DebugRunBeginPlan plan)
        {
            if (plan == null) throw SchemaInvalid("plan", "a completed begin preparation", "missing");
            var capture = plan.Capture;
            var runtimeId = plan.RuntimeId;
            var domainGeneration = plan.DomainGeneration;
            var editorSessionId = plan.EditorSessionId;
            var configuration = plan.Configuration;
            var fingerprint = plan.ConfigurationFingerprint;
            var sessionPrograms = plan.SessionPrograms;
            var environment = plan.Environment;
            var definitionHash = plan.DefinitionHash;
            var startedAtUtc = plan.StartedAtUtc;
            var startedAtMonotonic = plan.StartedAtMonotonicMs;
            string runId;
            DebugRunWindow created;
            lock (Sync)
            {
                // 详细证据预算：先淘汰最旧的详细窗口，再淘汰最旧的摘要，最后核对全局字节账本。
                string rejectReason;
                if (!ReserveDetailBudgetLocked(plan.SessionId, out rejectReason))
                {
                    throw new BridgeCommandException(
                        "E_DEBUG_LIMIT_EXCEEDED",
                        "The detailed evidence budget cannot hold another window, so no window was created.",
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "limit", "run-detailed-evidence" },
                            { "used", detailedBytes },
                            { "max", DebugLimits.MaxDetailedRunBytesGlobal },
                            { "requested", DetailReservation },
                            { "reason", rejectReason ?? string.Empty }
                        },
                        new[] { "Run tools run debug.run-end on an open window, or read and release windows you no longer need." });
                }
                var counter = RunCounters.TryGetValue(plan.SessionId, out var current) ? current : 0L;
                counter++;
                RunCounters[plan.SessionId] = counter;
                runId = "run-" + counter.ToString(CultureInfo.InvariantCulture);
                var window = new DebugRunWindow
                {
                    RunId = runId,
                    SessionId = plan.SessionId,
                    State = UnityCliDebugRunStates.Open,
                    RuntimeId = runtimeId,
                    DomainGeneration = domainGeneration,
                    StartedAtUtc = startedAtUtc,
                    StartedAtMonotonicMs = startedAtMonotonic,
                    StartSequence = capture.BoundarySequence,
                    MaxWindowMs = plan.MaxWindowMs,
                    AllowSegments = plan.AllowSegments,
                    DefinitionHash = definitionHash,
                    Checks = plan.Checks,
                    SourceRequirements = plan.SourceRequirements,
                    Conditions = plan.Conditions,
                    ConfigurationFingerprint = fingerprint,
                    ConfigurationFingerprintAfter = fingerprint,
                    ConfigurationBefore = configuration,
                    ConfigurationAfter = new List<string>(configuration),
                    SessionPrograms = sessionPrograms,
                    Environment = environment,
                    DetailBytes = DetailReservation,
                    AppendFailuresAtStart = capture.AppendFailuresAtBoundary
                };
                foreach (var check in plan.Checks)
                {
                    if (check.HookId.Length != 0) window.ReferencedHooks.Add(check.HookId);
                }
                Windows[runId] = window;
                List<string> runs;
                if (!RunsBySession.TryGetValue(plan.SessionId, out runs))
                {
                    runs = new List<string>();
                    RunsBySession[plan.SessionId] = runs;
                }
                runs.Add(runId);
                detailedBytes += DetailReservation;
                created = window;
            }

            return new DebugRunBeginResult
            {
                RunId = runId,
                SessionId = plan.SessionId,
                State = UnityCliDebugRunStates.Open,
                Terminal = false,
                StartedAtUtc = startedAtUtc,
                StartedAtMonotonicMs = startedAtMonotonic,
                StartSequence = capture.BoundarySequence,
                MaxWindowMs = plan.MaxWindowMs,
                AllowSegments = plan.AllowSegments,
                DefinitionHash = definitionHash,
                Capture = capture,
                Environment = environment,
                Configuration = ConfigurationView(created, false),
                RequiredChecks = CheckDeclarations(plan.Checks, plan.SourceRequirements),
                SourceRequirements = SourceRequirementDeclarations(plan.SourceRequirements),
                EndConditions = ConditionDeclarations(plan.Conditions),
                Budget = BudgetView(plan.SessionId),
                EditorSessionId = editorSessionId,
                RuntimeId = runtimeId,
                DomainGeneration = domainGeneration,
                Note = "The window is open and records only what the unified event store actually retained. begin does not prepare the scene, does not inject inputs and does not schedule anything: pass the returned runId to debug.program-run when a program should run inside this window."
            };
        }

        // ------------------------------------------------------------------
        // end
        // ------------------------------------------------------------------

        /// <summary>封闭窗口并计算判定；已终止的窗口原样重读，绝不重开。</summary>
        internal static DebugRunEndResult End(DebugRunEndRequest request)
        {
            if (request == null) throw SchemaInvalid("request", "a debug.run-end body", "missing");
            DebugRunWindow window;
            lock (Sync)
            {
                if (!Windows.TryGetValue(request.RunId ?? string.Empty, out window)) throw MissingRun(request.RunId);
                RefreshLocked(window);
            }
            if (window.IsTerminal)
            {
                return new DebugRunEndResult
                {
                    Sealed = false,
                    AlreadySealed = true,
                    Report = BuildReport(window),
                    EditorSessionId = UnityCliBridge.EditorSessionId(),
                    DomainGeneration = DebugSessionHost.DomainGeneration(),
                    Note = "This window is already terminal; end reports the frozen verdict again and never reopens it."
                };
            }
            var boundaryFacts = CaptureBoundaryAndQueue();
            var capture = boundaryFacts.View;
            List<string> configurationAfter;
            string fingerprintAfter;
            HashSet<string> programs;
            lock (Sync)
            {
                window.State = UnityCliDebugRunStates.Sealed;
                window.TerminationReason = DebugRunEvidence.TerminationRunEnd;
                window.SealedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                window.SealedAtMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
                window.EndSequence = capture.BoundarySequence;
                window.OldestAvailableAtEnd = boundaryFacts.OldestAvailableSequence;
                window.AppendFailuresAtEnd = capture.AppendFailuresAtBoundary;
                window.Capture = capture;
                configurationAfter = ConfigurationSnapshot(window.Checks, out fingerprintAfter, out programs);
                window.ConfigurationAfter = configurationAfter;
                window.ConfigurationFingerprintAfter = fingerprintAfter;
                foreach (var program in programs) window.SessionPrograms.Add(program);
            }
            var report = BuildReport(window);
            lock (Sync)
            {
                // 判定与有界证据在封闭时刻冻结：sealed run 的证据由此有界保留，之后只按 runId 读取，
                // 不再依赖共享事件环是否仍保留同一区间。详细证据按真实序列化字节记账。
                if (window.FrozenReport == null) window.FrozenReport = report;
                var actual = EstimateDetailBytes(report);
                detailedBytes += actual - window.DetailBytes;
                if (detailedBytes < 0L) detailedBytes = 0L;
                window.DetailBytes = actual;
                report = window.FrozenReport;
            }
            return new DebugRunEndResult
            {
                Sealed = true,
                AlreadySealed = false,
                Report = report,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                Note = "The window is sealed. Sealing closes the evidence, it does not stop a running program: read debug.program-info and use debug.program-stop when that run must also end."
            };
        }

        // ------------------------------------------------------------------
        // wait / info / list
        // ------------------------------------------------------------------

        /// <summary>只观察一个窗口直到终止或有界等待到期；不重新执行、不停止任何资源。</summary>
        internal static async ValueTask<DebugRunWaitResult> WaitAsync(DebugRunWaitRequest request, IUnityCliWaiter waiter, CancellationToken ct)
        {
            if (request == null) throw SchemaInvalid("request", "a debug.run-wait body", "missing");
            DebugRunWindow window;
            lock (Sync)
            {
                if (!Windows.TryGetValue(request.RunId ?? string.Empty, out window)) throw MissingRun(request.RunId);
                RefreshLocked(window);
            }
            var waitMs = request.WaitMs ?? 0L;
            var deadline = DebugEventClocks.MonotonicMilliseconds + waitMs;
            var waited = false;
            while (!window.IsTerminal && DebugEventClocks.MonotonicMilliseconds < deadline && !ct.IsCancellationRequested)
            {
                waited = true;
                await waiter.NextFrame(1, ct);
                lock (Sync) RefreshLocked(window);
            }
            var report = BuildReport(window);
            return new DebugRunWaitResult
            {
                Waited = waited,
                WaitMs = waitMs,
                Terminal = report.Terminal,
                Report = report,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                Note = "wait only observes this runId. A wait window that expires is a normal observation: terminal=false carries the current evidence and is different from a host request execution timeout. Requesting end of the wait does not stop the window or any resource."
            };
        }

        /// <summary>读取一个窗口的完整报告；不写存储、不改变判定、不重开窗口。</summary>
        internal static DebugRunInfoResult Info(DebugRunInfoRequest request)
        {
            if (request == null) throw SchemaInvalid("request", "a debug.run-info body", "missing");
            DebugRunWindow window;
            lock (Sync)
            {
                if (!Windows.TryGetValue(request.RunId ?? string.Empty, out window)) throw MissingRun(request.RunId);
                RefreshLocked(window);
            }
            return new DebugRunInfoResult
            {
                Report = BuildReport(window),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                Note = "info re-derives the verdict from the events the store actually retained inside this window. An open window reports terminal=false with verdict indeterminate, and never claims the business goal."
            };
        }

        /// <summary>分页读取窗口摘要；摘要保留最近若干窗口，更旧的详细证据可能已被预算淘汰。</summary>
        internal static DebugRunListResult List(DebugRunListRequest request)
        {
            var sessionId = request == null ? null : request.SessionId;
            var verdict = request == null ? null : request.Verdict;
            var detailAvailable = request == null ? (bool?)null : request.DetailAvailable;
            var runIds = new List<string>();
            lock (Sync)
            {
                foreach (var pair in RunsBySession)
                {
                    if (!string.IsNullOrEmpty(sessionId) && !string.Equals(pair.Key, sessionId, StringComparison.Ordinal)) continue;
                    runIds.AddRange(pair.Value);
                }
                runIds.Sort(CompareRunId);
            }
            var editorSessionId = UnityCliBridge.EditorSessionId();
            var scope = UnityCliContinuationToken.Fingerprint(
                editorSessionId + "\0debug.run-list\0" + (sessionId ?? string.Empty)
                + "\0" + (verdict ?? string.Empty)
                + "\0" + (detailAvailable.HasValue ? (detailAvailable.Value ? "true" : "false") : string.Empty));
            var offset = 0;
            if (request != null && !string.IsNullOrEmpty(request.Cursor))
            {
                string position;
                if (!UnityCliContinuationToken.TryParse(request.Cursor, scope, editorSessionId, out position)
                    || !int.TryParse(position, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset)
                    || offset < 0 || offset > runIds.Count)
                    throw new BridgeCommandException(
                        "E_CONTINUATION_STALE",
                        "The run list continuation does not match the current Editor session or the loaded windows.",
                        null,
                        new[] { "Start a new debug.run-list read without a cursor." });
            }
            var limit = request == null || request.Limit == null
                ? DebugLimits.RunListDefaultLimit
                : Math.Min(Math.Max(1, request.Limit.Value), DebugLimits.RunListMaxLimit);
            var page = new List<DebugRunSummaryView>();
            var nextOffset = offset;
            lock (Sync)
            {
                for (var index = offset; index < runIds.Count && page.Count < limit; index++)
                {
                    // 筛选在原始窗口顺序上推进：total 仍描述原始窗口数，cursor 指向下一个未检查序号。
                    nextOffset = index + 1;
                    DebugRunWindow window;
                    if (!Windows.TryGetValue(runIds[index], out window)) continue;
                    RefreshLocked(window);
                    var summary = SummaryOf(window, BuildReport(window));
                    if (!string.IsNullOrEmpty(verdict) && !string.Equals(summary.Verdict, verdict, StringComparison.Ordinal)) continue;
                    if (detailAvailable.HasValue && summary.DetailAvailable != detailAvailable.Value) continue;
                    page.Add(summary);
                }
            }
            return new DebugRunListResult
            {
                Items = page,
                NextCursor = nextOffset < runIds.Count
                    ? UnityCliContinuationToken.Create(scope, editorSessionId, nextOffset.ToString(CultureInfo.InvariantCulture))
                    : null,
                Total = runIds.Count,
                NextOffset = nextOffset,
                HasMore = nextOffset < runIds.Count,
                Budget = BudgetView(sessionId),
                EditorSessionId = editorSessionId,
                DomainGeneration = DebugSessionHost.DomainGeneration(),
                Note = "Summaries describe what each window retained. When a window's detail was evicted by the declared budget, detailAvailable is false and only the summary facts remain. verdict and detailAvailable filter over the raw window order, so total stays the raw window count and nextCursor continues at the first window this page did not examine. "
            };
        }

        // ------------------------------------------------------------------
        // compare
        // ------------------------------------------------------------------

        /// <summary>
        /// 比较两个保留窗口：按调用方声明的来源、位置、对齐键、值路径与时钟定位首个可证明差异。
        /// 本路径是纯读取：不封闭窗口、不改变判定、不写文件、不注册回调，也不建立第二份事件副本。
        /// </summary>
        internal static DebugRunCompareResult Compare(DebugRunCompareRequest request)
        {
            var plan = DebugRunCompare.Validate(request);
            DebugRunCompareResult result;
            lock (Sync)
            {
                DebugRunWindow left;
                if (!Windows.TryGetValue(plan.LeftRunId, out left)) throw MissingRun(plan.LeftRunId);
                DebugRunWindow right;
                if (!Windows.TryGetValue(plan.RightRunId, out right)) throw MissingRun(plan.RightRunId);
                RefreshLocked(left);
                RefreshLocked(right);
                // 两个区间与同一瞬间的保留边界在这一次读取里固定，避免两次读取拼成一个并不存在的区间。
                var boundary = DebugEventStore.CaptureBoundary();
                var gaps = DebugEventStore.Gaps;
                var appendFailures = DebugEventStore.AppendFailures;
                // 终端窗口在第一次读取报告时冻结其判定与证据；compare 复用同一份冻结事实。
                var leftReport = BuildReport(left);
                var rightReport = BuildReport(right);
                var leftEvents = DebugEventStore.Range(left.StartSequence, left.IsTerminal ? left.EndSequence : boundary.Sequence);
                var rightEvents = DebugEventStore.Range(right.StartSequence, right.IsTerminal ? right.EndSequence : boundary.Sequence);
                result = DebugRunCompare.Run(
                    plan,
                    left,
                    leftReport,
                    leftEvents,
                    right,
                    rightReport,
                    rightEvents,
                    gaps,
                    boundary.OldestAvailableSequence,
                    appendFailures,
                    boundary.Sequence);
            }
            result.EditorSessionId = UnityCliBridge.EditorSessionId();
            result.DomainGeneration = DebugSessionHost.DomainGeneration();
            return result;
        }

        // ------------------------------------------------------------------
        // session.info 的最近运行读回
        // ------------------------------------------------------------------

        /// <summary>session.info 的最近窗口摘要；没有窗口时如实报告不可用，不伪造运行。</summary>
        internal static DebugSessionRunView DescribeLastRun(string sessionId)
        {
            DebugRunWindow window = null;
            lock (Sync)
            {
                List<string> runs;
                if (!string.IsNullOrEmpty(sessionId) && RunsBySession.TryGetValue(sessionId, out runs) && runs.Count > 0)
                    Windows.TryGetValue(runs[runs.Count - 1], out window);
            }
            if (window == null)
            {
                return new DebugSessionRunView
                {
                    Available = false,
                    OwnerRegistered = true,
                    RunId = string.Empty,
                    Kind = "evidence-window",
                    StartedAtUtc = string.Empty,
                    CompletedAtUtc = string.Empty,
                    Success = false,
                    Detail = string.Empty,
                    Note = "The run owner is registered; this session has no evidence window yet."
                };
            }
            var report = BuildReport(window);
            return new DebugSessionRunView
            {
                Available = true,
                OwnerRegistered = true,
                RunId = window.RunId,
                Kind = "evidence-window",
                StartedAtUtc = window.StartedAtUtc,
                CompletedAtUtc = window.SealedAtUtc,
                Success = string.Equals(report.Verdict, UnityCliDebugRunVerdicts.Passed, StringComparison.Ordinal),
                Detail = (window.State ?? string.Empty) + " / " + (report.Verdict ?? string.Empty),
                Note = report.Note
            };
        }

        /// <summary>本次 domain 的窗口状态作废；测试与域重载共用这里。</summary>
        /// <summary>
        /// 导出该 session 已声明的检查定义（不包含任何评估结果或证据），用于 recipe。
        /// Hook 身份不被复用，因此声明里的 hookId 不进入 recipe：它只描述位置与量词。
        /// </summary>
        internal static List<Dictionary<string, object>> ExportCheckDeclarations(string sessionId)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(sessionId)) return result;
            lock (Sync)
            {
                foreach (var window in Windows.Values.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)).OrderBy(item => item.StartSequence))
                {
                    if (result.Count >= DebugLimits.MaxRunSummariesPerSession) break;
                    foreach (var declaration in CheckDeclarations(window.Checks, window.SourceRequirements))
                    {
                        if (result.Count >= DebugLimits.MaxRunSummariesPerSession) break;
                        result.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "name", declaration.Name ?? string.Empty },
                            { "source", declaration.Source ?? string.Empty },
                            { "position", declaration.Position ?? string.Empty },
                            { "quantifier", declaration.Quantifier ?? string.Empty },
                            { "kind", declaration.Kind ?? string.Empty },
                            { "requiredSources", (declaration.RequiredSources ?? new List<string>()).Cast<object>().ToList() }
                        });
                    }
                }
            }
            return result;
        }

        internal static void Reset()
        {
            lock (Sync)
            {
                Windows.Clear();
                RunsBySession.Clear();
                RunCounters.Clear();
                detailedBytes = 0L;
            }
        }

        // ------------------------------------------------------------------
        // 预算与淘汰
        // ------------------------------------------------------------------

        /// <summary>
        /// 为新窗口预留详细证据份额：先淘汰最旧的详细窗口，再淘汰最旧的摘要，
        /// 全局字节账本不足时按窗口年龄继续淘汰；都无法腾出空间时拒绝本次 begin。
        /// </summary>
        private static bool ReserveDetailBudgetLocked(string sessionId, out string rejectReason)
        {
            rejectReason = null;
            List<string> runs;
            if (!string.IsNullOrEmpty(sessionId) && RunsBySession.TryGetValue(sessionId, out runs))
            {
                while (runs.Count >= DebugLimits.MaxRunSummariesPerSession)
                {
                    // 会话列表是摘要预算的唯一账本：这里只移除一次，否则每次淘汰会一次性丢掉两个摘要。
                    var evicted = runs[0];
                    EvictLocked(evicted);
                    runs.Remove(evicted);
                }
                var detailed = 0;
                foreach (var runId in runs)
                {
                    DebugRunWindow existing;
                    if (Windows.TryGetValue(runId, out existing) && existing.DetailAvailable) detailed++;
                }
                while (detailed >= DebugLimits.MaxDetailedRunWindowsPerSession)
                {
                    var evicted = false;
                    foreach (var runId in runs)
                    {
                        DebugRunWindow existing;
                        if (!Windows.TryGetValue(runId, out existing) || !existing.DetailAvailable) continue;
                        EvictDetailLocked(existing);
                        detailed--;
                        evicted = true;
                        break;
                    }
                    if (!evicted) break;
                }
            }
            var attempts = 0;
            var limit = DebugLimits.MaxRunSummariesPerSession * DebugLimits.MaxSessions;
            while (detailedBytes + DetailReservation > DebugLimits.MaxDetailedRunBytesGlobal && attempts < limit)
            {
                attempts++;
                if (!EvictOldestDetailLocked()) break;
            }
            if (detailedBytes + DetailReservation > DebugLimits.MaxDetailedRunBytesGlobal)
            {
                rejectReason = "global-detailed-evidence-exhausted";
                return false;
            }
            return true;
        }

        /// <summary>释放最旧窗口的详细证据，只保留摘要。</summary>
        private static bool EvictOldestDetailLocked()
        {
            DebugRunWindow oldest = null;
            foreach (var pair in RunsBySession)
            {
                foreach (var runId in pair.Value)
                {
                    DebugRunWindow candidate;
                    if (!Windows.TryGetValue(runId, out candidate) || !candidate.DetailAvailable) continue;
                    if (oldest == null || CompareRunId(candidate.RunId, oldest.RunId) < 0) oldest = candidate;
                }
            }
            if (oldest == null) return false;
            EvictDetailLocked(oldest);
            return true;
        }

        /// <summary>把详细证据归还全局账本，释放冻结载荷，并把该窗口标记为只剩摘要。</summary>
        private static void EvictDetailLocked(DebugRunWindow window)
        {
            window.DetailAvailable = false;
            if (window.DetailBytes > 0L)
            {
                detailedBytes -= window.DetailBytes;
                window.DetailBytes = 0L;
            }
            if (detailedBytes < 0L) detailedBytes = 0L;
            ReleaseFrozenPayload(window.FrozenReport);
            DebugRunEvidence.AddReason(window.EvictedReasons, UnityCliDebugRunVerdictReasons.DetailEvicted);
        }

        /// <summary>
        /// 释放已冻结报告里的详细证据载荷，保留判定结论、原因与覆盖事实。
        /// 被释放的是逐检查的匹配证据、首反例载荷与分段逐检查状态；已证明的反例结论不因此改变。
        /// </summary>
        private static void ReleaseFrozenPayload(DebugRunReportView frozen)
        {
            if (frozen == null) return;
            frozen.DetailAvailable = false;
            frozen.FirstCounterexample = null;
            if (frozen.Checks != null)
            {
                foreach (var check in frozen.Checks) check.Evidence = new List<DebugRunEvidenceView>();
            }
            if (frozen.Segments != null)
            {
                foreach (var segment in frozen.Segments) segment.CheckStatuses = new List<DebugRunCheckStatusView>();
            }
        }

        /// <summary>彻底删除一个窗口及其详细证据占用；会话摘要账本仍由调用方维护。</summary>
        private static void EvictLocked(string runId)
        {
            DebugRunWindow window;
            if (!Windows.TryGetValue(runId, out window)) return;
            EvictDetailLocked(window);
            Windows.Remove(runId);
        }

        // ------------------------------------------------------------------
        // 读取与判定装配
        // ------------------------------------------------------------------

        /// <summary>观察 runtime 段与 session 状态；开放窗口在这些边界上进入 interrupted。</summary>
        private static void RefreshLocked(DebugRunWindow window)
        {
            if (window == null || window.IsTerminal) return;
            var runtimeId = DebugSessionHost.RuntimeId();
            if (!string.Equals(runtimeId, window.RuntimeId, StringComparison.Ordinal))
            {
                InterruptLocked(window, DebugRunEvidence.TerminationRuntimeChanged);
                return;
            }
            if (!SessionIsOpen(window.SessionId)) InterruptLocked(window, DebugRunEvidence.TerminationSessionStopped);
        }

        /// <summary>runtime 段变化或 session 停止时中断窗口；已收集证据保留，判定按缺口处理。</summary>
        private static void InterruptLocked(DebugRunWindow window, string reason)
        {
            var boundaryFacts = CaptureBoundaryAndQueue();
            var capture = boundaryFacts.View;
            List<string> configurationAfter;
            string fingerprintAfter;
            HashSet<string> programs;
            window.State = UnityCliDebugRunStates.Interrupted;
            window.TerminationReason = reason;
            window.SealedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            window.SealedAtMonotonicMs = DebugEventClocks.MonotonicMilliseconds;
            window.EndSequence = capture.BoundarySequence;
            window.OldestAvailableAtEnd = boundaryFacts.OldestAvailableSequence;
            window.AppendFailuresAtEnd = capture.AppendFailuresAtBoundary;
            window.Capture = capture;
            configurationAfter = ConfigurationSnapshot(window.Checks, out fingerprintAfter, out programs);
            window.ConfigurationAfter = configurationAfter;
            window.ConfigurationFingerprintAfter = fingerprintAfter;
            foreach (var program in programs) window.SessionPrograms.Add(program);
        }

        /// <summary>session owner 的只读状态查询；session 不存在或存储不可用时按不可用处理。</summary>
        private static bool SessionIsOpen(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            try
            {
                var info = DebugSessionHost.Info(new DebugSessionInfoRequest { SessionId = sessionId });
                return string.Equals(info.Session.State, "open", StringComparison.Ordinal);
            }
            catch (BridgeCommandException)
            {
                return false;
            }
        }

        /// <summary>
        /// 装配一个窗口的完整报告：判定只来自 DebugRunEvidence 的一次评估。
        /// 已封闭窗口返回封闭时冻结的报告（只刷新与判定无关的预算与保留事实），
        /// 开窗期间的报告由读取时刻真实保留的事件推导。
        /// </summary>
        private static DebugRunReportView BuildReport(DebugRunWindow window)
        {
            if (window.IsTerminal)
            {
                DebugRunReportView frozen;
                lock (Sync)
                {
                    frozen = window.FrozenReport;
                    if (frozen != null)
                    {
                        // 判断与证据已冻结；预算占用、详细载荷是否仍保留、淘汰原因都是读取时刻的事实。
                        frozen.Budget = BudgetView(window.SessionId);
                        frozen.DetailAvailable = window.DetailAvailable;
                        foreach (var reason in window.EvictedReasons) DebugRunEvidence.AddReason(frozen.VerdictReasons, reason);
                    }
                }
                if (frozen != null) return frozen;
            }
            DebugRunCaptureView capture;
            List<UnityCliDebugEvent> events;
            IReadOnlyList<UnityCliDebugGap> gaps;
            long appendFailureDelta;
            long oldestAvailableAtEnd;
            long boundary;
            lock (Sync)
            {
                capture = window.Capture;
                oldestAvailableAtEnd = window.OldestAvailableAtEnd;
                if (!window.IsTerminal)
                {
                    var boundaryFacts = CaptureBoundaryAndQueue();
                    capture = boundaryFacts.View;
                    oldestAvailableAtEnd = boundaryFacts.OldestAvailableSequence;
                }
                boundary = window.IsTerminal ? window.EndSequence : capture.BoundarySequence;
                var appendAtEnd = window.IsTerminal ? window.AppendFailuresAtEnd : capture.AppendFailuresAtBoundary;
                appendFailureDelta = Math.Max(0L, appendAtEnd - window.AppendFailuresAtStart);
                events = DebugEventStore.Range(window.StartSequence, boundary);
                gaps = DebugEventStore.Gaps;
            }
            var verdict = DebugRunEvidence.Evaluate(window, events, gaps, appendFailureDelta, oldestAvailableAtEnd, boundary, capture.QueueDrained);
            foreach (var reason in window.EvictedReasons) DebugRunEvidence.AddReason(verdict.Reasons, reason);
            var report = new DebugRunReportView
            {
                RunId = window.RunId,
                SessionId = window.SessionId,
                State = window.State,
                Terminal = window.IsTerminal,
                Verdict = verdict.Verdict,
                VerdictReasons = new List<string>(verdict.Reasons),
                TerminationReason = window.TerminationReason,
                StartedAtUtc = window.StartedAtUtc,
                StartedAtMonotonicMs = window.StartedAtMonotonicMs,
                SealedAtUtc = window.SealedAtUtc,
                SealedAtMonotonicMs = window.SealedAtMonotonicMs,
                StartSequence = window.StartSequence,
                EndSequence = window.IsTerminal ? window.EndSequence : boundary,
                MaxWindowMs = window.MaxWindowMs,
                AllowSegments = window.AllowSegments,
                DetailAvailable = window.DetailAvailable,
                Checks = CheckResults(verdict.Checks, window.DetailAvailable),
                FirstCounterexample = window.DetailAvailable ? verdict.FirstCounterexample : null,
                CoverageGaps = BoundedGaps(verdict.Gaps),
                Sources = SourceResults(verdict.Sources, window.DetailAvailable),
                Conditions = ConditionViews(window, verdict),
                Segments = SegmentViews(verdict, window.DetailAvailable),
                Capture = capture,
                Environment = window.Environment,
                Configuration = ConfigurationView(window, window.IsTerminal),
                Budget = BudgetView(window.SessionId),
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                RuntimeId = window.RuntimeId,
                DomainGeneration = window.DomainGeneration
            };
            report.Note = ReportNote(report, window);
            if (report.Terminal)
            {
                // 封闭边界一旦确立，判定与证据载荷就固定下来：之后按 runId 读取的是同一份事实。
                lock (Sync)
                {
                    if (window.FrozenReport == null) window.FrozenReport = report;
                    else report = window.FrozenReport;
                }
            }
            return report;
        }

        /// <summary>报告说明：只描述当前事实与判定边界，不重复结论。</summary>
        private static string ReportNote(DebugRunReportView report, DebugRunWindow window)
        {
            if (!report.Terminal)
                return "The window is open, so lifecycle and evidence are still being collected: terminal is false and the verdict is indeterminate with window-not-complete. A complete counterexample is still reported as failed.";
            if (!window.DetailAvailable)
                return "This window is terminal, so its verdict and reasons were frozen when it closed; its detailed evidence payload was later released by the declared run budget, and the per-check evidence rows, the first counterexample payload and the per-segment check statuses are no longer readable. The verdict is the conclusion computed from the evidence that existed at the closing boundary.";
            if (string.Equals(report.Verdict, UnityCliDebugRunVerdicts.Failed, StringComparison.Ordinal))
                return "A complete counterexample exists, so the verdict stays failed; later unrelated coverage gaps never turn it into passed.";
            if (string.Equals(report.Verdict, UnityCliDebugRunVerdicts.Indeterminate, StringComparison.Ordinal))
                return "The retained evidence cannot prove every required check, so the verdict is indeterminate. The reasons and coverage gaps name the exact missing facts; an assertion failure is never reported as an error code.";
            return "Every required check is satisfied with proven coverage for the whole applicable window and the declared end condition was observed.";
        }

        private static List<DebugRunCheckResultView> CheckResults(List<DebugRunCheckEvaluation> checks, bool detailAvailable)
        {
            var views = new List<DebugRunCheckResultView>();
            foreach (var check in checks)
            {
                views.Add(new DebugRunCheckResultView
                {
                    Name = check.Plan == null ? string.Empty : check.Plan.Name,
                    Source = check.Plan == null ? string.Empty : check.Plan.Source,
                    Position = check.Plan == null ? string.Empty : check.Plan.Position,
                    Quantifier = check.Plan == null ? string.Empty : check.Plan.Quantifier,
                    Status = check.Status,
                    MatchCount = check.MatchCount,
                    PassedCount = check.PassedCount,
                    FailedCount = check.FailedCount,
                    CoverageRequired = check.CoverageRequired,
                    CoverageProven = check.CoverageProven,
                    SamplePointOnly = check.SamplePointOnly,
                    Truncated = check.Truncated,
                    CoverageGaps = BoundedGaps(check.Gaps),
                    Evidence = detailAvailable ? DebugRunEvidence.EvidenceOf(check) : new List<DebugRunEvidenceView>(),
                    Reasons = new List<string>(check.Reasons),
                    Detail = CheckDetail(check, detailAvailable)
                });
            }
            return views;
        }

        /// <summary>逐检查说明：覆盖要求与覆盖证明是两个独立事实，说明文字按量词如实区分。</summary>
        private static string CheckDetail(DebugRunCheckEvaluation check, bool detailAvailable)
        {
            if (check.Plan == null) return string.Empty;
            if (!detailAvailable) return "The detailed evidence payload of this window was released by the declared run budget; the verdict and its reasons stay frozen, and the per-check evidence rows are no longer readable.";
            if (string.Equals(check.Status, DebugRunEvidence.StatusSatisfied, StringComparison.Ordinal))
            {
                return check.CoverageRequired
                    ? "A retained matching event satisfies this quantifier together with the caller-declared coverage proof for its required sources over the whole applicable window."
                    : "A retained matching event satisfies this quantifier; it declares coverageRequired false because only whole-window and absent consume a caller-declared coverage requirement, and coverageProven only states that the interval of its required sources was readable.";
            }
            if (string.Equals(check.Status, DebugRunEvidence.StatusCounterexample, StringComparison.Ordinal))
                return "A retained event is a complete counterexample for this check; the first one is reported in firstCounterexample.";
            return check.CoverageRequired
                ? "The retained evidence is insufficient: see reasons and coverageGaps for the exact missing fact. coverageRequired is true, so this quantifier also needs the caller-declared coverage requirement for its required sources."
                : "The retained evidence is insufficient: see reasons and coverageGaps for the exact missing fact. coverageRequired is false, so no declared coverage requirement was consumed by this quantifier.";
        }

        private static List<DebugRunSourceCoverageView> SourceResults(List<DebugRunSourceEvaluation> sources, bool detailAvailable)
        {
            var views = new List<DebugRunSourceCoverageView>();
            foreach (var source in sources)
            {
                views.Add(new DebugRunSourceCoverageView
                {
                    Source = source.Source,
                    Proof = source.ProofRequired
                        ? UnityCliDebugRunCoverageProofs.Coverage
                        : (source.Declared ? UnityCliDebugRunCoverageProofs.Declared : UnityCliDebugRunCoverageProofs.None),
                    ProofRequired = source.ProofRequired,
                    CoverageProven = source.CoverageProven,
                    MatchCount = source.MatchCount,
                    Sampled = source.Sampled,
                    SampleEvery = source.Sampled ? 0 : 1,
                    Truncated = source.Truncated,
                    CoverageGaps = BoundedGaps(source.Gaps),
                    Reasons = new List<string>(source.Reasons)
                });
            }
            return views;
        }

        private static List<DebugRunConditionView> ConditionViews(DebugRunWindow window, DebugRunVerdict verdict)
        {
            var views = new List<DebugRunConditionView>();
            for (var index = 0; index < window.Conditions.Count; index++)
            {
                var condition = window.Conditions[index];
                var observed = index < verdict.ConditionObserved.Count && verdict.ConditionObserved[index];
                views.Add(new DebugRunConditionView
                {
                    Kind = condition.Kind,
                    Name = condition.Name,
                    Verified = !string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal),
                    DeclaredOnly = string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal),
                    Observed = observed,
                    Detail = ConditionDetail(condition, observed)
                });
            }
            return views;
        }

        private static string ConditionDetail(DebugRunConditionPlan condition, bool observed)
        {
            if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Manual, StringComparison.Ordinal))
                return "Satisfied by an explicit debug.run-end on this window.";
            if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Clock, StringComparison.Ordinal))
                return "Satisfied when the window was sealed after the declared maxWindowMs elapsed.";
            if (string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Check, StringComparison.Ordinal))
                return "Satisfied when a retained event named " + condition.Name + " appears in the window.";
            return observed
                ? "A retained trigger fact with this signal name appears in the window."
                : "A future signal delivery cannot be proven at begin; this condition is recorded as caller-declared and is observed only if the trigger fact really appears.";
        }

        private static List<DebugRunSegmentView> SegmentViews(DebugRunVerdict verdict, bool detailAvailable)
        {
            var views = new List<DebugRunSegmentView>();
            foreach (var segment in verdict.Segments)
            {
                views.Add(new DebugRunSegmentView
                {
                    Index = segment.Index,
                    FromSequence = segment.FromSequence,
                    ToSequence = segment.ToSequence,
                    Verdict = segment.Verdict,
                    Reasons = new List<string>(segment.Reasons),
                    CheckStatuses = detailAvailable ? new List<DebugRunCheckStatusView>(segment.CheckStatuses) : new List<DebugRunCheckStatusView>(),
                    BoundaryFact = segment.BoundaryFact
                });
            }
            return views;
        }

        private static List<DebugRunCoverageGapView> BoundedGaps(List<DebugRunCoverageGapView> gaps)
        {
            var views = new List<DebugRunCoverageGapView>();
            foreach (var gap in gaps)
            {
                if (views.Count >= DebugLimits.MaxRunCoverageGaps) break;
                views.Add(gap);
            }
            return views;
        }

        private static DebugRunSummaryView SummaryOf(DebugRunWindow window, DebugRunReportView report)
        {
            long failed = 0L;
            foreach (var check in report.Checks)
            {
                if (string.Equals(check.Status, DebugRunEvidence.StatusCounterexample, StringComparison.Ordinal)) failed++;
            }
            return new DebugRunSummaryView
            {
                RunId = window.RunId,
                SessionId = window.SessionId,
                State = window.State,
                Terminal = window.IsTerminal,
                Verdict = report.Verdict,
                VerdictReasons = report.VerdictReasons,
                StartedAtUtc = window.StartedAtUtc,
                SealedAtUtc = window.SealedAtUtc,
                StartSequence = window.StartSequence,
                EndSequence = report.EndSequence,
                RequiredCheckCount = window.Checks.Count,
                FailedCheckCount = failed,
                CounterexampleCount = report.FirstCounterexample == null ? 0L : 1L,
                DetailAvailable = window.DetailAvailable,
                AllowSegments = window.AllowSegments,
                SegmentCount = report.Segments.Count
            };
        }

        // ------------------------------------------------------------------
        // 声明校验
        // ------------------------------------------------------------------

        private static List<DebugRunCheckPlan> ValidateChecks(List<DebugRunCheckSpec> specs)
        {
            var plans = new List<DebugRunCheckPlan>();
            if (specs == null || specs.Count == 0) throw SchemaInvalid("requiredChecks", "at least one required check", "empty");
            foreach (var spec in specs)
            {
                if (spec == null) throw SchemaInvalid("requiredChecks[]", "a check declaration", "null");
                var name = RequireText(spec.Name, "requiredChecks[].name");
                foreach (var existing in plans)
                {
                    if (string.Equals(existing.Name, name, StringComparison.Ordinal))
                        throw SchemaInvalid("requiredChecks[].name", "a name that is not declared twice", name);
                }
                if (!UnityCliDebugRunCheckSources.IsDefined(spec.Source))
                    throw SchemaInvalid("requiredChecks[].source", "one of " + string.Join(", ", UnityCliDebugRunCheckSources.All()), spec.Source);
                var position = RequireText(spec.Position, "requiredChecks[].position");
                var kind = spec.Kind ?? string.Empty;
                if (kind.Length > MaxKindBytes)
                    throw SchemaInvalid("requiredChecks[].kind", MaxKindBytes.ToString(CultureInfo.InvariantCulture) + " characters or fewer", kind);
                if (!UnityCliDebugRunQuantifiers.IsDefined(spec.Quantifier))
                    throw SchemaInvalid("requiredChecks[].quantifier", "one of " + string.Join(", ", UnityCliDebugRunQuantifiers.All()), spec.Quantifier);
                var required = new List<string>();
                if (spec.RequiredSources != null)
                {
                    foreach (var source in spec.RequiredSources)
                    {
                        if (!UnityCliDebugRunCheckSources.IsDefined(source))
                            throw SchemaInvalid("requiredChecks[].requiredSources", "only declared sources " + string.Join(", ", UnityCliDebugRunCheckSources.All()), source);
                        if (!required.Contains(source)) required.Add(source);
                    }
                }
                if (required.Count == 0) required.Add(spec.Source);
                else if (!required.Contains(spec.Source)) required.Insert(0, spec.Source);
                plans.Add(new DebugRunCheckPlan
                {
                    Name = name,
                    Source = spec.Source,
                    Position = position,
                    Quantifier = spec.Quantifier,
                    HookId = spec.HookId ?? string.Empty,
                    Kind = kind,
                    RequiredSources = required
                });
            }
            return plans;
        }

        private static List<DebugRunSourcePlan> ValidateSourceRequirements(List<DebugRunSourceRequirementSpec> specs)
        {
            var plans = new List<DebugRunSourcePlan>();
            if (specs == null) return plans;
            foreach (var spec in specs)
            {
                if (spec == null) throw SchemaInvalid("sourceRequirements[]", "a source requirement", "null");
                if (!UnityCliDebugRunCheckSources.IsDefined(spec.Source))
                    throw SchemaInvalid("sourceRequirements[].source", "one of " + string.Join(", ", UnityCliDebugRunCheckSources.All()), spec.Source);
                if (!UnityCliDebugRunCoverageProofs.IsDefined(spec.Proof))
                    throw SchemaInvalid("sourceRequirements[].proof", "coverage or declared", spec.Proof);
                foreach (var existing in plans)
                {
                    if (string.Equals(existing.Source, spec.Source, StringComparison.Ordinal))
                        throw SchemaInvalid("sourceRequirements[].source", "one requirement per source", spec.Source);
                }
                plans.Add(new DebugRunSourcePlan { Source = spec.Source, Proof = spec.Proof });
            }
            return plans;
        }

        private static List<DebugRunConditionPlan> ValidateConditions(List<DebugRunEndConditionSpec> specs, long? maxWindowMs)
        {
            var plans = new List<DebugRunConditionPlan>();
            if (specs != null)
            {
                foreach (var spec in specs)
                {
                    if (spec == null) throw SchemaInvalid("endConditions[]", "an end condition", "null");
                    if (!UnityCliDebugRunEndConditionKinds.IsDefined(spec.Kind))
                        throw SchemaInvalid("endConditions[].kind", "one of " + string.Join(", ", UnityCliDebugRunEndConditionKinds.All()), spec.Kind);
                    var name = spec.Name ?? string.Empty;
                    var named = string.Equals(spec.Kind, UnityCliDebugRunEndConditionKinds.Check, StringComparison.Ordinal)
                        || string.Equals(spec.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal);
                    if (named && name.Length == 0)
                        throw SchemaInvalid("endConditions[].name", "a non-empty name for kind " + spec.Kind, "empty");
                    if (!named && name.Length != 0)
                        throw SchemaInvalid("endConditions[].name", "no name for kind " + spec.Kind, name);
                    plans.Add(new DebugRunConditionPlan { Kind = spec.Kind, Name = name });
                }
            }
            var hasClock = false;
            foreach (var plan in plans)
            {
                if (string.Equals(plan.Kind, UnityCliDebugRunEndConditionKinds.Clock, StringComparison.Ordinal)) hasClock = true;
            }
            if (hasClock && !maxWindowMs.HasValue)
                throw SchemaInvalid("maxWindowMs", "a duration when an end condition of kind clock is declared", "missing");
            if (plans.Count == 0)
            {
                // 未声明结束条件时，唯一的结束方式就是显式 run.end；如实记录为一条 manual 条件。
                plans.Add(new DebugRunConditionPlan { Kind = UnityCliDebugRunEndConditionKinds.Manual, Name = string.Empty });
            }
            return plans;
        }

        /// <summary>全窗口与负向检查必须由调用方声明覆盖要求；没有声明时在 begin 就明确拒绝。</summary>
        private static void ValidateCheckCoverage(List<DebugRunCheckPlan> checks, List<DebugRunSourcePlan> requirements)
        {
            foreach (var check in checks)
            {
                var requiresCoverage = string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.WholeWindow, StringComparison.Ordinal)
                    || string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.Absent, StringComparison.Ordinal);
                if (!requiresCoverage) continue;
                foreach (var source in check.RequiredSources)
                {
                    var declared = false;
                    foreach (var requirement in requirements)
                    {
                        if (!string.Equals(requirement.Source, source, StringComparison.Ordinal)) continue;
                        if (requirement.ProofRequired) declared = true;
                    }
                    if (!declared)
                        throw SchemaInvalid(
                            "sourceRequirements",
                            "a coverage requirement for source " + source + " because check " + check.Name + " uses quantifier " + check.Quantifier,
                            "missing");
                }
            }
        }

        private static string RequireText(string value, string field)
        {
            var text = value == null ? string.Empty : value.Trim();
            if (text.Length == 0) throw SchemaInvalid(field, "a non-empty string", "empty");
            if (System.Text.Encoding.UTF8.GetByteCount(text) > MaxPositionBytes)
                throw SchemaInvalid(field, MaxPositionBytes.ToString(CultureInfo.InvariantCulture) + " UTF-8 bytes or fewer", text);
            return text;
        }

        // ------------------------------------------------------------------
        // 配置、环境与捕获
        // ------------------------------------------------------------------

        /// <summary>当前相关配置的指纹与逐条回读：本 session 的 program 与本窗口声明的 hook。</summary>
        private static List<string> ConfigurationSnapshot(List<DebugRunCheckPlan> checks, out string fingerprint, out HashSet<string> sessionPrograms)
        {
            var entries = new List<string>();
            sessionPrograms = new HashSet<string>(StringComparer.Ordinal);
            var hookIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var check in checks)
            {
                if (check.HookId.Length != 0) hookIds.Add(check.HookId);
            }
            try
            {
                var programs = DebugProgramHost.List(new DebugProgramListRequest { Limit = DebugLimits.ProgramListMaxLimit });
                foreach (var program in programs.Items)
                {
                    if (string.IsNullOrEmpty(program.ProgramId)) continue;
                    sessionPrograms.Add(program.ProgramId);
                    entries.Add("program:" + program.ProgramId + ":" + program.Revision.ToString(CultureInfo.InvariantCulture) + ":" + (program.State ?? string.Empty));
                }
            }
            catch (BridgeCommandException)
            {
                // program owner 不可读时如实记录，不伪造一条配置。
                entries.Add("program:unreadable");
            }
            try
            {
                var hooks = MethodHookHost.List(new MethodHookListRequest { Limit = DebugLimits.ListMaxLimit });
                foreach (var hook in hooks.Items)
                {
                    if (string.IsNullOrEmpty(hook.HookId)) continue;
                    if (!hookIds.Contains(hook.HookId)) continue;
                    entries.Add("hook:" + hook.HookId + ":" + hook.Revision.ToString(CultureInfo.InvariantCulture) + ":" + (hook.State ?? string.Empty));
                }
            }
            catch (BridgeCommandException)
            {
                entries.Add("hook:unreadable");
            }
            entries.Sort(StringComparer.Ordinal);
            fingerprint = UnityCliContinuationToken.Fingerprint(string.Join("\n", entries));
            return entries;
        }

        /// <summary>begin 时刻的环境与时钟溯源；场景读取只在可证明的主线程上发生。</summary>
        private static Dictionary<string, object> EnvironmentSnapshot(DebugRunBeginRequest request, string runtimeId, string domainGeneration, string editorSessionId)
        {
            var environment = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "editorSessionId", editorSessionId },
                { "runtimeId", runtimeId },
                { "domainGeneration", domainGeneration },
                { "startedAtMonotonicMs", DebugEventClocks.MonotonicMilliseconds },
                { "seedControl", request.SeedControl ?? string.Empty },
                { "preparationDeclared", request.Preparation != null && request.Preparation.Count > 0 },
                { "inputsDeclared", request.Inputs != null && request.Inputs.Count > 0 },
                { "preparationNote", "The host records the caller-declared preparation and inputs; it never executes preparation or injects inputs for this window." }
            };
            if (DebugEventClocks.IsCurrentThreadMainThread())
            {
                var scene = SceneManager.GetActiveScene();
                environment["isPlaying"] = UnityEditor.EditorApplication.isPlaying;
                environment["sceneName"] = scene.name ?? string.Empty;
                environment["scenePath"] = scene.path ?? string.Empty;
                environment["unityFrame"] = DebugEventClocks.ReadUnityFrame();
                environment["clockKind"] = UnityCliDebugClocks.UnityFrame;
            }
            else
            {
                environment["isPlaying"] = false;
                environment["sceneName"] = string.Empty;
                environment["scenePath"] = string.Empty;
                environment["unityFrame"] = DebugEventClocks.LastObservedMainFrame;
                environment["clockKind"] = UnityCliDebugClocks.Monotonic;
            }
            return environment;
        }

        /// <summary>准备与输入定义的内容 hash；只对调用方提交的 JSON 计算，不推断业务语义。</summary>
        private static string DefinitionHash(DebugRunBeginRequest request)
        {
            var text = new System.Text.StringBuilder();
            text.Append("prep=").Append(request.Preparation == null ? "{}" : UnityCliSerializer.Serialize(request.Preparation));
            text.Append("\ninputs=").Append(request.Inputs == null ? "{}" : UnityCliSerializer.Serialize(request.Inputs));
            text.Append("\nseed=").Append(request.SeedControl ?? string.Empty);
            return UnityCliContinuationToken.Fingerprint(text.ToString());
        }

        /// <summary>一次性捕获边界 sequence、保留范围下界与同一瞬间的 queue 水位。</summary>
        private static DebugRunBoundaryFacts CaptureBoundaryAndQueue()
        {
            var boundary = DebugEventStore.CaptureBoundary();
            var scheduler = DebugScheduler.Describe();
            var pending = Convert.ToInt64(scheduler["pendingDeliveries"], CultureInfo.InvariantCulture);
            var drivers = Convert.ToInt64(scheduler["drivers"], CultureInfo.InvariantCulture);
            var drained = pending == 0L && drivers == 0L;
            return new DebugRunBoundaryFacts
            {
                OldestAvailableSequence = boundary.OldestAvailableSequence,
                View = new DebugRunCaptureView
                {
                    BoundarySequence = boundary.Sequence,
                    DroppedAtBoundary = boundary.Dropped,
                    AppendFailuresAtBoundary = DebugEventStore.AppendFailures,
                    PendingDeliveries = pending,
                    QueueHighWaterMark = Convert.ToInt64(scheduler["queueHighWaterMark"], CultureInfo.InvariantCulture),
                    ExpiredDeliveries = Convert.ToInt64(scheduler["expiredDeliveries"], CultureInfo.InvariantCulture),
                    DroppedDeliveries = Convert.ToInt64(scheduler["droppedDeliveries"], CultureInfo.InvariantCulture),
                    Drivers = drivers,
                    QueueDrained = drained,
                    Detail = drained
                        ? "The scheduler held no pending delivery and no active driver at the captured boundary, so every event produced before this boundary had already entered the store."
                        : "The scheduler still held pending deliveries or drivers at the captured boundary: events they produce arrive after this boundary and are therefore outside this window."
                }
            };
        }

        /// <summary>
        /// 一次边界捕获的内部事实：返回给调用方的公开视图，以及同一临界区里仍保留的最早 sequence。
        /// 后者只用于把环淘汰定位到窗口自己的区间，不属于公开契约。
        /// </summary>
        private sealed class DebugRunBoundaryFacts
        {
            internal DebugRunCaptureView View;
            internal long OldestAvailableSequence;
        }

        private static Dictionary<string, object> ConfigurationView(DebugRunWindow window, bool atBoundary)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "fingerprintAtBegin", window.ConfigurationFingerprint },
                { "fingerprintAtBoundary", window.ConfigurationFingerprintAfter },
                { "changed", !string.Equals(window.ConfigurationFingerprint, window.ConfigurationFingerprintAfter, StringComparison.Ordinal) },
                { "atBegin", new List<string>(window.ConfigurationBefore) },
                { "atBoundary", new List<string>(atBoundary ? window.ConfigurationAfter : window.ConfigurationBefore) },
                { "allowSegments", window.AllowSegments },
                { "referencedHooks", new List<string>(window.ReferencedHooks) },
                { "referencedPrograms", new List<string>(window.SessionPrograms) }
            };
        }

        // ------------------------------------------------------------------
        // 声明回读与预算
        // ------------------------------------------------------------------

        private static List<DebugRunCheckDeclarationView> CheckDeclarations(List<DebugRunCheckPlan> checks, List<DebugRunSourcePlan> requirements)
        {
            var views = new List<DebugRunCheckDeclarationView>();
            foreach (var check in checks)
            {
                views.Add(new DebugRunCheckDeclarationView
                {
                    Name = check.Name,
                    Source = check.Source,
                    Position = check.Position,
                    Quantifier = check.Quantifier,
                    HookId = check.HookId,
                    Kind = check.Kind,
                    RequiredSources = new List<string>(check.RequiredSources),
                    Verified = true,
                    Detail = DeclarationDetail(check, requirements)
                });
            }
            return views;
        }

        private static string DeclarationDetail(DebugRunCheckPlan check, List<DebugRunSourcePlan> requirements)
        {
            var requiresCoverage = string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.WholeWindow, StringComparison.Ordinal)
                || string.Equals(check.Quantifier, UnityCliDebugRunQuantifiers.Absent, StringComparison.Ordinal);
            if (!requiresCoverage)
                return "Verified at begin: the source, position and quantifier are legal, and a retained matching event decides this check.";
            var covered = true;
            foreach (var source in check.RequiredSources)
            {
                var declared = false;
                foreach (var requirement in requirements)
                {
                    if (!string.Equals(requirement.Source, source, StringComparison.Ordinal)) continue;
                    if (requirement.ProofRequired) declared = true;
                }
                if (!declared) covered = false;
            }
            return covered
                ? "Verified at begin: this check requires proven coverage for " + string.Join(", ", check.RequiredSources) + "."
                : "Verified at begin: this check requires declared coverage proof for " + string.Join(", ", check.RequiredSources) + ".";
        }

        private static List<DebugRunSourceRequirementView> SourceRequirementDeclarations(List<DebugRunSourcePlan> requirements)
        {
            var views = new List<DebugRunSourceRequirementView>();
            foreach (var requirement in requirements)
            {
                views.Add(new DebugRunSourceRequirementView
                {
                    Source = requirement.Source,
                    Proof = requirement.Proof,
                    ProofRequired = requirement.ProofRequired,
                    Detail = requirement.ProofRequired
                        ? "A claim about this source across the whole window needs proven store coverage for the window interval."
                        : "This requirement is only recorded as declared by the caller and never counts as coverage proof."
                });
            }
            return views;
        }

        private static List<DebugRunConditionView> ConditionDeclarations(List<DebugRunConditionPlan> conditions)
        {
            var views = new List<DebugRunConditionView>();
            foreach (var condition in conditions)
            {
                views.Add(new DebugRunConditionView
                {
                    Kind = condition.Kind,
                    Name = condition.Name,
                    Verified = !string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal),
                    DeclaredOnly = string.Equals(condition.Kind, UnityCliDebugRunEndConditionKinds.Signal, StringComparison.Ordinal),
                    Observed = false,
                    Detail = ConditionDetail(condition, false)
                });
            }
            return views;
        }

        private static DebugRunBudgetView BudgetView(string sessionId)
        {
            lock (Sync)
            {
                long used = 0L;
                long detailed = 0L;
                // 计数器描述本次读取的范围：给出 sessionId 时只统计该 session，未给出时统计全部 session。
                // 上限仍是每 session 的限制，所以聚合值仅用于判断“当前范围内有多少”。
                foreach (var pair in RunsBySession)
                {
                    if (!string.IsNullOrEmpty(sessionId) && !string.Equals(pair.Key, sessionId, StringComparison.Ordinal)) continue;
                    used += pair.Value.Count;
                    foreach (var runId in pair.Value)
                    {
                        DebugRunWindow window;
                        if (Windows.TryGetValue(runId, out window) && window.DetailAvailable) detailed++;
                    }
                }
                return new DebugRunBudgetView
                {
                    SummariesUsed = used,
                    SummariesRemaining = Math.Max(0L, (long)DebugLimits.MaxRunSummariesPerSession - used),
                    MaxSummariesPerSession = DebugLimits.MaxRunSummariesPerSession,
                    DetailedWindowsUsed = detailed,
                    DetailedWindowsRemaining = Math.Max(0L, (long)DebugLimits.MaxDetailedRunWindowsPerSession - detailed),
                    MaxDetailedWindowsPerSession = DebugLimits.MaxDetailedRunWindowsPerSession,
                    DetailedBytesUsed = detailedBytes,
                    DetailedBytesRemaining = Math.Max(0L, DebugLimits.MaxDetailedRunBytesGlobal - detailedBytes),
                    MaxDetailedBytesGlobal = DebugLimits.MaxDetailedRunBytesGlobal
                };
            }
        }

        /// <summary>按真实序列化字节估算一个报告的详细证据占用。</summary>
        private static long EstimateDetailBytes(DebugRunReportView report)
        {
            long total = 0L;
            if (report == null) return total;
            if (report.Checks != null)
            {
                foreach (var check in report.Checks)
                {
                    if (check.Evidence != null)
                    {
                        foreach (var evidence in check.Evidence)
                        {
                            total += System.Text.Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(evidence));
                        }
                    }
                    if (check.CoverageGaps != null)
                    {
                        foreach (var gap in check.CoverageGaps)
                        {
                            total += System.Text.Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(gap));
                        }
                    }
                }
            }
            if (report.FirstCounterexample != null) total += System.Text.Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(report.FirstCounterexample));
            if (report.Segments != null)
            {
                foreach (var segment in report.Segments)
                {
                    if (segment.CheckStatuses == null) continue;
                    foreach (var status in segment.CheckStatuses)
                    {
                        total += System.Text.Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(status));
                    }
                }
            }
            return Math.Min(total, DebugLimits.MaxRunDetailBytesPerWindow);
        }

        // ------------------------------------------------------------------
        // 命名与错误
        // ------------------------------------------------------------------

        /// <summary>按 runId 的数字部分比较，保证 list 的顺序是创建顺序而不是字符串顺序。</summary>
        private static int CompareRunId(string left, string right)
        {
            var leftIndex = ParseRunIndex(left);
            var rightIndex = ParseRunIndex(right);
            return leftIndex == rightIndex ? string.CompareOrdinal(left, right) : leftIndex.CompareTo(rightIndex);
        }

        private static long ParseRunIndex(string runId)
        {
            if (string.IsNullOrEmpty(runId)) return 0L;
            var separator = runId.LastIndexOf('-');
            long parsed;
            return separator >= 0 && long.TryParse(runId.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L;
        }

        /// <summary>未知 runId 或跨 domain 的旧 runId 都按不存在返回；绝不把旧身份映射到新窗口。</summary>
        private static BridgeCommandException MissingRun(string runId)
        {
            var known = new List<object>();
            lock (Sync)
            {
                foreach (var pair in RunsBySession)
                {
                    foreach (var candidate in pair.Value)
                    {
                        if (known.Count >= DebugLimits.MaxRunSummariesPerSession) break;
                        known.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            { "runId", candidate },
                            { "sessionId", pair.Key }
                        });
                    }
                }
            }
            return new BridgeCommandException(
                "E_DEBUG_RUN_NOT_FOUND",
                "No live evidence window has that runId in the current domain generation.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "runId", runId ?? string.Empty },
                    { "domainGeneration", DebugSessionHost.DomainGeneration() },
                    { "knownRuns", known }
                },
                new[]
                {
                    "Run tools run debug.run-list to read the windows of the current domain generation.",
                    "Run tools run debug.run-begin to create a new window; a window from a previous domain generation is never reattached."
                });
        }

        private static BridgeCommandException SchemaInvalid(string field, string expected, string actual)
        {
            return new BridgeCommandException(
                "E_SCHEMA_INVALID",
                "Field " + field + " is invalid: expected " + expected + ".",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "path", "$." + field },
                    { "expected", expected },
                    { "actual", actual ?? string.Empty }
                },
                new[] { "Run tools describe debug.run-begin to read the field contract, then submit the corrected input." });
        }

    }
}
#endif
