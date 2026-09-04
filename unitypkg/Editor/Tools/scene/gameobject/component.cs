#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>scene.gameobject.component 只读 Tool：只读一个组件预览。</summary>
        public sealed class SceneGameObjectComponentTool : IUnityCliTool<SceneGameObjectComponentTool.Request, SceneGameObjectComponentTool.Result>
        {
            public sealed class Request { public string Target { get; set; } public int Id { get; set; } public string Path { get; set; } public string Component { get; set; } public UnityCliReadBudgetInput Budget { get; set; } }
            public sealed class Result { public GameObjectRef GameObject { get; set; } public ComponentInfo Component { get; set; } }
            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class ComponentInfo
            {
                public string Type { get; set; } public string FullName { get; set; } public bool Enabled { get; set; }
                [UnityCliAnyJson] public object Properties { get; set; }
            }

            // 返回只读 Tool 的发现元数据。
            public ToolDescriptor Describe() => new()
            {
                Name = "scene.gameobject.component", Summary = "Read one component preview attached to a GameObject by component type name.",
                Description = "Lightweight preview of one component attached to a GameObject. Use scene.gameobject.components first when unsure which component name exists. Does not replace deep reads; use component.get for complete fields/props.",
                DestructiveHint = false,
                Group = "scene", Target = "editor", PlayModeSupport = "supported",
                Examples = new[] { new ToolExample { Description = "Read a Camera component preview", Input = new Request { Target = "Main Camera", Component = "Camera" } } }
            };

            /// <summary>供内部 completion observer 解析 GameObject/Component identity，缺失组件在 effect 前拒绝。</summary>
            internal UnityCliProofResourceClaim ResolveProofResource(Request request)
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
            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var budget = ctx.UseReadBudget(request.Budget);
                var gameObject = ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path, budget);
                if (string.IsNullOrEmpty(request.Component))
                    throw new BridgeCommandException("E_COMPONENT_TYPE_REQUIRED", "scene.gameobject.component requires component.", "Pass input component with a Unity component type name.");
                var component = ResolveSceneContextComponent(gameObject, request.Component, budget);
                if (component == null)
                    throw new BridgeCommandException("E_COMPONENT_NOT_FOUND", "Component not found on target: " + request.Component, "Run tools run scene.gameobject.components with the same target/path/id to list available component names.");
                if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var componentType = component.GetType();
                var result = new Result
                {
                    GameObject = new GameObjectRef { Id = ConsumeSceneGetter(budget, gameObject.GetInstanceID), Name = ConsumeSceneGetter(budget, () => gameObject.name), Path = GameObjectToolSupport.HierarchyPath(gameObject, budget) },
                    Component = new ComponentInfo { Type = componentType.Name, FullName = componentType.FullName, Enabled = GameObjectToolSupport.ComponentEnabled(component, budget), Properties = SceneContextComponentReadableProperties(component, budget) }
                };
                return new System.Threading.Tasks.ValueTask<Result>(result);
            }
        }
    }
}
#endif
