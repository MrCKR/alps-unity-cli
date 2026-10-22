#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>install 的 effect 前提炼结果；回调编译发生在唯一 effect barrier 之前。</summary>
    internal sealed class MethodHookInstallPlan
    {
        internal MethodHookResolvedTarget Resolved;
        /// <summary>已解析的 exact 目标声明；持久化与 recipe 导出共用同一份声明数据，不重复推导。</summary>
        internal MethodHookTargetSpec TargetSpec;
        internal MethodHookCapture Capture;
        internal MethodHookCallbackSpec Callbacks;
        internal string Activation;
        internal int Priority;
        internal bool EmitHitEvents;
        internal string Label;
        /// <summary>声明 listener 时规范化后的监听状态；null 表示命中即生效。</summary>
        internal MethodHookListenerState Listener;
        /// <summary>已在唯一触发预算账本中预留的计划身份；本次 install 失败时必须释放。</summary>
        internal string TriggerPlanId;
        /// <summary>挂载方身份；直接 debug.hook.* 调用为空，program 挂载时为 program:&lt;programId&gt;。</summary>
        internal string Owner;
        /// <summary>断点配置与它占用的 session 资源槽位；未声明断点时为 null。</summary>
        internal DebugPauseBreakpointPlan BreakpointPlan;
    }

    /// <summary>已编译并加载的回调；只在 effect barrier 之前产生 bytes 与类型。</summary>
    internal sealed class MethodHookPreparedCallbacks
    {
        internal string CompileKey = string.Empty;
        internal string PrefixHash = string.Empty;
        internal string PostfixHash = string.Empty;
        internal long SourceBytes;
        internal IUnityCliMethodHookCallback Callback;
        internal bool Reused;
    }

    /// <summary>replace/enable/disable/remove 的 effect 前提炼结果。</summary>
    internal sealed class MethodHookMutationPlan
    {
        internal string Action;
        internal MethodHookDefinition Definition;
        internal MethodHookCapture Capture;
        internal MethodHookCallbackSpec Callbacks;
        internal int Priority;
        internal bool EmitHitEvents;
        internal bool NoOp;
        internal bool RequiresTargetResolve;
        internal MethodHookResolvedTarget Resolved;
        /// <summary>声明或替换 listener 时规范化后的监听状态；null 表示本次不改变监听配置。</summary>
        internal MethodHookListenerState Listener;
        /// <summary>true 表示本次 replace 确实提交了 listener 字段（包括显式清除）。</summary>
        internal bool ListenerSubmitted;
        /// <summary>本次 replace 需要重新预留的计划身份；不改变监听配置时为空。</summary>
        internal string TriggerPlanId;
        /// <summary>提交时替换断点配置；省略字段表示保留当前断点。</summary>
        internal DebugPauseBreakpointPlan BreakpointPlan;
        /// <summary>true 表示本次 replace 确实提交了 breakpoint 字段。</summary>
        internal bool BreakpointSubmitted;
    }

    /// <summary>
    /// Hook Definition 的唯一 host：状态机、exact 解析、调用上下文、事件、持久化与生命周期恢复。
    ///
    /// 约定：
    /// - 定义、定义表与后端状态的写入只在 Unity 主线程发生；命中路径只读取不可变快照与原子字段，
    ///   因此任意受支持托管线程都可以安全命中。
    /// - 判断主线程身份依靠已绑定的主线程 id；无法证明时一律按 worker 处理，不调用 Unity 主线程 API。
    /// - 定义属于当前 Editor Session，Editor 重启后必须作废；fault/disabled 定义不自动复活。
    /// - 不注册 EditorApplication.update；恢复与延迟清理由现有中央 scheduler tick 推进。
    /// </summary>
    internal static partial class MethodHookHost
    {
        private const string BackendAssemblyName = "UnityCli.Hooks.Editor";
        private const string BackendTypeName = "UnityCli.Hooks.MethodHookHarmonyBackend";
        private const string CallbackNamespacePrefix = "UnityCli.HookCallbacks";
        private const string WrapperAbi = "unitycli-hook-callback-abi/1";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, MethodHookDefinition> Definitions = new Dictionary<string, MethodHookDefinition>(StringComparer.Ordinal);
        private static readonly Dictionary<string, MethodHookTargetRuntime> Targets = new Dictionary<string, MethodHookTargetRuntime>(StringComparer.Ordinal);
        private static readonly Dictionary<string, IUnityCliMethodHookCallback> CallbackCache = new Dictionary<string, IUnityCliMethodHookCallback>(StringComparer.Ordinal);
        private static readonly HashSet<string> AdapterIdentities = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Suppression = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly Dispatcher dispatcherAdapter = new Dispatcher();
        private static readonly Stopwatch domainWatch = Stopwatch.StartNew();

        private static IUnityCliMethodHookBackend backend;
        private static string backendFailure;
        private static volatile bool initialized;
        private static bool playScope;
        private static long generation;
        private static long installSequence;
        private static long invocationSequence;
        private static long checkpointSequence;
        private static int mainThreadId;
        private static long tickCounter;
        private static long playSessionIndex;
        private static string playSessionId = string.Empty;
        private static string clockSegment = string.Empty;
        private static DateTime recoveryDeadlineUtc = DateTime.MaxValue;
        private static CancellationTokenSource restoreCancellation;
        private static bool restoreCompileInFlight;

        /// <summary>静态 host 无法实现接口，这里只做最小转发。</summary>
        private sealed class Dispatcher : IUnityCliMethodHookDispatcher
        {
            public bool DispatchPrefix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
            {
                return DispatchPrefixCore(instance, arguments, original, result, out resultOut, out writeResult);
            }

            public void DispatchPostfix(object instance, object[] arguments, MethodBase original, object result, out object resultOut, out bool writeResult)
            {
                DispatchPostfixCore(instance, arguments, original, result, out resultOut, out writeResult);
            }

            public void DispatchFinalizer(Exception exception, MethodBase original)
            {
                DispatchFinalizerCore(exception, original);
            }
        }

        // ------------------------------------------------------------------
        // 生命周期
        // ------------------------------------------------------------------

        /// <summary>发布中的定义快照观察；语义 CAS 的期望值，发布成功后更新。</summary>
        private static DebugStoreObservation definitionsObservation = new DebugStoreObservation();

        /// <summary>发布中的事件 checkpoint 观察；语义 CAS 的期望值，发布成功后更新。</summary>
        private static DebugStoreObservation eventsObservation = new DebugStoreObservation();

        /// <summary>
        /// 新域最早时机（等价 InitializeOnLoadMethod）建立重挂，不等 update 循环。</summary>
        internal static void InitializeAfterReload()
        {
            try
            {
                InitializeAfterReloadCore();
            }
            catch (Exception exception)
            {
                // 初始化失败只隔离 hook 能力，不得拖垮 Bridge listener、Press catalog 或状态观察。
                initialized = false;
                backendFailure = "Method hook initialization failed in this domain: " + UnityCliExceptionProjection.Project(exception).PublicMessage;
                UnityCliBridge.WriteLog("method_hook_initialize_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static void InitializeAfterReloadCore()
        {
            lock (Sync)
            {
                generation++;
                domainWatch.Restart();
                Volatile.Write(ref mainThreadId, Thread.CurrentThread.ManagedThreadId);
                playScope = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
                restoreCancellation = new CancellationTokenSource();
                MethodHookInterception.Bind(dispatcherAdapter);
                Suppression.Clear();
                foreach (var pair in DebugStore.LoadSuppression()) Suppression[pair.Key] = pair.Value;

                // 定义与事件 checkpoint 只从统一 debug store 读取；不可用文件 fail closed 并记录缺口。
                var eventFloor = DebugStore.EventFloor();
                var definitionsObservationLoaded = DebugStore.ObserveDefinitions();
                var eventsObservationLoaded = DebugStore.ObserveEvents();
                string unusableReason;
                if (!DebugStore.TryAccept(definitionsObservationLoaded, DebugStore.DefinitionsFormat, out unusableReason))
                {
                    DebugStore.LogUnusable(DebugStore.DefinitionsPath(), unusableReason);
                    DebugEventStore.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "definitions-file-unusable:" + unusableReason);
                }
                if (!DebugStore.TryAccept(eventsObservationLoaded, DebugStore.EventsFormat, out unusableReason))
                {
                    DebugStore.LogUnusable(DebugStore.EventsPath(), unusableReason);
                    DebugEventStore.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "events-file-unusable:" + unusableReason);
                }
                definitionsObservation = definitionsObservationLoaded;
                eventsObservation = eventsObservationLoaded;
                var definitionsPayload = definitionsObservation.Payload;
                var eventsPayload = eventsObservation.Payload;
                var definitions = ReadDefinitions(definitionsPayload, out var definitionsCheckpoint);
                var eventsAccepted = eventsPayload != null && definitionsPayload != null && definitionsCheckpoint > 0
                    && Long(eventsPayload, "checkpoint") == definitionsCheckpoint;
                if (definitionsPayload != null && eventsPayload == null)
                    DebugEventStore.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "events-checkpoint-missing");
                else if (eventsPayload != null && !eventsAccepted)
                    DebugEventStore.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "events-checkpoint-mismatch");
                DebugEventStore.Restore(eventsAccepted ? Entries(eventsPayload) : null, eventFloor, ReadGaps(eventsPayload));

                if (playScope && string.IsNullOrEmpty(playSessionId)) BeginSegment();
                foreach (var definition in definitions)
                {
                    InstallSequence(definition);
                    definition.StateBag = new MethodHookStateBag(definition.HookId, ReadStateValues(definition));
                    // program 挂载的所有权只属于旧启动：新域里该 program 定义已经不存在，不得假装仍然有效。
                    definition.Owner = string.Empty;
                    if (definition.Listener != null) RestoreListener(definition);
                    // 恢复的定义必须先进入 host 定义表，list/info/events 与 tick 恢复才能看到它。
                    Definitions[definition.HookId] = definition;
                    string suppressed;
                    if (Suppression.TryGetValue(definition.HookId, out suppressed))
                    {
                        definition.State = MethodHookState.Faulted;
                        definition.FaultCode = "E_HOOK_RECOVERY_SUPPRESSED";
                        definition.FaultMessage = string.IsNullOrEmpty(suppressed) ? "Recovery was suppressed after a failed state save." : suppressed;
                        continue;
                    }
                    if (definition.State == MethodHookState.Disabled || definition.State == MethodHookState.Faulted) continue;
                    definition.State = MethodHookState.Restoring;
                    RestoreAttachment(definition);
                }
                // 恢复的定义在发布快照后才可能被命中；命中路径不遍历定义表。
                PublishDispatchSnapshots();
                recoveryDeadlineUtc = DateTime.UtcNow.AddSeconds(DebugLimits.RecoveryBudgetSeconds);
                initialized = true;
                if (definitions.Count > 0)
                    UnityCliBridge.WriteLog("method_hook_restore generation=" + generation.ToString(CultureInfo.InvariantCulture)
                        + " definitions=" + definitions.Count.ToString(CultureInfo.InvariantCulture)
                        + " restoring=" + Definitions.Values.Count(item => item.State == MethodHookState.Restoring).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 重载后的 listener 交接：触发预算必须在新域重新登记，否则账本会把不存在于本启动的计划当成有效；
        /// 有限命中 listener 绝不自动重新武装，旧启动的命中次数不得被当成仍然成立的剩余次数。
        /// </summary>
        private static void RestoreListener(MethodHookDefinition definition)
        {
            var ownerId = "hook:" + definition.HookId;
            int used;
            var failure = DebugTriggerBudget.TryPlan(definition.Listener.SessionId, DebugTriggerBudget.KindListener, ownerId, out used);
            if (failure != null)
            {
                definition.State = MethodHookState.Faulted;
                definition.FaultCode = "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED";
                definition.FaultMessage = "The planned injection budget refused this restored listener: " + failure + ".";
                return;
            }
            definition.TriggerOwnerId = ownerId;
            // 重载后必须重新建立 listener 声明的 phase 现场：它是新域的机制状态，旧域一个也不能留下。
            var phaseFailure = InstallListenerPhaseSite(definition, ownerId, definition.Listener);
            if (phaseFailure != null)
            {
                definition.State = MethodHookState.Faulted;
                definition.FaultCode = "E_DEBUG_PLAYER_LOOP_UNAVAILABLE";
                definition.FaultMessage = "The restored listener declares a PlayerLoop phase that could not be installed in this domain: " + phaseFailure + ".";
                return;
            }
            if (definition.Listener.MaxHits <= 0L) return;
            definition.State = MethodHookState.Disabled;
            definition.FaultCode = "E_HOOK_LISTENER_NOT_REARMED";
            definition.FaultMessage = "A one-shot or finite listener is not rearmed after a domain reload; enable it explicitly to start a new hit budget.";
        }

        /// <summary>
        /// 重载前：先解除全部暂停现场，再写 checkpoint、回收线程侧状态、清理旧域 Attachment 与 pending 编译。
        ///
        /// 解除必须先于 <see cref="ResetDomainCaches"/>：该方法会直接丢弃现场注册表，之后 <c>resume</c> 再也找不到现场，
        /// 未设时间边界的暂停命中线程将永远等不到解除信号（表现为挂死）。解除只置信号、不等待，不抢占用户同步代码。
        /// </summary>
        internal static void BeforeReload()
        {
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.DomainReload);
            List<MethodHookTargetRuntime> runtimes;
            lock (Sync)
            {
                if (!initialized && Definitions.Count == 0) return;
                initialized = false;
                try { CheckpointToDisk(); }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("method_hook_checkpoint_failed " + exception.GetType().Name + ": " + exception.Message);
                }
                runtimes = new List<MethodHookTargetRuntime>(Targets.Values);
                Targets.Clear();
                DispatchTargets.Clear();
                foreach (var definition in Definitions.Values)
                {
                    definition.Resolved = null;
                    definition.Callback = null;
                    definition.CallbackKey = string.Empty;
                }
            }
            ResetDispatchThreadState();
            CancelPendingWork();
            foreach (var runtime in runtimes) TryDetach(runtime);
            MethodHookInterception.Bind(null);
            ResetDomainCaches();
        }

        /// <summary>Editor 正常退出：清理本 session 定义、Attachment 与快照文件。</summary>
        internal static void Shutdown()
        {
            // 先解除全部暂停现场，否则仍在等待的线程会一直停到自己的超时。
            DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.EditorShutdown);
            List<MethodHookTargetRuntime> runtimes;
            lock (Sync)
            {
                initialized = false;
                runtimes = new List<MethodHookTargetRuntime>(Targets.Values);
                Targets.Clear();
                DispatchTargets.Clear();
                Definitions.Clear();
            }
            ResetDispatchThreadState();
            CancelPendingWork();
            foreach (var runtime in runtimes) TryDetach(runtime);
            MethodHookInterception.Bind(null);
            ResetDomainCaches();
            DebugEventStore.Reset();
            try
            {
                DebugStore.DeleteOwnedFiles();
                DebugStore.ClearSuppression();
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("method_hook_shutdown_cleanup_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        /// <summary>Play 段边界：建立或结束运行段并按 activation 重算 armed/dormant。</summary>
        internal static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                lock (Sync)
                {
                    playScope = true;
                    BeginSegment();
                    ResetRuntimeSegment();
                    RecomputeActivation("play-scope-entered");
                }
                return;
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                // Play 段结束：段内建立的暂停现场、phase 现场与它的 runtimeId 一起失效。
                DebugPauseHost.ReleaseAll(DebugPauseReleaseReasons.PlayExit);
                DebugPlayerLoopHost.ReleaseAll("play-exit");
                lock (Sync)
                {
                    playScope = false;
                    ResetRuntimeSegment();
                    RecomputeActivation("edit-scope-entered");
                }
            }
        }

        /// <summary>中央 scheduler tick：线程侧故障收尾、主线程身份补充、恢复编译与恢复预算。</summary>
        internal static void Tick()
        {
            if (!initialized) return;
            Interlocked.Increment(ref tickCounter);
            // 断点现场的 runtime/domain 身份与待释放的 session 资源槽位只在主线程归位。
            DebugPauseHost.Pump();
            // 线程侧请求只在本主线程 tick 上落地：持久化、detach 与 Unity 身份读取都不得在 worker 上发生。
            ApplyPendingThreadFaults();
            PublishInstanceSupplements();
            ReleaseIdleThreadFrames();
            MethodHookDefinition restoring = null;
            List<MethodHookDefinition> expired = null;
            lock (Sync)
            {
                var now = DateTime.UtcNow;
                foreach (var definition in Definitions.Values)
                {
                    if (definition.State != MethodHookState.Restoring) continue;
                    if (now > recoveryDeadlineUtc)
                    {
                        expired = expired ?? new List<MethodHookDefinition>();
                        expired.Add(definition);
                        continue;
                    }
                    if (restoring == null && definition.HasCallbacks && definition.Callback == null) restoring = definition;
                }
            }
            if (expired != null)
            {
                foreach (var definition in expired)
                    FaultDefinition(definition, Volatile.Read(ref definition.Revision), "E_COMPILER_RESTORE_BUDGET_EXHAUSTED", "The per-generation restore budget elapsed before this definition was reattached.", 0, "restore");
            }
            if (restoring != null && !restoreCompileInFlight) StartRestoreCompile(restoring);
        }

        /// <summary>回收已经空栈的线程记录，使一次性触达的线程不留下长期记录。</summary>
        private static void ReleaseIdleThreadFrames()
        {
            if (ThreadFrames.IsEmpty) return;
            foreach (var pair in ThreadFrames)
            {
                if (!pair.Value.IsEmpty) continue;
                MethodHookThreadFrames released;
                if (ThreadFrames.TryRemove(pair.Key, out released)) Interlocked.Decrement(ref threadRecordCount);
            }
        }


        // ------------------------------------------------------------------
        // 快照与持久化
        // ------------------------------------------------------------------

        /// <summary>配置与启停变更后原子保存定义快照；保存失败必须让调用方停止派发。</summary>
        private static void PersistDefinitions(string reason)
        {
            var checkpoint = ++checkpointSequence;
            definitionsObservation = DebugStore.Publish(DebugStore.DefinitionsPath(), DebugStore.DefinitionsFormat, BuildDefinitionsPayload(checkpoint), definitionsObservation);
            UnityCliBridge.WriteLog("method_hook_persist reason=" + reason + " definitions=" + Definitions.Count.ToString(CultureInfo.InvariantCulture)
                + " checkpoint=" + checkpoint.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>重载前给 definitions/events 发布同一 checkpoint，并先写事件 sequence 下界。</summary>
        private static void CheckpointToDisk()
        {
            var checkpoint = ++checkpointSequence;
            DebugStore.SetEventFloor(DebugEventStore.OldestAvailableSequence);
            definitionsObservation = DebugStore.Publish(DebugStore.DefinitionsPath(), DebugStore.DefinitionsFormat, BuildDefinitionsPayload(checkpoint), definitionsObservation);
            var snapshot = DebugEventStore.Snapshot();
            int dropped;
            var bounded = BuildBoundedEventCheckpoint(snapshot, out dropped);
            if (dropped > 0)
            {
                // 被截断区间必须作为 history gap 与新域共享，不得伪装为完整历史。
                var droppedOldest = snapshot.Count == 0 ? 0L : Convert.ToInt64(snapshot[0]["sequence"], CultureInfo.InvariantCulture);
                var droppedNewest = bounded.Count == 0 ? droppedOldest : Convert.ToInt64(bounded[0]["sequence"], CultureInfo.InvariantCulture) - 1;
                DebugEventStore.RecordGap(droppedOldest, droppedNewest, "checkpoint-truncated");
                UnityCliBridge.WriteLog("method_hook_checkpoint_truncated dropped=" + dropped.ToString(CultureInfo.InvariantCulture)
                    + " kept=" + bounded.Count.ToString(CultureInfo.InvariantCulture));
            }
            eventsObservation = DebugStore.Publish(DebugStore.EventsPath(), DebugStore.EventsFormat, new Dictionary<string, object>
            {
                { "checkpoint", checkpoint },
                { "sequenceFloor", DebugEventStore.OldestAvailableSequence },
                { "latestSequence", DebugEventStore.LatestSequence },
                { "entries", bounded.Cast<object>().ToList() },
                { "gaps", DebugEventStore.Gaps.Select(gap => (object)new Dictionary<string, object>
                    {
                        { "fromSequence", gap.FromSequence }, { "toSequence", gap.ToSequence }, { "reason", gap.Reason ?? string.Empty }
                    }).ToList() }
            }, eventsObservation);
        }

        /// <summary>
        /// 从最新端往回保留事件，直到超出 checkpoint 字节预算；返回保留的条目数并报告丢弃条数。
        /// 单条事件本身有上限，因此这里必然收敛，不会因单条过大而无法落盘。
        /// </summary>
        internal static List<Dictionary<string, object>> BuildBoundedEventCheckpoint(List<Dictionary<string, object>> snapshot, out int dropped)
        {
            dropped = 0;
            var kept = new List<Dictionary<string, object>>();
            if (snapshot == null || snapshot.Count == 0) return kept;
            long total = 0;
            for (var index = snapshot.Count - 1; index >= 0; index--)
            {
                var entry = snapshot[index];
                long size = 0;
                try { size = Encoding.UTF8.GetByteCount(UnityCliSerializer.Serialize(entry)); }
                catch { size = DebugLimits.MaxEventBytes; }
                if (total + size > DebugLimits.MaxCheckpointEventBytes && kept.Count > 0) break;
                kept.Add(entry);
                total += size;
            }
            kept.Reverse();
            dropped = snapshot.Count - kept.Count;
            return kept;
        }

        private static Dictionary<string, object> BuildDefinitionsPayload(long checkpoint)
        {
            return new Dictionary<string, object>
            {
                { "publicationSequence", checkpoint },
                { "checkpoint", checkpoint },
                { "definitions", Definitions.Values.OrderBy(definition => definition.InstallSequence).Select(definition => (object)definition.ToSnapshotData()).ToList() }
            };
        }

        private static List<MethodHookDefinition> ReadDefinitions(IDictionary<string, object> payload, out long checkpoint)
        {
            checkpoint = 0;
            var result = new List<MethodHookDefinition>();
            if (payload == null) return result;
            checkpoint = Long(payload, "checkpoint");
            object value;
            if (!payload.TryGetValue("definitions", out value)) return result;
            var items = value as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items)
            {
                var definition = MethodHookDefinition.FromData(item);
                if (definition == null) continue;
                if (result.Count >= DebugLimits.MaxDefinitionsPerSession) break;
                result.Add(definition);
            }
            return result.OrderBy(definition => definition.InstallSequence).ToList();
        }

        /// <summary>
        /// 导出属于该 session 的直接 Hook 定义数据。
        ///
        /// 只返回 owner 为空（由 debug.hook.* 直接安装）且 listener 或 breakpoint 声明的 session 匹配的定义；
        /// program 挂载的 binding 由 program 定义自己携带，不得在 recipe 里再复制一份 Hook 定义。
        /// 写出去的是声明投影：现场线程身份不进入 recipe，否则导出内容会被自己的导入按 excluded-content 拒绝。
        /// </summary>
        internal static List<Dictionary<string, object>> ExportDefinitions(string sessionId)
        {
            var result = new List<Dictionary<string, object>>();
            if (string.IsNullOrEmpty(sessionId)) return result;
            lock (Sync)
            {
                foreach (var definition in Definitions.Values.OrderBy(item => item.InstallSequence))
                {
                    if (result.Count >= DebugLimits.MaxDefinitionsPerSession) break;
                    if (!string.IsNullOrEmpty(definition.Owner)) continue;
                    var listenerSession = definition.Listener == null ? null : definition.Listener.SessionId;
                    var breakpointSession = definition.Breakpoint == null ? null : definition.Breakpoint.SessionId;
                    if (!string.Equals(listenerSession, sessionId, StringComparison.Ordinal)
                        && !string.Equals(breakpointSession, sessionId, StringComparison.Ordinal)) continue;
                    result.Add(definition.ToData());
                }
            }
            return result;
        }

        /// <summary>
        /// 由 recipe 数据登记一个未激活的 Hook 定义。
        ///
        /// 只登记配置：不解析目标、不编译回调、不挂 detour、不建立 listener phase 现场，也不占用触发预算。
        /// 因此 import 不会执行 startup/cleanup 代码，也不会重放 recipe 里的任何操作；
        /// 调用方之后显式 enable 时才会解析与编译，届时目标失效会返回精确的绑定错误。
        /// </summary>
        internal static MethodHookDefinition ImportDormant(Dictionary<string, object> data)
        {
            if (data == null) return null;
            var imported = MethodHookDefinition.FromData(data);
            if (imported == null) return null;
            lock (Sync)
            {
                // recipe 可以被复用，但有效对象身份不能：导入使用新的 hookId 与 revision 1。
                imported.HookId = "hook-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                imported.Revision = 1L;
                imported.Owner = string.Empty;
                imported.State = MethodHookState.Disabled;
                imported.FaultCode = string.Empty;
                imported.FaultMessage = "Imported from a recipe as a dormant definition; enable it explicitly to resolve the target and compile the callback in this domain.";
                imported.Resolved = null;
                imported.Callback = null;
                imported.CallbackKey = string.Empty;
                imported.HitCount = 0L;
                imported.BypassCount = 0L;
                imported.StaleFaults = 0L;
                imported.SampleIndex = 0L;
                imported.DispatchClosed = 0;
                imported.TriggerOwnerId = string.Empty;
                imported.InstallSequence = 0L;
                InstallSequence(imported);
                imported.StateBag = new MethodHookStateBag(imported.HookId, ReadStateValues(imported));
                Definitions[imported.HookId] = imported;
                PublishDispatchSnapshots();
                PersistDefinitions("import");
                UnityCliBridge.WriteLog("method_hook_imported_dormant hookId=" + imported.HookId);
                return imported;
            }
        }

        /// <summary>
        /// 删除本次导入在失败时登记的 dormant 定义；只在同一 import 的补偿路径上调用，
        /// 因此不可能删掉用户已有定义（它按新 hookId 精确删除）。
        /// </summary>
        internal static bool RemoveImportedDormant(string hookId)
        {
            if (string.IsNullOrEmpty(hookId)) return false;
            lock (Sync)
            {
                MethodHookDefinition definition;
                if (!Definitions.TryGetValue(hookId, out definition)) return true;
                if (definition.State != MethodHookState.Disabled || !string.IsNullOrEmpty(definition.Owner)) return false;
                Definitions.Remove(hookId);
                PublishDispatchSnapshots();
                PersistDefinitions("import-rollback");
                return true;
            }
        }

        private static List<Dictionary<string, object>> Entries(IDictionary<string, object> payload)
        {
            if (payload == null) return null;
            object value;
            if (!payload.TryGetValue("entries", out value)) return null;
            var items = value as IEnumerable<object>;
            if (items == null) return null;
            var result = new List<Dictionary<string, object>>();
            foreach (var item in items)
            {
                var dictionary = item as IDictionary<string, object>;
                if (dictionary == null) continue;
                result.Add(new Dictionary<string, object>(dictionary, StringComparer.Ordinal));
            }
            return result;
        }

        private static List<UnityCliDebugGap> ReadGaps(IDictionary<string, object> payload)
        {
            var result = new List<UnityCliDebugGap>();
            if (payload == null) return result;
            object value;
            if (!payload.TryGetValue("gaps", out value)) return result;
            var items = value as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items)
            {
                var dictionary = item as IDictionary<string, object>;
                if (dictionary == null) continue;
                result.Add(new UnityCliDebugGap
                {
                    FromSequence = Long(dictionary, "fromSequence"),
                    ToSequence = Long(dictionary, "toSequence"),
                    Reason = Text(dictionary, "reason")
                });
            }
            return result;
        }

        private static Dictionary<string, object> ReadStateValues(MethodHookDefinition definition)
        {
            return definition == null || definition.RestoredStateValues == null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object>(definition.RestoredStateValues, StringComparer.Ordinal);
        }

        private static long Long(IDictionary<string, object> data, string key)
        {
            long parsed;
            return data != null && long.TryParse(Text(data, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L;
        }

        private static string Text(IDictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) && value != null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Empty;
        }

        private static void InstallSequence(MethodHookDefinition definition)
        {
            if (definition.InstallSequence <= 0) definition.InstallSequence = ++installSequence;
            else if (definition.InstallSequence > installSequence) installSequence = definition.InstallSequence;
        }

        // ------------------------------------------------------------------
        // target runtime、后端与 Attachment
        // ------------------------------------------------------------------

        private static void EnsureTargetRuntimeDispatch(MethodHookTargetRuntime runtime)
        {
            // 新 target 先发布空快照，命中共现在到达时只会看到一致状态。
            PublishDispatchSnapshot(runtime);
        }

        private static MethodHookTargetRuntime EnsureTargetRuntime(MethodHookResolvedTarget resolved, MethodHookTargetSpec spec)
        {
            MethodHookTargetRuntime runtime;
            var targetKey = MethodHookTargetKey.From(resolved.Method).Text;
            if (Targets.TryGetValue(targetKey, out runtime))
            {
                if (runtime.Spec == null) runtime.Spec = spec;
                return runtime;
            }
            runtime = new MethodHookTargetRuntime
            {
                TargetKey = MethodHookTargetKey.From(resolved.Method).Text,
                Spec = spec,
                Method = resolved.Method,
                MethodIdentity = MethodHookMethodText.Describe(resolved.Method),
                AssemblyIdentity = resolved.AssemblyIdentity
            };
            Targets[targetKey] = runtime;
            DispatchTargets[resolved.Method] = runtime;
            AdapterIdentities.Add(runtime.TargetKey);
            EnsureTargetRuntimeDispatch(runtime);
            return runtime;
        }

        private static MethodHookTargetRuntime FindRuntimeByKey(string targetKey)
        {
            MethodHookTargetRuntime runtime;
            return Targets.TryGetValue(targetKey, out runtime) ? runtime : null;
        }

        /// <summary>取得唯一固定身份的后端；加载失败或来源不符只隔离 hook 功能。</summary>
        private static IUnityCliMethodHookBackend EnsureBackend()
        {
            if (backend != null) return backend;
            if (backendFailure != null)
                throw new BridgeCommandException("E_HOOK_BACKEND_UNAVAILABLE", backendFailure, null, new[] { "Reinstall the UnityCLI package with its bundled Harmony dependency, then reload the domain." });
            try
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate => string.Equals(candidate.GetName().Name, BackendAssemblyName, StringComparison.Ordinal));
                if (assembly == null) assembly = Assembly.Load(new AssemblyName(BackendAssemblyName));
                var type = assembly.GetType(BackendTypeName, false);
                if (type == null) throw new InvalidOperationException("The hook backend type is missing from " + BackendAssemblyName + ".");
                var instance = Activator.CreateInstance(type) as IUnityCliMethodHookBackend;
                if (instance == null) throw new InvalidOperationException("The hook backend does not implement the hook backend contract.");
                if (!instance.Available) throw new InvalidOperationException(string.IsNullOrEmpty(instance.UnavailableReason) ? "The hook backend reports itself unavailable." : instance.UnavailableReason);
                VerifyPluginImporterSettings(PackagedPluginAssetPath());
                VerifyDependencySource(instance);
                UnityCliBridge.WriteLog("method_hook_backend_loaded version=" + instance.Version + " dependency=" + instance.DependencyIdentity);
                backend = instance;
                return backend;
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                backendFailure = "The Harmony hook backend could not be loaded: " + UnityCliExceptionProjection.Project(exception).PublicMessage;
                throw new BridgeCommandException("E_HOOK_BACKEND_UNAVAILABLE", backendFailure, null, new[] { "Confirm the package contains Editor/Plugins/0Harmony.dll and that it loaded from the package, then reload the domain." });
            }
        }

        /// <summary>运行时核对已加载依赖的 exact identity 与 Package 来源，不用 LoadFrom 猜测另一份 DLL。</summary>
        internal static void VerifyDependencySource(IUnityCliMethodHookBackend instance)
        {
            var identity = instance.DependencyIdentity ?? string.Empty;
            if (!identity.StartsWith("0Harmony", StringComparison.Ordinal))
                throw new BridgeCommandException("E_HOOK_DEPENDENCY_CONFLICT", "The loaded Harmony dependency is not the packaged 0Harmony assembly: " + identity, null, new[] { "Remove other copies of the Harmony assembly from the project and reinstall the package." });
            var location = instance.DependencyLocation ?? string.Empty;
            var root = UnityCliBridge.BridgePackageResolvedPath();
            if (string.IsNullOrEmpty(location) || string.IsNullOrEmpty(root)
                || location.IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0)
                throw new BridgeCommandException("E_HOOK_DEPENDENCY_CONFLICT", "The loaded Harmony dependency does not come from the UnityCLI package: " + (string.IsNullOrEmpty(location) ? "<no location>" : location), null, new[] { "Remove duplicate Harmony assemblies from the project, then reinstall the package." });
        }

        /// <summary>
        /// 插件被 Unity 导入后的后置核对：打包插件必须仍按包内投影导入（仅 Editor、不面向全平台、不预加载），
        /// 否则 hook 后端可能绑定到被项目改写或替代的导入结果，此时必须 fail closed。
        /// 自动引用开关（isExplicitlyReferenced）在 2022.3 的 PluginImporter API 中不可读，
        /// 由随包 .meta 与 closure 审计离线保证。
        /// </summary>
        internal static void VerifyPluginImporterSettings(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                throw new BridgeCommandException("E_HOOK_BACKEND_UNAVAILABLE", "The packaged Harmony plugin asset path is unavailable.", null, new[] { "Reinstall the UnityCLI package, then reload the domain." });
            var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
            if (importer == null)
                throw new BridgeCommandException("E_HOOK_BACKEND_UNAVAILABLE", "The packaged Harmony plugin is missing or not imported as a managed plugin: " + assetPath, null, new[] { "Reinstall the UnityCLI package so Editor/Plugins/0Harmony.dll is imported from the package." });
            var anyPlatform = importer.GetCompatibleWithAnyPlatform();
            var editorPlatform = importer.GetCompatibleWithEditor();
            var preloaded = importer.isPreloaded;
            if (anyPlatform || !editorPlatform || preloaded)
                throw new BridgeCommandException(
                    "E_HOOK_DEPENDENCY_CONFLICT",
                    "The packaged Harmony plugin is imported with unexpected settings: anyPlatform=" + anyPlatform + ", editor=" + editorPlatform + ", preloaded=" + preloaded + ".",
                    null,
                    new[] { "Reinstall the UnityCLI package so the packaged importer projection is restored; do not edit the package cache." });
        }

        /// <summary>打包插件在 Package 内的固定相对路径；包根由 Bridge 包身份推导，不另存一份包名。</summary>
        private static string PackagedPluginAssetPath()
        {
            return UnityCliBridge.BridgePackageAssetRoot() + "/Editor/Plugins/0Harmony.dll";
        }

        private static void AttachIfNeeded(MethodHookTargetRuntime runtime)
        {
            if (runtime == null || runtime.Attached) return;
            var method = runtime.Method as MethodInfo;
            if (method == null) throw new BridgeCommandException("E_HOOK_PATCH_FAILED", "The exact target is not a method with a body.", "Re-resolve the target signature before retrying.");
            var currentBackend = EnsureBackend();
            currentBackend.Attach(method);
            var owned = currentBackend.OwnPatchCount(method);
            if (owned <= 0)
                throw new BridgeCommandException("E_HOOK_PATCH_FAILED", "The owned Adapter was not observed on the exact target after attaching.", "Inspect Unity Console; the target may have been rewritten by another patch owner.");
            // 重复 attach 会把同一 Adapter 叠加到 patch 链上，必须 fail closed。
            if (!currentBackend.OwnedExactlyOnce(method))
                throw new BridgeCommandException("E_HOOK_PATCH_FAILED", "The owned Adapter is not attached exactly once on the exact target.", "Inspect whether another owner rewrote the target, then retry after removing and reinstalling the hook.");
            runtime.Attached = true;
        }

        private static void TryDetach(MethodHookTargetRuntime runtime)
        {
            if (runtime == null || !runtime.Attached) return;
            runtime.Attached = false;
            try
            {
                var method = runtime.Method as MethodInfo;
                if (method == null || backend == null) return;
                backend.Detach(method);
            }
            catch (Exception exception)
            {
                UnityCliBridge.WriteLog("method_hook_detach_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string SafeOwnerSummary(MethodHookTargetRuntime runtime)
        {
            try
            {
                var method = runtime.Method as MethodInfo;
                return method == null || backend == null ? string.Empty : backend.OwnerSummary(method);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>最后一个定义离开 target 时才撤销自己的 Adapter。</summary>
        private static bool ReleaseAdapterIfUnused(MethodHookDefinition definition)
        {
            MethodHookTargetRuntime runtime;
            Targets.TryGetValue(definition.TargetKey, out runtime);
            if (runtime == null) return false;
            // 无论是否还有其它定义，该定义必须立即离开派发列表；否则已删除的定义会继续生效。
            runtime.Definitions.Remove(definition);
            if (runtime.Definitions.Count > 0)
            {
                PublishDispatchSnapshot(runtime);
                return false;
            }
            Volatile.Write(ref runtime.Dispatch, MethodHookDispatch.Empty);
            TryDetach(runtime);
            if (runtime.Method != null)
            {
                MethodHookTargetRuntime removed;
                DispatchTargets.TryRemove(runtime.Method, out removed);
            }
            Targets.Remove(runtime.TargetKey);
            AdapterIdentities.Remove(runtime.TargetKey);
            return true;
        }

        private static void RollbackDefinition(MethodHookDefinition definition, MethodHookTargetRuntime runtime)
        {
            lock (Sync)
            {
                Definitions.Remove(definition.HookId);
                if (runtime != null)
                {
                    runtime.Definitions.Remove(definition);
                    if (runtime.Definitions.Count == 0)
                    {
                        Volatile.Write(ref runtime.Dispatch, MethodHookDispatch.Empty);
                        TryDetach(runtime);
                        if (runtime.Method != null)
                        {
                            MethodHookTargetRuntime removed;
                            DispatchTargets.TryRemove(runtime.Method, out removed);
                        }
                        Targets.Remove(runtime.TargetKey);
                        AdapterIdentities.Remove(runtime.TargetKey);
                    }
                    else PublishDispatchSnapshot(runtime);
                }
            }
        }

        // ------------------------------------------------------------------
        // 回调编译
        // ------------------------------------------------------------------

        /// <summary>回调编译必须发生在唯一 effect barrier 之前；成本只包含 bytes 与诊断。</summary>
        internal static async ValueTask<MethodHookPreparedCallbacks> CompileCallbacksAsync(
            MethodHookCallbackSpec callbacks,
            IUnityCliWaiter waiter,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var prefix = callbacks == null ? string.Empty : callbacks.PrefixCode ?? string.Empty;
            var postfix = callbacks == null ? string.Empty : callbacks.PostfixCode ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(postfix)) return null;
            EnsureBackend();
            var key = CompileKey(prefix, postfix);
            if (CallbackCache.TryGetValue(key, out var cached))
            {
                // 同域同 key 必须复用：不再排队、不再编译、不再计入产物预算。
                UnityCliBridge.WriteLog("method_hook_callback_reuse key=" + key.Substring(0, 16));
                return new MethodHookPreparedCallbacks { CompileKey = key, Callback = cached, Reused = true, PrefixHash = Hash(prefix), PostfixHash = Hash(postfix), SourceBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(postfix) };
            }
            UnityCliBridge.WriteLog("method_hook_callback_compile key=" + key.Substring(0, 16) + " refs=" + DebugCompilerOwner.ReferenceFingerprint().Substring(0, 16) + " " + DebugCompilerOwner.ReferenceDiagnostics());
            // 唯一编译所有权与共享产物预算都来自 DebugCompilerOwner；hook 只保留回调路由缓存。
            if (!DebugCompilerOwner.TryBeginOwnership(DebugCompilerLane.MainThreadTick))
                throw new BridgeCommandException("E_UNITY_BUSY", "Another dynamic compilation currently owns the single compiler.", new Dictionary<string, object> { { "owner", "dynamic-compiler" } }, new[] { "Wait for the current compilation to reach its terminal result, then submit this request again." }, true, 250);
            try
            {
                RequireCompileAdmission(key, 0L);
                var stem = "UnityCli.HookCallback." + key.Substring(0, 16);
                var source = BuildCallbackSource(key, prefix, postfix);
                var compileRequest = new DynamicCodeCompileRequest
                {
                    SourceText = source,
                    SourceFileName = stem + ".cs",
                    ResponseFileName = "references.rsp",
                    AssemblyFileName = stem + ".dll",
                    PdbFileName = stem + ".pdb",
                    AssemblySimpleName = stem,
                    Defines = null,
                    MaxSourceBytes = DebugLimits.MaxCallbackSourceBytes * 2,
                    MaxArtifactBytes = DebugLimits.MaxSingleArtifactBytes
                };
                List<string> references;
                try { references = DebugCompilerOwner.References(null, null, () => !cancellationToken.IsCancellationRequested); }
                catch (OperationCanceledException) { throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Hook callback compilation exceeded the immutable deadline.", null, new[] { "Inspect the Editor state before submitting a new request." }); }
                var compilation = await DynamicCodeCompiler.CompileAsync(compileRequest, references, () => !cancellationToken.IsCancellationRequested, waiter, timeout, cancellationToken);
                if (compilation.TimedOut)
                    throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Hook callback compilation exceeded the immutable deadline.", null, new[] { "Inspect the Editor state before submitting a new request." });
                if (!compilation.Success)
                {
                    if (!string.IsNullOrEmpty(compilation.FailureCode))
                        throw new BridgeCommandException(compilation.FailureCode, compilation.FailureMessage, null, new[] { "Reduce the callback source size before retrying." });
                    var details = new Dictionary<string, object> { { "diagnostics", compilation.Diagnostics.Take(5).Cast<object>().ToList() } };
                    var message = compilation.Diagnostics.Count == 0 ? "Hook callback compilation failed." : "Hook callback compilation failed: " + Convert.ToString(compilation.Diagnostics[0]["message"], CultureInfo.InvariantCulture);
                    throw new BridgeCommandException("E_CODE_COMPILE_FAILED", UnityCliBridge.BoundedText(message, 4096), details, new[] { "Fix the callback diagnostics described in details and submit the mutation again." });
                }
                // 加载前的精确准入：用真实 bytes 判定，拒绝时不加载该产物。
                RequireCompileAdmission(key, compilation.AssemblyBytes.LongLength);
                var loaded = Assembly.Load(compilation.AssemblyBytes);
                // 加载成功即占用：Mono 不能逐个卸载，后续 ABI 校验失败也不扣减。
                DebugCompilerOwner.CommitLoadedArtifact(key, compilation.AssemblyBytes.LongLength, loaded.GetName().Name);
                var type = loaded.GetType(CallbackTypeName(key), false);
                var instance = type == null ? null : Activator.CreateInstance(type) as IUnityCliMethodHookCallback;
                if (instance == null)
                    throw new BridgeCommandException("E_HOOK_CALLBACK_FAILED", "The compiled callback does not implement the hook callback contract.", null, new[] { "Remove unsupported async or generic callback shapes and retry." });
                lock (Sync) CallbackCache[key] = instance;
                return new MethodHookPreparedCallbacks
                {
                    CompileKey = key,
                    Callback = instance,
                    PrefixHash = Hash(prefix),
                    PostfixHash = Hash(postfix),
                    SourceBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(postfix)
                };
            }
            finally
            {
                DebugCompilerOwner.EndOwnership(DebugCompilerLane.MainThreadTick);
            }
        }

        /// <summary>
        /// 共享编译产物预算准入：拒绝时抛出稳定错误并携带当前占用（数量、字节、剩余）与 reload 建议。
        /// owner 不自动 reload；remove/stop 不扣减已加载占用。
        /// </summary>
        private static void RequireCompileAdmission(string identity, long artifactBytes)
        {
            var admission = DebugCompilerOwner.TryAdmitCandidate(identity, artifactBytes);
            if (admission.Admitted) return;
            throw new BridgeCommandException(
                DebugCompilerOwner.BudgetExhaustedCode,
                "This domain cannot load another compiled callback artifact: " + admission.Reason + ".",
                new Dictionary<string, object> { { "reason", admission.Reason }, { "budget", admission.Report.ToData() } },
                new[] { DebugCompilerOwner.ReloadSuggestion });
        }

        /// <summary>编译键包含源码、wrapper ABI、编译参数与引用 identity/MVID 摘要。</summary>
        private static string CompileKey(string prefix, string postfix)
        {
            return UnityCliSerializer.Sha256Canonical(new Dictionary<string, object>
            {
                { "wrapper", WrapperAbi },
                { "prefix", prefix ?? string.Empty },
                { "postfix", postfix ?? string.Empty },
                { "compiler", "unity-mono-csc /langversion:latest /target:library" },
                { "references", DebugCompilerOwner.ReferenceFingerprint() }
            });
        }

        private static string BuildCallbackSource(string key, string prefix, string postfix)
        {
            var identifier = CallbackIdentifier(key);
            var builder = new StringBuilder();
            builder.Append("using System; using System.Collections.Generic; using System.Linq; using System.Text; using UnityEngine; using UnityEditor;");
            builder.Append(" namespace ").Append(CallbackNamespacePrefix).Append('.').Append(identifier).Append(" { public sealed class Callback : UnityCli.IUnityCliMethodHookCallback {");
            builder.Append(" public void Prefix(UnityCli.UnityCliHookContext ctx) { ").Append(string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix).Append(" }");
            builder.Append(" public void Postfix(UnityCli.UnityCliHookContext ctx) { ").Append(string.IsNullOrWhiteSpace(postfix) ? string.Empty : postfix).Append(" }");
            builder.Append(" } }");
            return builder.ToString();
        }

        /// <summary>编译键是十六进制，直接用作标识符可能以数字开头，因此固定加字母前缀。</summary>
        private static string CallbackIdentifier(string key)
        {
            return "K" + key.Substring(0, 16);
        }

        /// <summary>回调类型全名与生成源码必须共用同一推导，避免两边漂移。</summary>
        private static string CallbackTypeName(string key)
        {
            return CallbackNamespacePrefix + "." + CallbackIdentifier(key) + ".Callback";
        }

        private static string Hash(string value)
        {
            return Encoding.UTF8.GetBytes(value ?? string.Empty).Length == 0
                ? string.Empty
                : UnityCliSerializer.Sha256Canonical(value ?? string.Empty);
        }

        private static void StartRestoreCompile(MethodHookDefinition definition)
        {
            restoreCompileInFlight = true;
            var timeout = recoveryDeadlineUtc > DateTime.UtcNow ? recoveryDeadlineUtc - DateTime.UtcNow : TimeSpan.FromMilliseconds(1);
            var cancellation = restoreCancellation ?? new CancellationTokenSource();
            _ = RestoreCompileAsync(definition, timeout, cancellation.Token);
        }

        /// <summary>全部公开操作都要求 hook host 已在本域初始化；否则明确失败而不是静默返回空定义。</summary>
        private static void EnsureReady()
        {
            if (initialized) return;
            throw new BridgeCommandException(
                "E_HOOK_BACKEND_UNAVAILABLE",
                string.IsNullOrEmpty(backendFailure) ? "Method hooks are not initialized in the current Unity domain." : backendFailure, null,
                new[] { "Let Unity reload the domain, or reinstall the UnityCLI package with its bundled Harmony dependency." });
        }

        /// <summary>恢复期回调编译；失败使该定义 faulted，不封锁其他定义与基础观察。</summary>
        private static async Task RestoreCompileAsync(MethodHookDefinition definition, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                var waiter = new UnityCliWaiter("method-hook-restore", "method-hook");
                var prepared = await CompileCallbacksAsync(new MethodHookCallbackSpec { PrefixCode = definition.PrefixCode, PostfixCode = definition.PostfixCode }, waiter, timeout, cancellationToken);
                if (prepared == null) return;
                lock (Sync)
                {
                    if (!initialized || definition.State != MethodHookState.Restoring) return;
                    definition.Callback = prepared.Callback;
                    definition.CallbackKey = prepared.CompileKey;
                    definition.PrefixHash = prepared.PrefixHash;
                    definition.PostfixHash = prepared.PostfixHash;
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    // 回调编译完成改变了可执行配置，必须发布新快照。
                    PublishDispatchSnapshots();
                    AppendEvent(new UnityCliDebugEvent
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        Kind = "lifecycle",
                        PlaySessionId = playSessionId,
                        ClockSegment = clockSegment,
                        Name = "restored",
                        MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
                    });
                }
            }
            catch (Exception exception)
            {
                FaultDefinition(definition, Volatile.Read(ref definition.Revision), exception is BridgeCommandException ? ((BridgeCommandException)exception).Code : "E_HOOK_CALLBACK_FAILED",
                    exception.Message, 0, "restore");
            }
            finally
            {
                restoreCompileInFlight = false;
            }
        }

        // ------------------------------------------------------------------
        // 小工具
        // ------------------------------------------------------------------

        internal static void ResetDomainCaches()
        {
            CallbackCache.Clear();
            AdapterIdentities.Clear();
            // 触发预算、日志订阅、PlayerLoop phase 现场与触发观测计数属于本次 Editor 启动：域边界一律回收。
            DispatchClosedSessions.Clear();
            DebugTriggerBudget.Reset();
            DebugListenerHost.Reset();
            DebugPlayerLoopHost.ReleaseAll("domain-reset");
            // 编译所有权、引用快照与已加载产物占用统一由 DebugCompilerOwner 按 domain generation 归零。
            DebugCompilerOwner.ResetDomain();
            restoreCompileInFlight = false;
            playSessionId = string.Empty;
            clockSegment = string.Empty;
            playSessionIndex = 0;
            Interlocked.Exchange(ref tickCounter, 0L);
            ResetDispatchThreadState();
            // 暂停现场、覆盖缺口计数与本域身份快照都是本次 Editor 启动的机制状态，域边界一律回收。
            DebugPauseHost.Reset();
        }

        private static void CancelPendingWork()
        {
            try { if (restoreCancellation != null) restoreCancellation.Cancel(); } catch { }
            try { if (restoreCancellation != null) restoreCancellation.Dispose(); } catch { }
            restoreCancellation = null;
        }

        private static MethodHookRuntimeSummary SummaryLocked()
        {
            // 已加载编译产物的数量与字节只由共享 owner 记账，hook 不另存一份。
            var compiled = DebugCompilerOwner.Budget();
            return new MethodHookRuntimeSummary
            {
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DefinitionCount = Definitions.Count,
                TargetCount = Targets.Count,
                AdapterIdentities = AdapterIdentities.Count,
                CompiledArtifacts = compiled.Count,
                CompiledArtifactBytes = compiled.Bytes,
                ClockSegment = clockSegment,
                PlaySessionId = playSessionId
            };
        }

        private static void BeginSegment()
        {
            playSessionIndex++;
            playSessionId = "play-" + playSessionIndex.ToString(CultureInfo.InvariantCulture);
            clockSegment = playSessionId + ":seg-1";
            UnityCliBridge.WriteLog("method_hook_play_segment session=" + playSessionId);
        }

        /// <summary>切换运行段：重置采样基线与 State，不保留旧的运行时对象引用。</summary>
        private static void ResetRuntimeSegment()
        {
            clockSegment = (string.IsNullOrEmpty(playSessionId) ? "edit" : playSessionId) + ":seg-" + (DateTime.UtcNow.Ticks & 0xffff).ToString(CultureInfo.InvariantCulture);
            foreach (var definition in Definitions.Values)
            {
                // 运行段切换重置采样基线；采样序号可能被任意线程读走，因此按原子写重置。
                Interlocked.Exchange(ref definition.SampleIndex, 0L);
                definition.StateBag = new MethodHookStateBag(definition.HookId);
            }
        }

        /// <summary>按当前 Edit/Play 段重算 activation，disabled/faulted 定义保持原状态。</summary>
        private static void RecomputeActivation(string reason)
        {
            foreach (var definition in Definitions.Values)
            {
                if (definition.State == MethodHookState.Disabled || definition.State == MethodHookState.Faulted) continue;
                if (definition.State == MethodHookState.Restoring)
                {
                    if (definition.Callback == null && definition.HasCallbacks) continue;
                    definition.State = definition.ActivationMatches(playScope) ? MethodHookState.Armed : MethodHookState.Dormant;
                    continue;
                }
                var armed = definition.ActivationMatches(playScope);
                var desired = armed ? MethodHookState.Armed : MethodHookState.Dormant;
                if (definition.State == desired) continue;
                definition.State = desired;
                AppendEvent(new UnityCliDebugEvent
                {
                    HookId = definition.HookId,
                    Revision = definition.Revision,
                    Kind = "lifecycle",
                    PlaySessionId = playSessionId,
                    ClockSegment = clockSegment,
                    Name = reason,
                    MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
                });
            }
        }

        private static void AppendLifecycle(MethodHookDefinition definition, string name, string detail)
        {
            AppendEvent(new UnityCliDebugEvent
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                Kind = "lifecycle",
                PlaySessionId = playSessionId,
                ClockSegment = clockSegment,
                Name = name,
                Value = detail,
                MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
            });
        }

        /// <summary>
        /// hook 事件统一入口：补齐来源、运行段与捕获点后交给唯一事件存储。
        /// 线程身份与时钟溯源由存储按可证明的线程归属记录，调用方不得自行声明。
        /// </summary>
        private static void AppendEvent(UnityCliDebugEvent record)
        {
            record.PlaySessionId = string.IsNullOrEmpty(record.PlaySessionId) ? playSessionId : record.PlaySessionId;
            record.ClockSegment = string.IsNullOrEmpty(record.ClockSegment) ? clockSegment : record.ClockSegment;
            if (string.IsNullOrEmpty(record.SourceId))
                record.SourceId = string.Equals(record.Kind, "lifecycle", StringComparison.Ordinal) ? UnityCliDebugSources.Lifecycle : UnityCliDebugSources.Hook;
            if (string.IsNullOrEmpty(record.ExecutionPoint)) record.ExecutionPoint = ExecutionPointFor(record);
            DebugEventStore.Append(record);
        }

        /// <summary>捕获点描述：命中使用事件种类，生命周期与故障带上事件名，便于读取端定位来源。</summary>
        private static string ExecutionPointFor(UnityCliDebugEvent record)
        {
            var kind = record.Kind ?? string.Empty;
            var name = string.IsNullOrEmpty(record.Name) ? "unknown" : record.Name;
            if (string.Equals(kind, "lifecycle", StringComparison.Ordinal)) return "lifecycle:" + name;
            if (string.Equals(kind, "fault", StringComparison.Ordinal)) return "fault:" + name;
            return "dispatch:" + kind;
        }

        private static MethodHookDefinition RequireDefinition(string hookId, string editorSessionId, long? expectedRevision, bool allowUnknownSession = false)
        {
            if (string.IsNullOrWhiteSpace(hookId))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "hookId is required.", "Pass the hookId returned by debug.hook.install or debug.hook.list.");
            if (!allowUnknownSession)
            {
                if (string.IsNullOrWhiteSpace(editorSessionId))
                    throw new BridgeCommandException("E_SCHEMA_INVALID", "editorSessionId is required for this mutation.", "Pass the editorSessionId returned by install or info.");
                if (!string.Equals(editorSessionId, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal))
                    throw new BridgeCommandException("E_HOOK_SESSION_STALE", "The definition belongs to a different Editor session.", null, new[] { "Re-read debug.hook.list on the current session before mutating definitions." });
            }
            lock (Sync)
            {
                MethodHookDefinition definition;
                if (!Definitions.TryGetValue(hookId, out definition))
                    throw new BridgeCommandException("E_HOOK_NOT_FOUND", "No hook definition with that id exists in the current Editor session.", null, new[] { "Read debug.hook.list to inspect the current definitions." });
                if (expectedRevision.HasValue && definition.Revision != expectedRevision.Value)
                    throw new BridgeCommandException("E_HOOK_REVISION_STALE", "The definition revision changed from " + expectedRevision.Value.ToString(CultureInfo.InvariantCulture) + " to " + definition.Revision.ToString(CultureInfo.InvariantCulture) + ".", null, new[] { "Read debug.hook.info for the current revision and retry with expectedRevision." });
                return definition;
            }
        }

        private static MethodHookCapture NormalizeCapture(MethodHookCaptureSpec spec)
        {
            var capture = new MethodHookCapture();
            if (spec == null) return capture;
            capture.Arguments = spec.Arguments ?? false;
            capture.Result = spec.Result ?? false;
            capture.SampleEvery = spec.SampleEvery == null ? 1 : Math.Min(Math.Max(1, spec.SampleEvery.Value), DebugLimits.MaxSampleEvery);
            if (spec.InstanceFields != null) foreach (var field in spec.InstanceFields) capture.InstanceFields.Add(NormalizeField(field));
            if (spec.StaticFields != null) foreach (var field in spec.StaticFields) capture.StaticFields.Add(NormalizeField(field));
            if (capture.FieldCount > DebugLimits.MaxCaptureFields)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook captures at most " + DebugLimits.MaxCaptureFields.ToString(CultureInfo.InvariantCulture) + " exact fields.", "Reduce the captured field list.");
            return capture;
        }

        private static MethodHookCaptureField NormalizeField(MethodHookCaptureFieldSpec spec)
        {
            if (spec == null || string.IsNullOrWhiteSpace(spec.FieldName))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Every capture field requires fieldName.", "Pass fieldName, and declaringType when it is declared on a base type.");
            return new MethodHookCaptureField { DeclaringType = spec.DeclaringType ?? string.Empty, FieldName = spec.FieldName };
        }

        private static MethodHookCallbackSpec NormalizeCallbacks(MethodHookCallbackSpec spec)
        {
            if (spec == null) return null;
            var prefix = spec.PrefixCode ?? string.Empty;
            var postfix = spec.PostfixCode ?? string.Empty;
            if (Encoding.UTF8.GetByteCount(prefix) > DebugLimits.MaxCallbackSourceBytes)
                throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "prefixCode exceeds the declared callback source budget.", null, new[] { "Split the callback into a smaller body or move shared logic into project code." });
            if (Encoding.UTF8.GetByteCount(postfix) > DebugLimits.MaxCallbackSourceBytes)
                throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "postfixCode exceeds the declared callback source budget.", null, new[] { "Split the callback into a smaller body or move shared logic into project code." });
            return new MethodHookCallbackSpec { PrefixCode = prefix, PostfixCode = postfix };
        }

        private static List<MethodHookCallbackSummary> SummarizeCallbacks(MethodHookDefinition definition)
        {
            return new List<MethodHookCallbackSummary>
            {
                new MethodHookCallbackSummary { Slot = "prefix", Present = !string.IsNullOrEmpty(definition.PrefixCode), Hash = definition.PrefixHash, SourceBytes = Encoding.UTF8.GetByteCount(definition.PrefixCode ?? string.Empty) },
                new MethodHookCallbackSummary { Slot = "postfix", Present = !string.IsNullOrEmpty(definition.PostfixCode), Hash = definition.PostfixHash, SourceBytes = Encoding.UTF8.GetByteCount(definition.PostfixCode ?? string.Empty) }
            };
        }

        private static MethodHookTargetSummary SummarizeStoredTarget(MethodHookDefinition definition)
        {
            var spec = definition.TargetSpec;
            return new MethodHookTargetSummary
            {
                AssemblyName = spec.AssemblyName,
                AssemblyIdentity = definition.Resolved == null ? string.Empty : MethodHookMethodText.AssemblyIdentity(definition.Resolved),
                TypeName = spec.TypeName,
                MethodName = spec.MethodName,
                ParameterTypes = spec.ParameterTypes ?? new List<string>(),
                ReturnType = spec.ReturnType,
                IsStatic = spec.IsStatic,
                MethodIdentity = spec.TypeName + "." + spec.MethodName + "(" + string.Join(", ", (spec.ParameterTypes ?? new List<string>()).ToArray()) + ")"
            };
        }

        private static bool CaptureEquals(MethodHookCapture left, MethodHookCapture right)
        {
            if (left == null || right == null) return left == right;
            if (left.Arguments != right.Arguments || left.Result != right.Result || left.SampleEvery != right.SampleEvery) return false;
            if (left.InstanceFields.Count != right.InstanceFields.Count || left.StaticFields.Count != right.StaticFields.Count) return false;
            for (var index = 0; index < left.InstanceFields.Count; index++)
                if (!FieldEquals(left.InstanceFields[index], right.InstanceFields[index])) return false;
            for (var index = 0; index < left.StaticFields.Count; index++)
                if (!FieldEquals(left.StaticFields[index], right.StaticFields[index])) return false;
            return true;
        }

        private static bool FieldEquals(MethodHookCaptureField left, MethodHookCaptureField right)
        {
            return string.Equals(left.DeclaringType ?? string.Empty, right.DeclaringType ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(left.FieldName ?? string.Empty, right.FieldName ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>按 UTF-8 字节偏移切一段有界文本，不截断 surrogate pair。</summary>
        internal static string SubstringByUtf8Bytes(string value, int offsetBytes, int maxBytes)
        {
            if (string.IsNullOrEmpty(value) || maxBytes <= 0) return string.Empty;
            var start = offsetBytes <= 0 ? 0 : CharIndexAtUtf8Byte(value, offsetBytes);
            if (start >= value.Length) return string.Empty;
            return UnityCliBridge.BoundedText(value.Substring(start), maxBytes);
        }

        private static int CharIndexAtUtf8Byte(string value, int offsetBytes)
        {
            var bytes = 0;
            for (var index = 0; index < value.Length; index++)
            {
                var size = Encoding.UTF8.GetByteCount(value, index, 1);
                if (bytes + size > offsetBytes) return index;
                bytes += size;
            }
            return value.Length;
        }

        private static string EncodeOffsetCursor(string editorSessionId, string scope, int offset)
        {
            var payload = "ml1|" + editorSessionId + "|" + scope + "|" + offset.ToString(CultureInfo.InvariantCulture);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        }

        private static int DecodeOffsetCursor(string cursor, string editorSessionId, string scope)
        {
            try
            {
                var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
                if (parts.Length != 4 || !string.Equals(parts[0], "ml1", StringComparison.Ordinal)
                    || !string.Equals(parts[1], editorSessionId, StringComparison.Ordinal)
                    || !string.Equals(parts[2], scope, StringComparison.Ordinal)) throw new FormatException("cursor scope mismatch");
                return Math.Max(0, int.Parse(parts[3], CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                throw new BridgeCommandException("E_CONTINUATION_STALE", "The list continuation no longer matches the current Editor session or filters.", null, new[] { "Re-read debug.hook.list without a cursor." });
            }
        }
    }
}
#endif

