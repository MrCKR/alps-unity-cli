#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.priority — 修改虚拟相机优先级与启用标志。
    /// </summary>
    public class PriorityTool : IUnityCliTool<PriorityTool.Request, PriorityTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public int? Priority { get; set; }
            public bool? Enabled { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineObjectInfo Object { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.priority",
            Summary = "Get or set the priority and enabled flag on a Cinemachine virtual camera.",
            Description = "Set the priority and enabled flag on a Cinemachine virtual camera through the SerializedObject-backed PrioritySettings struct.",
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
                new ToolExample { Description = "提高优先级", Input = new Request { Target = "PlayerCam", Priority = 20 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.priority");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine priority requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();
            if (!request.Priority.HasValue && !request.Enabled.HasValue) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_INPUT_REQUIRED", "cinemachine.priority requires priority or enabled.", "Pass at least one priority field.");
            using (var validate = new UnityEditor.SerializedObject(camera))
            {
                var priority = validate.FindProperty("Priority") ?? validate.FindProperty("m_Priority");
                var value = priority != null && priority.propertyType == UnityEditor.SerializedPropertyType.Integer ? priority : priority?.FindPropertyRelative("Value") ?? priority?.FindPropertyRelative("m_Value");
                var enabled = priority?.FindPropertyRelative("Enabled") ?? priority?.FindPropertyRelative("m_Enabled");
                if (priority == null || (request.Priority.HasValue && (value == null || value.propertyType != UnityEditor.SerializedPropertyType.Integer)) || (request.Enabled.HasValue && (enabled == null || enabled.propertyType != UnityEditor.SerializedPropertyType.Boolean))) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_PROPERTY_NOT_FOUND", "Cinemachine priority fields are not writable.", "Use a supported Cinemachine package version.");
            }

            if (request.Enabled.HasValue) CinemachineToolSupport.RequirePriorityEnabledWritable(camera);

            ctx.CommitEffect();
            if (request.Priority.HasValue)
            {
                CinemachineToolSupport.SetPriority(camera, request.Priority.Value);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.priority",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    Field = "priority",
                    Value = request.Priority.Value.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (request.Enabled.HasValue)
            {
                CinemachineToolSupport.SetPriorityEnabled(camera, request.Enabled.Value);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.priority",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    Field = "enabled",
                    Value = request.Enabled.Value ? "true" : "false"
                });
            }

            var observedCamera = CinemachineToolSupport.RequireCameraComponent(CinemachineToolSupport.ResolveRequiredTarget(gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture), "Cinemachine camera could not be resolved after setting priority."));
            using (var observed = new UnityEditor.SerializedObject(observedCamera))
            {
                var priority = observed.FindProperty("Priority") ?? observed.FindProperty("m_Priority");
                var value = priority != null && priority.propertyType == UnityEditor.SerializedPropertyType.Integer ? priority : priority?.FindPropertyRelative("Value") ?? priority?.FindPropertyRelative("m_Value");
                var enabled = priority?.FindPropertyRelative("Enabled") ?? priority?.FindPropertyRelative("m_Enabled");
                if (request.Priority.HasValue && (value == null || value.intValue != request.Priority.Value)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_VERIFY_FAILED", "Cinemachine priority did not retain the requested value.", "Check the camera and retry.");
                if (request.Enabled.HasValue && (enabled == null || enabled.boolValue != request.Enabled.Value)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_PRIORITY_VERIFY_FAILED", "Cinemachine priority enabled flag did not retain the requested value.", "Check the camera and retry.");
            }

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(gameObject);
            }

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
