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
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.noise",
            Summary = "Add, configure, or remove a CinemachineBasicMultiChannelPerlin noise component on a virtual camera.",
            Description = "Add, configure, or remove a CinemachineBasicMultiChannelPerlin noise component on a Cinemachine virtual camera. remove=true deletes the noise component from the target camera.",
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
                new ToolExample { Description = "配置噪声", Input = new Request { Target = "PlayerCam", NoiseProfile = "Handheld_tele" } },
                new ToolExample { Description = "移除噪声", Input = new Request { Target = "PlayerCam", Remove = true } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.noise");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine noise requires --target <go>.");
            CinemachineToolSupport.RequireCameraComponent(gameObject);
            var noiseType = CinemachineToolSupport.RequireComponentType("CinemachineBasicMultiChannelPerlin", "Noise");
            var effects = new List<CinemachinePlannedEffect>();

            if (request.Remove == true)
            {
                var existing = gameObject.GetComponent(noiseType);
                if (existing == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_NOISE_NOT_FOUND",
                        "No CinemachineBasicMultiChannelPerlin component found on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                        "The noise component may have already been removed.");
                }

                UnityCliSceneMutation.DestroyObject(existing);
                CinemachineToolSupport.MarkDirty(gameObject);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.noise.remove",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject)
                });
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                    PlannedEffects = effects,
                    LogPath = ctx.LogPath
                });
            }

            var noise = gameObject.GetComponent(noiseType) ?? UnityCliSceneMutation.AddComponent(gameObject, noiseType);

            if (!string.IsNullOrWhiteSpace(request.NoiseProfile))
            {
                var profile = CinemachineToolSupport.FindNoiseProfile(request.NoiseProfile);
                if (profile != null)
                {
                    var property = noise.GetType().GetProperty("NoiseProfile") ?? noise.GetType().GetProperty("m_NoiseProfile");
                    if (property != null && property.CanWrite)
                    {
                        property.SetValue(noise, profile, null);
                        effects.Add(new CinemachinePlannedEffect
                        {
                            Type = "cinemachine.noise",
                            Target = GameObjectToolSupport.HierarchyPath(gameObject),
                            Field = "noiseProfile",
                            Value = request.NoiseProfile
                        });
                    }
                }
            }

            if (request.Amplitude.HasValue || request.Frequency.HasValue)
            {
                using (var serializedObject = new SerializedObject(noise))
                {
                    if (request.Amplitude.HasValue)
                    {
                        var amplitude = serializedObject.FindProperty("Amplitude") ?? serializedObject.FindProperty("m_Amplitude");
                        if (amplitude != null)
                        {
                            amplitude.floatValue = (float)request.Amplitude.Value;
                            effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "amplitude", Value = request.Amplitude.Value.ToString() });
                        }
                    }
                    if (request.Frequency.HasValue)
                    {
                        var frequency = serializedObject.FindProperty("Frequency") ?? serializedObject.FindProperty("m_Frequency");
                        if (frequency != null)
                        {
                            frequency.floatValue = (float)request.Frequency.Value;
                            effects.Add(new CinemachinePlannedEffect { Type = "cinemachine.noise", Target = GameObjectToolSupport.HierarchyPath(gameObject), Field = "frequency", Value = request.Frequency.Value.ToString() });
                        }
                    }
                    serializedObject.ApplyModifiedProperties();
                }
            }

            CinemachineToolSupport.MarkDirty(gameObject);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
