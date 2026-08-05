#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.noise — 添加、配置或移除基础多通道 Perlin 噪声组件。
    /// </summary>
    public class NoiseTool : IUnityCliTool<NoiseTool.Request, NoiseTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string NoiseProfile { get; set; }
            public double? Amplitude { get; set; }
            public double? Frequency { get; set; }
            public bool? Remove { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.noise",
            Summary = "Add, configure, or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera.",
            Description = "Add, configure, or remove a CinemachineBasicMultiChannelPerlin noise component on a Cinemachine virtual camera. remove=true deletes the noise component from the target camera.",
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
                new ToolExample { Description = "配置噪声", Input = new Request { Target = "PlayerCam", NoiseProfile = "Handheld_tele" } },
                new ToolExample { Description = "移除噪声", Input = new Request { Target = "PlayerCam", Remove = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.noise");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine noise requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var noiseType = CinemachineToolSupport.RequireComponentType("CinemachineBasicMultiChannelPerlin", "Noise");
            CinemachineToolSupport.RequirePipelineComponentStage(noiseType, "Noise");
            var effects = new List<CinemachinePlannedEffect>();
            var existing = CinemachineToolSupport.GetPipelineComponent(camera, "Noise");
            if (request.Remove == true && (request.Amplitude.HasValue || request.Frequency.HasValue || !string.IsNullOrWhiteSpace(request.NoiseProfile)))
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_REMOVE_CONFLICT", "remove cannot be combined with noise settings.", "Pass remove alone, or omit it when configuring noise.");
            if (request.Remove == true && existing == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_NOT_FOUND", "No CinemachineBasicMultiChannelPerlin component found on target: " + GameObjectToolSupport.HierarchyPath(gameObject), "The noise component may have already been removed.");
            if (request.Amplitude.HasValue) CinemachineToolSupport.RequireFinite(request.Amplitude.Value, "amplitude");
            if (request.Frequency.HasValue) CinemachineToolSupport.RequireFinite(request.Frequency.Value, "frequency");

            var profile = string.IsNullOrWhiteSpace(request.NoiseProfile) ? null : CinemachineToolSupport.FindNoiseProfile(request.NoiseProfile);
            if (!string.IsNullOrWhiteSpace(request.NoiseProfile) && profile == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_PROFILE_NOT_FOUND", "Noise profile not found: " + request.NoiseProfile, "Pass the name of an imported NoiseSettings asset.");
            var profilePlan = profile == null ? null : CinemachineToolSupport.PrepareFirstComponentObjectMember(noiseType, profile, "NoiseProfile", "m_NoiseProfile");
            var amplitudePlan = request.Amplitude.HasValue ? CinemachineToolSupport.PrepareFirstComponentMember(noiseType, request.Amplitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "AmplitudeGain", "Amplitude", "m_AmplitudeGain", "m_Amplitude") : null;
            var frequencyPlan = request.Frequency.HasValue ? CinemachineToolSupport.PrepareFirstComponentMember(noiseType, request.Frequency.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "FrequencyGain", "Frequency", "m_FrequencyGain", "m_Frequency") : null;

            ctx.CommitEffect();
            if (request.Remove == true)
            {
                CinemachineToolSupport.RemovePipelineComponent(camera, existing);
                var removedTarget = CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(), "Cinemachine noise target could not be resolved after removal.");
                var removedCamera = CinemachineToolSupport.RequireCameraComponent(removedTarget);
                if (CinemachineToolSupport.GetPipelineComponent(removedCamera, "Noise") != null)
                    throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_VERIFY_FAILED", "Cinemachine noise component still exists after removal.", "Inspect the virtual camera before retrying.");
                CinemachineToolSupport.MarkDirty(removedTarget);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise.remove", Target = GameObjectToolSupport.HierarchyPath(removedTarget) });
                return new System.Threading.Tasks.ValueTask<Result>(new Result { Cinemachine = CinemachineToolSupport.BuildPackageStatus(), Object = CinemachineToolSupport.BuildObjectInfo(removedTarget), PlannedEffects = effects });
            }

            if (existing == null) CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Noise", noiseType);
            var noise = CinemachineToolSupport.GetPipelineComponent(camera, "Noise");
            if (noise == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_COMPONENT_NOT_FOUND", "Cinemachine noise component could not be resolved after installation.", "Inspect the virtual camera before retrying.");

            if (profilePlan != null)
            {
                CinemachineToolSupport.ApplyComponentMember(noise, profilePlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "noiseProfile", Value = request.NoiseProfile });
            }
            if (amplitudePlan != null)
            {
                CinemachineToolSupport.ApplyComponentMember(noise, amplitudePlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "amplitude", Value = request.Amplitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
            if (frequencyPlan != null)
            {
                CinemachineToolSupport.ApplyComponentMember(noise, frequencyPlan);
                effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "frequency", Value = request.Frequency.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }

            var observedObject = CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(), "Cinemachine noise target could not be resolved after mutation.");
            var observedCamera = CinemachineToolSupport.RequireCameraComponent(observedObject);
            var observedNoise = CinemachineToolSupport.GetPipelineComponent(observedCamera, "Noise");
            if (observedNoise == null)
                throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_NOISE_VERIFY_FAILED", "Cinemachine noise component is missing after mutation.", "Inspect the virtual camera before retrying.");
            if (profilePlan != null) CinemachineToolSupport.VerifyComponentMember(observedNoise, profilePlan, "E_CINEMACHINE_NOISE_VERIFY_FAILED");
            if (amplitudePlan != null) CinemachineToolSupport.VerifyComponentMember(observedNoise, amplitudePlan, "E_CINEMACHINE_NOISE_VERIFY_FAILED");
            if (frequencyPlan != null) CinemachineToolSupport.VerifyComponentMember(observedNoise, frequencyPlan, "E_CINEMACHINE_NOISE_VERIFY_FAILED");
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
