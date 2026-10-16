#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.modify — 用可验证的 SerializedProperty pathPatches 修改单个 Component。支持 dryRun。
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
            [UnityCliRange(0L, int.MaxValue)] public int? ComponentIndex { get; set; }
            public string Component { get; set; }
            public string ComponentType { get; set; }
            [UnityCliRange(0L, 16L)] public int? MaxDepth { get; set; }
            [UnityCliAnyJson] public List<object> PathPatches { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
        }

        public sealed class Result
        {
            public bool? DryRun { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            [UnityCliAnyJson] public object UpdatedData { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.modify",
            Summary = "Modify one Component with verified serialized pathPatches; use component.get/debug.type.schema first.",
            Description = "Apply direct replace pathPatches to verifiable SerializedProperties of one Component. Returns fresh updated data and changed paths. Use component.get first. In Play Mode, scene component changes affect only the current runtime session unless later persisted by Unity workflow; in Edit Mode they mark scene/component state dirty.",
            Group = "component",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "component-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Patch localPosition", Input = new Request { Path = "Player", Component = "Transform", PathPatches = new List<object> { new Dictionary<string, object> { { "path", "m_LocalPosition.x" }, { "op", "replace" }, { "value", 1L } } } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("component.modify");
            var readBudget = ctx.UseReadBudget(request.Budget);
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(request, readBudget);
            if (request.PathPatches == null || request.PathPatches.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_PATCH_REQUIRED", "component.modify requires non-empty pathPatches.", "Pass direct replace pathPatches for writable serialized properties.");
            UnityCliBridge.ObjectPatch.ValidateOnly(component, request.PathPatches, null);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    TypeName = component.GetType().FullName,
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "component.modify", Target = component.gameObject.name } }
                });
            }
            if (EditorApplication.isPlaying && EditorUtility.IsPersistent(component))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "component.modify cannot modify persistent assets during Play Mode.", "Target a scene component or exit Play Mode before modifying an asset.");
            }

            // 所有请求形态、target 和 PlayMode 均已预检；紧邻首个 patch effect 提交屏障。
            ctx.CommitEffect();
            UnityCliBridge.ObjectPatch.PatchResult result = UnityCliBridge.ObjectPatch.ApplyVerifiedSerializedPatches(component, request.PathPatches);
            UnityCliSceneMutation.MarkSceneObjectDirty(component);
            UnityCliSceneMutation.MarkSceneDirty(component.gameObject.scene);
            var componentId = component.GetInstanceID();
            var observedComponent = EditorUtility.InstanceIDToObject(componentId) as Component;
            if (observedComponent == null || observedComponent.GetInstanceID() != componentId)
                throw new UnityCliBridge.BridgeCommandException("E_COMPONENT_MODIFY_STATE_MISMATCH", "Component target/type did not match after independent readback.", "Inspect the component and retry after the Editor is idle.");
            var observedSerializedObject = new SerializedObject(observedComponent);
            observedSerializedObject.Update();
            UnityCliBridge.ObjectPatch.VerifyFreshSerializedProperties(observedComponent, request.PathPatches);
            var maxDepth = request.MaxDepth ?? 2;
            var updatedData = UnityCliBridge.ObjectSerializer.Serialize(observedComponent, new UnityCliBridge.ObjectSerializer.Options { MaxDepth = maxDepth, IncludeFields = true, IncludeProperties = true, DeepSerialization = false }, readBudget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TypeName = component.GetType().FullName,
                ChangedPaths = new List<string>(result.ChangedPaths),
                UpdatedData = updatedData
            });
        }
    }
}
#endif
