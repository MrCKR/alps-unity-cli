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
        private static void RegisterEditorCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "editor.state", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorStateJson()); });
            RegisterCommand(registry, "editor.play", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorPlayJson(request)); });
            RegisterCommand(registry, "editor.pause", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorPauseJson(request)); });
            RegisterCommand(registry, "editor.stop", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorStopJson(request)); });
            RegisterCommand(registry, "editor.menu", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorMenuJson(request)); });
            RegisterCommand(registry, "editor.console", "editor", false, EditorConsole);
        }

        private static string EditorStateJson()
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return "{"
                + "\"playMode\":{\"isPlaying\":" + Bool(EditorApplication.isPlaying) + ",\"isPaused\":" + Bool(EditorApplication.isPaused) + ",\"isPlayingOrWillChangePlaymode\":" + Bool(EditorApplication.isPlayingOrWillChangePlaymode) + "},"
                + "\"busy\":{\"isBusy\":" + Bool(EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || domainReloading) + ",\"compiling\":" + Bool(EditorApplication.isCompiling) + ",\"updating\":" + Bool(EditorApplication.isUpdating) + ",\"building\":" + Bool(BuildPipeline.isBuildingPlayer) + ",\"domainReloading\":" + Bool(domainReloading) + ",\"playModeChanging\":" + Bool(EditorApplication.isPlayingOrWillChangePlaymode) + "},"
                + "\"activeScene\":{\"name\":" + Quote(scene.name) + ",\"path\":" + Quote(scene.path) + ",\"isLoaded\":" + Bool(scene.isLoaded) + ",\"isDirty\":" + Bool(scene.isDirty) + "},"
                + "\"prefabStage\":" + PrefabStageJson(prefabStage) + ","
                + "\"bridge\":{\"buildStamp\":" + Quote(BridgeBuildStamp()) + "},\"runtime\":" + RuntimeSessionJson() + ","
                + "\"plannedEffects\":[]"
                + "}";
        }

        private static string EditorPlayJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.play");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
            WaitForPlayModeState(true, TimeoutMs(request));
            return $"{{\"plannedEffects\":[{{\"type\":\"editor.play\",\"from\":{Bool(before)},\"to\":true}}],\"result\":{{\"requested\":\"play\",\"changed\":{Bool(before != EditorApplication.isPlaying)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{EditorStateJson()}}}";
        }

        private static string EditorPauseJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return $"{{\"plannedEffects\":[{{\"type\":\"editor.pause\",\"from\":{Bool(before)},\"to\":{Bool(EditorApplication.isPaused)}}}],\"result\":{{\"requested\":\"pause\",\"changed\":{Bool(before != EditorApplication.isPaused)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{EditorStateJson()}}}";
        }

        private static string EditorStopJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.stop");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = false;
            // Do not WaitForPlayModeState(false) here: ProcessQueuedWork runs on the
            // main thread via EditorApplication.update, and the stop transition can only
            // complete after the main thread yields back to Unity's event loop.  A
            // synchronous wait for !isPlaying && !isPlayingOrWillChangePlaymode would
            // deadlock (always timeout).  Return immediately — callers can poll
            // editor.state to confirm the transition completed.
            return $"{{\"plannedEffects\":[{{\"type\":\"editor.stop\",\"from\":{Bool(before)},\"to\":false}}],\"result\":{{\"requested\":\"stop\",\"changed\":{Bool(before != EditorApplication.isPlaying)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{EditorStateJson()}}}";
        }

        private static string EditorMenuJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.menu");
            var path = request.StringArg("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Use unity_cli editor menu <path>, for example File/Save.");
            }

            var startedAt = DateTime.UtcNow;
            var executed = EditorApplication.ExecuteMenuItem(path);
            if (!executed)
            {
                throw new BridgeCommandException("E_MENU_NOT_FOUND", "Unity menu item was not found or could not be executed.", "Use the exact Unity Editor menu path, such as File/Save or Assets/Refresh.");
            }

            var elapsedMs = (int)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            var timeoutMs = TimeoutMs(request);
            if (elapsedMs > timeoutMs)
            {
                throw new BridgeCommandException("E_MENU_TIMEOUT", "Unity menu item execution exceeded the requested timeout.", "Check Unity Editor and Unity CLI Bridge logs for long-running menu work.");
            }

            return $"{{\"plannedEffects\":[{{\"type\":\"editor.menu\",\"path\":{Quote(path)}}}],\"result\":{{\"path\":{Quote(path)},\"executed\":true,\"elapsedMs\":{elapsedMs}}},\"summary\":{{\"affected\":\"Unity Editor menu item executed\"}},\"state\":{EditorStateJson()}}}";
        }

        private static string EditorConsole(BridgeRequest request)
        {
            var type = request.StringArg("type");
            var count = Math.Max(1, Math.Min(request.IntArg("count", 50), 500));
            var logEntries = LogEntriesType();
            if (logEntries == null)
            {
                return SuccessJson("{\"cleared\":false,\"type\":" + Quote(type) + ",\"items\":[],\"pageInfo\":{\"pageSize\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":0,\"nextCursor\":null,\"total\":0},\"diagnostics\":[{\"level\":\"warning\",\"code\":\"E_CONSOLE_INTERNAL_API_UNAVAILABLE\",\"message\":\"Unity LogEntries internal API was not available.\"}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            if (request.BoolArg("clear", false))
            {
                var clearMethod = logEntries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (clearMethod != null)
                {
                    clearMethod.Invoke(null, null);
                }
                return SuccessJson("{\"cleared\":true,\"items\":[],\"pageInfo\":{\"pageSize\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":0,\"nextCursor\":null,\"total\":0},\"logPath\":" + Quote(LogPath()) + "}");
            }
            return SuccessJson("{\"cleared\":false,\"type\":" + Quote(type) + ",\"items\":" + ConsoleItemsJson(type, count) + ",\"pageInfo\":{\"pageSize\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":0,\"nextCursor\":null,\"total\":" + ConsoleEntryCount().ToString(CultureInfo.InvariantCulture) + "},\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ConsoleItemsJson(string type, int count)
        {
            var entryType = FindType("UnityEditor.LogEntry");
            var logEntries = LogEntriesType();
            var getEntry = logEntries == null ? null : logEntries.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (entryType == null || getEntry == null)
            {
                return "[]";
            }
            var entry = Activator.CreateInstance(entryType);
            var items = new List<string>();
            var total = ConsoleEntryCount();
            for (var index = total - 1; index >= 0 && items.Count < count; index--)
            {
                getEntry.Invoke(null, new object[] { index, entry });
                var mode = Convert.ToInt32(entryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var level = ConsoleLevel(mode);
                if (!string.IsNullOrEmpty(type) && !string.Equals(type, level, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                items.Add("{\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"type\":" + Quote(level) + ",\"message\":" + Quote(Convert.ToString(entryType.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture)) + ",\"file\":" + Quote(Convert.ToString(entryType.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture)) + ",\"line\":" + Convert.ToInt32(entryType.GetField("line", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) + "}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static int ConsoleEntryCount()
        {
            var logEntries = LogEntriesType();
            var method = logEntries == null ? null : logEntries.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return method == null ? 0 : Convert.ToInt32(method.Invoke(null, null), CultureInfo.InvariantCulture);
        }

        private static Type LogEntriesType()
        {
            return FindType("UnityEditor.LogEntries");
        }

        private static string ConsoleLevel(int mode)
        {
            if ((mode & 1) != 0) return "error";
            if ((mode & 2) != 0) return "assert";
            if ((mode & 4) != 0) return "warning";
            return "log";
        }

        private static int TimeoutMs(BridgeRequest request)
        {
            var value = request.OptionalIntArg("timeoutMs");
            return value > 0 ? Math.Min(value.Value, 7000) : 7000;
        }

        private static void EnsureEditorNotBusy(string command)
        {
            if (domainReloading)
            {
                throw new BridgeCommandException("E_UNITY_DOMAIN_RELOAD", "Unity Editor is reloading assemblies (domain reload).", "Wait for domain reload to complete and retry.", transient: true, retryAfterMs: 10000);
            }
            if (EditorApplication.isCompiling)
            {
                throw new BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isUpdating)
            {
                throw new BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode
                && command != "editor.pause"
                && command != "editor.stop")
            {
                throw new BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            }
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
        }

        private static void WaitForPlayModeState(bool expectedPlaying, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow <= deadline)
            {
                var reached = expectedPlaying ? (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) : (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode);
                if (reached)
                {
                    return;
                }
                Thread.Sleep(25);
            }
            throw new BridgeCommandException("E_PLAYMODE_TIMEOUT", "Unity PlayMode did not reach the requested state before the timeout.", "Wait for PlayMode switching or domain reload to finish, then retry.");
        }
    }
}
#endif
