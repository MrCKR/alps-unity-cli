#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// editor.state 内置 Context：读取 Unity Editor 当前只读状态，并保留 waitReady deferred 语义。
        /// </summary>
        public sealed class EditorStateContext : IUnityCliContext<EditorStateContext.Request, EditorStateContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                public bool WaitReady { get; set; }
                public int TimeoutMs { get; set; }
            }

            public sealed class Result
            {
                public string SchemaVersion { get; set; }
                public string ObservedAt { get; set; }
                public bool TimedOut { get; set; }
                public string Diagnostics { get; set; }
                public StateInfo State { get; set; }
            }

            public sealed class StateInfo
            {
                public PlayModeInfo PlayMode { get; set; }
                public BusyInfo Busy { get; set; }
                public ActiveSceneInfo ActiveScene { get; set; }
                public PrefabStageInfo PrefabStage { get; set; }
                public BridgeInfo Bridge { get; set; }
                public RuntimeInfo Runtime { get; set; }
            }

            public sealed class PlayModeInfo
            {
                public bool IsPlaying { get; set; }
                public bool IsPaused { get; set; }
                public bool IsPlayingOrWillChangePlaymode { get; set; }
            }

            public sealed class BusyInfo
            {
                public bool IsBusy { get; set; }
                public bool Compiling { get; set; }
                public bool Updating { get; set; }
                public bool Building { get; set; }
                public bool DomainReloading { get; set; }
                public bool PlayModeChanging { get; set; }
            }

            public sealed class ActiveSceneInfo
            {
                public string Name { get; set; }
                public string Path { get; set; }
                public bool IsLoaded { get; set; }
                public bool IsDirty { get; set; }
            }

            public sealed class PrefabStageInfo
            {
                public string AssetPath { get; set; }
                public string ScenePath { get; set; }
            }

            public sealed class BridgeInfo
            {
                public string BuildStamp { get; set; }
            }

            public sealed class RuntimeInfo
            {
                public string PlayModeSessionId { get; set; }
                public bool Ready { get; set; }
                public long RegistryVersion { get; set; }
                public string ToolCatalogFingerprint { get; set; }
                public List<RuntimeToolInfo> AvailableRuntimeTools { get; set; }
            }

            public sealed class RuntimeToolInfo
            {
                public string Name { get; set; }
                public string Target { get; set; }
                public string AvailabilityScope { get; set; }
                public bool Available { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "editor.state",
                Summary = "Read current Unity Editor play, busy, scene, prefab stage, bridge, and runtime session state.",
                Description = "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy. Pass waitReady=true to block on the main thread until Unity is no longer busy; on timeout it returns the latest snapshot with timedOut=true instead of erroring.",
                Category = "editor",
                Version = "1.0.0",
                Source = "built-in",
                Target = "editor",
                AvailabilityScope = "editorSession",
                PlayModeSupport = "supported",
                RefreshBehavior = "live"
            };

            /// <summary>
            /// 同步 fallback 读取；生产 Bridge 注册路径会优先调用 ReadRaw，以保留 deferred marker 控制流。
            /// </summary>
            public Result Read(Request request, UnityCliProjectToolContext ctx)
            {
                var json = EditorStateContextEnvelopeJson(EditorStateJson(), false);
                return UnityCliSerializer.Deserialize(json, typeof(Result)) as Result;
            }

            /// <summary>
            /// 复用 Bridge 原始响应协议，支持 waitReady 在 busy 状态下挂起当前 BridgeWorkItem。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                var waitReady = request.BoolArg("waitReady", false);
                var timeoutMs = Math.Max(0, request.IntArg("timeoutMs", 60000));
                var stateJson = EditorStateJson();
                var isBusy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || domainReloading;
                // wait-ready：当前 busy 且允许延迟时，在主线程轮询到 idle，避免 AI 反复轮询消耗往返。
                // 超时不报错，返回当前状态 + timedOut=true，让 AI 至少拿到快照。
                if (waitReady && isBusy && currentWorkItem != null)
                {
                    var item = currentWorkItem;
                    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                    EditorApplication.CallbackFunction waiter = null;
                    waiter = () =>
                    {
                        try
                        {
                            var stillBusy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || domainReloading;
                            if (!stillBusy || DateTime.UtcNow > deadline)
                            {
                                EditorApplication.update -= waiter;
                                UnregisterDeferredItem(item);
                                var timedOut = DateTime.UtcNow > deadline;
                                item.Response = SuccessJson(EditorStateContextEnvelopeJson(EditorStateJson(), timedOut));
                                item.Completed.Set();
                                WriteLog("editor_state_wait_completed requestId=" + item.Request.RequestId + " timedOut=" + timedOut.ToString().ToLowerInvariant());
                            }
                        }
                        catch (Exception exception)
                        {
                            EditorApplication.update -= waiter;
                            UnregisterDeferredItem(item);
                            item.Response = SuccessJson(EditorStateContextEnvelopeJson(EditorStateJson(), true));
                            item.Completed.Set();
                            WriteLog("editor_state_wait_exception requestId=" + item.Request.RequestId + " " + exception.GetType().Name + ": " + exception.Message);
                        }
                    };
                    item.MarkDeferred();
                    lock (DeferredItems) { DeferredItems.Add(item); }
                    EditorApplication.update += waiter;
                    WriteLog("editor_state_wait_started requestId=" + item.Request.RequestId + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                    return DeferredResponseMarker;
                }
                return SuccessJson(EditorStateContextEnvelopeJson(stateJson, false));
            }
        }

        // editor.state Context envelope：schemaVersion + observedAt + state + 可选 timedOut。
        private static string EditorStateContextEnvelopeJson(string stateJson, bool timedOut)
        {
            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", "1.0" },
                { "observedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "state", UnityCliToolJson.Parse(stateJson) }
            };
            if (timedOut)
            {
                payload["timedOut"] = true;
                payload["diagnostics"] = "wait-ready timed out before Unity reached idle; returned the latest snapshot.";
            }
            return UnityCliSerializer.Serialize(payload);
        }
    }
}
#endif
