#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.lens — 更新 Camera 镜头/投影设置:FOV、正交模式/尺寸、近/远裁剪面、清屏标志、深度。
    /// </summary>
    public class CameraLensTool : IUnityCliTool<CameraLensTool.Request, CameraLensTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public double? FieldOfView { get; set; }
            public bool? Orthographic { get; set; }
            public double? OrthographicSize { get; set; }
            public double? NearClipPlane { get; set; }
            public double? FarClipPlane { get; set; }
            public string ClearFlags { get; set; }
            public double? Depth { get; set; }
        }

        public sealed class Result
        {
            public CameraInfo Camera { get; set; }
            public List<CameraPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.lens",
            Summary = "Update Camera lens/projection settings: FOV, orthographic mode/size, near/far clip planes, clear flags, and depth.",
            Description = "Update Camera lens/projection settings: FOV, orthographic mode/size, near/far clip planes, clear flags, and depth. Invalid clearFlags returns E_CAMERA_CLEAR_FLAGS_INVALID instead of silently doing nothing.",
            Group = "camera",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Set camera FOV", Input = new Request { Target = "UnityCliAlignmentCamera", FieldOfView = 45, NearClipPlane = 0.1, FarClipPlane = 1000 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "camera.lens requires target.", "Run tools run camera.list, then pass a camera name, hierarchy path, or instance id.");
            }
            var camera = CameraToolSupport.ResolveCamera(request.Target);
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_NOT_FOUND", "Camera not found: " + request.Target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            if (!request.FieldOfView.HasValue && !request.Orthographic.HasValue && !request.OrthographicSize.HasValue && !request.NearClipPlane.HasValue && !request.FarClipPlane.HasValue && string.IsNullOrEmpty(request.ClearFlags) && !request.Depth.HasValue)
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_LENS_REQUEST_EMPTY", "camera.lens requires at least one lens setting.", "Pass a fieldOfView, projection, clipping, clearFlags, or depth value.");
            CameraToolSupport.ValidateFloatRange(request.FieldOfView, "fieldOfView", 1d, 179d);
            CameraToolSupport.ValidateFloatRange(request.OrthographicSize, "orthographicSize", double.Epsilon, float.MaxValue);
            CameraToolSupport.ValidateFloatRange(request.NearClipPlane, "nearClipPlane", double.Epsilon, float.MaxValue);
            CameraToolSupport.ValidateFloatRange(request.FarClipPlane, "farClipPlane", double.Epsilon, float.MaxValue);
            CameraToolSupport.ValidateFloatRange(request.Depth, "depth", -float.MaxValue, float.MaxValue);
            if (request.NearClipPlane.HasValue && request.FarClipPlane.HasValue && request.NearClipPlane.Value >= request.FarClipPlane.Value)
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_LENS_RANGE_INVALID", "nearClipPlane must be less than farClipPlane.", "Pass finite clipping planes with nearClipPlane < farClipPlane.");
            // clearFlags 先校验再写入,避免部分应用
            var clearFlags = string.IsNullOrEmpty(request.ClearFlags) ? (CameraClearFlags?)null : CameraToolSupport.ParseCameraClearFlags(request.ClearFlags);
            var effects = new List<CameraPlannedEffect>();
            var cameraId = camera.GetInstanceID();
            ctx.CommitEffect();
            if (request.FieldOfView.HasValue)
            {
                camera.fieldOfView = (float)request.FieldOfView.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "fieldOfView", Value = camera.fieldOfView.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.Orthographic.HasValue)
            {
                camera.orthographic = request.Orthographic.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "orthographic", Value = camera.orthographic.ToString().ToLowerInvariant() });
            }
            if (request.OrthographicSize.HasValue)
            {
                camera.orthographicSize = (float)request.OrthographicSize.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "orthographicSize", Value = camera.orthographicSize.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.NearClipPlane.HasValue)
            {
                camera.nearClipPlane = (float)request.NearClipPlane.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "nearClipPlane", Value = camera.nearClipPlane.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (request.FarClipPlane.HasValue)
            {
                camera.farClipPlane = (float)request.FarClipPlane.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "farClipPlane", Value = camera.farClipPlane.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (clearFlags.HasValue)
            {
                camera.clearFlags = clearFlags.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "clearFlags", Value = request.ClearFlags });
            }
            if (request.Depth.HasValue)
            {
                camera.depth = (float)request.Depth.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.lens", Property = "depth", Value = camera.depth.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            UnityCliSceneMutation.MarkSceneObjectDirty(camera);
            var observed = EditorUtility.InstanceIDToObject(cameraId) as Camera;
            if (observed == null || (request.FieldOfView.HasValue && !Mathf.Approximately(observed.fieldOfView, (float)request.FieldOfView.Value)) || (request.Orthographic.HasValue && observed.orthographic != request.Orthographic.Value) || (request.OrthographicSize.HasValue && !Mathf.Approximately(observed.orthographicSize, (float)request.OrthographicSize.Value)) || (request.NearClipPlane.HasValue && !Mathf.Approximately(observed.nearClipPlane, (float)request.NearClipPlane.Value)) || (request.FarClipPlane.HasValue && !Mathf.Approximately(observed.farClipPlane, (float)request.FarClipPlane.Value)) || (clearFlags.HasValue && observed.clearFlags != clearFlags.Value) || (request.Depth.HasValue && !Mathf.Approximately(observed.depth, (float)request.Depth.Value)))
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_LENS_STATE_MISMATCH", "Camera lens did not satisfy the requested predicate.", "Inspect the Camera and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Camera = CameraToolSupport.BuildCameraInfo(observed), PlannedEffects = effects });
        }

    }
}
#endif
