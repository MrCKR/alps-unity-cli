#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.aim — 切换或配置 Aim 管线组件。
    /// </summary>
    public class AimTool : IUnityCliTool<AimTool.Request, AimTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Aim { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.aim",
            Summary = "Swap or configure the Cinemachine Aim pipeline component on a virtual camera.",
            Description = "Swap the Aim pipeline component on a Cinemachine virtual camera, or configure a writable public/serialized member on the current Aim component. aim may be omitted when property and value target the existing Aim component.",
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
                new ToolExample { Description = "切换 Aim 组件", Input = new Request { Target = "PlayerCam", Aim = "CinemachineRotationComposer" } },
                new ToolExample { Description = "配置现有 Aim 属性", Input = new Request { Target = "PlayerCam", Property = "m_HorizontalDamping", Value = "0.25" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.aim");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine aim requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();
            if (string.IsNullOrWhiteSpace(request.Aim) && string.IsNullOrWhiteSpace(request.Property))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_AIM_INPUT_REQUIRED", "cinemachine.aim requires aim or property/value.", "Pass aim to swap the stage component, or property and value to configure the existing Aim component.");
            if (string.IsNullOrWhiteSpace(request.Property) != string.IsNullOrWhiteSpace(request.Value))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_PROPERTY_REQUIRED", "Configuring an Aim component requires both property and value.", "Pass property and value together.");

            var existing = CinemachineToolSupport.GetPipelineComponent(camera, "Aim");
            var aimType = string.IsNullOrWhiteSpace(request.Aim) ? existing?.GetType() : CinemachineToolSupport.RequireComponentType(request.Aim, "Aim");
            if (aimType == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_AIM_COMPONENT_NOT_FOUND", "No Aim pipeline component is installed on target: " + GameObjectToolSupport.HierarchyPath(gameObject), "Provide aim to install an Aim component before configuring properties.");
            CinemachineToolSupport.RequirePipelineComponentStage(aimType, "Aim");
            var memberPlan = string.IsNullOrWhiteSpace(request.Property) ? null : CinemachineToolSupport.PrepareComponentMember(aimType, request.Property, request.Value);

            ctx.CommitEffect();
            if (!string.IsNullOrWhiteSpace(request.Aim))
            {
                CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Aim", aimType);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.aim", Target = GameObjectToolSupport.HierarchyPath(gameObject), AimType = aimType.Name });
            }
            var applied = CinemachineToolSupport.GetPipelineComponent(camera, "Aim");
            if (memberPlan != null)
            {
                CinemachineToolSupport.ApplyComponentMember(applied, memberPlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.aim", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = request.Property, Value = request.Value, AimType = aimType.Name });
            }

            var observedObject = CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(), "Cinemachine Aim target could not be resolved after mutation.");
            var observedCamera = CinemachineToolSupport.RequireCameraComponent(observedObject);
            var observedAim = CinemachineToolSupport.GetPipelineComponent(observedCamera, "Aim");
            if (observedAim == null || observedAim.GetType() != aimType)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_AIM_VERIFY_FAILED", "Cinemachine Aim component did not retain the requested type.", "Inspect the virtual camera before retrying.");
            if (memberPlan != null) CinemachineToolSupport.VerifyComponentMember(observedAim, memberPlan, "E_CINEMACHINE_AIM_VERIFY_FAILED");
            CinemachineToolSupport.MarkDirty(observedObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects
            });
        }
    }
}
#endif
