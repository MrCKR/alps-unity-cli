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
        /// <summary>
        /// Register context.* internal bridge commands.
        ///
        /// Context commands are read-only observations of Unity project and editor state.
        /// They are always allowed in Play Mode because they never mutate state.
        /// If a capability has side effects, it must be a Tool, not a Context.
        ///
        /// Built-in context registry with context definitions and read handlers.
        /// Registration is handled by ProjectContextRegistry which unifies built-in
        /// and project custom context commands into a single handler.
        /// </summary>

        // ── Built-in context definition ──

        internal sealed class BuiltInContextDefinition
        {
            public string Name;
            public string Description;
            public string Category;
            public string Source;
            public string RefreshBehavior;
            public string Target;
            public string AvailabilityScope;
            public string Handler;
            public string InputSchema;
            public string OutputSchema;
            public string[] RequiresPackages;
            public Func<BridgeRequest, string> ReadHandler;
        }

        private static readonly BuiltInContextDefinition[] BuiltInContexts = BuildBuiltInContextTable();

        private static BuiltInContextDefinition[] BuildBuiltInContextTable()
        {
            return new[]
            {
                // ── Project contexts ──
                new BuiltInContextDefinition
                {
                    Name = "project.info", Description = "Project metadata: name, path, Unity version, and overall project structure.",
                    Category = "project", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"projectRoot\":{\"type\":\"string\"},\"projectName\":{\"type\":\"string\"},\"unityVersion\":{\"type\":\"string\"},\"platform\":{\"type\":\"string\"},\"assetsPath\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectInfo
                },
                new BuiltInContextDefinition
                {
                    Name = "project.tags", Description = "List all tags defined in the project's Tag Manager.",
                    Category = "project", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectTags
                },
                new BuiltInContextDefinition
                {
                    Name = "project.layers", Description = "List all layers defined in the project's Tag Manager.",
                    Category = "project", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}}}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectLayers
                },
                // ── Editor contexts ──
                new BuiltInContextDefinition
                {
                    Name = "editor.state", Description = "Read-only snapshot of current Unity Editor state: Play Mode, compile/import/build busy flags, active scene, Prefab Stage, Bridge build stamp, and runtime session status. Use this before live tools to avoid acting while Unity is busy. Pass waitReady=true to block on the main thread until Unity is no longer busy (compiling/updating/building/playmode-switching/domain-reloading), reducing AI polling round-trips; on timeout it returns the latest snapshot with timedOut=true instead of erroring.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"waitReady\":{\"type\":\"boolean\",\"description\":\"If true, block on the Unity main thread until the editor is no longer busy (compile/import/build/playmode/domain-reload), then return the idle snapshot. Defaults to false (immediate snapshot).\"},\"timeoutMs\":{\"type\":\"integer\",\"default\":60000,\"description\":\"Maximum milliseconds to wait when waitReady=true. On timeout the latest snapshot is returned with timedOut=true.\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"schemaVersion\":{\"type\":\"string\"},\"observedAt\":{\"type\":\"string\"},\"timedOut\":{\"type\":\"boolean\"},\"diagnostics\":{\"type\":\"string\"},\"state\":{\"type\":\"object\",\"properties\":{\"playMode\":{\"type\":\"object\"},\"busy\":{\"type\":\"object\"},\"activeScene\":{\"type\":\"object\"},\"prefabStage\":{\"type\":\"object\"},\"bridge\":{\"type\":\"object\"},\"runtime\":{\"type\":\"object\"}}}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorStateContext
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.selection", Description = "Read the current Unity Editor selection in Hierarchy or Project Browser. Returns legacy name arrays plus reusable object details: instanceId, type, asset path/GUID, hierarchy path, GameObject reference, transform, and component summaries. It does not select, focus, ping, or modify anything.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"activeObject\":{\"type\":[\"string\",\"null\"]},\"activeGameObject\":{\"type\":[\"string\",\"null\"]},\"activeTransform\":{\"type\":[\"string\",\"null\"]},\"activeObjectDetail\":{\"type\":[\"object\",\"null\"]},\"activeGameObjectDetail\":{\"type\":[\"object\",\"null\"]},\"activeTransformDetail\":{\"type\":[\"object\",\"null\"]},\"objects\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}},\"objectDetails\":{\"type\":\"array\",\"items\":{\"type\":[\"object\",\"null\"]}},\"gameObjects\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"gameObjectDetails\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"assetGUIDs\":{\"type\":\"array\",\"items\":{\"type\":[\"string\",\"null\"]}}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorSelection
                },

                new BuiltInContextDefinition
                {
                    Name = "editor.active-tool", Description = "Read the currently active Unity Editor transform/tool handle plus pivot mode, pivot rotation, handle position, and handle rotation. Does not change the active tool.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"activeTool\":{\"type\":\"string\"},\"isCustom\":{\"type\":\"boolean\"},\"pivotMode\":{\"type\":\"string\"},\"pivotRotation\":{\"type\":\"string\"},\"handlePosition\":{\"type\":\"object\"},\"handleRotation\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorActiveTool
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.windows", Description = "Read currently open Unity EditorWindow instances with title, type names, focused state, and instance IDs. This observes editor UI state without opening, closing, focusing, or moving windows.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"windows\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"fullName\":{\"type\":\"string\"},\"isFocused\":{\"type\":\"boolean\"},\"instanceID\":{\"type\":\"integer\"}}}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorWindows
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.tool-states", Description = "Read Unity Editor transform tool settings, pivot mode/rotation, snap settings, and grid availability. This is not MCP tool discovery; it observes editor tool configuration only.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"transform\":{\"type\":\"object\"},\"snap\":{\"type\":\"object\"},\"gridAvailable\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorToolStates
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.stage", Description = "Current Prefab Mode / Prefab Stage state if a prefab is being edited.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"isOpen\":{\"type\":\"boolean\"},\"assetPath\":{\"type\":\"string\"},\"prefabRootName\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"},\"isDirty\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabStage
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.menu-items", Description = "Attempt to enumerate Unity Editor menu items as read-only discovery. Unity has no stable public API for full menu enumeration, so this context may return an empty items array with diagnostics; executing a menu path remains the destructive Tool editor.menu.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"search\":{\"type\":\"string\",\"description\":\"Optional menu path/title substring filter if a Unity version exposes enumerable menu data.\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"count\":{\"type\":\"integer\"},\"diagnostics\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorMenuItems
                },
                // ── Scene contexts ──
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject", Description = "Read a specific GameObject's properties from the active scene.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"GameObject name or path.\"},\"id\":{\"type\":\"integer\",\"description\":\"Instance ID.\"},\"path\":{\"type\":\"string\",\"description\":\"Hierarchy path.\"},\"includeComponents\":{\"type\":\"boolean\",\"description\":\"Include component summaries.\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObject
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject.components", Description = "Read all components attached to a GameObject.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"},\"includeProperties\":{\"type\":\"boolean\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"},\"components\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObjectComponents
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject.component", Description = "Read one component attached to a GameObject.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"},\"component\":{\"type\":\"string\"}},\"required\":[\"component\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"},\"component\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObjectComponent
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.opened", Description = "List currently loaded scenes in the Editor with name, path, build index, loaded state, dirty state, and active scene. Read-only; does not load, unload, save, or switch scenes.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"scenes\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"buildIndex\":{\"type\":\"integer\"},\"isLoaded\":{\"type\":\"boolean\"},\"isDirty\":{\"type\":\"boolean\"}}}},\"count\":{\"type\":\"integer\"},\"activeScene\":{\"type\":\"string\"},\"logPath\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneOpened
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.cameras", Description = "List all active scene cameras with stable GameObject path, tag, enabled state, projection, FOV/orthographic size, depth, target texture, and pixel size. Read-only camera inventory.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"cameras\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneCameras
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.volumes", Description = "List Volume components in the active scene with target GameObject, enabled state, global flag, priority, weight, profile reference, and pagination metadata. Returns an empty list when Volume type is unavailable.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\"},\"cursor\":{\"type\":\"string\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"volumes\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"count\":{\"type\":\"integer\"},\"pageInfo\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneVolumes
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.renderer-features", Description = "Read URP renderer feature availability and configured feature names for the active/default render pipeline. If URP or a pipeline asset is unavailable, returns available=false with diagnostics and next_actions.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"available\":{\"type\":\"boolean\"},\"renderer\":{\"type\":\"string\"},\"features\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"count\":{\"type\":\"integer\"},\"diagnostics\":{\"type\":\"string\"},\"next_actions\":{\"type\":\"array\"}}}",
                    RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, ReadHandler = ReadSceneRendererFeatures
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.rendering-stats", Description = "Read current rendering statistics from UnityStats when available. If Unity's internal stats API is unavailable, returns available=false with diagnostics and next_actions.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"available\":{\"type\":\"boolean\"},\"stats\":{\"type\":\"object\",\"properties\":{\"batches\":{\"type\":\"string\"},\"triangles\":{\"type\":\"string\"},\"vertices\":{\"type\":\"string\"},\"screenResolution\":{\"type\":\"string\"}}},\"diagnostics\":{\"type\":\"string\"},\"next_actions\":{\"type\":\"array\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneRenderingStats
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.info", Description = "Read Prefab asset metadata, dependencies, variants, and nested references.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"prefab\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabInfoContext
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.hierarchy", Description = "Read a Prefab hierarchy without modifying Prefab Stage.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\"},\"cursor\":{\"type\":\"integer\"}},\"required\":[\"path\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"prefab\":{\"type\":\"object\"},\"items\":{\"type\":\"array\"},\"pageInfo\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabHierarchyContext
                },
                new BuiltInContextDefinition
                {
                    Name = "instances.list", Description = "Read the current Bridge instance summary from Unity.",
                    Category = "instances", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "always", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"instances\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadInstancesListContext
                },
                new BuiltInContextDefinition
                {
                    Name = "tools.groups", Description = "Read built-in and extension tool groups with counts from the local CLI registry; no Unity state is modified.",
                    Category = "tools", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "always", Handler = "offline",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"groups\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadToolsGroupsContext
                },
                new BuiltInContextDefinition
                {
                    Name = "tools.custom", Description = "Read custom Unity project tools discovered through UnityCliTool attributes.",
                    Category = "tools", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"tools\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadToolsCustomContext
                },
                // ── Tests contexts ──
                new BuiltInContextDefinition
                {
                    Name = "tests.list", Description = "List all test fixtures and test methods in the project.",
                    Category = "tests", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"EditMode\",\"PlayMode\"]},\"pageSize\":{\"type\":\"integer\"},\"cursor\":{\"type\":\"string\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"available\":{\"type\":\"boolean\"},\"tests\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"},\"diagnostics\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadTestsList
                }
            };
        }

        // ── Built-in context lookup (used by ProjectContextRegistry) ──

        internal static BuiltInContextDefinition FindBuiltInContext(string name)
        {
            foreach (var ctx in BuiltInContexts)
            {
                if (string.Equals(ctx.Name, name, StringComparison.Ordinal))
                {
                    return ctx;
                }
            }
            return null;
        }

        private static string ContextDescriptorJson(BuiltInContextDefinition ctx)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("name", ctx.Name)
                .Prop("description", ctx.Description)
                .Prop("source", ctx.Source)
                .Prop("category", ctx.Category)
                .Prop("refreshBehavior", ctx.RefreshBehavior)
                .Prop("target", ctx.Target)
                .Prop("availabilityScope", ctx.AvailabilityScope)
                .Prop("playModeSupport", "supported")
                .Prop("supportStatus", string.Equals(ctx.Handler, "offline", StringComparison.Ordinal) ? "cli-only" : "live-bridge");
            w.PropRaw("inputSchema", ctx.InputSchema ?? "{}")
                .PropRaw("outputSchema", ctx.OutputSchema ?? "{}")
                .Prop("cliCommand", "context read " + ctx.Name);
            if (ctx.RequiresPackages != null && ctx.RequiresPackages.Length > 0)
            {
                w.PropArray("requiresPackages", arr => { foreach (var pkg in ctx.RequiresPackages) { arr.Value(pkg); } });
            }
            w.EndObject();
            return w.ToString();
        }

        // ── Read handlers: Project ──

        private static string ReadProjectInfo(BridgeRequest request)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var projectName = Path.GetFileName(projectRoot);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("projectRoot", projectRoot)
                .Prop("projectName", projectName)
                .Prop("unityVersion", Application.unityVersion)
                .Prop("platform", Application.platform.ToString())
                .Prop("assetsPath", Application.dataPath)
                .EndObject()
                .ToString());
        }

        private static string ReadProjectTags(BridgeRequest request)
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("tags", arr => { foreach (var tag in tags) { arr.Value(tag); } })
                .Prop("count", (long)tags.Length)
                .EndObject()
                .ToString());
        }

        private static string ReadProjectLayers(BridgeRequest request)
        {
            var count = 0;
            for (var i = 0; i < 32; i++) { if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) count++; }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("layers", arr =>
                {
                    for (var i = 0; i < 32; i++)
                    {
                        var name = LayerMask.LayerToName(i);
                        if (!string.IsNullOrEmpty(name))
                        {
                            arr.BeginObject().Prop("index", (long)i).Prop("name", name).EndObject();
                        }
                    }
                })
                .Prop("count", (long)count)
                .EndObject()
                .ToString());
        }

        // ── Read handlers: Editor ──

        private static string ReadEditorStateContext(BridgeRequest request)
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

        // editor.state Context envelope：schemaVersion + observedAt + state + 可选 timedOut。
        private static string EditorStateContextEnvelopeJson(string stateJson, bool timedOut)
        {
            var w = new UnityCliJsonWriter().BeginObject()
                .Prop("schemaVersion", "1.0")
                .Prop("observedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .PropRaw("state", stateJson);
            if (timedOut) w.Prop("timedOut", true).Prop("diagnostics", "wait-ready timed out before Unity reached idle; returned the latest snapshot.");
            return w.EndObject().ToString();
        }

        private static string ReadEditorSelection(BridgeRequest request)
        {
            var activeObject = Selection.activeObject;
            var activeGameObject = Selection.activeGameObject;
            var activeTransform = Selection.activeTransform;
            var objects = Selection.objects ?? new UnityEngine.Object[0];
            var gameObjects = Selection.gameObjects ?? new GameObject[0];
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("activeObject", activeObject != null ? activeObject.name : null)
                .Prop("activeGameObject", activeGameObject != null ? activeGameObject.name : null)
                .Prop("activeTransform", activeTransform != null ? activeTransform.name : null)
                .PropRaw("activeObjectDetail", SelectionObjectJson(activeObject))
                .PropRaw("activeGameObjectDetail", activeGameObject != null ? SelectionObjectJson(activeGameObject) : "null")
                .PropRaw("activeTransformDetail", activeTransform != null ? SelectionObjectJson(activeTransform) : "null")
                .PropArray("objects", arr => { foreach (var obj in objects) { arr.Value(obj != null ? obj.name : null); } })
                .PropArray("objectDetails", arr => { foreach (var obj in objects) { arr.RawValue(SelectionObjectJson(obj)); } })
                .PropArray("gameObjects", arr => { foreach (var go in gameObjects) { arr.Value(go != null ? go.name : null); } })
                .PropArray("gameObjectDetails", arr => { foreach (var go in gameObjects) { if (go != null) arr.RawValue(SelectionObjectJson(go)); } })
                .PropArray("assetGUIDs", arr => { foreach (var obj in objects) { arr.Value(SelectionAssetGuid(obj)); } })
                .EndObject()
                .ToString());
        }

        // 将选中对象转成后续工具可直接复用的稳定引用摘要；只读，不修改 Selection 或资产。
        private static string SelectionObjectJson(UnityEngine.Object obj)
        {
            if (obj == null) return "null";
            var assetPath = NormalizeAssetPath(AssetDatabase.GetAssetPath(obj));
            var assetGuid = SelectionAssetGuid(obj);
            var gameObject = SelectionObjectGameObject(obj);
            var writer = new UnityCliJsonWriter().BeginObject()
                .Prop("name", obj.name)
                .Prop("instanceId", (long)obj.GetInstanceID())
                .Prop("type", obj.GetType().Name)
                .Prop("fullName", obj.GetType().FullName)
                .Prop("assetPath", assetPath)
                .Prop("assetGUID", assetGuid);
            if (gameObject != null)
            {
                writer.Prop("hierarchyPath", HierarchyPath(gameObject))
                    .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                    .PropRaw("transform", TransformJson(gameObject.transform))
                    .PropRaw("components", ComponentsJson(gameObject));
            }
            else
            {
                writer.Prop("hierarchyPath", null)
                    .PropRaw("gameObject", "null")
                    .PropRaw("transform", "null")
                    .PropRaw("components", "[]");
            }
            return writer.EndObject().ToString();
        }

        // 从任意 Unity 对象解析其所属 GameObject；资产对象返回 null。
        private static GameObject SelectionObjectGameObject(UnityEngine.Object obj)
        {
            if (obj is GameObject gameObject) return gameObject;
            if (obj is Component component) return component.gameObject;
            return null;
        }

        // 读取选中资产 GUID；场景对象或非资产对象返回 null。
        private static string SelectionAssetGuid(UnityEngine.Object obj)
        {
            if (obj == null) return null;
            var assetPath = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(assetPath)) return null;
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? null : guid;
        }

        private static string ReadEditorActiveTool(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("activeTool", Tools.current.ToString())
                .Prop("isCustom", Tools.current != Tool.Move && Tools.current != Tool.Rotate && Tools.current != Tool.Scale && Tools.current != Tool.Rect && Tools.current != Tool.None)
                .Prop("pivotMode", Tools.pivotMode.ToString())
                .Prop("pivotRotation", Tools.pivotRotation.ToString())
                .PropRaw("handlePosition", Vector3Json(Tools.handlePosition))
                .PropRaw("handleRotation", QuaternionJson(Tools.handleRotation))
                .EndObject()
                .ToString());
        }

        private static string QuaternionJson(Quaternion q)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("x", (double)q.x)
                .Prop("y", (double)q.y)
                .Prop("z", (double)q.z)
                .Prop("w", (double)q.w)
                .Prop("eulerAngles", Vector3Json(q.eulerAngles))
                .EndObject()
                .ToString();
        }

        private static string ReadEditorWindows(BridgeRequest request)
        {
            var windows = Resources.FindObjectsOfTypeAll<EditorWindow>();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("windows", arr =>
                {
                    foreach (var window in windows)
                    {
                        if (window == null) continue;
                        arr.BeginObject()
                            .Prop("title", window.titleContent.text)
                            .Prop("typeName", window.GetType().Name)
                            .Prop("fullName", window.GetType().FullName)
                            .Prop("isFocused", EditorWindow.focusedWindow == window)
                            .Prop("instanceID", (long)window.GetInstanceID())
                            .EndObject();
                    }
                })
                .Prop("count", (long)windows.Length)
                .EndObject()
                .ToString());
        }

        private static string ReadEditorToolStates(BridgeRequest request)
        {
            // Read tool states via reflection where possible; unavailable fields get diagnostics
            var snapSettingsType = FindType("UnityEditor.EditorSnapSettings");
            var moveSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapMoveEnabled") : null;
            var rotationSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapRotationEnabled") : null;
            var scaleSnapEnabled = snapSettingsType != null ? BoolMemberValue(snapSettingsType, null, "snapScaleEnabled") : null;
            var moveSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "moveSnap") : null;
            var rotationSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "rotationSnap") : null;
            var scaleSnap = snapSettingsType != null ? FloatMemberValue(snapSettingsType, null, "scaleSnap") : null;

            var w = new UnityCliJsonWriter().BeginObject()
                .PropObject("transform", obj => obj
                    .Prop("activeTool", Tools.current.ToString())
                    .Prop("pivotMode", Tools.pivotMode.ToString())
                    .Prop("pivotRotation", Tools.pivotRotation.ToString()))
                .PropObject("snap", obj => obj
                    .Prop("moveSnapEnabled", moveSnapEnabled ?? false)
                    .Prop("rotationSnapEnabled", rotationSnapEnabled ?? false)
                    .Prop("scaleSnapEnabled", scaleSnapEnabled ?? false)
                    .Prop("moveSnap", moveSnapEnabled.HasValue && moveSnapEnabled.Value && moveSnap.HasValue ? moveSnap.Value : 0f)
                    .Prop("rotationSnap", rotationSnapEnabled.HasValue && rotationSnapEnabled.Value && rotationSnap.HasValue ? rotationSnap.Value : 0f)
                    .Prop("scaleSnap", scaleSnapEnabled.HasValue && scaleSnapEnabled.Value && scaleSnap.HasValue ? scaleSnap.Value : 0f))
                .Prop("gridAvailable", snapSettingsType != null)
                .EndObject();
            return SuccessJson(w.ToString());
        }

        private static string ReadPrefabStage(BridgeRequest request)
        {
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("isOpen", false)
                    .Prop("assetPath", null)
                    .Prop("prefabRootName", null)
                    .Prop("mode", null)
                    .Prop("isDirty", false)
                    .EndObject()
                    .ToString());
            }
            var type = prefabStage.GetType();
            var assetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString();
            var openMode = type.GetProperty("mode")?.GetValue(prefabStage);
            var modeStr = openMode != null ? openMode.ToString() : null;
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            var isDirty = sceneValue is Scene scene ? scene.isDirty : false;
            var root = type.GetProperty("prefabContentsRoot")?.GetValue(prefabStage);
            var rootName = root is GameObject go ? go.name : null;

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("isOpen", true)
                .Prop("assetPath", assetPath)
                .Prop("prefabRootName", rootName)
                .Prop("mode", modeStr)
                .Prop("isDirty", isDirty)
                .EndObject()
                .ToString());
        }

        private static string ReadEditorMenuItems(BridgeRequest request)
        {
            // Unity does not expose a public API to enumerate all menu items.
            // Reflection on internal menu caches is version-dependent and fragile.
            // This context returns diagnostics explaining the limitation;
            // it does NOT execute any menu items.
            var search = request.StringArg("search");
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("items", arr => { /* Unity internal menu cache not reliably accessible */ })
                .Prop("count", (long)0)
                .Prop("diagnostics", "Unity Editor menu items cannot be enumerated through a public API. Reflection on internal MenuItem cache is version-dependent and not guaranteed. This context returns an empty list; menu execution is a Tool (editor.menu), not a Context.")
                .EndObject()
                .ToString());
        }

        // ── Read handlers: Scene ──

        private static string ReadSceneGameObject(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("name");
            var id = request.OptionalIntArg("id");
            var path = request.StringArg("path");
            var includeComponents = request.BoolArg("includeComponents", true);

            // Priority: id > path > target
            GameObject gameObject = null;
            if (id.HasValue)
            {
                gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
            }
            else if (!string.IsNullOrEmpty(path))
            {
                gameObject = GameObject.Find(path);
            }
            else if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Matches.Count == 1)
                {
                    gameObject = resolution.Target;
                }
                else if (resolution.Matches.Count > 1)
                {
                    return ErrorJson("E_TARGET_AMBIGUOUS", $"Multiple GameObjects matched: {target}", "Pass a hierarchy path or instance id for disambiguation.");
                }
            }

            if (gameObject == null)
            {
                var hint = !string.IsNullOrEmpty(target) || id.HasValue || !string.IsNullOrEmpty(path)
                    ? "GameObject not found. Pass a name, hierarchy path or instance id."
                    : "A target is required: pass input target, id, or path.";
                return ErrorJson("E_TARGET_NOT_FOUND", hint, "Run unity_cli tools run scene.hierarchy --input '{\"pageSize\":25}' to see available GameObjects.");
            }

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", includeComponents ? GameObjectDetailJson(gameObject) : GameObjectTargetJson(gameObject))
                .EndObject()
                .ToString());
        }

        private static string ReadSceneOpened(BridgeRequest request)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            w.PropArray("scenes", arr =>
            {
                for (var i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    arr.BeginObject()
                        .Prop("name", scene.name)
                        .Prop("path", NormalizeAssetPath(scene.path))
                        .Prop("buildIndex", (long)scene.buildIndex)
                        .Prop("isLoaded", scene.isLoaded)
                        .Prop("isDirty", scene.isDirty)
                        .EndObject();
                }
            });
            w.Prop("count", (long)SceneManager.sceneCount);
            w.Prop("activeScene", SceneManager.GetActiveScene().name);
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static string ReadSceneGameObjectComponents(BridgeRequest request)
        {
            var gameObject = ResolveContextGameObject(request);
            var includeProperties = request.BoolArg("includeProperties", true);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("components", includeProperties ? ComponentDetailsJson(gameObject) : ComponentsJson(gameObject))
                .Prop("count", (long)gameObject.GetComponents<Component>().Length)
                .EndObject()
                .ToString());
        }

        private static string ReadSceneGameObjectComponent(BridgeRequest request)
        {
            var gameObject = ResolveContextGameObject(request);
            var componentName = request.StringArg("component") ?? request.StringArg("name");
            if (string.IsNullOrEmpty(componentName))
            {
                return ErrorJson("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass --input '{\"target\":\"Player\",\"component\":\"Transform\"}'.");
            }
            var component = ResolveComponent(gameObject, componentName);
            if (component == null)
            {
                return ErrorJson("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run context read scene.gameobject.components with the same target/path/id to list available component names.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("component", ComponentDetailJson(component))
                .EndObject()
                .ToString());
        }

        private static string ReadSceneCameras(BridgeRequest request)
        {
            // Reuse existing CameraListJson()
            return SuccessJson(CameraListJson());
        }

        private static string ReadSceneVolumes(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var pageSize = request.IntArg("pageSize", 100);
            var volumesJson = GraphicsVolumesJson(target);

            // GraphicsVolumesJson returns a JSON array; wrap with pageInfo
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("volumes", volumesJson)
                .Prop("count", (long)CountTopLevelJsonArrayItems(volumesJson))
                .PropObject("pageInfo", obj => obj
                    .Prop("hasMore", false)
                    .Prop("pageSize", (long)pageSize))
                .EndObject()
                .ToString());
        }

        private static string ReadSceneRendererFeatures(BridgeRequest request)
        {
            // Requires URP package; if not available, return available=false + diagnostics
            var urpAssetType = FindType("UnityEngine.Rendering.Universal.RendererData");
            var urpAssetType2 = FindType("UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset");
            var featureType = FindType("UnityEngine.Rendering.Universal.ScriptableRendererFeature");

            if (urpAssetType2 == null && urpAssetType == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("available", false)
                    .PropArray("features", arr => { })
                    .Prop("diagnostics", "URP package (com.unity.render-pipelines.universal) is not installed. Renderer features context requires URP.")
                    .PropArray("next_actions", arr => arr.Value("Install the URP package via Package Manager."))
                    .EndObject()
                    .ToString());
            }

            // Try to enumerate renderer features via reflection
            var pipelineAsset = GraphicsSettings.defaultRenderPipeline;
            if (pipelineAsset == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("available", false)
                    .PropArray("features", arr => { })
                    .Prop("diagnostics", "No URP render pipeline asset is assigned in Graphics Settings.")
                    .PropArray("next_actions", arr => arr.Value("Assign a UniversalRenderPipelineAsset in Graphics Settings."))
                    .EndObject()
                    .ToString());
            }

            // Get renderer list from pipeline asset
            var rendererDataListProp = pipelineAsset.GetType().GetProperty("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? pipelineAsset.GetType().GetProperty("rendererDataList", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var rendererList = rendererDataListProp?.GetValue(pipelineAsset) as IEnumerable<UnityEngine.Object>;

            var features = new List<string>();
            if (rendererList != null)
            {
                foreach (var rendererObj in rendererList)
                {
                    if (rendererObj == null) continue;
                    features.Add(rendererObj.name + " (" + rendererObj.GetType().Name + ")");
                }
            }

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("available", true)
                .Prop("renderer", pipelineAsset.name)
                .PropArray("features", arr => { foreach (var f in features) { arr.Value(f); } })
                .Prop("count", (long)features.Count)
                .EndObject()
                .ToString());
        }

        private static string ReadSceneRenderingStats(BridgeRequest request)
        {
            // UnityStats is an internal class; try reflection
            var statsType = FindType("UnityEditor.UnityStats");
            if (statsType == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("available", false)
                    .Prop("diagnostics", "UnityStats internal API not available in this Unity version. Rendering stats cannot be read.")
                    .PropArray("next_actions", arr => arr.Value("Rendering stats may be available through the Stats overlay window in the Game view."))
                    .EndObject()
                    .ToString());
            }

            // Read stats properties via reflection
            var batches = StringMemberValue(statsType, null, "batches");
            var triangles = StringMemberValue(statsType, null, "triangles");
            var vertices = StringMemberValue(statsType, null, "vertices");
            var screenRes = StringMemberValue(statsType, null, "screenRes");

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("available", true)
                .PropObject("stats", obj => obj
                    .Prop("batches", batches)
                    .Prop("triangles", triangles)
                    .Prop("vertices", vertices)
                    .Prop("screenResolution", screenRes))
                .EndObject()
                .ToString());
        }

        private static string ReadPrefabInfoContext(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            var prefab = LoadPrefabAsset(path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("prefab", PrefabAssetJson(path, prefab))
                .EndObject()
                .ToString());
        }

        private static string ReadPrefabHierarchyContext(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var pageSize = Math.Max(1, request.IntArg("pageSize", 100));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var items = new List<string>();
                var total = CountHierarchy(root.transform);
                CollectHierarchy(root.transform, items, cursor, pageSize, new Counter());
                var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("prefab", PrefabAssetJson(path, root, root))
                    .PropArray("items", arr => { foreach (var item in items) { arr.RawValue(item); } })
                    .PropObject("pageInfo", pageInfo => pageInfo
                        .Prop("pageSize", (long)pageSize)
                        .Prop("cursor", (long)cursor)
                        .Prop("nextCursor", nextCursor)
                        .Prop("total", (long)total))
                    .EndObject()
                    .ToString());
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string ReadInstancesListContext(BridgeRequest request)
        {
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("instances", arr => arr.BeginObject()
                    .Prop("instanceId", ListenerId)
                    .Prop("projectPath", ProjectRoot())
                    .Prop("unityVersion", Application.unityVersion)
                    .Prop("bridgeEndpoint", "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture))
                    .Prop("status", "ready")
                    .EndObject())
                .Prop("count", 1L)
                .EndObject()
                .ToString());
        }

        private static string ReadToolsGroupsContext(BridgeRequest request)
        {
            var groups = new Dictionary<string, int>(StringComparer.Ordinal);
            AddToolGroupCounts(groups, "camera", CameraCommands);
            AddToolGroupCounts(groups, "scene", SceneCommands);
            AddToolGroupCounts(groups, "go", GameObjectCommands);
            AddToolGroupCounts(groups, "asset", AssetCommands);
            AddToolGroupCounts(groups, "prefab", PrefabCommands);
            AddToolGroupCounts(groups, "physics", PhysicsCommands);
            AddToolGroupCounts(groups, "editor", EditorCommands);
            AddToolGroupCounts(groups, "scriptable-object", ScriptableObjectCommands);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("groups", arr => { foreach (var pair in groups.OrderBy(p => p.Key, StringComparer.Ordinal)) { arr.BeginObject().Prop("name", pair.Key).Prop("toolCount", (long)pair.Value).Prop("cliCommand", "tools group list " + pair.Key).EndObject(); } })
                .Prop("count", (long)groups.Count)
                .EndObject()
                .ToString());
        }

        private static string ReadToolsCustomContext(BridgeRequest request)
        {
            // 只读发现项目自定义工具，并只暴露 AI-facing descriptor 字段。
            var toolsJson = ProjectToolPublicDiscoveryJson();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("tools", toolsJson)
                .Prop("count", (long)CountTopLevelJsonArrayItems(toolsJson))
                .EndObject()
                .ToString());
        }

        private static string ReadTestsList(BridgeRequest request)
        {
            // Try to find Unity Test Runner API via reflection
            var testRunnerType = FindType("UnityEditor.TestTools.TestRunner.TestRunnerWindow")
                ?? FindType("UnityEditor.TestRunner.TestRunnerWindow");
            var editorTestType = FindType("NUnit.Framework.TestAttribute");

            if (testRunnerType == null && editorTestType == null)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("available", false)
                    .PropArray("tests", arr => { })
                    .Prop("count", (long)0)
                    .Prop("diagnostics", "Unity Test Framework is not installed or not available. Test enumeration requires the Test Runner package.")
                    .PropArray("next_actions", arr =>
                    {
                        arr.Value("Install the Unity Test Framework package via Package Manager.");
                        arr.Value("Enable the Test Runner window: Window > General > Test Runner.");
                    })
                    .EndObject()
                    .ToString());
            }

            // Test enumeration via reflection is complex and version-dependent
            // Return available=true but empty list with diagnostic explanation
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("available", true)
                .PropArray("tests", arr => { })
                .Prop("count", (long)0)
                .Prop("diagnostics", "Test enumeration via reflection is not reliably available. Use the Unity Test Runner window for full test discovery, or run tests via the CLI test runner tool.")
                .EndObject()
                .ToString());
        }

        // ── Helper methods ──

        private static GameObject ResolveContextGameObject(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("name");
            var id = request.OptionalIntArg("id");
            var path = request.StringArg("path");
            GameObject gameObject = null;
            if (id.HasValue) gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
            else if (!string.IsNullOrEmpty(path)) gameObject = GameObject.Find(path);
            else if (!string.IsNullOrEmpty(target)) gameObject = ResolveUniqueTarget(target, "Context target is required.");
            if (gameObject == null)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass input target, path, or id; use tools run scene.hierarchy to discover paths.");
            }
            return gameObject;
        }

        private static string ComponentDetailsJson(GameObject gameObject)
        {
            var writer = new UnityCliJsonWriter().BeginArray();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                writer.RawValue(ComponentDetailJson(component));
            }
            return writer.EndArray().ToString();
        }

        private static string ComponentDetailJson(Component component)
        {
            if (component == null) return ComponentSummaryJson(component);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("type", component.GetType().Name)
                .Prop("fullName", component.GetType().FullName)
                .Prop("enabled", ComponentEnabled(component))
                .PropRaw("properties", ComponentReadablePropertiesJson(component))
                .EndObject()
                .ToString();
        }

        private static string ComponentReadablePropertiesJson(Component component)
        {
            var writer = new UnityCliJsonWriter().BeginObject();
            foreach (var property in component.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try
                {
                    var value = property.GetValue(component, null);
                    writer.Prop(property.Name, value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture));
                }
                catch { }
            }
            return writer.EndObject().ToString();
        }

        private static void AddToolGroupCounts(Dictionary<string, int> groups, string name, string[] commands)
        {
            groups[name] = commands == null ? 0 : commands.Length;
        }

        // 统计顶层 JSON 数组元素数量，用于复用已有数组序列化 helper 后补稳定 count 字段。
        private static int CountTopLevelJsonArrayItems(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return 0;
            var trimmed = json.Trim();
            if (trimmed == "[]") return 0;
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) || !trimmed.EndsWith("]", StringComparison.Ordinal)) return 0;
            var depth = 0;
            var count = 0;
            var inString = false;
            var escaped = false;
            var hasValue = false;
            for (var i = 1; i < trimmed.Length - 1; i++)
            {
                var ch = trimmed[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; hasValue = true; continue; }
                if (ch == '{' || ch == '[') { depth++; hasValue = true; continue; }
                if (ch == '}' || ch == ']') { depth--; continue; }
                if (depth == 0 && ch == ',') { count++; continue; }
                if (!char.IsWhiteSpace(ch)) hasValue = true;
            }
            return hasValue ? count + 1 : 0;
        }

        private static string ExtractJsonProperty(string json, string name)
        {
            var marker = "\"" + name + "\":";
            var index = (json ?? string.Empty).IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return null;
            var start = index + marker.Length;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < json.Length; i++)
            {
                var ch = json[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                if (ch == '"') { inString = true; continue; }
                if (ch == '{' || ch == '[') depth++;
                if (ch == '}' || ch == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, i - start + 1);
                }
            }
            return null;
        }

        private static bool? BoolMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return Convert.ToBoolean(prop.GetValue(instance, null), CultureInfo.InvariantCulture); } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return Convert.ToBoolean(field.GetValue(instance), CultureInfo.InvariantCulture); } catch { return null; }
            }
            return null;
        }

        private static float? FloatMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return Convert.ToSingle(prop.GetValue(instance, null), CultureInfo.InvariantCulture); } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return Convert.ToSingle(field.GetValue(instance), CultureInfo.InvariantCulture); } catch { return null; }
            }
            return null;
        }

        private static string StringMemberValue(Type type, object instance, string memberName)
        {
            var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                try { return prop.GetValue(instance, null) as string; } catch { return null; }
            }
            var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                try { return field.GetValue(instance) as string; } catch { return null; }
            }
            return null;
        }
    }
}
#endif
