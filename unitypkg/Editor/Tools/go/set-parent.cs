#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.set-parent — 将 GameObject 重新父级化到另一个 GameObject 或脱离到场景根。支持 dryRun。
    /// 对齐旧 GameObjectSetParent handler;session-affecting。worldPositionStays 默认 true。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class GameObjectSetParentTool : IUnityCliTool<GameObjectSetParentTool.Request, GameObjectSetParentTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Parent { get; set; }
            public bool? WorldPositionStays { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
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
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify", "undo-stack", "hierarchy-reparent" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Dry-run reparent by hierarchy path", Input = new Request { Target = "Player/Sword", Parent = "Player/RightHand", WorldPositionStays = true, DryRun = true } },
                new ToolExample { Description = "Detach to root by hierarchy path", Input = new Request { Target = "Player/Sword" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
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
            if (request.DryRun == true)
            {
                return new Result
                {
                    Command = "go.set-parent",
                    DryRun = true,
                    Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                    Parent = parentName ?? "<root>",
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.set-parent", Parent = parentName ?? "<root>" } }
                };
            }
            Undo.SetTransformParent(gameObject.transform, newParent, "go.set-parent");
            gameObject.transform.SetParent(newParent, worldPositionStays);
            EditorSceneManager.MarkSceneDirty(gameObject.scene);
            return new Result
            {
                Command = "go.set-parent",
                Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                Parent = parentName ?? "<root>",
                WorldPositionStays = worldPositionStays,
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.set-parent" } }
            };
        }
    }
}
#endif
