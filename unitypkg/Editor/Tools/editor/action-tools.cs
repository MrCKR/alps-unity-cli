#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace UnityCli
{
    /// <summary>editor.menu — 执行 Unity Editor 菜单项。</summary>
    public class EditorMenuTool : IUnityCliTool<EditorMenuTool.Request, EditorMenuTool.Response>
    {
        public sealed class Request { public string Path { get; set; } public int? TimeoutMs { get; set; } }
        public sealed class Response { public MenuResult Result { get; set; } public EditorStateInfo State { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.menu", Summary = "Execute a Unity Editor menu item by path.", Description = "Call EditorApplication.ExecuteMenuItem(path). Pass exact menu path like File/Save.", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Save scene", Input = new Request { Path = "File/Save" } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("editor.menu");
            if (string.IsNullOrWhiteSpace(r.Path)) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Pass an exact Unity menu path like File/Save.");
            var startedAt = DateTime.UtcNow;
            var executed = EditorApplication.ExecuteMenuItem(r.Path);
            if (!executed) throw new UnityCliBridge.BridgeCommandException("E_MENU_NOT_FOUND", "Unity menu item was not found or could not be executed.", "Use the exact Unity Editor menu path.");
            var elapsedMs = (long)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            return new Response { Result = new MenuResult { Path = r.Path, Executed = true, ElapsedMs = elapsedMs }, State = EditorToolSupport.BuildEditorState(), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "editor.menu", Path = r.Path } } };
        }
    }

    /// <summary>editor.compile — 请求脚本编译。</summary>
    public class EditorCompileTool : IUnityCliTool<EditorCompileTool.Request, EditorCompileTool.Result>
    {
        public sealed class Request { public bool? Refresh { get; set; } public bool? WaitReady { get; set; } public int? TimeoutMs { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public CompileResult Compile { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.compile", Summary = "Request script compilation in the live Unity Editor.", Description = "Request CompilationPipeline.RequestScriptCompilation(). refresh=true (default) refreshes assets first. waitReady blocks until idle.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "session-affecting" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Compile scripts", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (BuildPipeline.isBuildingPlayer) throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            var refresh = r.Refresh ?? true;
            var waitReady = r.WaitReady ?? false;
            var wasCompiling = EditorApplication.isCompiling;
            var wasUpdating = EditorApplication.isUpdating;
            var alreadyBusy = wasCompiling || wasUpdating;
            var skippedReason = alreadyBusy ? (wasCompiling ? "already-compiling" : "already-importing") : null;
            var dryRun = r.DryRun ?? false;
            if (dryRun)
            {
                var effects = new List<EditorPlannedEffect>();
                if (!alreadyBusy) { if (refresh) effects.Add(new EditorPlannedEffect { Type = "asset.refresh" }); effects.Add(new EditorPlannedEffect { Type = "script-compile-request" }); }
                return new Result { Compile = new CompileResult { DryRun = true, Refresh = refresh, WaitReady = waitReady, WouldRequest = !alreadyBusy, SkippedReason = skippedReason }, PlannedEffects = effects };
            }
            if (!alreadyBusy) { if (refresh) AssetDatabase.Refresh(); CompilationPipeline.RequestScriptCompilation(); }
            return new Result { Compile = new CompileResult { Requested = !alreadyBusy, Refresh = refresh, WaitReady = waitReady, SkippedReason = skippedReason }, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "script-compile-request" } } };
        }
    }

    /// <summary>editor.console — 读取 Console 日志(只读)。</summary>
    public class EditorConsoleTool : IUnityCliTool<EditorConsoleTool.Request, EditorConsoleTool.Result>
    {
        public sealed class Request { public string Type { get; set; } public int? Count { get; set; } }
        public sealed class Result { public bool Cleared { get; set; } public string Type { get; set; } public List<ConsoleEntry> Items { get; set; } public PageInfo PageInfo { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.console", Summary = "Read recent Unity Console log entries.", Description = "Read-only: returns recent Console entries filtered by type. Use console.clear to clear.", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read console", Input = new Request { Count = 20 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var count = Math.Max(1, Math.Min(r.Count ?? 50, 500));
            var items = EditorToolSupport.ReadConsoleEntries(r.Type, null, count);
            return new Result { Cleared = false, Type = r.Type, Items = items, PageInfo = new PageInfo { PageSize = count, Cursor = 0, Total = items.Count } };
        }
    }

    /// <summary>console.get — 读取 Console 日志(支持过滤和分页)。</summary>
    public class ConsoleGetTool : IUnityCliTool<ConsoleGetTool.Request, ConsoleGetTool.Result>
    {
        public sealed class Request { public string Type { get; set; } public string Filter { get; set; } public int? Count { get; set; } public int? Cursor { get; set; } }
        public sealed class Result { public string Command { get; set; } public List<ConsoleEntry> Items { get; set; } public string Filter { get; set; } public PageInfo PageInfo { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "console.get", Summary = "Read Unity Console entries with type filter, text search, and cursor paging.", Description = "Read Console entries newest-first. type filters by error/warning/log. filter does case-insensitive text search. cursor for pagination.", Group = "console", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read errors", Input = new Request { Type = "error", Count = 10 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var count = Math.Max(1, Math.Min(r.Count ?? 50, 500));
            var cursor = Math.Max(0, r.Cursor ?? 0);
            var items = EditorToolSupport.ReadConsoleEntries(r.Type, r.Filter, count + cursor);
            var paged = items.Skip(cursor).Take(count).ToList();
            return new Result { Command = "console.get", Items = paged, Filter = r.Filter, PageInfo = new PageInfo { PageSize = count, Cursor = cursor, Total = items.Count, NextCursor = cursor + paged.Count < items.Count ? (cursor + paged.Count).ToString() : null } };
        }
    }

    /// <summary>console.clear — 清空 Console。</summary>
    public class ConsoleClearTool : IUnityCliTool<ConsoleClearTool.Request, ConsoleClearTool.Result>
    {
        public sealed class Request { public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public bool Cleared { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "console.clear", Summary = "Clear the Unity Console log.", Description = "Clear all Console entries via LogEntries.Clear(). dryRun=true previews without clearing.", Group = "console", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Clear console", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "console.clear", DryRun = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } };
            var logEntriesType = GameObjectToolSupport.FindType("UnityEditor.LogEntries");
            var clearMethod = logEntriesType?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            clearMethod?.Invoke(null, null);
            return new Result { Command = "console.clear", DryRun = false, Cleared = true, PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "console.clear" } } };
        }
    }

    /// <summary>editor.selection.set — 设置 Editor 选择。</summary>
    public class EditorSelectionSetTool : IUnityCliTool<EditorSelectionSetTool.Request, EditorSelectionSetTool.Result>
    {
        public sealed class Request { public long? InstanceId { get; set; } public string Target { get; set; } public string Path { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string Selected { get; set; } public string TypeName { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.selection.set", Summary = "Set the active selection in the Unity Editor.", Description = "Set Selection.activeObject by instanceId or target path. dryRun=true previews.", Group = "editor", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Select by path", Input = new Request { Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var dryRun = r.DryRun ?? false;
            var target = r.Target ?? r.Path;
            if (dryRun) return new Result { Command = "editor.selection.set", DryRun = true, Selected = target ?? "<instanceId>" };
            UnityEngine.Object obj = null;
            if (r.InstanceId.HasValue && r.InstanceId.Value != 0) obj = EditorUtility.InstanceIDToObject((int)r.InstanceId.Value);
            else if (!string.IsNullOrEmpty(target)) obj = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(target);
            if (obj == null) throw new UnityCliBridge.BridgeCommandException("E_SELECTION_TARGET_NOT_FOUND", "Selection target not found.", "Pass a valid instanceId or path.");
            Selection.activeObject = obj;
            return new Result { Command = "editor.selection.set", DryRun = false, Selected = obj.name, TypeName = obj.GetType().Name };
        }
    }

    /// <summary>editor.active-tool.set — 设置 Editor 工具(Move/Rotate/Scale 等)。</summary>
    public class EditorActiveToolSetTool : IUnityCliTool<EditorActiveToolSetTool.Request, EditorActiveToolSetTool.Result>
    {
        public sealed class Request { public string Tool { get; set; } }
        public sealed class Result { public string Command { get; set; } public string Tool { get; set; } public string EditorTool { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "editor.active-tool.set", Summary = "Set the active Unity Editor tool (Move, Rotate, Scale, etc.).", Description = "Set Tools.current. Accepts View, Move, Rotate, Scale, Rect, Transform.", Group = "editor", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set Move tool", Input = new Request { Tool = "Move" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (string.IsNullOrEmpty(r.Tool)) throw new UnityCliBridge.BridgeCommandException("E_TOOL_REQUIRED", "editor.active-tool.set requires --tool.", "Pass View, Move, Rotate, Scale, Rect, Transform.");
            Tool editorTool;
            switch (r.Tool.ToLowerInvariant()) { case "view": case "pan": editorTool = Tool.View; break; case "move": case "translate": editorTool = Tool.Move; break; case "rotate": case "rotation": editorTool = Tool.Rotate; break; case "scale": editorTool = Tool.Scale; break; case "rect": editorTool = Tool.Rect; break; case "transform": editorTool = Tool.Transform; break; default: throw new UnityCliBridge.BridgeCommandException("E_TOOL_UNKNOWN", "Unknown editor tool: " + r.Tool, "Pass View, Move, Rotate, Scale, Rect, or Transform."); }
            Tools.current = editorTool;
            return new Result { Command = "editor.active-tool.set", Tool = r.Tool, EditorTool = editorTool.ToString() };
        }
    }

    /// <summary>project.tag — 添加/移除 Unity Tag。</summary>
    public class ProjectTagTool : IUnityCliTool<ProjectTagTool.Request, ProjectTagTool.Response>
    {
        public sealed class Request { public string Action { get; set; } public string Name { get; set; } }
        public sealed class Response { public TagLayerResult Result { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "project.tag", Summary = "Add or remove a Unity project tag.", Description = "Modify ProjectSettings/TagManager.asset tags. action=add (default) or remove.", Group = "project", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add tag", Input = new Request { Action = "add", Name = "Enemy" } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("project.tag");
            var (changed, _) = ApplyTagLayerChange(false, r.Action ?? "add", r.Name, null);
            return new Response { Result = new TagLayerResult { Action = r.Action ?? "add", Name = r.Name, Changed = changed }, PlannedEffects = changed ? new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "project.tag", Action = r.Action, Name = r.Name } } : new List<EditorPlannedEffect>() };
        }
        internal static (bool, int) ApplyTagLayerChange(bool layer, string action, string name, int? index)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new UnityCliBridge.BridgeCommandException("E_SCHEMA_INVALID", layer ? "Layer name is required." : "Tag name is required.", "Pass --action add|remove and a tag/layer name.");
            var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset").FirstOrDefault();
            if (asset == null) throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_NOT_FOUND", "Unity TagManager asset was not found.", "Open the project in Unity and retry.");
            var serialized = new SerializedObject(asset);
            var property = serialized.FindProperty(layer ? "layers" : "tags");
            if (property == null) throw new UnityCliBridge.BridgeCommandException("E_TAG_MANAGER_SCHEMA", "Unity TagManager schema was not recognized.", "Use the Unity Editor Project Settings UI.");
            bool changed; int resultIndex = -1;
            if (layer) changed = ApplyLayerChange(property, action, name, index, out resultIndex);
            else changed = ApplyTagChange(property, action, name);
            if (changed) serialized.ApplyModifiedProperties();
            return (changed, resultIndex);
        }
        private static bool ApplyTagChange(SerializedProperty tags, string action, string name)
        {
            for (var i = 0; i < tags.arraySize; i++) if (tags.GetArrayElementAtIndex(i).stringValue == name) { if (action == "remove") { tags.DeleteArrayElementAtIndex(i); return true; } return false; }
            if (action == "remove") return false;
            tags.InsertArrayElementAtIndex(tags.arraySize); tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = name; return true;
        }
        private static bool ApplyLayerChange(SerializedProperty layers, string action, string name, int? requestedIndex, out int usedIndex)
        {
            usedIndex = -1;
            for (var i = 8; i < Math.Min(32, layers.arraySize); i++) if (layers.GetArrayElementAtIndex(i).stringValue == name) { if (action == "remove") { layers.GetArrayElementAtIndex(i).stringValue = string.Empty; return true; } return false; }
            if (action == "remove") return false;
            var index = requestedIndex ?? -1;
            if (index < 8 || index >= Math.Min(32, layers.arraySize)) for (var i = 8; i < Math.Min(32, layers.arraySize); i++) if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue)) { index = i; break; }
            if (index < 8 || index >= Math.Min(32, layers.arraySize)) throw new UnityCliBridge.BridgeCommandException("E_LAYER_SLOT_UNAVAILABLE", "No user layer slot is available.", "Remove an unused user layer or pass --index 8..31.");
            layers.GetArrayElementAtIndex(index).stringValue = name; usedIndex = index; return true;
        }
    }

    /// <summary>project.layer — 添加/移除 Unity Layer。</summary>
    public class ProjectLayerTool : IUnityCliTool<ProjectLayerTool.Request, ProjectLayerTool.Response>
    {
        public sealed class Request { public string Action { get; set; } public string Name { get; set; } public int? Index { get; set; } }
        public sealed class Response { public TagLayerResult Result { get; set; } public long? LayerIndex { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "project.layer", Summary = "Add or remove a Unity project user layer.", Description = "Modify ProjectSettings/TagManager.asset layers (index 8-31). action=add (default) or remove.", Group = "project", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "settings-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Add layer", Input = new Request { Action = "add", Name = "Water" } } } };
        public Response Execute(Request r, UnityCliProjectToolContext ctx)
        {
            EditorToolSupport.EnsureEditorNotBusy("project.layer");
            var (changed, index) = ProjectTagTool.ApplyTagLayerChange(true, r.Action ?? "add", r.Name, r.Index);
            return new Response { Result = new TagLayerResult { Action = r.Action ?? "add", Name = r.Name, Changed = changed }, LayerIndex = index >= 0 ? index : (long?)null, PlannedEffects = changed ? new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "project.layer", Action = r.Action, Name = r.Name } } : new List<EditorPlannedEffect>() };
        }
    }

    /// <summary>package.embed — 将 Packages/ 包嵌入为 Assets/ 本地副本。</summary>
    public class PackageEmbedTool : IUnityCliTool<PackageEmbedTool.Request, PackageEmbedTool.Result>
    {
        public sealed class Request { public string Package { get; set; } }
        public sealed class Result { public string PackageId { get; set; } public string EmbeddedPath { get; set; } public List<EditorPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "package.embed", Summary = "Embed a Unity package from Packages/ into Assets/ as a local copy.", Description = "Use PackageClient to embed a package by id. The package becomes a local copy under Packages/.", Group = "package", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "package-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 120000, MaxTimeoutMs = 300000, Examples = new[] { new ToolExample { Description = "Embed package", Input = new Request { Package = "com.unity.x" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (string.IsNullOrEmpty(r.Package)) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_ID_REQUIRED", "package.embed requires --package <id>.", "Pass a package id like com.unity.x.");
            var client = typeof(UnityEditor.PackageManager.Client);
            var embedMethod = client.GetMethod("Embed", BindingFlags.Static | BindingFlags.Public);
            if (embedMethod == null) throw new UnityCliBridge.BridgeCommandException("E_PACKAGE_EMBED_UNSUPPORTED", "PackageClient.Embed is not available in this Unity version.", "Use Unity Editor Package Manager UI.");
            var request = embedMethod.Invoke(null, new object[] { r.Package });
            // 等待嵌入完成(同步轮询)
            var requestType = request.GetType();
            var isCompletedProp = requestType.GetProperty("IsCompleted");
            var statusProp = requestType.GetProperty("Status");
            var resultProp = requestType.GetProperty("Result");
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if ((bool)isCompletedProp.GetValue(request, null)) break;
                System.Threading.Thread.Sleep(100);
            }
            var status = statusProp != null ? statusProp.GetValue(request, null) : null;
            var result = resultProp != null ? resultProp.GetValue(request, null) : null;
            var resultType = result != null ? result.GetType() : null;
            var localPath = resultType?.GetProperty("localPath")?.GetValue(result, null) as string;
            return new Result { PackageId = r.Package, EmbeddedPath = localPath ?? ("Packages/" + r.Package), PlannedEffects = new List<EditorPlannedEffect> { new EditorPlannedEffect { Type = "package.embed", Path = r.Package } } };
        }
    }

    public sealed class PageInfo { public long PageSize { get; set; } public long Cursor { get; set; } public string NextCursor { get; set; } public long Total { get; set; } }
}
#endif
