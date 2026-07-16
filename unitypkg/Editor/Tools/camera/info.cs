#if UNITY_EDITOR
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.info — 获取单个 Camera 的序列化设置。
    /// </summary>
    public class CameraInfoTool : IUnityCliTool<CameraInfoTool.Request, CameraInfoTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public CameraInfo Camera { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.info",
            Summary = "Get one scene Camera's serializable settings by name, path, or instance id.",
            Description = "Get one Camera's serializable settings. Resolves by Camera name, hierarchy path, or instance id; returns E_CAMERA_NOT_FOUND with recovery guidance when missing.",
            Group = "camera",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Inspect Main Camera", Input = new Request { Target = "Main Camera" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var camera = CameraToolSupport.ResolveCamera(request.Target);
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_NOT_FOUND", "Camera not found: " + request.Target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Camera = CameraToolSupport.BuildCameraInfo(camera) });
        }
    }
}
#endif
