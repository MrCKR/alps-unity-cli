#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.look-at — 旋转 GameObject 使其 forward 指向另一个 GameObject。
    /// </summary>
    public class GameObjectLookAtTool : IUnityCliTool<GameObjectLookAtTool.Request, GameObjectLookAtTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string LookAt { get; set; }
            public string Destination { get; set; }
        }

        public sealed class Result
        {
            public GameObjectDetail GameObject { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.look-at",
            Summary = "Rotate a GameObject so its forward vector looks at another GameObject.",
            Description = "Rotate a GameObject so its forward vector looks at another GameObject. Prefer hierarchy paths or instance ids for both target and lookAt after create/duplicate operations.",
            Group = "go",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Look at another object by hierarchy path", Input = new Request { Target = "CameraRig/MainCamera", LookAt = "Player/Avatar" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.look-at");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject look-at requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject look-at requires a target.");
            var lookAtName = request.LookAt ?? request.Destination;
            var lookAt = GameObjectToolSupport.ResolveUniqueTarget(lookAtName, "GameObject look-at requires a lookAt target. Use unity_cli tools run go.look-at --input '{\"target\":\"CameraRig\",\"lookAt\":\"Player\"}'.");
            UnityCliSceneMutation.RecordObject(gameObject.transform, "go.look-at");
            gameObject.transform.LookAt(lookAt.transform);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(gameObject),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.look-at", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
            });
        }
    }
}
#endif
