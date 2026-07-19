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
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = true,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "在闭合网格上桥接两条边", Input = new Request { Target = "MyCube", EdgeA = new EdgeRef { A = 0, B = 1 }, EdgeB = new EdgeRef { A = 5, B = 7 }, AllowNonManifold = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("bridge-edges", "No ProBuilderMesh on target.");

            if (request.EdgeA == null || request.EdgeB == null)
                throw ProBuilderErrors.SchemaInvalid("edgeA and edgeB parameters are required (as {a, b} vertex index pairs).");
            var edgeA = new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(request.EdgeA.A, "edgeA.a"), b = ProBuilderOperationProof.CheckedInt(request.EdgeA.B, "edgeA.b") };
            var edgeB = new ProBuilderInput.EdgePair { a = ProBuilderOperationProof.CheckedInt(request.EdgeB.A, "edgeB.a"), b = ProBuilderOperationProof.CheckedInt(request.EdgeB.B, "edgeB.b") };
            var edgeArray = ProBuilderOperationProof.ValidateEdges(mesh, null, new[] { edgeA, edgeB });
            var before = ProBuilderOperationProof.Capture(mesh, edges: edgeArray);
            if (before == null) throw ProBuilderErrors.OperationFailed("bridge-edges", "Requested edge geometry is not readable.");
            ctx.CommitEffect();
            Undo.RecordObject(go, "ProBuilder Bridge Edges");
            var bridged = ProBuilderReflection.BridgeEdges(mesh, edgeA.a, edgeA.b, edgeB.a, edgeB.b, request.AllowNonManifold);
            if (!bridged)
                throw ProBuilderErrors.OperationFailed("bridge-edges", "AppendElements.Bridge method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);
            var freshMesh = ProBuilderOperationProof.ResolveFreshMesh(request.Target, go);
            var after = ProBuilderOperationProof.Capture(freshMesh, edges: edgeArray);
            if (!ProBuilderOperationProof.HasTopologyExpansion(before, after))
                throw ProBuilderErrors.OperationFailed("bridge-edges", "Requested edges did not produce readable bridge topology.");

            var newFaceCount = ProBuilderReflection.GetFaceCount(freshMesh);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { NewFaceCount = newFaceCount });
        }
    }
}
#endif
