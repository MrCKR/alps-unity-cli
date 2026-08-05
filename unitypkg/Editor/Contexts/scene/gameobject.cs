#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>scene.gameobject 内置 Context：只读单个 GameObject 摘要。</summary>
        public sealed class SceneGameObjectContext : IUnityCliContext<SceneGameObjectContext.Request, SceneGameObjectContext.Result>, IUnityCliProofObserver<SceneGameObjectContext.Request>
        {
            public sealed class Request { public string Target { get; set; } public int Id { get; set; } public string Path { get; set; } public bool IncludeComponents { get; set; } = true; }
            public sealed class Result { public GameObjectInfo GameObject { get; set; } }
            public sealed class GameObjectInfo
            {
                public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public bool Active { get; set; } public string Tag { get; set; } public long Layer { get; set; }
                public TransformInfo Transform { get; set; } public List<ComponentInfo> Components { get; set; } public List<GameObjectRef> Children { get; set; }
            }
            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class TransformInfo { public VectorInfo Position { get; set; } public VectorInfo Rotation { get; set; } public VectorInfo Scale { get; set; } }
            public sealed class VectorInfo { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
            public sealed class ComponentInfo { public string Type { get; set; } public string FullName { get; set; } public bool Enabled { get; set; } }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.gameobject", Summary = "Read one GameObject summary by id, hierarchy path, or name without modifying the scene.",
                Description = "Read one GameObject's lightweight scene summary by id, hierarchy path, or name. Use this before go.inspect, component.get, object.get-data, or mutation tools to confirm the stable path/id. Does not modify the scene; set includeComponents=false when only the reusable GameObject reference is needed.",
                Category = "scene",Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported",
                Examples = new[] { new ContextExample { Description = "Read the main camera object", Input = new Request { Target = "Main Camera", IncludeComponents = true } }, new ContextExample { Description = "Read only the reusable object reference", Input = new Request { Target = "Main Camera", IncludeComponents = false } } }
            };

            /// <summary>解析 completionProbe 的唯一场景对象 identity，不执行 Context read。</summary>
            public UnityCliProofResourceClaim ResolveProofResource(Request request)
            {
                request = request ?? new Request();
                return SceneProofResource(ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path));
            }

            /// <summary>解析并读取场景对象；不修改场景或 Selection。</summary>
            public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var gameObject = ResolveSceneContextGameObject(request.Target, request.Id == 0 ? (int?)null : request.Id, request.Path);
                return new System.Threading.Tasks.ValueTask<Result>(new Result { GameObject = SceneGameObjectInfoFrom(gameObject, request.IncludeComponents) });
            }
        }

        // 构造 GameObject DTO；includeComponents=false 时组件列表保持空集合。
        private static SceneGameObjectContext.GameObjectInfo SceneGameObjectInfoFrom(GameObject gameObject, bool includeComponents)
        {
            var info = new SceneGameObjectContext.GameObjectInfo
            {
                Id = gameObject.GetInstanceID(), Name = gameObject.name, Path = HierarchyPath(gameObject), Active = gameObject.activeSelf, Tag = gameObject.tag, Layer = gameObject.layer,
                Transform = new SceneGameObjectContext.TransformInfo
                {
                    Position = SceneVectorInfoFrom(gameObject.transform.position), Rotation = SceneVectorInfoFrom(gameObject.transform.eulerAngles), Scale = SceneVectorInfoFrom(gameObject.transform.localScale)
                },
                Components = new List<SceneGameObjectContext.ComponentInfo>(), Children = new List<SceneGameObjectContext.GameObjectRef>()
            };
            if (includeComponents)
            {
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    info.Components.Add(new SceneGameObjectContext.ComponentInfo { Type = component == null ? "MissingScript" : component.GetType().Name, FullName = component == null ? null : component.GetType().FullName, Enabled = component != null && ComponentEnabled(component) });
                }
            }
            foreach (Transform child in gameObject.transform)
            {
                info.Children.Add(new SceneGameObjectContext.GameObjectRef { Id = child.gameObject.GetInstanceID(), Name = child.gameObject.name, Path = HierarchyPath(child.gameObject) });
            }
            return info;
        }

        // 将 Vector3 转成场景对象协议 DTO。
        private static SceneGameObjectContext.VectorInfo SceneVectorInfoFrom(Vector3 value)
        {
            return new SceneGameObjectContext.VectorInfo { X = value.x, Y = value.y, Z = value.z };
        }
    }
}
#endif
