#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.release-override — 释放 force-camera 返回的 overrideId。
    /// </summary>
    public class ReleaseOverrideTool : IUnityCliTool<ReleaseOverrideTool.Request, ReleaseOverrideTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public int? OverrideId { get; set; }
            public string Brain { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public long OverrideId { get; set; }
            public bool Released { get; set; }
            public CinemachineBrainInfo Brain { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.release-override",
            Summary = "Release a CinemachineBrain camera override by overrideId.",
            Description = "Release a session-only CinemachineBrain camera override previously returned by cinemachine.force-camera. brain is optional; when omitted the first scene brain is used.",
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
                new ToolExample { Description = "释放 override", Input = new Request { OverrideId = 12, Brain = "Main Camera" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.release-override");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            if (!request.OverrideId.HasValue)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_OVERRIDE_ID_REQUIRED",
                    "cinemachine.release-override requires overrideId returned by cinemachine.force-camera.",
                    "Pass overrideId from the earlier cinemachine.force-camera result.");
            }

            var brainOwner = CinemachineToolSupport.ResolveBrainOwner(request.Brain);
            var brain = CinemachineToolSupport.RequireBrainComponent(brainOwner);
            var releaseOverride = CinemachineToolSupport.FindReleaseCameraOverrideMethod(brain.GetType());
            if (releaseOverride == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_OVERRIDE_UNSUPPORTED",
                    "CinemachineBrain.ReleaseCameraOverride is not available in this Cinemachine package version.",
                    "Use cinemachine.ping to inspect reflected Brain API availability.");
            }

            releaseOverride.Invoke(brain, new object[] { request.OverrideId.Value });
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                OverrideId = request.OverrideId.Value,
                Released = true,
                Brain = CinemachineToolSupport.BuildBrainInfo(brain),
                PlannedEffects = new List<CinemachinePlannedEffect>
                {
                    new CinemachinePlannedEffect
                    {
                        Type = "cinemachine.release-override",
                        Brain = GameObjectToolSupport.HierarchyPath(brainOwner),
                        OverrideId = request.OverrideId.Value,
                        SideEffects = "editor-session-state"
                    }
                },
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
