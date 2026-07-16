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
                    var enumIndex = CinemachineToolSupport.BlendStyleToEnumIndex(request.Style);
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
