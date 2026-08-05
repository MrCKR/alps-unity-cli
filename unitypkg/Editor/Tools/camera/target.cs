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
            if (string.IsNullOrEmpty(request.LookAt) && string.IsNullOrEmpty(request.Position) && string.IsNullOrEmpty(request.Rotation))
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_TARGET_REQUEST_EMPTY", "camera.target requires lookAt, position, or rotation.", "Pass at least one transform setting.");
            if (!string.IsNullOrEmpty(request.LookAt) && !string.IsNullOrEmpty(request.Rotation))
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_TARGET_CONFLICT", "camera.target cannot combine lookAt with explicit rotation.", "Use lookAt or rotation, not both.");
            var effects = new List<CameraPlannedEffect>();
            var cameraId = camera.GetInstanceID();
            var position = string.IsNullOrEmpty(request.Position) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(request.Position);
            var rotation = string.IsNullOrEmpty(request.Rotation) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(request.Rotation);
            var lookAtGo = string.IsNullOrEmpty(request.LookAt) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.LookAt, "camera.target --look-at <go> not found.");
            var plannedPosition = position ?? camera.transform.position;
            var lookDirection = lookAtGo == null ? Vector3.zero : lookAtGo.transform.position - plannedPosition;
            if (lookAtGo != null && lookDirection == Vector3.zero) throw new UnityCliBridge.BridgeCommandException("E_CAMERA_TARGET_INVALID", "camera.target lookAt must not be at the planned Camera position.", "Choose a target at a different position.");
            var expectedLookRotation = lookAtGo == null ? Quaternion.identity : Quaternion.LookRotation(lookDirection);
            ctx.CommitEffect();
            if (position.HasValue)
            {
                camera.transform.position = position.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "position", Value = request.Position });
            }
            if (lookAtGo != null)
            {
                camera.transform.rotation = expectedLookRotation;
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "lookAt", Target = CameraToolSupport.HierarchyPath(lookAtGo) });
            }
            if (rotation.HasValue)
            {
                camera.transform.eulerAngles = rotation.Value;
                effects.Add(new CameraPlannedEffect { Type = "camera.target", Property = "rotation", Value = request.Rotation });
            }
            UnityCliSceneMutation.MarkSceneObjectDirty(camera);
            var observed = EditorUtility.InstanceIDToObject(cameraId) as Camera;
            if (observed == null || (lookAtGo != null && Quaternion.Angle(observed.transform.rotation, expectedLookRotation) > 0.01f) || (position.HasValue && observed.transform.position != position.Value) || (rotation.HasValue && observed.transform.eulerAngles != rotation.Value))
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_TARGET_STATE_MISMATCH", "Camera transform did not satisfy the requested target predicate.", "Inspect the Camera and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Camera = CameraToolSupport.BuildCameraInfo(observed), PlannedEffects = effects });
        }
    }
}
#endif
