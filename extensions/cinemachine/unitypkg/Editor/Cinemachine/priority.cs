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
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.priority",
            Summary = "Get or set the priority and enabled flag on a Cinemachine virtual camera.",
            Description = "Set the priority and enabled flag on a Cinemachine virtual camera through the SerializedObject-backed PrioritySettings struct.",
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

            if (effects.Count > 0)
            {
                CinemachineToolSupport.MarkDirty(gameObject);
            }

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
