#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.extension — 枚举、添加或移除 CinemachineExtension 派生组件。
    /// </summary>
    public class ExtensionTool : IUnityCliTool<ExtensionTool.Request, ExtensionTool.Result>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Action { get; set; }
            public string ExtensionType { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public string Action { get; set; }
            public List<string> Extensions { get; set; }
            public long Count { get; set; }
            public string ExtensionType { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.extension",
            Summary = "List, add, or remove CinemachineExtension-derived components on a virtual camera.",
            Description = "List, add, or remove CinemachineExtension-derived components on a virtual camera. action supports list, add, and remove. add/remove mutate the current scene session and keep the existing destructive contract for compatibility.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = true,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "列出现有扩展", Input = new Request { Target = "PlayerCam", Action = "list" } },
                new ToolExample { Description = "添加扩展", Input = new Request { Target = "PlayerCam", Action = "add", ExtensionType = "CinemachineCollider" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.extension");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var target = CinemachineToolSupport.ResolveRequiredTarget(request.Target, "cinemachine extension requires --target <go>.");
            CinemachineToolSupport.RequireCameraComponent(target);
            var action = string.IsNullOrWhiteSpace(request.Action) ? "list" : request.Action.Trim().ToLowerInvariant();

            if (action == "list")
            {
                var extensions = new List<string>();
                if (CinemachineToolSupport.ExtensionBaseType != null)
                {
                    foreach (var component in target.GetComponents(CinemachineToolSupport.ExtensionBaseType))
                    {
                        extensions.Add(component.GetType().Name);
                    }
                }

                return new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Action = "list",
                    Extensions = extensions,
                    Count = extensions.Count,
                    LogPath = ctx.LogPath
                };
            }

            if (string.IsNullOrWhiteSpace(request.ExtensionType))
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_EXTENSION_TYPE_REQUIRED",
                    action == "add" ? "Add extension requires extensionType." : "Remove extension requires extensionType.",
                    "Pass a CinemachineExtension-derived type name.");
            }

            var extensionType = CinemachineToolSupport.ResolveComponentType(request.ExtensionType) ?? GameObjectToolSupport.FindType(request.ExtensionType);
            if (extensionType == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND",
                    "Extension type not found: " + request.ExtensionType,
                    "Ensure the Cinemachine package is fully imported and the type name is correct.");
            }

            if (action == "add")
            {
                if (target.GetComponent(extensionType) != null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_EXTENSION_EXISTS",
                        "Extension already present: " + request.ExtensionType,
                        "Remove it first with action=remove.");
                }
                Undo.AddComponent(target, extensionType);
                CinemachineToolSupport.MarkDirty(target);
                return new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Action = "add",
                    ExtensionType = request.ExtensionType,
                    PlannedEffects = new List<CinemachinePlannedEffect>
                    {
                        new CinemachinePlannedEffect { Type = "cinemachine.extension.add", Target = GameObjectToolSupport.HierarchyPath(target), ExtensionType = request.ExtensionType }
                    },
                    LogPath = ctx.LogPath
                };
            }

            if (action == "remove")
            {
                var existing = target.GetComponent(extensionType);
                if (existing == null)
                {
                    throw new UnityCliBridge.BridgeCommandException(
                        "E_CINEMACHINE_EXTENSION_NOT_FOUND",
                        "Extension not found on target: " + request.ExtensionType,
                        "Use action=list to inspect existing extensions.");
                }
                Undo.DestroyObjectImmediate(existing);
                CinemachineToolSupport.MarkDirty(target);
                return new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Action = "remove",
                    ExtensionType = request.ExtensionType,
                    PlannedEffects = new List<CinemachinePlannedEffect>
                    {
                        new CinemachinePlannedEffect { Type = "cinemachine.extension.remove", Target = GameObjectToolSupport.HierarchyPath(target), ExtensionType = request.ExtensionType }
                    },
                    LogPath = ctx.LogPath
                };
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_EXTENSION_ACTION_UNSUPPORTED",
                "Unsupported extension action: " + action,
                "Use list, add, or remove.");
        }
    }
}
#endif
