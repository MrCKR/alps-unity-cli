#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// physics.joint — 在目标 GameObject 上添加/移除/配置 Joint 组件。
    /// 对齐旧 PhysicsJointCommand handler;支持 dryRun。
    /// </summary>
    public class PhysicsJointTool : IUnityCliTool<PhysicsJointTool.Request, PhysicsJointTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Type { get; set; }
            public string Action { get; set; }
            public string ConnectedBody { get; set; }
            public int? ComponentIndex { get; set; }
            public bool? DryRun { get; set; }
        }

        public sealed class Result
        {
            public string Action { get; set; }
            public bool DryRun { get; set; }
            public GameObjectTargetRef Target { get; set; }
            public string Type { get; set; }
            public bool Exists { get; set; }
            public long Removed { get; set; }
            public List<PhysicsPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "physics.joint",
            Summary = "Add, configure, or remove a Joint component on a target GameObject.",
            Description = "Add a Joint (FixedJoint, HingeJoint, SpringJoint, CharacterJoint) to a target, or remove matching joints by type. action=add (default) or remove. Supports connectedBody and componentIndex. dryRun=true previews.",
            Group = "physics", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" },
            Target = "editor", PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000,
            Examples = new[] { new ToolExample { Description = "Add FixedJoint", Input = new Request { Target = "Player", Type = "FixedJoint" } } }
        };

        public Result Execute(Request request, UnityCliProjectToolContext ctx)
        {
            var target = GameObjectToolSupport.ResolveUniqueTarget(request.Target, "physics.joint requires --target.");
            var typeName = request.Type ?? "FixedJoint";
            var type = GameObjectToolSupport.ResolveComponentType(typeName.EndsWith("Joint") ? typeName : typeName + "Joint");
            if (!typeof(Joint).IsAssignableFrom(type))
                throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_TYPE_INVALID", "Joint type must derive from UnityEngine.Joint: " + typeName, "Use FixedJoint, HingeJoint, SpringJoint, or CharacterJoint.");
            var action = request.Action ?? "add";
            var dryRun = request.DryRun ?? false;

            if (action.ToLowerInvariant() == "remove")
            {
                var joints = target.GetComponents(type).Cast<Joint>().ToArray();
                if (joints.Length == 0)
                    throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_NOT_FOUND", "No matching Joint component found on target: " + type.Name, "Check the target and joint type, or run physics.joint with action add first.");
                var componentIndex = request.ComponentIndex ?? -1;
                long removed;
                if (componentIndex >= 0)
                {
                    if (componentIndex >= joints.Length)
                        throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_INDEX_INVALID", "componentIndex is outside the matching Joint component range.", "Use a componentIndex from 0 to " + (joints.Length - 1) + ".");
                    removed = 1;
                    if (!dryRun) Undo.DestroyObjectImmediate(joints[componentIndex]);
                }
                else
                {
                    removed = joints.Length;
                    if (!dryRun) foreach (var existing in joints) Undo.DestroyObjectImmediate(existing);
                }
                if (!dryRun) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
                return new Result
                {
                    Action = "remove", DryRun = dryRun,
                    Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                    Type = type.Name, Removed = removed,
                    PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.joint", Property = "remove", Value = type.Name } }
                };
            }

            // add/configure
            var existingJoint = target.GetComponent(type) as Joint;
            if (dryRun)
            {
                return new Result
                {
                    Action = existingJoint == null ? "add" : "configure", DryRun = true,
                    Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                    Type = type.Name, Exists = existingJoint != null,
                    PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.joint", Property = existingJoint == null ? "add" : "configure", Value = type.Name } }
                };
            }
            var joint = existingJoint ?? Undo.AddComponent(target, type) as Joint;
            if (!string.IsNullOrEmpty(request.ConnectedBody))
            {
                var connectedGo = GameObjectToolSupport.ResolveUniqueTarget(request.ConnectedBody, "Connected body target not found.");
                joint.connectedBody = connectedGo.GetComponent<Rigidbody>();
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(target.scene);
            return new Result
            {
                Action = existingJoint == null ? "add" : "configure", DryRun = false,
                Target = new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) },
                Type = type.Name, Exists = true,
                PlannedEffects = new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = "physics.joint", Property = existingJoint == null ? "add" : "configure", Value = type.Name } }
            };
        }
    }
}
#endif
