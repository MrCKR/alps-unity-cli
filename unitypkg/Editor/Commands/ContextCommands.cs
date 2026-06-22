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
            public string Title;
            public string Description;
            public string Category;
            public string Source;
            public string RefreshBehavior;
            public string Target;
            public string AvailabilityScope;
            public string ResourceUri;
            public string EquivalentUnityMcp;
            public string EquivalentUnityMcpNote;
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
                    Name = "project.info", Title = "Project Info", Description = "Project metadata: name, path, Unity version, and overall project structure.",
                    Category = "project", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://project/info", EquivalentUnityMcp = "mcpforunity://project/info", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"projectRoot\":{\"type\":\"string\"},\"projectName\":{\"type\":\"string\"},\"unityVersion\":{\"type\":\"string\"},\"platform\":{\"type\":\"string\"},\"assetsPath\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectInfo
                },
                new BuiltInContextDefinition
                {
                    Name = "project.tags", Title = "Project Tags", Description = "List all tags defined in the project's Tag Manager.",
                    Category = "project", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://project/tags", EquivalentUnityMcp = "mcpforunity://project/tags", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectTags
                },
                new BuiltInContextDefinition
                {
                    Name = "project.layers", Title = "Project Layers", Description = "List all layers defined in the project's Tag Manager.",
                    Category = "project", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://project/layers", EquivalentUnityMcp = "mcpforunity://project/layers", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"layers\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"index\":{\"type\":\"integer\"},\"name\":{\"type\":\"string\"}}}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadProjectLayers
                },
                // ── Editor contexts ──
                new BuiltInContextDefinition
                {
                    Name = "editor.state", Title = "Editor State", Description = "Current editor state: compilation state, play mode, and general editor status.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/state", EquivalentUnityMcp = "mcpforunity://editor/state", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"schemaVersion\":{\"type\":\"string\"},\"observedAt\":{\"type\":\"string\"},\"state\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorStateContext
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.selection", Title = "Editor Selection", Description = "Currently selected objects in the Unity Editor hierarchy or project browser.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/selection", EquivalentUnityMcp = "mcpforunity://editor/selection", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"activeObject\":{\"type\":\"string\"},\"activeGameObject\":{\"type\":\"string\"},\"activeTransform\":{\"type\":\"string\"},\"objects\":{\"type\":\"array\"},\"gameObjects\":{\"type\":\"array\"},\"assetGUIDs\":{\"type\":\"array\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorSelection
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.active-tool", Title = "Active Tool", Description = "Currently active editor tool (move, rotate, scale, rect, etc.).",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/active-tool", EquivalentUnityMcp = "mcpforunity://editor/active-tool", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"activeTool\":{\"type\":\"string\"},\"isCustom\":{\"type\":\"boolean\"},\"pivotMode\":{\"type\":\"string\"},\"pivotRotation\":{\"type\":\"string\"},\"handlePosition\":{\"type\":\"object\"},\"handleRotation\":{\"type\":\"object\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorActiveTool
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.windows", Title = "Editor Windows", Description = "List currently open editor windows and their types.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/windows", EquivalentUnityMcp = "mcpforunity://editor/windows", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"windows\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"typeName\":{\"type\":\"string\"},\"isFocused\":{\"type\":\"boolean\"}}}},\"count\":{\"type\":\"integer\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorWindows
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.tool-states", Title = "Tool States", Description = "Current state of editor tools (enabled/disabled, snap, grid settings).",
                    Category = "editor", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/tool-states", EquivalentUnityMcp = "none",
                    EquivalentUnityMcpNote = "unity-mcp 'get_tool_states' (mcpforunity://editor/tool-states) returns MCP tool discovery/enabled state, not Unity Editor tool settings. No direct unity-mcp equivalent exists for this context.",
                    Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"transform\":{\"type\":\"object\"},\"snap\":{\"type\":\"object\"},\"grid\":{\"type\":\"boolean\"},\"activeTool\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorToolStates
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.stage", Title = "Prefab Stage", Description = "Current Prefab Mode / Prefab Stage state if a prefab is being edited.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://prefab/stage", EquivalentUnityMcp = "mcpforunity://editor/prefab-stage", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"isOpen\":{\"type\":\"boolean\"},\"assetPath\":{\"type\":\"string\"},\"prefabRootName\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\"},\"isDirty\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabStage
                },
                new BuiltInContextDefinition
                {
                    Name = "editor.menu-items", Title = "Menu Items", Description = "Enumerate available menu items in the Unity Editor.",
                    Category = "editor", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://editor/menu-items", EquivalentUnityMcp = "mcpforunity://menu-items", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"search\":{\"type\":\"string\",\"description\":\"Optional filter substring.\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"items\":{\"type\":\"array\",\"items\":{\"type\":\"object\"}},\"count\":{\"type\":\"integer\"},\"diagnostics\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadEditorMenuItems
                },
                // ── Scene contexts ──
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject", Title = "Scene GameObject", Description = "Read a specific GameObject's properties from the active scene.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/gameobject/{instance_id}", EquivalentUnityMcp = "mcpforunity://scene/gameobject/{instance_id}", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\",\"description\":\"GameObject name or path.\"},\"id\":{\"type\":\"integer\",\"description\":\"Instance ID.\"},\"path\":{\"type\":\"string\",\"description\":\"Hierarchy path.\"},\"includeComponents\":{\"type\":\"boolean\",\"description\":\"Include component summaries.\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObject
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject.components", Title = "Scene GameObject Components", Description = "Read all components attached to a GameObject.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/gameobject/{instance_id}/components", EquivalentUnityMcp = "mcpforunity://scene/gameobject/{id}/components", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"},\"includeProperties\":{\"type\":\"boolean\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"},\"components\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObjectComponents
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.gameobject.component", Title = "Scene GameObject Component", Description = "Read one component attached to a GameObject.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/gameobject/{instance_id}/component/{name}", EquivalentUnityMcp = "mcpforunity://scene/gameobject/{id}/component/{name}", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"id\":{\"type\":\"integer\"},\"path\":{\"type\":\"string\"},\"component\":{\"type\":\"string\"}},\"required\":[\"component\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"gameObject\":{\"type\":\"object\"},\"component\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneGameObjectComponent
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.cameras", Title = "Scene Cameras", Description = "List all cameras in the active scene with their key properties.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/cameras", EquivalentUnityMcp = "mcpforunity://scene/cameras", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"camerasData\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneCameras
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.volumes", Title = "Scene Volumes", Description = "List all volume components in the active scene.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/volumes", EquivalentUnityMcp = "mcpforunity://scene/volumes", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"target\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\"},\"cursor\":{\"type\":\"string\"}}}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"volumes\":{\"type\":\"array\"},\"pageInfo\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneVolumes
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.renderer-features", Title = "Renderer Features", Description = "List URP renderer features configured in the project.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/renderer-features", EquivalentUnityMcp = "mcpforunity://pipeline/renderer-features", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"available\":{\"type\":\"boolean\"},\"features\":{\"type\":\"array\"},\"diagnostics\":{\"type\":\"string\"}}}",
                    RequiresPackages = new[] { "com.unity.render-pipelines.universal" }, ReadHandler = ReadSceneRendererFeatures
                },
                new BuiltInContextDefinition
                {
                    Name = "scene.rendering-stats", Title = "Rendering Stats", Description = "Current rendering statistics from the active scene camera.",
                    Category = "scene", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://scene/rendering-stats", EquivalentUnityMcp = "mcpforunity://rendering/stats", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"available\":{\"type\":\"boolean\"},\"stats\":{\"type\":\"object\"},\"diagnostics\":{\"type\":\"string\"}}}",
                    RequiresPackages = null, ReadHandler = ReadSceneRenderingStats
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.info", Title = "Prefab Info", Description = "Read Prefab asset metadata, dependencies, variants, and nested references.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://prefab/{path}", EquivalentUnityMcp = "mcpforunity://prefab/{path}", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"prefab\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabInfoContext
                },
                new BuiltInContextDefinition
                {
                    Name = "prefab.hierarchy", Title = "Prefab Hierarchy", Description = "Read a Prefab hierarchy without modifying Prefab Stage.",
                    Category = "prefab", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://prefab/{path}/hierarchy", EquivalentUnityMcp = "mcpforunity://prefab/{path}/hierarchy", Handler = "bridge",
                    InputSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"},\"pageSize\":{\"type\":\"integer\"},\"cursor\":{\"type\":\"integer\"}},\"required\":[\"path\"]}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"prefab\":{\"type\":\"object\"},\"items\":{\"type\":\"array\"},\"pageInfo\":{\"type\":\"object\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadPrefabHierarchyContext
                },
                new BuiltInContextDefinition
                {
                    Name = "instances.list", Title = "Unity Instances", Description = "Read the current Bridge instance summary from Unity.",
                    Category = "instances", Source = "built-in", RefreshBehavior = "live", Target = "editor", AvailabilityScope = "always",
                    ResourceUri = "unity-cli://instances/list", EquivalentUnityMcp = "mcpforunity://instances", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"instances\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadInstancesListContext
                },
                new BuiltInContextDefinition
                {
                    Name = "tools.groups", Title = "Tool Groups", Description = "Read built-in bridge tool groups and command counts.",
                    Category = "tools", Source = "built-in", RefreshBehavior = "static", Target = "editor", AvailabilityScope = "always",
                    ResourceUri = "unity-cli://tools/groups", EquivalentUnityMcp = "mcpforunity://tool-groups", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"groups\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadToolsGroupsContext
                },
                new BuiltInContextDefinition
                {
                    Name = "tools.custom", Title = "Custom Tools", Description = "Read custom Unity project tools discovered through UnityCliTool attributes.",
                    Category = "tools", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://tools/custom", EquivalentUnityMcp = "mcpforunity://custom-tools", Handler = "bridge",
                    InputSchema = "{}",
                    OutputSchema = "{\"type\":\"object\",\"properties\":{\"tools\":{\"type\":\"array\"},\"count\":{\"type\":\"integer\"},\"readOnlyHint\":{\"type\":\"boolean\"}}}",
                    RequiresPackages = null, ReadHandler = ReadToolsCustomContext
                },
                // ── Tests contexts ──
                new BuiltInContextDefinition
                {
                    Name = "tests.list", Title = "Test List", Description = "List all test fixtures and test methods in the project.",
                    Category = "tests", Source = "built-in", RefreshBehavior = "on-demand", Target = "editor", AvailabilityScope = "editorSession",
                    ResourceUri = "unity-cli://tests/list", EquivalentUnityMcp = "mcpforunity://tests", Handler = "bridge",
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
                .Prop("title", ctx.Title)
                .Prop("description", ctx.Description)
                .Prop("kind", "context")
                .Prop("source", ctx.Source)
                .Prop("category", ctx.Category)
                .Prop("readOnlyHint", true)
                .Prop("refreshBehavior", ctx.RefreshBehavior)
                .Prop("target", ctx.Target)
                .Prop("availabilityScope", ctx.AvailabilityScope)
                .Prop("resourceUri", ctx.ResourceUri)
                .Prop("equivalentUnityMcp", ctx.EquivalentUnityMcp);
            if (!string.IsNullOrEmpty(ctx.EquivalentUnityMcpNote))
            {
                w.Prop("equivalentUnityMcpNote", ctx.EquivalentUnityMcpNote);
            }
            w.Prop("handler", ctx.Handler)
                .PropRaw("inputSchema", ctx.InputSchema ?? "{}")
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
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadProjectTags(BridgeRequest request)
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("tags", arr => { foreach (var tag in tags) { arr.Value(tag); } })
                .Prop("count", (long)tags.Length)
                .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        // ── Read handlers: Editor ──

        private static string ReadEditorStateContext(BridgeRequest request)
        {
            // Reuse existing EditorStateJson() and wrap with context envelope fields
            var stateJson = EditorStateJson();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("schemaVersion", "1.0")
                .Prop("observedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                .PropRaw("state", stateJson)
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadEditorSelection(BridgeRequest request)
        {
            var activeObject = Selection.activeObject;
            var activeGameObject = Selection.activeGameObject;
            var activeTransform = Selection.activeTransform;
            var objects = Selection.objects;
            var gameObjects = Selection.gameObjects;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("activeObject", activeObject != null ? activeObject.name : null)
                .Prop("activeGameObject", activeGameObject != null ? activeGameObject.name : null)
                .Prop("activeTransform", activeTransform != null ? activeTransform.name : null)
                .PropArray("objects", arr => { if (objects != null) foreach (var obj in objects) { arr.Value(obj != null ? obj.name : null); } })
                .PropArray("gameObjects", arr => { if (gameObjects != null) foreach (var go in gameObjects) { arr.Value(go != null ? go.name : null); } })
                .PropArray("assetGUIDs", arr =>
                {
                    if (objects != null)
                    {
                        foreach (var obj in objects)
                        {
                            if (obj != null)
                            {
                                var assetPath = AssetDatabase.GetAssetPath(obj);
                                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                                arr.Value(string.IsNullOrEmpty(guid) ? null : guid);
                            }
                        }
                    }
                })
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
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
                .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                    .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                    : "A target is required: pass --target, --id or --path.";
                return ErrorJson("E_TARGET_NOT_FOUND", hint, "Run unity_cli scene hierarchy to see available GameObjects.");
            }

            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", includeComponents ? GameObjectDetailJson(gameObject) : GameObjectTargetJson(gameObject))
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadSceneGameObjectComponents(BridgeRequest request)
        {
            var gameObject = ResolveContextGameObject(request);
            var includeProperties = request.BoolArg("includeProperties", true);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("components", includeProperties ? ComponentDetailsJson(gameObject) : ComponentsJson(gameObject))
                .Prop("count", (long)gameObject.GetComponents<Component>().Length)
                .Prop("readOnlyHint", true)
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
                return ErrorJson("E_COMPONENT_NOT_FOUND", "Component not found on target: " + componentName, "Run context read scene.gameobject.components for the target.");
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", GameObjectTargetJson(gameObject))
                .PropRaw("component", ComponentDetailJson(component))
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadSceneCameras(BridgeRequest request)
        {
            // Reuse existing CameraListJson()
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("camerasData", CameraListJson())
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadSceneVolumes(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var pageSize = request.IntArg("pageSize", 100);
            var volumesJson = GraphicsVolumesJson(target);

            // GraphicsVolumesJson returns a JSON array; wrap with pageInfo
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("volumes", volumesJson)
                .PropObject("pageInfo", obj => obj
                    .Prop("hasMore", false)
                    .Prop("pageSize", (long)pageSize))
                .Prop("readOnlyHint", true)
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
                    .Prop("readOnlyHint", true)
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
                    .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                    .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadPrefabInfoContext(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            var prefab = LoadPrefabAsset(path);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("prefab", PrefabAssetJson(path, prefab))
                .Prop("readOnlyHint", true)
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
                    .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
            AddToolGroupCounts(groups, "script", ScriptCommands);
            AddToolGroupCounts(groups, "scriptable-object", ScriptableObjectCommands);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("groups", arr => { foreach (var pair in groups.OrderBy(p => p.Key, StringComparer.Ordinal)) { arr.BeginObject().Prop("name", pair.Key).Prop("toolCount", (long)pair.Value).Prop("cliCommand", "tools group list " + pair.Key).EndObject(); } })
                .Prop("count", (long)groups.Count)
                .Prop("readOnlyHint", true)
                .EndObject()
                .ToString());
        }

        private static string ReadToolsCustomContext(BridgeRequest request)
        {
            DiscoverProjectTools();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("tools", ProjectToolListRawJson())
                .Prop("readOnlyHint", true)
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
                    .Prop("diagnostics", "Unity Test Framework is not installed or not available. Test enumeration requires the Test Runner package.")
                    .PropArray("next_actions", arr =>
                    {
                        arr.Value("Install the Unity Test Framework package via Package Manager.");
                        arr.Value("Enable the Test Runner window: Window > General > Test Runner.");
                    })
                    .Prop("readOnlyHint", true)
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
                .Prop("readOnlyHint", true)
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
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject context target not found.", "Pass target, path, or id.");
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

        private static string ProjectToolListRawJson()
        {
            var response = ProjectToolList(new BridgeRequest("{}", "context_tools_custom", "project-tool.list"));
            var raw = response;
            var data = ExtractJsonProperty(raw, "data");
            var tools = ExtractJsonProperty(data, "tools");
            return string.IsNullOrEmpty(tools) ? "[]" : tools;
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
