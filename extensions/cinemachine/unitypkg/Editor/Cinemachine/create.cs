#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.create — 创建虚拟相机，并按 preset 安装 Body/Aim 管线组件。
    /// </summary>
    public class CreateTool : IUnityCliTool<CreateTool.Request, CreateTool.Result>
    {
        public sealed class Request
        {
            public string Preset { get; set; }
            public string Name { get; set; }
            public string Parent { get; set; }
            public string Follow { get; set; }
            public string LookAt { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
            public int? Priority { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.create",
            Summary = "Create a Cinemachine virtual camera with an optional preset that configures Body and Aim pipeline components.",
            Description = "Create a Cinemachine virtual camera with an optional preset. preset supports follow, third_person, freelook, dolly, static, top_down, and side_scroller. The tool can also set parent, follow, lookAt, position, and priority during creation.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            SupportsJob = true,
            Examples = new[]
            {
                new ToolExample { Description = "创建跟随相机", Input = new Request { Preset = "follow", Name = "PlayerCam", Follow = "Player" } },
                new ToolExample { Description = "创建 dolly 相机", Input = new Request { Preset = "dolly", Name = "DollyCam", LookAt = "Main Camera" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.create");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var preset = string.IsNullOrWhiteSpace(request.Preset) ? "follow" : request.Preset.Trim();
            var cameraName = string.IsNullOrWhiteSpace(request.Name) ? "UnityCli_CM_" + preset : request.Name.Trim();
            var gameObject = new GameObject(cameraName);
            var camera = gameObject.AddComponent(CinemachineToolSupport.CameraType);
            Undo.RegisterCreatedObjectUndo(gameObject, "UnityCli Cinemachine Create");

            gameObject.transform.position = new Vector3(
                (float)(request.X ?? gameObject.transform.position.x),
                (float)(request.Y ?? gameObject.transform.position.y),
                (float)(request.Z ?? gameObject.transform.position.z));

            var parent = CinemachineToolSupport.ResolveOptionalTarget(request.Parent);
            if (parent != null)
            {
                gameObject.transform.SetParent(parent.transform, false);
            }

            var follow = CinemachineToolSupport.ResolveOptionalTarget(request.Follow);
            if (follow != null)
            {
                CinemachineToolSupport.SetFollowTarget(camera, follow.transform);
            }

            var lookAt = CinemachineToolSupport.ResolveOptionalTarget(request.LookAt);
            if (lookAt != null)
            {
                CinemachineToolSupport.SetLookAtTarget(camera, lookAt.transform);
            }

            if (request.Priority.HasValue)
            {
                CinemachineToolSupport.SetPriority(camera, request.Priority.Value);
            }

            string[] presetParts;
            if (CinemachineToolSupport.Presets.TryGetValue(preset, out presetParts))
            {
                if (!string.IsNullOrWhiteSpace(presetParts[0]))
                {
                    CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Body", CinemachineToolSupport.RequireComponentType(presetParts[0], "Body"));
                }
                if (!string.IsNullOrWhiteSpace(presetParts[1]))
                {
                    CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Aim", CinemachineToolSupport.RequireComponentType(presetParts[1], "Aim"));
                }
            }

            CinemachineToolSupport.MarkDirty(gameObject);
            return new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject, preset),
                PlannedEffects = new List<CinemachinePlannedEffect>
                {
                    new CinemachinePlannedEffect
                    {
                        Type = "cinemachine.create",
                        Preset = preset,
                        Target = GameObjectToolSupport.HierarchyPath(gameObject)
                    }
                },
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
