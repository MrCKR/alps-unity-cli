#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.ProBuilder;
using UnityEngine.ProBuilder.MeshOperations;
namespace UnityCli.ProBuilder
{
    public class CreatePolyShapeTool : IUnityCliTool<CreatePolyShapeTool.Request, CreatePolyShapeTool.Result>
    {
        public sealed class Request { [UnityCliRequired] public List<Point3D> Points { get; set; } public double? ExtrudeHeight { get; set; } public bool FlipNormals { get; set; } public string Name { get; set; } }
        public sealed class Point3D { public double X { get; set; } public double Y { get; set; } public double Z { get; set; } }
        public sealed class Result { public ObjectRef Object { get; set; } }
        public sealed class ObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public long FaceCount { get; set; } public long VertexCount { get; set; } }
        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.create-poly-shape",
            Summary = "Create a ProBuilder mesh from an extruded polygon.",
            Description = "Create a ProBuilder mesh from at least three polygon points, with optional extrusion and normal direction.",
            Group = "probuilder",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 15000,
            MaxTimeoutMs = 30000,
            Examples = new[]
            {
                new ToolExample { Description = "创建水平三角形", Input = new Request { FlipNormals = false, Name = "ProBuilder_Triangle", Points = new List<Point3D> { new Point3D { X = 0, Y = 0, Z = 0 }, new Point3D { X = 1, Y = 0, Z = 0 }, new Point3D { X = 0, Y = 0, Z = 1 } } } }
            }
        };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable) throw ProBuilderErrors.PackageNotAvailable();
            var name = request.Name ?? "ProBuilder_PolyShape";
            var height = ProBuilderOperationProof.CheckedFloat(request.ExtrudeHeight ?? 1d, "extrudeHeight");
            var flipNormals = request.FlipNormals;
            if (string.IsNullOrWhiteSpace(name) || request.Points == null || request.Points.Any(point => point == null)) throw ProBuilderErrors.SchemaInvalid("name must be non-empty and points must be objects.");
            var points = request.Points.Select(point => ProBuilderOperationProof.CheckedVector3(point.X, point.Y, point.Z, "points")).ToList();
            if (points.Count < 3) throw ProBuilderErrors.SchemaInvalid("At least 3 points are required for create-poly-shape.");
            ctx.CommitEffect();
            GameObject go = null;
            var id = 0;
            try { go = new GameObject(name); id = go.GetInstanceID(); Undo.RegisterCreatedObjectUndo(go, "ProBuilder Create Poly Shape"); var mesh = go.AddComponent<ProBuilderMesh>(); var action = AppendElements.CreateShapeFromPolygon(mesh, points, height, flipNormals); if (mesh == null || action == null || action.status != ActionResult.Status.Success) throw ProBuilderErrors.OutcomeUnknown("create-poly-shape", "invoke", "CreateShapeFromPolygon failed."); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("create-poly-shape", "invoke", ex.Message); }
            try { var mesh = ProBuilderReflection.GetProBuilderMesh(go); if (!ProBuilderReflection.RefreshMesh(mesh)) throw ProBuilderErrors.OutcomeUnknown("create-poly-shape", "readback", "ProBuilder refresh failed."); EditorSceneManager.MarkSceneDirty(go.scene); var fresh = EditorUtility.InstanceIDToObject(id) as GameObject; var freshMesh = ProBuilderReflection.GetProBuilderMesh(fresh); if (fresh == null || fresh.name != name || !ProBuilderAtomicReadback.PolyShapeMatchesRequest(fresh, freshMesh, points, height, flipNormals)) throw ProBuilderErrors.OutcomeUnknown("create-poly-shape", "postcondition", "Created polygon could not be confirmed."); return new System.Threading.Tasks.ValueTask<Result>(new Result { Object = new ObjectRef { Id = fresh.GetInstanceID(), Name = fresh.name, Path = HierarchyPath(fresh), FaceCount = ProBuilderReflection.GetFaceCount(freshMesh), VertexCount = ProBuilderReflection.GetVertexCount(freshMesh) } }); }
            catch (UnityCliBridge.BridgeCommandException) { throw; } catch (Exception ex) { throw ProBuilderErrors.OutcomeUnknown("create-poly-shape", "readback", ex.Message); }
        }
        private static string HierarchyPath(GameObject go) { var names = new Stack<string>(); for (var current = go.transform; current != null; current = current.parent) names.Push(current.name); return string.Join("/", names.ToArray()); }
    }
}
#endif
