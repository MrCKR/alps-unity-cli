#if UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace UnityCli
{
    /// <summary>
    /// 拦截点派发：在任意受支持托管线程上按已发布的不可变快照派发，
    /// 并把一次调用的配置固定在 prefix 建立的帧上。
    ///
    /// 线程规则：
    /// - 命中可能发生在任意托管线程；派发只读取不可变快照与原子字段，不遍历可变集合。
    /// - worker 命中绝不调用 Unity 主线程 API：不解析 Unity 原生对象、不读对象名、不读 Unity 帧号。
    ///   需要主线程才能读取的显示身份由主线程在中央 tick 上作为独立、带时间戳的补充事件追加，
    ///   绝不回填为命中时刻的值。
    /// - worker 上的回调故障只关闭本 revision 的派发；持久化与 detach 由主线程在中央 tick 上完成。
    /// </summary>
    internal static partial class MethodHookHost
    {
        /// <summary>命中热路径只按已安装的 MethodBase 查找；并发字典允许 worker 命中与主线程安装同时进行。</summary>
        private static readonly ConcurrentDictionary<MethodBase, MethodHookTargetRuntime> DispatchTargets = new ConcurrentDictionary<MethodBase, MethodHookTargetRuntime>();
        /// <summary>按线程记录的帧栈；空栈时立即释放记录，因此一次性触达的线程不会留下长期记录。</summary>
        private static readonly ConcurrentDictionary<int, MethodHookThreadFrames> ThreadFrames = new ConcurrentDictionary<int, MethodHookThreadFrames>();
        /// <summary>线程上产生、等待主线程收尾的故障；按 hook 去重，因此天然有界。</summary>
        private static readonly ConcurrentDictionary<string, MethodHookThreadFault> PendingThreadFaults = new ConcurrentDictionary<string, MethodHookThreadFault>(StringComparer.Ordinal);
        /// <summary>等待主线程补充显示身份的命中；有界队列，超限即丢弃而不是阻塞业务调用。</summary>
        private static readonly ConcurrentQueue<MethodHookPendingSupplement> PendingSupplements = new ConcurrentQueue<MethodHookPendingSupplement>();

        private static int threadRecordCount;
        private static int pendingSupplementCount;
        private static long droppedSupplements;

        [ThreadStatic] private static HashSet<string> activeHooks;

        // ------------------------------------------------------------------
        // 线程身份与帧归属
        // ------------------------------------------------------------------

        /// <summary>主线程身份未绑定时一律按 worker 处理：无法证明主线程身份就不得读取主线程 API。</summary>
        private static bool IsMainThread()
        {
            var bound = Volatile.Read(ref mainThreadId);
            return bound != 0 && bound == Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>取得本线程的帧记录；达到全局记录上限时返回 null，由调用方保持原方法语义。</summary>
        private static MethodHookThreadFrames AcquireThreadFrames()
        {
            var threadId = Thread.CurrentThread.ManagedThreadId;
            MethodHookThreadFrames frames;
            if (ThreadFrames.TryGetValue(threadId, out frames)) return frames;
            if (Interlocked.Increment(ref threadRecordCount) > DebugLimits.MaxThreadRecords)
            {
                // 已达上限：绝不驱逐其他线程仍在进行的帧，只放弃本次新增记录。
                Interlocked.Decrement(ref threadRecordCount);
                return null;
            }
            frames = new MethodHookThreadFrames(threadId);
            if (!ThreadFrames.TryAdd(threadId, frames))
            {
                Interlocked.Decrement(ref threadRecordCount);
                ThreadFrames.TryGetValue(threadId, out frames);
            }
            return frames;
        }

        private static MethodHookThreadFrames FindThreadFrames()
        {
            MethodHookThreadFrames frames;
            return ThreadFrames.TryGetValue(Thread.CurrentThread.ManagedThreadId, out frames) ? frames : null;
        }

        /// <summary>帧栈已空说明该线程不再有在途命中，立即释放记录。</summary>
        private static void ReleaseThreadFramesIfEmpty(MethodHookThreadFrames frames)
        {
            if (frames == null || !frames.IsEmpty) return;
            if (ThreadFrames.TryRemove(frames.ThreadId, out frames)) Interlocked.Decrement(ref threadRecordCount);
        }

        // ------------------------------------------------------------------
        // 派发
        // ------------------------------------------------------------------

        /// <summary>Prefix 派发：为本次调用固定配置、采样与调用 id；host 缺席时保持原方法语义。</summary>
        internal static bool DispatchPrefixCore(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            if (!initialized) return true;
            MethodHookTargetRuntime runtime;
            if (!DispatchTargets.TryGetValue(original, out runtime)) return true;
            // 读路径只按引用读取已发布的不可变快照。
            var dispatch = Volatile.Read(ref runtime.Dispatch);
            var mainThread = IsMainThread();
            var frames = AcquireThreadFrames();
            if (frames == null)
            {
                RecordCoverageSkip(original, "thread-record-limit");
                return true;
            }
            if (frames.Count >= DebugLimits.MaxFramesPerThread)
            {
                // 活动帧上限：超出部分保持原方法语义，并记录有界覆盖缺口，绝不静默丢失在途帧。
                RecordCoverageSkip(original, "frame-limit");
                // 同一 original 的更深递归仍需占位帧，否则它的 postfix 会错配到外层帧。
                if (frames.ContainsOriginal(original))
                    frames.Push(new MethodHookFrame { Original = original, Bindings = new MethodHookBinding[0], InvocationIds = new long[0], Record = new bool[0], Stamp = CurrentStamp(IsMainThread()), Instance = instance, Arguments = arguments });
                return true;
            }

            var stamp = CurrentStamp(mainThread);
            var ordered = dispatch.Ordered;
            var bindings = new List<MethodHookBinding>();
            var invocationIds = new List<long>();
            var records = new List<bool>();
            var runOriginal = true;
            for (var index = 0; index < ordered.Length; index++)
            {
                var binding = ordered[index];
                var definition = binding.Definition;
                if (!DispatchAllowed(definition)) continue;
                if (definition.State != MethodHookState.Armed) continue;
                if (!definition.ActivationMatches(playScope)) continue;
                if (IsReentrant(binding.HookId))
                {
                    // 回调自身重入本 target 的调用被抑制，且不建立帧；这就是与业务递归的区别。
                    definition.CountBypass();
                    continue;
                }
                // listener 过滤在建立帧之前完成：条件不匹配或有限命中已用尽的调用不建立帧、不执行回调。
                if (!ListenerAllows(binding, instance, arguments, mainThread)) continue;
                var invocationId = Interlocked.Increment(ref invocationSequence);
                var record = binding.EmitHitEvents && SampleHit(binding, definition);
                if (record && binding.Capture.SampleEvery > 1) Interlocked.Increment(ref sampledHits);
                var invocation = new MethodHookInvocation(binding, instance, arguments, result, stamp, true, mainThread) { InvocationId = invocationId };
                var pending = new List<UnityCliDebugEvent>();
                var overrun = false;
                EnterCallback(binding.HookId);
                try
                {
                    overrun = InvokeCallback(binding, invocation, true);
                    pending.AddRange(invocation.Emitted);
                    if (record) pending.Add(BuildInvocationEvent(binding, invocation, "prefix", arguments, null, mainThread));
                }
                catch (Exception exception)
                {
                    ExitCallback(binding.HookId);
                    FaultDefinition(definition, binding.Revision, "E_HOOK_CALLBACK_FAILED", Describe(exception), invocationId, "prefix");
                    continue;
                }
                ExitCallback(binding.HookId);
                // 断点命中是本定义的一次命中行为：同一 target、同一 revision、同一事件真值。
                // 先服务现场（evaluate 可以修改本次调用边界），再提交暂存的参数与结果。
                if (binding.Breakpoint != null) DebugPauseHost.Hit(binding, invocation, arguments, true, mainThread);
                if (invocation.HasStagedArguments)
                {
                    var staged = invocation.StagedArguments;
                    for (var argumentIndex = 0; argumentIndex < arguments.Length && argumentIndex < staged.Length; argumentIndex++) arguments[argumentIndex] = staged[argumentIndex];
                }
                if (invocation.HasStagedResult)
                {
                    resultOut = invocation.StagedResult;
                    writeResult = true;
                }
                if (invocation.SkipRequested) runOriginal = false;
                definition.CountHit();
                bindings.Add(binding);
                invocationIds.Add(invocationId);
                records.Add(record);
                // worker 命中只登记弱引用：Unity 显示身份必须由主线程补充，不能在命中线程读取。
                if (record && !mainThread) EnqueueInstanceSupplement(binding, invocationId, instance, stamp);
                foreach (var item in pending) DebugEventStore.Append(item);
                if (overrun) FaultDefinition(definition, binding.Revision, "E_HOOK_CALLBACK_OVERRUN", "The callback returned after exceeding the overrun threshold.", invocationId, "prefix");
            }
            frames.Push(new MethodHookFrame
            {
                Original = original,
                Bindings = bindings.ToArray(),
                InvocationIds = invocationIds.ToArray(),
                Record = records.ToArray(),
                Stamp = stamp,
                Instance = instance,
                Arguments = arguments
            });
            // 已绑定 exact target 的执行点：在本命中线程上原子取得投递给该 target 的跨线程操作。
            if (DebugScheduler.PendingDeliveryCount > 0) DispatchTargetDeliveries(runtime.TargetKey);
            return runOriginal;
        }

        /// <summary>Postfix 派发：只使用 prefix 固定的帧，不重新查询最新配置。</summary>
        internal static void DispatchPostfixCore(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
        {
            writeResult = false;
            resultOut = result;
            if (!initialized) return;
            var frames = FindThreadFrames();
            if (frames == null) return;
            var frame = frames.Pop(original);
            if (frame == null) return;
            ReleaseThreadFramesIfEmpty(frames);
            var mainThread = IsMainThread();
            // Postfix 按已固定顺序串联结果：后一个定义看到前一个的替换结果，与 Harmony 的 ref __result 语义一致。
            var accumulatedResult = result;
            var anyStagedResult = false;
            for (var index = 0; index < frame.Bindings.Length; index++)
            {
                var binding = frame.Bindings[index];
                var definition = binding.Definition;
                var invocation = new MethodHookInvocation(binding, frame.Instance, arguments ?? frame.Arguments, accumulatedResult, frame.Stamp, false, mainThread)
                {
                    InvocationId = frame.InvocationIds[index]
                };
                var pending = new List<UnityCliDebugEvent>();
                var overrun = false;
                EnterCallback(binding.HookId);
                try
                {
                    overrun = InvokeCallback(binding, invocation, false);
                    pending.AddRange(invocation.Emitted);
                    if (frame.Record[index]) pending.Add(BuildInvocationEvent(binding, invocation, "postfix", arguments ?? frame.Arguments, accumulatedResult, mainThread));
                }
                catch (Exception exception)
                {
                    ExitCallback(binding.HookId);
                    FaultDefinition(definition, binding.Revision, "E_HOOK_CALLBACK_FAILED", Describe(exception), invocation.InvocationId, "postfix");
                    continue;
                }
                ExitCallback(binding.HookId);
                // postfix 断点：在原方法返回后命中，结果修改在适用边界写回。
                if (binding.Breakpoint != null) DebugPauseHost.Hit(binding, invocation, arguments ?? frame.Arguments, false, mainThread);
                if (invocation.HasStagedResult)
                {
                    accumulatedResult = invocation.StagedResult;
                    anyStagedResult = true;
                }
                // 命中计数只在 prefix 按“一次真实调用”记一次：postfix/finalizer 只是同一调用的后续阶段，
                // 在这里再计会把每次调用算成两次（异常结束没有 postfix 也不得漏计）。
                foreach (var item in pending) DebugEventStore.Append(item);
                if (overrun) FaultDefinition(definition, binding.Revision, "E_HOOK_CALLBACK_OVERRUN", "The callback returned after exceeding the overrun threshold.", invocation.InvocationId, "postfix");
            }
            if (anyStagedResult)
            {
                resultOut = accumulatedResult;
                writeResult = true;
            }
        }

        /// <summary>
        /// 内部 Finalizer：原方法抛出异常时按同一帧记录结束事件，不吞掉也不替换原异常。
        /// 帧不存在时（例如原始调用来自早于本 domain 的补丁）退回当前快照，只用于记录。
        /// </summary>
        internal static void DispatchFinalizerCore(Exception exception, MethodBase original)
        {
            if (!initialized || exception == null) return;
            var frames = FindThreadFrames();
            var frame = frames == null ? null : frames.Pop(original);
            if (frame != null)
            {
                ReleaseThreadFramesIfEmpty(frames);
                for (var index = 0; index < frame.Bindings.Length; index++)
                    AppendOriginalException(frame.Bindings[index], frame.InvocationIds[index], original, exception, frame.Stamp);
                return;
            }
            MethodHookTargetRuntime runtime;
            if (!DispatchTargets.TryGetValue(original, out runtime)) return;
            var ordered = Volatile.Read(ref runtime.Dispatch).Ordered;
            var stamp = CurrentStamp(IsMainThread());
            foreach (var binding in ordered)
            {
                var state = binding.Definition.State;
                if (state != MethodHookState.Armed && state != MethodHookState.Faulted && state != MethodHookState.Restoring) continue;
                AppendOriginalException(binding, 0L, original, exception, stamp);
            }
        }

        /// <summary>结束事件按帧内的固定 revision 记录，因此一次调用的所有事件属于同一 revision。</summary>
        private static void AppendOriginalException(MethodHookBinding binding, long invocationId, MethodBase original, Exception exception, MethodHookInterceptionStamp stamp)
        {
            DebugEventStore.Append(new UnityCliDebugEvent
            {
                HookId = binding.HookId,
                Revision = binding.Revision,
                InvocationId = invocationId,
                Kind = "lifecycle",
                SourceId = UnityCliDebugSources.Lifecycle,
                ExecutionPoint = "original-exception",
                PlaySessionId = stamp.PlaySessionId,
                ClockSegment = stamp.ClockSegment,
                ElapsedMilliseconds = stamp.ElapsedMilliseconds,
                MethodIdentity = MethodHookMethodText.Describe(original),
                Name = "original-exception",
                Value = MethodHookProjection.Project(exception, new MethodHookProjection.Truncation(), false)
            });
        }

        /// <summary>调用回调并测量耗时；超过阈值说明只是事后保护，不是抢占式超时。</summary>
        private static bool InvokeCallback(MethodHookBinding binding, MethodHookInvocation invocation, bool prefix)
        {
            if (binding.Callback == null) return false;
            var watch = Stopwatch.StartNew();
            var context = new UnityCliHookContext(invocation);
            if (prefix) binding.Callback.Prefix(context);
            else binding.Callback.Postfix(context);
            watch.Stop();
            return watch.ElapsedMilliseconds > DebugLimits.CallbackOverrunMs;
        }

        /// <summary>本帧固定的采样决定；采样序号按原子增量分配。</summary>
        private static bool SampleHit(MethodHookBinding binding, MethodHookDefinition definition)
        {
            var every = binding.Capture.SampleEvery;
            return every <= 1 || definition.NextSampleIndex(every) % every == 0;
        }

        /// <summary>构造一次 Invocation 事件；捕获字段访问失败按字段级原因记录。</summary>
        private static UnityCliDebugEvent BuildInvocationEvent(MethodHookBinding binding, MethodHookInvocation invocation, string kind, object[] arguments, object result, bool mainThread)
        {
            var truncation = new MethodHookProjection.Truncation();
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            try
            {
                CaptureFields(invocation, binding.Capture.InstanceFields, fields, truncation, mainThread);
                CaptureFields(invocation, binding.Capture.StaticFields, fields, truncation, mainThread);
            }
            catch (Exception exception)
            {
                fields["captureError"] = Describe(exception);
                truncation.Mark();
            }
            object projectedArguments = null;
            if (binding.Capture.Arguments)
                projectedArguments = MethodHookProjection.Project(arguments, truncation, mainThread);
            object projectedResult = null;
            if (kind == "postfix" && binding.Capture.Result)
                projectedResult = MethodHookProjection.Project(result, truncation, mainThread);
            return new UnityCliDebugEvent
            {
                HookId = binding.HookId,
                Revision = binding.Revision,
                InvocationId = invocation.InvocationId,
                Kind = kind,
                SourceId = UnityCliDebugSources.Hook,
                ExecutionPoint = "dispatch:" + kind,
                PlaySessionId = invocation.PlaySessionId,
                ClockSegment = invocation.ClockSegment,
                ElapsedMilliseconds = invocation.ElapsedMilliseconds,
                Sampled = binding.Capture.SampleEvery > 1,
                SampleEvery = binding.Capture.SampleEvery,
                CallbackPending = binding.Callback == null && binding.HasCallbacks,
                Instance = invocation.InstanceIdentity(),
                MethodIdentity = invocation.MethodIdentity,
                Arguments = projectedArguments,
                Result = projectedResult,
                Fields = fields.Count == 0 ? null : fields,
                Truncated = truncation.Truncated || invocation.Truncated
            };
        }

        internal static void CaptureFields(IUnityCliHookInvocation invocation, MethodHookFieldBinding[] bindings, Dictionary<string, object> target, MethodHookProjection.Truncation truncation, bool mainThread)
        {
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                if (binding.Field == null)
                {
                    // 字段在当前 domain 无法解析时如实记录，不猜测值。
                    target[binding.Key] = "unresolved-field";
                    truncation.Mark();
                    continue;
                }
                var value = binding.Field.GetValue(binding.Field.IsStatic ? null : invocation.Instance);
                target[binding.Key] = MethodHookProjection.Project(value, truncation, mainThread);
            }
        }

        // ------------------------------------------------------------------
        // 覆盖缺口与线程侧请求
        // ------------------------------------------------------------------

        /// <summary>帧或线程记录达到上限时记录有界覆盖缺口；事件存储自身的保留窗口给出硬上限。</summary>
        private static void RecordCoverageSkip(MethodBase original, string reason)
        {
            Interlocked.Increment(ref coverageSkips);
            AppendEvent(new UnityCliDebugEvent
            {
                Kind = "coverage",
                SourceId = UnityCliDebugSources.Hook,
                Name = reason,
                MethodIdentity = MethodHookMethodText.Describe(original),
                Value = new Dictionary<string, object> { { "reason", reason }, { "action", "original-call-left-unchanged" } }
            });
        }

        /// <summary>worker 命中只登记弱引用与命中时间戳，显示身份留给主线程补充。</summary>
        private static void EnqueueInstanceSupplement(MethodHookBinding binding, long invocationId, object instance, MethodHookInterceptionStamp stamp)
        {
            if (!(instance is UnityEngine.Object)) return;
            if (Interlocked.Increment(ref pendingSupplementCount) > DebugLimits.MaxPendingSupplements)
            {
                Interlocked.Decrement(ref pendingSupplementCount);
                Interlocked.Increment(ref droppedSupplements);
                return;
            }
            PendingSupplements.Enqueue(new MethodHookPendingSupplement
            {
                HookId = binding.HookId,
                Revision = binding.Revision,
                InvocationId = invocationId,
                HitThreadId = Thread.CurrentThread.ManagedThreadId,
                HitMilliseconds = stamp.ElapsedMilliseconds,
                MethodIdentity = MethodHookMethodText.Describe(binding.Resolved),
                Instance = new WeakReference(instance)
            });
        }

        /// <summary>
        /// 中央 tick 上的主线程补充：把 worker 命中时无法读取的 Unity 显示身份作为独立事件追加。
        /// 事件自带补充时刻的时钟，因此读取方不会把它当成命中时刻捕获的值。
        /// </summary>
        private static void PublishInstanceSupplements()
        {
            if (PendingSupplements.IsEmpty) return;
            MethodHookPendingSupplement entry;
            var drained = 0;
            while (drained < DebugLimits.MaxPendingSupplements && PendingSupplements.TryDequeue(out entry))
            {
                Interlocked.Decrement(ref pendingSupplementCount);
                drained++;
                if (entry == null || entry.Instance == null) continue;
                var alive = entry.Instance.Target as UnityEngine.Object;
                // 已销毁或已回收的对象不伪造身份：补充事件只在对象仍然存活时追加。
                if (alive == null || !alive) continue;
                var truncation = new MethodHookProjection.Truncation();
                DebugEventStore.Append(new UnityCliDebugEvent
                {
                    HookId = entry.HookId,
                    Revision = entry.Revision,
                    InvocationId = entry.InvocationId,
                    Kind = "supplement",
                    SourceId = UnityCliDebugSources.Hook,
                    ExecutionPoint = "supplement:instance-identity",
                    Name = "instance-identity",
                    MethodIdentity = entry.MethodIdentity,
                    ElapsedMilliseconds = Math.Max(0d, DebugEventClocks.MonotonicMilliseconds - entry.HitMilliseconds),
                    Value = new Dictionary<string, object>
                    {
                        { "identityCapturedAt", "main-thread-supplement" },
                        { "hitThreadId", (long)entry.HitThreadId },
                        { "hitElapsedMilliseconds", entry.HitMilliseconds },
                        { "instance", MethodHookProjection.ProjectUnityIdentity(alive, truncation) }
                    },
                    Truncated = truncation.Truncated
                });
            }
        }

        // ------------------------------------------------------------------
        // 故障
        // ------------------------------------------------------------------

        /// <summary>
        /// 记录回调故障并停止该定义的派发。
        /// 主线程直接落地状态与持久化；worker 线程只关闭本次命中所属 revision 的派发，
        /// 持久化与 detach 一律留给中央 scheduler tick，避免在业务线程上做文件 IO 或 patch 操作。
        /// </summary>
        private static void FaultDefinition(MethodHookDefinition definition, long revision, string code, string message, long invocationId, string owner)
        {
            if (definition == null) return;
            if (IsMainThread()) FaultDefinitionOnMainThread(definition, revision, code, message, invocationId, owner);
            else FaultDefinitionOnWorkerThread(definition, revision, code, message, invocationId, owner);
        }

        /// <summary>主线程故障：状态、持久化与故障事件在同一临界区内完成。</summary>
        private static void FaultDefinitionOnMainThread(MethodHookDefinition definition, long revision, string code, string message, long invocationId, string owner)
        {
            lock (Sync)
            {
                // 旧 revision 的在途故障不得停掉已发布的新 revision，只单独计数与记录事件。
                if (revision != definition.Revision)
                {
                    Interlocked.Increment(ref definition.StaleFaults);
                    AppendFaultEvent(definition, revision, code, message, invocationId, owner);
                    return;
                }
                definition.State = MethodHookState.Faulted;
                definition.FaultCode = code;
                definition.FaultMessage = UnityCliBridge.BoundedText(message ?? string.Empty, 1024);
                definition.FaultRevision = revision;
                definition.FaultInvocationId = invocationId;
                definition.FaultOwner = owner ?? string.Empty;
                try
                {
                    PersistDefinitions("fault");
                }
                catch (Exception storeException)
                {
                    Suppression[definition.HookId] = UnityCliBridge.BoundedText(definition.FaultMessage + " | state save failed: " + storeException.GetType().Name, 512);
                    try { DebugStore.SaveSuppression(Suppression); }
                    catch { /* 抑制集合本身不可写时仍保持 gate 关闭 */ }
                }
                AppendFaultEvent(definition, revision, code, message, invocationId, owner);
            }
        }

        /// <summary>
        /// worker 故障：只做原子关闭与登记。CAS 成功后若发现发布已推进到新 revision，立即撤销本次关闭，
        /// 使新 revision 继续派发；最终结论由主线程在 tick 上给出。
        /// </summary>
        private static void FaultDefinitionOnWorkerThread(MethodHookDefinition definition, long revision, string code, string message, long invocationId, string owner)
        {
            if (revision == Volatile.Read(ref definition.Revision)
                && Interlocked.CompareExchange(ref definition.DispatchClosed, (int)revision, 0) == 0
                && Volatile.Read(ref definition.Revision) != revision)
            {
                Interlocked.CompareExchange(ref definition.DispatchClosed, 0, (int)revision);
            }
            var bounded = UnityCliBridge.BoundedText(message ?? string.Empty, 1024);
            PendingThreadFaults[definition.HookId] = new MethodHookThreadFault
            {
                HookId = definition.HookId,
                Revision = revision,
                Code = code,
                Message = bounded,
                InvocationId = invocationId,
                Owner = owner ?? string.Empty
            };
            AppendFaultEvent(definition, revision, code, bounded, invocationId, owner);
        }

        /// <summary>故障事件按实际的 revision 记录；旧 revision 的故障因此可与新 revision 分开读取。</summary>
        private static void AppendFaultEvent(MethodHookDefinition definition, long revision, string code, string message, long invocationId, string owner)
        {
            AppendEvent(new UnityCliDebugEvent
            {
                HookId = definition.HookId,
                Revision = revision,
                InvocationId = invocationId,
                Kind = "fault",
                MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName,
                FaultCode = code,
                FaultMessage = UnityCliBridge.BoundedText(message ?? string.Empty, 1024),
                Name = owner ?? string.Empty
            });
        }

        /// <summary>主线程收尾线程侧故障：只在 revision 仍然一致时落地，否则按旧 revision 单独计数。</summary>
        private static void ApplyPendingThreadFaults()
        {
            if (PendingThreadFaults.IsEmpty) return;
            var hookIds = new List<string>(PendingThreadFaults.Keys);
            foreach (var hookId in hookIds) DrainThreadFault(hookId);
        }

        private static void DrainThreadFault(string hookId)
        {
            while (true)
            {
                MethodHookThreadFault fault;
                if (!PendingThreadFaults.TryRemove(hookId, out fault) || fault == null) return;
                lock (Sync)
                {
                    MethodHookDefinition definition;
                    if (!Definitions.TryGetValue(hookId, out definition)) continue;
                    if (definition.Revision != fault.Revision)
                    {
                        // 线程侧故障发生在已被取代的 revision 上：只计数，不改变当前 revision。
                        Interlocked.Increment(ref definition.StaleFaults);
                        continue;
                    }
                    definition.State = MethodHookState.Faulted;
                    definition.FaultCode = fault.Code;
                    definition.FaultMessage = fault.Message;
                    definition.FaultRevision = fault.Revision;
                    definition.FaultInvocationId = fault.InvocationId;
                    definition.FaultOwner = fault.Owner;
                    try
                    {
                        PersistDefinitions("fault");
                    }
                    catch (Exception storeException)
                    {
                        Suppression[definition.HookId] = UnityCliBridge.BoundedText(definition.FaultMessage + " | state save failed: " + storeException.GetType().Name, 512);
                        try { DebugStore.SaveSuppression(Suppression); }
                        catch { /* 抑制集合本身不可写时仍保持 gate 关闭 */ }
                    }
                    // 本 target 再无活动定义时撤销自己的 detour；其他 owner 的观察不受影响。
                    ReleaseAdapterWhenInactive(definition);
                }
            }
        }

        /// <summary>本 target 已无任何活动定义时撤销 detour；有其他 owner 或观察定义时保持不动。</summary>
        private static void ReleaseAdapterWhenInactive(MethodHookDefinition definition)
        {
            MethodHookTargetRuntime runtime;
            if (!Targets.TryGetValue(definition.TargetKey, out runtime)) return;
            foreach (var item in runtime.Definitions)
            {
                var state = item.State;
                if (state == MethodHookState.Armed || state == MethodHookState.Dormant || state == MethodHookState.Restoring) return;
            }
            TryDetach(runtime);
        }

        // ------------------------------------------------------------------
        // 快照发布
        // ------------------------------------------------------------------

        /// <summary>
        /// 重建并原子替换全部 target 的派发快照。
        /// 配置变更（安装、替换、启停、删除、重挂、回调编译完成）之后必须在主线程调用一次，
        /// 命中路径只读取已发布的不可变对象。
        /// </summary>
        private static void PublishDispatchSnapshots()
        {
            foreach (var runtime in Targets.Values) PublishDispatchSnapshot(runtime);
        }

        private static void PublishDispatchSnapshot(MethodHookTargetRuntime runtime)
        {
            if (runtime == null) return;
            var ordered = new List<MethodHookDefinition>(runtime.Definitions);
            ordered.Sort(CompareDefinitions);
            var bindings = new List<MethodHookBinding>(ordered.Count);
            foreach (var definition in ordered)
            {
                // 只清除属于旧 revision 的关闭：当前 revision 上的真实故障必须保持关闭，直到显式 enable/replace 推进 revision。
                var closed = Volatile.Read(ref definition.DispatchClosed);
                if (closed != 0 && closed != Volatile.Read(ref definition.Revision)) Volatile.Write(ref definition.DispatchClosed, 0);
                bindings.Add(BuildBinding(definition, runtime));
            }
            Volatile.Write(ref runtime.Dispatch, bindings.Count == 0 ? MethodHookDispatch.Empty : new MethodHookDispatch(bindings.ToArray()));
        }

        /// <summary>固定顺序：priority 降序，再按持久安装顺序升序。</summary>
        private static int CompareDefinitions(MethodHookDefinition left, MethodHookDefinition right)
        {
            if (left.Priority != right.Priority) return right.Priority.CompareTo(left.Priority);
            return left.InstallSequence.CompareTo(right.InstallSequence);
        }

        /// <summary>捕获字段在发布时解析（主线程），命中路径因此不做任何反射解析。</summary>
        private static MethodHookBinding BuildBinding(MethodHookDefinition definition, MethodHookTargetRuntime runtime)
        {
            var capture = definition.Capture ?? new MethodHookCapture();
            // listener 条件引用的字段与捕获字段同样在发布时解析，命中路径不做反射解析。
            ResolveListenerFields(definition.Listener, (definition.Resolved ?? runtime.Method) as MethodInfo);
            // 断点配置随本次发布冻结：命中路径只读已发布的不可变对象。
            var breakpoint = definition.Breakpoint == null ? null : definition.Breakpoint.Clone();
            if (breakpoint != null && definition.BreakpointOwner != null) breakpoint.ResourceId = definition.BreakpointOwner.ResourceId;
            return new MethodHookBinding
            {
                Definition = definition,
                HookId = definition.HookId,
                Revision = definition.Revision,
                EmitHitEvents = definition.EmitHitEvents,
                HasCallbacks = definition.HasCallbacks,
                Callback = definition.Callback,
                Resolved = (definition.Resolved ?? runtime.Method) as MethodInfo,
                Listener = definition.Listener,
                Breakpoint = breakpoint,
                Capture = new MethodHookCaptureView(
                    capture.Arguments,
                    capture.Result,
                    capture.SampleEvery,
                    ResolveCaptureFields(definition, capture.InstanceFields),
                    ResolveCaptureFields(definition, capture.StaticFields))
            };
        }

        private static MethodHookFieldBinding[] ResolveCaptureFields(MethodHookDefinition definition, List<MethodHookCaptureField> fields)
        {
            if (fields == null || fields.Count == 0) return new MethodHookFieldBinding[0];
            var result = new MethodHookFieldBinding[fields.Count];
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                FieldInfo resolved = null;
                try
                {
                    resolved = field.Field ?? MethodHookTargetResolver.ResolveField(definition.Resolved == null ? null : definition.Resolved.DeclaringType, field.DeclaringType, field.FieldName);
                }
                catch
                {
                    // 当前 domain 无法解析该字段：保留键并如实标记为不可解析。
                }
                result[index] = new MethodHookFieldBinding
                {
                    Key = (string.IsNullOrEmpty(field.DeclaringType) ? string.Empty : field.DeclaringType + ".") + field.FieldName,
                    Field = resolved
                };
            }
            return result;
        }

        // ------------------------------------------------------------------
        // 时间戳与小工具
        // ------------------------------------------------------------------

        /// <summary>
        /// 命中时间戳。worker 线程只读主线程缓存的帧号或中央 tick 计数，绝不调用 Unity 帧 API。
        /// </summary>
        private static MethodHookInterceptionStamp CurrentStamp(bool mainThread)
        {
            long frame;
            if (!playScope) frame = Volatile.Read(ref tickCounter);
            else frame = mainThread ? DebugEventClocks.ReadUnityFrame() : DebugEventClocks.LastObservedMainFrame;
            return new MethodHookInterceptionStamp
            {
                PlaySessionId = playSessionId,
                ClockSegment = clockSegment,
                ElapsedMilliseconds = domainWatch.Elapsed.TotalMilliseconds,
                Frame = (int)Math.Min(int.MaxValue, frame)
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

        /// <summary>清空线程帧与线程侧请求；只允许在 domain 重建或关停时调用。</summary>
        private static void ResetDispatchThreadState()
        {
            ThreadFrames.Clear();
            PendingThreadFaults.Clear();
            MethodHookPendingSupplement discarded;
            while (PendingSupplements.TryDequeue(out discarded)) { }
            Interlocked.Exchange(ref threadRecordCount, 0);
            Interlocked.Exchange(ref pendingSupplementCount, 0);
            Interlocked.Exchange(ref droppedSupplements, 0L);
            ResetListenerCounters();
            activeHooks = null;
        }
    }
}
#endif
