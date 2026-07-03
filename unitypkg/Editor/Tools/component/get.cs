#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// component.get — 深读单个 Component,支持 paths/viewQuery 互斥的 token-safe 读取。
    /// 对齐旧 ComponentGet handler;只读。通过 ctx.InputJson 构建 BridgeRequest 复用 ObjectRefResolver/ObjectSerializer。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class ComponentGetTool : IUnityCliTool<ComponentGetTool.Request, ComponentGetTool.Result>
    {
        public sealed class Request
        {
            public int? InstanceId { get; set; }
            public string Path { get; set; }
            public string Name { get; set; }
            public int? ComponentInstanceId { get; set; }
            public int? ComponentIndex { get; set; }
            public string Component { get; set; }
            public string ComponentType { get; set; }
            public int? MaxDepth { get; set; }
            public int? MaxArrayItems { get; set; }
            public bool? IncludeFields { get; set; }
            public bool? IncludeProperties { get; set; }
            public bool? DeepSerialization { get; set; }
            public string NamePattern { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public string TypeName { get; set; }
            public long ComponentInstanceId { get; set; }
            public GameObjectRef GameObject { get; set; }
            public object Data { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "component.get",
            Summary = "Deep-read one Component by GameObject and component selector; use paths/viewQuery for token-safe fields.",
            Description = "Deep-read one Component after selecting a GameObject with go.find/go.inspect or context read scene.gameobject.components. For token safety, pass exact paths or one viewQuery; they are mutually exclusive and skip full fields/props expansion. Use componentIndex/componentInstanceId when duplicate components exist. Keep maxDepth low before expanding MonoBehaviours.",
            Group = "component",
            Version = "1.0.0",
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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            // 通过原始输入 JSON 构建 BridgeRequest,复用 ObjectRefResolver 组件解析(ObjectRefResolver 是 internal nested)
            var bridgeRequest = UnityCliBridge.BridgeRequest.Parse(ctx.InputJson);
            var component = UnityCliBridge.ObjectRefResolver.ResolveComponent(bridgeRequest);
            var options = GameObjectToolSupport.BuildSerializerOptions(bridgeRequest);
            var memberJson = UnityCliBridge.ObjectSerializer.Serialize(component, options);
            return new Result
            {
                Command = "component.get",
                TypeName = component.GetType().FullName,
                ComponentInstanceId = component.GetInstanceID(),
                GameObject = GameObjectToolSupport.BuildGameObjectRef(component.gameObject),
                // ObjectSerializer 输出为动态 JSON 树;解析为嵌套结构供框架序列化(IDictionary 支持)
                Data = GameObjectToolSupport.ParseJsonToObject(memberJson)
            };
        }
    }
}
#endif
