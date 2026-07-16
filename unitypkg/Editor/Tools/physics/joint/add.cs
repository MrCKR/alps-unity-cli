#if UNITY_EDITOR
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public class PhysicsJointAddTool : IUnityCliTool<PhysicsJointAddTool.Request, PhysicsJointResult>
    {
        public sealed class Request
        {
            [UnityCliRequired] public string Target { get; set; }
            public string Type { get; set; }
            public string ConnectedBody { get; set; }
        }

        // 返回单一动作的公开 descriptor。
        public ToolDescriptor Describe() => new()
        {
            Name = "physics.joint.add",
            Summary = "Add or configure a Joint component on a scene GameObject.",
            Description = "Adds the requested Joint type to target, or configures its existing first component with connectedBody. This changes scene memory only, supports dryRun=true, and is available in Edit and Play mode.",
            Group = "physics",
            Version = "1.0.0",
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Add fixed joint", Input = new Request { Target = "Player", Type = "FixedJoint" } },
                new ToolExample { Description = "Connect fixed joint", Input = new Request { Target = "Player", Type = "FixedJoint", ConnectedBody = "Enemy" } }
            }
        };

        /// <summary>添加或配置 Joint；添加后重新解析组件，避免 Undo 返回的临时引用失效。</summary>
        public ValueTask<PhysicsJointResult> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, CancellationToken ct = default)
        {
            var resolved = PhysicsJointToolSupport.Resolve(request.Target, request.Type, "physics.joint.add");
            var target = resolved.Item1;
            var type = resolved.Item2;
            var existing = target.GetComponent(type) as Joint;
            if (ctx.DryRun)
            {
                return new ValueTask<PhysicsJointResult>(new PhysicsJointResult
                {
                    DryRun = true,
                    Target = PhysicsJointToolSupport.TargetRef(target),
                    Type = type.Name,
                    Exists = existing != null,
                    PlannedEffects = PhysicsJointToolSupport.Effects("physics.joint.add", existing == null ? "add" : "configure", type.Name)
                });
            }

            ct.ThrowIfCancellationRequested();
            if (existing == null) UnityCliSceneMutation.AddComponent(target, type);
            var joint = target.GetComponent(type) as Joint;
            if (joint == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PHYSICS_JOINT_ADD_FAILED",
                    "Unity did not attach the requested Joint component: " + type.Name,
                    "Confirm the requested joint type is compatible with the target GameObject and retry.");
            }

            if (!string.IsNullOrEmpty(request.ConnectedBody))
            {
                var connectedTarget = GameObjectToolSupport.ResolveUniqueTarget(request.ConnectedBody, "Connected body target not found.");
                joint.connectedBody = connectedTarget.GetComponent<Rigidbody>();
            }
            UnityCliSceneMutation.MarkSceneDirty(target.scene);
            return new ValueTask<PhysicsJointResult>(new PhysicsJointResult
            {
                Target = PhysicsJointToolSupport.TargetRef(target),
                Type = type.Name,
                Exists = true,
                PlannedEffects = PhysicsJointToolSupport.Effects("physics.joint.add", existing == null ? "add" : "configure", type.Name)
            });
        }
    }
}
#endif
