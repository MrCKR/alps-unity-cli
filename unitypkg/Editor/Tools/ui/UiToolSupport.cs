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
            try { return GameObjectToolSupport.ResolveUniqueTarget(parent, "UI parent not found."); } catch { return null; }
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
            var path = SceneToolSupport.NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml", System.StringComparison.OrdinalIgnoreCase))
                throw new UnityCliBridge.BridgeCommandException("E_UI_UXML_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "Use ui.document.create with a uxml path.");
            return path;
        }

        /// <summary>确保文本资产存在,不存在则创建默认内容。</summary>
        public static void EnsureTextAsset(string path, string defaultContent, bool overwrite, string existsErrorCode)
        {
            var fullPath = SceneToolSupport.AssetFullPath(path);
            if (File.Exists(fullPath) && !overwrite)
                throw new UnityCliBridge.BridgeCommandException(existsErrorCode, "Asset already exists: " + path, "Pass overwrite=true or choose a different path.");
            SceneToolSupport.CreateAssetDirectory(path);
            File.WriteAllText(fullPath, defaultContent, System.Text.Encoding.UTF8);
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

        /// <summary>构造 UI 元素引用 DTO。</summary>
        public static UiElementRef BuildUiElementRef(GameObject go, string kind)
        {
            return new UiElementRef { Id = go.GetInstanceID(), Name = go.name, Path = GameObjectToolSupport.HierarchyPath(go), Kind = kind };
        }
    }

    // ── ui 域 DTO ──

    public sealed class UiElementRef { public long Id { get; set; } public string Name { get; set; } public string Path { get; set; } public string Kind { get; set; } }
    public sealed class UiDocumentAssetInfo { public string Path { get; set; } public string Name { get; set; } public bool Exists { get; set; } public string VisualTreeName { get; set; } public string StyleSheetName { get; set; } }
    public sealed class UiDocumentLiveInfo { public string VisualTreeAsset { get; set; } public string PanelSettings { get; set; } public long SortingOrder { get; set; } }
    public sealed class UiPlannedEffect { public string Type { get; set; } public string Target { get; set; } public string Path { get; set; } public string Uxml { get; set; } public string Uss { get; set; } }
    public sealed class UiHierarchyItem { public string Name { get; set; } public string Path { get; set; } public string Type { get; set; } public long ChildCount { get; set; } }
}
#endif
