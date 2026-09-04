#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>BuildPlayer 的进行中状态只保留在 active-build 文件，终态立即删除。</summary>
        internal static class UnityCliLiveBuildCoordinator
        {
            internal const string Command = "internal.build.run.start";
            private const string Scope = "build";
            private static bool reloadRecoveryPending;
            internal static bool IsCommand(string command) { return command == Command; }

            /// <summary>production固定真实prepare/effect；测试仅按单次Execute显式注入，不写全局hook。</summary>
            internal sealed class BuildExecutionDependencies
            {
                internal Func<string, string, List<string>, Prepared> PrepareTarget;
                internal Func<Prepared, BuildEffectResult> RunEffect;
                internal readonly BuildTestBoundary Boundary;

                internal BuildExecutionDependencies(bool testMode)
                {
                    Boundary = new BuildTestBoundary(testMode);
                }

                internal static BuildExecutionDependencies Production()
                {
                    var dependencies = new BuildExecutionDependencies(false)
                    {
                        PrepareTarget = (target, outputPath, options) => Prepare(new TargetArgs { Target = target, OutputPath = outputPath, Options = options }),
                        RunEffect = RunBuildEffect
                    };
                    return dependencies;
                }

                internal void Hit(string name)
                {
                    Boundary.Hit(name);
                }

                internal sealed class BuildTestBoundary
                {
                    private readonly object sync = new object();
                    private readonly Dictionary<string, Action> expected = new Dictionary<string, Action>(StringComparer.Ordinal);
                    private readonly bool enabled;

                    internal BuildTestBoundary(bool enabled) { this.enabled = enabled; }
                    internal void ExpectOnce(string name, Action action)
                    {
                        if (!enabled) throw new InvalidOperationException("Production build dependencies do not accept test boundaries.");
                        if (string.IsNullOrWhiteSpace(name) || action == null) throw new ArgumentException("Build test boundary is invalid.");
                        lock (sync)
                        {
                            if (expected.ContainsKey(name)) throw new InvalidOperationException("Build test boundary is already registered: " + name);
                            expected.Add(name, action);
                        }
                    }
                    internal void Hit(string name)
                    {
                        Action action;
                        lock (sync)
                        {
                            if (!expected.TryGetValue(name, out action)) return;
                            expected.Remove(name);
                        }
                        action();
                    }
                    internal void AssertDrained()
                    {
                        lock (sync)
                        {
                            if (expected.Count != 0) throw new InvalidOperationException("Build test boundaries were not consumed: " + string.Join(",", expected.Keys));
                        }
                    }
                }
            }

            internal static void Register(BridgeCommandRegistry registry)
            {
                registry.Register<BridgeRequest>(Command, "internal", true, 600000, (r, c) => { throw new InvalidOperationException("Build is coordinator-owned."); }, UnityCliBridgeQueuePolicy.WorkClass.Mutation);
            }

            /// <summary>reload后先持久化outcome-unknown标记，再释放已终止旧domain的exact lease。</summary>
            internal static void Initialize() { RecoverTerminalCleanup(ProductionQueueState); reloadRecoveryPending = true; MarkReloadOutcomeUnknownAndRelease(); }
            internal static void Tick() { Tick(ProductionQueueState); }
            internal static void Tick(BridgeQueueState state) { RecoverTerminalCleanup(state); if (object.ReferenceEquals(state, ProductionQueueState) && reloadRecoveryPending) MarkReloadOutcomeUnknownAndRelease(); }

            private static void MarkReloadOutcomeUnknownAndRelease()
            {
                Dictionary<string, object> active;
                try { active = UnityCliActiveOperation.Load(Scope); }
                catch { return; }
                if (active == null) { reloadRecoveryPending = false; return; }
                var requestId = Value(active, "requestId");
                var requestDigest = Value(active, "requestDigest");
                var editorSessionId = Value(active, "editorSessionId");
                if (!IsSafeId(requestId) || !IsSha256(requestDigest)) return;
                if (!string.Equals(editorSessionId, EditorSessionId(), StringComparison.Ordinal))
                {
                    try { UnityCliActiveOperation.Clear(Scope); reloadRecoveryPending = false; } catch { }
                    return;
                }
                object marked;
                if (!active.TryGetValue("reloadOutcomeUnknown", out marked) || !(marked is bool) || !(bool)marked)
                {
                    active["reloadOutcomeUnknown"] = true;
                    try { UnityCliActiveOperation.Save(Scope, active); }
                    catch { return; }
                }
                UnityCliSessionMutationLease.Release(requestId, requestDigest);
                bool isOwned;
                if (UnityCliSessionMutationLease.TryIsOwnedBy(requestId, requestDigest, out isOwned) && !isOwned)
                    reloadRecoveryPending = false;
            }

            internal static void BeforeReload() { }
            internal static void Shutdown() { reloadRecoveryPending = false; }

            internal static Task<string> Execute(BridgeRequest request, byte[] rawRequestBytes, Func<bool> tryAccept, CancellationToken cancellationToken)
            {
                return Execute(ProductionQueueState, request, rawRequestBytes, tryAccept, cancellationToken, BuildExecutionDependencies.Production());
            }

            internal static async Task<string> Execute(BridgeQueueState queueState, BridgeRequest request, byte[] rawRequestBytes, Func<bool> tryAccept, CancellationToken cancellationToken, BuildExecutionDependencies dependencies)
            {
                if (queueState == null) throw new ArgumentNullException("queueState");
                if (dependencies == null || dependencies.PrepareTarget == null || dependencies.RunEffect == null) throw new ArgumentNullException("dependencies");
                var args = request.DeserializeArgs<Args>();
                if (request.Execution != null && request.Execution.DryRun == true)
                {
                    Validate(args);
                    return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "dryRun", true }, { "plannedEffects", args.Targets.Select(x => (object)new Dictionary<string, object> { { "type", "build-player" }, { "target", x.Target }, { "outputPath", x.OutputPath } }).ToList() } }));
                }
                var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
                object recoveredTerminal;
                if (queueState.TryGetTerminal(Scope, request.RequestId, digest, out recoveredTerminal))
                {
                    try { UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, request.RequestId, digest, queueState); } catch { }
                    return Terminal(request.RequestId, recoveredTerminal);
                }
                Dictionary<string, object> active;
                try { active = queueState.LoadOperation(Scope); }
                catch (Exception) { return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build active state cannot be read; outcome is unknown.")); }
                if (active != null)
                {
                    if (!string.Equals(Value(active, "requestId"), request.RequestId, StringComparison.Ordinal)) return Busy(request.RequestId);
                    if (!string.Equals(Value(active, "requestDigest"), digest, StringComparison.Ordinal))
                        return SerializeBridgeExceptionEnvelope(request.RequestId, new BridgeCommandException("E_REQUEST_ID_REUSED", "Build requestId was reused with different input."));
                    object reloadUnknown;
                    if (!active.TryGetValue("reloadOutcomeUnknown", out reloadUnknown) || !(reloadUnknown is bool) || !(bool)reloadUnknown)
                        return Busy(request.RequestId);
                    var unknown = ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "A previous build did not reach a terminal result before reload or timeout.", null, new[] { "Inspect build output and editor.state before retrying." });
                    return FinishTerminal(queueState, request.RequestId, digest, unknown, dependencies);
                }
                bool newlyAcquiredLease;
                if (!queueState.TryAcquireLease(request.RequestId, digest, out newlyAcquiredLease) || !newlyAcquiredLease) return Busy(request.RequestId);
                AdmissionOwnerIdentity queueAdmissionOwner;
                try { queueAdmissionOwner = CaptureAdmissionOwner(queueState, request.RequestId, digest); }
                catch (Exception exception)
                {
                    return FinishBuildPreRootFault(queueState, request.RequestId, digest, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build queue admission identity could not be frozen: " + exception.GetType().Name + "."));
                }
                var activeRootCreated = false;
                try
                {
                    if (tryAccept == null || !tryAccept()) throw new OperationCanceledException();
                    Validate(args);
                    var prepared = args.Targets.Select(item => dependencies.PrepareTarget(item.Target, item.OutputPath, item.Options)).ToList();
                    active = NewActive(request, digest, args);
                    queueState.SaveOperation(Scope, active);
                    activeRootCreated = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    active["phase"] = "running";
                    queueState.SaveOperation(Scope, active);
                    var reports = new List<object>();
                    foreach (var item in prepared)
                    {
                        bool ownedBefore;
                        if (!queueState.TryIsLeaseOwned(request.RequestId, digest, out ownedBefore) || !ownedBefore)
                            return FinishTerminal(queueState, request.RequestId, digest, UnknownLeaseTerminal(request.RequestId), dependencies);
                        reports.Add(Build(item, dependencies));
                        bool ownedAfter;
                        if (!queueState.TryIsLeaseOwned(request.RequestId, digest, out ownedAfter) || !ownedAfter)
                            return FinishTerminal(queueState, request.RequestId, digest, UnknownLeaseTerminal(request.RequestId), dependencies);
                    }
                    var data = new Dictionary<string, object> { { "targets", reports } };
                    var failed = reports.Cast<Dictionary<string, object>>().Any(x => !string.Equals(x["result"] as string, "Succeeded", StringComparison.Ordinal));
                    object envelope = failed ? (object)ProtocolEnvelopeFactory.Error(request.RequestId, "E_BUILD_FAILED", "One or more BuildPlayer reports were not successful.", data, new[] { "Inspect the returned BuildReport summaries and Unity Console." }) : ProtocolEnvelopeFactory.Success(request.RequestId, data);
                    return FinishTerminal(queueState, request.RequestId, digest, envelope, dependencies);
                }
                catch (OperationCanceledException)
                {
                    var error = new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build did not begin before the request deadline.");
                    if (!activeRootCreated && ActiveRootDefinitelyAbsent(queueState, request.RequestId, digest))
                        return FinishBuildPreRootFault(queueState, request.RequestId, digest, error);
                    return FinishBuildTerminalAfterRootFault(queueState, request.RequestId, digest, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "Build did not complete before the request deadline.", null, new[] { "Inspect editor.state and retry." }), queueAdmissionOwner, dependencies);
                }
                catch (BridgeCommandException ex)
                {
                    if (!activeRootCreated && ActiveRootDefinitelyAbsent(queueState, request.RequestId, digest))
                        return FinishBuildPreRootFault(queueState, request.RequestId, digest, ex);
                    return FinishBuildTerminalAfterRootFault(queueState, request.RequestId, digest, BridgeExceptionTerminal(request.RequestId, ex), queueAdmissionOwner, dependencies);
                }
                catch (Exception ex)
                {
                    WriteLog("build_unhandled_exception requestId=" + request.RequestId + " activeRootCreated=" + activeRootCreated + " exception=" + ex);
                    var error = new BridgeCommandException("E_EXECUTION_FAILED", "Build preparation failed before BuildPlayer could start.", "Inspect UnityCliBridge.log and Build Settings before retrying build.run.");
                    if (!activeRootCreated && ActiveRootDefinitelyAbsent(queueState, request.RequestId, digest))
                        return FinishBuildPreRootFault(queueState, request.RequestId, digest, error);
                    return FinishBuildTerminalAfterRootFault(queueState, request.RequestId, digest, SerializeException(request.RequestId, ex), queueAdmissionOwner, dependencies);
                }
                finally
                {
                    try
                    {
                        UnityCliActiveOperation.RetryExactTerminalCleanup(Scope, queueState);
                        if (!queueState.OperationExists(Scope) && !queueState.OperationExists(Scope + "-terminal-cleanup"))
                            queueState.ReleaseLease(request.RequestId, digest);
                    }
                    catch { }
                    await Task.FromResult(0);
                }
            }

            private static Dictionary<string, object> NewActive(BridgeRequest request, string digest, Args args) { return new Dictionary<string, object> { { "requestId", request.RequestId }, { "requestDigest", digest }, { "editorSessionId", EditorSessionId() }, { "phase", "accepted" }, { "startedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, { "targets", args.Targets.Select(x => (object)new Dictionary<string, object> { { "target", x.Target }, { "outputPath", x.OutputPath } }).ToList() } }; }
            private static string Value(IDictionary<string, object> value, string key) { object item; return value != null && value.TryGetValue(key, out item) ? item as string : null; }
            private static string Busy(string id) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(id, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, new[] { "Wait for the current mutation to reach its terminal result, then retry." }, true, 250)); }
            private static bool ActiveRootDefinitelyAbsent(BridgeQueueState state, string id, string digest)
            {
                try
                {
                    var root = state.LoadOperation(Scope);
                    if (root != null) return false;
                    return !state.OperationExists(Scope);
                }
                catch { return false; }
            }

            private static string FinishBuildPreRootFault(BridgeQueueState state, string id, string digest, BridgeCommandException error)
            {
                try { state.ReleaseLease(id, digest); } catch { }
                bool isOwned;
                if (state.TryIsLeaseOwned(id, digest, out isOwned) && !isOwned)
                    return SerializeBridgeExceptionEnvelope(id, error);
                try { UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, id, digest, state); } catch { }
                return SerializeBridgeExceptionEnvelope(id, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build was rejected before its durable root, but exact lease cleanup remains pending."));
            }

            private static string FinishBuildTerminalAfterRootFault(BridgeQueueState queueState, string id, string digest, object envelope, AdmissionOwnerIdentity queueAdmissionOwner, BuildExecutionDependencies dependencies)
            {
                try { return FinishTerminal(queueState, id, digest, envelope, dependencies); }
                catch (Exception exception)
                {
                    var unknown = ProtocolEnvelopeFactory.Error(id, "E_EXECUTION_TIMEOUT", "Build active state or terminal publication failed after effect; outcome is unknown.", null, new[] { "Inspect build artifacts and editor.state before submitting another mutation." });
                    try
                    {
                        var owner = queueAdmissionOwner ?? CaptureAdmissionOwner(queueState, id, digest);
                        owner.TopRequestDigest = digest;
                        PersistAdmissionOwnerTransfer(queueState, Scope + "-admission-owner", new Dictionary<string, object>
                        {
                            { "requestId", id }, { "requestDigest", digest }, { "editorSessionId", EditorSessionId() }
                        }, owner, UnityCliAdmissionPool.AdmissionOwnerState.Recovery);
                    }
                    catch (Exception markerException)
                    {
                        return SerializeBridgeExceptionEnvelope(id, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build recovery marker remains pending after a post-root failure: " + markerException.GetType().Name + "."));
                    }
                    try
                    {
                        try
                        {
                            var recoveryRoot = queueState.LoadOperation(Scope);
                            if (recoveryRoot != null && string.Equals(Value(recoveryRoot, "requestId"), id, StringComparison.Ordinal)
                                && string.Equals(Value(recoveryRoot, "requestDigest"), digest, StringComparison.Ordinal))
                            {
                                recoveryRoot["phase"] = "terminal_pending";
                                recoveryRoot["terminalEnvelope"] = unknown;
                                queueState.SaveOperation(Scope, recoveryRoot);
                            }
                        }
                        catch { }
                        dependencies.Hit("before-build-terminal-store");
                        var first = queueState.StoreTerminalFirst(Scope, id, digest, unknown);
                        try { UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, id, digest, queueState); } catch { }
                        return Terminal(id, first);
                    }
                    catch
                    {
                        return SerializeBridgeExceptionEnvelope(id, new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build conservative terminal remains pending after a post-root failure: " + exception.GetType().Name + "."));
                    }
                }
            }

            private static object BridgeExceptionTerminal(string id, BridgeCommandException exception)
            {
                return UnityCliSerializer.Parse(SerializeBridgeExceptionEnvelope(id, exception));
            }

            private static string FinishTerminal(BridgeQueueState queueState, string id, string digest, object envelope, BuildExecutionDependencies dependencies)
            {
                bool isOwned;
                if (!queueState.TryIsLeaseOwned(id, digest, out isOwned) || !isOwned) envelope = UnknownLeaseTerminal(id);
                dependencies.Hit("before-build-terminal-root-read");
                var active = queueState.LoadOperation(Scope);
                if (active != null)
                {
                    if (!string.Equals(Value(active, "requestId"), id, StringComparison.Ordinal)
                        || !string.Equals(Value(active, "requestDigest"), digest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_EXECUTION_TIMEOUT", "Build terminal active identity cannot be proven.");
                    active["phase"] = "terminal_pending";
                    active["terminalEnvelope"] = envelope;
                    queueState.SaveOperation(Scope, active);
                }
                dependencies.Hit("before-build-terminal-store");
                var first = queueState.StoreTerminalFirst(Scope, id, digest, envelope);
                UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, id, digest, queueState);
                return Terminal(id, first);
            }
            private static void RecoverTerminalCleanup(BridgeQueueState state)
            {
                try
                {
                    if (UnityCliActiveOperation.RetryExactTerminalCleanup(Scope, state)) return;
                    var active = state.LoadOperation(Scope);
                    if (active == null) { RecoverFromBuildAdmissionMarker(state); return; }
                    var requestId = Value(active, "requestId");
                    var requestDigest = Value(active, "requestDigest");
                    object terminal = null;
                    var hasTerminal = IsSafeId(requestId) && IsSha256(requestDigest)
                        && state.TryGetTerminal(Scope, requestId, requestDigest, out terminal);
                    if (!hasTerminal)
                    {
                        object pending;
                        if (!active.TryGetValue("terminalEnvelope", out pending) || pending == null)
                        {
                            if (!string.Equals(Value(active, "phase"), "running", StringComparison.Ordinal)) return;
                            pending = UnknownLeaseTerminal(requestId);
                            active["phase"] = "terminal_pending";
                            active["terminalEnvelope"] = pending;
                            state.SaveOperation(Scope, active);
                        }
                        terminal = state.StoreTerminalFirst(Scope, requestId, requestDigest, pending);
                    }
                    if (terminal != null) UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, requestId, requestDigest, state);
                }
                catch { RecoverFromBuildAdmissionMarker(state); }
            }

            /// <summary>root缺失时按完整Recovery marker与exact lease决定保守终态或无root清理。</summary>
            private static void RecoverFromBuildAdmissionMarker(BridgeQueueState state)
            {
                try
                {
                    var markerScope = Scope + "-admission-owner";
                    var marker = state.LoadOperation(markerScope);
                    if (marker == null || (UnityCliAdmissionPool.AdmissionOwnerState)AdmissionLong(marker, "admissionIntendedOwnerState") != UnityCliAdmissionPool.AdmissionOwnerState.Recovery) return;
                    var requestId = Value(marker, "requestId");
                    var topRequestDigest = Value(marker, "requestDigest");
                    var ownerRequestDigest = Value(marker, "admissionRequestDigest");
                    if (!IsSafeId(requestId) || !IsSha256(topRequestDigest) || !IsSha256(ownerRequestDigest)) return;
                    var owner = ReconcileAdmissionTransferForLiveMarker(state, markerScope, marker, requestId, ownerRequestDigest);
                    if (owner == null || !string.Equals(owner.TopRequestDigest, topRequestDigest, StringComparison.Ordinal)) return;
                    bool leaseOwned;
                    if (!state.TryIsLeaseOwned(owner.RequestId, owner.RequestDigest, out leaseOwned)) return;
                    object terminal;
                    if (!state.TryGetTerminal(Scope, requestId, topRequestDigest, out terminal))
                    {
                        var recoveryTerminal = leaseOwned
                            ? ProtocolEnvelopeFactory.Error(requestId, "E_EXECUTION_TIMEOUT", "Build active root disappeared while its exact lease was retained; outcome is unknown.", null, new[] { "Inspect build artifacts before submitting another mutation." })
                            : UnknownLeaseTerminal(requestId);
                        terminal = state.StoreTerminalFirst(Scope, requestId, topRequestDigest, recoveryTerminal);
                    }
                    object confirmed;
                    if (terminal == null || !state.TryGetTerminal(Scope, requestId, topRequestDigest, out confirmed)
                        || !string.Equals(UnityCliSerializer.Serialize(terminal), UnityCliSerializer.Serialize(confirmed), StringComparison.Ordinal)) return;
                    UnityCliActiveOperation.BeginExactTerminalCleanup(Scope, requestId, topRequestDigest, state);
                }
                catch { }
            }

            private static object UnknownLeaseTerminal(string id) { return ProtocolEnvelopeFactory.Error(id, "E_EXECUTION_TIMEOUT", "Build lost its exact mutation lease after BuildPlayer; outcome is unknown.", null, new[] { "Inspect build artifacts before submitting another mutation." }); }
            private static string Terminal(string id, object envelope) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", envelope } })); }
            private static object SerializeException(string id, Exception ex) { return ProtocolEnvelopeFactory.Error(id, "E_BRIDGE_COMMAND_FAILED", ex.Message, null, new[] { "Check Unity Console and UnityCliBridge.log." }); }
            internal sealed class BuildEffectResult
            {
                internal string Result;
                internal long TotalSize;
                internal long TotalErrors;
                internal long TotalWarnings;
                internal Dictionary<string, object> Artifact;
                internal List<object> Messages;
            }

            private static Prepared Prepare(TargetArgs input)
            {
                if (input == null || string.IsNullOrWhiteSpace(input.Target) || string.IsNullOrWhiteSpace(input.OutputPath)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Each build target requires target and outputPath.");
                BuildTarget target; if (!Enum.TryParse(input.Target, false, out target)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported Unity BuildTarget: " + input.Target);
                var configuredScenes = EditorBuildSettings.scenes ?? Array.Empty<EditorBuildSettingsScene>();
                var scenes = configuredScenes.Where(x => x != null && x.enabled && File.Exists(x.path)).Select(x => x.path).ToArray();
                if (scenes.Length == 0) throw new BridgeCommandException("E_BUILD_SCENES_EMPTY", "No enabled existing build scenes are available.");
                var publicPath = input.OutputPath.Replace('\\', '/'); if (Path.IsPathRooted(input.OutputPath) || publicPath.Split('/').Any(x => x == "..")) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build outputPath must be project-relative.");
                var projectRoot = Path.GetFullPath(ProjectRoot()); var outputPath = Path.GetFullPath(Path.Combine(projectRoot, input.OutputPath)); if (!outputPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !string.Equals(outputPath, projectRoot, StringComparison.Ordinal)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build outputPath escapes the project.");
                return new Prepared { Target = target, TargetName = input.Target, OutputPath = outputPath, PublicOutputPath = publicPath, Scenes = scenes, Options = ParseOptions(input.Options) };
            }
            private static BuildOptions ParseOptions(List<string> values) { var result = BuildOptions.None; foreach (var value in values ?? new List<string>()) { BuildOptions option; if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse(value, false, out option)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Unsupported BuildOptions value: " + value); result |= option; } return result; }
            private static Dictionary<string, object> Build(Prepared p, BuildExecutionDependencies dependencies)
            {
                var started = DateTimeOffset.UtcNow;
                var parent = Path.GetDirectoryName(p.OutputPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var effect = dependencies.RunEffect(p);
                return BuildResultData(p, effect, started, DateTimeOffset.UtcNow);
            }

            private static Dictionary<string, object> BuildResultData(Prepared prepared, BuildEffectResult effect, DateTimeOffset started, DateTimeOffset finished)
            {
                if (effect == null) throw new InvalidOperationException("Build effect returned no result.");
                if (effect.Result == BuildResult.Succeeded.ToString() && !SuccessfulArtifactProof(effect.Artifact))
                    throw new BridgeCommandException("E_BUILD_ARTIFACT_MISSING", "BuildReport succeeded but its fresh output artifact has no non-empty SHA-256 proof.");
                return new Dictionary<string, object>
                {
                    { "target", prepared.TargetName }, { "result", effect.Result }, { "outputPath", prepared.PublicOutputPath },
                    { "totalSize", effect.TotalSize }, { "totalErrors", effect.TotalErrors }, { "totalWarnings", effect.TotalWarnings },
                    { "startedAt", started.ToString("O", CultureInfo.InvariantCulture) }, { "finishedAt", finished.ToString("O", CultureInfo.InvariantCulture) },
                    { "durationMs", (long)(finished - started).TotalMilliseconds }, { "messages", effect.Messages ?? new List<object>() }, { "artifact", effect.Artifact }
                };
            }

            private static BuildEffectResult RunBuildEffect(Prepared p)
            {
                var report = BuildPipeline.BuildPlayer(p.Scenes, p.OutputPath, p.Target, p.Options);
                var artifact = ReadArtifact(p.OutputPath, p.PublicOutputPath);
                var messages = report.steps.SelectMany(step => step.messages ?? new BuildStepMessage[0]).Where(message => !string.IsNullOrWhiteSpace(message.content)).Select(message => (object)new Dictionary<string, object> { { "type", message.type.ToString() }, { "message", message.content } }).Take(100).ToList();
                return new BuildEffectResult { Result = report.summary.result.ToString(), TotalSize = checked((long)report.summary.totalSize), TotalErrors = report.summary.totalErrors, TotalWarnings = report.summary.totalWarnings, Artifact = artifact, Messages = messages };
            }
            private static Dictionary<string, object> ReadArtifact(string path, string publicPath) { var roots = new List<string>(); if (File.Exists(path)) roots.Add(path); if (Directory.Exists(path)) roots.Add(path); var sidecar = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path) + "_Data"); if (Directory.Exists(sidecar)) roots.Add(sidecar); var files = roots.SelectMany(root => File.Exists(root) ? new[] { root } : Directory.GetFiles(root, "*", SearchOption.AllDirectories)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray(); var size = files.Sum(x => new FileInfo(x).Length); var treeRoot = Path.GetDirectoryName(path) ?? path; return new Dictionary<string, object> { { "outputPath", publicPath }, { "exists", files.Length > 0 }, { "hash", File.Exists(path) ? HashFile(path) : null }, { "treeHash", files.Length > 0 ? HashTree(files, treeRoot) : null }, { "fileCount", (long)files.Length }, { "size", size } }; }
            private static bool SuccessfulArtifactProof(Dictionary<string, object> artifact) { object exists; object count; object size; object hash; object treeHash; return artifact != null && artifact.TryGetValue("exists", out exists) && artifact.TryGetValue("fileCount", out count) && artifact.TryGetValue("size", out size) && artifact.TryGetValue("hash", out hash) && artifact.TryGetValue("treeHash", out treeHash) && exists is bool && (bool)exists && count is long && (long)count > 0 && size is long && (long)size > 0 && (IsSha256(hash as string) || IsSha256(treeHash as string)); }
            private static string HashFile(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
            private static string HashTree(IEnumerable<string> files, string root) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", files.Select(x => Path.GetRelativePath(root, x).Replace('\\', '/') + "\0" + HashFile(x)))))).Replace("-", "").ToLowerInvariant(); }
            private static void Validate(Args args) { if (args == null || string.IsNullOrWhiteSpace(args.ExpectedEditorSessionId) || args.Targets == null || args.Targets.Count == 0) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build targets and expectedEditorSessionId are required."); if (args.Targets.Any(x => x == null || string.IsNullOrWhiteSpace(x.Target) || string.IsNullOrWhiteSpace(x.OutputPath))) throw new BridgeCommandException("E_SCHEMA_INVALID", "Each build target requires target and outputPath."); if (args.Targets.Select(x => x.Target).Distinct(StringComparer.Ordinal).Count() != args.Targets.Count) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build targets must not contain duplicates."); if (args.Targets.Select(x => x.OutputPath.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != args.Targets.Count) throw new BridgeCommandException("E_SCHEMA_INVALID", "Build output paths must not conflict."); if (!string.Equals(args.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before build execution."); if (EditorApplication.isPlaying || IsPlayModeChanging()) throw new BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "build.run is editor-only and requires stable Edit Mode.", new Dictionary<string, object> { { "toolName", "build.run" }, { "playModeSupport", "editor-only" } }, new[] { "Exit Play Mode before running build.run." }); if (EditorUtility.scriptCompilationFailed) throw new BridgeCommandException("E_UNITY_COMPILE_FAILED", "Unity has unresolved script compilation errors and cannot start BuildPlayer.", "Run editor.compile and fix all diagnostics before retrying build.run."); }
            private sealed class Args { public List<TargetArgs> Targets { get; set; } public string ExpectedEditorSessionId { get; set; } }
            private sealed class TargetArgs { public string Target { get; set; } public string OutputPath { get; set; } public List<string> Options { get; set; } }
            internal sealed class Prepared { internal BuildTarget Target; internal string TargetName; internal string OutputPath; internal string PublicOutputPath; internal string[] Scenes; internal BuildOptions Options; }
        }
    }
}
#endif
