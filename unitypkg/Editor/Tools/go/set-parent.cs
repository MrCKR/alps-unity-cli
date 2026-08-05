#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.set-parent — 将 GameObject 重新父级化到另一个 GameObject 或脱离到场景根。支持 dryRun。
    /// </summary>
    public class GameObjectSetParentTool : IUnityCliTool<GameObjectSetParentTool.Request, GameObjectSetParentTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Parent { get; set; }
            public bool? WorldPositionStays { get; set; }
        }

        public sealed class Result
        {
            public bool? DryRun { get; set; }
            public GameObjectRef Target { get; set; }
            public string Parent { get; set; }
            public bool WorldPositionStays { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.set-parent",
            Summary = "Reparent a GameObject under another GameObject or detach it to the scene root.",
            Description = "Reparent a GameObject under another GameObject or detach it to the scene root. Prefer hierarchy paths or instance ids for target and parent; plain names can be ambiguous after duplicate/create operations. Use --dry-run first for hierarchy work; worldPositionStays defaults to true.",
            Group = "go",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify", "undo-stack", "hierarchy-reparent" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Dry-run reparent by hierarchy path", Input = new Request { Target = "Player/Sword", Parent = "Player/RightHand", WorldPositionStays = true }, Execution = new ToolExampleExecution { DryRun = true } },
                new ToolExample { Description = "Detach to root by hierarchy path", Input = new Request { Target = "Player/Sword" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.set-parent");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "go.set-parent requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "go.set-parent requires a target.");
            var parentName = request.Parent;
            var worldPositionStays = request.WorldPositionStays ?? true;
            var newParent = string.IsNullOrEmpty(parentName) ? null : GameObjectToolSupport.ResolveUniqueTarget(parentName, "Parent GameObject is required.").transform;
            if (newParent == gameObject.transform || (newParent != null && newParent.IsChildOf(gameObject.transform)))
                throw new UnityCliBridge.BridgeCommandException("E_GO_PARENT_CYCLE", "A GameObject cannot be parented to itself or one of its descendants.", "Choose a parent outside the target hierarchy.");
            var targetId = gameObject.GetInstanceID();
            var parentId = newParent == null ? 0 : newParent.GetInstanceID();
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    DryRun = true,
                    Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                    Parent = parentName ?? "<root>",
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.set-parent", Parent = parentName ?? "<root>" } }
                });
            }
            ctx.CommitEffect();
            UnityCliSceneMutation.SetParent(gameObject.transform, newParent, "go.set-parent", worldPositionStays);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observed = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observedParent = parentId == 0 ? null : EditorUtility.InstanceIDToObject(parentId) as Transform;
            if (observed == null || observed.transform.parent != observedParent)
                throw new UnityCliBridge.BridgeCommandException("E_GO_SET_PARENT_STATE_MISMATCH", "GameObject hierarchy did not match the requested parent after write.", "Inspect the hierarchy and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = GameObjectToolSupport.BuildGameObjectRef(observed),
                Parent = parentName ?? "<root>",
                WorldPositionStays = worldPositionStays,
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.set-parent" } }
            });
        }
    }
}
#endif
