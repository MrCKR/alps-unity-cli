#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.body — 切换或配置 Body 管线组件。
    /// </summary>
    public class BodyTool : IUnityCliTool<BodyTool.Request, BodyTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Body { get; set; }
            public string Property { get; set; }
            public string Value { get; set; }
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
            Name = "cinemachine.body",
            Summary = "Swap or configure the Cinemachine Body pipeline component on a virtual camera.",
            Description = "Swap the Body pipeline component on a Cinemachine virtual camera, or configure a writable public/serialized member on the current Body component. body may be omitted when property and value target the existing Body component.",
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
                new ToolExample { Description = "切换 Body 组件", Input = new Request { Target = "PlayerCam", Body = "CinemachineFollow" } },
                new ToolExample { Description = "配置现有 Body 属性", Input = new Request { Target = "PlayerCam", Property = "Damping", Value = "0.5" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.body");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var gameObject = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine body requires --target <go>.");
            var camera = CinemachineToolSupport.RequireCameraComponent(gameObject);
            var effects = new List<CinemachinePlannedEffect>();

            if (string.IsNullOrWhiteSpace(request.Body) && string.IsNullOrWhiteSpace(request.Property))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_BODY_INPUT_REQUIRED",
                    "cinemachine.body requires body or property/value.",
                    "Pass body to swap the stage component, or property and value to configure the existing Body component.");
            }

            if (!string.IsNullOrWhiteSpace(request.Body))
            {
                var bodyType = CinemachineToolSupport.RequireComponentType(request.Body, "Body");
                CinemachineToolSupport.SwapPipelineComponent(gameObject, camera, "Body", bodyType);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.body",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    BodyType = request.Body
                });
            }

            if (!string.IsNullOrWhiteSpace(request.Property) || !string.IsNullOrWhiteSpace(request.Value))
            {
                if (string.IsNullOrWhiteSpace(request.Property) || string.IsNullOrWhiteSpace(request.Value))
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_COMPONENT_PROPERTY_REQUIRED",
                        "Configuring a Body component requires both property and value.",
                        "Pass property and value together.");
                }

                var bodyComponent = CinemachineToolSupport.GetPipelineComponent(camera, "Body");
                if (bodyComponent == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_BODY_COMPONENT_NOT_FOUND",
                        "No Body pipeline component is installed on target: " + GameObjectToolSupport.HierarchyPath(gameObject),
                        "Provide body to install a Body component before configuring properties.");
                }
                CinemachineToolSupport.ConfigurePipelineComponent(bodyComponent, request.Property, request.Value);
                effects.Add(new CinemachinePlannedEffect
                {
                    Type = "cinemachine.body",
                    Target = GameObjectToolSupport.HierarchyPath(gameObject),
                    Field = request.Property,
                    Value = request.Value,
                    BodyType = bodyComponent.GetType().Name
                });
            }

            CinemachineToolSupport.MarkDirty(gameObject);
            return new Result
            {
                Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                Object = CinemachineToolSupport.BuildObjectInfo(gameObject),
                PlannedEffects = effects,
                LogPath = ctx.LogPath
            };
        }
    }
}
#endif
