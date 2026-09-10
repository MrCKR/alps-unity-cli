#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>创建一个或一批 scene GameObject，并以完整 fresh object set 证明终态。</summary>
    public class GameObjectCreateTool : IUnityCliTool<GameObjectCreateTool.Request, GameObjectCreateTool.Result>
    {
        private const int MaxBatchCount = 1000;

        public sealed class Request
        {
            public string Name { get; set; }
            public string Target { get; set; }
            public string Parent { get; set; }
            public string Primitive { get; set; }
            public int? Count { get; set; }
            public List<List<double>> Positions { get; set; }
            public List<List<double>> Rotations { get; set; }
            public List<List<double>> Scales { get; set; }
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
            public long Count { get; set; }
            public GameObjectDetail GameObject { get; set; }
            public List<GameObjectDetail> GameObjects { get; set; }
            public List<PlannedEffect> PlannedEffects { get; set; }
        }

        public sealed class PlannedEffect
        {
            public string Type { get; set; }
            public long Count { get; set; }
            public string Parent { get; set; }
            public string Target { get; set; }
        }

        private sealed class CreatePlan
        {
            public int Count;
            public Transform Parent;
            public PrimitiveType? Primitive;
            public bool? Active;
            public string TagValue;
            public string LayerValue;
            public List<CreateItemPlan> Items;
        }

        private sealed class CreateItemPlan
        {
            public string Name;
            public double? X;
            public double? Y;
            public double? Z;
            public double? RotationX;
            public double? RotationY;
            public double? RotationZ;
            public double? ScaleX;
            public double? ScaleY;
            public double? ScaleZ;
            public List<double> LocalPosition;
            public List<double> LocalRotation;
            public List<double> LocalScale;
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "game-object.create",
            Summary = "Create one or up to 1000 scene GameObjects in one call, with optional primitive, parent, and per-object local transforms.",
            Description = "count defaults to 1. For a batch, positions/rotations/scales are optional arrays of exactly count [x,y,z] vectors; scalar transform fields remain the single-object form. The parent and every input are resolved before one effect barrier. Success returns the complete ordered object set from fresh instance-id readback.",
            Group = "game-object",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create one cube under the scene root.", Input = new Request { Name = "AlignmentCube", Primitive = "cube", X = 0, Y = 1, Z = 0 } },
                new ToolExample
                {
                    Description = "Create three individually addressable spheres with local positions.",
                    Input = new Request
                    {
                        Name = "Probe", Primitive = "sphere", Count = 3,
                        Positions = new List<List<double>>
                        {
                            new List<double> { 0, 0, 0 }, new List<double> { 1, 0, 0 }, new List<double> { 2, 0, 0 }
                        }
                    }
                }
            }
        };

        /// <summary>整批预检后跨越唯一 effect barrier，在一个 Undo group 中创建并 fresh 证明完整集合。</summary>
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            GameObjectToolSupport.EnsureEditorNotBusy("game-object.create");
            ct.ThrowIfCancellationRequested();
            var plan = Prepare(request ?? new Request());

            ct.ThrowIfCancellationRequested();
            ctx.CommitEffect();
            var instanceIds = new List<int>(plan.Count);
            var undoGroup = UnityCliSceneMutation.BeginUndoGroup("game-object.create");
            try
            {
                foreach (var item in plan.Items)
                {
                    var gameObject = plan.Primitive.HasValue
                        ? GameObject.CreatePrimitive(plan.Primitive.Value)
                        : new GameObject(item.Name);
                    gameObject.name = item.Name;
                    if (plan.Parent != null) gameObject.transform.SetParent(plan.Parent, false);
                    GameObjectToolSupport.ApplyGameObjectMutations(gameObject, null, plan.Active, plan.TagValue, plan.LayerValue,
                        item.X, item.Y, item.Z, item.RotationX, item.RotationY, item.RotationZ, item.ScaleX, item.ScaleY, item.ScaleZ);
                    ApplyLocalTransforms(gameObject.transform, item);
                    UnityCliSceneMutation.RegisterCreatedObject(gameObject, "game-object.create");
                    UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
                    instanceIds.Add(gameObject.GetInstanceID());
                }
            }
            finally
            {
                UnityCliSceneMutation.EndUndoGroup(undoGroup);
            }

            var observed = Prove(plan, instanceIds);
            var parentPath = plan.Parent == null ? null : GameObjectToolSupport.HierarchyPath(plan.Parent.gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Count = observed.Count,
                GameObject = observed[0],
                GameObjects = observed,
                PlannedEffects = new List<PlannedEffect>
                {
                    new PlannedEffect { Type = "game-object.create", Count = observed.Count, Parent = parentPath, Target = parentPath ?? "<scene-root>" }
                }
            });
        }

        /// <summary>冻结 count、parent、primitive、每个名称与 transform；任一非法项都在 effect 前终止。</summary>
        private static CreatePlan Prepare(Request request)
        {
            var count = request.Count ?? 1;
            if (count < 1 || count > MaxBatchCount)
                throw Error("E_GO_BATCH_COUNT_INVALID", "game-object.create count must be between 1 and " + MaxBatchCount + ".", "count", count);
            ValidateVectorArray(request.Positions, count, "positions");
            ValidateVectorArray(request.Rotations, count, "rotations");
            ValidateVectorArray(request.Scales, count, "scales");
            RejectTransformConflict(request.Positions, "positions", request.X, request.Y, request.Z);
            RejectTransformConflict(request.Rotations, "rotations", request.RotationX, request.RotationY, request.RotationZ);
            RejectTransformConflict(request.Scales, "scales", request.ScaleX, request.ScaleY, request.ScaleZ);
            if (count > 1 && HasScalarTransform(request))
                throw Error("E_GO_BATCH_TRANSFORM_CONFLICT", "Scalar transform fields are available only when count is 1; use positions, rotations, or scales for a batch.", "count", count);

            GameObjectToolSupport.ValidateMutationInputs(request.TagValue, request.LayerValue);
            var parent = string.IsNullOrEmpty(request.Parent)
                ? null
                : GameObjectToolSupport.ResolveUniqueTarget(request.Parent, "Parent GameObject is required.").transform;
            var primitive = string.IsNullOrEmpty(request.Primitive)
                ? (PrimitiveType?)null
                : GameObjectToolSupport.ParsePrimitiveType(request.Primitive);
            var baseName = request.Name ?? request.Target ?? "GameObject";
            if (string.IsNullOrWhiteSpace(baseName))
                throw Error("E_GO_CREATE_NAME_INVALID", "game-object.create name must not be empty or whitespace.", "name", null);

            var items = new List<CreateItemPlan>(count);
            for (var index = 0; index < count; index++)
            {
                var item = new CreateItemPlan
                {
                    Name = count > 1 ? baseName + (index + 1) : baseName,
                    X = request.Positions == null ? request.X : null,
                    Y = request.Positions == null ? request.Y : null,
                    Z = request.Positions == null ? request.Z : null,
                    RotationX = request.Rotations == null ? request.RotationX : null,
                    RotationY = request.Rotations == null ? request.RotationY : null,
                    RotationZ = request.Rotations == null ? request.RotationZ : null,
                    ScaleX = request.Scales == null ? request.ScaleX : null,
                    ScaleY = request.Scales == null ? request.ScaleY : null,
                    ScaleZ = request.Scales == null ? request.ScaleZ : null,
                    LocalPosition = request.Positions == null ? null : request.Positions[index],
                    LocalRotation = request.Rotations == null ? null : request.Rotations[index],
                    LocalScale = request.Scales == null ? null : request.Scales[index]
                };
                GameObjectToolSupport.ValidateTransformMutationValues(
                    item.LocalPosition == null ? item.X : item.LocalPosition[0],
                    item.LocalPosition == null ? item.Y : item.LocalPosition[1],
                    item.LocalPosition == null ? item.Z : item.LocalPosition[2],
                    item.LocalRotation == null ? item.RotationX : item.LocalRotation[0],
                    item.LocalRotation == null ? item.RotationY : item.LocalRotation[1],
                    item.LocalRotation == null ? item.RotationZ : item.LocalRotation[2],
                    item.LocalScale == null ? item.ScaleX : item.LocalScale[0],
                    item.LocalScale == null ? item.ScaleY : item.LocalScale[1],
                    item.LocalScale == null ? item.ScaleZ : item.LocalScale[2]);
                items.Add(item);
            }
            return new CreatePlan
            {
                Count = count,
                Parent = parent,
                Primitive = primitive,
                Active = request.Active,
                TagValue = request.TagValue,
                LayerValue = request.LayerValue,
                Items = items
            };
        }

        private static void ValidateVectorArray(List<List<double>> values, int count, string field)
        {
            if (values == null) return;
            if (values.Count != count)
                throw Error("E_GO_BATCH_LENGTH_MISMATCH", field + " must contain exactly count vectors.", field, values.Count);
            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] == null || values[index].Count != 3)
                    throw Error("E_GO_BATCH_VECTOR_INVALID", field + "[" + index + "] must be an [x,y,z] vector.", field + "[" + index + "]", values[index] == null ? null : (object)values[index].Count);
            }
        }

        private static void RejectTransformConflict(List<List<double>> vectors, string field, double? x, double? y, double? z)
        {
            if (vectors != null && (x.HasValue || y.HasValue || z.HasValue))
                throw Error("E_GO_BATCH_TRANSFORM_CONFLICT", field + " cannot be combined with its scalar transform fields.", field, null);
        }

        private static bool HasScalarTransform(Request request)
        {
            return request.X.HasValue || request.Y.HasValue || request.Z.HasValue
                || request.RotationX.HasValue || request.RotationY.HasValue || request.RotationZ.HasValue
                || request.ScaleX.HasValue || request.ScaleY.HasValue || request.ScaleZ.HasValue;
        }

        private static void ApplyLocalTransforms(Transform transform, CreateItemPlan item)
        {
            if (item.LocalPosition != null) transform.localPosition = Vector(item.LocalPosition);
            if (item.LocalRotation != null) transform.localEulerAngles = Vector(item.LocalRotation);
            if (item.LocalScale != null) transform.localScale = Vector(item.LocalScale);
        }

        /// <summary>effect 后只按冻结 instance id 顺序重取对象，并验证 identity、parent 与全部请求状态。</summary>
        private static List<GameObjectDetail> Prove(CreatePlan plan, List<int> instanceIds)
        {
            if (instanceIds.Count != plan.Count)
                throw Error("E_GO_CREATE_SET_MISMATCH", "game-object.create did not produce the requested object count.", "count", instanceIds.Count);
            var uniqueIds = new HashSet<int>();
            var result = new List<GameObjectDetail>(plan.Count);
            for (var index = 0; index < instanceIds.Count; index++)
            {
                if (!uniqueIds.Add(instanceIds[index]))
                    throw Error("E_GO_CREATE_SET_MISMATCH", "game-object.create returned a duplicate object identity.", "instanceId", instanceIds[index]);
                var observed = EditorUtility.InstanceIDToObject(instanceIds[index]) as GameObject;
                var expected = plan.Items[index];
                if (observed == null || !observed.scene.IsValid() || !observed.scene.isLoaded
                    || observed.name != expected.Name || observed.transform.parent != plan.Parent
                    || (plan.Active.HasValue && observed.activeSelf != plan.Active.Value)
                    || (!string.IsNullOrEmpty(plan.TagValue) && observed.tag != plan.TagValue)
                    || (!string.IsNullOrEmpty(plan.LayerValue) && observed.layer != GameObjectToolSupport.ParseLayer(plan.LayerValue))
                    || !TransformMatches(observed.transform, expected))
                    throw Error("E_GO_CREATE_SET_MISMATCH", "A created GameObject did not match the frozen batch plan.", "index", index);
                result.Add(GameObjectToolSupport.BuildGameObjectDetail(observed));
            }
            return result;
        }

        private static bool TransformMatches(Transform transform, CreateItemPlan item)
        {
            if (item.LocalPosition != null && !Approximately(transform.localPosition, item.LocalPosition, false)) return false;
            if (item.LocalRotation != null && !Approximately(transform.localEulerAngles, item.LocalRotation, true)) return false;
            if (item.LocalScale != null && !Approximately(transform.localScale, item.LocalScale, false)) return false;
            if (item.X.HasValue && !Approximately(transform.position.x, item.X.Value)) return false;
            if (item.Y.HasValue && !Approximately(transform.position.y, item.Y.Value)) return false;
            if (item.Z.HasValue && !Approximately(transform.position.z, item.Z.Value)) return false;
            if (item.RotationX.HasValue && !AngleApproximately(transform.eulerAngles.x, item.RotationX.Value)) return false;
            if (item.RotationY.HasValue && !AngleApproximately(transform.eulerAngles.y, item.RotationY.Value)) return false;
            if (item.RotationZ.HasValue && !AngleApproximately(transform.eulerAngles.z, item.RotationZ.Value)) return false;
            if (item.ScaleX.HasValue && !Approximately(transform.localScale.x, item.ScaleX.Value)) return false;
            if (item.ScaleY.HasValue && !Approximately(transform.localScale.y, item.ScaleY.Value)) return false;
            return !item.ScaleZ.HasValue || Approximately(transform.localScale.z, item.ScaleZ.Value);
        }

        private static bool Approximately(Vector3 actual, List<double> expected, bool angle)
        {
            return angle
                ? AngleApproximately(actual.x, expected[0]) && AngleApproximately(actual.y, expected[1]) && AngleApproximately(actual.z, expected[2])
                : Approximately(actual.x, expected[0]) && Approximately(actual.y, expected[1]) && Approximately(actual.z, expected[2]);
        }

        private static bool Approximately(float actual, double expected)
        {
            return Mathf.Abs(actual - (float)expected) <= 0.0001f;
        }

        private static bool AngleApproximately(float actual, double expected)
        {
            return Mathf.Abs(Mathf.DeltaAngle(actual, (float)expected)) <= 0.0001f;
        }

        private static Vector3 Vector(List<double> values)
        {
            return new Vector3((float)values[0], (float)values[1], (float)values[2]);
        }

        private static UnityCliBridge.BridgeCommandException Error(string code, string message, string field, object actual)
        {
            return new UnityCliBridge.BridgeCommandException(
                code,
                message,
                new Dictionary<string, object> { { "field", field }, { "actual", actual } },
                new[] { "Inspect tools describe game-object.create and submit one valid single-object or batch request." });
        }
    }
}
#endif
