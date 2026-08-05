#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// go.create — 创建场景 GameObject,可选 Unity 原型/父级/简单变换/标签/层/active。
    /// </summary>
    public class GameObjectCreateTool : IUnityCliTool<GameObjectCreateTool.Request, GameObjectCreateTool.Result>
    {
        public sealed class Request
        {
            public string Name { get; set; }
            public string Target { get; set; }
            public string Parent { get; set; }
            public string Primitive { get; set; }
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
            public List<GameObjectPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "go.create",
            Summary = "Create a scene GameObject, optionally as a Unity primitive, parented under another object, and with simple transform/tag/layer/active values.",
            Description = "Create a scene GameObject, optionally as a Unity primitive, parented under another object, and with simple transform/tag/layer/active values. When parenting under an object created earlier in the workflow, use the returned hierarchy path or instance id instead of a plain name.",
            Group = "go",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a cube under root", Input = new Request { Name = "AlignmentCube", Primitive = "cube", X = 0, Y = 1, Z = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            GameObjectToolSupport.EnsureEditorNotBusy("go.create");
            var name = request.Name ?? request.Target ?? "GameObject";
            var parentTransform = string.IsNullOrEmpty(request.Parent) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.Parent, "Parent GameObject is required.").transform;
            var primitiveType = string.IsNullOrEmpty(request.Primitive) ? (PrimitiveType?)null : GameObjectToolSupport.ParsePrimitiveType(request.Primitive);
            // 先完整校验输入,避免半成品对象
            GameObjectToolSupport.ValidateMutationInputs(request.TagValue, request.LayerValue);
            GameObjectToolSupport.ValidateTransformMutationValues(request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);

            ctx.CommitEffect();
            GameObject gameObject;
            if (primitiveType.HasValue)
            {
                gameObject = GameObject.CreatePrimitive(primitiveType.Value);
                gameObject.name = name;
            }
            else
            {
                gameObject = new GameObject(name);
            }
            if (parentTransform != null) gameObject.transform.SetParent(parentTransform, false);
            GameObjectToolSupport.ApplyGameObjectMutations(gameObject, null, request.Active, request.TagValue, request.LayerValue,
                request.X, request.Y, request.Z, request.RotationX, request.RotationY, request.RotationZ, request.ScaleX, request.ScaleY, request.ScaleZ);
            UnityCliSceneMutation.RegisterCreatedObject(gameObject, "go.create");
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observed = EditorUtility.InstanceIDToObject(gameObject.GetInstanceID()) as GameObject;
            if (observed == null) throw new UnityCliBridge.BridgeCommandException("E_GO_CREATE_STATE_MISMATCH", "Created GameObject could not be read back.", "Retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                GameObject = GameObjectToolSupport.BuildGameObjectDetail(observed),
                PlannedEffects = new List<GameObjectPlannedEffect> { new GameObjectPlannedEffect { Type = "go.create", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
            });
        }
    }
}
#endif
