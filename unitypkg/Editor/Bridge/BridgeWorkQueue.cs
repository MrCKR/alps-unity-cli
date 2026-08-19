#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static readonly Queue<BridgeWorkItem> WorkQueue = new Queue<BridgeWorkItem>();
        private static readonly HashSet<BridgeWorkItem> RunningWork = new HashSet<BridgeWorkItem>();
        private static readonly Dictionary<string, BridgeWorkItem> ActiveRequestWork = new Dictionary<string, BridgeWorkItem>(StringComparer.Ordinal);
        private const int MaxWorkStartsPerTick = 16;

        private static void CancelQueuedWork()
        {
            List<BridgeWorkItem> pending;
            lock (QueueLock)
            {
                pending = new List<BridgeWorkItem>(WorkQueue);
                WorkQueue.Clear();
            }
            foreach (var item in pending)
            {
                item.CancelBeforeStart();
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_UNITY_DOMAIN_RELOAD",
                    "Unity reloaded before the queued request began any effect.",
                    nextActions: new[] { "Keep the same root requestId and immutable request bytes while the session supervisor rebinds." },
                    transient: true,
                    retryAfterMs: 1000));
                CompleteWorkItem(item);
            }
        }

        private static void ProcessQueuedWork()
        {
            // pulse只证明EditorApplication.update最近推进；具体请求仍由TryStart证明开始。
            Volatile.Write(ref mainThreadPulseTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Increment(ref mainThreadPulseSequence);
            cachedIsCompiling = EditorApplication.isCompiling;
            cachedIsUpdating = EditorApplication.isUpdating;
            cachedIsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            cachedIsPlaying = EditorApplication.isPlaying;
            cachedIsBuilding = BuildPipeline.isBuildingPlayer;
            cachedIsPlayModeChanging = IsPlayModeChanging();
            AdvanceBridgeStartup();
            UnityCliSessionMutationLease.TryFinalizeOwnedPlayModeRoot();
            UnityCliWaitScheduler.Tick();

            // 生命周期变化会即时登记；这里仅低频刷新，修复漏事件并更新诊断快照。
            if (started && EditorApplication.timeSinceStartup >= nextRegistryRefreshAt)
            {
                RegisterInstance("ready");
                nextRegistryRefreshAt = EditorApplication.timeSinceStartup + RegistryRefreshSeconds;
            }
            var startedThisTick = 0;
            while (startedThisTick < MaxWorkStartsPerTick)
            {
                BridgeWorkItem item;
                var queueDepth = 0;
                lock (QueueLock)
                {
                    if (WorkQueue.Count == 0) return;
                    item = WorkQueue.Peek();
                    var mutation = CoordinatedRequestIsMutation(item.Request);
                    if (mutation && RunningWork.Any(running => running.IsMutation)) return;
                    item = WorkQueue.Dequeue();
                    item.IsMutation = mutation;
                    queueDepth = WorkQueue.Count;
                }
                WriteLog("bridge_request_dequeued requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));

                if (!item.TryStart())
                {
                    WriteLog("bridge_request_skipped_expired requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    item.Response = item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        item.Request.RequestId,
                        "E_BRIDGE_REQUEST_EXPIRED",
                        "Bridge request expired before Unity started executing it.",
                        nextActions: new[] { "Retry after Unity is no longer busy." }));
                    CompleteWorkItem(item);
                    continue;
                }

                lock (QueueLock) RunningWork.Add(item);
                _ = ExecuteWorkItemAsync(item);
                startedThisTick += 1;
            }
        }

        private static async Task ExecuteWorkItemAsync(BridgeWorkItem item)
        {
            try
            {
                WriteLog("bridge_request_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                item.Response = await ExecuteCoordinatedRequest(item.Request, item.RawRequestBytes, item.TryAccept, item.Token);
                WriteLog("bridge_request_finished requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " ok=" + Bool(item.Response != null && item.Response.Contains("\"ok\":true")));
            }
            catch (BridgeCommandException exception)
            {
                item.Response = SerializeBridgeExceptionEnvelope(item.Request.RequestId, exception);
            }
            catch (OperationCanceledException)
            {
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_EXECUTION_TIMEOUT",
                    "Bridge request execution was cancelled because the immutable deadline elapsed or the Unity session ended.",
                    nextActions: new[] { "Inspect editor.state and retry only after Unity is responsive." }));
            }
            catch (Exception exception)
            {
                item.Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                    item.Request.RequestId,
                    "E_BRIDGE_COMMAND_FAILED",
                    exception.Message,
                    nextActions: new[] { "Check Unity Console and UnityCliBridge.log." }));
            }
            finally
            {
                CompleteWorkItem(item);
            }
        }

        /// <summary>同 root request 的并发 redelivery 只附着一个 work item；不同 bytes 立即 fail closed。</summary>
        private static BridgeWorkItem EnqueueOrAttach(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string admissionListenerId, long admissionGeneration, out bool created, out int queueDepth)
        {
            var digest = UnityCliRequestDigest.Compute(rawRequestBytes, request.InstanceId);
            lock (QueueLock)
            {
                BridgeWorkItem existing;
                if (ActiveRequestWork.TryGetValue(request.RequestId, out existing))
                {
                    if (!string.Equals(existing.RequestDigest, digest, StringComparison.Ordinal))
                        throw new BridgeCommandException("E_REQUEST_ID_REUSED", "requestId is already bound to different request bytes.", "Generate a new requestId before retrying this command.");
                    created = false;
                    queueDepth = WorkQueue.Count;
                    return existing;
                }
                var item = new BridgeWorkItem(request, rawRequestBytes, digest, admissionListenerId, admissionGeneration);
                ActiveRequestWork.Add(request.RequestId, item);
                WorkQueue.Enqueue(item);
                created = true;
                queueDepth = WorkQueue.Count;
                return item;
            }
        }

        /// <summary>完成信号与 active-request map 在同一锁内更新，attach 不会观察到半完成 item。</summary>
        private static void CompleteWorkItem(BridgeWorkItem item)
        {
            UnityCliTransactionLog.Append(item.Request.RequestId, item.Request.Command, Encoding.UTF8.GetString(item.RawRequestBytes), item.Response ?? UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                item.Request.RequestId,
                "E_BRIDGE_COMMAND_FAILED",
                "Bridge request completed without a response.")));
            lock (QueueLock)
            {
                RunningWork.Remove(item);
                BridgeWorkItem current;
                if (ActiveRequestWork.TryGetValue(item.Request.RequestId, out current) && object.ReferenceEquals(current, item))
                    ActiveRequestWork.Remove(item.Request.RequestId);
                item.Completed.Set();
            }
        }

        private sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;
            private bool accepted;
            private bool acceptanceCancelled;
            private readonly CancellationTokenSource cancellation;

            public BridgeWorkItem(UnityCli.Protocol.BridgeRequest request, byte[] rawRequestBytes, string requestDigest, string admissionListenerId, long admissionGeneration)
            {
                Request = request;
                RawRequestBytes = rawRequestBytes;
                RequestDigest = requestDigest;
                AdmissionListenerId = admissionListenerId;
                AdmissionGeneration = admissionGeneration;
                cancellation = CreateExecutionLifetime(request);
            }

            public UnityCli.Protocol.BridgeRequest Request { get; private set; }
            public byte[] RawRequestBytes { get; private set; }
            public string RequestDigest { get; private set; }
            public string AdmissionListenerId { get; private set; }
            public long AdmissionGeneration { get; private set; }
            public CancellationToken Token { get { return cancellation.Token; } }
            public ManualResetEventSlim Started { get; private set; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            private string response;
            public string Response
            {
                get { lock (StateLock) return response; }
                set { lock (StateLock) response = value; }
            }
            public bool TimedOut { get; private set; }
            public bool IsMutation { get; set; }

            public bool TryStart()
            {
                lock (StateLock)
                {
                    if (cancelled)
                    {
                        return false;
                    }
                    // admission与主线程开始必须属于同一listener epoch；reload后的旧队列项不得执行。
                    if (!string.Equals(AdmissionListenerId, ListenerId, StringComparison.Ordinal)
                        || AdmissionGeneration != CurrentListenerGeneration())
                    {
                        cancelled = true;
                        cancellation.Cancel();
                        response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                            Request.RequestId,
                            "E_UNITY_DOMAIN_RELOAD",
                            "Unity listener generation changed before the queued request started.",
                            nextActions: new[] { "Keep the same root requestId and immutable request bytes while the session supervisor rebinds." },
                            transient: true,
                            retryAfterMs: 1000));
                        return false;
                    }
                    started = true;
                    int remainingMs;
                    if (Request != null && Request.TryDeadlineRemainingMs(out remainingMs))
                        cancellation.CancelAfter(TimeSpan.FromMilliseconds(remainingMs));
                    else
                        cancellation.Cancel();
                    Started.Set();
                    return true;
                }
            }

            public bool CancelBeforeStart()
            {
                lock (StateLock)
                {
                    if (started)
                    {
                        return false;
                    }
                    cancelled = true;
                    cancellation.Cancel();
                    return true;
                }
            }

            /// <summary>StateLock 使接受与 HTTP 超时取消使用同一内存裁决顺序。</summary>
            public bool TryAccept()
            {
                lock (StateLock)
                {
                    if (acceptanceCancelled) return false;
                    accepted = true;
                    return true;
                }
            }

            /// <summary>HTTP timeout 仅能取消尚未被 coordinator 接受的 mutation。</summary>
            public bool CancelBeforeAcceptance()
            {
                lock (StateLock)
                {
                    if (accepted) return false;
                    acceptanceCancelled = true;
                    cancellation.Cancel();
                    return true;
                }
            }

            public void Cancel()
            {
                lock (StateLock)
                {
                    TimedOut = true;
                }
            }

        }
    }
}
#endif
