#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>scene.gameobject.component 内置 Context：只读一个组件预览。</summary>
        public sealed class SceneGameObjectComponentContext : IUnityCliContext<SceneGameObjectComponentContext.Request, SceneGameObjectComponentContext.Result>, IUnityCliProofObserver<SceneGameObjectComponentContext.Request>
        {
            public sealed class Request { public string Target { get; set; } public int Id { get; set; } public string Path { get; set; } public string Component { get; set; } }
            public sealed class Result { public GameObjectRef GameObject { get; set; } public ComponentInfo Component { get; set; } }
            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class ComponentInfo
            {
                public string Type { get; set; } public string FullName { get; set; } public bool Enabled { get; set; }
                [UnityCliAnyJson] public object Properties { get; set; }
            }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.gameobject.component", Summary = "Read one component preview attached to a GameObject by component type name.",
                Description = "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does not replace deep reads; use component.get for complete fields/props.",
                Category = "scene",Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported",
                Examples = new[] { new ContextExample { Description = "Read a Camera component preview", Input = new Request { Target = "Main Camera", Component = "Camera" } } }
            };

            /// <summary>解析 completionProbe 的 GameObject/Component identity，缺失组件在 effect 前拒绝。</summary>
            public UnityCliProofResourceClaim ResolveProofResource(Request request)
            {
                request = request ?? new Request();
                var gameObject = ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path);
                if (string.IsNullOrEmpty(request.Component))
                    throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass input component with a Unity component type name.");
                var component = ResolveComponent(gameObject, request.Component);
                if (component == null)
                    throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + request.Component, "Read scene.gameobject.components before constructing completionProbe.");
                return SceneProofResource(gameObject, component);
            }

            /// <summary>读取指定组件及公开属性，不调用任何写入 API。</summary>
            public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var gameObject = ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path);
                if (string.IsNullOrEmpty(request.Component))
                    throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass input component with a Unity component type name.");
                var component = ResolveComponent(gameObject, request.Component);
                if (component == null)
                    throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + request.Component, "Run context read scene.gameobject.components with the same target/path/id to list available component names.");
                var result = new Result
                {
                    GameObject = new GameObjectRef { Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = HierarchyPath(gameObject) },
                    Component = new ComponentInfo { Type = component.GetType().Name, FullName = component.GetType().FullName, Enabled = ComponentEnabled(component), Properties = SceneContextComponentReadableProperties(component) }
                };
                return new System.Threading.Tasks.ValueTask<Result>(result);
            }
        }
    }
}
#endif
