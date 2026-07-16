#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.duplicate — 复制场景中的 GameObject,可指定新名称或父级。
    /// </summary>
    public class GameObjectDuplicateTool : IUnityCliTool<GameObjectDuplicateTool.Request, GameObjectDuplicateTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Name { get; set; }
            public string NewName { get; set; }
            public string Parent { get; set; }
        }

        public sealed class Result
        {
            public GameObjectDetail GameObject { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.duplicate",
            Summary = "Duplicate a GameObject in the active scene, optionally assigning a new name or parent.",
            Description = "Duplicate a GameObject in the active scene, optionally assigning a new name or parent. Prefer hierarchy paths or instance ids for target and parent; returns the duplicate path/id so follow-up calls can avoid ambiguous names.",
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
                new ToolExample { Description = "Duplicate by hierarchy path", Input = new Request { Target = "Player/Avatar", NewName = "Player Copy" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.duplicate");
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject duplicate requires a target.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var source = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject duplicate requires a target.");
            var parentTransform = string.IsNullOrEmpty(request.Parent) ? source.transform.parent : GameObjectToolSupport.ResolveUniqueTarget(request.Parent, "Parent GameObject is required.").transform;
            var duplicate = UnityEngine.Object.Instantiate(source);
            UnityCliSceneMutation.RegisterCreatedObject(duplicate, "go.duplicate");
            duplicate.name = request.NewName ?? request.Name ?? (source.name + " Copy");
            duplicate.transform.SetParent(parentTransform, true);
            duplicate.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            UnityCliSceneMutation.MarkSceneDirty(duplicate.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(duplicate),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.duplicate", Target = GameObjectToolSupport.HierarchyPath(duplicate) } }
            });
        }
    }
}
#endif
