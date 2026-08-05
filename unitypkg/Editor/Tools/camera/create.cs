#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.create — 在激活场景创建 Camera GameObject,可设置父级/变换/投影/清屏/深度/MainCamera 标签。
    /// </summary>
    public class CameraCreateTool : IUnityCliTool<CameraCreateTool.Request, CameraCreateTool.Result>
    {
        public sealed class Request
        {
            public string Name { get; set; }
            public string Parent { get; set; }
            public string Position { get; set; }
            public string Rotation { get; set; }
            public double? FieldOfView { get; set; }
            public bool? Orthographic { get; set; }
            public double? OrthographicSize { get; set; }
            public string ClearFlags { get; set; }
            public double? Depth { get; set; }
            public string Tag { get; set; }
        }

        public sealed class Result
        {
            public CameraInfo Camera { get; set; }
            public List<CameraPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.create",
            Summary = "Create a Unity Camera GameObject in the active scene with optional parent, transform, projection, clear flags, depth, and MainCamera tag.",
            Description = "Create a Unity Camera GameObject in the active scene with optional parent, transform, projection, clear flags, depth, and MainCamera tag. Parent and enum inputs are validated before mutation to avoid partial objects.",
            Group = "camera",
            DestructiveHint = false,
            SideEffects = new[] { "scene-modify", "component-add" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Create a perspective camera", Input = new Request { Name = "UnityCliAlignmentCamera", Position = "0,2,-6", Rotation = "15,0,0", FieldOfView = 60 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CameraToolSupport.EnsureEditorNotBusy("camera.create");
            var name = string.IsNullOrEmpty(request.Name) ? "Camera" : request.Name;
            // 父级先解析,clearFlags 先校验,再创建 GameObject,避免半成品
            var parentGo = string.IsNullOrEmpty(request.Parent) ? null : GameObjectToolSupport.ResolveUniqueTarget(request.Parent, "camera.create --parent <go> not found.");
            var clearFlags = string.IsNullOrEmpty(request.ClearFlags) ? (CameraClearFlags?)null : CameraToolSupport.ParseCameraClearFlags(request.ClearFlags);
            var position = string.IsNullOrEmpty(request.Position) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(request.Position);
            var rotation = string.IsNullOrEmpty(request.Rotation) ? (Vector3?)null : GameObjectToolSupport.ParseVector3(request.Rotation);
            GameObjectToolSupport.ValidateMutationInputs(request.Tag, null);
            CameraToolSupport.ValidateFloatRange(request.FieldOfView, "fieldOfView", 1d, 179d);
            CameraToolSupport.ValidateFloatRange(request.OrthographicSize, "orthographicSize", double.Epsilon, float.MaxValue);
            CameraToolSupport.ValidateFloatRange(request.Depth, "depth", -float.MaxValue, float.MaxValue);
            var parentId = parentGo == null ? 0 : parentGo.GetInstanceID();

            ctx.CommitEffect();
            var gameObject = new GameObject(name);
            if (parentGo != null)
            {
                gameObject.transform.SetParent(parentGo.transform, false);
            }
            var camera = gameObject.AddComponent<Camera>();
            if (position.HasValue)
            {
                gameObject.transform.position = position.Value;
            }
            if (rotation.HasValue)
            {
                gameObject.transform.eulerAngles = rotation.Value;
            }
            if (request.FieldOfView.HasValue) camera.fieldOfView = (float)request.FieldOfView.Value;
            if (request.Orthographic.HasValue) camera.orthographic = request.Orthographic.Value;
            if (request.OrthographicSize.HasValue) camera.orthographicSize = (float)request.OrthographicSize.Value;
            if (clearFlags.HasValue) camera.clearFlags = clearFlags.Value;
            if (request.Depth.HasValue) camera.depth = (float)request.Depth.Value;
            if (request.Tag == "MainCamera")
            {
                gameObject.tag = "MainCamera";
            }
            UnityCliSceneMutation.MarkSceneObjectDirty(gameObject);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            var observed = EditorUtility.InstanceIDToObject(camera.GetInstanceID()) as Camera;
            if (observed == null || observed.gameObject.name != name || (parentId == 0 && observed.transform.parent != null) || (parentId != 0 && (observed.transform.parent == null || observed.transform.parent.GetInstanceID() != parentId)) ||
                (position.HasValue && observed.transform.position != position.Value) || (rotation.HasValue && observed.transform.eulerAngles != rotation.Value) ||
                (request.FieldOfView.HasValue && !Mathf.Approximately(observed.fieldOfView, (float)request.FieldOfView.Value)) ||
                (request.Orthographic.HasValue && observed.orthographic != request.Orthographic.Value) ||
                (request.OrthographicSize.HasValue && !Mathf.Approximately(observed.orthographicSize, (float)request.OrthographicSize.Value)) ||
                (clearFlags.HasValue && observed.clearFlags != clearFlags.Value) ||
                (request.Depth.HasValue && !Mathf.Approximately(observed.depth, (float)request.Depth.Value)) ||
                (request.Tag == "MainCamera" && observed.gameObject.tag != "MainCamera"))
                throw new UnityCliBridge.BridgeCommandException("E_CAMERA_CREATE_STATE_MISMATCH", "Created Camera did not satisfy the requested predicate.", "Inspect the Camera and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Camera = CameraToolSupport.BuildCameraInfo(observed),
                PlannedEffects = new List<CameraPlannedEffect>
                {
                    new CameraPlannedEffect { Type = "camera.create", Target = name }
                }
            });
        }
    }
}
#endif
