#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// probuilder.ping — 检查 ProBuilder 包可用性与桥接状态。
    /// </summary>
    public class PingTool : IUnityCliTool<PingTool.Request, PingTool.Result>
    {
        public sealed class Request
        {
            // 无输入字段
        }

        public sealed class Result
        {
            public bool Installed { get; set; }
            public bool MeshTypeAvailable { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "probuilder.ping",
            Summary = "Check ProBuilder package availability and bridge status.",
            Description = "Check ProBuilder package availability and bridge status. Returns whether the package is installed and whether the ProBuilderMesh type is resolvable. No input required.",
            Group = "probuilder",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 5000,
            MaxTimeoutMs = 10000,
            SupportsJob = false,
            ArtifactKinds = new[] { "json" },
            Examples = new[]
            {
                new ToolExample { Description = "探测 ProBuilder 可用性", Input = new Request() }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var available = ProBuilderReflection.IsAvailable;
            var meshTypeAvailable = ProBuilderReflection.GetProBuilderMeshType() != null;
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Installed = available,
                MeshTypeAvailable = meshTypeAvailable
            });
        }
    }
}
#endif
