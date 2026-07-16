#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.select-faces — 按方向/增长/洪水/循环/环选 ProBuilder 网格的面。
    /// </summary>
    public class SelectFacesTool : IUnityCliTool<SelectFacesTool.Request, SelectFacesTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Direction { get; set; }                   // 可选:north/south/east/west/up/down
            public List<long> SeedFaceIndices { get; set; }         // grow/flood/loop/ring 的种子面
            public long? Grow { get; set; }                         // 可选:增长迭代次数
            public bool Flood { get; set; }                         // 默认 false
            public bool Loop { get; set; }                          // 默认 false
            public bool Ring { get; set; }                          // 默认 false
        }

        public sealed class Result
        {
            public List<long> FaceIndices { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.select-faces",
            Summary = "Resolve ProBuilder face indices by direction or the 5.x ElementSelection grow, flood, loop, and ring APIs.",
            Description = "Return matching face indices without changing Unity selection. Choose exactly one mode. Direction matches cardinal normals. Grow, loop, and ring require seedFaceIndices; flood uses seeds when provided and otherwise returns all faces.",
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
                new ToolExample { Description = "选择朝上的面", Input = new Request { Target = "MyCube", Direction = "up" } },
                new ToolExample { Description = "选择所有面", Input = new Request { Target = "MyCube", Flood = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (!ProBuilderReflection.IsAvailable)
                throw ProBuilderErrors.PackageNotAvailable();

            var go = ProBuilderInput.ResolveTarget(request.Target);
            if (go == null) throw ProBuilderErrors.TargetNotFound(request.Target);

            var mesh = ProBuilderReflection.GetProBuilderMesh(go);
            if (mesh == null) throw ProBuilderErrors.OperationFailed("select-faces", "No ProBuilderMesh on target.");

            var allFaces = ProBuilderReflection.GetAllFaces(mesh);
            if (allFaces == null)
                throw ProBuilderErrors.OperationFailed("select-faces", "Could not read faces from ProBuilderMesh.");

            var direction = request.Direction;
            var flood = request.Flood;
            var grow = request.Grow;
            var loop = request.Loop;
            var ring = request.Ring;

            var modeCount = (!string.IsNullOrEmpty(direction) ? 1 : 0) + (grow.HasValue ? 1 : 0) + (flood ? 1 : 0) + (loop ? 1 : 0) + (ring ? 1 : 0);
            if (modeCount != 1)
                throw ProBuilderErrors.SchemaInvalid("Choose exactly one of direction, grow, flood, loop, or ring.");

            var selectedIndices = new List<int>();
            if (!string.IsNullOrEmpty(direction))
            {
                var targetDir = ParseDirection(direction);
                const float tolerance = 0.7f;
                for (int i = 0; i < allFaces.Length; i++)
                {
                    var normal = ProBuilderReflection.ComputeFaceNormal(mesh, allFaces.GetValue(i));
                    if (Vector3.Dot(normal, targetDir) > tolerance)
                        selectedIndices.Add(i);
                }
            }
            else if (flood && (request.SeedFaceIndices == null || request.SeedFaceIndices.Count == 0))
            {
                for (int i = 0; i < allFaces.Length; i++)
                    selectedIndices.Add(i);
            }
            else
            {
                if (request.SeedFaceIndices == null || request.SeedFaceIndices.Count == 0)
                    throw ProBuilderErrors.SchemaInvalid("seedFaceIndices is required for grow, seeded flood, loop, and ring.");
                if (grow.HasValue && grow.Value < 1)
                    throw ProBuilderErrors.SchemaInvalid("grow must be at least 1.");

                var seedIndices = request.SeedFaceIndices.ConvertAll(value => (int)value).ToArray();
                var seeds = ProBuilderReflection.GetFacesByIndices(mesh, seedIndices);
                if (seeds == null || seeds.Length != seedIndices.Length)
                    throw ProBuilderErrors.SchemaInvalid("seedFaceIndices contains an invalid face index.");

                var operation = grow.HasValue ? "grow" : flood ? "flood" : loop ? "loop" : "ring";
                var selectedFaces = ProBuilderReflection.SelectFaces(mesh, seeds, operation, (int)(grow ?? 1));
                if (selectedFaces == null)
                    throw ProBuilderErrors.OperationFailed("select-faces", "ProBuilder 5.x ElementSelection failed for operation: " + operation);
                selectedIndices.AddRange(ResolveFaceIndices(allFaces, selectedFaces));
            }

            selectedIndices.Sort();

            // 转 long 列表
            var resultIndices = new List<long>();
            foreach (var idx in selectedIndices) resultIndices.Add(idx);

            return new System.Threading.Tasks.ValueTask<Result>(new Result { FaceIndices = resultIndices });
        }

        /// <summary>将公开方向名称映射为 Unity 局部空间方向。</summary>
        private static Vector3 ParseDirection(string direction)
        {
            switch (direction.ToLowerInvariant())
            {
                case "north":
                case "forward":
                case "front": return Vector3.forward;
                case "south":
                case "back":
                case "backward": return Vector3.back;
                case "east":
                case "right": return Vector3.right;
                case "west":
                case "left": return Vector3.left;
                case "up":
                case "top": return Vector3.up;
                case "down":
                case "bottom": return Vector3.down;
                default: throw ProBuilderErrors.SchemaInvalid($"Unknown direction '{direction}'. Valid: north/south/east/west/up/down.");
            }
        }

        /// <summary>把 ElementSelection 返回的 Face 引用映射回稳定面索引。</summary>
        private static List<int> ResolveFaceIndices(Array allFaces, Array selectedFaces)
        {
            var indices = new List<int>();
            foreach (var selected in selectedFaces)
            {
                for (var index = 0; index < allFaces.Length; index++)
                {
                    if (!ReferenceEquals(allFaces.GetValue(index), selected)) continue;
                    indices.Add(index);
                    break;
                }
            }
            return indices;
        }
    }
}
#endif
