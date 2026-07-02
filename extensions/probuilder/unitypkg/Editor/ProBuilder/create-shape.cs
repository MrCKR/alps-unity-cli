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

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var shapeType = request.ShapeType;
            if (!IsSupportedShape(shapeType))
                throw ProBuilderErrors.SchemaInvalid("Unsupported shapeType for probuilder.create-shape: " + shapeType + ". Supported values: " + SupportedShapeValues + ".");

            var name = request.Name ?? ("ProBuilder_" + shapeType);
            var position = new Vector3((float)request.X, (float)request.Y, (float)request.Z);

            var go = ProBuilderReflection.CreateShape(shapeType, position, Quaternion.identity);
            if (go == null)
            {
                // 回退:创建 Unity 基本体并转换
                var shape = NormalizeShape(shapeType);
                var primitive = CreateUnityPrimitive(shape);
                if (primitive == null)
                    throw ProBuilderErrors.OperationFailed("create-shape", "Failed to create shape: " + shapeType);
                go = primitive;
                go.transform.position = position;
                ProBuilderReflection.AddProBuilderMesh(go);
            }

            go.name = name;

            // 应用尺寸(如指定)
            if (request.Width.HasValue || request.Height.HasValue || request.Depth.HasValue)
            {
                var scale = go.transform.localScale;
                go.transform.localScale = new Vector3(
                    (float)(request.Width ?? scale.x),
                    (float)(request.Height ?? scale.y),
                    (float)(request.Depth ?? scale.z));
            }

            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Shape");
            EditorSceneManager.MarkSceneDirty(go.scene);

            var hasPbMesh = ProBuilderReflection.HasProBuilderMesh(go);
            return new Result
            {
                Object = new ObjectRef
                {
                    Id = go.GetInstanceID(),
                    Name = go.name,
                    Path = HierarchyPath(go),
                    Shape = shapeType,
                    HasProBuilderMesh = hasPbMesh
                }
            };
        }

        private const string SupportedShapeValues = "Cube, Cylinder, Sphere, Plane, Cone, Torus, Pipe, Arch, Stair, CurvedStair, Door, Prism";

        private static string NormalizeShape(string shapeType)
        {
            var s = (shapeType ?? "Cube").Trim();
            if (string.Equals(s, "box", StringComparison.OrdinalIgnoreCase)) return "Cube";
            if (string.Equals(s, "quad", StringComparison.OrdinalIgnoreCase)) return "Plane";
            return s;
        }

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

        private static GameObject CreateUnityPrimitive(string shape)
        {
            switch (shape.ToLowerInvariant())
            {
                case "cube": return GameObject.CreatePrimitive(PrimitiveType.Cube);
                case "sphere": return GameObject.CreatePrimitive(PrimitiveType.Sphere);
                case "cylinder": return GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                case "plane": return GameObject.CreatePrimitive(PrimitiveType.Plane);
                case "cone": return GameObject.CreatePrimitive(PrimitiveType.Capsule); // 最近似
                default: return null;
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
