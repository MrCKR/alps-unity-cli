#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.target — 通过 lookAt 和/或显式 position/rotation 调整 Camera 变换。
    /// </summary>
    public class CameraTargetTool : IUnityCliTool<CameraTargetTool.Request, CameraTargetTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string LookAt { get; set; }
            public string Position { get; set; }
            public string Rotation { get; set; }
        }

        public sealed class Result
        {
            public CameraInfo Camera { get; set; }
            public List<CameraPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.target",
            Summary = "Aim or move one Camera transform using lookAt and/or explicit position/rotation.",
            Description = "Set a Camera transform by looking at a GameObject and/or assigning explicit position/rotation. Session-affecting: marks the Camera dirty and can affect the running Play Mode view without persisting runtime-only state.",
            Group = "camera",
            Version = "1.0.0",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Point a camera at a target", Input = new Request { Target = "UnityCliAlignmentCamera", LookAt = "UnityCliAlignmentProbe" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Target))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "camera.target requires target.", "Run tools run camera.list, then pass a camera name, hierarchy path, or instance id.");
            }
            var camera = CameraToolSupport.ResolveCamera(request.Target);
            if (camera == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_NOT_FOUND", "Camera not found: " + request.Target, "Run tools run camera.list, then retry with a camera name, hierarchy path, or instance id from the result.");
            }
            var effects = new List<CameraPlannedEffect>();
            if (!string.IsNullOrEmpty(request.LookAt))
            {
                var lookAtGo = GameObjectToolSupport.ResolveUniqueTarget(request.LookAt, "camera.target --look-at <go> not found.");
                camera.transform.LookAt(lookAtGo.transform);
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "lookAt", Target = CameraToolSupport.HierarchyPath(lookAtGo) });
            }
            if (!string.IsNullOrEmpty(request.Position))
            {
                camera.transform.position = GameObjectToolSupport.ParseVector3(request.Position);
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "position", Value = request.Position });
            }
            if (!string.IsNullOrEmpty(request.Rotation))
            {
                camera.transform.eulerAngles = GameObjectToolSupport.ParseVector3(request.Rotation);
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "rotation", Value = request.Rotation });
            }
            UnityCliSceneMutation.MarkSceneObjectDirty(camera);
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Camera = CameraToolSupport.BuildCameraInfo(camera), PlannedEffects = effects });
        }
    }
}
#endif
