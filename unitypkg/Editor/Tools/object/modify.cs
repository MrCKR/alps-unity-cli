#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// object.modify — 修改任意 Unity Object:支持 pathPatches 与 jsonPatch (Merge Patch)。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ObjectModifyTool : IUnityCliTool<ObjectModifyTool.Request, ObjectModifyTool.Result>
    {
        public sealed class Request
        {
            public long? InstanceId { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public string PathPatches { get; set; }
            public string JsonPatch { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public List<string> Paths { get; set; }
            public object ViewQuery { get; set; }
            public string NamePattern { get; set; }
            public bool DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool DryRun { get; set; }
            public long InstanceId { get; set; }
            public string TypeName { get; set; }
            public List<string> ChangedPaths { get; set; }
            public object UpdatedData { get; set; }
            public List<string> Logs { get; set; }
            public object Audit { get; set; }
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
                new ToolExample { Description = "Modify by pathPatches", Input = new Request { InstanceId = 12345, PathPatches = "[{\"path\":\"m_Color\",\"value\":\"#FF0000\"}]" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            ObjectToolSupport.EnsureEditorNotBusy("object.modify");
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var target = UnityCliBridge.ObjectRefResolver.ResolveObject(bridgeRequest);

            var pathPatchesJson = bridgeRequest.RawJsonArg("pathPatches");
            var jsonPatchJson = bridgeRequest.RawJsonArg("jsonPatch");
            if (string.IsNullOrEmpty(pathPatchesJson) && string.IsNullOrEmpty(jsonPatchJson))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_PATCH_REQUIRED",
                    "object.modify requires pathPatches or jsonPatch.",
                    "Pass --input '{\"instanceId\":123,\"pathPatches\":[{\"path\":\"m_Color\",\"value\":\"#FF0000\"}]}' or jsonPatch merge.");
            }

            var hasPathPatches = !string.IsNullOrEmpty(pathPatchesJson);
            var hasJsonPatch = !string.IsNullOrEmpty(jsonPatchJson);

            // dry-run 预览:不修改对象
            if (bridgeRequest.DryRun)
            {
                return new Result
                {
                    Command = "object.modify",
                    DryRun = true,
                    InstanceId = target.GetInstanceID(),
                    TypeName = target.GetType().FullName,
                    PlannedEffects = new List<PlannedEffect>
                    {
                        new PlannedEffect { Type = "object.modify", Target = target.name, HasPathPatches = hasPathPatches, HasJsonPatch = hasJsonPatch }
                    }
                };
            }

            // 执行顺序:jsonPatch → pathPatches
            UnityCliBridge.ObjectPatch.PatchResult result = null;
            if (!string.IsNullOrEmpty(jsonPatchJson))
            {
                result = UnityCliBridge.ObjectPatch.ApplyMergePatch(target, jsonPatchJson);
            }
            if (!string.IsNullOrEmpty(pathPatchesJson))
            {
                var pathResult = UnityCliBridge.ObjectPatch.ApplyPathPatches(target, pathPatchesJson);
                if (result == null) result = pathResult;
                else
                {
                    result.ChangedPaths.AddRange(pathResult.ChangedPaths);
                    result.Logs.AddRange(pathResult.Logs);
                }
            }

            // 标记脏(场景对象)
            if (target is Component c && c.gameObject != null) EditorUtility.SetDirty(c);
            if (target is GameObject go) EditorUtility.SetDirty(go);
            if (target is UnityEngine.Object uo) EditorUtility.SetDirty(uo);

            // 回读更新后数据
            var options = ObjectToolSupport.BuildSerializerOptions(bridgeRequest);
            var afterJson = UnityCliBridge.ObjectSerializer.Serialize(target, options);

            return new Result
            {
                Command = "object.modify",
                InstanceId = target.GetInstanceID(),
                TypeName = target.GetType().FullName,
                ChangedPaths = result.ChangedPaths,
                UpdatedData = UnityCliToolJson.Parse(afterJson),
                Logs = result.Logs,
                Audit = UnityCliToolJson.Parse(UnityCliBridge.ObjectPatch.ResultToJson(result))
            };
        }
    }
}
#endif
