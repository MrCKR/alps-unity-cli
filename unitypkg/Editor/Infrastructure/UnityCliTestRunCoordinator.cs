#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;
using UnityEngine.TestTools;
using UnityCli.Protocol;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>TestRunner 的 callback 身份和阶段保存在 active-test-run 文件中，reload 后只续接而不重放 Execute。</summary>
        internal static class UnityCliTestRunCoordinator
        {
            internal const string Command = "internal.test.run.start";
            private const string StatusCommand = "internal.test.run.status";
            private const string Scope = "test-run";
            private static readonly TimeSpan RecoveryGrace = TimeSpan.FromSeconds(5);
            private static TestRunnerApi api;
            private static Broker broker;
            // 终态不落盘；同一 Editor session 内的 status 轮询可取回 callback 结果。
            private static string terminalRequestId;
            private static object terminalEnvelope;
            private static readonly Coordinator operation = new Coordinator();

            private sealed class Coordinator : UnityCliReloadOperation
            {
                protected override string Scope { get { return "test-run"; } }
                protected override string StartCommand { get { return Command; } }
                protected override string StatusCommand { get { return UnityCliTestRunCoordinator.StatusCommand; } }
                protected override string OperationDisplayName { get { return "Test run"; } }
                protected override object DeserializeStartArgs(BridgeRequest request) { return request.DeserializeArgs<Args>(); }
                protected override void ValidateStart(object args) { }
                protected override object Planned(object args) { return null; }
                protected override Dictionary<string, object> CreateRoot(BridgeRequest request, string digest, object args) { return null; }
                protected override void OnTick(Dictionary<string, object> root) { }
                protected override object BuildSuccessData(Dictionary<string, object> root) { return null; }
                internal void Initialize() { InitializeLegacy(); }
                internal void Shutdown() { ShutdownLegacy(); }
                internal void BeforeReload() { BeforeReloadLegacy(); }
                internal override Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return LegacyExecute(request, raw, accept, ct); }
                internal override void Tick() { LegacyTick(); }
            }

            private sealed class Root { internal string RequestId; internal string RequestDigest; internal string EditorSessionId; internal DateTimeOffset AbsoluteDeadlineUtc; internal Dictionary<string, object> TestOperation; }
            internal static bool IsCommand(string command) { return operation.IsCommand(command); }
            internal static void Register(BridgeCommandRegistry registry) { operation.Register(registry); }
            internal static void Initialize() { operation.Initialize(); }
            internal static void Shutdown() { operation.Shutdown(); }
            internal static void BeforeReload() { operation.BeforeReload(); }
            private static void InitializeLegacy() { api = ScriptableObject.CreateInstance<TestRunnerApi>(); broker = new Broker(); api.RegisterCallbacks(broker, 1000); }
            private static void ShutdownLegacy() { if (api != null && broker != null) api.UnregisterCallbacks(broker); api = null; broker = null; }
            private static void BeforeReloadLegacy() { }

            internal static Task<string> Execute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct) { return operation.Execute(request, raw, accept, ct); }

            private static async Task<string> LegacyExecute(BridgeRequest request, byte[] raw, Func<bool> accept, CancellationToken ct)
            {
                if (request.Command == StatusCommand) return UnityCliSerializer.Serialize(Status(request));
                var args = request.DeserializeArgs<Args>(); var digest = UnityCliRequestDigest.Compute(raw, request.InstanceId); bool newlyAcquiredLease;
                if (!UnityCliSessionMutationLease.TryAcquire(request.RequestId, digest, out newlyAcquiredLease)) return BusyStart(request.RequestId);
                try
                {
                    Root root;
                    try { root = Load(); }
                    catch (Exception ex) { UnityCliActiveOperation.Clear(Scope); return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "Test active state is corrupt: " + ex.Message, null, new[] { "Inspect editor.state and retry." })); }
                    if (root == null)
                    {
                        if (accept == null || !accept()) throw new OperationCanceledException(); Validate(args);
                        root = NewRoot(request, digest, args); Save(root);
                    }
                    else if (!string.Equals(root.RequestId, request.RequestId, StringComparison.Ordinal)) return BusyStart(request.RequestId);
                    else if (!string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)) { Fail(root, "E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began."); return Accepted(request.RequestId); }
                    await Task.FromResult(0); return Accepted(request.RequestId);
                }
                catch (OperationCanceledException) { UnityCliActiveOperation.Clear(Scope); UnityCliSessionMutationLease.Release(request.RequestId); return Terminal(request.RequestId, ProtocolEnvelopeFactory.Error(request.RequestId, "E_EXECUTION_TIMEOUT", "Tests did not begin before the request deadline.", null, new[] { "Inspect editor.state and retry." })); }
                catch (BridgeCommandException ex) { if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId); return SerializeBridgeExceptionEnvelope(request.RequestId, ex); }
                catch { if (newlyAcquiredLease) UnityCliSessionMutationLease.Release(request.RequestId); throw; }
            }

            internal static void Tick() { operation.Tick(); }

            private static void LegacyTick()
            {
                Root root;
                try { root = Load(); }
                catch (Exception) { UnityCliActiveOperation.Clear(Scope); return; }
                if (root == null || !UnityCliSessionMutationLease.IsOwnedBy(root.RequestId)) return;
                if (!string.Equals(root.EditorSessionId, EditorSessionId(), StringComparison.Ordinal)) { Fail(root, "E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began."); return; }
                if (DateTimeOffset.UtcNow >= root.AbsoluteDeadlineUtc) { RecoverAfterDeadline(root); return; }
                var phase = Text(root.TestOperation, "phase"); if (phase != "queued") { RecoverRunIdentity(root); return; }
                if (EditorApplication.isCompiling || EditorApplication.isUpdating || IsPlayModeChanging()) return;
                string[] activeRunIds; string reason;
                if (!TryReadActiveRunIds(out activeRunIds, out reason)) { Fail(root, "E_TEST_RUN_STATE_UNPROVEN", "Unity Test Framework active-run state could not be verified: " + reason + "."); return; }
                if (activeRunIds.Length > 0) { Fail(root, "E_TEST_RUN_BUSY", "Another Unity Test Framework run is already active."); return; }
                var dispatchNonce = Guid.NewGuid().ToString("N"); Update(root, x => { x.TestOperation["phase"] = "execute_dispatching"; x.TestOperation["dispatchNonce"] = dispatchNonce; });
                try
                {
                    var runId = api.Execute(Settings(root.TestOperation));
                    if (!Guid.TryParse(runId, out _)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi.Execute returned an invalid run identity."); return; }
                    root = Required(); Update(root, x => { x.TestOperation["returnedRunId"] = runId; x.TestOperation["phase"] = "run_identity_pending"; }); ResolveRunIdentity(Required());
                }
                catch (Exception ex) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi.Execute failed: " + ex.GetType().Name + "."); }
            }

            private sealed class Broker : ICallbacks
            {
                public void RunStarted(ITestAdaptor testsToRun)
                {
                    var root = Lease(); if (!CanObserveCallback(root, "execute_dispatching", "run_identity_pending", "running")) return;
                    string[] activeRunIds; string reason; var unique = TryReadActiveRunIds(out activeRunIds, out reason) && activeRunIds.Length == 1;
                    Update(root, x => { x.TestOperation["runStarted"] = true; if (unique) x.TestOperation["observedRunId"] = activeRunIds[0]; x.TestOperation["testTreeHash"] = TestTreeHash(testsToRun); x.TestOperation["testCaseCount"] = (long)Math.Max(0, testsToRun.TestCaseCount); });
                }
                public void TestStarted(ITestAdaptor test) { }
                public void TestFinished(ITestResultAdaptor result) { }
                public void RunFinished(ITestResultAdaptor result)
                {
                    var root = Lease(); if (!CanObserveCallback(root, "execute_dispatching", "run_identity_pending", "running") || !Bool(root.TestOperation, "runStarted")) return;
                    var observed = Text(root.TestOperation, "observedRunId"); if (string.IsNullOrEmpty(observed) || !Same(Text(root.TestOperation, "testTreeHash"), TestTreeHash(result.Test))) return;
                    if (Text(root.TestOperation, "phase") == "running" && Same(Text(root.TestOperation, "runId"), observed)) Complete(root, result);
                    else Update(root, x => { x.TestOperation["pendingFinished"] = CaptureResultData(x, result); x.TestOperation["pendingFinishedRunId"] = observed; });
                }
            }

            private static Root Lease() { var root = LoadSafely(); return root != null && UnityCliSessionMutationLease.IsOwnedBy(root.RequestId) ? root : null; }
            private static void RecoverAfterDeadline(Root root) { object pending; if (root.TestOperation.TryGetValue("pendingFinished", out pending) && pending is Dictionary<string, object>) { ResolveRunIdentity(root); return; } string[] ids; string reason; var runId = Text(root.TestOperation, "runId") ?? Text(root.TestOperation, "returnedRunId") ?? Text(root.TestOperation, "observedRunId"); if (TryReadActiveRunIds(out ids, out reason) && ids.Length == 1 && Same(ids[0], runId)) return; if (DateTimeOffset.UtcNow >= root.AbsoluteDeadlineUtc + RecoveryGrace) Fail(root, "E_EXECUTION_TIMEOUT", "TestRunner callback terminal is unavailable after recovery grace."); }
            private static void RecoverRunIdentity(Root root) { var phase = Text(root.TestOperation, "phase"); if (phase == "execute_dispatching" || phase == "run_identity_pending") ResolveRunIdentity(root); }
            private static void ResolveRunIdentity(Root root)
            {
                root = Required(); var returned = Text(root.TestOperation, "returnedRunId"); var observed = Text(root.TestOperation, "observedRunId"); if (!string.IsNullOrEmpty(returned) && !string.IsNullOrEmpty(observed) && !Same(returned, observed)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi returned GUID does not match RunStarted attribution."); return; }
                var candidate = returned ?? observed; object pending; if (root.TestOperation.TryGetValue("pendingFinished", out pending) && pending is Dictionary<string, object>) { if (string.IsNullOrEmpty(candidate) || !Same(Text(root.TestOperation, "pendingFinishedRunId"), candidate)) { Fail(root, "E_EXECUTION_TIMEOUT", "Synchronous RunFinished result has no matching durable run identity."); return; } Update(root, x => { x.TestOperation["runId"] = candidate; x.TestOperation["phase"] = "running"; }); CompletePending(Required(), (Dictionary<string, object>)pending); return; }
                string[] ids; string reason; if (!TryReadActiveRunIds(out ids, out reason) || ids.Length != 1 || string.IsNullOrEmpty(candidate) || !Same(ids[0], candidate)) { Fail(root, "E_EXECUTION_TIMEOUT", "TestRunnerApi active run identity could not be matched after Execute: " + (reason ?? "identity-mismatch") + "."); return; }
                Update(root, x => { x.TestOperation["runId"] = candidate; x.TestOperation["observedRunId"] = candidate; x.TestOperation["phase"] = "running"; });
            }
            private static void CompletePending(Root root, Dictionary<string, object> data) { var summary = data["summary"] as Dictionary<string, object>; if (summary == null) { Fail(root, "E_EXECUTION_TIMEOUT", "Pending RunFinished summary is corrupt."); return; } object artifactFailed; if (data.TryGetValue("artifactWriteFailed", out artifactFailed) && artifactFailed is bool && (bool)artifactFailed) { data.Remove("artifactWriteFailed"); Finish(root, ProtocolEnvelopeFactory.Error(root.RequestId, "E_TEST_ARTIFACT_WRITE_FAILED", "RunFinished was proven but its NUnit XML artifact could not be written.", data, new[] { "Inspect the returned test summary and verify the UnityCli data directory is writable." })); return; } var failed = Convert.ToInt32(summary["failed"], CultureInfo.InvariantCulture); var count = Convert.ToInt32(summary["testCount"], CultureInfo.InvariantCulture); object envelope = count == 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_NOT_FOUND", "The requested filters matched no Unity tests.", data, new[] { "Inspect the available test names and relax the test filters." }) : failed > 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_FAILED", "One or more Unity tests failed.", data, new[] { "Inspect failures and the optional NUnit XML artifact." }) : ProtocolEnvelopeFactory.Success(root.RequestId, data); Finish(root, envelope); }
            private static void Complete(Root root, ITestResultAdaptor result) { Finish(root, ResultEnvelope(root, result)); }
            private static object ResultEnvelope(Root root, ITestResultAdaptor result) { var data = CaptureResultData(root, result); object artifactFailed; if (data.TryGetValue("artifactWriteFailed", out artifactFailed) && artifactFailed is bool && (bool)artifactFailed) { data.Remove("artifactWriteFailed"); return ProtocolEnvelopeFactory.Error(root.RequestId, "E_TEST_ARTIFACT_WRITE_FAILED", "RunFinished was proven but its NUnit XML artifact could not be written.", data, new[] { "Inspect the returned test summary and verify the UnityCli data directory is writable." }); } var failed = result.FailCount; var count = result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount; return count == 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_NOT_FOUND", "The requested filters matched no Unity tests.", data, new[] { "Inspect the available test names and relax the test filters." }) : failed > 0 ? (object)ProtocolEnvelopeFactory.Error(root.RequestId, "E_TESTS_FAILED", "One or more Unity tests failed.", data, new[] { "Inspect failures and the optional NUnit XML artifact." }) : ProtocolEnvelopeFactory.Success(root.RequestId, data); }
            private static Dictionary<string, object> CaptureResultData(Root root, ITestResultAdaptor result) { var summary = new Dictionary<string, object> { { "platform", Text(root.TestOperation, "platform") }, { "status", result.FailCount == 0 ? "passed" : "failed" }, { "testCount", (long)(result.PassCount + result.FailCount + result.SkipCount + result.InconclusiveCount) }, { "passed", (long)result.PassCount }, { "failed", (long)result.FailCount }, { "skipped", (long)result.SkipCount }, { "inconclusive", (long)result.InconclusiveCount }, { "duration", result.Duration }, { "startedAt", result.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) }, { "finishedAt", result.EndTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) } }; var data = new Dictionary<string, object> { { "summary", summary }, { "failures", Failures(result).Cast<object>().ToList() } }; if (Bool(root.TestOperation, "xml")) { try { data["xml"] = WriteXml(root, result); } catch { data["artifactWriteFailed"] = true; } } return data; }
            private static Root Required() { var root = LoadSafely(); if (root == null) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Test active operation was not found."); return root; }
            private static Root LoadSafely() { try { return Load(); } catch { return null; } }
            private static Root Load() { var state = UnityCliActiveOperation.Load(Scope); if (state == null) return null; return Parse(state); }
            private static void Save(Root root) { UnityCliActiveOperation.Save(Scope, Data(root)); }
            private static void Update(Root root, Action<Root> change) { var current = Required(); change(current); Save(current); }
            private static void Finish(Root root, object envelope) { terminalRequestId = root.RequestId; terminalEnvelope = envelope; UnityCliActiveOperation.Clear(Scope); UnityCliSessionMutationLease.Release(root.RequestId); }
            private static void Fail(Root root, string code, string message) { Finish(root, ProtocolEnvelopeFactory.Error(root.RequestId, code, message, null, new[] { "Inspect editor.state and retry." })); }
            private static object Status(BridgeRequest request) { var input = request.DeserializeArgs<StatusArgs>(); if (input == null || !IsSafeId(input.RootRequestId)) throw new BridgeCommandException("E_SCHEMA_INVALID", "rootRequestId is required."); if (Same(terminalRequestId, input.RootRequestId) && terminalEnvelope != null) return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", terminalEnvelope } }); var root = Required(); if (!Same(root.RequestId, input.RootRequestId)) throw new BridgeCommandException("E_REQUEST_NOT_FOUND", "Test request was not found."); return ProtocolEnvelopeFactory.Success(request.RequestId, new Dictionary<string, object> { { "terminal", false }, { "accepted", true } }); }
            private static Root NewRoot(BridgeRequest request, string digest, Args args) { DateTimeOffset deadline; if (!DateTimeOffset.TryParse(request.Execution.AbsoluteDeadlineUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline)) throw new BridgeCommandException("E_SCHEMA_INVALID", "Request deadline is invalid."); return new Root { RequestId = request.RequestId, RequestDigest = digest, EditorSessionId = EditorSessionId(), AbsoluteDeadlineUtc = deadline, TestOperation = new Dictionary<string, object> { { "phase", "queued" }, { "platform", args.Platform ?? "EditMode" }, { "xml", args.Xml }, { "testNames", args.TestNames ?? new string[0] }, { "groupNames", args.GroupNames ?? new string[0] }, { "categoryNames", args.CategoryNames ?? new string[0] }, { "assemblyNames", args.AssemblyNames ?? new string[0] }, { "lease", digest }, { "filterFingerprint", FilterFingerprint(args) } } }; }
            private static Root Parse(Dictionary<string, object> state) { object id; object digest; object session; object deadline; object operation; if (!state.TryGetValue("requestId", out id) || !state.TryGetValue("requestDigest", out digest) || !state.TryGetValue("editorSessionId", out session) || !state.TryGetValue("absoluteDeadlineUtc", out deadline) || !state.TryGetValue("testOperation", out operation) || !(operation is Dictionary<string, object>)) throw new InvalidDataException("Test active state is incomplete."); DateTimeOffset parsed; if (!DateTimeOffset.TryParse(deadline as string, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)) throw new InvalidDataException("Test active deadline is invalid."); return new Root { RequestId = id as string, RequestDigest = digest as string, EditorSessionId = session as string, AbsoluteDeadlineUtc = parsed, TestOperation = (Dictionary<string, object>)operation }; }
            private static Dictionary<string, object> Data(Root root) { return new Dictionary<string, object> { { "requestId", root.RequestId }, { "requestDigest", root.RequestDigest }, { "editorSessionId", root.EditorSessionId }, { "absoluteDeadlineUtc", root.AbsoluteDeadlineUtc.ToString("O", CultureInfo.InvariantCulture) }, { "testOperation", root.TestOperation } }; }
            private static bool CanObserveCallback(Root root, params string[] phases) { return root != null && root.TestOperation != null && phases.Contains(Text(root.TestOperation, "phase")); }
            private static string BusyStart(string id) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Error(id, "E_UNITY_BUSY", "Another mutation owns the selected Unity instance.", null, new[] { "Wait for the current mutation to reach its terminal result, then retry." }, true, 250)); }
            private static string Accepted(string id) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", false }, { "accepted", true } })); }
            private static string Terminal(string id, object envelope) { return UnityCliSerializer.Serialize(ProtocolEnvelopeFactory.Success(id, new Dictionary<string, object> { { "terminal", true }, { "rootEnvelope", envelope } })); }
            private static string TestTreeHash(ITestAdaptor root) { var names = new List<string>(); CollectTestNames(root, names); names.Sort(StringComparer.Ordinal); return Sha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", names))); }
            private static void CollectTestNames(ITestAdaptor node, List<string> names) { if (node == null) return; if (node.HasChildren && node.Children != null) { foreach (var child in node.Children) CollectTestNames(child, names); return; } names.Add(node.UniqueName ?? node.FullName ?? node.Name ?? string.Empty); }
            private static bool TryReadActiveRunIds(out string[] runIds, out string reason) { runIds = new string[0]; reason = null; try { var holderType = typeof(TestRunnerApi).Assembly.GetType("UnityEditor.TestTools.TestRunner.TestRun.TestJobDataHolder", false); var property = holderType == null ? null : holderType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy); var holder = property == null ? null : property.GetValue(null, null); var field = holderType == null ? null : holderType.GetField("TestRuns", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var runs = holder == null || field == null ? null : field.GetValue(holder) as IEnumerable; if (runs == null) { reason = "active-runs-missing"; return false; } var ids = new List<string>(); foreach (var run in runs) { var type = run.GetType(); var running = type.GetField("isRunning", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); var guid = type.GetField("guid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); if (running == null || guid == null) { reason = "run-fields-missing"; return false; } if (running.GetValue(run) is bool && (bool)running.GetValue(run)) ids.Add(guid.GetValue(run) as string); } runIds = ids.Where(x => Guid.TryParse(x, out _)).Distinct(StringComparer.Ordinal).ToArray(); return true; } catch (Exception ex) { reason = ex.GetType().Name; return false; } }
            private static IEnumerable<Dictionary<string, object>> Failures(ITestResultAdaptor node) { if (node.HasChildren && node.Children != null) foreach (var child in node.Children) foreach (var failure in Failures(child)) yield return failure; else if (node.TestStatus == TestStatus.Failed) yield return new Dictionary<string, object> { { "fullName", node.FullName ?? string.Empty }, { "message", node.Message ?? string.Empty }, { "stackTrace", node.StackTrace ?? string.Empty }, { "duration", node.Duration } }; }
            private static Dictionary<string, object> WriteXml(Root root, ITestResultAdaptor result) { var path = Path.Combine(DataDir(), "test-results", InstanceId(), root.RequestId + ".xml"); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, result.ToXml().OuterXml, new UTF8Encoding(false)); var bytes = File.ReadAllBytes(path); using (var sha = SHA256.Create()) return new Dictionary<string, object> { { "path", path }, { "kind", "nunit-xml" }, { "hash", Hex(sha.ComputeHash(bytes)) }, { "size", (long)bytes.Length } }; }
            private static ExecutionSettings Settings(Dictionary<string, object> op) { return new ExecutionSettings(new Filter { testMode = Text(op, "platform") == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode, testNames = Strings(op, "testNames"), groupNames = Strings(op, "groupNames"), categoryNames = Strings(op, "categoryNames"), assemblyNames = Strings(op, "assemblyNames") }); }
            private static string Text(Dictionary<string, object> x, string key) { object value; return x != null && x.TryGetValue(key, out value) ? value as string : null; } private static bool Bool(Dictionary<string, object> x, string key) { object value; return x != null && x.TryGetValue(key, out value) && value is bool && (bool)value; } private static bool Same(string left, string right) { return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.Ordinal); } private static string[] Strings(Dictionary<string, object> x, string key) { object value; if (x == null || !x.TryGetValue(key, out value)) return new string[0]; var items = value as IEnumerable; return items == null ? new string[0] : items.Cast<object>().OfType<string>().ToArray(); }
            private static string FilterFingerprint(Args a) { return Sha256Hex(Encoding.UTF8.GetBytes(UnityCliSerializer.Serialize(new Dictionary<string, object> { { "platform", a.Platform ?? "EditMode" }, { "xml", a.Xml }, { "testNames", a.TestNames ?? new string[0] }, { "groupNames", a.GroupNames ?? new string[0] }, { "categoryNames", a.CategoryNames ?? new string[0] }, { "assemblyNames", a.AssemblyNames ?? new string[0] } }))); }
            private static void Validate(Args a) { if (a == null || string.IsNullOrWhiteSpace(a.ExpectedEditorSessionId) || (a.Platform != null && a.Platform != "EditMode" && a.Platform != "PlayMode")) throw new BridgeCommandException("E_SCHEMA_INVALID", "platform and expectedEditorSessionId are invalid."); if (!string.Equals(a.ExpectedEditorSessionId, EditorSessionId(), StringComparison.Ordinal)) throw new BridgeCommandException("E_EDITOR_SESSION_REPLACED", "The selected Editor session changed before tests began."); }
            private sealed class Args { public string Platform { get; set; } public string[] TestNames { get; set; } public string[] GroupNames { get; set; } public string[] CategoryNames { get; set; } public string[] AssemblyNames { get; set; } public bool Xml { get; set; } public string ExpectedEditorSessionId { get; set; } } private sealed class StatusArgs { public string RootRequestId { get; set; } }
        }
    }
}
#endif
