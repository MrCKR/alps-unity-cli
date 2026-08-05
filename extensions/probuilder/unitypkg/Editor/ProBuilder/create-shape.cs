#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.create-shape - 在场景中创建 ProBuilder 基本形状
    /// </summary>
    public class CreateShapeTool : IUnityCliTool<CreateShapeTool.Request, CreateShapeTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string ShapeType { get; set; }
            public string Name { get; set; }                         // 可选，默认 ProBuilder_<shapeType>
            public double X { get; set; }                            // 默认 0
            public double Y { get; set; }                            // 默认 0
            public double Z { get; set; }                            // ?? 0
            public double? Width { get; set; }                       // 可选
            public double? Height { get; set; }                       // 可选
            public double? Depth { get; set; }                       // 可选
        }

        public sealed class Result
        {
            public ObjectRef Object { get; set; }
        }

        public sealed class ObjectRef
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string Shape { get; set; }
            public bool HasProBuilderMesh { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.create-shape",
            Summary = "Create a ProBuilder primitive shape in the active scene.",
            Description = "Create a default primitive from the ProBuilder 5.2.4 topology record. Supports Cube, Stair, CurvedStair, Prism, Cylinder, Plane, Door, Pipe, Cone, Sprite, Arch, Sphere, and Torus. Plane and Sprite have a zero Y normal axis, so height is not accepted. Optionally set position and dimensions whose topology-record-derived scale remains finite. Other ProBuilder package versions are rejected before scene mutation.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "创建立方体", Input = new Request { ShapeType = "Cube" } },
                new ToolExample { Description = "创建指定尺寸的圆柱", Input = new Request { ShapeType = "Cylinder", Width = 2, Height = 4, Depth = 2 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var shapeType = request.ShapeType;
            var requestedName = request.Name;
            if (!IsSupportedShape(shapeType))
                throw ProBuilderErrors.SchemaInvalid("Unsupported shapeType for probuilder.create-shape: " + shapeType + ". Supported values: " + SupportedShapeValues + ".");

            var name = requestedName ?? ("ProBuilder_" + shapeType);
            if (string.IsNullOrWhiteSpace(name)) throw ProBuilderErrors.SchemaInvalid("name must be non-empty.");
            var activeScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !activeScene.isLoaded) throw ProBuilderErrors.SchemaInvalid("Active scene must be valid and loaded.");
            var position = ProBuilderOperationProof.CheckedVector3(request.X, request.Y, request.Z, "position");
            var width = request.Width.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Width.Value, "width") : (float?)null;
            var height = request.Height.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Height.Value, "height") : (float?)null;
            var depth = request.Depth.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Depth.Value, "depth") : (float?)null;
            // Plane does not support height; ShapeGenerator.CreateShape creates Plane and Sprite through GeneratePlane(... Axis.Up).
            if (HasZeroYAxis(shapeType) && height.HasValue) throw ProBuilderErrors.SchemaInvalid(shapeType + " does not support an explicit height along its zero normal axis.");

            // 所有成功条件必须在 effect 前冻结，禁止由创建结果反推请求语义。
            var plan = ProBuilderVertexObjectCompletionProof.CaptureCreateShapePlan(shapeType, name, position, width, height, depth, activeScene);
            if (plan == null)
                throw ProBuilderErrors.SchemaInvalid("The ProBuilder 5.2.4 topology record or finite center-pivot bounds are unavailable; create-shape is fail-closed until the supported package is installed.");
            ctx.CommitEffect();
            try
            {
                var go = ProBuilderReflection.CreateShape(shapeType, position, Quaternion.identity);
                if (go == null)
                    throw ProBuilderErrors.OutcomeUnknown("create-shape", "invoke", "ProBuilder 5.2.4 ShapeGenerator.CreateShape failed for shape: " + shapeType);
                go.name = name;
                go.transform.localScale = plan.LocalScale;
                Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Shape");
                EditorSceneManager.MarkSceneDirty(go.scene);

                var createdIdentity = ProBuilderOperationProof.CaptureIdentity(go);
                var freshGo = ProBuilderOperationProof.ResolveFreshObject(go.GetInstanceID().ToString(), createdIdentity);
                if (freshGo == null)
                    throw ProBuilderErrors.OutcomeUnknown("create-shape", "readback", "Created object identity could not be resolved.");
                var createdMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
                var hasPbMesh = ProBuilderReflection.HasProBuilderMesh(freshGo);
                if (createdMesh == null)
                    throw ProBuilderErrors.OutcomeUnknown("create-shape", "readback", "Created ProBuilderMesh could not be read.");
                var mismatch = hasPbMesh ? ProBuilderVertexObjectCompletionProof.CreatedShapeMismatch(plan, freshGo, createdMesh, createdIdentity) : "probuilder-component";
                if (mismatch != null)
                    throw ProBuilderErrors.OutcomeUnknown("create-shape", "postcondition", "Fresh readback did not prove the frozen ProBuilder 5.2.4 condition: " + mismatch + ".");
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Object = new ObjectRef
                    {
                        Id = freshGo.GetInstanceID(),
                        Name = freshGo.name,
                        Path = HierarchyPath(freshGo),
                        Shape = shapeType,
                        HasProBuilderMesh = hasPbMesh
                    }
                });
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (System.Exception exception)
            {
                throw ProBuilderErrors.OutcomeUnknown("create-shape", "postcondition", exception.Message);
            }
        }

        private const string SupportedShapeValues = "Cube, Stair, CurvedStair, Prism, Cylinder, Plane, Door, Pipe, Cone, Sprite, Arch, Sphere, Torus";

        /// <summary>内部证明辅助逻辑。</summary>
        private static bool IsSupportedShape(string shapeType)
        {
            switch (shapeType ?? string.Empty)
            {
                case "Cube":
                case "Cylinder":
                case "Sphere":
                case "Plane":
                case "Cone":
                case "Sprite":
                case "Torus":
                case "Pipe":
                case "Arch":
                case "Stair":
                case "CurvedStair":
                case "Door":
                case "Prism":
                    return true;
                default:
                    return false;
            }
        }

        private static bool HasZeroYAxis(string shapeType) => shapeType == "Plane" || shapeType == "Sprite";

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
