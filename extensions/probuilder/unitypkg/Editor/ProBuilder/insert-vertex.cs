#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.insert-vertex — 在边或面上插入新顶点。
    /// </summary>
    public class InsertVertexTool : IUnityCliTool<InsertVertexTool.Request, InsertVertexTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public EdgeRef Edge { get; set; }                       // 可选,边 {a,b}
            public long? FaceIndex { get; set; }                    // 可选,面索引
            public double X { get; set; }                           // 默认 0
            public double Y { get; set; }                           // 默认 0
            public double Z { get; set; }                           // 默认 0
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public bool Inserted { get; set; }
            public long VertexCountBefore { get; set; }
            public long VertexCountAfter { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.insert-vertex",
            Summary = "Insert a new vertex on an edge or face of a ProBuilder mesh.",
            Description = "Insert a new vertex on an edge or face of a ProBuilder mesh. When Edge is provided, inserts on that edge; when FaceIndex is provided, inserts in that face. The insertion point is specified by X/Y/Z coordinates.",
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
                new ToolExample { Description = "在边上插入顶点", Input = new Request { Target = "MyCube", Edge = new EdgeRef { A = 0, B = 1 }, X = 0.5, Y = 0, Z = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("insert-vertex", "No ProBuilderMesh on target.");

            var point = new Vector3((float)request.X, (float)request.Y, (float)request.Z);

            if ((request.Edge != null) == request.FaceIndex.HasValue)
                throw ProBuilderErrors.SchemaInvalid("Pass exactly one of edge or faceIndex for insert-vertex.");

            Undo.RecordObject(go, "ProBuilder Insert Vertex");

            var vertexCountBefore = ProBuilderReflection.GetVertexCount(mesh);
            var inserted = request.Edge != null
                ? ProBuilderReflection.InsertVertexOnEdgeByPair(mesh, (int)request.Edge.A, (int)request.Edge.B, point)
                : ProBuilderReflection.InsertVertexInFace(mesh, (int)request.FaceIndex.Value, point);
            var vertexCountAfter = ProBuilderReflection.GetVertexCount(mesh);
            if (!inserted || vertexCountBefore < 0 || vertexCountAfter < 0)
                throw ProBuilderErrors.OperationFailed("insert-vertex", "ProBuilder 5.x AppendElements insertion failed.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Inserted = true,
                VertexCountBefore = vertexCountBefore,
                VertexCountAfter = vertexCountAfter
            });
        }
    }
}
#endif
