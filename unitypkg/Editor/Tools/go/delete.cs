#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.delete — 从场景删除单个 GameObject(递归删除子级)。支持 dryRun 预览。
    /// 对齐旧 GameObjectDelete handler;session-affecting。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class GameObjectDeleteTool : IUnityCliTool<GameObjectDeleteTool.Request, GameObjectDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public GameObjectRef Target { get; set; }
            public GameObjectRef Deleted { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.delete",
            Summary = "Delete one GameObject from the scene.",
            Description = "Delete one GameObject from the scene. This recursively removes its children in the current scene/session. Prefer hierarchy paths or instance ids, especially after create/duplicate operations where plain names may be ambiguous.",
            Group = "go",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify", "undo-stack", "recursive-delete" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Dry-run deletion by hierarchy path", Input = new Request { Target = "TempRoot/GeneratedChild", DryRun = true } },
                new ToolExample { Description = "Delete by instance id", Input = new Request { Target = "12345" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.delete");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject delete requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var gameObject = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject delete requires a target.");
            var targetRef = GameObjectToolSupport.BuildGameObjectRef(gameObject);
            if (request.DryRun == true)
            {
                return new Result
                {
                    Command = "go.delete",
                    DryRun = true,
                    Target = targetRef,
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.delete", Target = targetRef.Path, SideEffects = "none" } }
                };
            }
            var scene = gameObject.scene;
            Undo.DestroyObjectImmediate(gameObject);
            if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            return new Result
            {
                Deleted = targetRef,
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.delete", Target = targetRef.Path } }
            };
        }
    }
}
#endif
