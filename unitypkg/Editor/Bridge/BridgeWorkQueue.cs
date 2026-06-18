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

        private static void ProcessQueuedWork()
        {
            // Cache Unity main-thread-only API values so HandleHttpCore can read them
            // safely from a background thread pool thread.
            cachedIsCompiling = EditorApplication.isCompiling;
            cachedIsUpdating = EditorApplication.isUpdating;
            cachedIsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode;
            cachedIsPlaying = EditorApplication.isPlaying;
            cachedIsBuilding = BuildPipeline.isBuildingPlayer;

            if (started && EditorApplication.timeSinceStartup >= nextHeartbeatAt)
            {
                RegisterInstance("healthy");
                nextHeartbeatAt = EditorApplication.timeSinceStartup + 2.0;
            }
            while (true)
            {
                BridgeWorkItem item;
                var queueDepth = 0;
                lock (QueueLock)
                {
                    if (WorkQueue.Count == 0)
                    {
                        return;
                    }
                    item = WorkQueue.Dequeue();
                    queueDepth = WorkQueue.Count;
                }
                WriteLog("bridge_request_dequeued requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " queueDepth=" + queueDepth.ToString(CultureInfo.InvariantCulture));

                if (!item.TryStart())
                {
                    WriteLog("bridge_request_skipped_expired requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    item.Response = item.Response ?? ErrorJson("E_BRIDGE_REQUEST_EXPIRED", "Bridge request expired before Unity started executing it.", "Retry after Unity is no longer busy.");
                    item.Completed.Set();
                    continue;
                }

                try
                {
                    WriteLog("bridge_request_started requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    currentWorkItem = item;
                    item.Response = Execute(item.Request);
                    currentWorkItem = null;
                    WriteLog("bridge_request_finished requestId=" + item.Request.RequestId + " command=" + item.Request.Command + " ok=" + Bool(item.Response != null && item.Response.Contains("\"ok\":true")) + " deferred=" + Bool(item.IsDeferred));
                }
                catch (BridgeCommandException exception)
                {
                    if (exception.Transient)
                    {
                        item.Response = TransientErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check output path, permissions, format parameters and UnityCliBridge.log.", exception.RetryAfterMs > 0 ? exception.RetryAfterMs : 5000);
                    }
                    else
                    {
                        item.Response = ErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check output path, permissions, format parameters and UnityCliBridge.log.");
                    }
                }
                catch (Exception exception)
                {
                    currentWorkItem = null;
                    item.Response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and UnityCliBridge.log.");
                }
                finally
                {
                    if (item.TimedOut)
                    {
                        WriteLog("bridge_request_completed_after_http_timeout requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    }
                    // Deferred items are completed asynchronously via EditorApplication.update callbacks;
                    // do not signal the HTTP thread here — the callback will call Completed.Set() later.
                    if (!item.IsDeferred)
                    {
                        item.Completed.Set();
                    }
                }
            }
        }

        private sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;

            public BridgeWorkItem(BridgeRequest request)
            {
                Request = request;
            }

            public BridgeRequest Request { get; private set; }
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            public string Response { get; set; }
            public bool TimedOut { get; private set; }
            public bool IsDeferred { get; private set; }

            public bool TryStart()
            {
                lock (StateLock)
                {
                    if (cancelled)
                    {
                        return false;
                    }
                    started = true;
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
                    return true;
                }
            }

            public void MarkTimedOut()
            {
                TimedOut = true;
            }

            public void MarkDeferred()
            {
                IsDeferred = true;
            }
        }
    }
}
#endif
