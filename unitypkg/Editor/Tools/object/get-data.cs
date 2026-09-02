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
            [UnityCliRange(0L, 16L)] public int? MaxDepth { get; set; }
            [UnityCliRange(1L, 1000L)] public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public List<string> Paths { get; set; }
            [UnityCliAnyJson] public object ViewQuery { get; set; }
            public string NamePattern { get; set; }
            public UnityCliReadBudgetInput Budget { get; set; }
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
            Description = "Read deep serialized data from any UnityEngine.Object. maxDepth is 0..16 and maxArrayItems is 1..1000. paths accepts at most 128 ordered exact reads; all paths and recursive branches share one budget (10000 scans, 4096 nodes, 2048 getters, 4096 reflection calls, 250 main-thread ms, 1 MiB UTF-8 by default; public maxima are 100000/20000/10000/20000/2000 ms/8 MiB). Truncated nodes return nextPath. Read-only in Edit and Play Mode.",
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
            var data = UnityCliBridge.ObjectSerializer.Serialize(target, options, ctx.UseReadBudget(request.Budget));
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TypeName = target.GetType().FullName,
                Data = data
            });
        }
    }
}
#endif
