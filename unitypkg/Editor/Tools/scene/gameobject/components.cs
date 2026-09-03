#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// scene.gameobject.components 只读 Tool：读取目标 GameObject 的组件列表。
        /// </summary>
        public sealed class SceneGameObjectComponentsTool : IUnityCliTool<SceneGameObjectComponentsTool.Request, SceneGameObjectComponentsTool.Result>
        {
            public sealed class Request
            {
                public string Target { get; set; }
                public int Id { get; set; }
                public string Path { get; set; }
                public bool IncludeProperties { get; set; } = true;
                public UnityCliReadBudgetInput Budget { get; set; }
            }

            public sealed class Result
            {
                public GameObjectRef GameObject { get; set; }
                public List<ComponentInfo> Components { get; set; }
                public long Count { get; set; }
            }

            public sealed class GameObjectRef
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
            }

            public sealed class ComponentInfo
            {
                public string Type { get; set; }
                public string FullName { get; set; }
                public bool Enabled { get; set; }
                [UnityCliAnyJson] public object Properties { get; set; }
            }

            public ToolDescriptor Describe() => new()
            {
                Name = "scene.gameobject.components",
                Summary = "List components attached to one GameObject, optionally including readable property previews.",
                Description = "Lightweight list of components attached to a GameObject: type names, enabled state, and optional readable property preview. Use this before component.get/component.modify to choose component name or index. Does not return every serialized field; use component.get for deep component data.",
                DestructiveHint = false,
                Group = "scene",
                Target = "editor",
                PlayModeSupport = "supported",
                Examples = new[] { new ToolExample { Description = "List components on the main camera", Input = new Request { Target = "Main Camera", IncludeProperties = true } }, new ToolExample { Description = "List component names only", Input = new Request { Target = "Main Camera", IncludeProperties = false } } }
            };

            /// <summary>解析目标并直接构造组件列表 DTO，不经过 JSON 字符串。</summary>
            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var budget = ctx.UseReadBudget(request.Budget);
                var id = request.Id == 0 ? (int?)null : request.Id;
                var gameObject = ResolveSceneContextGameObject(request.Target, id, request.Path, budget);
                var components = SceneContextComponentDetails(gameObject, request.IncludeProperties, budget);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    GameObject = new GameObjectRef
                    {
                        Id = ConsumeSceneGetter(budget, gameObject.GetInstanceID),
                        Name = ConsumeSceneGetter(budget, () => gameObject.name),
                        Path = HierarchyPath(gameObject, budget)
                    },
                    Components = components,
                    Count = components.Count
                });
            }
        }
    }
}
#endif
