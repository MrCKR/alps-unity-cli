#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>scene.volumes 内置 Context：只读当前场景 Volume 摘要。</summary>
        public sealed class SceneVolumesContext : IUnityCliContext<SceneVolumesContext.Request, SceneVolumesContext.Result>
        {
            public sealed class Request { public string Target { get; set; } public int PageSize { get; set; } public string Cursor { get; set; } }
            public sealed class Result { public List<VolumeInfo> Volumes { get; set; } public long Count { get; set; } public PageInfo PageInfo { get; set; } }
            public sealed class VolumeInfo { public GameObjectRef Target { get; set; } public bool Enabled { get; set; } public bool IsGlobal { get; set; } public double Priority { get; set; } public double Weight { get; set; } public bool HasProfile { get; set; } public AssetRef Profile { get; set; } }
            public sealed class GameObjectRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } }
            public sealed class AssetRef { public string Name { get; set; } public string Path { get; set; } public string Guid { get; set; } }
            public sealed class PageInfo { public bool HasMore { get; set; } public long PageSize { get; set; } }

            // 返回 Context 的发现元数据。
            public ContextDescriptor Describe() => new ContextDescriptor
            {
                Name = "scene.volumes", Summary = "List Volume components in the active scene with target object, profile reference, and pagination metadata.",
                Description = "Read Volume components in the active scene with target GameObject reference, enabled state, global flag, priority, weight, profile reference, count, and pageInfo. Optional target filters by GameObject name or hierarchy path. Returns an empty list when UnityEngine.Rendering.Volume is unavailable. This context is read-only and does not modify render settings or Volume profiles.",
                Category = "scene",Source = "built-in", Target = "editor", AvailabilityScope = "editorSession", PlayModeSupport = "supported",
                Examples = new[] { new ContextExample { Description = "List scene Volumes", Input = new Request { PageSize = 50 } }, new ContextExample { Description = "Filter Volumes by target", Input = new Request { Target = "Main Camera", PageSize = 50 } } }
            };

            /// <summary>反射读取已加载 Volume，不创建或修改 profile。</summary>
            public System.Threading.Tasks.ValueTask<Result> ReadAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
            {
                request = request ?? new Request();
                var pageSize = request.PageSize == 0 ? 100 : request.PageSize;
                var volumes = SceneVolumeInfosFrom(request.Target);
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Volumes = volumes, Count = volumes.Count, PageInfo = new PageInfo { HasMore = false, PageSize = pageSize }
                });
            }
        }

        // 查询非持久化 Volume 组件；类型不可用时返回空列表。
        private static List<SceneVolumesContext.VolumeInfo> SceneVolumeInfosFrom(string target)
        {
            var result = new List<SceneVolumesContext.VolumeInfo>();
            var volumeType = FindType("UnityEngine.Rendering.Volume");
            if (volumeType == null) return result;
            var components = new List<Component>();
            foreach (var value in Resources.FindObjectsOfTypeAll(volumeType))
            {
                var component = value as Component;
                if (component == null || component.gameObject == null || EditorUtility.IsPersistent(component.gameObject)) continue;
                var path = HierarchyPath(component.gameObject);
                if (!string.IsNullOrEmpty(target) && !path.Equals(target, StringComparison.OrdinalIgnoreCase) && !component.gameObject.name.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                components.Add(component);
            }
            components.Sort((left, right) => string.Compare(HierarchyPath(left.gameObject), HierarchyPath(right.gameObject), StringComparison.OrdinalIgnoreCase));
            foreach (var component in components) result.Add(SceneVolumeInfoFrom(component));
            return result;
        }

        // 读取一个 Volume 的公开字段与有效 profile 引用。
        private static SceneVolumesContext.VolumeInfo SceneVolumeInfoFrom(Component component)
        {
            var type = component.GetType();
            var enabledProperty = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            var isGlobalField = type.GetField("isGlobal", BindingFlags.Instance | BindingFlags.Public);
            var priorityField = type.GetField("priority", BindingFlags.Instance | BindingFlags.Public);
            var weightField = type.GetField("weight", BindingFlags.Instance | BindingFlags.Public);
            var profile = MemberValue(type, component, "profile") as UnityEngine.Object;
            var sharedProfile = MemberValue(type, component, "sharedProfile") as UnityEngine.Object;
            var effectiveProfile = profile ?? sharedProfile;
            return new SceneVolumesContext.VolumeInfo
            {
                Target = new SceneVolumesContext.GameObjectRef { Id = component.gameObject.GetInstanceID(), Name = component.gameObject.name, Path = HierarchyPath(component.gameObject) },
                Enabled = enabledProperty == null || Convert.ToBoolean(enabledProperty.GetValue(component, null), CultureInfo.InvariantCulture),
                IsGlobal = isGlobalField != null && Convert.ToBoolean(isGlobalField.GetValue(component), CultureInfo.InvariantCulture),
                Priority = priorityField == null ? 0d : Convert.ToDouble(priorityField.GetValue(component), CultureInfo.InvariantCulture),
                Weight = weightField == null ? 0d : Convert.ToDouble(weightField.GetValue(component), CultureInfo.InvariantCulture),
                HasProfile = effectiveProfile != null, Profile = SceneVolumeAssetRefFrom(effectiveProfile)
            };
        }

        // 将 Volume profile 映射为资产引用；无 profile 时返回无值。
        private static SceneVolumesContext.AssetRef SceneVolumeAssetRefFrom(UnityEngine.Object asset)
        {
            if (asset == null) return null;
            var path = NormalizeAssetPath(AssetDatabase.GetAssetPath(asset));
            return new SceneVolumesContext.AssetRef { Name = asset.name, Path = path, Guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path) };
        }
    }
}
#endif
