#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli.Cinemachine
{
    /// <summary>
    /// cinemachine.brain — 确保 Brain 存在，或读取当前 Brain 状态。
    /// </summary>
    public class BrainTool : IUnityCliTool<BrainTool.Request, BrainTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Action { get; set; }
        }

        public sealed class Result
        {
            public CinemachinePackageStatus Cinemachine { get; set; }
            public CinemachineBrainInfo Brain { get; set; }
            public List<CinemachinePlannedEffect> PlannedEffects { get; set; }
            public string LogPath { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "cinemachine.brain",
            Summary = "Ensure a CinemachineBrain exists, or report the active virtual camera and blending state.",
            Description = "Ensure a CinemachineBrain exists on the target or default camera owner, or report the current brain status. action supports ensure and status. target is optional for both actions.",
            Group = "cinemachine",
            Version = "1.0.0",
            DestructiveHint = false,
            RequiresPackages = new[] { "com.unity.cinemachine" },
            SideEffects = new[] { "scene-modify", "undo-stack" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 60000,
            Examples = new[]
            {
                new ToolExample { Description = "确保 Brain 存在", Input = new Request { Action = "ensure" } },
                new ToolExample { Description = "读取 Brain 状态", Input = new Request { Target = "Main Camera", Action = "status" } }
            }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            CinemachineToolSupport.EnsureEditorReady("cinemachine.brain");
            CinemachineToolSupport.EnsureCinemachineAvailable();

            var action = string.IsNullOrWhiteSpace(request.Action) ? "ensure" : request.Action.Trim().ToLowerInvariant();
            if (action == "ensure")
            {
                var owner = !string.IsNullOrWhiteSpace(request.Target)
                    ? CinemachineToolSupport.ResolveRequiredTarget(request.Target, "Pass a GameObject name, hierarchy path or instance id.")
                    : CinemachineToolSupport.FindOrCreateBrainOwnerCandidate();
                if (owner == null)
                {
                    owner = new GameObject("CinemachineBrain");
                    Undo.RegisterCreatedObjectUndo(owner, "UnityCli Cinemachine Brain Ensure");
                }

                var brain = owner.GetComponent(CinemachineToolSupport.BrainType) ?? Undo.AddComponent(owner, CinemachineToolSupport.BrainType);
                CinemachineToolSupport.MarkDirty(owner);
                return new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Brain = CinemachineToolSupport.BuildBrainInfo(brain),
                    PlannedEffects = new List<CinemachinePlannedEffect>
                    {
                        new CinemachinePlannedEffect { Type = "cinemachine.brain.ensure", Target = GameObjectToolSupport.HierarchyPath(owner) }
                    },
                    LogPath = ctx.LogPath
                };
            }

            if (action == "status")
            {
                var owner = CinemachineToolSupport.ResolveBrainOwner(request.Target);
                var brain = CinemachineToolSupport.RequireBrainComponent(owner);
                return new Result
                {
                    Cinemachine = CinemachineToolSupport.BuildPackageStatus(),
                    Brain = CinemachineToolSupport.BuildBrainInfo(brain),
                    LogPath = ctx.LogPath
                };
            }

            throw new UnityCliBridge.BridgeCommandException(
                "E_CINEMACHINE_BRAIN_ACTION_UNSUPPORTED",
                "Unsupported brain action: " + action,
                "Use ensure or status.");
        }
    }
}
#endif
