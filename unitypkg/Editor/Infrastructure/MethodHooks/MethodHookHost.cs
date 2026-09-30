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
        internal MethodHookCapture Capture;
        internal MethodHookCallbackSpec Callbacks;
        internal string Activation;
        internal int Priority;
        internal bool EmitHitEvents;
        internal string Label;
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
    }

    /// <summary>
    /// Hook Definition 的唯一 host：状态机、exact 解析、调用上下文、事件、持久化与生命周期恢复。
    ///
    /// 约定：
    /// - 所有状态只在 Unity 主线程读写；命中共现在其他线程时只记录有界 thread fault。
    /// - 逻辑 gate 在建立/撤销 Attachment、持久化与 mutation 窗口内关闭，命中期间不做半更新派发。
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
        // 命中热路径只按当前域已安装的 MethodBase 查找，避免每次命中都重建 stable target key。
        private static readonly Dictionary<MethodBase, MethodHookTargetRuntime> DispatchTargets = new Dictionary<MethodBase, MethodHookTargetRuntime>();
        private static readonly Dictionary<string, IUnityCliMethodHookCallback> CallbackCache = new Dictionary<string, IUnityCliMethodHookCallback>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoadedCallbackKeys = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> AdapterIdentities = new HashSet<string>(StringComparer.Ordinal);
        private static readonly Dictionary<string, string> Suppression = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly List<string> DeferredDetach = new List<string>();
        private static readonly MethodHookEventBuffer Events = new MethodHookEventBuffer();
        private static readonly Dispatcher dispatcherAdapter = new Dispatcher();
        private static readonly Stopwatch domainWatch = Stopwatch.StartNew();

        [ThreadStatic] private static HashSet<string> activeHooks;
        [ThreadStatic] private static Dictionary<string, List<MethodHookFrame>> frames;

        private static IUnityCliMethodHookBackend backend;
        private static string backendFailure;
        private static bool initialized;
        private static bool playScope;
        private static long generation;
        private static long installSequence;
        private static long invocationSequence;
        private static long checkpointSequence;
        private static long compiledArtifactBytes;
        private static string domainReferenceFingerprint;
        private static int mainThreadId;
        private static long tickCounter;
        private static long playSessionIndex;
        private static string playSessionId = string.Empty;
        private static string clockSegment = string.Empty;
        private static DateTime recoveryDeadlineUtc = DateTime.MaxValue;
        private static CancellationTokenSource restoreCancellation;
        private static bool restoreCompileInFlight;

        private sealed class MethodHookFrame
        {
            internal long InvocationId;
            internal bool Record;
        }

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

        /// <summary>新域最早时机（等价 InitializeOnLoadMethod）建立重挂，不等 update 循环。</summary>
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
                mainThreadId = Thread.CurrentThread.ManagedThreadId;
                playScope = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
                restoreCancellation = new CancellationTokenSource();
                MethodHookInterception.Bind(dispatcherAdapter);
                var session = UnityCliBridge.EditorSessionId();
                Suppression.Clear();
                foreach (var pair in MethodHookStore.LoadSuppression()) Suppression[pair.Key] = pair.Value;

                var definitionsPayload = MethodHookStore.LoadDefinitions(session);
                var eventsPayload = MethodHookStore.LoadEvents(session);
                var definitions = ReadDefinitions(definitionsPayload, out var definitionsCheckpoint);
                var eventFloor = MethodHookStore.EventFloor();
                var eventsAccepted = eventsPayload != null && definitionsPayload != null && definitionsCheckpoint > 0
                    && Long(eventsPayload, "checkpoint") == definitionsCheckpoint;
                if (definitionsPayload != null && eventsPayload == null)
                    Events.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "events-checkpoint-missing");
                else if (eventsPayload != null && !eventsAccepted)
                    Events.RecordGap(Math.Max(1, eventFloor), Math.Max(1, eventFloor), "events-checkpoint-mismatch");
                Events.Restore(eventsAccepted ? Entries(eventsPayload) : null, eventFloor, ReadGaps(eventsPayload));

                if (playScope && string.IsNullOrEmpty(playSessionId)) BeginSegment();
                foreach (var definition in definitions)
                {
                    InstallSequence(definition);
                    definition.StateBag = new MethodHookStateBag(definition.HookId, ReadStateValues(definition));
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
                }                recoveryDeadlineUtc = DateTime.UtcNow.AddSeconds(MethodHookLimits.RecoveryBudgetSeconds);
                initialized = true;
                if (definitions.Count > 0)
                    UnityCliBridge.WriteLog("method_hook_restore generation=" + generation.ToString(CultureInfo.InvariantCulture)
                        + " definitions=" + definitions.Count.ToString(CultureInfo.InvariantCulture)
                        + " restoring=" + Definitions.Values.Count(item => item.State == MethodHookState.Restoring).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>重载前：关闭 gate、写 checkpoint、清理旧域 Attachment 与 pending 编译。</summary>
        internal static void BeforeReload()
        {
            List<MethodHookTargetRuntime> runtimes;
            lock (Sync)
            {
                if (!initialized && Definitions.Count == 0) return;
                initialized = false;
                foreach (var runtime in Targets.Values) runtime.GateOpen = false;
                try { CheckpointToDisk(); }
                catch (Exception exception)
                {
                    UnityCliBridge.WriteLog("method_hook_checkpoint_failed " + exception.GetType().Name + ": " + exception.Message);
                }
                runtimes = Targets.Values.ToList();
                Targets.Clear();
                DispatchTargets.Clear();
                foreach (var definition in Definitions.Values)
                {
                    definition.Resolved = null;
                    definition.Callback = null;
                    definition.CallbackKey = string.Empty;
                }
            }
            CancelPendingWork();
            foreach (var runtime in runtimes) TryDetach(runtime);
            MethodHookInterception.Bind(null);
            ResetDomainCaches();
        }

        /// <summary>Editor 正常退出：清理本 session 定义、Attachment 与快照文件。</summary>
        internal static void Shutdown()
        {
            List<MethodHookTargetRuntime> runtimes;
            lock (Sync)
            {
                initialized = false;
                foreach (var runtime in Targets.Values) runtime.GateOpen = false;
                runtimes = Targets.Values.ToList();
                Targets.Clear();
                DispatchTargets.Clear();
                Definitions.Clear();
            }
            CancelPendingWork();
            foreach (var runtime in runtimes) TryDetach(runtime);
            MethodHookInterception.Bind(null);
            ResetDomainCaches();
            Events.Reset();
            try
            {
                MethodHookStore.DeleteOwnedFiles();
                MethodHookStore.ClearSuppression();
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
                lock (Sync)
                {
                    playScope = false;
                    ResetRuntimeSegment();
                    RecomputeActivation("edit-scope-entered");
                }
            }
        }

        /// <summary>中央 scheduler tick：延迟清理、恢复编译与恢复预算。</summary>
        internal static void Tick()
        {
            if (!initialized) return;
            tickCounter++;
            List<string> detach = null;
            MethodHookDefinition restoring = null;
            List<MethodHookDefinition> expired = null;
            lock (Sync)
            {
                if (DeferredDetach.Count > 0)
                {
                    detach = DeferredDetach.ToList();
                    DeferredDetach.Clear();
                }
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
            if (detach != null) foreach (var key in detach) TryDeferredDetach(key);
            if (expired != null)
            {
                foreach (var definition in expired)
                    FaultDefinition(definition, "E_HOOK_RESTORE_BUDGET_EXHAUSTED", "The per-generation restore budget elapsed before this definition was reattached.", 0, "restore");
            }
            if (restoring != null && !restoreCompileInFlight) StartRestoreCompile(restoring);
        }


        // ------------------------------------------------------------------
        // 快照与持久化
        // ------------------------------------------------------------------

        /// <summary>配置与启停变更后原子保存定义快照；保存失败必须让调用方停止派发。</summary>
        private static void PersistDefinitions(string reason)
        {
            var checkpoint = ++checkpointSequence;
            MethodHookStore.PublishDefinitions(BuildDefinitionsPayload(checkpoint));
            UnityCliBridge.WriteLog("method_hook_persist reason=" + reason + " definitions=" + Definitions.Count.ToString(CultureInfo.InvariantCulture)
                + " checkpoint=" + checkpoint.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>重载前给 definitions/events 发布同一 checkpoint，并先写事件 sequence 下界。</summary>
        private static void CheckpointToDisk()
        {
            var checkpoint = ++checkpointSequence;
            MethodHookStore.SetEventFloor(Events.OldestAvailableSequence);
            MethodHookStore.PublishDefinitions(BuildDefinitionsPayload(checkpoint));
            var snapshot = Events.Snapshot();
            int dropped;
            var bounded = BuildBoundedEventCheckpoint(snapshot, out dropped);
            if (dropped > 0)
            {
                // 被截断区间必须作为 history gap 与新域共享，不得伪装为完整历史。
                var droppedOldest = snapshot.Count == 0 ? 0L : Convert.ToInt64(snapshot[0]["sequence"], CultureInfo.InvariantCulture);
                var droppedNewest = bounded.Count == 0 ? droppedOldest : Convert.ToInt64(bounded[0]["sequence"], CultureInfo.InvariantCulture) - 1;
                Events.RecordGap(droppedOldest, droppedNewest, "checkpoint-truncated");
                UnityCliBridge.WriteLog("method_hook_checkpoint_truncated dropped=" + dropped.ToString(CultureInfo.InvariantCulture)
                    + " kept=" + bounded.Count.ToString(CultureInfo.InvariantCulture));
            }
            MethodHookStore.PublishEvents(new Dictionary<string, object>
            {
                { "format", MethodHookStore.EventsFormat },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "checkpoint", checkpoint },
                { "sequenceFloor", Events.OldestAvailableSequence },
                { "latestSequence", Events.LastSequence },
                { "entries", bounded.Cast<object>().ToList() },
                { "gaps", Events.Gaps.Select(gap => (object)new Dictionary<string, object>
                    {
                        { "fromSequence", gap.FromSequence }, { "toSequence", gap.ToSequence }, { "reason", gap.Reason ?? string.Empty }
                    }).ToList() }
            });
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
                catch { size = MethodHookLimits.MaxEventBytes; }
                if (total + size > MethodHookLimits.MaxCheckpointEventBytes && kept.Count > 0) break;
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
                { "format", MethodHookStore.DefinitionsFormat },
                { "editorSessionId", UnityCliBridge.EditorSessionId() },
                { "publicationSequence", checkpoint },
                { "checkpoint", checkpoint },
                { "definitions", Definitions.Values.OrderBy(definition => definition.InstallSequence).Select(definition => (object)definition.ToData()).ToList() }
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
                if (result.Count >= MethodHookLimits.MaxDefinitionsPerSession) break;
                result.Add(definition);
            }
            return result.OrderBy(definition => definition.InstallSequence).ToList();
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

        private static List<MethodHookGap> ReadGaps(IDictionary<string, object> payload)
        {
            var result = new List<MethodHookGap>();
            if (payload == null) return result;
            object value;
            if (!payload.TryGetValue("gaps", out value)) return result;
            var items = value as IEnumerable<object>;
            if (items == null) return result;
            foreach (var item in items)
            {
                var dictionary = item as IDictionary<string, object>;
                if (dictionary == null) continue;
                result.Add(new MethodHookGap
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
            if (runtime.Definitions.Count > 0) return false;
            runtime.GateOpen = false;
            TryDetach(runtime);
            if (runtime.Method != null) DispatchTargets.Remove(runtime.Method);
            Targets.Remove(runtime.TargetKey);
            AdapterIdentities.Remove(runtime.TargetKey);
            return true;
        }

        private static void TryDeferredDetach(string targetKey)
        {
            MethodHookTargetRuntime runtime;
            lock (Sync) runtime = FindRuntimeByKey(targetKey);
            if (runtime == null) return;
            if (runtime.Definitions.Count > 0) return;
            TryDetach(runtime);
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
                        TryDetach(runtime);
                        if (runtime.Method != null) DispatchTargets.Remove(runtime.Method);
                Targets.Remove(runtime.TargetKey);
                        AdapterIdentities.Remove(runtime.TargetKey);
                    }
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
            UnityCliBridge.WriteLog("method_hook_callback_compile key=" + key.Substring(0, 16) + " refs=" + ReferenceFingerprint().Substring(0, 16) + " " + ReferenceDiagnostics());
            if (!DynamicCodeCompiler.TryBeginCompilation())
                throw new BridgeCommandException("E_UNITY_BUSY", "Another dynamic compilation currently owns the single compiler.", new Dictionary<string, object> { { "owner", "dynamic-compiler" } }, new[] { "Wait for the current compilation to reach its terminal result, then submit this request again." }, true, 250);
            try
            {
                lock (Sync) EnsureCompileBudget(0);
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
                    MaxSourceBytes = MethodHookLimits.MaxCallbackSourceBytes * 2,
                    MaxArtifactBytes = MethodHookLimits.MaxSingleArtifactBytes
                };
                List<string> references;
                try { references = DynamicCodeCompiler.CollectReferences(null, null, () => !cancellationToken.IsCancellationRequested); }
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
                // 累计预算必须在加载新产物前用真实 bytes 再次核对；超限则不加载该产物。
                lock (Sync) EnsureCompileBudget(compilation.AssemblyBytes.LongLength);
                var loaded = Assembly.Load(compilation.AssemblyBytes);
                var type = loaded.GetType(CallbackTypeName(key), false);
                var instance = type == null ? null : Activator.CreateInstance(type) as IUnityCliMethodHookCallback;
                if (instance == null)
                    throw new BridgeCommandException("E_HOOK_CALLBACK_FAILED", "The compiled callback does not implement the hook callback contract.", null, new[] { "Remove unsupported async or generic callback shapes and retry." });
                lock (Sync)
                {
                    compiledArtifactBytes += compilation.AssemblyBytes.LongLength;
                    LoadedCallbackKeys.Add(key);
                    CallbackCache[key] = instance;
                }
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
                DynamicCodeCompiler.EndCompilation();
            }
        }

        /// <summary>编译预算按当前域曾加载的独特产物数与累计 bytes 计数。</summary>
        private static void EnsureCompileBudget(long additionalBytes)
        {
            if (LoadedCallbackKeys.Count >= MethodHookLimits.MaxCompiledArtifacts)
                throw new BridgeCommandException("E_HOOK_COMPILE_BUDGET_EXHAUSTED", "This domain already loaded " + MethodHookLimits.MaxCompiledArtifacts.ToString(CultureInfo.InvariantCulture) + " distinct callback artifacts.", "Disable or remove hooks, or reload the domain, before loading another callback artifact.");
            if (compiledArtifactBytes + additionalBytes > MethodHookLimits.MaxCompiledArtifactBytes)
                throw new BridgeCommandException("E_HOOK_COMPILE_BUDGET_EXHAUSTED", "This domain already loaded the maximum callback assembly bytes.", "Disable or remove hooks, or reload the domain, before loading another callback artifact.");
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
                { "references", ReferenceFingerprint() }
            });
        }

        /// <summary>
        /// 当前域的引用身份与 MVID 摘要，按域快照一次（域重载或启动时重新建立）。
        /// 快照语义是必须的：若每次编译都重算，同域内任何后续加载（回调程序集、code.execute 动态程序集、
        /// 其他功能的惰性加载）都会改变键，同源码再也无法复用，与“同域同 key 复用”的目标相矛盾。
        /// 本功能自己生成的产物是输出而不是输入，必须排除。
        /// </summary>
        private static string ReferenceFingerprint()
        {
            if (domainReferenceFingerprint != null) return domainReferenceFingerprint;
            var identities = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || IsOwnGeneratedAssembly(assembly)) continue;
                try
                {
                    var name = assembly.GetName();
                    var mvid = assembly.ManifestModule == null ? string.Empty : assembly.ManifestModule.ModuleVersionId.ToString("D");
                    identities.Add((name.FullName ?? name.Name) + "#" + mvid);
                }
                catch
                {
                    // 无法读取身份的动态或已卸载程序集不参与指纹。
                }
            }
            identities.Sort(StringComparer.Ordinal);
            var fingerprint = UnityCliSerializer.Sha256Canonical(identities.Cast<object>().ToList());
            lock (Sync) domainReferenceFingerprint = fingerprint;
            return fingerprint;
        }

        /// <summary>判断程序集是否由本功能在运行时生成（回调 wrapper 或 Adapter 容器）。</summary>
        private static bool IsOwnGeneratedAssembly(Assembly assembly)
        {
            var simpleName = assembly.GetName().Name ?? string.Empty;
            return simpleName.StartsWith("UnityCli.HookCallback.", StringComparison.Ordinal)
                || string.Equals(simpleName, "UnityCli.HookAdapters.Generated", StringComparison.Ordinal);
        }

        /// <summary>指纹内容的有界诊断：参与计数的程序集数量与排除掉的自家产物数量。</summary>
        private static string ReferenceDiagnostics()
        {
            var count = 0;
            var excluded = 0;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || IsOwnGeneratedAssembly(assembly)) { excluded++; continue; }
                count++;
            }
            return "refCount=" + count.ToString(CultureInfo.InvariantCulture) + " excluded=" + excluded.ToString(CultureInfo.InvariantCulture);
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
                    AppendEvent(new MethodHookEventRecord
                    {
                        HookId = definition.HookId,
                        Revision = definition.Revision,
                        Kind = "lifecycle",
                        PlaySessionId = playSessionId,
                        ClockSegment = clockSegment,
                        Frame = tickCounter,
                        Name = "restored",
                        MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
                    });
                }
            }
            catch (Exception exception)
            {
                FaultDefinition(definition, exception is BridgeCommandException ? ((BridgeCommandException)exception).Code : "E_HOOK_CALLBACK_FAILED",
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
            LoadedCallbackKeys.Clear();
            AdapterIdentities.Clear();
            compiledArtifactBytes = 0;
            domainReferenceFingerprint = null;
            restoreCompileInFlight = false;
            playSessionId = string.Empty;
            clockSegment = string.Empty;
            playSessionIndex = 0;
            tickCounter = 0;
        }

        private static void CancelPendingWork()
        {
            try { if (restoreCancellation != null) restoreCancellation.Cancel(); } catch { }
            try { if (restoreCancellation != null) restoreCancellation.Dispose(); } catch { }
            restoreCancellation = null;
        }

        private static MethodHookRuntimeSummary SummaryLocked()
        {
            return new MethodHookRuntimeSummary
            {
                EditorSessionId = UnityCliBridge.EditorSessionId(),
                DefinitionCount = Definitions.Count,
                TargetCount = Targets.Count,
                AdapterIdentities = AdapterIdentities.Count,
                CompiledArtifacts = LoadedCallbackKeys.Count,
                CompiledArtifactBytes = compiledArtifactBytes,
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
                definition.SampleIndex = 0;
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
                AppendEvent(new MethodHookEventRecord
                {
                    HookId = definition.HookId,
                    Revision = definition.Revision,
                    Kind = "lifecycle",
                    PlaySessionId = playSessionId,
                    ClockSegment = clockSegment,
                    Frame = tickCounter,
                    Name = reason,
                    MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
                });
            }
        }

        private static void AppendLifecycle(MethodHookDefinition definition, string name, string detail)
        {
            AppendEvent(new MethodHookEventRecord
            {
                HookId = definition.HookId,
                Revision = definition.Revision,
                Kind = "lifecycle",
                PlaySessionId = playSessionId,
                ClockSegment = clockSegment,
                Frame = tickCounter,
                Name = name,
                Value = detail,
                MethodIdentity = definition.TargetSpec == null ? string.Empty : definition.TargetSpec.TypeName + "." + definition.TargetSpec.MethodName
            });
        }

        private static void AppendEvent(MethodHookEventRecord record)
        {
            record.PlaySessionId = string.IsNullOrEmpty(record.PlaySessionId) ? playSessionId : record.PlaySessionId;
            record.ClockSegment = string.IsNullOrEmpty(record.ClockSegment) ? clockSegment : record.ClockSegment;
            record.Frame = record.Frame == 0 ? tickCounter : record.Frame;
            Events.Append(record);
        }

        private static MethodHookDefinition RequireDefinition(string hookId, string editorSessionId, long? expectedRevision, bool allowUnknownSession = false)
        {
            if (string.IsNullOrWhiteSpace(hookId))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "hookId is required.", "Pass the hookId returned by method-hook.install or method-hook.list.");
            if (!allowUnknownSession)
            {
                if (string.IsNullOrWhiteSpace(editorSessionId))
                    throw new BridgeCommandException("E_SCHEMA_INVALID", "editorSessionId is required for this mutation.", "Pass the editorSessionId returned by install or info.");
                if (!string.Equals(editorSessionId, UnityCliBridge.EditorSessionId(), StringComparison.Ordinal))
                    throw new BridgeCommandException("E_HOOK_SESSION_STALE", "The definition belongs to a different Editor session.", null, new[] { "Re-read method-hook.list on the current session before mutating definitions." });
            }
            lock (Sync)
            {
                MethodHookDefinition definition;
                if (!Definitions.TryGetValue(hookId, out definition))
                    throw new BridgeCommandException("E_HOOK_NOT_FOUND", "No hook definition with that id exists in the current Editor session.", null, new[] { "Read method-hook.list to inspect the current definitions." });
                if (expectedRevision.HasValue && definition.Revision != expectedRevision.Value)
                    throw new BridgeCommandException("E_HOOK_REVISION_STALE", "The definition revision changed from " + expectedRevision.Value.ToString(CultureInfo.InvariantCulture) + " to " + definition.Revision.ToString(CultureInfo.InvariantCulture) + ".", null, new[] { "Read method-hook.info for the current revision and retry with expectedRevision." });
                return definition;
            }
        }

        private static MethodHookCapture NormalizeCapture(MethodHookCaptureSpec spec)
        {
            var capture = new MethodHookCapture();
            if (spec == null) return capture;
            capture.Arguments = spec.Arguments ?? false;
            capture.Result = spec.Result ?? false;
            capture.SampleEvery = spec.SampleEvery == null ? 1 : Math.Min(Math.Max(1, spec.SampleEvery.Value), MethodHookLimits.MaxSampleEvery);
            if (spec.InstanceFields != null) foreach (var field in spec.InstanceFields) capture.InstanceFields.Add(NormalizeField(field));
            if (spec.StaticFields != null) foreach (var field in spec.StaticFields) capture.StaticFields.Add(NormalizeField(field));
            if (capture.FieldCount > MethodHookLimits.MaxCaptureFields)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A hook captures at most " + MethodHookLimits.MaxCaptureFields.ToString(CultureInfo.InvariantCulture) + " exact fields.", "Reduce the captured field list.");
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
            if (Encoding.UTF8.GetByteCount(prefix) > MethodHookLimits.MaxCallbackSourceBytes)
                throw new BridgeCommandException("E_HOOK_LIMIT_EXCEEDED", "prefixCode exceeds the declared callback source budget.", null, new[] { "Split the callback into a smaller body or move shared logic into project code." });
            if (Encoding.UTF8.GetByteCount(postfix) > MethodHookLimits.MaxCallbackSourceBytes)
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
                throw new BridgeCommandException("E_CONTINUATION_STALE", "The list continuation no longer matches the current Editor session or filters.", null, new[] { "Re-read method-hook.list without a cursor." });
            }
        }
    }
}
#endif

