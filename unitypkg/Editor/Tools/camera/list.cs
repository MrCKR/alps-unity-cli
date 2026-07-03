#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// camera.list — 列出场景中所有 Camera 组件的摘要信息。
    /// 对齐旧 CameraListJson handler;只读。
    /// 设计依据:.plan/工具统一形态.md(C# Bridge 工具 → C# 自包含)。
    /// </summary>
    public class CameraListTool : IUnityCliTool<CameraListTool.Request, CameraListTool.Result>
    {
        public sealed class Request
        {
            // camera.list 无入参
        }

        public sealed class Result
        {
            public List<CameraInfo> Cameras { get; set; }
            public long Count { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "camera.list",
            Summary = "List active scene Unity Camera components with name, hierarchy path, projection, FOV/ortho size, depth, targetTexture, and pixel size.",
            Description = "List active scene Unity Camera components with name, hierarchy path, projection, FOV/ortho size, depth, targetTexture, and pixel size. Read-only; use paths or ids from this output for camera.info/screenshot/lens/target.",
            Group = "camera",
            Version = "1.0.0",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "List scene cameras", Input = new Request { } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var cameras = Camera.allCameras;
            var list = new List<CameraInfo>();
            foreach (var camera in cameras)
            {
                list.Add(CameraToolSupport.BuildCameraInfo(camera));
            }
            return new Result { Cameras = list, Count = list.Count };
        }
    }
}
#endif
