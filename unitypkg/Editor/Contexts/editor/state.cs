#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>editor.state 内置 Context：读取 Unity Editor 当前只读状态。</summary>
        public sealed class EditorStateContext : IUnityCliContext<EditorStateContext.Request, EditorStateContext.Result>
        {
            public sealed class Request { public bool WaitReady { get; set; } }
            public sealed class Result
            {
                public string SchemaVersion { get; set; }
                public string ObservedAt { get; set; }
                public StateInfo State { get; set; }
                public bool TimedOut { get; set; }
                public string Diagnostics { get; set; }
                public List<PlannedEffect> PlannedEffects { get; set; }
            }
            public sealed class PlannedEffect { public string Type { get; set; } }
            public sealed class StateInfo
            {
                public PlayModeInfo PlayMode { get; set; } public BusyInfo Busy { get; set; } public ActiveSceneInfo ActiveScene { get; set; }
                public bool HasPrefabStage { get; set; } public PrefabStageInfo PrefabStage { get; set; } public BridgeInfo Bridge { get; set; } public RuntimeInfo Runtime { get; set; }
            }
            public sealed class PlayModeInfo { public bool IsPlaying { get; set; } public bool IsPaused { get; set; } public bool IsPlayingOrWillChangePlaymode { get; set; } }
            public sealed class BusyInfo { public bool IsBusy { get; set; } public bool Compiling { get; set; } public bool Updating { get; set; } public bool Building { get; set; } public bool DomainReloading { get; set; } public bool PlayModeChanging { get; set; } }
            public sealed class ActiveSceneInfo { public string Name { get; set; } public string Path { get; set; } public bool IsLoaded { get; set; } public bool IsDirty { get; set; } }
            public sealed class PrefabStageInfo { public string AssetPath { get; set; } public string ScenePath { get; set; } }
            public sealed class BridgeInfo { public string BuildStamp { get; set; } }
            public sealed class RuntimeInfo { public string PlayModeSessionId { get; set; } public bool Ready { get; set; } public long RegistryVersion { get; set; } public string ToolCatalogFingerprint { get; set; } public List<RuntimeToolInfo> AvailableRuntimeTools { get; set; } }
            public sealed class RuntimeToolInfo { public string Name { get; set; } public string Target { get; set; } public string AvailabilityScope { get; set; } public bool Available { get; set; } }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "editor.state", Summary = "Read current Unity Editor play, busy, scene, prefab stage, bridge, and runtime session state.",
                Description = "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy. Pass waitReady=true to block until Unity is no longer busy; on timeout it returns the latest snapshot with timedOut=true instead of erroring.",
                Category = "editor", Version = "1.0.0", Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported", RefreshBehavior = "live",
                Examples = new[] { new ContextExample { Description = "Read the current Editor state", Input = new Request { } }, new ContextExample { Description = "Wait for the Editor to become ready", Input = new Request { WaitReady = true } } }
            };

            /// <summary>可选等待现有忙碌状态结束；不会请求刷新、导入或编译。</summary>
            public async ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
            {
                request = request ?? new Request();
                var timedOut = false;
                if (request.WaitReady && IsEditorBusy())
                {
                    var timeoutMs = Math.Max(0, ctx == null || ctx.TimeoutMs <= 0 ? 60000 : ctx.TimeoutMs);
                    timedOut = ctx == null || ctx.Wait == null || !await ctx.Wait.Until(() => !IsEditorBusy(), TimeSpan.FromMilliseconds(timeoutMs), ct);
                }
                return EditorStateResultFrom(timedOut);
            }
        }

        // 仅当期望播放状态与当前状态不一致时，Unity 才处于 PlayMode 切换期。
        internal static bool IsPlayModeChanging()
        {
            return EditorApplication.isPlayingOrWillChangePlaymode != EditorApplication.isPlaying;
        }

        // 判断 Unity 是否处于编译、导入、构建或播放模式切换状态。
        private static bool IsEditorBusy()
        {
            return EditorApplication.isCompiling || EditorApplication.isUpdating || IsPlayModeChanging() || BuildPipeline.isBuildingPlayer || domainReloading;
        }

        // 构造 Editor 状态 DTO；只读取当前内存状态和场景元数据。
        private static EditorStateContext.Result EditorStateResultFrom(bool timedOut)
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            var result = new EditorStateContext.Result
            {
                SchemaVersion = "1.0", ObservedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), TimedOut = timedOut,
                Diagnostics = timedOut ? "wait-ready timed out before Unity reached idle; returned the latest snapshot." : null,
                PlannedEffects = new List<EditorStateContext.PlannedEffect>(),
                State = new EditorStateContext.StateInfo
                {
                    PlayMode = new EditorStateContext.PlayModeInfo { IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused, IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode },
                    Busy = new EditorStateContext.BusyInfo { IsBusy = IsEditorBusy(), Compiling = EditorApplication.isCompiling, Updating = EditorApplication.isUpdating, Building = BuildPipeline.isBuildingPlayer, DomainReloading = domainReloading, PlayModeChanging = IsPlayModeChanging() },
                    ActiveScene = new EditorStateContext.ActiveSceneInfo { Name = scene.name, Path = scene.path, IsLoaded = scene.isLoaded, IsDirty = scene.isDirty },
                    HasPrefabStage = prefabStage != null, PrefabStage = PrefabStageInfoFrom(prefabStage),
                    Bridge = new EditorStateContext.BridgeInfo { BuildStamp = BridgeBuildStamp() }, Runtime = RuntimeInfoFromCurrentSession()
                }
            };
            return result;
        }

        // 读取 Prefab Stage 路径；未打开 Stage 时返回无值。
        private static EditorStateContext.PrefabStageInfo PrefabStageInfoFrom(object prefabStage)
        {
            if (prefabStage == null) return null;
            var type = prefabStage.GetType();
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            return new EditorStateContext.PrefabStageInfo
            {
                AssetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString(),
                ScenePath = sceneValue is Scene scene ? scene.path : null
            };
        }

        // 读取当前 runtime registry 会话，不初始化或变更运行时状态。
        private static EditorStateContext.RuntimeInfo RuntimeInfoFromCurrentSession()
        {
            var tools = new List<EditorStateContext.RuntimeToolInfo>();
            foreach (var pair in ProjectTools)
            {
                var definition = pair.Value;
                if (definition.Target != "runtime" && definition.Target != "both") continue;
                tools.Add(new EditorStateContext.RuntimeToolInfo { Name = definition.Name, Target = definition.Target, AvailabilityScope = definition.AvailabilityScope, Available = playModeReady });
            }
            return new EditorStateContext.RuntimeInfo
            {
                PlayModeSessionId = currentPlayModeSessionId, Ready = playModeReady, RegistryVersion = runtimeRegistryVersion,
                ToolCatalogFingerprint = runtimeToolCatalogFingerprint, AvailableRuntimeTools = tools
            };
        }
    }
}
#endif
