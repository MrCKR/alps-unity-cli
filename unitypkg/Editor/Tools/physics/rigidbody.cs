#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.rigidbody — 添加/配置/校验目标 GameObject 的 Rigidbody。
    /// </summary>
    public class PhysicsRigidbodyTool : IUnityCliTool<PhysicsRigidbodyTool.Request, PhysicsRigidbodyTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public float? Mass { get; set; }
            public float? Drag { get; set; }
            public float? AngularDrag { get; set; }
            public bool? UseGravity { get; set; }
            public bool? IsKinematic { get; set; }
            public bool Validate { get; set; }
        }

        public sealed class Result
        {
            public bool Validate { get; set; }
            public GameObjectTargetRef Target { get; set; }
            public bool HasRigidbody { get; set; }
            public RigidbodyInfo Rigidbody { get; set; }
            public Rigidbody2DInfo Rigidbody2D { get; set; }
            public List<string> NextActions { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.rigidbody",
            Summary = "Add, configure, or validate a Rigidbody on a target GameObject.",
            Description = "Add a Rigidbody (if missing) and configure mass/drag/angularDrag/useGravity/isKinematic. validate=true reads existing Rigidbody/Rigidbody2D without mutating.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Validate rigidbody", Input = new Request { Target = "Player", Validate = true } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.rigidbody requires --target.");
            var rigidbody = target.GetComponent<Rigidbody>();
            if (request.Validate)
            {
                var rigidbody2D = target.GetComponent<Rigidbody2D>();
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Validate = true,
                    Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                    HasRigidbody = rigidbody != null,
                    Rigidbody = rigidbody == null ? null : PhysicsToolSupport.BuildRigidbodyInfo(rigidbody),
                    Rigidbody2D = rigidbody2D == null ? null : PhysicsToolSupport.BuildRigidbody2DInfo(rigidbody2D),
                    NextActions = rigidbody != null ? new List<string>() : new List<string> { "No Rigidbody is attached; run physics.rigidbody without validate=true to add and configure one." }
                });
            }
            if (rigidbody == null) rigidbody = UnityCliSceneMutation.AddComponent<Rigidbody>(target);
            if (request.Mass.HasValue) rigidbody.mass = request.Mass.Value;
            if (request.Drag.HasValue) rigidbody.drag = request.Drag.Value;
            if (request.AngularDrag.HasValue) rigidbody.angularDrag = request.AngularDrag.Value;
            if (request.UseGravity.HasValue) rigidbody.useGravity = request.UseGravity.Value;
            if (request.IsKinematic.HasValue) rigidbody.isKinematic = request.IsKinematic.Value;
            UnityCliSceneMutation.MarkSceneDirty(target.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                HasRigidbody = true,
                Rigidbody = PhysicsToolSupport.BuildRigidbodyInfo(rigidbody),
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.rigidbody", Target = GameObjectToolSupport.HierarchyPath(target) } }
            });
        }
    }
}
#endif
