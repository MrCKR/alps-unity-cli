#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>
        /// scene.gameobject 内置 Context：按 id/path/name 读取单个 GameObject 摘要。
        /// </summary>
        public sealed class SceneGameObjectContext : IUnityCliContext<SceneGameObjectContext.Request, SceneGameObjectContext.Result>, IUnityCliBuiltInRawBridgeContext
        {
            public sealed class Request
            {
                public string Target { get; set; }
                public int Id { get; set; }
                public string Path { get; set; }
                public bool IncludeComponents { get; set; } = true;
            }

            public sealed class Result
            {
                public GameObjectInfo GameObject { get; set; }
            }

            public sealed class GameObjectInfo
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
                public bool Active { get; set; }
                public string Tag { get; set; }
                public long Layer { get; set; }
                public TransformInfo Transform { get; set; }
                public List<ComponentInfo> Components { get; set; }
                public List<GameObjectRef> Children { get; set; }
            }

            public sealed class GameObjectRef
            {
                public long Id { get; set; }
                public string Name { get; set; }
                public string Path { get; set; }
            }

            public sealed class TransformInfo
            {
                public VectorInfo Position { get; set; }
                public VectorInfo Rotation { get; set; }
                public VectorInfo Scale { get; set; }
            }

            public sealed class VectorInfo
            {
                public double X { get; set; }
                public double Y { get; set; }
                public double Z { get; set; }
            }

            public sealed class ComponentInfo
            {
                public string Type { get; set; }
                public string FullName { get; set; }
                public bool Enabled { get; set; }
            }

            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.gameobject",
                Summary = "Read one GameObject summary by id, hierarchy path, or name without modifying the scene.",
                Description = "Read one GameObject's lightweight scene summary by id, hierarchy path, or name. Use this before go.inspect, component.get, object.get-data, or mutation tools to confirm the stable path/id. Does not modify the scene; set includeComponents=false when only the reusable GameObject reference is needed.",
                Category = "scene",
                Version = "1.0.0",
                Source = "built-in",
                Target = "editor",
                AvailabilityScope = "editorSession",
                PlayModeSupport = "supported",
                RefreshBehavior = "on-demand"
            };

            /// <summary>
            /// 同步 fallback 读取；生产 Bridge 注册路径会优先调用 ReadRaw 保留 legacy JSON shape。
            /// </summary>
            public Result Read(Request request, UnityCliProjectToolContext ctx)
            {
                var id = request.Id == 0 ? (int?)null : request.Id;
                var gameObject = ResolveSceneContextGameObject(request.Target, id, request.Path);
                var json = SceneGameObjectPayloadJson(gameObject, request.IncludeComponents);
                return UnityCliSerializer.Deserialize(json, typeof(Result)) as Result;
            }

            /// <summary>
            /// 返回 legacy raw payload，保持 includeComponents、target/path/id 解析和错误 envelope 不变。
            /// </summary>
            public string ReadRaw(BridgeRequest request, UnityCliProjectToolContext ctx)
            {
                var target = request.StringArg("target") ?? request.StringArg("name");
                var id = request.OptionalIntArg("id");
                var path = request.StringArg("path");
                var includeComponents = request.BoolArg("includeComponents", true);

                GameObject gameObject = null;
                if (id.HasValue)
                {
                    gameObject = EditorUtility.InstanceIDToObject(id.Value) as GameObject;
                }
                else if (!string.IsNullOrEmpty(path))
                {
                    gameObject = GameObject.Find(path);
                }
                else if (!string.IsNullOrEmpty(target))
                {
                    var resolution = ResolveGameObjectTarget(target);
                    if (resolution.Matches.Count == 1)
                    {
                        gameObject = resolution.Target;
                    }
                    else if (resolution.Matches.Count > 1)
                    {
                        return ErrorJson("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id for disambiguation.");
                    }
                }

                if (gameObject == null)
                {
                    var hint = !string.IsNullOrEmpty(target) || id.HasValue || !string.IsNullOrEmpty(path)
                        ? "GameObject not found. Pass a name, hierarchy path or instance id."
                        : "A target is required: pass input target, id, or path.";
                    return ErrorJson("E_TARGET_NOT_FOUND", hint, "Read a scene context such as scene.cameras to discover reusable GameObject paths or ids.");
                }

                return SuccessJson(SceneGameObjectPayloadJson(gameObject, includeComponents));
            }
        }

        // 生成 scene.gameobject legacy payload；includeComponents=false 时只返回可复用 GameObject 引用。
        private static string SceneGameObjectPayloadJson(GameObject gameObject, bool includeComponents)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("gameObject", includeComponents ? GameObjectDetailJson(gameObject) : GameObjectTargetJson(gameObject))
                .EndObject()
                .ToString();
        }
    }
}
#endif
