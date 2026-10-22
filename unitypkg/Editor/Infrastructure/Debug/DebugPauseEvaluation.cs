#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>
    /// 暂停现场上的显式调试代码与已安装入口执行。
    ///
    /// 规则：
    /// - 只在暂停线程上执行；编译所有权、引用快照与已加载产物预算全部来自唯一 owner <see cref="DebugCompilerOwner"/>，
    ///   本类不新建编译队列、预算或临时区，也不新注册 update 回调。
    /// - 编译在暂停服务内部有界推进（与主线程 lane 互斥持有同一所有权），因此主线程不 tick 时仍可编译。
    /// - 用户同步代码不返回时本类无法抢占它：现场保持 paused，调用方只能得到 pending。
    /// - 不捕获进程 stdout：暂停现场可能就在主线程上，重定向全局 Console 会改写其他组件的行为。
    /// </summary>
    internal static class DebugPauseEvaluation
    {
        internal static DebugPauseEvaluateResult Run(DebugPauseSite site, DebugPauseOperation operation)
        {
            var request = operation.EvaluateRequest ?? new DebugPauseEvaluateRequest();
            var invocation = site.Invocation;
            var diagnostics = new List<string>();
            var returnValue = default(object);
            var sourceKind = "code";
            var compileKey = string.Empty;
            var started = DebugEventClocks.MonotonicMilliseconds;

            var argumentsStagedBefore = invocation is MethodHookInvocation && ((MethodHookInvocation)invocation).HasStagedArguments;
            var resultStagedBefore = invocation is MethodHookInvocation && ((MethodHookInvocation)invocation).HasStagedResult;
            var fieldWritesBefore = invocation is MethodHookInvocation ? ((MethodHookInvocation)invocation).FieldWrites : 0;
            var emittedBefore = invocation is MethodHookInvocation ? ((MethodHookInvocation)invocation).Emitted.Count : 0;
            var argumentsBefore = invocation.Arguments == null ? Array.Empty<object>() : (object[])invocation.Arguments.Clone();

            if (request.ProgramEntry != null && !string.IsNullOrWhiteSpace(request.ProgramEntry.ProgramId))
            {
                sourceKind = "program-entry";
                RunProgramEntry(site, request.ProgramEntry);
            }
            else
            {
                var source = !string.IsNullOrWhiteSpace(request.SourceText) ? request.SourceText : request.Code;
                if (string.IsNullOrWhiteSpace(source))
                    throw new BridgeCommandException(
                        "E_SCHEMA_INVALID",
                        "Field evaluate requires exactly one of sourceText/code or programEntry.",
                        new Dictionary<string, object> { { "path", "$.code" }, { "pauseId", site.PauseId } },
                        new[] { "Submit debug code with sourceText, or name an installed program entry with programEntry." });
                returnValue = RunCode(source, SerializeArgs(request.Args), diagnostics, operation, out compileKey);
            }

            var hookInvocation = invocation as MethodHookInvocation;
            var interventions = new List<string>();
            if (hookInvocation != null)
            {
                if (!argumentsStagedBefore && hookInvocation.HasStagedArguments) interventions.Add("argument-write");
                else if (hookInvocation.HasStagedArguments)
                {
                    for (var index = 0; index < argumentsBefore.Length && index < hookInvocation.Arguments.Length; index++)
                    {
                        if (!Equals(argumentsBefore[index], hookInvocation.Arguments[index])) { interventions.Add("argument-write"); break; }
                    }
                }
                if (hookInvocation.SkipRequested) interventions.Add("skip-original");
                if (!resultStagedBefore && hookInvocation.HasStagedResult) interventions.Add("result-write");
                var fieldWrites = hookInvocation.FieldWrites - fieldWritesBefore;
                if (fieldWrites > 0) interventions.Add("field-writes:" + fieldWrites.ToString(CultureInfo.InvariantCulture));
                var emitted = hookInvocation.Emitted.Count - emittedBefore;
                if (emitted > 0) interventions.Add("emitted:" + emitted.ToString(CultureInfo.InvariantCulture));
            }
            if (string.Equals(sourceKind, "program-entry", StringComparison.Ordinal)) interventions.Add("program-entry:" + (request.ProgramEntry == null ? string.Empty : request.ProgramEntry.Entry));
            if (site.Position == DebugPausePositions.Postfix && interventions.Contains("argument-write"))
                interventions.Add("argument-write-ignored:arguments are only honoured at a prefix boundary");
            bool returnValueTruncated;
            var projectedReturnValue = ProjectReturnValue(returnValue, site.MainThread, out returnValueTruncated);
            return new DebugPauseEvaluateResult
            {
                PauseId = site.PauseId,
                ExecutionState = DebugPauseExecutionStates.Completed,
                Completed = true,
                ServedOnPausedThread = true,
                ServedOnThreadId = Thread.CurrentThread.ManagedThreadId,
                ReturnValue = projectedReturnValue,
                ReturnValueTruncated = returnValueTruncated,
                DurationMs = DebugEventClocks.MonotonicMilliseconds - started,
                SourceKind = sourceKind,
                CompileKey = compileKey,
                KnownInterventions = interventions,
                CompileDiagnostics = diagnostics,
                OutputNote = "Process stdout is not captured or redirected for an on-site evaluation; return a value instead of printing.",
                PendingNote = string.Empty,
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DomainGeneration = DebugSessionHost.DomainGeneration()
            };
        }

        /// <summary>已安装 program 命名入口：必须声明并 attach 到本现场对应的 target 与 phase。</summary>
        private static void RunProgramEntry(DebugPauseSite site, DebugPauseProgramEntrySpec entry)
        {
            var phase = site.Position == DebugPausePositions.Postfix
                ? UnityCliDebugProgramBindingPhases.Postfix
                : UnityCliDebugProgramBindingPhases.Prefix;
            DebugProgramInfoResult info;
            try
            {
                info = DebugProgramHost.Info(new DebugProgramInfoRequest { ProgramId = entry.ProgramId });
            }
            catch (BridgeCommandException)
            {
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_ENTRY_INVALID",
                    "No installed program with that programId exists in the current Editor session.",
                    new Dictionary<string, object> { { "programId", entry.ProgramId }, { "entry", entry.Entry }, { "pauseId", site.PauseId } },
                    new[] { "Run tools run debug.program.list to read the installed programs.", "Install the entry with debug.program.install before using it at a pause site." });
            }
            var bindings = info.Program == null ? null : info.Program.Bindings;
            var bound = bindings != null && bindings.Any(binding => binding.Attached
                && string.Equals(binding.Entry, entry.Entry, StringComparison.Ordinal)
                && string.Equals(binding.Phase, phase, StringComparison.Ordinal)
                && string.Equals(binding.HookId, site.HookId, StringComparison.Ordinal));
            if (!bound)
                throw new BridgeCommandException(
                    "E_DEBUG_PROGRAM_ENTRY_INVALID",
                    "The named program entry is not attached as a binding on this exact target and phase, so running it here would not be the declared entry.",
                    new Dictionary<string, object>
                    {
                        { "programId", entry.ProgramId },
                        { "entry", entry.Entry },
                        { "phase", phase },
                        { "hookId", site.HookId },
                        { "pauseId", site.PauseId },
                        { "attachedBindings", (long)(bindings == null ? 0 : bindings.Count(binding => binding.Attached)) }
                    },
                    new[]
                    {
                        "Run tools run debug.program.info to read the declared bindings and their attached state.",
                        "Replace the program with a binding whose entry, phase and exact target match this pause site."
                    });
            var context = new UnityCliHookContext(site.Invocation);
            DebugProgramHost.DispatchMethod(entry.ProgramId, entry.Entry, context, site.Position == DebugPausePositions.Prefix);
        }

        /// <summary>
        /// 在暂停线程上编译并调用显式调试代码。
        /// 编译所有权与主线程 lane 互斥；取不到时返回忙，现场继续保留并仍可 inspect/resume。
        /// 整个 on-site 操作（含引用解析与编译）共用一个不可续期的预算：预算到期只说明共享编译没跑完，
        /// 用独立稳定码如实归因，绝不冒充"用户代码未返回"或"编译失败"。
        /// </summary>
        private static object RunCode(string source, string argsJson, List<string> diagnostics, DebugPauseOperation operation, out string compileKey)
        {
            compileKey = string.Empty;
            var cancellationToken = operation.Cancellation.Token;
            if (cancellationToken.IsCancellationRequested)
                throw new BridgeCommandException(
                    "E_DEBUG_PAUSE_STALE",
                    "The pause site left the paused state before this evaluation started, so no code was compiled or run.",
                    new Dictionary<string, object> { { "stage", "before-compile" } },
                    new[] { "Run tools run debug.pause.list to read the sites that are paused right now." });
            var acquireBudget = TimeSpan.FromMilliseconds(DebugLimits.PauseCompilerAcquireMs);
            if (!DebugCompilerOwner.TryBeginOwnershipWithin(DebugCompilerLane.PauseService, acquireBudget))
                throw new BridgeCommandException(
                    "E_UNITY_BUSY",
                    "Another dynamic compilation currently owns the single compiler, so this on-site evaluation was not started.",
                    new Dictionary<string, object> { { "owner", "dynamic-compiler" }, { "lane", "pause-service" } },
                    new[] { "Wait for the current compilation to reach its terminal result, then submit the evaluation again; the pause site is still held." });
            try
            {
                var wrapped = UnityCliBridge.WrapCodeSource(source);
                compileKey = DebugCompilerOwner.CandidateIdentity("debug.pause.evaluate", wrapped, string.Empty);
                var admission = DebugCompilerOwner.TryAdmitCandidate(compileKey, 0L);
                if (!admission.Admitted) throw BudgetExhausted(admission);
                var deadlineMs = DebugEventClocks.MonotonicMilliseconds + DebugLimits.PauseOperationTimeoutMs;
                Func<bool> deadlineOk = () => DebugEventClocks.MonotonicMilliseconds < deadlineMs;
                var references = ResolveReferencesWithinBudget(deadlineOk);
                var run = DynamicCodeCompiler.StartCompilation(
                    new DynamicCodeCompileRequest
                    {
                        SourceText = wrapped,
                        AssemblySimpleName = "UnityCli.PauseEvaluate",
                        MaxSourceBytes = DebugLimits.MaxProgramSourceBytes,
                        MaxArtifactBytes = DebugLimits.MaxSingleArtifactBytes
                    },
                    references,
                    deadlineOk,
                    cancellationToken);
                // 有界推进入口在本 lane 持有所有权时返回 false 只表示编译尚未终态，因此必须像主线程 lane 一样
                // 在预算内反复推进；把"尚未终态"当成编译不可用会立即报出从未到达的 deadline。
                while (!run.Terminal && deadlineOk())
                {
                    DebugCompilerOwner.AdvanceOwnedCompilation(run, DebugCompilerLane.PauseService);
                    if (!run.Terminal) Thread.Sleep(2);
                }
                if (!run.Terminal)
                {
                    // 预算到期时终止本次共享编译并删除固定临时文件（owner 的既有包含策略），现场仍然停住、仍可 inspect/resume。
                    DynamicCodeCompiler.AbortCompilation(run);
                    throw CompileBudgetExceeded("compile");
                }
                var compilation = DynamicCodeCompiler.CompleteCompilation(run);
                if (compilation.TimedOut)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new BridgeCommandException(
                            "E_DEBUG_PAUSE_STALE",
                            "The pause site was released while this evaluation was compiling, so the compile was cancelled and no code ran.",
                            new Dictionary<string, object> { { "stage", "compile" }, { "cancelled", true } },
                            new[] { "Read debug.pause.list to confirm the site state, then submit the evaluation again if a site is still paused." });
                    throw CompileBudgetExceeded("compile-output");
                }
                if (!compilation.Success)
                {
                    foreach (var item in compilation.Diagnostics)
                    {
                        var id = item.ContainsKey("id") ? Convert.ToString(item["id"], CultureInfo.InvariantCulture) : string.Empty;
                        var message = item.ContainsKey("message") ? Convert.ToString(item["message"], CultureInfo.InvariantCulture) : string.Empty;
                        diagnostics.Add((id + " " + message).Trim());
                    }
                    throw new BridgeCommandException(
                        "E_CODE_COMPILE_FAILED",
                        string.IsNullOrEmpty(compilation.FailureMessage)
                            ? "The on-site evaluation source did not compile."
                            : compilation.FailureMessage,
                        new Dictionary<string, object> { { "diagnostics", diagnostics.Cast<object>().ToList() } },
                        new[] { "Fix the compiler diagnostics and submit the evaluation again; the pause site is still held." });
                }
                var loadAdmission = DebugCompilerOwner.TryAdmitCandidate(compileKey, compilation.AssemblyBytes.LongLength);
                if (!loadAdmission.Admitted) throw BudgetExhausted(loadAdmission);
                var assembly = Assembly.Load(compilation.AssemblyBytes);
                // 加载成功即占用：Mono 不能逐个卸载，后续 ABI 校验失败也不扣减。
                DebugCompilerOwner.CommitLoadedArtifact(compileKey, compilation.AssemblyBytes.LongLength, assembly.GetName().Name);
                var type = assembly.GetType("UnityCli.RuntimeCode.Script", true);
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                if (method == null || method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(string))
                    throw new BridgeCommandException(
                        "E_CODE_ENTRYPOINT_INVALID",
                        "On-site evaluation requires public static object Run(string argsJson).",
                        new Dictionary<string, object> { { "type", "UnityCli.RuntimeCode.Script" } },
                        new[] { "Use the default wrapped source body or declare the exact Run signature." });
                try
                {
                    return method.Invoke(null, new object[] { argsJson ?? "{}" });
                }
                catch (TargetInvocationException exception)
                {
                    var inner = exception.InnerException ?? exception;
                    throw new BridgeCommandException(
                        "E_CODE_RUNTIME_EXCEPTION",
                        "The on-site evaluation threw: " + inner.GetType().Name + ".",
                        new Dictionary<string, object> { { "type", inner.GetType().FullName ?? inner.GetType().Name }, { "message", inner.Message ?? string.Empty } },
                        new[] { "Read debug.pause.inspect to see the paused state before submitting a corrected evaluation." });
                }
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.PauseService);
            }
        }

        /// <summary>
        /// 现场编译的引用快照解析。它与编译共用同一个不可续期的现场操作预算；
        /// 预算在解析阶段到期只说明共享编译没能在预算内起步，不是引用解析失败，也不是用户代码未返回。
        /// </summary>
        private static List<string> ResolveReferencesWithinBudget(Func<bool> deadlineOk)
        {
            try
            {
                return DebugCompilerOwner.References(null, null, deadlineOk);
            }
            catch (OperationCanceledException)
            {
                throw CompileBudgetExceeded("reference-resolution");
            }
        }

        /// <summary>
        /// 共享编译没能在现场操作预算内产出可用结果时的唯一归因点。
        /// 该情况不释放暂停现场，也不声称用户代码已运行或未返回：调用方可以重试，或在非暂停路径预热共享编译后重试。
        /// </summary>
        private static BridgeCommandException CompileBudgetExceeded(string stage)
        {
            return new BridgeCommandException(
                "E_COMPILER_BUDGET_EXHAUSTED",
                "The on-site compilation did not reach a usable result inside the "
                    + DebugLimits.PauseOperationTimeoutMs.ToString(CultureInfo.InvariantCulture)
                    + " ms pause operation budget, so this evaluation compiled, loaded and ran no code. The shared compile was"
                    + " stopped for this operation; this is not a compile failure and not user code that failed to return.",
                new Dictionary<string, object>
                {
                    { "stage", stage },
                    { "budgetMs", (long)DebugLimits.PauseOperationTimeoutMs }
                },
                new[]
                {
                    "Run debug.pause.list to confirm the site is still paused and inspectable; a compile-budget overrun never releases a pause site.",
                    "Warm the single shared compiler on the non-pause path first: run debug.code.execute with the same source, then submit debug.pause.evaluate again.",
                    "Or release the site with debug.pause.resume and evaluate the same source outside the pause path."
                });
        }

        private static BridgeCommandException BudgetExhausted(DebugCompilerAdmission admission)
        {
            return new BridgeCommandException(
                DebugCompilerOwner.BudgetExhaustedCode,
                "This domain cannot load another compiled artifact: " + admission.Reason + ".",
                new Dictionary<string, object>
                {
                    { "reason", admission.Reason },
                    { "budget", admission.Report.ToData() }
                },
                new[] { DebugCompilerOwner.ReloadSuggestion });
        }

        private static string SerializeArgs(object args)
        {
            if (args == null) return "{}";
            try { return UnityCliSerializer.Serialize(args); }
            catch (Exception exception)
            {
                throw new BridgeCommandException(
                    "E_SCHEMA_INVALID",
                    "Field args could not be serialized: " + exception.GetType().Name + ".",
                    new Dictionary<string, object> { { "path", "$.args" } },
                    new[] { "Submit args as a bounded JSON object or array." });
            }
        }

        /// <summary>返回值按同一投影边界物化并输出裁剪事实；暂停在 worker 上时不得读取 Unity 主线程显示身份。</summary>
        private static object ProjectReturnValue(object value, bool mainThread, out bool truncated)
        {
            truncated = false;
            if (value == null) return null;
            var truncation = new MethodHookProjection.Truncation();
            try
            {
                var projected = MethodHookProjection.Project(value, truncation, mainThread);
                truncated = truncation.Truncated;
                return projected;
            }
            catch (Exception exception)
            {
                // 投影失败不是“值被裁剪”：这里保持 truncated=false，由显式的不可用标记表明该值本身不可作为业务结果。
                return new Dictionary<string, object> { { "unavailable", "The return value could not be projected: " + exception.GetType().Name + "." } };
            }
        }
    }
}
#endif
