#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.force-camera — 调用 Brain override API 临时强制切换输出虚拟相机。
    /// </summary>
    public class ForceCameraTool : IUnityCliTool<ForceCameraTool.Request, ForceCameraTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Brain { get; set; }
            public string Source { get; set; }
            public int? OverrideId { get; set; }
            public double? Weight { get; set; }
            public double? DeltaTime { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public long OverrideId { get; set; }
            public long PreviousOverrideId { get; set; }
            public double Weight { get; set; }
            public double DeltaTime { get; set; }
            public CinemachineBrainInfo Brain { get; set; }
            public CinemachineObjectInfo ForcedCamera { get; set; }
            public CinemachineObjectInfo SourceCamera { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.force-camera",
            Summary = "Force a CinemachineBrain override to a target virtual camera and return the overrideId.",
            Description = "Force a CinemachineBrain override to a target Cinemachine virtual camera using the reflected Brain override API. The effect is session-only and can be released later with cinemachine.release-override.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "editor-session-state" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "强制切换到近景相机", Input = new Request { Target = "CloseupCam", Brain = "Main Camera" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.force-camera");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var brainOwner = CinemachineToolSupport.ResolveBrainOwner(request.Brain);
            var brain = CinemachineToolSupport.RequireBrainComponent(brainOwner);
            var target = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine.force-camera requires --target <go>.");
            CinemachineToolSupport.RequireCameraComponent(target);

            Component source = null;
            if (!string.IsNullOrWhiteSpace(request.Source))
            {
                var sourceOwner = CinemachineToolSupport.ResolveRequiredTarget(request.Source, "Pass a Cinemachine camera GameObject as source.");
                source = CinemachineToolSupport.RequireCameraComponent(sourceOwner);
            }
            if (source == null)
            {
                source = CinemachineToolSupport.GetActiveVirtualCameraComponent(brain);
            }

            var setOverride = CinemachineToolSupport.FindSetCameraOverrideMethod(brain.GetType());
            if (setOverride == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_OVERRIDE_UNSUPPORTED",
                    "CinemachineBrain.SetCameraOverride is not available in this Cinemachine package version.",
                    "Use cinemachine.ping to inspect reflected Brain API availability, or use priority/blend controls instead.");
            }

            var previousOverrideId = request.OverrideId ?? -1;
            var weight = request.Weight ?? 1d;
            var deltaTime = request.DeltaTime ?? -1d;
            var newOverrideId = System.Convert.ToInt32(setOverride.Invoke(brain, new object[]
            {
                previousOverrideId,
                source,
                target.GetComponent(CinemachineToolSupport.CameraType),
                (float)weight,
                (float)deltaTime
            }), CultureInfo.InvariantCulture);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                OverrideId = newOverrideId,
                PreviousOverrideId = previousOverrideId,
                Weight = weight,
                DeltaTime = deltaTime,
                Brain = CinemachineToolSupport.BuildBrainInfo(brain),
                ForcedCamera = CinemachineToolSupport.BuildObjectInfo(target),
                SourceCamera = source != null ? CinemachineToolSupport.BuildObjectInfo(source.gameObject) : null,
                PlannedEffects = new List<CinemachinePlannedEffect>
                {
                    new CinemachinePlannedEffect
                    {
                        Type = "cinemachine.force-camera",
                        Target = GameObjectToolSupport.HierarchyPath(target),
                        Brain = GameObjectToolSupport.HierarchyPath(brainOwner),
                        OverrideId = newOverrideId,
                        SideEffects = "editor-session-state"
                    }
                },
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
