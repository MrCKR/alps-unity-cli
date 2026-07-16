#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.modify — 用 pathPatches/jsonPatch/componentDiff 深改单个 Component。支持 dryRun。
    /// </summary>
    public class ComponentModifyTool : IUnityCliTool<ComponentModifyTool.Request, ComponentModifyTool.Result>
    {
        public sealed class Request : IUnityCliObjectReferenceInput
        {
            public int? InstanceId { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public string Target { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public int? ComponentInstanceId { get; set; }
            public int? ComponentIndex { get; set; }
            public string Component { get; set; }
            public string ComponentType { get; set; }
            public int? MaxDepth { get; set; }
            [UnityCliAnyJson] public List<object> PathPatches { get; set; }
            [UnityCliAnyJson] public object JsonPatch { get; set; }
            [UnityCliAnyJson] public object ComponentDiff { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public long ComponentInstanceId { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            [UnityCliAnyJson] public object UpdatedData { get; set; }
            public List<string> Logs { get; set; }
            [UnityCliAnyJson] public object Audit { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.modify",
            Summary = "Modify one Component with pathPatches/jsonPatch/componentDiff; use component.get/type.schema first.",
            Description = "Apply pathPatches, jsonPatch (Merge Patch) or componentDiff to a Component. Returns updated data, changed paths, logs and audit. Use component.get first. In Play Mode, scene component changes affect only the current runtime session unless later persisted by Unity workflow; in Edit Mode they mark scene/component state dirty.",
            Group = "component",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "component-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Patch localPosition", Input = new Request { Path = "Player", Component = "Transform", PathPatches = new List<object> { new Dictionary<string, object> { { "path", "localPosition/x" }, { "value", 1L } } } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.modify");
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(request);
            if ((request.PathPatches == null || request.PathPatches.Count == 0) && request.JsonPatch == null && request.ComponentDiff == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PATCH_REQUIRED",
                    "component.modify requires pathPatches, jsonPatch or componentDiff.",
                    "Pass --input '{\"component\":{\"type\":\"Transform\"},\"pathPatches\":[{\"path\":\"localPosition\",\"value\":{\"x\":1}}]}'.");
            }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "component.modify",
                    DryRun = true,
                    ComponentInstanceId = component.GetInstanceID(),
                    TypeName = component.GetType().FullName,
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.modify", Target = component.gameObject.name } }
                });
            }
            if (EditorApplication.isPlaying && EditorUtility.IsPersistent(component))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "component.modify cannot modify persistent assets during Play Mode.", "Target a scene component or exit Play Mode before modifying an asset.");
            }

            // 执行顺序对齐 IvanMurzak: jsonPatch/componentDiff → pathPatches
            UnityCliBridge.ObjectPatch.PatchResult result = null;
            var effectiveMerge = request.JsonPatch ?? request.ComponentDiff;
            if (effectiveMerge != null)
            {
                result = UnityCliBridge.ObjectPatch.ApplyMergePatch(component, effectiveMerge);
            }
            if (request.PathPatches != null && request.PathPatches.Count > 0)
            {
                var pathResult = UnityCliBridge.ObjectPatch.ApplyPathPatches(component, request.PathPatches);
                if (result == null) result = pathResult;
                else { result.ChangedPaths.AddRange(pathResult.ChangedPaths); result.Logs.AddRange(pathResult.Logs); }
            }
            UnityCliSceneMutation.MarkSceneObjectDirty(component);
            UnityCliSceneMutation.MarkSceneDirty(component.gameObject.scene);
            var maxDepth = request.MaxDepth ?? 2;
            var updatedData = UnityCliBridge.ObjectSerializer.Serialize(component, new UnityCliBridge.ObjectSerializer.Options { MaxDepth = maxDepth, IncludeFields = true, IncludeProperties = true, DeepSerialization = false });
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "component.modify",
                ComponentInstanceId = component.GetInstanceID(),
                TypeName = component.GetType().FullName,
                ChangedPaths = new List<string>(result.ChangedPaths),
                Logs = new List<string>(result.Logs),
                UpdatedData = updatedData,
                Audit = UnityCliBridge.ObjectPatch.ResultData(result)
            });
        }
    }
}
#endif
