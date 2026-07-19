#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.create-shape — 在场景中创建 ProBuilder 基本形状。
    /// </summary>
    public class CreateShapeTool : IUnityCliTool<CreateShapeTool.Request, CreateShapeTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string ShapeType { get; set; }
            public string Name { get; set; }                         // 可选,默认 ProBuilder_<shapeType>
            public double X { get; set; }                            // 默认 0
            public double Y { get; set; }                            // 默认 0
            public double Z { get; set; }                            // 默认 0
            public double? Width { get; set; }                       // 可选
            public double? Height { get; set; }                      // 可选
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
            Description = "Create a ProBuilder primitive shape in the scene. Supports Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism. Optionally set position and dimensions.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
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
            if (!IsSupportedShape(shapeType))
                throw ProBuilderErrors.SchemaInvalid("Unsupported shapeType for probuilder.create-shape: " + shapeType + ". Supported values: " + SupportedShapeValues + ".");

            var name = request.Name ?? ("ProBuilder_" + shapeType);
            var position = ProBuilderOperationProof.CheckedVector3(request.X, request.Y, request.Z, "position");
            var width = request.Width.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Width.Value, "width") : (float?)null;
            var height = request.Height.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Height.Value, "height") : (float?)null;
            var depth = request.Depth.HasValue ? ProBuilderOperationProof.CheckedPositiveFloat(request.Depth.Value, "depth") : (float?)null;

            ctx.CommitEffect();
            var go = ProBuilderReflection.CreateShape(shapeType, position, Quaternion.identity);
            if (go == null)
                throw ProBuilderErrors.OperationFailed("create-shape", "ProBuilder 5.x ShapeGenerator.CreateShape failed for shape: " + shapeType);

            go.name = name;

            // 按实际 bounds 缩放，保证公开尺寸字段表示成品尺寸而非 transform 数值。
            var renderer = go.GetComponent<Renderer>();
            if (renderer == null)
                throw ProBuilderErrors.OperationFailed("create-shape", "Created shape has no Renderer for requested bounds verification.");
            var actualSize = renderer.bounds.size;
            var requestedSize = new Vector3(width ?? actualSize.x, height ?? actualSize.y, depth ?? actualSize.z);
            if (actualSize.x <= 0 || actualSize.y <= 0 || actualSize.z <= 0 || requestedSize.x <= 0 || requestedSize.y <= 0 || requestedSize.z <= 0)
                throw ProBuilderErrors.OperationFailed("create-shape", "Created shape has unreadable bounds.");
            go.transform.localScale = new Vector3(requestedSize.x / actualSize.x, requestedSize.y / actualSize.y, requestedSize.z / actualSize.z);

            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Shape");
            EditorSceneManager.MarkSceneDirty(go.scene);

            var freshGo = ProBuilderInput.ResolveTarget(go.GetInstanceID().ToString());
            var createdMesh = ProBuilderReflection.GetProBuilderMesh(freshGo);
            var hasPbMesh = ProBuilderReflection.HasProBuilderMesh(freshGo);
            if (freshGo == null || freshGo.name != name || !hasPbMesh || !ProBuilderRequestProof.CreatedShapeMatchesRequest(freshGo, createdMesh, position, requestedSize))
                throw ProBuilderErrors.OperationFailed("create-shape", "Created shape did not retain readable bounds and position.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Object = new ObjectRef
                {
                    Id = go.GetInstanceID(),
                    Name = go.name,
                    Path = HierarchyPath(go),
                    Shape = null,
                    HasProBuilderMesh = hasPbMesh
                }
            });
        }

        private const string SupportedShapeValues = "Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism";

        /// <summary>验证 shapeType 与公开 descriptor enum 一致,避免未知值静默创建错误形状。</summary>
        private static bool IsSupportedShape(string shapeType)
        {
            switch (shapeType ?? string.Empty)
            {
                case "Cube":
                case "Cylinder":
                case "Sphere":
                case "Plane":
                case "Cone":
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
