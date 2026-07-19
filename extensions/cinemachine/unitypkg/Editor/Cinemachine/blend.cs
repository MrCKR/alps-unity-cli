#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.blend — 修改 Brain 的默认切镜样式与时长。
    /// </summary>
    public class BlendTool : IUnityCliTool<BlendTool.Request, BlendTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Style { get; set; }
            public double? Duration { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public CinemachineBrainInfo Brain { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.blend",
            Summary = "Get or set the DefaultBlend style and duration on a CinemachineBrain.",
            Description = "Set the DefaultBlend style and duration on a CinemachineBrain. target is optional; when omitted the first scene brain is used.",
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
                new ToolExample { Description = "设置默认混合", Input = new Request { Target = "Main Camera", Style = "ease_in_out", Duration = 2 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.blend");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var brainOwner = CinemachineToolSupport.ResolveBrainOwner(request.Target);
            var brain = CinemachineToolSupport.RequireBrainComponent(brainOwner);
            var effects = new List<CinemachinePlannedEffect>();
            if (string.IsNullOrWhiteSpace(request.Style) && !request.Duration.HasValue) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_INPUT_REQUIRED", "cinemachine.blend requires style or duration.", "Pass at least one blend field.");
            if (request.Duration.HasValue) { CinemachineToolSupport.RequireFinite(request.Duration.Value, "duration"); if (request.Duration.Value < 0d) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_RANGE_INVALID", "duration must be non-negative.", "Pass a duration greater than or equal to zero."); }
            var requestedStyle = string.IsNullOrWhiteSpace(request.Style) ? (int?)null : CinemachineToolSupport.BlendStyleToEnumIndex(request.Style);
            if (!string.IsNullOrWhiteSpace(request.Style) && !requestedStyle.HasValue) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_STYLE_INVALID", "Unsupported Cinemachine blend style: " + request.Style, "Pass a supported blend style.");
            using (var validate = new SerializedObject(brain))
            {
                var blend = validate.FindProperty("DefaultBlend") ?? validate.FindProperty("m_DefaultBlend");
                if (blend == null || (requestedStyle.HasValue && (blend.FindPropertyRelative("Style") ?? blend.FindPropertyRelative("m_Style")) == null) || (request.Duration.HasValue && (blend.FindPropertyRelative("Time") ?? blend.FindPropertyRelative("m_Time")) == null)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_PROPERTY_NOT_FOUND", "DefaultBlend fields are not writable.", "Use a supported Cinemachine package version.");
            }

            ctx.CommitEffect();
            using (var serializedObject = new SerializedObject(brain))
            {
                var blendProperty = serializedObject.FindProperty("DefaultBlend") ?? serializedObject.FindProperty("m_DefaultBlend");
                if (blendProperty == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_BLEND_PROPERTY_NOT_FOUND",
                        "DefaultBlend property not accessible on CinemachineBrain.",
                        "The Cinemachine package version may differ; inspect the Unity Console for details.");
                }

                if (!string.IsNullOrWhiteSpace(request.Style))
                {
                    var enumIndex = requestedStyle;
                    if (enumIndex.HasValue)
                    {
                        var styleProperty = blendProperty.FindPropertyRelative("Style") ?? blendProperty.FindPropertyRelative("m_Style");
                        if (styleProperty != null)
                        {
                            styleProperty.enumValueIndex = enumIndex.Value;
                            effects.Add(new CinemachinePlannedEffect
                            {
                                Type = "cinemachine.blend",
                                Target = GameObjectToolSupport.HierarchyPath(brainOwner),
                                Field = "style",
                                Value = request.Style
                            });
                        }
                    }
                }

                if (request.Duration.HasValue)
                {
                    var timeProperty = blendProperty.FindPropertyRelative("Time") ?? blendProperty.FindPropertyRelative("m_Time");
                    if (timeProperty != null)
                    {
                        timeProperty.floatValue = (float)request.Duration.Value;
                        effects.Add(new CinemachinePlannedEffect
                        {
                            Type = "cinemachine.blend",
                            Target = GameObjectToolSupport.HierarchyPath(brainOwner),
                            Field = "duration",
                            Value = request.Duration.Value.ToString()
                        });
                    }
                }

                serializedObject.ApplyModifiedProperties();
            }

            var observedBrain = CinemachineToolSupport.RequireBrainComponent(CinemachineToolSupport.ResolveRequiredTarget(brainOwner.GetInstanceID().ToString(), "CinemachineBrain could not be resolved after setting blend."));
            using (var observed = new SerializedObject(observedBrain))
            {
                var blend = observed.FindProperty("DefaultBlend") ?? observed.FindProperty("m_DefaultBlend");
                var style = blend?.FindPropertyRelative("Style") ?? blend?.FindPropertyRelative("m_Style");
                var time = blend?.FindPropertyRelative("Time") ?? blend?.FindPropertyRelative("m_Time");
                if (requestedStyle.HasValue && (style == null || style.enumValueIndex != requestedStyle.Value)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_VERIFY_FAILED", "Cinemachine blend style did not retain the requested value.", "Check the brain and retry.");
                if (request.Duration.HasValue && (time == null || !UnityEngine.Mathf.Approximately(time.floatValue, (float)request.Duration.Value))) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_BLEND_VERIFY_FAILED", "Cinemachine blend duration did not retain the requested value.", "Check the brain and retry.");
            }

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(brainOwner);
            }

            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(brainOwner),
                Brain = CinemachineToolSupport.BuildBrainInfo(brain),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            });
        }
    }
}
#endif
