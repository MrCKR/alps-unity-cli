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
    /// 对齐旧 ComponentModify handler;session-affecting。执行顺序:jsonPatch/componentDiff → pathPatches。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentModifyTool : IUnityCliTool<ComponentModifyTool.Request, ComponentModifyTool.Result>
    {
        public sealed class Request
        {
            public int? InstanceId { get; set; }
            public string Path { get; set; }
            public int? ComponentInstanceId { get; set; }
            public int? ComponentIndex { get; set; }
            public string Component { get; set; }
            public int? MaxDepth { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public long ComponentInstanceId { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            public object UpdatedData { get; set; }
            public List<string> Logs { get; set; }
            public object Audit { get; set; }
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
                new ToolExample { Description = "Patch localPosition", Input = new Request { Path = "Player", Component = "Transform" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.modify");
            // 通过原始输入 JSON 构建 BridgeRequest,复用 ObjectRefResolver 组件解析与 ObjectPatch
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(bridgeRequest);
            var pathPatchesJson = bridgeRequest.RawJsonArg("pathPatches");
            var jsonPatchJson = bridgeRequest.RawJsonArg("jsonPatch");
            var componentDiffJson = bridgeRequest.RawJsonArg("componentDiff");
            if (string.IsNullOrEmpty(pathPatchesJson) && string.IsNullOrEmpty(jsonPatchJson) && string.IsNullOrEmpty(componentDiffJson))
            {
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PATCH_REQUIRED",
                    "component.modify requires pathPatches, jsonPatch or componentDiff.",
                    "Pass --input '{\"component\":{\"type\":\"Transform\"},\"pathPatches\":[{\"path\":\"localPosition\",\"value\":{\"x\":1}}]}'.");
            }
            if (request.DryRun == true)
            {
                return new Result
                {
                    Command = "component.modify",
                    DryRun = true,
                    ComponentInstanceId = component.GetInstanceID(),
                    TypeName = component.GetType().FullName,
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.modify", Target = component.gameObject.name } }
                };
            }
            // 执行顺序对齐 IvanMurzak: jsonPatch/componentDiff → pathPatches
            UnityCliBridge.ObjectPatch.PatchResult result = null;
            var effectiveMerge = !string.IsNullOrEmpty(jsonPatchJson) ? jsonPatchJson : componentDiffJson;
            if (!string.IsNullOrEmpty(effectiveMerge))
            {
                result = UnityCliBridge.ObjectPatch.ApplyMergePatch(component, effectiveMerge);
            }
            if (!string.IsNullOrEmpty(pathPatchesJson))
            {
                var pathResult = UnityCliBridge.ObjectPatch.ApplyPathPatches(component, pathPatchesJson);
                if (result == null) result = pathResult;
                else { result.ChangedPaths.AddRange(pathResult.ChangedPaths); result.Logs.AddRange(pathResult.Logs); }
            }
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
            var maxDepth = bridgeRequest.IntArg("maxDepth", 2);
            var afterJson = UnityCliBridge.ObjectSerializer.Serialize(component, new UnityCliBridge.ObjectSerializer.Options { MaxDepth = maxDepth, IncludeFields = true, IncludeProperties = true, DeepSerialization = false });
            return new Result
            {
                Command = "component.modify",
                ComponentInstanceId = component.GetInstanceID(),
                TypeName = component.GetType().FullName,
                ChangedPaths = new List<string>(result.ChangedPaths),
                Logs = new List<string>(result.Logs),
                UpdatedData = GameObjectToolSupport.ParseJsonToObject(afterJson),
                Audit = GameObjectToolSupport.ParseJsonToObject(UnityCliBridge.ObjectPatch.ResultToJson(result))
            };
        }
    }
}
#endif
