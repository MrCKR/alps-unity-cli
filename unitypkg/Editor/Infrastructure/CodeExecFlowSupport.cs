#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// debug.code.flow.submit / run / wait / cancel 的 Bridge 命令面。
    ///
    /// submit 只提交并交回句柄，它的完成含义是“已被接受”，因此不等待、不返回流状态；
    /// run 提交并等到终态，提交前先证明“流预算加余量能落在本次请求超时内”，所以它不会退回半途态；
    /// wait 只观察已有句柄，用于 submit 之后续等或取一次快照；cancel 终止一条流。
    /// 四个命令共享同一条编译路径与同一份产物账本，不建立第二套执行器。
    /// </summary>
    public static partial class UnityCliBridge
    {
        private const int FlowSubmitTimeoutMs = 90000;
        private const int FlowRunTimeoutMs = 3900000;
        private const int FlowWaitTimeoutMs = 3900000;
        private const int FlowCancelTimeoutMs = 30000;

        // 注册跨帧流命令。命令 timeout 只覆盖传输层，流的寿命始终由 flowBudgetMs 决定。
        private static void RegisterCodeFlowCommands(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.submit",
                "code",
                true,
                FlowSubmitTimeoutMs,
                async (request, cancellationToken) => (object)await CodeFlowSubmit(request, cancellationToken));
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.run",
                "code",
                true,
                FlowRunTimeoutMs,
                async (request, cancellationToken) => (object)await CodeFlowRun(request, cancellationToken));
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.wait",
                "code",
                false,
                FlowWaitTimeoutMs,
                async (request, cancellationToken) => (object)await CodeFlowWait(request, cancellationToken));
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.cancel",
                "code",
                true,
                FlowCancelTimeoutMs,
                async (request, cancellationToken) => (object)await CodeFlowCancel(request, cancellationToken));
        }

        /// <summary>提交一条流并立即返回句柄；在这里完成编译，因此返回后流已经在 tick 上推进。</summary>
        private static async ValueTask<DebugCodeFlowSubmitResult> CodeFlowSubmit(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var budgetMs = DebugCodeFlowWait.ValidateBudget(OptionalNumberArg(request, "flowBudgetMs"));
            var record = await CreateFlowAsync(request, budgetMs, cancellationToken);
            var receipt = new DebugCodeFlowSubmitResult
            {
                Registered = true,
                FlowId = record.FlowId,
                SessionId = record.SessionId,
                BudgetMs = record.BudgetMs,
                StartedMonotonicMs = record.CreatedMonotonicMs,
                ActiveFlows = DebugFlowHost.ActiveCount,
                SessionActiveFlows = DebugFlowHost.ActiveForSession(record.SessionId),
                MaxFlowsPerInstance = DebugLimits.MaxFlowsPerInstance,
                MaxFlowsPerSession = DebugLimits.MaxFlowsPerSession,
                EditorSessionId = EditorSessionId(),
                Note = "registered means the flow is stored and will advance on the central editor tick. The receipt is not a result: read it with debug.code.flow.wait."
            };
            // 提交是偏变动作：完成证据只允许登记在多变契约上。
            var contract = ResolveRequestCompletionContract(request);
            if (contract != null && contract.Mutation)
                RecordDirectCompletionEvidence(request.RequestId, "debug.code.flow.submit", contract, receipt);
            return receipt;
        }

        /// <summary>
        /// 提交并等到终态。提交前先证明本次请求的剩余超时足以覆盖流预算与余量，
        /// 因此这里要么返回真实终态，要么在任何编译开始前就拒绝，不会退回半途状态。
        /// </summary>
        private static async ValueTask<DebugCodeFlowResult> CodeFlowRun(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var budgetMs = DebugCodeFlowWait.ValidateBudget(OptionalNumberArg(request, "flowBudgetMs"));
            int remaining;
            request.TryDeadlineRemainingMs(out remaining);
            var needed = budgetMs + DebugLimits.FlowRunMarginMs;
            if (remaining > 0 && needed > remaining)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "flowBudgetMs plus the declared margin does not fit inside this request's remaining deadline.",
                    new Dictionary<string, object>
                    {
                        { "path", "$.flowBudgetMs" },
                        { "flowBudgetMs", budgetMs },
                        { "requiredRemainingMs", needed },
                        { "actualRemainingMs", (double)remaining }
                    },
                    new[]
                    {
                        "Raise the request timeout so it covers flowBudgetMs plus the declared margin.",
                        "Or submit with debug.code.flow.submit and read the flow with debug.code.flow.wait, which never needs one long request."
                    });
            var record = await CreateFlowAsync(request, budgetMs, cancellationToken);
            // 预算已在前面证明落在请求超时内，因此这里等到终态是必然的；剩余额度只作传输层的最后兜底。
            var result = await DebugCodeFlowWait.AwaitAsync(record, remaining > 0 ? remaining : needed, new UnityCliWaiter(request.RequestId, "debug.code.flow.run"), cancellationToken);
            return CompleteOrThrow(request, "debug.code.flow.run", result);
        }

        /// <summary>只观察一条已存在的流；不编译、不执行、不取消。省略 waitMs 时观察到请求超时为止。</summary>
        private static async ValueTask<DebugCodeFlowResult> CodeFlowWait(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var record = RequireFlow(request.StringArg("flowId"));
            var requested = OptionalNumberArg(request, "waitMs");
            double waitMs;
            if (requested.HasValue) waitMs = DebugCodeFlowWait.ValidateWaitMs(requested);
            else
            {
                int remaining;
                request.TryDeadlineRemainingMs(out remaining);
                waitMs = remaining > 0 ? remaining : DebugLimits.FlowRequestWaitDefaultMs;
            }
            var result = await DebugCodeFlowWait.AwaitAsync(record, waitMs, new UnityCliWaiter(request.RequestId, "debug.code.flow.wait"), cancellationToken);
            return CompleteOrThrow(request, "debug.code.flow.wait", result);
        }

        /// <summary>取消一条流；已终态时返回同一终态。</summary>
        private static ValueTask<DebugCodeFlowResult> CodeFlowCancel(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var record = RequireFlow(request.StringArg("flowId"));
            DebugFlowHost.Cancel(record);
            return new ValueTask<DebugCodeFlowResult>(CompleteOrThrow(request, "debug.code.flow.cancel", DebugCodeFlowWait.Project(record, false, 0d)));
        }

        // 提交路径的共用前置：模式门、源码校验、嵌套拒绝、编译与登记。
        private static async ValueTask<DebugFlowHost.FlowRecord> CreateFlowAsync(UnityCli.Protocol.BridgeRequest request, double budgetMs, CancellationToken cancellationToken)
        {
            // 模式门在编译前判定：模式不对时不消耗编译与产物额度。
            RequireFlowMode(request.StringArg("requiresPlayMode"));
            var source = request.StringArg("sourceText");
            if (string.IsNullOrWhiteSpace(source))
                throw new BridgeCommandException(
                    "E_CODE_SOURCE_INVALID",
                    "Flow source is empty.",
                    null,
                    new[] { "Pass non-empty C# source for the flow body." });
            // 嵌套提交会让两条流互相等待同一条请求：拒绝而不是排队。
            if (DebugFlowHost.IsExecutingUserCode)
                throw new BridgeCommandException(
                    "E_DEBUG_FLOW_NESTED",
                    "A flow cannot be submitted from inside another flow.",
                    null,
                    new[] { "Return from the running flow first, then submit the next one." });
            var assembly = await DebugCodeFlowCompiler.CompileAsync(source, request.StringArg("defines"), request.StringArg("refs"), request.RequestId, cancellationToken);
            var body = DebugCodeFlowCompiler.CreateBody(assembly, request.StringArg("argsJson"));
            return DebugFlowHost.Create(body, request.StringArg("sessionId"), budgetMs);
        }

        private static DebugFlowHost.FlowRecord RequireFlow(string flowId)
        {
            var record = DebugFlowHost.Find(flowId);
            if (record == null) throw DebugCodeFlowWait.NotFound(flowId);
            return record;
        }

        // 模式门：any 不限制；play 要求已在 Play；edit 要求不在 Play。
        private static void RequireFlowMode(string required)
        {
            var mode = string.IsNullOrEmpty(required) ? "any" : required;
            if (string.Equals(mode, "any", StringComparison.Ordinal)) return;
            var playing = UnityEditor.EditorApplication.isPlaying;
            if (string.Equals(mode, "play", StringComparison.Ordinal) && !playing)
                throw ModeMismatch("play", playing);
            if (string.Equals(mode, "edit", StringComparison.Ordinal) && playing)
                throw ModeMismatch("edit", playing);
            if (!string.Equals(mode, "play", StringComparison.Ordinal) && !string.Equals(mode, "edit", StringComparison.Ordinal))
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "requiresPlayMode must be one of any, play or edit.",
                    new Dictionary<string, object> { { "path", "$.requiresPlayMode" }, { "actual", mode } },
                    new[] { "Pass any, play or edit, or omit the field to accept either mode." });
        }

        private static BridgeCommandException ModeMismatch(string required, bool playing)
        {
            return new BridgeCommandException(
                "E_DEBUG_FLOW_MODE_MISMATCH",
                "This flow requires " + required + " mode but the Editor is in " + (playing ? "play" : "edit") + " mode.",
                new Dictionary<string, object>
                {
                    { "requiredMode", required },
                    { "actualMode", playing ? "play" : "edit" }
                },
                new[] { "Switch the Editor mode first, then submit the flow; a flow never spans a mode change because entering or leaving Play reloads the domain." });
        }

        /// <summary>
        /// 只有 completed 是成功终态；pending、failed、timed-out、cancelled、aborted 一律走稳定错误码。
        /// 完成驱动器只接受稳定成功终态，因此非终态与失败必须在错误信封里带齐可继续的事实。
        /// </summary>
        private static DebugCodeFlowResult CompleteOrThrow(UnityCli.Protocol.BridgeRequest request, string toolName, DebugCodeFlowResult result)
        {
            if (string.Equals(result.Status, DebugFlowHost.StateCompleted, StringComparison.Ordinal))
            {
                // 完成证据只允许登记在多变契约上：只读观察（wait）不写业务状态，因此不登记驱动证据。
                var contract = ResolveRequestCompletionContract(request);
                if (contract != null && contract.Mutation)
                    RecordDirectCompletionEvidence(request.RequestId, toolName, contract, result);
                return result;
            }

            var details = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "toolName", toolName },
                { "flowId", result.FlowId },
                { "status", result.Status },
                { "steps", result.Steps },
                { "elapsedMs", result.ElapsedMs },
                { "samples", result.Samples },
                { "budgetMs", result.BudgetMs },
                { "budgetConsumedMs", result.BudgetConsumedMs },
                { "activeFlows", result.ActiveFlows },
                { "sessionActiveFlows", result.SessionActiveFlows }
            };
            if (result.LastObserved != null) details["lastObserved"] = result.LastObserved;
            if (!string.IsNullOrEmpty(result.FailureTag)) details["failureTag"] = result.FailureTag;
            if (!string.IsNullOrEmpty(result.ErrorCode)) details["flowErrorCode"] = result.ErrorCode;
            if (!string.IsNullOrEmpty(result.ErrorMessage)) details["flowErrorMessage"] = result.ErrorMessage;

            var pending = string.Equals(result.Status, DebugFlowHost.StateRunning, StringComparison.Ordinal);
            throw new BridgeCommandException(
                RedirectCode(result.Status),
                pending
                    ? "The flow is still running; this call did not observe a terminal state."
                    : "The flow reached the terminal state " + result.Status + ".",
                details,
                pending
                    ? new[] { "Observe the same flowId with debug.code.flow.wait; never resubmit the flow to obtain its result." }
                    : new[] { "Read the details for the terminal facts; a completed flow is the only success outcome of this Tool." });
        }

        // 五种非 completed 终态的稳定错误码；调用方靠 error.code 与 details.status 一起区分。
        private static string RedirectCode(string status)
        {
            if (string.Equals(status, DebugFlowHost.StateRunning, StringComparison.Ordinal)) return "E_DEBUG_FLOW_PENDING";
            if (string.Equals(status, DebugFlowHost.StateFailed, StringComparison.Ordinal)) return "E_DEBUG_FLOW_FAILED";
            if (string.Equals(status, DebugFlowHost.StateTimedOut, StringComparison.Ordinal)) return "E_DEBUG_FLOW_TIMEOUT";
            if (string.Equals(status, DebugFlowHost.StateCancelled, StringComparison.Ordinal)) return "E_DEBUG_FLOW_CANCELLED";
            return "E_DEBUG_FLOW_ABORTED";
        }

        // 可选数值参数：缺省或 null 表示使用该参数的声明默认值。
        private static double? OptionalNumberArg(UnityCli.Protocol.BridgeRequest request, string key)
        {
            var raw = request.RawJsonArg(key);
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal)) return null;
            double value;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    key + " must be a number.",
                    new Dictionary<string, object> { { "path", "$." + key }, { "actual", raw } },
                    new[] { "Pass a numeric value, or omit the field to use the declared default." });
            return value;
        }
    }
}
#endif
