#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// object.get-data — 读取任意 UnityEngine.Object 的深序列化数据。只读。
    /// 内置工具(原 RegisterCommand 形态)迁移为 IUnityCliTool 自包含。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ObjectGetDataTool : IUnityCliTool<ObjectGetDataTool.Request, ObjectGetDataTool.Result>
    {
        public sealed class Request
        {
            public long? InstanceId { get; set; }
            public string AssetPath { get; set; }
            public string Guid { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public List<string> Paths { get; set; }
            public object ViewQuery { get; set; }
            public string NamePattern { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string TypeName { get; set; }
            public long InstanceId { get; set; }
            public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "object.get-data",
            Summary = "Read deep serialized data from any Unity Object by instanceId, assetPath, guid or path.",
            Description = "Read deep serialized data from any UnityEngine.Object. Resolves the target by instanceId, assetPath/guid, or path/name. Supports maxDepth, maxArrayItems, includeFields, includeProperties, deepSerialization, paths (exact member reads) and viewQuery (filtered subtree). Read-only.",
            Group = "object",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read object data by instanceId", Input = new Request { InstanceId = 12345 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            // 用 BridgeRequest.Parse 从 InputJson 构造 BridgeRequest,复用 ObjectRefResolver 的完整解析逻辑
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var target = UnityCliBridge.ObjectRefResolver.ResolveObject(bridgeRequest);
            var options = ObjectToolSupport.BuildSerializerOptions(bridgeRequest);
            var memberJson = UnityCliBridge.ObjectSerializer.Serialize(target, options);
            return new Result
            {
                Command = "object.get-data",
                TypeName = target.GetType().FullName,
                InstanceId = target.GetInstanceID(),
                Data = UnityCliToolJson.Parse(memberJson)
            };
        }
    }
}
#endif
