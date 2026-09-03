#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.get — 深读单个 Component,支持 paths/viewQuery 互斥的 token-safe 读取。
    /// </summary>
    public class ComponentGetTool : IUnityCliTool<ComponentGetTool.Request, ComponentGetTool.Result>
    {
        public sealed class Request : IUnityCliObjectReferenceInput, IUnityCliObjectSerializationInput
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
            public GameObjectRef GameObject { get; set; }
            [UnityCliAnyJson] public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.get",
            Summary = "Deep-read one Component by GameObject and component selector; use paths/viewQuery for token-safe fields.",
            Description = "Deep-read one Component after selecting it by stable GameObject and component identity. maxDepth is 0..16, maxArrayItems is 1..1000, and paths accepts at most 128 ordered exact reads. Cycle/depth checks happen before expansion; all paths share budget scan/node/getter/reflection/time/UTF-8 ceilings and truncated nodes return nextPath. Defaults are 10000 scans, 4096 nodes, 2048 getters, 4096 reflection calls, 250 main-thread ms and 1 MiB; maxima are 100000/20000/10000/20000/2000 ms/8 MiB. Read-only in Edit and Play Mode.",
            Group = "component",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Read Transform position only", Input = new Request { Path = "Main Camera", Component = "Transform", MaxDepth = 2 } },
                new ToolExample { Description = "Read one filtered subtree", Input = new Request { Path = "Main Camera", Component = "Transform", NamePattern = "position" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var budget = ctx.UseReadBudget(request.Budget);
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(request, budget);
            var options = GameObjectToolSupport.BuildSerializerOptions(request);
            var data = UnityCliBridge.ObjectSerializer.Serialize(component, options, budget);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TypeName = component.GetType().FullName,
                GameObject = GameObjectToolSupport.BuildGameObjectRef(component.gameObject),
                Data = data
            });
        }
    }
}
#endif
