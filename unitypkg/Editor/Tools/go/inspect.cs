#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.inspect — 查看单个 GameObject 的摘要(id/name/path/active/tag/layer/transform/components/children)。
    /// </summary>
    public class GameObjectInspectTool : IUnityCliTool<GameObjectInspectTool.Request, GameObjectInspectTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string Id { get; set; }
        }

        public sealed class Result
        {
            public GameObjectDetail GameObject { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.inspect",
            Summary = "Inspect one GameObject summary by name/path/id; use component.get for deep component fields.",
            Description = "Inspect one GameObject by name, hierarchy path, or instance id. Returns transform, components and direct children; use object.get-data or component.get for deeper serialized data and keep go.find/scene.hierarchy for discovery.",
            Group = "go",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect by hierarchy path", Input = new Request { Target = "Player/CameraRig" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target ?? request.Name ?? request.Path ?? request.Id;
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(target, "GameObject inspect requires a target.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { GameObject = GameObjectToolSupport.BuildGameObjectDetail(gameObject) });
        }
    }
}
#endif
