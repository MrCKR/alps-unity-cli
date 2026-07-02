#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.mesh-info — 获取 ProBuilder 网格的详细信息。
    /// </summary>
    public class MeshInfoTool : IUnityCliTool<MeshInfoTool.Request, MeshInfoTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Include { get; set; }                     // 可选,保留字段(summary/faces/edges/all)
        }

        public sealed class Result
        {
            public long VertexCount { get; set; }
            public long FaceCount { get; set; }
            public long EdgeCount { get; set; }
            public BoundsInfo Bounds { get; set; }
            public List<MaterialInfo> Materials { get; set; }
        }

        public sealed class BoundsInfo
        {
            public Vec3 Center { get; set; }
            public Vec3 Size { get; set; }
        }

        public sealed class Vec3
        {
            public double X { get; set; }
            public double Y { get; set; }
            public double Z { get; set; }
        }

        public sealed class MaterialInfo
        {
            public string Name { get; set; }
            public string Path { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.mesh-info",
            Summary = "Read detailed vertex, face, edge, bounds, and material info for a ProBuilder mesh.",
            Description = "Get detailed information about a ProBuilder mesh including vertex, face, edge counts, renderer bounds, and assigned materials. Edge count is approximated as faceCount * 3 / 2 for triangle meshes.",
            Group = "probuilder",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 10000,
            MaxTimeoutMs = 30000,
            SupportsJob = false,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "读取网格信息", Input = new Request { Target = "MyCube" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(ctx.InputJson);
            if (go == null)
                throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null)
                throw ProBuilderErrors.OperationFailed("mesh-info", "GameObject does not have a ProBuilderMesh component.");

            var faceCount = ProBuilderReflection.GetFaceCount(mesh);
            var vertexCount = ProBuilderReflection.GetVertexCount(mesh);

            var result = new Result
            {
                VertexCount = vertexCount,
                FaceCount = faceCount,
                EdgeCount = faceCount * 3 / 2 // 三角形近似
            };

            var renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                var bounds = renderer.bounds;
                result.Bounds = new BoundsInfo
                {
                    Center = new Vec3 { X = bounds.center.x, Y = bounds.center.y, Z = bounds.center.z },
                    Size = new Vec3 { X = bounds.size.x, Y = bounds.size.y, Z = bounds.size.z }
                };

                var materials = new List<MaterialInfo>();
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null) continue;
                    materials.Add(new MaterialInfo
                    {
                        Name = mat.name,
                        Path = AssetDatabase.GetAssetPath(mat)
                    });
                }
                result.Materials = materials;
            }

            return result;
        }
    }
}
#endif
