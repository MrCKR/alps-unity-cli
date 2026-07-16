#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli.Cinemachine
{
    /// <summary>扩展组件 Tool 的共享目标与类型解析。</summary>
    internal static class CinemachineExtensionToolSupport
    {
        public static UnityEngine.GameObject Target(string target, string tool) { CinemachineToolSupport.EnsureEditorReady(tool); CinemachineToolSupport.EnsureCinemachineAvailable(); var value = CinemachineToolSupport.ResolveRequiredTarget(target, tool + " requires target."); CinemachineToolSupport.RequireCameraComponent(value); return value; }
        public static System.Type Type(string name) { var value = CinemachineToolSupport.ResolveComponentType(name) ?? GameObjectToolSupport.FindType(name); if (value == null || CinemachineToolSupport.ExtensionBaseType == null || !CinemachineToolSupport.ExtensionBaseType.IsAssignableFrom(value)) throw new UnityCliBridge.BridgeCommandException("E_CINEMACHINE_COMPONENT_TYPE_NOT_FOUND", "CinemachineExtension type not found: " + name, "Pass a CinemachineExtension-derived type name."); return value; }
        public static List<string> List(UnityEngine.GameObject target) { var result = new List<string>(); foreach (var item in target.GetComponents(CinemachineToolSupport.ExtensionBaseType)) result.Add(item.GetType().Name); return result; }
    }
}
#endif
