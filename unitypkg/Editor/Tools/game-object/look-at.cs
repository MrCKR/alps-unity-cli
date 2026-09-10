#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// game-object.look-at — 旋转 GameObject 使其 forward 指向另一个 GameObject。
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
            Name = "game-object.look-at",
            Summary = "Rotate a GameObject so its forward vector looks at another GameObject.",
            Description = "Rotate a GameObject so its forward vector looks at another GameObject. Prefer hierarchy paths or instance ids for both target and lookAt after create/duplicate operations.",
            Group = "game-object",
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
            GameObjectToolSupport.EnsureEditorNotBusy("game-object.look-at");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject look-at requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject look-at requires a target.");
            var lookAtName = request.LookAt ?? request.Destination;
            var lookAt = GameObjectToolSupport.ResolveUniqueTarget(lookAtName, "GameObject look-at requires a lookAt target. Use unitycli tools run game-object.look-at --input '{\"target\":\"CameraRig\",\"lookAt\":\"Player\"}'.");
            var gameObjectId = gameObject.GetInstanceID();
            var direction = lookAt.transform.position - gameObject.transform.position;
            if (direction.sqrMagnitude <= Mathf.Epsilon)
                throw new UnityCliBridge.BridgeCommandException("E_GO_LOOK_AT_DIRECTION_INVALID", "GameObject look-at target must not share the target position.", "Move one object before calling go.look-at.");
            // 在 barrier 前冻结 LookAt 的不可变期望旋转，写入后只接受 fresh readback。
            var expectedRotation = Quaternion.LookRotation(direction);
            ctx.CommitEffect();
            UnityCliSceneMutation.RecordObject(gameObject.transform, "game-object.look-at");
            gameObject.transform.rotation = expectedRotation;
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observed = EditorUtility.InstanceIDToObject(gameObjectId) as GameObject;
            if (observed == null || Quaternion.Angle(observed.transform.rotation, expectedRotation) > 0.01f)
                throw new UnityCliBridge.BridgeCommandException("E_GO_LOOK_AT_STATE_MISMATCH", "GameObject rotation did not match the requested look-at direction.", "Inspect the scene object and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(observed),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "game-object.look-at", Target = GameObjectToolSupport.HierarchyPath(observed) } }
            });
        }
    }
}
#endif
