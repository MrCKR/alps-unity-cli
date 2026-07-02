#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.bridge-edges — 在两条边之间创建桥接面。
    /// </summary>
    public class BridgeEdgesTool : IUnityCliTool<BridgeEdgesTool.Request, BridgeEdgesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            [UnityCliRequired] public EdgeRef EdgeA { get; set; }
            [UnityCliRequired] public EdgeRef EdgeB { get; set; }
            public bool AllowNonManifold { get; set; }
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long NewFaceCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.bridge-edges",
            Summary = "Bridge two edges on a ProBuilder mesh to create a connecting face.",
            Description = "Create a bridge between two edges on a ProBuilder mesh. Each edge is specified as a vertex-pair {a,b}. Optionally allow non-manifold geometry.",
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
                new ToolExample { Description = "桥接两条边", Input = new Request { Target = "MyCube", EdgeA = new EdgeRef { A = 0, B = 1 }, EdgeB = new EdgeRef { A = 2, B = 3 } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("bridge-edges", "No ProBuilderMesh on target.");

            if (request.EdgeA == null || request.EdgeB == null)
                throw ProBuilderErrors.SchemaInvalid("edgeA and edgeB parameters are required (as {a, b} vertex index pairs).");

            Undo.RecordObject(go, "ProBuilder Bridge Edges");
            var bridged = ProBuilderReflection.BridgeEdges(mesh, (int)request.EdgeA.A, (int)request.EdgeA.B, (int)request.EdgeB.A, (int)request.EdgeB.B, request.AllowNonManifold);
            if (!bridged)
                throw ProBuilderErrors.OperationFailed("bridge-edges", "AppendElements.Bridge method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            var newFaceCount = ProBuilderReflection.GetFaceCount(mesh);
            return new Result { NewFaceCount = newFaceCount };
        }
    }
}
#endif
