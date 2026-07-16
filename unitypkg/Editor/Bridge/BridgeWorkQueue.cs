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
                    "E_EXECUTION_CANCELLED",
                    "Bridge request was cancelled because Unity is reloading or quitting.",
                    nextActions: new[] { "Restart or wait for Unity, then retry the request." },
                    transient: true));
                item.Completed.Set();
            }
        }

        private static void ProcessQueuedWork()
        {
            // Cache Unity main-thread-only API values so HandleHttpCore can read them
            // safely from a background thread pool thread.
            cachedIsCompiling = EditorApplication.isCompiling;
            cachedIsUpdating = EditorApplication.isUpdating;
            cachedIsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            cachedIsPlaying = EditorApplication.isPlaying;
            cachedIsBuilding = BuildPipeline.isBuildingPlayer;
            UnityCliWaitScheduler.EnsureMainThreadSynchronizationContext();
            UnityCliWaitScheduler.Tick();

            if (started && EditorApplication.timeSinceStartup >= nextHeartbeatAt)
            {
                RegisterInstance("healthy");
                nextHeartbeatAt = EditorApplication.timeSinceStartup + 2.0;
            }
            var startedThisTick = 0;
            while (startedThisTick < MaxWorkStartsPerTick)
            {
                BridgeWorkItem item;
                var queueDepth = 0;
                lock (QueueLock)
                {
                    if (WorkQueue.Count == 0) return;
                    item = WorkQueue.Dequeue();
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
                    item.Completed.Set();
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
                item.Response = await Execute(item.Request, item.Token);
                item.Token.ThrowIfCancellationRequested();
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
                    item.ClientDisconnected ? "E_CLIENT_DISCONNECTED" : "E_EXECUTION_TIMEOUT",
                    item.ClientDisconnected ? "Bridge request was cancelled because the HTTP client disconnected." : "Bridge request execution was cancelled or timed out.",
                    nextActions: new[] { item.ClientDisconnected ? "Reconnect and submit a new request; the cancelled request cannot be resumed." : "Retry with a larger execution.timeoutMs after Unity is responsive." },
                    transient: true));
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
                lock (QueueLock) RunningWork.Remove(item);
                item.Completed.Set();
            }
        }

        private sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;
            private readonly CancellationTokenSource cancellation;

            public BridgeWorkItem(UnityCli.Protocol.BridgeRequest request, CancellationToken clientAbort = default)
            {
                Request = request;
                cancellation = CreateExecutionLifetime(request, clientAbort);
            }

            public UnityCli.Protocol.BridgeRequest Request { get; private set; }
            public CancellationToken Token { get { return cancellation.Token; } }
            public ManualResetEventSlim Started { get; private set; } = new ManualResetEventSlim(false);
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            public string Response { get; set; }
            public bool TimedOut { get; private set; }
            public bool ClientDisconnected { get; private set; }

            public bool TryStart()
            {
                lock (StateLock)
                {
                    if (cancelled)
                    {
                        return false;
                    }
                    started = true;
                    var timeoutMs = Request == null || Request.Execution == null ? null : Request.Execution.TimeoutMs;
                    if (timeoutMs.HasValue && timeoutMs.Value > 0) cancellation.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(timeoutMs.Value, int.MaxValue)));
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

            public void Cancel()
            {
                TimedOut = true;
                cancellation.Cancel();
            }

            public void CancelClientDisconnected()
            {
                var completedBeforeStart = false;
                lock (StateLock)
                {
                    if (ClientDisconnected) return;
                    ClientDisconnected = true;
                    if (!started)
                    {
                        cancelled = true;
                        completedBeforeStart = true;
                    }
                }
                cancellation.Cancel();
                if (completedBeforeStart)
                {
                    Response = UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(
                        Request.RequestId,
                        "E_CLIENT_DISCONNECTED",
                        "Bridge request was cancelled before execution because the HTTP client disconnected.",
                        nextActions: new[] { "Reconnect and submit a new request; no Unity side effects were applied." },
                        transient: true));
                    Completed.Set();
                }
            }

        }
    }
}
#endif
