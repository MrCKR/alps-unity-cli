#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityCli
{
    /// <summary>graphics.volume — 列出场景中的 Volume 组件(通过反射,兼容 URP/HDRP)。</summary>
    public class GraphicsVolumeTool : IUnityCliTool<GraphicsVolumeTool.Request, GraphicsVolumeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } }
        public sealed class Result { public List<VolumeInfo> Volumes { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "graphics.volume", Summary = "List Volume components in the scene, optionally filtered by target.", Description = "Find Volume components (URP/HDRP) via reflection. Filter by GameObject name or hierarchy path.", Group = "graphics", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List all volumes", Input = new Request { } } } };
        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request r, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var volumes = new List<VolumeInfo>();
            var volumeType = GraphicsToolSupport.FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null) return new System.Threading.Tasks.ValueTask<Result>(new Result { Volumes = volumes });
            foreach (var component in Resources.FindObjectsOfTypeAll(volumeType))
            {
                var typed = component as Component;
                if (typed == null || typed.gameObject == null) continue;
                if (EditorUtility.IsPersistent(typed.gameObject)) continue;
                if (!string.IsNullOrEmpty(r.Target) && !GameObjectToolSupport.HierarchyPath(typed.gameObject).Equals(r.Target, System.StringComparison.OrdinalIgnoreCase) && !typed.gameObject.name.Equals(r.Target, System.StringComparison.OrdinalIgnoreCase)) continue;
                var type = typed.GetType();
                var enabledProp = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
                var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
                var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
                var profile = GraphicsToolSupport.MemberValue(type, typed, "profile") as UnityEngine.Object;
                var sharedProfile = GraphicsToolSupport.MemberValue(type, typed, "sharedProfile") as UnityEngine.Object;
                var effectiveProfile = profile ?? sharedProfile;
                volumes.Add(new VolumeInfo
                {
                    Target = new GameObjectTargetRef { Id = typed.gameObject.GetInstanceID(), Name = typed.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(typed.gameObject) },
                    Enabled = enabledProp == null || (bool)enabledProp.GetValue(typed, null),
                    IsGlobal = isGlobalField != null && (bool)isGlobalField.GetValue(typed),
                    Priority = priorityField == null ? 0 : (float)priorityField.GetValue(typed),
                    Weight = weightField == null ? 0 : (float)weightField.GetValue(typed),
                    Profile = effectiveProfile == null ? null : new AssetRef { Name = effectiveProfile.name, Path = AssetDatabase.GetAssetPath(effectiveProfile) }
                });
            }
            return new System.Threading.Tasks.ValueTask<Result>(new Result { Volumes = volumes });
        }
    }
}
#endif
