#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    /// <summary>
    /// ui 域 IUnityCliTool 共享 helper:uGUI 元素创建、UIDocument 管理、
    /// VisualTreeAsset/StyleSheet 加载。仅供 ui 域自包含工具复用。
    /// </summary>
    public static class UiToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            SceneToolSupport.EnsureEditorNotBusy(command);
        }

        /// <summary>解析 UI 父节点(Canvas/RectTransform),空返回 null。</summary>
        public static GameObject ResolveUiParent(string parent)
        {
            if (string.IsNullOrEmpty(parent)) return null;
            try
            {
                var resolved = GameObjectToolSupport.ResolveUniqueTarget(parent, "UI parent not found.");
                if (resolved == null || resolved.GetComponent<RectTransform>() == null) return null;
                var instanceId = resolved.GetInstanceID();
                var fresh = EditorUtility.InstanceIDToObject(instanceId) as GameObject;
                return fresh != null && fresh.GetInstanceID() == instanceId && fresh.GetComponent<RectTransform>() != null ? fresh : null;
            }
            catch { return null; }
        }

        /// <summary>解析 GameObject 目标,支持名称/路径/实例ID。</summary>
        public static GameObject ResolveGameObjectTarget(string target)
        {
            return GameObjectToolSupport.ResolveUniqueTarget(target, "Target not found: " + target);
        }

        /// <summary>加载 VisualTreeAsset;不存在抛异常。</summary>
        public static VisualTreeAsset LoadVisualTreeAsset(string path)
        {
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (vta == null) throw new UnityCliBridge.BridgeCommandException("E_UI_UXML_NOT_FOUND", "VisualTreeAsset not found: " + path, "Pass an existing Assets/*.uxml path.");
            return vta;
        }

        /// <summary>加载 StyleSheet。</summary>
        public static StyleSheet LoadStyleSheet(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var ss = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (ss == null) throw new UnityCliBridge.BridgeCommandException("E_UI_USS_NOT_FOUND", "StyleSheet not found: " + path, "Pass an existing Assets/*.uss path.");
            return ss;
        }

        /// <summary>校验 uxml 路径。</summary>
        public static string RequireUxmlPath(string rawPath)
        {
            return RequireUiDocumentPath(rawPath, ".uxml", "E_UI_UXML_PATH_REQUIRED");
        }

        /// <summary>校验 USS 路径。</summary>
        public static string RequireUssPath(string rawPath)
        {
            return RequireUiDocumentPath(rawPath, ".uss", "E_UI_USS_PATH_REQUIRED");
        }

        /// <summary>校验 UI 文档路径在 Assets/ 内且不含路径穿越段。</summary>
        public static string RequireUiDocumentPath(string rawPath, string extension, string errorCode)
        {
            var path = AssetToolSupport.RequireAssetPath(rawPath);
            if (!path.EndsWith(extension, System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException(errorCode, "Pass an Assets/*" + extension + " path.", "Use a canonical path under Assets/ without path traversal segments.");
            AssetToolSupport.AssetFullPath(path);
            return path;
        }

        /// <summary>校验 UXML 或 USS 文档路径。</summary>
        public static string RequireUiDocumentAssetPath(string rawPath)
        {
            var path = AssetToolSupport.RequireAssetPath(rawPath);
            if (!path.EndsWith(".uxml", System.StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".uss", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_PATH_INVALID", "UI document path must end with .uxml or .uss: " + path, "Pass a canonical Assets/*.uxml or Assets/*.uss path.");
            AssetToolSupport.AssetFullPath(path);
            return path;
        }

        public static void ValidateVisualElementSize(float? width, float? height)
        {
            ValidateFinite(width, "Width");
            ValidateFinite(height, "Height");
            if ((width.HasValue && width.Value < 0f) || (height.HasValue && height.Value < 0f))
                throw new UnityCliBridge.BridgeCommandException("E_UI_RANGE_INVALID", "Width and Height must be finite and non-negative.", "Pass finite dimensions greater than or equal to zero.");
        }

        /// <summary>在 effect 前验证文本资产路径与覆盖策略。</summary>
        public static void ValidateTextAssetCreation(string path, bool overwrite, string existsErrorCode)
        {
            if (File.Exists(SceneToolSupport.AssetFullPath(path)) && !overwrite)
                throw new UnityCliBridge.BridgeCommandException(existsErrorCode, "Asset already exists: " + path, "Pass overwrite=true or choose a different path.");
        }

        /// <summary>写入已验证的文本资产。</summary>
        public static void EnsureTextAsset(string path, string defaultContent)
        {
            SceneToolSupport.CreateAssetDirectory(path);
            File.WriteAllText(SceneToolSupport.AssetFullPath(path), defaultContent, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }

        /// <summary>默认 UXML 模板。</summary>
        public static string DefaultUxml(string ussPath)
        {
            var ussRef = string.IsNullOrEmpty(ussPath) ? "" : " <ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">";
            if (string.IsNullOrEmpty(ussRef))
                return "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">\n  <ui:Label text=\"Hello World\" />\n</ui:UXML>\n";
            return "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">\n  <Style src=\"" + Path.GetFileName(ussPath) + "\" />\n  <ui:Label text=\"Hello World\" />\n</ui:UXML>\n";
        }

        /// <summary>默认 USS 模板。</summary>
        public static string DefaultUss() => ":root { font-size: 14px; }\n";

        public static void ValidateUguiRequest(int? fontSize, float? width, float? height, float? x, float? y)
        {
            if (fontSize.HasValue && fontSize.Value <= 0) throw new UnityCliBridge.BridgeCommandException("E_UI_RANGE_INVALID", "FontSize must be positive.", "Pass a positive fontSize.");
            ValidateFinite(width, "Width"); ValidateFinite(height, "Height"); ValidateFinite(x, "X"); ValidateFinite(y, "Y");
            if (width.HasValue && width.Value < 0f || height.HasValue && height.Value < 0f)
                throw new UnityCliBridge.BridgeCommandException("E_UI_RANGE_INVALID", "Width and Height cannot be negative.", "Pass non-negative dimensions.");
        }
        private static void ValidateFinite(float? value, string field)
        {
            if (value.HasValue && (float.IsNaN(value.Value) || float.IsInfinity(value.Value)))
                throw new UnityCliBridge.BridgeCommandException("E_UI_VALUE_INVALID", field + " must be finite.", "Pass a finite numeric value.");
        }

        /// <summary>构造 UI 元素引用 DTO。</summary>
        public static UiElementRef BuildUiElementRef(GameObject go, string kind)
        {
            return new UiElementRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go), Kind = kind };
        }
    }

    // ── ui 域 DTO ──

    public sealed class UiElementRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public string Kind { get; set; } }
    public sealed class UiDocumentAssetInfo { public string Path { get; set; } public string Name { get; set; } public bool Exists { get; set; } public string VisualTreeName { get; set; } public string StyleSheetName { get; set; } }
    public sealed class UiDocumentLiveInfo { public string VisualTreeAsset { get; set; } public string PanelSettings { get; set; } public double SortingOrder { get; set; } }
    public sealed class UiPanelSettingsInfo { public string Path { get; set; } public string ScaleMode { get; set; } public int ReferenceWidth { get; set; } public int ReferenceHeight { get; set; } public double SortingOrder { get; set; } }
    public sealed class UiVisualElementInfo { public string Name { get; set; } public string Text { get; set; } public string Display { get; set; } public float? Width { get; set; } public float? Height { get; set; } public string[] Classes { get; set; } }
    public sealed class UiPlannedEffect { public string Type { get; set; } public string Target { get; set; } public string Path { get; set; } public string Uxml { get; set; } public string Uss { get; set; } }
    public sealed class UiHierarchyItem { public string Name { get; set; } public string Path { get; set; } public string Type { get; set; } public long ChildCount { get; set; } }
}
#endif
