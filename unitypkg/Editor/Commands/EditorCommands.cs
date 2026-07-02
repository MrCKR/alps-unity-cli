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
using UnityEditor.Compilation;
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
            RegisterCommand(registry, "editor.undo", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorUndoRedoJson("editor.undo")); });
            RegisterCommand(registry, "editor.redo", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorUndoRedoJson("editor.redo")); });
            RegisterCommand(registry, "editor.resolution", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorResolutionJson(request)); });
            RegisterCommand(registry, "editor.menu", "editor", false, delegate(BridgeRequest request) { return SuccessJson(EditorMenuJson(request)); });
            RegisterCommand(registry, "editor.compile", "editor", false, EditorCompile);
            RegisterCommand(registry, "editor.console", "editor", false, EditorConsole);
            RegisterCommand(registry, "console.get", "console", false, ConsoleGet);
            RegisterCommand(registry, "console.clear", "console", false, ConsoleClear);
            RegisterCommand(registry, "editor.selection.set", "editor", false, EditorSelectionSet);
            RegisterCommand(registry, "editor.active-tool.set", "editor", false, EditorActiveToolSet);
            RegisterCommand(registry, "project.tag", "project", false, delegate(BridgeRequest request) { return SuccessJson(ProjectTagLayerJson(request, false)); });
            RegisterCommand(registry, "project.layer", "project", false, delegate(BridgeRequest request) { return SuccessJson(ProjectTagLayerJson(request, true)); });
            RegisterCommand(registry, "package.embed", "package", true, PackageEmbed);
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

        private static string EditorUndoRedoJson(string command)
        {
            EnsureEditorNotBusy(command);
            if (command == "editor.undo") Undo.PerformUndo();
            else Undo.PerformRedo();
            return new UnityCliJsonWriter().BeginObject()
                .Prop("performed", true)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", command).Prop("performed", true).EndObject())
                .PropRaw("state", EditorStateJson())
                .EndObject().ToString();
        }

        private static string EditorResolutionJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.resolution");
            var width = Math.Max(1, request.IntArg("width", PlayerSettings.defaultScreenWidth));
            var height = Math.Max(1, request.IntArg("height", PlayerSettings.defaultScreenHeight));
            var fullscreen = request.OptionalBoolArg("fullscreen");
            PlayerSettings.defaultScreenWidth = width;
            PlayerSettings.defaultScreenHeight = height;
            if (fullscreen.HasValue) PlayerSettings.fullScreenMode = fullscreen.Value ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("resolution", w => w.Prop("width", (long)width).Prop("height", (long)height).Prop("fullScreenMode", PlayerSettings.fullScreenMode.ToString()))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "editor.resolution").Prop("width", (long)width).Prop("height", (long)height).EndObject())
                .EndObject().ToString();
        }

        private static string ProjectTagLayerJson(BridgeRequest request, bool layer)
        {
            EnsureEditorNotBusy(layer ? "project.layer" : "project.tag");
            var action = request.StringArg("action") ?? "add";
            var name = request.StringArg("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID", layer ? "Layer name is required." : "Tag name is required.", "Pass --action add|remove and a tag/layer name.");
            }
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (asset == null)
            {
                throw new BridgeCommandException("E_TAG_MANAGER_NOT_FOUND", "Unity TagManager asset was not found.", "Open the project in Unity and retry.");
            }
            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(layer ? "layers" : "tags");
            if (property == null)
            {
                throw new BridgeCommandException("E_TAG_MANAGER_SCHEMA", "Unity TagManager schema was not recognized.", "Use the Unity Editor Project Settings UI for this Unity version.");
            }
            var changed = layer ? ApplyLayerChange(property, action, name, request.OptionalIntArg("index")) : ApplyTagChange(property, action, name);
            if (changed)
            {
                serialized.ApplyModifiedProperties();
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("action", action)
                .Prop("name", name)
                .Prop("changed", changed)
                .PropArray("plannedEffects", arr => { if (changed) arr.BeginObject().Prop("type", layer ? "project.layer" : "project.tag").Prop("action", action).Prop("name", name).EndObject(); })
                .EndObject().ToString();
        }

        private static bool ApplyTagChange(SerializedProperty tags, string action, string name)
        {
            for (var i = 0; i < tags.arraySize; i++)
            {
                if (tags.GetArrayElementAtIndex(i).stringValue == name)
                {
                    if (action == "remove") { tags.DeleteArrayElementAtIndex(i); return true; }
                    return false;
                }
            }
            if (action == "remove") return false;
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = name;
            return true;
        }

        private static bool ApplyLayerChange(SerializedProperty layers, string action, string name, int? requestedIndex)
        {
            for (var i = 8; i < Math.Min(32, layers.arraySize); i++)
            {
                if (layers.GetArrayElementAtIndex(i).stringValue == name)
                {
                    if (action == "remove") { layers.GetArrayElementAtIndex(i).stringValue = string.Empty; return true; }
                    return false;
                }
            }
            if (action == "remove") return false;
            var index = requestedIndex.HasValue ? requestedIndex.Value : -1;
            if (index < 8 || index >= Math.Min(32, layers.arraySize))
            {
                for (var i = 8; i < Math.Min(32, layers.arraySize); i++)
                {
                    if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { index = i; break; }
                }
            }
            if (index < 8 || index >= Math.Min(32, layers.arraySize)) throw new BridgeCommandException("E_LAYER_SLOT_UNAVAILABLE", "No user layer slot is available.", "Remove an unused user layer or pass --index 8..31.");
            layers.GetArrayElementAtIndex(index).stringValue = name;
            return true;
        }

        private static string EditorMenuJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.menu");
            var path = request.StringArg("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Use unity_cli tools run editor.menu --input '{\"path\":\"File/Save\"}' --yes, or pass another exact Unity menu path.");
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

        /// <summary>editor.compile：在 live Editor 中主动请求脚本编译，可选刷新资产并等待空闲。</summary>
        private static string EditorCompile(BridgeRequest request)
        {
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
            var refresh = request.BoolArg("refresh", true);
            var waitReady = request.BoolArg("waitReady", false) || request.BoolArg("wait-ready", false);
            var timeoutMs = CompileTimeoutMs(request);
            var wasCompiling = EditorApplication.isCompiling;
            var wasUpdating = EditorApplication.isUpdating;
            var alreadyBusy = wasCompiling || wasUpdating;
            var skippedReason = alreadyBusy ? (wasCompiling ? "already-compiling" : "already-importing") : null;
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "editor.compile")
                    .Prop("dryRun", true)
                    .Prop("refresh", refresh)
                    .Prop("waitReady", waitReady)
                    .Prop("wouldRequest", !alreadyBusy)
                    .Prop("skippedReason", skippedReason)
                    .PropArray("plannedEffects", arr => {
                        if (!alreadyBusy)
                        {
                            if (refresh) arr.BeginObject().Prop("type", "asset.refresh").EndObject();
                            arr.BeginObject().Prop("type", "script-compile-request").EndObject();
                        }
                        if (waitReady) arr.BeginObject().Prop("type", "editor.wait-ready").Prop("timeoutMs", (long)timeoutMs).EndObject();
                    })
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }
            if (!alreadyBusy)
            {
                if (refresh)
                {
                    AssetDatabase.Refresh();
                }
                CompilationPipeline.RequestScriptCompilation();
            }
            Func<string> responseFactory = () => new UnityCliJsonWriter().BeginObject()
                .Prop("command", "editor.compile")
                .Prop("requested", !alreadyBusy)
                .Prop("refresh", refresh)
                .Prop("waitReady", waitReady)
                .Prop("wasCompiling", wasCompiling)
                .Prop("wasUpdating", wasUpdating)
                .Prop("skippedReason", skippedReason)
                .PropObject("state", w => w
                    .Prop("compiling", EditorApplication.isCompiling)
                    .Prop("updating", EditorApplication.isUpdating)
                    .Prop("domainReloading", domainReloading)
                    .Prop("isBusy", EditorApplication.isCompiling || EditorApplication.isUpdating || domainReloading || BuildPipeline.isBuildingPlayer))
                .PropArray("plannedEffects", arr => {
                    if (!alreadyBusy)
                    {
                        if (refresh) arr.BeginObject().Prop("type", "asset.refresh").EndObject();
                        arr.BeginObject().Prop("type", "script-compile-request").EndObject();
                    }
                    if (waitReady) arr.BeginObject().Prop("type", "editor.wait-ready").Prop("timeoutMs", (long)timeoutMs).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString();
            if (!waitReady)
            {
                return SuccessJson(responseFactory());
            }
            var yieldedOnce = false;
            return StartDeferredWait(
                currentWorkItem,
                () => {
                    if (!yieldedOnce)
                    {
                        yieldedOnce = true;
                        return false;
                    }
                    return !EditorApplication.isCompiling && !EditorApplication.isUpdating && !domainReloading;
                },
                timeoutMs,
                responseFactory,
                "E_COMPILE_TIMEOUT",
                "Unity did not become idle after requesting script compilation before the timeout.",
                "Increase timeoutMs or inspect editor.state before retrying.");
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
                throw new BridgeCommandException("E_EDITOR_CONSOLE_CLEAR_UNSUPPORTED", "editor.console is read-only and no longer clears the Console.", "Use unity_cli tools run console.clear --dry-run to preview, then unity_cli tools run console.clear --yes to clear.");
            }
            var total = 0;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("cleared", false)
                .Prop("type", type)
                .PropRaw("items", ConsoleItemsJson(type, count, null, 0, out total))
                .PropObject("pageInfo", w => w
                    .Prop("pageSize", (long)count)
                    .Prop("cursor", (long)0)
                    .PropNull("nextCursor")
                    .Prop("total", (long)total))
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>读取 Console 日志：按类型、文本过滤和 newest-first cursor 分页返回，避免一次拉取大量日志。</summary>
        private static string ConsoleGet(BridgeRequest request)
        {
            var type = request.StringArg("type") ?? request.StringArg("level");
            var count = Math.Max(1, Math.Min(request.IntArg("count", 50), 500));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var filter = request.StringArg("filter") ?? request.StringArg("search");
            var logEntries = LogEntriesType();
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "console.get");
            if (logEntries == null)
            {
                w.PropArray("items", null).Prop("total", 0).Prop("diagnostics", "LogEntries API unavailable");
                w.PropObject("pageInfo", p => p.Prop("pageSize", (long)count).Prop("cursor", (long)cursor).PropNull("nextCursor").Prop("total", 0L));
            }
            else
            {
                var total = 0;
                w.PropRaw("items", ConsoleItemsJson(type, count, filter, cursor, out total));
                w.Prop("total", (long)total);
                var nextCursor = cursor + count < total ? (cursor + count).ToString(CultureInfo.InvariantCulture) : null;
                w.PropObject("pageInfo", p => p.Prop("pageSize", (long)count).Prop("cursor", (long)cursor).Prop("nextCursor", nextCursor).Prop("total", (long)total));
            }
            if (!string.IsNullOrEmpty(filter)) w.Prop("filter", filter);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        /// <summary>console.clear：清空 Console。</summary>
        private static string ConsoleClear(BridgeRequest request)
        {
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "console.clear").Prop("dryRun", true)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "console.clear").Prop("sideEffects", "clears-console").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var logEntries = LogEntriesType();
            var clearMethod = logEntries?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            clearMethod?.Invoke(null, null);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "console.clear").Prop("cleared", true)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "console.clear").EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>editor.selection.set：设置 Editor 选择。读 selection 走 Context。</summary>
        private static string EditorSelectionSet(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("path");
            var instanceId = request.OptionalIntArg("instanceId");
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "editor.selection.set").Prop("dryRun", true)
                    .Prop("target", target ?? "<instanceId>").Prop("logPath", LogPath()).EndObject().ToString());
            }
            UnityEngine.Object obj = null;
            if (instanceId.HasValue && instanceId.Value != 0)
            {
                obj = EditorUtility.InstanceIDToObject(instanceId.Value);
            }
            else if (!string.IsNullOrEmpty(target))
            {
                obj = ObjectRefResolver.FindGameObjectByPath(target);
            }
            if (obj == null)
            {
                throw new BridgeCommandException("E_SELECTION_TARGET_NOT_FOUND", "Selection target not found.", "Pass a valid instanceId or path.");
            }
            Selection.activeObject = obj;
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "editor.selection.set")
                .Prop("selected", obj.name).Prop("typeName", obj.GetType().Name)
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>editor.active-tool.set：设置 Unity Editor Move/Rotate/Scale 工具。不是 unity_cli tool activation。</summary>
        private static string EditorActiveToolSet(BridgeRequest request)
        {
            var tool = request.StringArg("tool") ?? request.StringArg("name");
            if (string.IsNullOrEmpty(tool))
            {
                throw new BridgeCommandException("E_TOOL_REQUIRED", "editor.active-tool.set requires --tool.", "Pass View, Move, Rotate, Scale, Rect, Transform.");
            }
            Tool editorTool;
            switch (tool.ToLowerInvariant())
            {
                case "view": case "pan": editorTool = Tool.View; break;
                case "move": case "translate": editorTool = Tool.Move; break;
                case "rotate": case "rotation": editorTool = Tool.Rotate; break;
                case "scale": editorTool = Tool.Scale; break;
                case "rect": editorTool = Tool.Rect; break;
                case "transform": editorTool = Tool.Transform; break;
                default: throw new BridgeCommandException("E_TOOL_UNKNOWN", "Unknown editor tool: " + tool, "Pass View, Move, Rotate, Scale, Rect, or Transform.");
            }
            Tools.current = editorTool;
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "editor.active-tool.set")
                .Prop("tool", tool).Prop("editorTool", editorTool.ToString())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>package.embed：将 Packages/ 下的包嵌入为 Assets/ 本地副本。对齐 CoplayDev embed_package。</summary>
        private static string PackageEmbed(BridgeRequest request)
        {
            var packageId = request.StringArg("package") ?? request.StringArg("packageId") ?? request.StringArg("id");
            if (string.IsNullOrEmpty(packageId))
            {
                throw new BridgeCommandException("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", "Pass a package id like com.unity.x.");
            }
            var info = FindInstalledPackageInfo(packageId);
            if (info == null)
            {
                throw new BridgeCommandException("E_PACKAGE_NOT_FOUND", "Package not found: " + packageId, "Run package list to discover installed packages.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "package.embed").Prop("dryRun", true)
                    .Prop("package", packageId)
                    .Prop("source", info.resolvedPath)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "package.embed").Prop("sideEffects", "domain-reload|manifest-change|file-copy").EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            var destPath = "Assets/" + packageId;
            CreateAssetDirectory(destPath + "/.");
            FileUtil.CopyFileOrDirectory(info.resolvedPath, AssetFullPath(destPath));
            AssetDatabase.Refresh();
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "package.embed")
                .Prop("package", packageId).Prop("source", info.resolvedPath).Prop("destination", destPath)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "package.embed").Prop("destination", destPath).EndObject())
                .Prop("nextActions", "Remove the package from manifest after embedding to avoid duplicate symbols.")
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>查找已安装 Unity package，dry-run 也复用该校验，避免不存在包返回可执行计划。</summary>
        private static UnityEditor.PackageManager.PackageInfo FindInstalledPackageInfo(string packageId)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + packageId);
            if (info != null) return info;
            var listRequest = UnityEditor.PackageManager.Client.List(true);
            while (!listRequest.IsCompleted) System.Threading.Thread.Sleep(50);
            if (listRequest.Status == UnityEditor.PackageManager.StatusCode.Success)
            {
                return listRequest.Result.FirstOrDefault(p => p.name == packageId);
            }
            return null;
        }

        /// <summary>按 newest-first 顺序读取 Console 条目，并在过滤后的集合上应用 cursor 分页。</summary>
        private static string ConsoleItemsJson(string type, int count, string filter, int cursor, out int filteredTotal)
        {
            filteredTotal = 0;
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
            var skipped = 0;
            var includeAllTypes = string.IsNullOrEmpty(type) || string.Equals(type, "all", StringComparison.OrdinalIgnoreCase);
            for (var index = total - 1; index >= 0; index--)
            {
                getEntry.Invoke(null, new object[] { index, entry });
                var mode = Convert.ToInt32(entryType.GetField("mode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture);
                var level = ConsoleLevel(mode);
                if (!includeAllTypes && !string.Equals(type, level, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var message = Convert.ToString(entryType.GetField("message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture) ?? "";
                var file = Convert.ToString(entryType.GetField("file", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(entry), CultureInfo.InvariantCulture) ?? "";
                if (!string.IsNullOrEmpty(filter)
                    && message.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && file.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                filteredTotal++;
                if (skipped < cursor)
                {
                    skipped++;
                    continue;
                }
                if (added >= count)
                {
                    continue;
                }
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

        /// <summary>脚本编译可能跨 import/domain reload，等待上限与 compile-diagnostics 保持一致。</summary>
        private static int CompileTimeoutMs(BridgeRequest request)
        {
            var value = request.OptionalIntArg("timeoutMs");
            return value > 0 ? Math.Min(value.Value, 55000) : 45000;
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
                && !EditorApplication.isPlaying
                && command != "editor.pause"
                && command != "editor.stop"
                && command != "editor.state"
                && command != "editor.console")
            {
                // 只拦截进入/退出 Play Mode 的过渡期；稳定运行态允许 AI 继续用 CLI 工具调试和改场景。
                throw new BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
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
