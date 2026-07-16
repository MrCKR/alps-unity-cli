#if UNITY_EDITOR
using System.Collections.Generic; using System.Linq; using UnityEditor; using UnityEngine;
namespace UnityCli
{
    public sealed class PhysicsJointResult { public bool DryRun { get; set; } public GameObjectTargetRef Target { get; set; } public string Type { get; set; } public bool Exists { get; set; } public long Removed { get; set; } public List<PhysicsPlannedEffect> PlannedEffects { get; set; } }
    public static class PhysicsJointToolSupport
    {
        public static (GameObject, System.Type) Resolve(string targetName, string typeName, string toolName) { var target = GameObjectToolSupport.ResolveUniqueTarget(targetName, toolName + " requires target."); var type = GameObjectToolSupport.ResolveComponentType((typeName ?? "FixedJoint").EndsWith("Joint") ? typeName ?? "FixedJoint" : typeName + "Joint"); if (!typeof(Joint).IsAssignableFrom(type)) throw new UnityCliBridge.BridgeCommandException("E_PHYSICS_JOINT_TYPE_INVALID", "Joint type must derive from UnityEngine.Joint: " + typeName, "Use FixedJoint, HingeJoint, SpringJoint, or CharacterJoint."); return (target, type); }
        public static GameObjectTargetRef TargetRef(GameObject target) => new GameObjectTargetRef { Id = target.GetInstanceID(), Name = target.name, Path = GameObjectToolSupport.HierarchyPath(target) };
        public static List<PhysicsPlannedEffect> Effects(string toolName, string operation, string typeName) => new List<PhysicsPlannedEffect> { new PhysicsPlannedEffect { Type = toolName, Property = operation, Value = typeName } };
    }
}
#endif
