#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.move-relative — 按 XYZ 偏移移动 GameObject,不替换绝对变换。
    /// </summary>
    public class GameObjectMoveRelativeTool : IUnityCliTool<GameObjectMoveRelativeTool.Request, GameObjectMoveRelativeTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public double? OffsetX { get; set; }
            public double? OffsetY { get; set; }
            public double? OffsetZ { get; set; }
        }

        public sealed class Result
        {
            public GameObjectDetail GameObject { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.move-relative",
            Summary = "Move a GameObject by a relative XYZ offset without replacing its absolute transform.",
            Description = "Move a GameObject by a relative XYZ offset without replacing its absolute transform. Use go.modify when you need absolute position; prefer hierarchy paths or instance ids after create/duplicate operations.",
            Group = "go",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Move up by hierarchy path", Input = new Request { Target = "Player/Avatar", OffsetX = 0, OffsetY = 2, OffsetZ = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.move-relative");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject move-relative requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject move-relative requires a target.");
            GameObjectToolSupport.ValidateTransformMutationValues(request.OffsetX, request.OffsetY, request.OffsetZ);
            var gameObjectId = gameObject.GetInstanceID();
            var offset = new Vector3((float)(request.OffsetX ?? 0), (float)(request.OffsetY ?? 0), (float)(request.OffsetZ ?? 0));
            // 以写入前位置加请求偏移冻结期望值，不能用写后旧引用自证。
            var expectedPosition = gameObject.transform.position + offset;
            ctx.CommitEffect();
            UnityCliSceneMutation.RecordObject(gameObject.transform, "go.move-relative");
            gameObject.transform.position = expectedPosition;
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observed = EditorUtility.InstanceIDToObject(gameObjectId) as GameObject;
            if (observed == null || !Mathf.Approximately(observed.transform.position.x, expectedPosition.x) || !Mathf.Approximately(observed.transform.position.y, expectedPosition.y) || !Mathf.Approximately(observed.transform.position.z, expectedPosition.z))
                throw new UnityCliBridge.BridgeCommandException("E_GO_MOVE_RELATIVE_STATE_MISMATCH", "GameObject move was not observed after write.", "Inspect the scene object and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(observed),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.move-relative", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
            });
        }
    }
}
#endif
