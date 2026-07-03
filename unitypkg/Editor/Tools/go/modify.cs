#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.modify — 修改一个或多个 GameObject 的名称/active/标签/层/绝对变换。
    /// 对齐旧 GameObjectModify handler;session-affecting。targets[] 批量修改为 all-or-none。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class GameObjectModifyTool : IUnityCliTool<GameObjectModifyTool.Request, GameObjectModifyTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public List<string> Targets { get; set; }
            public string NewName { get; set; }
            public string Parent { get; set; }
            public bool? Active { get; set; }
            public string TagValue { get; set; }
            public string LayerValue { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
            public double? RotationX { get; set; }
            public double? RotationY { get; set; }
            public double? RotationZ { get; set; }
            public double? ScaleX { get; set; }
            public double? ScaleY { get; set; }
            public double? ScaleZ { get; set; }
        }

        public sealed class Result
        {
            public GameObjectDetail GameObject { get; set; }
            public List<GameObjectDetail> GameObjects { get; set; }
            public long ModifiedCount { get; set; }
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.modify",
            Summary = "Modify one or more GameObjects' name, active state, tag, layer, absolute transform values, or parent.",
            Description = "Modify one or more GameObjects' name, active state, tag, layer, absolute transform values, or parent. Pass target for one object or targets[] to apply the same mutation to multiple objects in one all-or-none call; every target must resolve uniquely before any scene object is changed.",
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
                new ToolExample { Description = "Move and deactivate by hierarchy path", Input = new Request { Target = "Player/Avatar", X = 1, Y = 2, Z = 3, Active = false } },
                new ToolExample { Description = "Apply the same transform to multiple objects", Input = new Request { Targets = new List<string> { "Generated/A", "Generated/B" }, X = 0, Y = 1, Z = 0 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.modify");
            // 先完整校验,避免前半成功后半失败的部分写入
            GameObjectToolSupport.ValidateMutationInputs(request.TagValue, request.LayerValue);
            var targets = ResolveModifyTargets(request);

            if (targets.Count == 1)
            {
                var gameObject = targets[0];
                Undo.RecordObject(gameObject.transform, "unity_cli go.modify");
                Undo.RecordObject(gameObject, "unity_cli go.modify");
                GameObjectToolSupport.ApplyGameObjectMutations(gameObject, request.NewName, request.Active, request.TagValue, request.LayerValue,
                    request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                return new Result
                {
                    GameObject = GameObjectToolSupport.BuildGameObjectDetail(gameObject),
                    PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.modify", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
                };
            }

            foreach (var gameObject in targets)
            {
                Undo.RecordObject(gameObject.transform, "unity_cli go.modify batch");
                Undo.RecordObject(gameObject, "unity_cli go.modify batch");
            }
            var details = new List<GameObjectDetail>();
            var effects = new List<GameObjectPlannedEffect>();
            foreach (var gameObject in targets)
            {
                GameObjectToolSupport.ApplyGameObjectMutations(gameObject, request.NewName, request.Active, request.TagValue, request.LayerValue,
                    request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
                details.Add(GameObjectToolSupport.BuildGameObjectDetail(gameObject));
                effects.Add(new GameObjectPlannedEffect { Type = "go.modify", Target = GameObjectToolSupport.HierarchyPath(gameObject) });
            }
            return new Result { GameObjects = details, ModifiedCount = details.Count, PlannedEffects = effects };
        }

        // go.modify 支持 targets[] 批量修改;先完整解析,避免部分写入
        private static List<GameObject> ResolveModifyTargets(Request request)
        {
            if (request.Targets == null || request.Targets.Count == 0)
            {
                if (string.IsNullOrEmpty(request.Target))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject modify requires target or targets[].", "Pass target for one object or targets as a non-empty string array.");
                }
                return new List<GameObject> { GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject modify requires target or targets[].") };
            }
            var targets = new List<GameObject>();
            var seen = new HashSet<int>();
            foreach (var targetName in request.Targets)
            {
                var gameObject = GameObjectToolSupport.ResolveUniqueTarget(targetName, "GameObject modify targets[] contains an empty target.");
                if (seen.Add(gameObject.GetInstanceID())) targets.Add(gameObject);
            }
            if (targets.Count == 0)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject modify targets[] must contain at least one target.", "Pass target for one object or targets as a non-empty string array.");
            }
            return targets;
        }
    }
}
#endif
