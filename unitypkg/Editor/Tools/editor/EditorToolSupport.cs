#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// editor/console 域 IUnityCliTool 共享 helper:编辑器状态、PlayMode 控制、
    /// 菜单执行、Console 读取、标签/层管理。仅供 editor 域自包含工具复用。
    /// </summary>
    public static class EditorToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            if (EditorApplication.isUpdating)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            if (BuildPipeline.isBuildingPlayer)
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
        }

        /// <summary>构造编辑器状态 DTO。</summary>
        public static EditorStateInfo BuildEditorState()
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return new EditorStateInfo
            {
                PlayMode = new PlayModeInfo { IsPlaying = EditorApplication.isPlaying, IsPaused = EditorApplication.isPaused, IsPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode },
                Busy = new BusyInfo { IsBusy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer, Compiling = EditorApplication.isCompiling, Updating = EditorApplication.isUpdating, Building = BuildPipeline.isBuildingPlayer, PlayModeChanging = EditorApplication.isPlayingOrWillChangePlaymode },
                ActiveScene = new ActiveSceneInfo { Name = scene.name, Path = scene.path, IsLoaded = scene.isLoaded, IsDirty = scene.isDirty },
                PrefabStageOpen = prefabStage != null,
                PrefabStagePath = prefabStage != null ? prefabStage.assetPath : null
            };
        }

        /// <summary>读取 Console 日志条目。</summary>
        public static List<ConsoleEntry> ReadConsoleEntries(string typeFilter, string textFilter, int count)
        {
            var entries = new List<ConsoleEntry>();
            var logs = InternalEditorUtility.GetConsoleEntries();
            var max = Math.Max(1, Math.Min(count > 0 ? count : 50, 1000));
            for (var i = logs.Length - 1; i >= 0 && entries.Count < max; i--)
            {
                var log = logs[i];
                var mode = (int)log.mode;
                var logType = "log";
                if ((mode & (int)LogType.Error) != 0 || (mode & (int)LogType.Exception) != 0 || (mode & (int)LogType.Assert) != 0) logType = "error";
                else if ((mode & (int)LogType.Warning) != 0) logType = "warning";
                if (!string.IsNullOrEmpty(typeFilter) && !string.Equals(logType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var text = log.condition ?? "";
                if (!string.IsNullOrEmpty(textFilter) && text.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                entries.Add(new ConsoleEntry { Type = logType, Message = text, Count = log.instanceCount });
            }
            return entries;
        }
    }

    // ── editor 域 DTO ──

    public sealed class EditorStateInfo
    {
        public PlayModeInfo PlayMode { get; set; }
        public BusyInfo Busy { get; set; }
        public ActiveSceneInfo ActiveScene { get; set; }
        public bool PrefabStageOpen { get; set; }
        public string PrefabStagePath { get; set; }
    }

    public sealed class PlayModeInfo { public bool IsPlaying { get; set; } public bool IsPaused { get; set; } public bool IsPlayingOrWillChangePlaymode { get; set; } }
    public sealed class BusyInfo { public bool IsBusy { get; set; } public bool Compiling { get; set; } public bool Updating { get; set; } public bool Building { get; set; } public bool PlayModeChanging { get; set; } }
    public sealed class ActiveSceneInfo { public string Name { get; set; } public string Path { get; set; } public bool IsLoaded { get; set; } public bool IsDirty { get; set; } }
    public sealed class ConsoleEntry { public string Type { get; set; } public string Message { get; set; } public long Count { get; set; } }
    public sealed class EditorPlannedEffect { public string Type { get; set; } public string Path { get; set; } public string Action { get; set; } public string Name { get; set; } }
    public sealed class PlayModeResult { public string Requested { get; set; } public bool Changed { get; set; } public bool IsPlaying { get; set; } public bool IsPaused { get; set; } }
    public sealed class MenuResult { public string Path { get; set; } public bool Executed { get; set; } public long ElapsedMs { get; set; } }
    public sealed class CompileResult { public bool DryRun { get; set; } public bool Refresh { get; set; } public bool WaitReady { get; set; } public bool WouldRequest { get; set; } public string SkippedReason { get; set; } public bool Requested { get; set; } }
    public sealed class TagLayerResult { public string Action { get; set; } public string Name { get; set; } public bool Changed { get; set; } }
}
#endif
