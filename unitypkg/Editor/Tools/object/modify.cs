#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// object.modify — 使用可验证的 SerializedProperty pathPatches 修改 Unity Object。
    /// </summary>
    public class ObjectModifyTool : IUnityCliTool<ObjectModifyTool.Request, ObjectModifyTool.Result>
    {
        public sealed class Request : IUnityCliObjectReferenceInput, IUnityCliObjectSerializationInput
        {
            public int? InstanceId { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public string Target { get; set; }
            public int? ComponentInstanceId { get; set; }
            public int? ComponentIndex { get; set; }
            public string Component { get; set; }
            public string ComponentType { get; set; }
            [UnityCliAnyJson] public List<object> PathPatches { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public List<string> Paths { get; set; }
            [UnityCliAnyJson] public object ViewQuery { get; set; }
            public string NamePattern { get; set; }
        }

        public sealed class Result
        {
            public bool DryRun { get; set; }
            public long InstanceId { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            [UnityCliAnyJson] public object UpdatedData { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public bool HasPathPatches { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "object.modify",
            Summary = "Modify any Unity Object via verified serialized pathPatches; supports dry-run.",
            Description = "Modify an object using non-empty direct replace pathPatches on verifiable SerializedProperties. Resolves the target by instanceId, assetPath/guid, or path/name. Supports dry-run to preview without applying. Marks scene objects dirty.",
            Group = "object",
            DestructiveHint = true,
            SideEffects = new[] { "object-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Modify by pathPatches", Input = new Request { Path = "Player", PathPatches = new List<object> { new Dictionary<string, object> { { "path", "m_Name" }, { "op", "replace" }, { "value", "PlayerModified" } } } } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ObjectToolSupport.EnsureEditorNotBusy("object.modify");
            var target = UnityCliBridge.ObjectRefResolver.ResolveObject(request);
            if (EditorApplication.isPlaying && EditorUtility.IsPersistent(target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TOOL_PLAYMODE_UNAVAILABLE", "object.modify cannot modify persistent assets during Play Mode.", "Target a scene object or exit Play Mode before modifying an asset.");
            }

            if (request.PathPatches == null || request.PathPatches.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_PATCH_REQUIRED", "object.modify requires non-empty pathPatches.", "Pass direct replace pathPatches for writable serialized properties.");

            var hasPathPatches = true;
            UnityCliBridge.ObjectPatch.ValidateOnly(target, request.PathPatches, null);

            // dry-run 预览:不修改对象
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    InstanceId = target.GetInstanceID(),
                    TypeName = target.GetType().FullName,
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "object.modify", Target = target.name, HasPathPatches = hasPathPatches }
                    }
                });
            }

            ctx.CommitEffect();
            UnityCliBridge.ObjectPatch.PatchResult result = UnityCliBridge.ObjectPatch.ApplyVerifiedSerializedPatches(target, request.PathPatches);

            // Edit Mode 中同时持久化 asset dirty；Play Mode helper 不写任何 Editor dirty 状态。
            UnityCliSceneMutation.MarkSceneObjectDirty(target);
            if (target is Component c && c.gameObject != null) UnityCliSceneMutation.MarkSceneDirty(c.gameObject.scene);
            if (target is GameObject go) UnityCliSceneMutation.MarkSceneDirty(go.scene);

            // 回读更新后数据；空 patch result 不能作为业务修改已完成的证明。
            if (result == null || result.ChangedPaths == null || result.ChangedPaths.Count == 0)
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_MODIFY_STATE_MISMATCH", "Object patch produced no independently observed changed paths.", "Inspect the target and patch paths, then retry with valid writable properties.");
            var observedTarget = EditorUtility.InstanceIDToObject(target.GetInstanceID());
            if (observedTarget == null) throw new UnityCliBridge.BridgeCommandException("E_OBJECT_MODIFY_STATE_MISMATCH", "Target could not be fresh-resolved after patch.", "Retry after the Editor is idle.");
            UnityCliBridge.ObjectPatch.VerifyFreshSerializedProperties(observedTarget, request.PathPatches);
            var options = ObjectToolSupport.BuildSerializerOptions(request);
            var updatedData = UnityCliBridge.ObjectSerializer.Serialize(observedTarget, options);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                InstanceId = target.GetInstanceID(),
                TypeName = target.GetType().FullName,
                ChangedPaths = result.ChangedPaths,
                UpdatedData = updatedData
            });
        }
    }
}
#endif
