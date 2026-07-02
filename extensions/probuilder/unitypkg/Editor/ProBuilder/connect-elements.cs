#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.connect-elements — 在选定边或面之间插入连接边。
    /// </summary>
    public class ConnectElementsTool : IUnityCliTool<ConnectElementsTool.Request, ConnectElementsTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public List<long> EdgeIndices { get; set; }            // 可选
            public List<EdgeRef> Edges { get; set; }               // 可选
            public List<long> FaceIndices { get; set; }            // 可选
        }

        public sealed class EdgeRef
        {
            public long A { get; set; }
            public long B { get; set; }
        }

        public sealed class Result
        {
            public long ConnectedCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.connect-elements",
            Summary = "Insert a connecting edge loop across selected edges or faces of a ProBuilder mesh.",
            Description = "Connect selected edges or faces on a ProBuilder mesh by inserting a new edge loop. Specify edgeIndices, vertex-pair edges [{a,b}], or faceIndices. At least one selection is required.",
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
                new ToolExample { Description = "连接两条边", Input = new Request { Target = "MyCube", Edges = new List<EdgeRef> { new EdgeRef { A = 0, B = 1 }, new EdgeRef { A = 2, B = 3 } } } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("connect-elements", "No ProBuilderMesh on target.");

            int[] edgeIndices = request.EdgeIndices?.Select(i => (int)i).ToArray();
            ProBuilderInput.EdgePair[] edgePairs = request.Edges?.Select(e => new ProBuilderInput.EdgePair { a = (int)e.A, b = (int)e.B }).ToArray();
            int[] faceIndices = request.FaceIndices?.Select(i => (int)i).ToArray();

            if ((edgeIndices == null || edgeIndices.Length == 0) &&
                (edgePairs == null || edgePairs.Length == 0) &&
                (faceIndices == null || faceIndices.Length == 0))
                throw ProBuilderErrors.SchemaInvalid("Either edgeIndices, edges, or faceIndices parameter is required for connect-elements.");

            Undo.RecordObject(go, "ProBuilder Connect Elements");
            var connected = ProBuilderReflection.ConnectElements(mesh, edgeIndices, edgePairs, faceIndices);
            if (!connected)
                throw ProBuilderErrors.OperationFailed("connect-elements", "ConnectElements.Connect method not found or failed in ProBuilder assembly.");

            ProBuilderReflection.RefreshMesh(mesh);
            EditorSceneManager.MarkSceneDirty(go.scene);

            return new Result { ConnectedCount = 1 };
        }
    }
}
#endif
