#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace UnityCli.Cinemachine
{
    public sealed class BrainEnsureTool : IUnityCliTool<BrainEnsureTool.Request, BrainEnsureTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public CinemachinePackageStatus Cinemachine { get; set; } public CinemachineBrainInfo Brain { get; set; } public List<CinemachinePlannedEffect> PlannedEffects { get; set; } public string LogPath { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "cinemachine.brain.ensure", Summary = "Ensure a CinemachineBrain exists.", Description = "Adds a CinemachineBrain to the target, current brain owner, or Main Camera; creates an owner when needed.", Group = "cinemachine", Version = "1.0.0", RequiresPackages = new[] { "com.unity.cinemachine" }, SideEffects = new[] { "scene-modify", "undo-stack" }, Target = "editor", PlayModeSupport = "session-affecting", DefaultTimeoutMs = 30000, MaxTimeoutMs = 60000, Examples = new[] { new ToolExample { Description = "Ensure Main Camera has a brain", Input = new Request { Target = "Main Camera" } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default) { CinemachineToolSupport.EnsureEditorReady("cinemachine.brain.ensure"); CinemachineToolSupport.EnsureCinemachineAvailable(); var owner = !string.IsNullOrWhiteSpace(request.Target) ? CinemachineToolSupport.ResolveRequiredTarget(request.Target, "Pass a GameObject name, hierarchy path or instance id.") : CinemachineToolSupport.FindOrCreateBrainOwnerCandidate(); if (owner == null) { owner = new GameObject("CinemachineBrain"); UnityCliSceneMutation.RegisterCreatedObject(owner, "UnityCli Cinemachine Brain Ensure"); } var brain = owner.GetComponent(CinemachineToolSupport.BrainType) ?? UnityCliSceneMutation.AddComponent(owner, CinemachineToolSupport.BrainType); CinemachineToolSupport.MarkDirty(owner); return new(new Result { Cinemachine = CinemachineToolSupport.BuildPackageStatus(), Brain = CinemachineToolSupport.BuildBrainInfo(brain), PlannedEffects = new List<CinemachinePlannedEffect> { new CinemachinePlannedEffect { Type = "cinemachine.brain.ensure", Target = GameObjectToolSupport.HierarchyPath(owner) } }, LogPath = ctx.LogPath }); }
    }
}
#endif
