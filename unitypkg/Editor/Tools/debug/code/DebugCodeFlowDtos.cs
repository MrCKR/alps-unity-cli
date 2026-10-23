#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// submit 的结果：是一张收据而不是一个结论。registered 表示流已登记会在 tick 上推进，
    /// 因此不带 status 字段：把它当成运行中状态会被完成驱动器当成中间态拒掉。
    /// </summary>
    internal sealed class DebugCodeFlowSubmitResult
    {
        public bool Registered { get; internal set; }
        public string FlowId { get; internal set; }
        public string SessionId { get; internal set; }
        public double BudgetMs { get; internal set; }
        public double StartedMonotonicMs { get; internal set; }
        public long ActiveFlows { get; internal set; }
        public long SessionActiveFlows { get; internal set; }
        public long MaxFlowsPerInstance { get; internal set; }
        public long MaxFlowsPerSession { get; internal set; }
        public string EditorSessionId { get; internal set; }
        public string Note { get; internal set; }
    }

    /// <summary>
    /// 流结果视图。status 是唯一区分器：ok:true 不代表完成。
    /// </summary>
    internal sealed class DebugCodeFlowResult
    {
        public string Status { get; internal set; }
        public string FlowId { get; internal set; }
        public string SessionId { get; internal set; }
        public double ElapsedMs { get; internal set; }
        public long Steps { get; internal set; }
        public bool Waited { get; internal set; }
        public double WaitMs { get; internal set; }
        public object Result { get; internal set; }
        public bool ResultTruncated { get; internal set; }
        public string FailureTag { get; internal set; }
        public string ErrorCode { get; internal set; }
        public string ErrorMessage { get; internal set; }
        public object LastObserved { get; internal set; }
        public long Samples { get; internal set; }
        public double BudgetMs { get; internal set; }
        public double BudgetConsumedMs { get; internal set; }
        public long ActiveFlows { get; internal set; }
        public long SessionActiveFlows { get; internal set; }
        public long MaxFlowsPerInstance { get; internal set; }
        public long MaxFlowsPerSession { get; internal set; }
        public string EditorSessionId { get; internal set; }
        public string Note { get; internal set; }
    }

    /// <summary>
    /// 流代码的编译入口：与一次性执行共用同一编译所有权、并发额度与产物账本。
    /// 只替换包装形状（IEnumerator 入口）与装配名，不建立第二套编译器。
    /// </summary>
    internal static class DebugCodeFlowCompiler
    {
        internal const string AssemblySimpleName = "UnityCli.RuntimeFlowCode";

        // 诊断投影只保留有界条目，避免编译失败时返回超大详情。
        private const int MaxDiagnostics = 32;

        private const string Usings =
            "using System; using System.Collections; using System.Collections.Generic; using System.IO; using System.Linq; "
            + "using UnityEngine; using UnityEditor; using UnityCli; ";

        /// <summary>把调用方源码包装成 IEnumerator 入口；已声明入口的源码原样放入类体。</summary>
        internal static string WrapFlowSource(string source)
        {
            if (Regex.IsMatch(source, "\\bpublic\\s+static\\s+IEnumerator\\s+Run\\s*\\("))
                return Usings + "namespace UnityCli.RuntimeCode { public static class Script { " + source + " } }";
            return Usings + "namespace UnityCli.RuntimeCode { public static class Script { public static IEnumerator Run(string argsJson) { " + source + " } } }";
        }

        /// <summary>编译并装载流体；失败时抛带稳定 code 的 BridgeCommandException。</summary>
        internal static async ValueTask<Assembly> CompileAsync(string sourceText, string defines, string refs, string requestId, CancellationToken ct)
        {
            Func<bool> deadlineOk = () => !ct.IsCancellationRequested;
            if (!DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick))
                throw new BridgeCommandException(
                    "E_UNITY_BUSY",
                    "Another dynamic compilation currently owns the single compiler.",
                    new Dictionary<string, object> { { "owner", "dynamic-compiler" } },
                    new[] { "Wait for the current compilation to reach its terminal result, then submit this flow again." });
            try
            {
                var wrapped = WrapFlowSource(sourceText);
                var identity = DebugCompilerOwner.CandidateIdentity("debug.code-flow-execute", wrapped, defines);
                var admission = DebugCompilerOwner.TryAdmitCandidate(identity, 0L);
                if (!admission.Admitted) throw BudgetExhausted(admission);
                var references = DebugCompilerOwner.References(refs, null, deadlineOk);
                var compileRequest = new DynamicCodeCompileRequest
                {
                    SourceText = wrapped,
                    SourceFileName = "flow.cs",
                    ResponseFileName = "references.rsp",
                    AssemblyFileName = "flow.dll",
                    PdbFileName = "flow.pdb",
                    AssemblySimpleName = AssemblySimpleName,
                    Defines = defines
                };
                var compilation = await DynamicCodeCompiler.CompileAsync(
                    compileRequest,
                    references,
                    deadlineOk,
                    new UnityCliWaiter(requestId ?? "debug.code-flow-execute", "debug.code-flow-execute"),
                    TimeSpan.FromMilliseconds(DebugLimits.FlowRequestWaitMaxMs),
                    ct);
                if (compilation.TimedOut)
                    throw new BridgeCommandException(
                        "E_EXECUTION_TIMEOUT",
                        "Flow compilation exceeded the immutable deadline.",
                        null,
                        new[] { "Inspect the Editor state before submitting the flow again." });
                if (!compilation.Success)
                {
                    if (!string.IsNullOrEmpty(compilation.FailureCode))
                        throw new BridgeCommandException(compilation.FailureCode, compilation.FailureMessage, null, new[] { "Fix the request input and submit the flow again." });
                    var diagnostics = compilation.Diagnostics;
                    var details = new Dictionary<string, object> { { "diagnostics", CompactDiagnostics(diagnostics) } };
                    var referenceFailure = diagnostics.FirstOrDefault(item => string.Equals((string)item["id"], "CS0006", StringComparison.OrdinalIgnoreCase));
                    if (referenceFailure != null)
                        throw new BridgeCommandException(
                            "E_REFERENCE_RESOLUTION_FAILED",
                            "Flow reference was not found or readable: " + (string)referenceFailure["message"],
                            details,
                            new[] { "Check Unity/Editor/project assembly references and refs." });
                    throw new BridgeCommandException(
                        "E_CODE_COMPILE_FAILED",
                        "Flow source failed to compile.",
                        details,
                        new[] { "Fix compiler diagnostics and submit the flow again." });
                }
                var loadAdmission = DebugCompilerOwner.TryAdmitCandidate(identity, compilation.AssemblyBytes.LongLength);
                if (!loadAdmission.Admitted) throw BudgetExhausted(loadAdmission);
                var assembly = Assembly.Load(compilation.AssemblyBytes);
                // 装载成功即占用：Mono 不能逐个卸载程序集，后续入口校验失败也不回收。
                DebugCompilerOwner.CommitLoadedArtifact(identity, compilation.AssemblyBytes.LongLength, assembly.GetName().Name);
                return assembly;
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            }
        }

        /// <summary>取得并校验 IEnumerator 入口。</summary>
        internal static IEnumerator CreateBody(Assembly assembly, string argsJson)
        {
            var type = assembly.GetType("UnityCli.RuntimeCode.Script", true);
            var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
            if (method == null
                || method.GetParameters().Length != 1
                || method.GetParameters()[0].ParameterType != typeof(string)
                || !typeof(IEnumerator).IsAssignableFrom(method.ReturnType))
                throw new BridgeCommandException(
                    "E_CODE_ENTRYPOINT_INVALID",
                    "Flow source requires public static IEnumerator Run(string argsJson).",
                    null,
                    new[] { "Use the default wrapped body, or declare exactly that entry point." });
            try
            {
                return (IEnumerator)method.Invoke(null, new object[] { string.IsNullOrEmpty(argsJson) ? "{}" : argsJson });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                throw new BridgeCommandException(
                    "E_DEBUG_FLOW_USER_CODE_FAILED",
                    UnityCliBridge.BoundedText(inner.GetType().Name + ": " + inner.Message, 2048),
                    null,
                    new[] { "Fix the flow body; the entry threw before returning an iterator." });
            }
        }

        private static BridgeCommandException BudgetExhausted(DebugCompilerAdmission admission)
        {
            return new BridgeCommandException(
                DebugCompilerOwner.BudgetExhaustedCode,
                "This domain cannot load another compiled flow artifact: " + admission.Reason + ".",
                new Dictionary<string, object>
                {
                    { "reason", admission.Reason },
                    { "budget", admission.Report.ToData() }
                },
                new[] { DebugCompilerOwner.ReloadSuggestion });
        }

        private static List<Dictionary<string, object>> CompactDiagnostics(List<Dictionary<string, object>> diagnostics)
        {
            var compact = new List<Dictionary<string, object>>();
            if (diagnostics == null) return compact;
            foreach (var item in diagnostics.Take(MaxDiagnostics))
            {
                compact.Add(new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    { "severity", item.ContainsKey("severity") ? item["severity"] : string.Empty },
                    { "id", item.ContainsKey("id") ? item["id"] : string.Empty },
                    { "message", UnityCliBridge.BoundedText(Convert.ToString(item.ContainsKey("message") ? item["message"] : string.Empty, CultureInfo.InvariantCulture), 512) },
                    { "line", item.ContainsKey("line") ? item["line"] : 0 }
                });
            }
            return compact;
        }
    }

    /// <summary>
    /// 三个流调用共享的等待与视图投影。
    /// waitMs 只约束本次调用等待多久；到期返回当前状态，不取消流，也不改变终态。
    /// </summary>
    internal static class DebugCodeFlowWait
    {
        internal static double ValidateWaitMs(double? requested)
        {
            var waitMs = requested ?? DebugLimits.FlowRequestWaitDefaultMs;
            if (double.IsNaN(waitMs) || waitMs < 0d || waitMs > DebugLimits.FlowRequestWaitMaxMs)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "waitMs must be inside the declared wait budget.",
                    new Dictionary<string, object>
                    {
                        { "path", "$.waitMs" },
                        { "expected", "0.." + DebugLimits.FlowRequestWaitMaxMs.ToString(CultureInfo.InvariantCulture) },
                        { "actual", waitMs.ToString(CultureInfo.InvariantCulture) }
                    },
                    new[] { "Pass a smaller waitMs, or omit it to use the declared default." });
            return waitMs;
        }

        internal static double ValidateBudget(double? requested)
        {
            var budget = requested ?? DebugLimits.FlowBudgetDefaultMs;
            if (double.IsNaN(budget) || budget <= 0d || budget > DebugLimits.FlowBudgetMaxMs)
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "flowBudgetMs must be inside the declared flow budget.",
                    new Dictionary<string, object>
                    {
                        { "path", "$.flowBudgetMs" },
                        { "expected", "1.." + DebugLimits.FlowBudgetMaxMs.ToString(CultureInfo.InvariantCulture) },
                        { "actual", budget.ToString(CultureInfo.InvariantCulture) }
                    },
                    new[] { "Pass a positive flowBudgetMs inside the declared maximum, or omit it to use the default." });
            return budget;
        }

        /// <summary>有界等待终态；到期只是一次观察，不是失败，也不取消流。</summary>
        internal static async ValueTask<DebugCodeFlowResult> AwaitAsync(DebugFlowHost.FlowRecord record, double waitMs, IUnityCliWaiter waiter, CancellationToken ct)
        {
            var deadline = DebugEventClocks.MonotonicMilliseconds + waitMs;
            var waited = false;
            while (!record.Terminal && DebugEventClocks.MonotonicMilliseconds < deadline && !ct.IsCancellationRequested)
            {
                waited = true;
                await waiter.NextFrame(1, ct);
            }
            return Project(record, waited, waitMs);
        }

        internal static DebugCodeFlowResult Project(DebugFlowHost.FlowRecord record, bool waited, double waitMs)
        {
            return new DebugCodeFlowResult
            {
                Status = record.State,
                FlowId = record.FlowId,
                SessionId = record.SessionId,
                ElapsedMs = record.ConsumedMs,
                Steps = record.Steps,
                Waited = waited,
                WaitMs = waitMs,
                Result = record.Result,
                ResultTruncated = record.ResultTruncated,
                FailureTag = record.FailureTag,
                ErrorCode = record.ErrorCode,
                ErrorMessage = record.ErrorMessage,
                LastObserved = record.LastObserved,
                Samples = record.Samples,
                BudgetMs = record.BudgetMs,
                BudgetConsumedMs = record.ConsumedMs,
                ActiveFlows = DebugFlowHost.ActiveCount,
                SessionActiveFlows = DebugFlowHost.ActiveForSession(record.SessionId),
                MaxFlowsPerInstance = DebugLimits.MaxFlowsPerInstance,
                MaxFlowsPerSession = DebugLimits.MaxFlowsPerSession,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                Note = "status is the only discriminator. A running status is not a completion: wait the same flowId again, and never resubmit the flow to obtain its result. Elapsed time counts only unpaused time and excludes compilation."
            };
        }

        internal static BridgeCommandException NotFound(string flowId)
        {
            return new BridgeCommandException(
                "E_DEBUG_FLOW_NOT_FOUND",
                "No flow exists for this flowId in the current Editor session.",
                new Dictionary<string, object> { { "flowId", flowId ?? string.Empty } },
                new[]
                {
                    "A flow does not survive a Domain Reload, an Editor restart or a Play mode exit; submit a new one.",
                    "Check the flowId; terminal flows stay readable only for a bounded number of records."
                });
        }
    }
}
#endif
