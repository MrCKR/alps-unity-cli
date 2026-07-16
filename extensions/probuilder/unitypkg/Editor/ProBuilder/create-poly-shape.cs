#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.create-poly-shape — 从多边形点集创建 ProBuilder 网格。
    /// </summary>
    public class CreatePolyShapeTool : IUnityCliTool<CreatePolyShapeTool.Request, CreatePolyShapeTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public List<Point3D> Points { get; set; }
            public double? ExtrudeHeight { get; set; }              // 可选,默认 1
            public bool FlipNormals { get; set; }                   // 默认 false
            public string Name { get; set; }                        // 可选,默认 ProBuilder_PolyShape
        }

        public sealed class Point3D
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
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
            public long FaceCount { get; set; }
            public long VertexCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.create-poly-shape",
            Summary = "Build a ProBuilder mesh by extruding a 2D polygon point set.",
            Description = "Create a ProBuilder mesh from a set of 2D polygon points extruded to a given height. Requires at least 3 points. Optionally flip normals and name the created GameObject.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "从四边形点集创建挤出体", Input = new Request { Points = new List<Point3D> { new Point3D { X = 0, Y = 0, Z = 0 }, new Point3D { X = 1, Y = 0, Z = 0 }, new Point3D { X = 1, Y = 0, Z = 1 }, new Point3D { X = 0, Y = 0, Z = 1 } }, ExtrudeHeight = 1 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var name = request.Name ?? "ProBuilder_PolyShape";
            var height = (float)(request.ExtrudeHeight ?? 1);
            var flipNormals = request.FlipNormals;

            var points = request.Points?.Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z)).ToList();
            if (points == null || points.Count < 3)
                throw ProBuilderErrors.SchemaInvalid("At least 3 points are required for create-poly-shape.");

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Poly Shape");

            var pbMesh = ProBuilderReflection.AddProBuilderMesh(go);
            if (pbMesh == null)
            {
                UnityEngine.Object.DestroyImmediate(go);
                throw ProBuilderErrors.OperationFailed("create-poly-shape", "Failed to add ProBuilderMesh component.");
            }

            var created = ProBuilderReflection.CreateShapeFromPolygon(pbMesh, points, height, flipNormals);
            if (!created)
            {
                UnityEngine.Object.DestroyImmediate(go);
                throw ProBuilderErrors.OperationFailed("create-poly-shape", "CreateShapeFromPolygon method not found or failed in ProBuilder assembly.");
            }

            ProBuilderReflection.RefreshMesh(pbMesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var faceCount = ProBuilderReflection.GetFaceCount(pbMesh);
            var vertexCount = ProBuilderReflection.GetVertexCount(pbMesh);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Object = new ObjectRef
                {
                    Id = go.GetInstanceID(),
                    Name = go.name,
                    Path = HierarchyPath(go),
                    FaceCount = faceCount,
                    VertexCount = vertexCount
                }
            });
        }

        private static string HierarchyPath(GameObject go)
        {
            var names = new Stack<string>();
            var current = go.transform;
            while (current != null) { names.Push(current.name); current = current.parent; }
            return string.Join("/", names.ToArray());
        }
    }
}
#endif
