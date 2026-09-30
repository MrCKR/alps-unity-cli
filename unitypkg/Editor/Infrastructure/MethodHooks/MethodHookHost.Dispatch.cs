#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>拦截点派发：按定义顺序执行回调或预编译捕获，并处理故障、重入与线程约束。</summary>
    internal static partial class MethodHookHost
    {
        /// <summary>Prefix 派发；host 缺席或 gate 关闭时保持原方法语义。</summary>
        internal static bool DispatchPrefixCore(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            if (!initialized) return true;
            MethodHookTargetRuntime runtime;
            if (!DispatchTargets.TryGetValue(original, out runtime) || !runtime.GateOpen || runtime.Definitions.Count == 0) return true;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                RecordThreadFault(runtime, original);
                return true;
            }
            List<MethodHookDefinition> ordered;
            lock (Sync) ordered = runtime.Ordered();
            var runOriginal = true;
            var stamp = CurrentStamp();
            foreach (var definition in ordered)
            {
                if (definition.State != MethodHookState.Armed) continue;
                if (!definition.ActivationMatches(playScope)) continue;
                if (IsReentrant(definition.HookId))
                {
                    definition.BypassCount++;
                    continue;
                }
                definition.SampleIndex++;
                var record = definition.EmitHitEvents && Sample(definition);
                var invocation = new MethodHookInvocation(definition, instance, arguments, result, stamp, true) { InvocationId = ++invocationSequence };
                var pending = new List<MethodHookEventRecord>();
                var overrun = false;
                EnterCallback(definition.HookId);
                try
                {
                    overrun = InvokeCallback(definition, invocation, true);
                    pending.AddRange(invocation.Emitted);
                    if (record) pending.Add(BuildInvocationEvent(definition, invocation, "prefix", arguments, null));
                }
                catch (Exception exception)
                {
                    ExitCallback(definition.HookId);
                    FaultDefinition(definition, "E_HOOK_CALLBACK_FAILED", Describe(exception), invocation.InvocationId, "prefix");
                    continue;
                }
                ExitCallback(definition.HookId);
                if (invocation.HasStagedArguments)
                {
                    var staged = invocation.StagedArguments;
                    for (var index = 0; index < arguments.Length && index < staged.Length; index++) arguments[index] = staged[index];
                }
                if (invocation.HasStagedResult)
                {
                    resultOut = invocation.StagedResult;
                    writeResult = true;
                }
                if (invocation.SkipRequested) runOriginal = false;
                definition.HitCount++;
                PushFrame(definition.HookId, invocation.InvocationId, record);
                foreach (var item in pending) Events.Append(item);
                if (overrun) FaultDefinition(definition, "E_HOOK_CALLBACK_OVERRUN", "The callback returned after exceeding the overrun threshold.", invocation.InvocationId, "prefix");
            }
            return runOriginal;
        }

        /// <summary>Postfix 派发；只写回显式请求的结果替换。</summary>
        internal static void DispatchPostfixCore(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            if (!initialized) return;
            MethodHookTargetRuntime runtime;
            if (!DispatchTargets.TryGetValue(original, out runtime) || !runtime.GateOpen || runtime.Definitions.Count == 0) return;
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
            {
                RecordThreadFault(runtime, original);
                return;
            }
            List<MethodHookDefinition> ordered;
            lock (Sync) ordered = runtime.Ordered();
            var stamp = CurrentStamp();
            // Postfix 按已公布顺序串联结果：后一个定义看到前一个的替换结果，与 Harmony 的 ref __result 语义一致。
            var accumulatedResult = result;
            var anyStagedResult = false;
            foreach (var definition in ordered)
            {
                if (definition.State != MethodHookState.Armed) continue;
                if (!definition.ActivationMatches(playScope)) continue;
                if (IsReentrant(definition.HookId))
                {
                    definition.BypassCount++;
                    continue;
                }
                long invocationId;
                bool paired;
                var record = PopFrame(definition.HookId, out invocationId);
                paired = invocationId > 0;
                if (!paired && definition.EmitHitEvents)
                {
                    definition.SampleIndex++;
                    record = Sample(definition);
                }
                var invocation = new MethodHookInvocation(definition, instance, arguments, accumulatedResult, stamp, false) { InvocationId = paired ? invocationId : ++invocationSequence };
                var pending = new List<MethodHookEventRecord>();
                var overrun = false;
                EnterCallback(definition.HookId);
                try
                {
                    overrun = InvokeCallback(definition, invocation, false);
                    pending.AddRange(invocation.Emitted);
                    if (record) pending.Add(BuildInvocationEvent(definition, invocation, "postfix", arguments, accumulatedResult));
                }
                catch (Exception exception)
                {
                    ExitCallback(definition.HookId);
                    FaultDefinition(definition, "E_HOOK_CALLBACK_FAILED", Describe(exception), invocation.InvocationId, "postfix");
                    continue;
                }
                ExitCallback(definition.HookId);
                if (invocation.HasStagedResult)
                {
                    accumulatedResult = invocation.StagedResult;
                    anyStagedResult = true;
                }
                definition.HitCount++;
                foreach (var item in pending) Events.Append(item);
                if (overrun) FaultDefinition(definition, "E_HOOK_CALLBACK_OVERRUN", "The callback returned after exceeding the overrun threshold.", invocation.InvocationId, "postfix");
            }
            if (anyStagedResult)
            {
                resultOut = accumulatedResult;
                writeResult = true;
            }
        }

        /// <summary>内部 Finalizer：原方法抛出异常时记录结束事件，不吞掉也不替换原异常。</summary>
        internal static void DispatchFinalizerCore(Exception exception, MethodBase original)
        {
            if (!initialized || exception == null) return;
            MethodHookTargetRuntime runtime;
            if (!DispatchTargets.TryGetValue(original, out runtime) || runtime.Definitions.Count == 0) return;
            var stamp = CurrentStamp();
            foreach (var definition in runtime.Definitions)
            {
                if (definition.State != MethodHookState.Armed && definition.State != MethodHookState.Faulted && definition.State != MethodHookState.Restoring) continue;
                var invocationId = PopImplementationFrame(definition.HookId);
                Events.Append(new MethodHookEventRecord
                {
                    HookId = definition.HookId,
                    Revision = definition.Revision,
                    InvocationId = invocationId,
                    Kind = "lifecycle",
                    PlaySessionId = stamp.PlaySessionId,
                    ClockSegment = stamp.ClockSegment,
                    ElapsedMilliseconds = stamp.ElapsedMilliseconds,
                    Frame = stamp.Frame,
                    MethodIdentity = MethodHookMethodText.Describe(original),
                    Name = "original-exception",
                    Value = MethodHookProjection.Project(exception, new MethodHookProjection.Truncation())
                });
            }
        }

        /// <summary>调用回调并测量耗时；超过阈值说明只是事后保护，不是抢占式超时。</summary>
        private static bool InvokeCallback(MethodHookDefinition definition, MethodHookInvocation invocation, bool prefix)
        {
            if (definition.Callback == null) return false;
            var watch = Stopwatch.StartNew();
            var context = new UnityCliHookContext(invocation);
            if (prefix) definition.Callback.Prefix(context);
            else definition.Callback.Postfix(context);
            watch.Stop();
            return watch.ElapsedMilliseconds > MethodHookLimits.CallbackOverrunMs;
        }

        private static bool Sample(MethodHookDefinition definition)
        {
            var every = definition.Capture == null ? 1 : Math.Max(1, definition.Capture.SampleEvery);
            return every <= 1 || definition.SampleIndex % every == 0;
        }

        /// <summary>构造一次 Invocation 事件；捕获字段访问失败按字段级原因记录。</summary>
        private static MethodHookEventRecord BuildInvocationEvent(MethodHookDefinition definition, MethodHookInvocation invocation, string kind, object[] arguments, object result)
        {
            var truncation = new MethodHookProjection.Truncation();
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                CaptureFields(definition, invocation, definition.Capture.InstanceFields, fields, truncation);
                CaptureFields(definition, invocation, definition.Capture.StaticFields, fields, truncation);
            }
            catch (Exception exception)
            {
                fields["captureError"] = Describe(exception);
                truncation.Mark();
            }
            object projectedArguments = null;
            if (definition.Capture.Arguments)
                projectedArguments = MethodHookProjection.Project(arguments, truncation);
            object projectedResult = null;
            if (kind == "postfix" && definition.Capture.Result)
                projectedResult = MethodHookProjection.Project(result, truncation);
            return new MethodHookEventRecord
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                InvocationId = invocation.InvocationId,
                Kind = kind,
                PlaySessionId = invocation.PlaySessionId,
                ClockSegment = invocation.ClockSegment,
                ElapsedMilliseconds = invocation.ElapsedMilliseconds,
                Frame = invocation.Frame,
                MainThread = true,
                Sampled = true,
                CallbackPending = definition.Callback == null && definition.HasCallbacks,
                Instance = invocation.InstanceIdentity(),
                MethodIdentity = invocation.MethodIdentity,
                Arguments = projectedArguments,
                Result = projectedResult,
                Fields = fields.Count == 0 ? null : fields,
                Truncated = truncation.Truncated || invocation.Truncated
            };
        }

        private static void CaptureFields(MethodHookDefinition definition, MethodHookInvocation invocation, List<MethodHookCaptureField> fields, Dictionary<string, object> target, MethodHookProjection.Truncation truncation)
        {
            if (fields == null || fields.Count == 0) return;
            foreach (var field in fields)
            {
                var resolved = field.Field ?? MethodHookTargetResolver.ResolveField(
                    invocation.Instance == null ? definition.Resolved.DeclaringType : invocation.Instance.GetType(), field.DeclaringType, field.FieldName);
                var value = resolved.GetValue(resolved.IsStatic ? null : invocation.Instance);
                target[(string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName] = MethodHookProjection.Project(value, truncation);
            }
        }

        /// <summary>记录故障并停止该定义派发；状态保存失败时保持 fail closed 并写入恢复抑制。</summary>
        private static void FaultDefinition(MethodHookDefinition definition, string code, string message, long invocationId, string owner)
        {
            if (definition == null) return;
            lock (Sync)
            {
                definition.State = MethodHookState.Faulted;
                definition.FaultCode = code;
                definition.FaultMessage = UnityCliBridge.BoundedText(message ?? string.Empty, 1024);
                definition.FaultRevision = definition.Revision;
                definition.FaultInvocationId = invocationId;
                definition.FaultOwner = owner ?? string.Empty;
                try
                {
                    PersistDefinitions("fault");
                }
                catch (Exception storeException)
                {
                    Suppression[definition.HookId] = UnityCliBridge.BoundedText(definition.FaultMessage + " | state save failed: " + storeException.GetType().Name, 512);
                    try { MethodHookStore.SaveSuppression(Suppression); }
                    catch { /* 抑制集合本身不可写时仍保持 gate 关闭 */ }
                }
                AppendEvent(new MethodHookEventRecord
                {
                    HookId = definition.HookId,
                    Revision = definition.Revision,
                    InvocationId = invocationId,
                    Kind = "fault",
                    PlaySessionId = playSessionId,
                    ClockSegment = clockSegment,
                    Frame = tickCounter,
                    MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName,
                    FaultCode = code,
                    FaultMessage = definition.FaultMessage,
                    Name = owner
                });
            }
        }

        /// <summary>worker 线程命中：保持原方法语义，只记录有界 thread fault 并把清理留给中央 tick。</summary>
        private static void RecordThreadFault(MethodHookTargetRuntime runtime, MethodBase original)
        {
            List<MethodHookDefinition> snapshot;
            lock (Sync) snapshot = runtime.Definitions.ToList();
            foreach (var definition in snapshot)
            {
                if (definition.State != MethodHookState.Armed && definition.State != MethodHookState.Restoring) continue;
                lock (Sync)
                {
                    definition.State = MethodHookState.Faulted;
                    definition.FaultCode = "E_HOOK_THREAD_UNSUPPORTED";
                    definition.FaultMessage = "A hook hit arrived on a non-main thread; the original method was left unchanged.";
                    definition.FaultRevision = definition.Revision;
                    definition.FaultOwner = "thread";
                    DeferredDetach.Add(definition.TargetKey);
                    AppendEvent(new MethodHookEventRecord
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        Kind = "fault",
                        PlaySessionId = playSessionId,
                        ClockSegment = clockSegment,
                        MainThread = false,
                        MethodIdentity = MethodHookMethodText.Describe(original),
                        FaultCode = "E_HOOK_THREAD_UNSUPPORTED",
                        FaultMessage = definition.FaultMessage,
                        Name = "thread"
                    });
                }
            }
        }

        private static MethodHookInterceptionStamp CurrentStamp()
        {
            return new MethodHookInterceptionStamp
            {
                PlaySessionId = playSessionId,
                ClockSegment = clockSegment,
                ElapsedMilliseconds = domainWatch.Elapsed.TotalMilliseconds,
                Frame = (int)Math.Min(int.MaxValue, playScope ? (long)Time.frameCount : tickCounter)
            };
        }

        private static string Describe(Exception exception)
        {
            return UnityCliBridge.BoundedText(UnityCliExceptionProjection.Project(exception).PublicMessage, 1024);
        }

        private static bool IsReentrant(string hookId)
        {
            return activeHooks != null && activeHooks.Contains(hookId);
        }

        private static void EnterCallback(string hookId)
        {
            if (activeHooks == null) activeHooks = new HashSet<string>(StringComparer.Ordinal);
            activeHooks.Add(hookId);
        }

        private static void ExitCallback(string hookId)
        {
            if (activeHooks != null) activeHooks.Remove(hookId);
        }

        private static void PushFrame(string hookId, long invocationId, bool record)
        {
            if (frames == null) frames = new Dictionary<string, List<MethodHookFrame>>(StringComparer.Ordinal);
            List<MethodHookFrame> stack;
            if (!frames.TryGetValue(hookId, out stack)) { stack = new List<MethodHookFrame>(); frames.Add(hookId, stack); }
            stack.Add(new MethodHookFrame { InvocationId = invocationId, Record = record });
        }

        /// <summary>弹出同一 hook 的最近一帧，使 Prefix/Postfix 关联同一 Invocation。</summary>
        private static bool PopFrame(string hookId, out long invocationId)
        {
            invocationId = 0;
            if (frames == null) return false;
            List<MethodHookFrame> stack;
            if (!frames.TryGetValue(hookId, out stack) || stack.Count == 0) return false;
            var frame = stack[stack.Count - 1];
            stack.RemoveAt(stack.Count - 1);
            invocationId = frame.InvocationId;
            return frame.Record;
        }

        private static long PopImplementationFrame(string hookId)
        {
            long invocationId;
            PopFrame(hookId, out invocationId);
            return invocationId;
        }
    }
}
#endif

