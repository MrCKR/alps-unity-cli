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
            public long InsertedVertexIndex { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.insert-vertex",
            Summary = "Insert a new vertex on an edge or face of a ProBuilder mesh.",
            Description = "Insert a new vertex on an edge or face of a ProBuilder mesh. When Edge is provided, inserts on that edge; when FaceIndex is provided, inserts in that face. The insertion point is specified by X/Y/Z coordinates.",
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
                new ToolExample { Description = "在边上插入顶点", Input = new Request { Target = "MyCube", Edge = new EdgeRef { A = 0, B = 1 }, X = 0.5, Y = 0, Z = 0 } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("insert-vertex", "No ProBuilderMesh on target.");

            var point = new Vector3((float)request.X, (float)request.Y, (float)request.Z);

            Undo.RecordObject(go, "ProBuilder Insert Vertex");

            int insertedIndex = -1;
            if (request.Edge != null)
            {
                insertedIndex = ProBuilderReflection.InsertVertexOnEdgeByPair(mesh, (int)request.Edge.A, (int)request.Edge.B, point);
            }
            else if (request.FaceIndex.HasValue)
            {
                var inserted = ProBuilderReflection.InsertVertexInFace(mesh, (int)request.FaceIndex.Value, point);
                if (inserted) insertedIndex = ProBuilderReflection.GetVertexCount(mesh) - 1;
            }
            else
            {
                // 回退:用 null edge 尝试旧方法
                insertedIndex = ProBuilderReflection.InsertVertexOnEdge(mesh, null, point);
            }

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { InsertedVertexIndex = insertedIndex };
        }
    }
}
#endif
