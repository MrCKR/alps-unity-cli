#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// object.modify — 修改任意 Unity Object:支持 pathPatches 与 jsonPatch (Merge Patch)。
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
            [UnityCliAnyJson] public object JsonPatch { get; set; }
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
            public string Command { get; set; }
            public bool DryRun { get; set; }
            public long InstanceId { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            [UnityCliAnyJson] public object UpdatedData { get; set; }
            public List<string> Logs { get; set; }
            [UnityCliAnyJson] public object Audit { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public bool HasPathPatches { get; set; }
            public bool HasJsonPatch { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "object.modify",
            Summary = "Modify any Unity Object via pathPatches or jsonPatch (Merge Patch); supports dry-run.",
            Description = "Modify any Unity Object via pathPatches (per-path set/add/remove) or jsonPatch (RFC 7386 Merge Patch). Resolves the target by instanceId, assetPath/guid, or path/name. Supports dry-run to preview without applying. Marks scene objects dirty.",
            Group = "object",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "object-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            SupportsDryRun = true,
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Modify by pathPatches", Input = new Request { Path = "Player", PathPatches = new List<object> { new Dictionary<string, object> { { "path", "name" }, { "value", "PlayerModified" } } } } }
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

            if ((request.PathPatches == null || request.PathPatches.Count == 0) && request.JsonPatch == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_PATCH_REQUIRED",
                    "object.modify requires pathPatches or jsonPatch.",
                    "Pass --input '{\"instanceId\":123,\"pathPatches\":[{\"path\":\"m_Color\",\"value\":\"#FF0000\"}]}' or jsonPatch merge.");
            }

            var hasPathPatches = request.PathPatches != null && request.PathPatches.Count > 0;
            var hasJsonPatch = request.JsonPatch != null;

            // dry-run 预览:不修改对象
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "object.modify",
                    DryRun = true,
                    InstanceId = target.GetInstanceID(),
                    TypeName = target.GetType().FullName,
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "object.modify", Target = target.name, HasPathPatches = hasPathPatches, HasJsonPatch = hasJsonPatch }
                    }
                });
            }

            // 执行顺序:jsonPatch → pathPatches
            UnityCliBridge.ObjectPatch.PatchResult result = null;
            if (request.JsonPatch != null)
            {
                result = UnityCliBridge.ObjectPatch.ApplyMergePatch(target, request.JsonPatch);
            }
            if (hasPathPatches)
            {
                var pathResult = UnityCliBridge.ObjectPatch.ApplyPathPatches(target, request.PathPatches);
                if (result == null) result = pathResult;
                else
                {
                    result.ChangedPaths.AddRange(pathResult.ChangedPaths);
                    result.Logs.AddRange(pathResult.Logs);
                }
            }

            // Edit Mode 中同时持久化 asset dirty；Play Mode helper 不写任何 Editor dirty 状态。
            UnityCliSceneMutation.MarkSceneObjectDirty(target);
            if (target is Component c && c.gameObject != null) UnityCliSceneMutation.MarkSceneDirty(c.gameObject.scene);
            if (target is GameObject go) UnityCliSceneMutation.MarkSceneDirty(go.scene);

            // 回读更新后数据
            var options = ObjectToolSupport.BuildSerializerOptions(request);
            var updatedData = UnityCliBridge.ObjectSerializer.Serialize(target, options);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "object.modify",
                InstanceId = target.GetInstanceID(),
                TypeName = target.GetType().FullName,
                ChangedPaths = result.ChangedPaths,
                UpdatedData = updatedData,
                Logs = result.Logs,
                Audit = UnityCliBridge.ObjectPatch.ResultData(result)
            });
        }
    }
}
#endif
