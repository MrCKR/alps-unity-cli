#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.target — 设置虚拟相机的 Follow / LookAt 目标。
    /// </summary>
    public class TargetTool : IUnityCliTool<TargetTool.Request, TargetTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Follow { get; set; }
            public string LookAt { get; set; }
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
            Name = "cinemachine.target",
            Summary = "Set the Follow and/or LookAt targets on a Cinemachine virtual camera.",
            Description = "Set the Follow and/or LookAt targets on a Cinemachine virtual camera. target must resolve to a GameObject that already owns a Cinemachine camera component.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "设置 Follow", Input = new Request { Target = "PlayerCam", Follow = "Player" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.target");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "Cinemachine target requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();

            if (!string.IsNullOrWhiteSpace(request.Follow))
            {
                var follow = CinemachineToolSupport.ResolveRequiredTarget(request.Follow, "Pass an existing GameObject name or path.");
                CinemachineToolSupport.SetFollowTarget(camera, follow.transform);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.target",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    Field = "follow",
                    Value = follow.name
                });
            }

            if (!string.IsNullOrWhiteSpace(request.LookAt))
            {
                var lookAt = CinemachineToolSupport.ResolveRequiredTarget(request.LookAt, "Pass an existing GameObject name or path.");
                CinemachineToolSupport.SetLookAtTarget(camera, lookAt.transform);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.target",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    Field = "lookAt",
                    Value = lookAt.name
                });
            }

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(gameObject);
            }

            return new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
