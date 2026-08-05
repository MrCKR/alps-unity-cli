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
            public long ComponentInstanceId { get; set; }
            public GameObjectRef GameObject { get; set; }
            [UnityCliAnyJson] public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.get",
            Summary = "Deep-read one Component by GameObject and component selector; use paths/viewQuery for token-safe fields.",
            Description = "Deep-read one Component after selecting a GameObject with go.find/go.inspect or context read scene.gameobject.components. For token safety, pass exact paths or one viewQuery; they are mutually exclusive and skip full fields/props expansion. Use componentIndex/componentInstanceId when duplicate components exist. Keep maxDepth low before expanding MonoBehaviours.",
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
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(request);
            var options = GameObjectToolSupport.BuildSerializerOptions(request);
            var data = UnityCliBridge.ObjectSerializer.Serialize(component, options);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                TypeName = component.GetType().FullName,
                ComponentInstanceId = component.GetInstanceID(),
                GameObject = GameObjectToolSupport.BuildGameObjectRef(component.gameObject),
                Data = data
            });
        }
    }
}
#endif
