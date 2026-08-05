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
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.target",
            Summary = "Set the Follow and/or LookAt targets on a Cinemachine virtual camera.",
            Description = "Set the Follow and/or LookAt targets on a Cinemachine virtual camera. target must resolve to a GameObject that already owns a Cinemachine camera component.",
            Group = "cinemachine",
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

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.target");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "Cinemachine target requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();
            var follow = string.IsNullOrWhiteSpace(request.Follow) ? null : CinemachineToolSupport.ResolveRequiredTarget(request.Follow, "Pass an existing GameObject name or path.");
            var lookAt = string.IsNullOrWhiteSpace(request.LookAt) ? null : CinemachineToolSupport.ResolveRequiredTarget(request.LookAt, "Pass an existing GameObject name or path.");
            if (follow == null && lookAt == null) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_TARGET_INPUT_REQUIRED", "cinemachine.target requires follow or lookAt.", "Pass at least one target field.");
            var followPlan = follow == null ? null : CinemachineToolSupport.PrepareComponentObjectMember(camera.GetType(), "Follow", follow.transform, "E_CINEMACHINE_FOLLOW_NOT_WRITABLE");
            var lookAtPlan = lookAt == null ? null : CinemachineToolSupport.PrepareComponentObjectMember(camera.GetType(), "LookAt", lookAt.transform, "E_CINEMACHINE_LOOKAT_NOT_WRITABLE");
            ctx.CommitEffect();
            if (follow != null)
            {
                CinemachineToolSupport.ApplyComponentMember(camera, followPlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.target", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "follow", Value = follow.name });
            }
            if (lookAt != null)
            {
                CinemachineToolSupport.ApplyComponentMember(camera, lookAtPlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.target", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "lookAt", Value = lookAt.name });
            }
            var observedObject = CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(System.Globalization.CultureInfo.InvariantCulture), "Cinemachine camera could not be resolved.");
            var observed = CinemachineToolSupport.RequireCameraComponent(observedObject);
            if (followPlan != null) CinemachineToolSupport.VerifyComponentMember(observed, followPlan, "E_CINEMACHINE_TARGET_VERIFY_FAILED");
            if (lookAtPlan != null) CinemachineToolSupport.VerifyComponentMember(observed, lookAtPlan, "E_CINEMACHINE_TARGET_VERIFY_FAILED");
            CinemachineToolSupport.MarkDirty(observedObject);

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(observedObject),
                PlannedEffects = effects
            });
        }
    }
}
#endif
