#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Contexts.Scene
{
    /// <summary>
    /// scene.cameras 内置 Context：读取当前场景中活跃 Camera 组件清单。
    /// </summary>
    public class SceneCamerasContext : IUnityCliContext<SceneCamerasContext.Request, SceneCamerasContext.Result>
    {
        public sealed class Request { }

        public sealed class Result
        {
            public List<CameraInfo> Cameras { get; set; }
            public long Count { get; set; }
        }

        public ContextDescriptor Describe() => new ContextDescriptor
        {
            Name = "scene.cameras",
            Summary = "List active scene cameras with hierarchy paths, projection, lens, depth, and pixel size.",
            Description = "List all active scene cameras with stable GameObject path, tag, enabled state, projection, FOV/orthographic size, depth, target texture, and pixel size. Read-only camera inventory.",
            Category = "scene",
            Version = "1.0.0",
            Source = "built-in",
            Target = "editor",
            AvailabilityScope = "editorSession",
            PlayModeSupport = "supported",
            RefreshBehavior = "on-demand"
        };

        /// <summary>
        /// 枚举 Unity 当前活跃 Camera.allCameras；只返回摘要 DTO，不修改相机或场景。
        /// </summary>
        public Result Read(Request request, UnityCliProjectToolContext ctx)
        {
            var cameras = new List<CameraInfo>();
            foreach (var camera in Camera.allCameras)
            {
                cameras.Add(CameraToolSupport.BuildCameraInfo(camera));
            }

            return new Result
            {
                Cameras = cameras,
                Count = cameras.Count
            };
        }
    }
}
#endif
