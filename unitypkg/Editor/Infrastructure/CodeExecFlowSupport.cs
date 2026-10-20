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
    /// debug.code.flow.execute / -Wait / -Cancel 的 Bridge 命令面。
    ///
    /// 三个命令与 debug.code.execute 同族：共享源码读取、编译所有权与产物账本；
    /// 差别只在返回形态（句柄或终态）与推进方式（跨帧而不是同步到底）。
    /// </summary>
    public static partial class UnityCliBridge
    {
        private const int FlowSubmitTimeoutMs = 140000;
        private const int FlowWaitTimeoutMs = 140000;
        private const int FlowCancelTimeoutMs = 30000;

        // 注册跨帧流命令。timeout 只是调用方等待上限，流的生命周期由 flowBudgetMs 单独约束。
        private static void RegisterCodeFlowCommands(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.execute",
                "code",
                true,
                FlowSubmitTimeoutMs,
                async (request, cancellationToken) => (object)await CodeExecuteAsync(request, cancellationToken));
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.wait",
                "code",
                false,
                FlowWaitTimeoutMs,
                async (request, cancellationToken) => (object)await CodeExecuteAsyncWait(request, cancellationToken));
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "debug.code.flow.cancel",
                "code",
                true,
                FlowCancelTimeoutMs,
                async (request, cancellationToken) => (object)await CodeExecuteAsyncCancel(request, cancellationToken));
        }

        /// <summary>提交一条流：编译一次后登记，等待预算内到达终态则直接返回结果。</summary>
        private static async ValueTask<DebugCodeFlowResult> CodeExecuteAsync(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
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
            var waitMs = DebugCodeFlowWait.ValidateWaitMs(OptionalNumberArg(request, "waitMs"));
            var budgetMs = DebugCodeFlowWait.ValidateBudget(OptionalNumberArg(request, "flowBudgetMs"));
            // 模式门在编译前判定：模式不对时不消耗编译与产物额度。
            RequireFlowMode(request.StringArg("requiresPlayMode"));
            var assembly = await DebugCodeFlowCompiler.CompileAsync(source, request.StringArg("defines"), request.StringArg("refs"), request.RequestId, cancellationToken);
            var body = DebugCodeFlowCompiler.CreateBody(assembly, request.StringArg("argsJson"));
            var record = DebugFlowHost.Create(body, request.StringArg("sessionId"), budgetMs);
            var result = await DebugCodeFlowWait.AwaitAsync(record, waitMs, new UnityCliWaiter(request.RequestId, "debug.code.flow.execute"), cancellationToken);
            return CompleteOrThrow(request, "debug.code.flow.execute", result);
        }

        /// <summary>只观察一条已存在的流；不编译、不执行、不取消。</summary>
        private static async ValueTask<DebugCodeFlowResult> CodeExecuteAsyncWait(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var waitMs = DebugCodeFlowWait.ValidateWaitMs(OptionalNumberArg(request, "waitMs"));
            var record = RequireFlow(request.StringArg("flowId"));
            var result = await DebugCodeFlowWait.AwaitAsync(record, waitMs, new UnityCliWaiter(request.RequestId, "debug.code.flow.wait"), cancellationToken);
            return CompleteOrThrow(request, "debug.code.flow.wait", result);
        }

        /// <summary>取消一条流；已终态时返回同一终态。</summary>
        private static ValueTask<DebugCodeFlowResult> CodeExecuteAsyncCancel(UnityCli.Protocol.BridgeRequest request, CancellationToken cancellationToken)
        {
            var record = RequireFlow(request.StringArg("flowId"));
            DebugFlowHost.Cancel(record);
            return new ValueTask<DebugCodeFlowResult>(CompleteOrThrow(request, "debug.code.flow.cancel", DebugCodeFlowWait.Project(record, false, 0d)));
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
            if (string.Equals(status, DebugFlowHost.StateTimedOut, StringComparison.Ordinal)) return "E_DEBUG_FLOW_TIMED_OUT";
            if (string.Equals(status, DebugFlowHost.StateCancelled, StringComparison.Ordinal)) return "E_DEBUG_FLOW_CANCELLED";
            return "E_DEBUG_FLOW_ABORTED";
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

        private static DebugFlowHost.FlowRecord RequireFlow(string flowId)
        {
            var record = DebugFlowHost.Find(flowId);
            if (record == null) throw DebugCodeFlowWait.NotFound(flowId);
            return record;
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
