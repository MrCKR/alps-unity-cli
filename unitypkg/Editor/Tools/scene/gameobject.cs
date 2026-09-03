#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>scene.gameobject 只读 Tool：只读单个 GameObject 摘要。</summary>
        public sealed class SceneGameObjectTool : IUnityCliTool<SceneGameObjectTool.Request, SceneGameObjectTool.Result>
        {
            public sealed class Request { public string Target { get; set; } public int Id { get; set; } public string Path { get; set; } public bool IncludeComponents { get; set; } = true; public UnityCliReadBudgetInput Budget { get; set; } }
            public sealed class Result { public GameObjectInfo GameObject { get; set; } }
            public sealed class GameObjectInfo
            {
                public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public bool Active { get; set; } public string Tag { get; set; } public long Layer { get; set; }
                public TransformInfo Transform { get; set; }
                [UnityCliItemsRange(0, ObjectRefResolver.AbsoluteMaxBatchItems)] public List<ComponentInfo> Components { get; set; }
                [UnityCliItemsRange(0, 100000)] public List<GameObjectRef> Children { get; set; }
            }
            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class TransformInfo { public VectorInfo Position { get; set; } public VectorInfo Rotation { get; set; } public VectorInfo Scale { get; set; } }
            public sealed class VectorInfo { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
            public sealed class ComponentInfo { public string Type { get; set; } public string FullName { get; set; } public bool Enabled { get; set; } }

            // 返回只读 Tool 的发现元数据。
            public ToolDescriptor Describe() => new()
            {
                Name = "scene.gameobject", Summary = "Read one GameObject summary by id, hierarchy path, or name without modifying the scene.",
                Description = "Read one GameObject's lightweight scene summary by id, hierarchy path, or name in Edit or Play mode. Use this after scene.hierarchy or go.find discovery and before component.get, object.get-data, or mutation tools to confirm a stable path/id. Does not modify the scene; set includeComponents=false when only the reusable reference is needed.",
                DestructiveHint = false,
                Group = "scene", Target = "editor", PlayModeSupport = "supported",
                Examples = new[] { new ToolExample { Description = "Read the main camera object", Input = new Request { Target = "Main Camera", IncludeComponents = true } }, new ToolExample { Description = "Read only the reusable object reference", Input = new Request { Target = "Main Camera", IncludeComponents = false } } }
            };

            /// <summary>供内部 completion observer 解析唯一场景对象 identity，不执行读取。</summary>
            internal UnityCliProofResourceClaim ResolveProofResource(Request request)
            {
                request = request ?? new Request();
                return SceneProofResource(ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path));
            }

            /// <summary>解析并读取场景对象；不修改场景或 Selection。</summary>
            public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var budget = ctx.UseReadBudget(request.Budget);
                var gameObject = ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path, budget);
                return new System.Threading.Tasks.ValueTask<Result>(new Result { GameObject = SceneGameObjectInfoFrom(gameObject, request.IncludeComponents, budget) });
            }
        }

        // 构造 GameObject DTO；includeComponents=false 时组件列表保持空集合。
        private static SceneGameObjectTool.GameObjectInfo SceneGameObjectInfoFrom(GameObject gameObject, bool includeComponents, UnityCliRequestBudget budget)
        {
            if (!budget.TryConsumeGetters(6)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var transform = gameObject.transform;
            var info = new SceneGameObjectTool.GameObjectInfo
            {
                Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = HierarchyPath(gameObject, budget), Active = gameObject.activeSelf, Tag = gameObject.tag, Layer = gameObject.layer,
                Transform = new SceneGameObjectTool.TransformInfo(),
                Components = new List<SceneGameObjectTool.ComponentInfo>(), Children = new List<SceneGameObjectTool.GameObjectRef>()
            };
            if (!budget.TryConsumeGetters(3)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            info.Transform.Position = SceneVectorInfoFrom(transform.position);
            info.Transform.Rotation = SceneVectorInfoFrom(transform.eulerAngles);
            info.Transform.Scale = SceneVectorInfoFrom(transform.localScale);
            if (includeComponents)
            {
                foreach (var component in ObjectRefResolver.BoundedComponents(gameObject, budget))
                {
                    if (component == null)
                    {
                        info.Components.Add(new SceneGameObjectTool.ComponentInfo { Type = "MissingScript", Enabled = false });
                        continue;
                    }
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    var componentType = component.GetType();
                    info.Components.Add(new SceneGameObjectTool.ComponentInfo { Type = componentType.Name, FullName = componentType.FullName, Enabled = ComponentEnabled(component, budget) });
                }
            }
            if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            var childCount = transform.childCount;
            if (childCount > budget.RemainingScannedRecords) throw new UnityCliRequestBudgetExceededException("scannedRecords");
            for (var index = 0; index < childCount; index++)
            {
                if (!budget.TryConsumeScannedRecord() || !budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var child = transform.GetChild(index);
                if (!budget.TryConsumeGetter()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                var childObject = child.gameObject;
                if (!budget.TryConsumeGetters(2)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                info.Children.Add(new SceneGameObjectTool.GameObjectRef { Id = childObject.GetInstanceID(), Name = childObject.name, Path = HierarchyPath(childObject, budget) });
            }
            return info;
        }

        // 将 Vector3 转成场景对象协议 DTO。
        private static SceneGameObjectTool.VectorInfo SceneVectorInfoFrom(Vector3 value)
        {
            return new SceneGameObjectTool.VectorInfo { X = value.x, Y = value.y, Z = value.z };
        }
    }
}
#endif
