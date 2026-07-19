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
            var targetId = target.GetInstanceID();
            var type = resolved.Item2;
            var existing = target.GetComponent(type) as Joint;
            Rigidbody connectedBody = null;
            var connectedBodyId = 0;
            if (!string.IsNullOrEmpty(request.ConnectedBody))
            {
                var connectedTarget = GameObjectToolSupport.ResolveUniqueTarget(request.ConnectedBody, "Connected body target not found.");
                connectedBody = connectedTarget.GetComponent<Rigidbody>();
                if (connectedBody == null)
                    throw new UnityCliBridge.BridgeCommandException("E_RIGIDBODY_NOT_FOUND", "Connected body target has no Rigidbody: " + connectedTarget.name, "Add a Rigidbody to the connected body before adding this joint.");
                connectedBodyId = connectedBody.GetInstanceID();
            }
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
            ctx.CommitEffect();
            if (existing == null) UnityCliSceneMutation.AddComponent(target, type);
            var joint = target.GetComponent(type) as Joint;
            if (joint == null)
            {
                throw new UnityCliBridge.BridgeCommandException(
                    "E_PHYSICS_JOINT_ADD_FAILED",
                    "Unity did not attach the requested Joint component: " + type.Name,
                    "Confirm the requested joint type is compatible with the target GameObject and retry.");
            }

            if (connectedBody != null)
            {
                joint.connectedBody = connectedBody;
                if (joint.connectedBody != connectedBody)
                    throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_ADD_STATE_MISMATCH", "Joint connectedBody did not match the requested Rigidbody.", "Inspect the Joint component and retry after the Editor is idle.");
            }
            UnityCliSceneMutation.MarkSceneDirty(target.scene);
            // proof 必须从 instance ID 重解析目标、Joint 及 connectedBody，不能复用写前引用。
            var observedTarget = EditorUtility.InstanceIDToObject(targetId) as GameObject;
            var observedJoint = observedTarget == null ? null : observedTarget.GetComponent(type) as Joint;
            var observedConnectedBody = connectedBodyId == 0 ? null : EditorUtility.InstanceIDToObject(connectedBodyId) as Rigidbody;
            if (observedJoint == null || (connectedBodyId != 0 && (observedConnectedBody == null || observedJoint.connectedBody != observedConnectedBody)))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_ADD_STATE_MISMATCH", "Requested Joint component state was not present after mutation.", "Inspect the target GameObject and retry.");
            return new ValueTask<PhysicsJointResult>(new PhysicsJointResult
            {
                Target = PhysicsJointToolSupport.TargetRef(observedTarget),
                Type = type.Name,
                Exists = true,
                PlannedEffects = PhysicsJointToolSupport.Effects("physics.joint.add", existing == null ? "add" : "configure", type.Name)
            });
        }
    }
}
#endif
