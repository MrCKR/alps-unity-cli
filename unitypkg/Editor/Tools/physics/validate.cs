#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.validate — 校验目标 GameObject 的 Rigidbody/Collider 配置合理性。
    /// </summary>
    public class PhysicsValidateTool : IUnityCliTool<PhysicsValidateTool.Request, PhysicsValidateTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public bool Valid { get; set; }
            public List<string> Issues { get; set; }
            public bool HasRigidbody { get; set; }
            public bool HasRigidbody2D { get; set; }
            public long ColliderCount { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.validate",
            Summary = "Validate Rigidbody and Collider configuration on a target GameObject.",
            Description = "Check a GameObject's physics setup for common issues: Rigidbody without Collider, mixed 2D/3D bodies, kinematic body with only trigger colliders.",
            Group = "physics",DestructiveHint = false,
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Validate Player", Input = new Request { Target = "Player" } } }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var target = request.Target ?? request.Path;
            var go = string.IsNullOrEmpty(target) ? null : UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null)
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "physics.validate requires a target GameObject.", "Pass --target <path>.");
            var rb = go.GetComponent<Rigidbody>();
            var rb2d = go.GetComponent<Rigidbody2D>();
            var colliders = go.GetComponents<Collider>();
            var colliders2d = go.GetComponents<Collider2D>();
            var issues = new List<string>();
            if (rb != null && colliders.Length == 0 && colliders2d.Length == 0)
                issues.Add("Rigidbody without any Collider: physics body will not collide.");
            if (rb != null && rb2d != null)
                issues.Add("Both Rigidbody and Rigidbody2D present: ambiguous physics domain.");
            if (rb != null && rb.isKinematic && colliders.Length > 0)
            {
                var anyNonTrigger = false;
                foreach (var c in colliders) if (!c.isTrigger) { anyNonTrigger = true; break; }
                if (!anyNonTrigger) issues.Add("Kinematic Rigidbody with only trigger colliders: no solid collision.");
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Valid = issues.Count == 0, Issues = issues,
                HasRigidbody = rb != null, HasRigidbody2D = rb2d != null, ColliderCount = colliders.Length + colliders2d.Length
            });
        }
    }
}
#endif
