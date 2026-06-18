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
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("playMode", w => w
                    .Prop("isPlaying", EditorApplication.isPlaying)
                    .Prop("isPaused", EditorApplication.isPaused)
                    .Prop("isPlayingOrWillChangePlaymode", EditorApplication.isPlayingOrWillChangePlaymode))
                .PropObject("busy", w => w
                    .Prop("isBusy", EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer || domainReloading)
                    .Prop("compiling", EditorApplication.isCompiling)
                    .Prop("updating", EditorApplication.isUpdating)
                    .Prop("building", BuildPipeline.isBuildingPlayer)
                    .Prop("domainReloading", domainReloading)
                    .Prop("playModeChanging", EditorApplication.isPlayingOrWillChangePlaymode))
                .PropObject("activeScene", w => w
                    .Prop("name", scene.name)
                    .Prop("path", scene.path)
                    .Prop("isLoaded", scene.isLoaded)
                    .Prop("isDirty", scene.isDirty))
                .PropRaw("prefabStage", PrefabStageJson(prefabStage))
                .PropObject("bridge", w => w.Prop("buildStamp", BridgeBuildStamp()))
                .PropRaw("runtime", RuntimeSessionJson())
                .PropArray("plannedEffects", null)
                .EndObject().ToString();
        }

        private static string EditorPlayJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.play");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
            var timeoutMs = TimeoutMs(request);
            var responseFactory = new Func<string>(() =>
                new UnityCliJsonWriter().BeginObject()
                    .PropArray("plannedEffects", arr => arr
                        .BeginObject().Prop("type", "editor.play").Prop("from", before).Prop("to", true).EndObject())
                    .PropObject("result", w => w
                        .Prop("requested", "play")
                        .Prop("changed", before != EditorApplication.isPlaying)
                        .Prop("isPlaying", EditorApplication.isPlaying)
                        .Prop("isPaused", EditorApplication.isPaused))
                    .PropRaw("state", EditorStateJson())
                    .EndObject().ToString());
            var reached = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            if (reached)
            {
                return responseFactory();
            }
            // Play mode transition requires the main thread to yield back to Unity's event loop.
            // Use a deferred EditorApplication.update callback to wait without blocking the main thread.
            return StartDeferredWait(
                currentWorkItem,
                () => EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode,
                timeoutMs,
                responseFactory,
                "E_PLAYMODE_TIMEOUT",
                "Unity PlayMode did not reach the requested state before the timeout.",
                "Wait for PlayMode switching or domain reload to finish, then retry.");
        }

        private static string EditorPauseJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("plannedEffects", arr => arr
                    .BeginObject().Prop("type", "editor.pause").Prop("from", before).Prop("to", EditorApplication.isPaused).EndObject())
                .PropObject("result", w => w
                    .Prop("requested", "pause")
                    .Prop("changed", before != EditorApplication.isPaused)
                    .Prop("isPlaying", EditorApplication.isPlaying)
                    .Prop("isPaused", EditorApplication.isPaused))
                .PropRaw("state", EditorStateJson())
                .EndObject().ToString();
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
            return new UnityCliJsonWriter().BeginObject()
                .PropArray("plannedEffects", arr => arr
                    .BeginObject().Prop("type", "editor.stop").Prop("from", before).Prop("to", false).EndObject())
                .PropObject("result", w => w
                    .Prop("requested", "stop")
                    .Prop("changed", before != EditorApplication.isPlaying)
                    .Prop("isPlaying", EditorApplication.isPlaying)
                    .Prop("isPaused", EditorApplication.isPaused))
                .PropRaw("state", EditorStateJson())
                .EndObject().ToString();
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

            return new UnityCliJsonWriter().BeginObject()
                .PropArray("plannedEffects", arr => arr
                    .BeginObject().Prop("type", "editor.menu").Prop("path", path).EndObject())
                .PropObject("result", w => w
                    .Prop("path", path)
                    .Prop("executed", true)
                    .Prop("elapsedMs", (long)elapsedMs))
                .PropObject("summary", w => w
                    .Prop("affected", "Unity Editor menu item executed"))
                .PropRaw("state", EditorStateJson())
                .EndObject().ToString();
        }

        private static string EditorConsole(BridgeRequest request)
        {
            var type = request.StringArg("type");
            var count = Math.Max(1, Math.Min(request.IntArg("count", 50), 500));
            var logEntries = LogEntriesType();
            if (logEntries == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("cleared", false)
                    .Prop("type", type)
                    .PropArray("items", null)
                    .PropObject("pageInfo", w => w
                        .Prop("pageSize", (long)count)
                        .Prop("cursor", (long)0)
                        .PropNull("nextCursor")
                        .Prop("total", (long)0))
                    .PropArray("diagnostics", arr => arr
                        .BeginObject()
                        .Prop("level", "warning")
                        .Prop("code", "E_CONSOLE_INTERNAL_API_UNAVAILABLE")
                        .Prop("message", "Unity LogEntries internal API was not available.")
                        .EndObject())
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            if (request.BoolArg("clear", false))
            {
                var clearMethod = logEntries.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (clearMethod != null)
                {
                    clearMethod.Invoke(null, null);
                }
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("cleared", true)
                    .PropArray("items", null)
                    .PropObject("pageInfo", w => w
                        .Prop("pageSize", (long)count)
                        .Prop("cursor", (long)0)
                        .PropNull("nextCursor")
                        .Prop("total", (long)0))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("cleared", false)
                .Prop("type", type)
                .PropRaw("items", ConsoleItemsJson(type, count))
                .PropObject("pageInfo", w => w
                    .Prop("pageSize", (long)count)
                    .Prop("cursor", (long)0)
                    .PropNull("nextCursor")
                    .Prop("total", (long)ConsoleEntryCount()))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
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
            var writer = new UnityCliJsonWriter().BeginArray();
            var total = ConsoleEntryCount();
            var added = 0;
            for (var index = total - 1; index >= 0 && added < count; index--)
            {
                getEntry.Invoke(null, new object[] { index, entry });
                var mode = Convert.ToInt32(entryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var level = ConsoleLevel(mode);
                if (!string.IsNullOrEmpty(type) && !string.Equals(type, level, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var message = Convert.ToString(entryType.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var file = Convert.ToString(entryType.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var line = Convert.ToInt32(entryType.GetField("line", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                writer.BeginObject()
                    .Prop("index", (long)index)
                    .Prop("type", level)
                    .Prop("message", message)
                    .Prop("file", file)
                    .Prop("line", (long)line)
                    .EndObject();
                added++;
            }
            return writer.EndArray().ToString();
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
                && command != "editor.stop"
                && command != "editor.state"
                && command != "editor.console")
            {
                var isTransition = !EditorApplication.isPlaying;
                var message = isTransition
                    ? "Unity Editor is entering or exiting Play mode."
                    : "Unity Editor is in Play Mode; this editor-domain command is not available.";
                var nextAction = isTransition
                    ? "Wait for Play mode transition to complete and retry."
                    : "Exit Play Mode first (use editor stop).";
                throw new BridgeCommandException("E_UNITY_PLAY_MODE", message, nextAction, transient: isTransition, retryAfterMs: isTransition ? 3000 : 0);
            }
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
        }

        /// <summary>
        /// Removed: WaitForPlayModeState with Thread.Sleep has been replaced by
        /// the deferred EditorApplication.update mechanism in EditorPlayJson.
        /// Play mode state transitions require the main thread to yield to Unity's
        /// event loop; synchronous polling with Thread.Sleep blocks the editor and
        /// is no longer used. See StartDeferredWait for the replacement pattern.
        /// </summary>
    }
}
#endif
