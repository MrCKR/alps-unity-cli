#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

        /// <summary>读取 Console 日志条目;Unity 2022 通过内部 LogEntries 反射读取。</summary>
        public static List<ConsoleEntry> ReadConsoleEntries(string typeFilter, string textFilter, int count)
        {
            var entries = new List<ConsoleEntry>();
            var entryType = FindType("UnityEditor.LogEntry");
            var logEntries = FindType("UnityEditor.LogEntries");
            var getEntry = logEntries == null ? null : logEntries.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (entryType == null || getEntry == null)
            {
                return entries;
            }

            var entry = Activator.CreateInstance(entryType);
            var total = ConsoleEntryCount(logEntries);
            var max = Math.Max(1, Math.Min(count > 0 ? count : 50, 1000));
            var includeAllTypes = string.IsNullOrEmpty(typeFilter) || string.Equals(typeFilter, "all", StringComparison.OrdinalIgnoreCase);
            for (var i = total - 1; i >= 0 && entries.Count < max; i--)
            {
                getEntry.Invoke(null, new object[] { i, entry });
                var mode = Convert.ToInt32(entryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var logType = ConsoleLevel(mode);
                if (!includeAllTypes && !string.Equals(logType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                var message = Convert.ToString(entryType.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture) ?? "";
                var file = Convert.ToString(entryType.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture) ?? "";
                if (!string.IsNullOrEmpty(textFilter)
                    && message.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0
                    && file.IndexOf(textFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                entries.Add(new ConsoleEntry { Type = logType, Message = message, Count = 1 });
            }
            return entries;
        }

        private static int ConsoleEntryCount(Type logEntries)
        {
            var method = logEntries == null ? null : logEntries.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null ? 0 : Convert.ToInt32(method.Invoke(null, null), CultureInfo.InvariantCulture);
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }

        private static string ConsoleLevel(int mode)
        {
            if ((mode & 1) != 0) return "error";
            if ((mode & 2) != 0) return "assert";
            if ((mode & 4) != 0) return "warning";
            return "log";
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
