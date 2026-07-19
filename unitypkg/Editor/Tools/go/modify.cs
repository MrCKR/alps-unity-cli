#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.modify — 修改一个或多个 GameObject 的名称/active/标签/层/绝对变换。
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.modify");
            // 先完整校验,避免前半成功后半失败的部分写入
            GameObjectToolSupport.ValidateMutationInputs(request.TagValue, request.LayerValue);
            GameObjectToolSupport.ValidateTransformMutationValues(request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);
            var plans = ResolveModifyTargets(request);
            var parent = string.IsNullOrEmpty(request.Parent) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.Parent, "Parent GameObject is required.");
            var parentId = parent == null ? 0 : parent.GetInstanceID();
            if (parent != null)
            {
                foreach (var plan in plans)
                {
                    var target = EditorUtility.InstanceIDToObject(plan.Id) as GameObject;
                    if (target == null || parent.transform == target.transform || parent.transform.IsChildOf(target.transform))
                        throw new UnityCliBridge.BridgeCommandException("E_GO_PARENT_CYCLE", "A GameObject cannot be parented to itself or one of its descendants.", "Choose a parent outside the target hierarchy.");
                }
            }
            ctx.CommitEffect();
            var details = new List<GameObjectDetail>();
            var effects = new List<GameObjectPlannedEffect>();
            foreach (var plan in plans)
            {
                var gameObject = EditorUtility.InstanceIDToObject(plan.Id) as GameObject;
                if (gameObject == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject disappeared before modification.", "Retry after the Editor is idle.");
                UnityCliSceneMutation.RecordObject(gameObject.transform, "go.modify");
                UnityCliSceneMutation.RecordObject(gameObject, "go.modify");
                GameObjectToolSupport.ApplyGameObjectMutations(gameObject, request.NewName, request.Active, request.TagValue, request.LayerValue,
                    request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);
                if (parent != null) gameObject.transform.SetParent(parent.transform, true);
                UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            }
            foreach (var plan in plans)
            {
                var observed = EditorUtility.InstanceIDToObject(plan.Id) as GameObject;
                if (observed == null || !MatchesRequest(observed, request) || (request.Parent != null && (observed.transform.parent == null || observed.transform.parent.GetInstanceID() != parentId)))
                    throw new UnityCliBridge.BridgeCommandException("E_GO_MODIFY_STATE_MISMATCH", "GameObject did not satisfy the requested mutation after write.", "Inspect the scene object and retry after the Editor is idle.");
                details.Add(GameObjectToolSupport.BuildGameObjectDetail(observed));
                effects.Add(new GameObjectPlannedEffect { Type = "go.modify", Target = GameObjectToolSupport.HierarchyPath(observed) });
            }
            if (details.Count == 1) return new System.Threading.Tasks.ValueTask<Result>(new Result { GameObject = details[0], PlannedEffects = effects });
            return new System.Threading.Tasks.ValueTask<Result>(new Result { GameObjects = details, ModifiedCount = details.Count, PlannedEffects = effects });
        }

        // go.modify 支持 targets[] 批量修改;先完整解析,避免部分写入
        private sealed class ModifyPlan { public int Id { get; set; } }

        private static bool MatchesRequest(GameObject observed, Request request)
        {
            if (request.NewName != null && observed.name != request.NewName) return false;
            if (request.Active.HasValue && observed.activeSelf != request.Active.Value) return false;
            if (request.TagValue != null && observed.tag != request.TagValue) return false;
            if (request.LayerValue != null && observed.layer != GameObjectToolSupport.ParseLayer(request.LayerValue)) return false;
            if (request.X.HasValue && !Mathf.Approximately(observed.transform.position.x, (float)request.X.Value)) return false;
            if (request.Y.HasValue && !Mathf.Approximately(observed.transform.position.y, (float)request.Y.Value)) return false;
            if (request.Z.HasValue && !Mathf.Approximately(observed.transform.position.z, (float)request.Z.Value)) return false;
            if (request.RotationX.HasValue && !Mathf.Approximately(observed.transform.eulerAngles.x, (float)request.RotationX.Value)) return false;
            if (request.RotationY.HasValue && !Mathf.Approximately(observed.transform.eulerAngles.y, (float)request.RotationY.Value)) return false;
            if (request.RotationZ.HasValue && !Mathf.Approximately(observed.transform.eulerAngles.z, (float)request.RotationZ.Value)) return false;
            if (request.ScaleX.HasValue && !Mathf.Approximately(observed.transform.localScale.x, (float)request.ScaleX.Value)) return false;
            if (request.ScaleY.HasValue && !Mathf.Approximately(observed.transform.localScale.y, (float)request.ScaleY.Value)) return false;
            return !request.ScaleZ.HasValue || Mathf.Approximately(observed.transform.localScale.z, (float)request.ScaleZ.Value);
        }

        private static List<ModifyPlan> ResolveModifyTargets(Request request)
        {
            if (request.Targets == null || request.Targets.Count == 0)
            {
                if (string.IsNullOrEmpty(request.Target))
                {
                    throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject modify requires target or targets[].", "Pass target for one object or targets as a non-empty string array.");
                }
                var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "GameObject modify requires target or targets[].");
                return new List<ModifyPlan> { new ModifyPlan { Id = target.GetInstanceID() } };
            }
            var targets = new List<ModifyPlan>();
            var seen = new HashSet<int>();
            foreach (var targetName in request.Targets)
            {
                var gameObject = GameObjectToolSupport.ResolveUniqueTarget(targetName, "GameObject modify targets[] contains an empty target.");
                if (seen.Add(gameObject.GetInstanceID())) targets.Add(new ModifyPlan { Id = gameObject.GetInstanceID() });
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
