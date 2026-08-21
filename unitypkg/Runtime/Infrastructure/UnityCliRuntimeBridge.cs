using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityCli
{
    /// <summary>Development Windows Player 的loopback Bridge，负责受限 Runtime Tool 分派与session attestation。</summary>
    internal sealed class UnityCliRuntimeBridge : MonoBehaviour
    {
        private const int ProtocolVersion = 2;
        private const int RequestBodyLimitBytes = 1024 * 1024;
        private const int MaxQueueItems = 128;
        private const int MaxRequestsPerFrame = 16;
        private const int RegistryRefreshSeconds = 30;
        private static UnityCliRuntimeBridge current;
        private readonly ConcurrentQueue<PendingRequest> pending = new ConcurrentQueue<PendingRequest>();
        private readonly object requestCacheSync = new object();
        private readonly Dictionary<string, PendingRequest> requestCache = new Dictionary<string, PendingRequest>(StringComparer.Ordinal);
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private long requestOrder;
        private long mainThreadPulseSequence;
        private long mainThreadPulseTimestamp;
        private HttpListener listener;
        private Task listenerTask;
        private UnityCliRuntimeRegistry registry;
        private UnityCliRuntimeIdentity identity;
        private UnityCliAdmissionPool admissionPool;
        private string sessionId;
        private string listenerId;
        private string buildId;
        private string buildStamp;
        private int port;
        private float nextRegistryRefreshAt;
        private bool previousRunInBackground;
        private bool runInBackgroundCaptured;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void StartBeforeSceneLoad()
        {
#if UNITY_STANDALONE_WIN
            // Runtime assembly即使被Editor加载也不得启动listener或登记runtime claim。
            if (!Debug.isDebugBuild || current != null) return;
            RuntimeConfig config;
            string reason;
            if (!TryReadConfiguration(out config, out reason)) { Debug.LogWarning("UnityCli runtime bridge disabled: " + reason); return; }
            var host = new GameObject("UnityCli.RuntimeBridge");
            DontDestroyOnLoad(host);
            current = host.AddComponent<UnityCliRuntimeBridge>();
            current.Initialize(config);
#endif
        }

        /// <summary>只接受已由sidecar证明的development Windows Player身份后启动listener。</summary>
        private void Initialize(RuntimeConfig config)
        {
            try
            {
                identity = new UnityCliRuntimeIdentity { InstanceId = config.InstanceId, CanonicalProjectPath = config.CanonicalProjectPath, DisplayProjectPath = config.DisplayProjectPath };
                previousRunInBackground = Application.runInBackground;
                runInBackgroundCaptured = true;
                Application.runInBackground = true;
                buildId = config.BuildId;
                buildStamp = config.BuildStamp;
                var process = Process.GetCurrentProcess();
                sessionId = "session-" + Sha256(config.InstanceId + "\0" + config.BuildId + "\0" + process.Id.ToString(CultureInfo.InvariantCulture) + "\0" + ProcessStartedAt(process));
                listenerId = "listener-" + Guid.NewGuid().ToString("N");
                admissionPool = new UnityCliAdmissionPool(config.InstanceId, sessionId, config.TaskPoolMaxActive, config.TaskPoolReopenAt);
                int preferredPort;
                listener = StartListener(config.InstanceId, out preferredPort, out port);
                registry = new UnityCliRuntimeRegistry(identity, sessionId, listenerId, preferredPort, port, buildStamp, buildId);
                UnityCliRuntimeTools.Initialize(sessionId, buildId, config.InstanceId);
                registry.PublishReady();
                listenerTask = Task.Run(ListenLoop);
                nextRegistryRefreshAt = Time.realtimeSinceStartup + RegistryRefreshSeconds;
            }
            catch (Exception exception)
            {
                Debug.LogError("UnityCli runtime bridge failed to start: " + exception.GetType().Name);
                StopBridge();
            }
        }

        private void Update()
        {
            // Runtime health只读取该内存pulse；具体请求仍由ExecutionState取得主线程ownership。
            Interlocked.Exchange(ref mainThreadPulseTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Increment(ref mainThreadPulseSequence);
            PendingRequest request;
            UnityCliRuntimeTools.TickFrame(Time.frameCount);
            var processed = 0;
            while (processed < MaxRequestsPerFrame && pending.TryDequeue(out request)) { ExecuteOnMainThread(request); processed++; }
            // Player 生命周期稳定时只低频刷新登记；启动和退出仍即时发布或移除。
            if (registry != null && Time.realtimeSinceStartup >= nextRegistryRefreshAt)
            {
                nextRegistryRefreshAt = Time.realtimeSinceStartup + RegistryRefreshSeconds;
                try { registry.PublishReady(); } catch (Exception exception) { Debug.LogWarning("UnityCli runtime registry refresh failed: " + exception.GetType().Name); }
            }
        }

        private void OnApplicationQuit() { StopBridge(); }
        private void OnDestroy() { if (current == this) { current = null; StopBridge(); } }

        /// <summary>终止listener，完成active请求，清空session state并移除registry claim。</summary>
        private void StopBridge()
        {
            lock (requestCacheSync)
            {
                // cancellation与request入队共享同一线性化点，锁释放后不能再产生本session work。
                try { stopping.Cancel(); } catch { }
                foreach (var entry in requestCache.Values)
                    if (!entry.Completion.Task.IsCompleted) entry.Completion.TrySetResult(Error(ReadString(entry.Request, "requestId") ?? string.Empty, "E_RUNTIME_SESSION_STALE", "The development Player session ended before the request completed.", new[] { "Launch a new development Player and submit a new request." }));
                requestCache.Clear();
                if (admissionPool != null) { admissionPool.Reset(); admissionPool = null; }
            }
            try { if (listener != null) { listener.Stop(); listener.Close(); listener = null; } } catch { }
            try { UnityCliRuntimeTools.Shutdown(); } catch { }
            try { if (registry != null) { registry.Dispose(); registry = null; } } catch { }
            if (runInBackgroundCaptured) { Application.runInBackground = previousRunInBackground; runInBackgroundCaptured = false; }
        }

        private async Task ListenLoop()
        {
            while (!stopping.IsCancellationRequested && listener != null && listener.IsListening)
            {
                HttpListenerContext context = null;
                try
                {
                    context = await listener.GetContextAsync();
                    var accepted = context;
                    ThreadPool.QueueUserWorkItem(delegate { HandleHttp(accepted); });
                }
                catch (HttpListenerException) { if (!stopping.IsCancellationRequested) Debug.LogWarning("UnityCli runtime listener stopped unexpectedly."); break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception exception) { Debug.LogWarning("UnityCli runtime listener error: " + exception.GetType().Name); }
            }
        }

        /// <summary>校验loopback请求、attestation和exact replay，再把首次请求提交主线程。</summary>
        private void HandleHttp(HttpListenerContext context)
        {
            var requestId = string.Empty;
            try
            {
                if (string.Equals(context.Request.HttpMethod, "GET", StringComparison.Ordinal) && string.Equals(context.Request.Url.AbsolutePath, "/health", StringComparison.Ordinal))
                {
                    string healthAttestationError;
                    if (!ValidateAttestationHeaders(context.Request, false, out healthAttestationError)) { Write(context, Error(string.Empty, "E_INSTANCE_IDENTITY_MISMATCH", healthAttestationError, new[] { "Resolve the runtime target again through --instance." })); return; }
                    Write(context, Success(string.Empty, HealthData())); return;
                }
                if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal)
                    && (string.Equals(context.Request.Url.AbsolutePath, "/bridge/admission/acquire", StringComparison.Ordinal)
                        || string.Equals(context.Request.Url.AbsolutePath, "/bridge/admission/revoke", StringComparison.Ordinal)))
                {
                    string admissionAttestationError;
                    if (!ValidateAttestationHeaders(context.Request, true, out admissionAttestationError))
                    {
                        Write(context, Error(string.Empty, "E_INSTANCE_IDENTITY_MISMATCH", admissionAttestationError, new[] { "Resolve the runtime target again through --instance." }));
                        return;
                    }
                    HandleAdmission(context, context.Request.Url.AbsolutePath);
                    return;
                }
                if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.Ordinal) || !string.Equals(context.Request.Url.AbsolutePath, "/bridge/execute", StringComparison.Ordinal))
                {
                    Write(context, Error(requestId, "E_BRIDGE_ROUTE_NOT_FOUND", "Runtime bridge route was not found.", new[] { "Use GET /health or POST /bridge/execute." })); return;
                }
                var body = ReadBody(context.Request);
                var envelope = UnityCliRuntimeJson.Object(UnityCliRuntimeJson.Parse(body), "Bridge request must be a JSON object.");
                requestId = RequiredString(envelope, "requestId", 128);
                string attestationError;
                if (!ValidateAttestationHeaders(context.Request, true, out attestationError))
                {
                    Write(context, Error(requestId, "E_INSTANCE_IDENTITY_MISMATCH", attestationError, new[] { "Resolve the runtime target again through --instance." }));
                    return;
                }
                var digest = Sha256(body);
                PendingRequest work = null;
                string immediate = null;
                var enqueue = false;
                // 同字节replay可读取已验证请求的terminal；只有首次request才要求deadline仍在未来。
                lock (requestCacheSync)
                {
                    if (stopping.IsCancellationRequested)
                    {
                        immediate = Error(requestId, "E_RUNTIME_SESSION_STALE", "The development Player session is stopping.", new[] { "Launch a new development Player and submit a new request." });
                    }
                    else if (requestCache.TryGetValue(requestId, out work))
                    {
                        if (!string.Equals(work.RequestDigest, digest, StringComparison.Ordinal))
                            immediate = Error(requestId, "E_REQUEST_REPLAY_MISMATCH", "requestId was reused with different immutable request bytes.", new[] { "Submit a new requestId for different Runtime Tool input." });
                    }
                    else
                    {
                        var deadline = ValidateRequest(envelope);
                        UnityCliAdmissionPool.ConsumedIdentity admissionOwner;
                        var admissionError = AuthorizeRuntimeAdmission(context.Request, envelope, out admissionOwner);
                        if (admissionError != null) immediate = admissionError;
                        else if (!ReserveRequestCacheSlot())
                            immediate = Error(requestId, "E_RUNTIME_QUEUE_FULL", "Runtime request cache is full of active work.", new[] { "Wait for active Player requests to complete and retry." }, true, 100);
                        else if (pending.Count >= MaxQueueItems)
                            immediate = Error(requestId, "E_RUNTIME_QUEUE_FULL", "Runtime bridge queue is full.", new[] { "Wait for the Player to process pending requests and retry." }, true, 100);
                        else
                        {
                            work = new PendingRequest { Request = envelope, Completion = new TaskCompletionSource<string>(), AbsoluteDeadlineMs = deadline, RequestDigest = digest, Order = ++requestOrder, AdmissionOwner = admissionOwner };
                            requestCache.Add(requestId, work);
                            enqueue = true;
                        }
                    }
                    if (immediate == null && enqueue) pending.Enqueue(work);
                }
                if (immediate != null) { Write(context, immediate); return; }
                while (!work.Completion.Task.IsCompleted)
                {
                    var remaining = work.AbsoluteDeadlineMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (remaining > 0L)
                    {
                        if (work.Completion.Task.Wait((int)Math.Min(remaining, int.MaxValue))) break;
                        continue;
                    }
                    var timeout = Error(requestId, "E_EXECUTION_TIMEOUT", "Runtime request did not reach the effect barrier before its immutable deadline.", new[] { "Inspect runtime.state before submitting a new request." }, true, 100);
                    // deadline与main thread只允许一个owner；effect已开始时必须等待同一terminal，不能伪报timeout。
                    if (Interlocked.CompareExchange(ref work.ExecutionState, 2, 0) == 0)
                    {
                        work.Completion.TrySetResult(timeout);
                        if (work.AdmissionOwner != null) CompleteRuntimeAdmissionRoot(work.AdmissionOwner);
                    }
                    else work.Completion.Task.Wait();
                }
                Write(context, work.Completion.Task.Result);
            }
            catch (FormatException exception) { Write(context, Error(requestId, "E_SCHEMA_INVALID", exception.Message, new[] { "Send protocolVersion 2 with a complete Runtime bridge request." })); }
            catch (Exception exception) { Write(context, Error(requestId, "E_RUNTIME_BRIDGE_FAILED", "Runtime bridge could not process the request.", new[] { "Inspect Player logs and retry after the runtime is ready." })); Debug.LogWarning("UnityCli runtime request failed: " + exception.GetType().Name); }
        }

        /// <summary>admission route只操作有界内存，不进入Player主线程队列。</summary>
        private void HandleAdmission(HttpListenerContext context, string route)
        {
            var requestId = string.Empty;
            try
            {
                var value = UnityCliRuntimeJson.Object(UnityCliRuntimeJson.Parse(ReadBody(context.Request)), "Admission body must be a JSON object.");
                requestId = RequiredString(value, "requestId", 200);
                if (string.Equals(route, "/bridge/admission/acquire", StringComparison.Ordinal))
                {
                    if (!HasOnlyKeys(value, "protocolVersion", "requestId", "instanceId", "target", "command", "rootFingerprint")
                        || ReadLong(value, "protocolVersion") != 1L || RequiredString(value, "instanceId", 128) != identity.InstanceId
                        || RequiredString(value, "target", 16) != "runtime") throw new FormatException("Runtime admission acquire identity is invalid.");
                    RequiredString(value, "command", 256);
                    var acquireFingerprint = RequiredString(value, "rootFingerprint", 64);
                    var result = admissionPool.Acquire(requestId, acquireFingerprint, listenerId, 1L);
                    if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.Invalid) throw new FormatException("Runtime admission acquire fields are invalid.");
                    if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.IdentityMismatch)
                    {
                        Write(context, Error(requestId, "E_REQUEST_ID_REUSED", "Admission requestId is already bound to different business input.", new[] { "Generate a new requestId for different Runtime Tool input." }));
                        return;
                    }
                    if (result.Outcome == UnityCliAdmissionPool.AcquireOutcome.AlreadyConsumed)
                    {
                        Write(context, Error(requestId, "E_ADMISSION_ALREADY_CONSUMED", "Admission root is already consumed and must use exact attach or terminal recovery.", new[] { "Inspect the exact Runtime root before retrying." }));
                        return;
                    }
                    var granted = result.Outcome == UnityCliAdmissionPool.AcquireOutcome.Granted;
                    if (granted)
                    {
                        context.Response.Headers["x-unity-cli-admission-token"] = result.Token;
                        Debug.Log("UnityCli " + (result.NewlyGranted ? "admission_granted" : "admission_attached") + " requestId=" + requestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
                    }
                    Write(context, Success(requestId, new Dictionary<string, object>
                    {
                        { "protocolVersion", 1L }, { "granted", granted },
                        { "grantExpiresInMs", (long)(granted ? result.GrantExpiresInMs : 0) },
                        { "retryAfterMs", (long)result.RetryAfterMs }
                    }));
                    return;
                }

                if (!HasOnlyKeys(value, "protocolVersion", "requestId", "instanceId", "target", "rootFingerprint")
                    || ReadLong(value, "protocolVersion") != 1L || RequiredString(value, "instanceId", 128) != identity.InstanceId
                    || RequiredString(value, "target", 16) != "runtime") throw new FormatException("Runtime admission release identity is invalid.");
                var fingerprint = RequiredString(value, "rootFingerprint", 64);
                if (!string.Equals(context.Request.Headers["x-unity-cli-admission-root-id"], requestId, StringComparison.Ordinal)
                    || !string.Equals(context.Request.Headers["x-unity-cli-admission-fingerprint"], fingerprint, StringComparison.Ordinal))
                    throw new FormatException("Runtime admission release headers do not match the body identity.");
                var token = context.Request.Headers["x-unity-cli-admission-token"];
                if (string.IsNullOrEmpty(token) || token.Length > 256) throw new FormatException("Runtime admission token header is missing or invalid.");
                if (!string.Equals(route, "/bridge/admission/revoke", StringComparison.Ordinal)) throw new FormatException("Runtime admission release route is invalid.");
                var revoked = admissionPool.Revoke(requestId, fingerprint, token);
                if (revoked) Debug.Log("UnityCli admission_revoked requestId=" + requestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
                if (!revoked) { Write(context, Error(requestId, "E_ADMISSION_IDENTITY_MISMATCH", "Admission revoke identity does not match an unconsumed Runtime root.", new[] { "Inspect the selected Runtime session before retrying." })); return; }
                Write(context, Success(requestId, new Dictionary<string, object> { { "revoked", true } }));
            }
            catch (FormatException exception) { Write(context, Error(requestId, "E_SCHEMA_INVALID", exception.Message, new[] { "Use the internal admission protocol schema." })); }
            catch (Exception exception) { Write(context, Error(requestId, "E_RUNTIME_BRIDGE_FAILED", "Runtime admission failed: " + exception.GetType().Name + ".", new[] { "Inspect the Player session before retrying." })); }
        }

        /// <summary>新业务request必须消费同session grant；同字节requestCache replay不再次授权。</summary>
        private string AuthorizeRuntimeAdmission(HttpListenerRequest httpRequest, Dictionary<string, object> request, out UnityCliAdmissionPool.ConsumedIdentity admissionOwner)
        {
            admissionOwner = null;
            var rootId = httpRequest.Headers["x-unity-cli-admission-root-id"];
            var fingerprint = httpRequest.Headers["x-unity-cli-admission-fingerprint"];
            var token = httpRequest.Headers["x-unity-cli-admission-token"];
            var deadlineText = httpRequest.Headers["x-unity-cli-admission-deadline-utc"];
            var any = !string.IsNullOrEmpty(rootId) || !string.IsNullOrEmpty(fingerprint) || !string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(deadlineText);
            var requestId = ReadString(request, "requestId") ?? string.Empty;
            var command = ReadString(request, "command");
            if (!any)
            {
                if (string.Equals(command, "runtime-tool.list", StringComparison.Ordinal)) { rootId = null; return null; }
                return Error(requestId, "E_ADMISSION_REQUIRED", "A valid Unity task-pool admission is required before this Runtime business request can enter the queue.", new[] { "Use the current unity_cli to submit the Runtime Tool." });
            }
            if (string.IsNullOrEmpty(rootId) || string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deadlineText))
                return Error(requestId, "E_ADMISSION_IDENTITY_MISMATCH", "Runtime admission headers are incomplete.", new[] { "Resolve the Runtime target again before retrying." });
            if (!string.Equals(rootId, requestId, StringComparison.Ordinal))
                return Error(requestId, "E_ADMISSION_IDENTITY_MISMATCH", "Runtime admission root requestId does not match the Bridge request body.", new[] { "Submit the request through the current unity_cli." });
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline))
                return Error(requestId, "E_ADMISSION_IDENTITY_MISMATCH", "Runtime admission deadline is invalid.", new[] { "Submit the request through the current unity_cli." });
            var outcome = admissionPool.Authorize(rootId, fingerprint, token, listenerId, 1L, deadline.ToUnixTimeMilliseconds());
            if (outcome != UnityCliAdmissionPool.AuthorizeOutcome.Authorized)
                return Error(requestId, "E_ADMISSION_IDENTITY_MISMATCH", "Runtime admission token is missing, expired, or bound to another root.", new[] { "Wait for a fresh Runtime admission before submitting business work." });
            if (!admissionPool.TryGetConsumedIdentity(rootId, out admissionOwner))
                return Error(requestId, "E_EXECUTION_TIMEOUT", "Runtime admission owner identity could not be frozen after consumption.", new[] { "Inspect the Runtime session before submitting another root." });
            return null;
        }

        /// <summary>所有Unity API只在Update主线程处理；descriptor由TS catalog唯一持有，Player只返回空执行器列表。</summary>
        private void ExecuteOnMainThread(PendingRequest pendingRequest)
        {
            // 只有pending request可取得effect ownership；deadline或其他waiter已终结时不得执行。
            if (Interlocked.CompareExchange(ref pendingRequest.ExecutionState, 1, 0) != 0) return;
            var request = pendingRequest.Request;
            var requestId = ReadString(request, "requestId") ?? string.Empty;
            try
            {
                var deadline = pendingRequest.AbsoluteDeadlineMs;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline) { pendingRequest.Completion.TrySetResult(Error(requestId, "E_EXECUTION_TIMEOUT", "Runtime request deadline elapsed before execution.", new[] { "Retry only after the Player is responsive." }, true, 100)); return; }
                var command = RequiredString(request, "command", 128);
                if (string.Equals(command, "runtime-tool.list", StringComparison.Ordinal))
                {
                    pendingRequest.Completion.TrySetResult(Success(requestId, new Dictionary<string, object> { { "tools", new List<object>() }, { "target", "runtime" }, { "runtimeSessionId", sessionId } })); return;
                }
                if (string.Equals(command, "runtime-tool.run", StringComparison.Ordinal))
                {
                    var wrapper = ReadObject(request, "input");
                    if (wrapper == null || !HasOnlyKeys(wrapper, "name", "input", "artifactDir", "playModeSessionId")) throw new FormatException("runtime-tool.run wrapper is invalid.");
                    var name = RequiredString(wrapper, "name", 128);
                    var toolInput = ReadObject(wrapper, "input");
                    if (toolInput == null) throw new FormatException("runtime-tool.run input must be an object.");
                    var execution = ReadObject(request, "execution");
                    object dryRunValue;
                    var dryRun = execution != null && execution.TryGetValue("dryRun", out dryRunValue) && dryRunValue is bool && (bool)dryRunValue;
                    var result = UnityCliRuntimeTools.Execute(name, toolInput, dryRun);
                    pendingRequest.Completion.TrySetResult(Success(requestId, new Dictionary<string, object> { { "result", result } }));
                    return;
                }
                pendingRequest.Completion.TrySetResult(Error(requestId, "E_TOOL_NOT_FOUND", "Runtime bridge command is not approved.", new[] { "Use the public Tool catalog to select a Runtime Tool." }));
            }
            catch (UnityCliRuntimeToolException exception) { pendingRequest.Completion.TrySetResult(Error(requestId, exception.Code, exception.Message, new[] { "Run tools describe for the selected Runtime Tool and inspect the current Player session." })); }
            catch (FormatException exception) { pendingRequest.Completion.TrySetResult(Error(requestId, "E_SCHEMA_INVALID", exception.Message, new[] { "Use the Runtime bridge request schema." })); }
            catch (Exception exception) { pendingRequest.Completion.TrySetResult(Error(requestId, "E_RUNTIME_BRIDGE_FAILED", "Runtime command failed before a terminal result was proven.", new[] { "Inspect Player logs and retry after the runtime is ready." })); Debug.LogWarning("UnityCli runtime main-thread command failed: " + exception.GetType().Name); }
            finally { if (pendingRequest.AdmissionOwner != null) CompleteRuntimeAdmissionRoot(pendingRequest.AdmissionOwner); }
        }

        private void CompleteRuntimeAdmissionRoot(UnityCliAdmissionPool.ConsumedIdentity identity)
        {
            if (admissionPool != null && identity != null
                && admissionPool.CompleteTrusted(identity.RequestId, identity.RootFingerprint, identity.Token, identity.SessionId, identity.OwnerKind))
                Debug.Log("UnityCli admission_terminal requestId=" + identity.RequestId + " active=" + admissionPool.Capture().Active.ToString(CultureInfo.InvariantCulture));
        }

        private Dictionary<string, object> HealthData()
        {
            long before;
            long after;
            long timestamp;
            do
            {
                before = Interlocked.Read(ref mainThreadPulseSequence);
                timestamp = Interlocked.Read(ref mainThreadPulseTimestamp);
                after = Interlocked.Read(ref mainThreadPulseSequence);
            }
            while (before != after);
            object ageMs = null;
            if (timestamp > 0)
            {
                var elapsed = Math.Max(0L, System.Diagnostics.Stopwatch.GetTimestamp() - timestamp);
                ageMs = (long)(elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
            return new Dictionary<string, object>
            {
                { "version", 3L }, { "target", "runtime" }, { "instanceId", identity.InstanceId },
                { "canonicalProjectPath", identity.CanonicalProjectPath }, { "canonicalizationVersion", 1L },
                { "sessionId", sessionId }, { "runtimeSessionId", sessionId }, { "listenerId", listenerId },
                { "listenerGeneration", 1L }, { "actualPort", (long)port }, { "buildStamp", buildStamp }, { "buildId", buildId },
                { "mainThread", new Dictionary<string, object>
                    {
                        { "observed", timestamp > 0 }, { "sequence", after }, { "ageMs", ageMs },
                        { "listenerGeneration", 1L }, { "lifecycle", "ready" }
                    }
                }
            };
        }

        private void Write(HttpListenerContext context, string envelope)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(envelope);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers["x-unity-cli-target"] = "runtime";
                context.Response.Headers["x-unity-cli-instance-id"] = identity == null ? string.Empty : identity.InstanceId;
                context.Response.Headers["x-unity-cli-session-id"] = sessionId ?? string.Empty;
                context.Response.Headers["x-unity-cli-listener-id"] = listenerId ?? string.Empty;
                context.Response.Headers["x-unity-cli-listener-generation"] = "1";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { }
            finally { try { context.Response.Close(); } catch { } }
        }

        /// <summary>严格校验BridgeRequest v2，并把嵌套execution deadline冻结为Unix毫秒。</summary>
        private static long ValidateRequest(Dictionary<string, object> request)
        {
            if (!HasOnlyKeys(request, "protocolVersion", "instanceId", "command", "requestId", "input", "execution")) throw new FormatException("Runtime request contains an unknown field.");
            if (ReadLong(request, "protocolVersion") != ProtocolVersion) throw new FormatException("Runtime request protocolVersion must be 2.");
            if (!string.Equals(RequiredString(request, "instanceId", 128), current.identity.InstanceId, StringComparison.Ordinal)) throw new FormatException("Runtime request instanceId does not match the Player identity.");
            RequiredString(request, "command", 128);
            RequiredString(request, "requestId", 128);
            if (!(request.ContainsKey("input") && request["input"] is Dictionary<string, object>)) throw new FormatException("Runtime request input must be a JSON object.");
            var execution = ReadObject(request, "execution");
            if (execution == null || !HasOnlyKeys(execution, "absoluteDeadlineUtc", "completionContractVersion", "requestDigestVersion", "dryRun")
                || ReadLong(execution, "completionContractVersion") != 1L || ReadLong(execution, "requestDigestVersion") != 1L) throw new FormatException("Runtime request execution contract is invalid.");
            object dryRun;
            if (execution.TryGetValue("dryRun", out dryRun) && !(dryRun is bool)) throw new FormatException("Runtime request execution.dryRun must be a boolean.");
            var deadlineText = RequiredString(execution, "absoluteDeadlineUtc", 128);
            DateTimeOffset deadline;
            if (!DateTimeOffset.TryParse(deadlineText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out deadline)) throw new FormatException("Runtime request execution.absoluteDeadlineUtc is invalid.");
            var deadlineMs = deadline.ToUnixTimeMilliseconds();
            if (deadlineMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) throw new FormatException("Runtime request execution.absoluteDeadlineUtc must be in the future.");
            return deadlineMs;
        }

        /// <summary>health与execute都必须绑定同一target/session；execute额外冻结listener generation。</summary>
        private bool ValidateAttestationHeaders(HttpListenerRequest request, bool requireListener, out string error)
        {
            error = null;
            if (!string.Equals(request.Headers["x-unity-cli-expected-target"], "runtime", StringComparison.Ordinal)
                || !string.Equals(request.Headers["x-unity-cli-expected-instance-id"], identity.InstanceId, StringComparison.Ordinal)
                || !string.Equals(request.Headers["x-unity-cli-expected-session-id"], sessionId, StringComparison.Ordinal))
            {
                error = "Runtime target, instance, or session attestation does not match the active Player.";
                return false;
            }
            if (requireListener && (!string.Equals(request.Headers["x-unity-cli-expected-listener-id"], listenerId, StringComparison.Ordinal)
                || !string.Equals(request.Headers["x-unity-cli-expected-listener-generation"], "1", StringComparison.Ordinal)))
            {
                error = "Runtime listener attestation does not match the active Player generation.";
                return false;
            }
            return true;
        }

        /// <summary>从固定sidecar读取并验证build-bound Player identity；任何未知字段均fail closed。</summary>
        private static bool TryReadConfiguration(out RuntimeConfig config, out string reason)
        {
            config = null; reason = null;
            try
            {
                var path = Path.Combine(Application.dataPath, "UnityCli", "runtime-config.json");
                if (!File.Exists(path)) { reason = "runtime sidecar is missing"; return false; }
                var value = UnityCliRuntimeJson.Object(UnityCliRuntimeJson.Parse(File.ReadAllText(path, Encoding.UTF8)), "Runtime sidecar must be a JSON object.");
                if (!HasOnlyKeys(value, "version", "canonicalizationVersion", "instanceId", "canonicalProjectPath", "displayProjectPath", "buildId", "buildGuid", "buildStamp", "taskPoolMaxActive", "taskPoolReopenAt", "unityVersion", "developmentBuild", "generatedAt")) { reason = "runtime sidecar contains an unknown field"; return false; }
                if (ReadLong(value, "version") != 2L || ReadLong(value, "canonicalizationVersion") != 1L || !ReadBool(value, "developmentBuild")) { reason = "runtime sidecar version or development marker is invalid"; return false; }
                var canonical = RequiredString(value, "canonicalProjectPath", 4096);
                var instance = RequiredString(value, "instanceId", 128);
                RequiredString(value, "unityVersion", 128);
                RequiredString(value, "generatedAt", 128);
                if (!string.Equals(instance, "unity-" + Sha256(canonical), StringComparison.Ordinal)) { reason = "runtime sidecar instance identity is invalid"; return false; }
                if (!Path.IsPathRooted(canonical)) { reason = "runtime sidecar canonical project path is not absolute"; return false; }
                var buildId = RequiredString(value, "buildId", 32);
                if (!IsHex(buildId, 32)) { reason = "runtime sidecar buildId is invalid"; return false; }
                var runtimeBuildGuid = RuntimeBuildGuid();
                if (!string.IsNullOrEmpty(runtimeBuildGuid) && !string.Equals(buildId, runtimeBuildGuid, StringComparison.Ordinal)) { reason = "runtime sidecar buildId does not match the Player build"; return false; }
                var displayPath = RequiredString(value, "displayProjectPath", 4096);
                if (!Path.IsPathRooted(displayPath)) { reason = "runtime sidecar display project path is not absolute"; return false; }
                var sidecarBuildGuid = RequiredString(value, "buildGuid", 32);
                if (!string.Equals(sidecarBuildGuid, buildId, StringComparison.Ordinal)) { reason = "runtime sidecar build GUID is invalid"; return false; }
                if (!string.Equals(RequiredString(value, "unityVersion", 128), Application.unityVersion, StringComparison.Ordinal)) { reason = "runtime sidecar Unity version does not match the Player"; return false; }
                DateTimeOffset generatedAt;
                if (!DateTimeOffset.TryParse(RequiredString(value, "generatedAt", 128), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out generatedAt)) { reason = "runtime sidecar generatedAt is invalid"; return false; }
                var maxActive = checked((int)ReadLong(value, "taskPoolMaxActive"));
                var reopenAt = checked((int)ReadLong(value, "taskPoolReopenAt"));
                if (maxActive < 2 || reopenAt < 1 || reopenAt >= maxActive) { reason = "runtime sidecar task-pool configuration is invalid"; return false; }
                config = new RuntimeConfig { InstanceId = instance, CanonicalProjectPath = canonical, DisplayProjectPath = displayPath, BuildId = buildId, BuildStamp = RequiredString(value, "buildStamp", 256), TaskPoolMaxActive = maxActive, TaskPoolReopenAt = reopenAt };
                return true;
            }
            catch (Exception exception) { reason = exception.Message; return false; }
        }

        /// <summary>每个候选端口使用新的listener；失败对象绝不进入下一次Start。</summary>
        private static HttpListener StartListener(string instanceId, out int preferredPort, out int actualPort)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(instanceId));
                var basePort = 49152 + ((hash[0] << 8 | hash[1]) % 12000);
                preferredPort = basePort;
                for (var offset = 0; offset < 64; offset++)
                {
                    var candidate = basePort + offset;
                    var candidateListener = new HttpListener();
                    try
                    {
                        candidateListener.Prefixes.Add("http://127.0.0.1:" + candidate.ToString(CultureInfo.InvariantCulture) + "/");
                        candidateListener.Start();
                        actualPort = candidate;
                        return candidateListener;
                    }
                    catch (Exception exception) when (exception is HttpListenerException || exception is SocketException)
                    {
                        try { candidateListener.Close(); } catch { }
                    }
                }
            }
            preferredPort = 0;
            actualPort = 0;
            throw new InvalidOperationException("No loopback runtime listener port is available.");
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (request.ContentLength64 > RequestBodyLimitBytes) throw new FormatException("Runtime request body exceeds 1 MiB.");
            using (var stream = request.InputStream)
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[8192]; int read;
                while ((read = stream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    if (buffer.Length + read > RequestBodyLimitBytes) throw new FormatException("Runtime request body exceeds 1 MiB.");
                    buffer.Write(bytes, 0, read);
                }
                var result = buffer.ToArray();
                if (result.Length >= 3 && result[0] == 0xef && result[1] == 0xbb && result[2] == 0xbf) throw new FormatException("Runtime request must not contain a UTF-8 BOM.");
                return new UTF8Encoding(false, true).GetString(result);
            }
        }

        private static string Success(string requestId, object data) { return UnityCliRuntimeJson.Serialize(new Dictionary<string, object> { { "ok", true }, { "requestId", requestId ?? string.Empty }, { "data", data } }); }
        private static string Error(string requestId, string code, string message, string[] nextActions, bool transient = false, int retryAfterMs = 0) { return UnityCliRuntimeJson.Serialize(new Dictionary<string, object> { { "ok", false }, { "requestId", requestId ?? string.Empty }, { "error", new Dictionary<string, object> { { "code", code }, { "message", message }, { "details", new Dictionary<string, object>() }, { "nextActions", new List<object>(nextActions ?? new string[0]) }, { "transient", transient }, { "retryAfterMs", (long)retryAfterMs } } } }); }
        private static string RequiredString(Dictionary<string, object> value, string key, int maximum) { var result = ReadString(value, key); if (string.IsNullOrEmpty(result) || result.Length > maximum) throw new FormatException("Runtime request field " + key + " is missing or invalid."); return result; }
        private static string ReadString(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result as string : null; }
        private static long ReadLong(Dictionary<string, object> value, string key) { object result; if (value == null || !value.TryGetValue(key, out result)) return 0L; if (result is long) return (long)result; if (result is double) { var number = (double)result; return !double.IsNaN(number) && !double.IsInfinity(number) && Math.Floor(number) == number && number >= long.MinValue && number <= long.MaxValue ? (long)number : 0L; } return 0L; }
        private static bool ReadBool(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) && result is bool && (bool)result; }
        private static Dictionary<string, object> ReadObject(Dictionary<string, object> value, string key) { object result; return value != null && value.TryGetValue(key, out result) ? result as Dictionary<string, object> : null; }
        private static bool IsHex(string value, int length) { if (string.IsNullOrEmpty(value) || value.Length != length) return false; foreach (var item in value) if (!Uri.IsHexDigit(item)) return false; return true; }
        private static bool HasOnlyKeys(Dictionary<string, object> value, params string[] allowed) { if (value == null) return false; var names = new HashSet<string>(allowed, StringComparer.Ordinal); foreach (var key in value.Keys) if (!names.Contains(key)) return false; return true; }
        /// <summary>缓存固定为128项；只淘汰最旧terminal，进行中的mutation不会失去去重身份。</summary>
        private bool ReserveRequestCacheSlot() { if (requestCache.Count < MaxQueueItems) return true; PendingRequest oldest = null; string oldestId = null; foreach (var pair in requestCache) if (pair.Value.Completion.Task.IsCompleted && (oldest == null || pair.Value.Order < oldest.Order)) { oldest = pair.Value; oldestId = pair.Key; } if (oldestId == null) return false; requestCache.Remove(oldestId); return true; }
        private static string Sha256(string value) { using (var sha = SHA256.Create()) { var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value)); var result = new StringBuilder(bytes.Length * 2); foreach (var item in bytes) result.Append(item.ToString("x2")); return result.ToString(); } }
        private static string ProcessStartedAt(Process process) { return process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
        private static string RuntimeBuildGuid() { var property = typeof(Application).GetProperty("buildGUID", BindingFlags.Static | BindingFlags.Public); return property == null ? string.Empty : property.GetValue(null, null) as string ?? string.Empty; }

        private sealed class PendingRequest { internal Dictionary<string, object> Request; internal TaskCompletionSource<string> Completion; internal long AbsoluteDeadlineMs; internal string RequestDigest; internal UnityCliAdmissionPool.ConsumedIdentity AdmissionOwner; internal long Order; internal int ExecutionState; }
        private sealed class RuntimeConfig { internal string InstanceId; internal string CanonicalProjectPath; internal string DisplayProjectPath; internal string BuildId; internal string BuildStamp; internal int TaskPoolMaxActive; internal int TaskPoolReopenAt; }
    }
}
