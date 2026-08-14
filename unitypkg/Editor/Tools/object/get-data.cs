#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// object.get-data — 读取任意 UnityEngine.Object 的深序列化数据。只读。
    /// </summary>
    public class ObjectGetDataTool : IUnityCliTool<ObjectGetDataTool.Request, ObjectGetDataTool.Result>
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
            public string TypeName { get; set; }
            [UnityCliAnyJson] public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "object.get-data",
            Summary = "Read deep serialized data from any Unity Object by instanceId, assetPath, guid or path.",
            Description = "Read deep serialized data from any UnityEngine.Object. Resolves the target by instanceId, assetPath/guid, or path/name. Supports maxDepth, maxArrayItems, includeFields, includeProperties, deepSerialization, paths (exact member reads) and viewQuery (filtered subtree). Read-only.",
            Group = "object",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read object data by hierarchy path", Input = new Request { Path = "Player" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = UnityCliBridge.ObjectRefResolver.ResolveObject(request);
            var options = ObjectToolSupport.BuildSerializerOptions(request);
            var data = UnityCliBridge.ObjectSerializer.Serialize(target, options);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TypeName = target.GetType().FullName,
                Data = data
            });
        }
    }
}
#endif
