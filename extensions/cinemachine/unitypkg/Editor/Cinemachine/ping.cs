#if UNITY_EDITOR
namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.ping — 只读探测 Cinemachine 包与关键反射 API 可用性。
    /// </summary>
    public class PingTool : IUnityCliTool<PingTool.Request, PingTool.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.ping",
            Summary = "Check Cinemachine package availability and reflected camera/brain API status.",
            Description = "Check Cinemachine package availability and reflected camera/brain API status without changing the scene. Returns whether Cinemachine camera, brain, extension, and brain override APIs are available in the current Unity session.",
            Group = "cinemachine",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 5000,
            MaxTimeoutMs = 10000,
            Examples = new[]
            {
                new ToolExample { Description = "检查 Cinemachine 可用性", Input = new Request() }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus()
            });
        }
    }
}
#endif
