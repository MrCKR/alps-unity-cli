#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>
    /// go.delete — 从场景删除单个 GameObject(递归删除子级)。支持 dryRun 预览。
    /// </summary>
    public class GameObjectDeleteTool : IUnityCliTool<GameObjectDeleteTool.Request, GameObjectDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public bool? DryRun { get; set; }
            public GameObjectRef Deleted { get; set; }
            public bool Destroyed { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.delete",
            Summary = "Delete one scene GameObject; EditMode stays dirty, and final PlayMode proof requires reading editor.state.",
            Description = "Delete one GameObject from the current scene/session and recursively remove its children. Dry-run returns target with destroyed=false. Live execution returns deleted with destroyed=true only after the target no longer exists; Play Mode scene instances are destroyed synchronously before the command returns. In EditMode, deletion adds shared Undo history and intentionally leaves the scene dirty. If a task started from a clean scene and must discard all temporary in-memory changes without saving, finish business verification, delete the temporary object, then call scene.load once with the exact active scene path; do not probe or repeat editor.undo when pre-existing Undo history is unknown. When final PlayMode is a task requirement, read the editor.state Context after cleanup. Prefer hierarchy paths or instance ids when plain names may be ambiguous.",
            Group = "go",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify", "undo-stack", "recursive-delete" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Dry-run deletion by hierarchy path", Input = new Request { Target = "TempRoot/GeneratedChild" }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Delete the generated child", Input = new Request { Target = "TempRoot/GeneratedChild" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.delete");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject delete requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject delete requires a target.");
            var targetRef = GameObjectToolSupport.BuildGameObjectRef(gameObject);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    Destroyed = false,
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.delete", Target = targetRef.Path, SideEffects = "none" } }
                });
            }
            var scene = gameObject.scene;
            ctx.CommitEffect();
            UnityCliSceneMutation.DestroyObject(gameObject);
            if (gameObject != null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_GAMEOBJECT_DELETE_FAILED",
                    "GameObject remained alive after synchronous deletion: " + targetRef.Path,
                    new Dictionary<string, object>
                    {
                        { "target", targetRef.Path },
                        { "instanceId", targetRef.Id }
                    },
                    new[] { "Run tools run go.inspect for the target and inspect the Unity Console before retrying." });
            }
            UnityCliSceneMutation.MarkSceneDirty(scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Deleted = targetRef,
                Destroyed = true,
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.delete", Target = targetRef.Path } }
            });
        }
    }
}
#endif
