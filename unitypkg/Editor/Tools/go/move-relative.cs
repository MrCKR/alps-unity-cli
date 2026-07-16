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
            Version = "1.0.0",
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
            UnityCliSceneMutation.RecordObject(gameObject.transform, "go.move-relative");
            var offset = new Vector3((float)(request.OffsetX ?? 0), (float)(request.OffsetY ?? 0), (float)(request.OffsetZ ?? 0));
            gameObject.transform.position += offset;
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(gameObject),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.move-relative", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
            });
        }
    }
}
#endif
