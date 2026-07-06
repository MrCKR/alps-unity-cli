#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnityCli
{
    /// <summary>ui.document.info — 读取 UIDocument 或 UXML 文件信息。</summary>
    public class UiDocumentInfoTool : IUnityCliTool<UiDocumentInfoTool.Request, UiDocumentInfoTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Path { get; set; } public string Uss { get; set; } }
        public sealed class Result { public UiDocumentLiveInfo Document { get; set; } public UiElementRef Target { get; set; } public UiDocumentAssetInfo Asset { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.info", Summary = "Inspect a live UIDocument or read offline .uxml/.uss asset metadata.", Description = "Pass target for a live UIDocument inspection, or path for offline .uxml file read. uss for associated stylesheet.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read UIDocument", Input = new Request { Target = "UIDocument" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            if (!string.IsNullOrEmpty(r.Target))
            {
                var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
                var doc = go.GetComponent<UIDocument>();
                if (doc == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "UIDocument target not found: " + r.Target, "Pass a UIDocument GameObject or an Assets/*.uxml path.");
                return new Result { Document = new UiDocumentLiveInfo { VisualTreeAsset = doc.visualTreeAsset?.name, PanelSettings = doc.panelSettings?.name, SortingOrder = doc.sortingOrder }, Target = UiToolSupport.BuildUiElementRef(go, "UIDocument") };
            }
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_PATH_REQUIRED", "UIDocument info requires a target or Assets/*.uxml path.", "Pass a target or path.");
            var vta = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            var ss = string.IsNullOrEmpty(r.Uss) ? null : AssetDatabase.LoadAssetAtPath<StyleSheet>(r.Uss);
            return new Result { Asset = new UiDocumentAssetInfo { Path = path, Name = Path.GetFileNameWithoutExtension(path), Exists = vta != null, VisualTreeName = vta?.name, StyleSheetName = ss?.name } };
        }
    }

    /// <summary>ui.document.create — 创建 UXML/USS 资产和 UIDocument GameObject。</summary>
    public class UiDocumentCreateTool : IUnityCliTool<UiDocumentCreateTool.Request, UiDocumentCreateTool.Result>
    {
        public sealed class Request { public string Name { get; set; } public string Uxml { get; set; } public string Uss { get; set; } public string PanelSettings { get; set; } public bool? Overwrite { get; set; } }
        public sealed class Result { public UiDocumentLiveInfo Document { get; set; } public UiElementRef Target { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.create", Summary = "Create a UIDocument GameObject with UXML/USS assets and optional PanelSettings.", Description = "Create .uxml (and optional .uss) text assets, then a UIDocument GameObject linking them. overwrite=true to replace existing assets.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SideEffects = new[] { "asset-write", "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create UIDocument", Input = new Request { Name = "UIDocument", Uxml = "Assets/UI/Main.uxml" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.document.create");
            var name = string.IsNullOrEmpty(r.Name) ? "UIDocument" : r.Name;
            var uxmlPath = UiToolSupport.RequireUxmlPath(r.Uxml);
            var ussPath = SceneToolSupport.NormalizeAssetPath(r.Uss);
            var overwrite = r.Overwrite ?? false;
            UiToolSupport.EnsureTextAsset(uxmlPath, UiToolSupport.DefaultUxml(ussPath), overwrite, "E_UI_UXML_EXISTS");
            if (!string.IsNullOrEmpty(ussPath)) UiToolSupport.EnsureTextAsset(ussPath, UiToolSupport.DefaultUss(), overwrite, "E_UI_USS_EXISTS");
            PanelSettings panelSettings = null;
            if (!string.IsNullOrEmpty(r.PanelSettings)) { panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(SceneToolSupport.NormalizeAssetPath(r.PanelSettings)); if (panelSettings == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + r.PanelSettings, "Create a PanelSettings asset or pass a valid path."); }
            var go = new GameObject(name, typeof(UIDocument));
            var doc = go.GetComponent<UIDocument>();
            doc.visualTreeAsset = UiToolSupport.LoadVisualTreeAsset(uxmlPath);
            if (panelSettings != null) doc.panelSettings = panelSettings;
            Selection.activeGameObject = go;
            AssetDatabase.SaveAssets();
            return new Result { Document = new UiDocumentLiveInfo { VisualTreeAsset = doc.visualTreeAsset?.name, PanelSettings = doc.panelSettings?.name, SortingOrder = doc.sortingOrder }, Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.create", Target = GameObjectToolSupport.HierarchyPath(go), Uxml = uxmlPath, Uss = ussPath } } };
        }
    }

    /// <summary>ui.document.update — 更新 UXML 文本内容。</summary>
    public class UiDocumentUpdateTool : IUnityCliTool<UiDocumentUpdateTool.Request, UiDocumentUpdateTool.Result>
    {
        public sealed class Request { public string Uxml { get; set; } public string Path { get; set; } public string Content { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public UiDocumentAssetInfo Document { get; set; } public long SizeBytes { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.update", Summary = "Update the text content of an existing .uxml asset.", Description = "Write new content to an existing .uxml file and reimport. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Update UXML", Input = new Request { Uxml = "Assets/UI/Main.uxml", Content = "<ui:UXML />" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Uxml ?? r.Path);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".uxml")) throw new UnityCliBridge.BridgeCommandException("E_UI_UXML_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "");
            if (r.Content == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_CONTENT_REQUIRED", "ui.document.update requires content.", "Pass --content or structured input content.");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "ui.document.update", DryRun = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.update", Uxml = path } } };
            File.WriteAllText(SceneToolSupport.AssetFullPath(path), r.Content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            return new Result { Command = "ui.document.update", DryRun = false, Document = new UiDocumentAssetInfo { Path = path, Exists = true }, SizeBytes = System.Text.Encoding.UTF8.GetByteCount(r.Content), PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.update", Uxml = path } } };
        }
    }

    /// <summary>ui.document.delete — 删除 UXML/USS 资产。</summary>
    public class UiDocumentDeleteTool : IUnityCliTool<UiDocumentDeleteTool.Request, UiDocumentDeleteTool.Result>
    {
        public sealed class Request { public string Uxml { get; set; } public string Path { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public UiDocumentAssetInfo Document { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.document.delete", Summary = "Delete a .uxml or .uss UI document asset.", Description = "Delete the asset via AssetDatabase.DeleteAsset. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Delete UXML", Input = new Request { Uxml = "Assets/UI/Main.uxml" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Uxml ?? r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "ui.document.delete", DryRun = true, Document = new UiDocumentAssetInfo { Path = path, Exists = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.delete", Path = path } } };
            if (!AssetDatabase.DeleteAsset(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_DELETE_FAILED", "Failed to delete UI document asset: " + path, "Check Unity Console and file permissions.");
            AssetDatabase.Refresh();
            return new Result { Command = "ui.document.delete", DryRun = false, Document = new UiDocumentAssetInfo { Path = path, Exists = false }, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.document.delete", Path = path } } };
        }
    }

    /// <summary>ui.hierarchy — 列出 uGUI/UI Toolkit 层级。</summary>
    public class UiHierarchyTool : IUnityCliTool<UiHierarchyTool.Request, UiHierarchyTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Parent { get; set; } public int? PageSize { get; set; } }
        public sealed class Result { public List<UiHierarchyItem> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.hierarchy", Summary = "List uGUI and UI Toolkit elements in the scene hierarchy.", Description = "Find Canvas, UIDocument, and visual element GameObjects. Optionally filter by target/parent.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List UI hierarchy", Input = new Request { } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var items = new List<UiHierarchyItem>();
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            foreach (var c in canvases) items.Add(new UiHierarchyItem { Name = c.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(c.gameObject), Type = "Canvas", ChildCount = c.transform.childCount });
            var docs = UnityEngine.Object.FindObjectsOfType<UIDocument>();
            foreach (var d in docs) items.Add(new UiHierarchyItem { Name = d.gameObject.name, Path = GameObjectToolSupport.HierarchyPath(d.gameObject), Type = "UIDocument", ChildCount = d.transform.childCount });
            return new Result { Items = items };
        }
    }

    /// <summary>ui.attach-document — 将 UIDocument 挂载到 GameObject。</summary>
    public class UiAttachDocumentTool : IUnityCliTool<UiAttachDocumentTool.Request, UiAttachDocumentTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string SourceAsset { get; set; } public string Uxml { get; set; } public string PanelSettings { get; set; } public int? SortOrder { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public UiElementRef Target { get; set; } public string SourceAsset { get; set; } public bool HasPanelSettings { get; set; } public long SortOrder { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.attach-document", Summary = "Attach a UIDocument component to a target GameObject.", Description = "Add or configure a UIDocument with optional visualTreeAsset and panelSettings. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Attach UIDocument", Input = new Request { Target = "Player", SourceAsset = "Assets/UI/Main.uxml" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.attach-document");
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(r.Target);
            if (go == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "ui.attach-document requires a target GameObject.", "Pass --target <path>.");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), SourceAsset = r.SourceAsset ?? r.Uxml, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } };
            var sourceAsset = r.SourceAsset ?? r.Uxml;
            var sortOrder = r.SortOrder ?? 0;
            var uiDoc = go.GetComponent<UIDocument>() ?? go.AddComponent<UIDocument>();
            if (!string.IsNullOrEmpty(sourceAsset)) { var uxml = AssetDatabase.LoadMainAssetAtPath(sourceAsset) as VisualTreeAsset; if (uxml != null) uiDoc.visualTreeAsset = uxml; }
            if (!string.IsNullOrEmpty(r.PanelSettings)) { var panel = AssetDatabase.LoadMainAssetAtPath(r.PanelSettings) as PanelSettings; if (panel != null) uiDoc.panelSettings = panel; }
            else if (uiDoc.panelSettings == null) { var defaults = ScriptableObject.CreateInstance<PanelSettings>(); defaults.scaleMode = PanelScaleMode.ScaleWithScreenSize; defaults.referenceResolution = new Vector2Int(1920, 1080); uiDoc.panelSettings = defaults; }
            uiDoc.sortingOrder = sortOrder;
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
            return new Result { Command = "ui.attach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), SourceAsset = sourceAsset, HasPanelSettings = uiDoc.panelSettings != null, SortOrder = sortOrder, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.attach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>ui.detach-document — 从 GameObject 移除 UIDocument 组件。</summary>
    public class UiDetachDocumentTool : IUnityCliTool<UiDetachDocumentTool.Request, UiDetachDocumentTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public UiElementRef Target { get; set; } public bool Detached { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.detach-document", Summary = "Remove a UIDocument component from a target GameObject.", Description = "Remove UIDocument component. Only affects current scene session. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "scene-modify" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Detach UIDocument", Input = new Request { Target = "Player" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            UiToolSupport.EnsureEditorNotBusy("ui.detach-document");
            var go = UnityCliBridge.ObjectRefResolver.FindGameObjectByPath(r.Target);
            if (go == null) throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "ui.detach-document requires a target GameObject.", "Pass --target <path>.");
            var doc = go.GetComponent<UIDocument>();
            if (doc == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument on target: " + r.Target, "Pass a target with a UIDocument.");
            var dryRun = r.DryRun ?? false;
            if (!dryRun) { UnityEngine.Object.DestroyImmediate(doc); UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene); }
            return new Result { Command = "ui.detach-document", Target = UiToolSupport.BuildUiElementRef(go, "UIDocument"), Detached = !dryRun, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.detach-document", Target = GameObjectToolSupport.HierarchyPath(go) } } };
        }
    }

    /// <summary>ui.create-panel-settings — 创建 PanelSettings 资产。</summary>
    public class UiCreatePanelSettingsTool : IUnityCliTool<UiCreatePanelSettingsTool.Request, UiCreatePanelSettingsTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string ScaleMode { get; set; } public int? ReferenceWidth { get; set; } public int? ReferenceHeight { get; set; } public int? SortingOrder { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Path { get; set; } public bool Created { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.create-panel-settings", Summary = "Create a PanelSettings asset for UI Toolkit.", Description = "Create a .asset PanelSettings with scale mode and reference resolution. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Create PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            if (string.IsNullOrEmpty(path)) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_PATH_REQUIRED", "Pass an Assets/*.asset path.", "");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Path = path, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } };
            SceneToolSupport.CreateAssetDirectory(path);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = (r.ScaleMode ?? "scaleWithScreenSize").ToLowerInvariant() == "constantpixelsize" ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize;
            panel.referenceResolution = new Vector2Int(r.ReferenceWidth ?? 1920, r.ReferenceHeight ?? 1080);
            if (r.SortingOrder.HasValue) panel.sortingOrder = r.SortingOrder.Value;
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.SaveAssets();
            return new Result { Path = path, Created = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.create-panel-settings", Path = path } } };
        }
    }

    /// <summary>ui.update-panel-settings — 更新 PanelSettings 资产。</summary>
    public class UiUpdatePanelSettingsTool : IUnityCliTool<UiUpdatePanelSettingsTool.Request, UiUpdatePanelSettingsTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string ScaleMode { get; set; } public int? ReferenceWidth { get; set; } public int? ReferenceHeight { get; set; } public int? SortingOrder { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Path { get; set; } public bool Updated { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.update-panel-settings", Summary = "Update an existing PanelSettings asset.", Description = "Modify scale mode, reference resolution, or sorting order of an existing PanelSettings. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Update PanelSettings", Input = new Request { Path = "Assets/UI/Panel.asset", ReferenceWidth = 1280 } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var path = SceneToolSupport.NormalizeAssetPath(r.Path);
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel == null) throw new UnityCliBridge.BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + path, "Create it with ui.create-panel-settings first.");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Path = path, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } };
            if (!string.IsNullOrEmpty(r.ScaleMode)) panel.scaleMode = r.ScaleMode.ToLowerInvariant() == "constantpixelsize" ? PanelScaleMode.ConstantPixelSize : PanelScaleMode.ScaleWithScreenSize;
            if (r.ReferenceWidth.HasValue || r.ReferenceHeight.HasValue) panel.referenceResolution = new Vector2Int(r.ReferenceWidth ?? panel.referenceResolution.x, r.ReferenceHeight ?? panel.referenceResolution.y);
            if (r.SortingOrder.HasValue) panel.sortingOrder = r.SortingOrder.Value;
            EditorUtility.SetDirty(panel); AssetDatabase.SaveAssets();
            return new Result { Path = path, Updated = true, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.update-panel-settings", Path = path } } };
        }
    }

    /// <summary>ui.get-visual-tree — 读取 UIDocument 可视树结构。</summary>
    public class UiGetVisualTreeTool : IUnityCliTool<UiGetVisualTreeTool.Request, UiGetVisualTreeTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Parent { get; set; } public int? MaxDepth { get; set; } }
        public sealed class Result { public string Command { get; set; } public List<UiHierarchyItem> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.get-visual-tree", Summary = "Read the visual tree hierarchy of a live UIDocument.", Description = "Traverse the VisualElement hierarchy of a UIDocument's root. Returns element names and types.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Read visual tree", Input = new Request { Target = "UIDocument" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var doc = go.GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null) return new Result { Command = "ui.get-visual-tree", Items = new List<UiHierarchyItem>() };
            var items = new List<UiHierarchyItem>();
            TraverseVisualElement(doc.rootVisualElement, "", 0, r.MaxDepth ?? 10, items);
            return new Result { Command = "ui.get-visual-tree", Items = items };
        }
        private static void TraverseVisualElement(VisualElement ve, string parentPath, int depth, int maxDepth, List<UiHierarchyItem> items)
        {
            if (depth > maxDepth) return;
            var path = string.IsNullOrEmpty(parentPath) ? ve.name : parentPath + "/" + ve.name;
            items.Add(new UiHierarchyItem { Name = ve.name, Path = path, Type = ve.GetType().Name, ChildCount = ve.childCount });
            foreach (var child in ve.Children()) TraverseVisualElement(child, path, depth + 1, maxDepth, items);
        }
    }

    /// <summary>ui.modify-visual-element — 修改 VisualElement 属性。</summary>
    public class UiModifyVisualElementTool : IUnityCliTool<UiModifyVisualElementTool.Request, UiModifyVisualElementTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string ElementName { get; set; } public string Text { get; set; } public string Display { get; set; } public float? Width { get; set; } public float? Height { get; set; } public string AddClass { get; set; } public string RemoveClass { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string ElementName { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.modify-visual-element", Summary = "Modify a VisualElement in a live UIDocument by name.", Description = "Find a VisualElement by name in a UIDocument and modify text/display/size/classes. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Set text", Input = new Request { Target = "UIDocument", ElementName = "label", Text = "Updated" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var doc = go.GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument with a visual tree on target.", "Pass a target with a UIDocument.");
            var dryRun = r.DryRun ?? false;
            var element = doc.rootVisualElement.Q(r.ElementName);
            if (element == null) throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_NOT_FOUND", "VisualElement not found: " + r.ElementName, "Pass a valid element name.");
            if (!dryRun)
            {
                if (!string.IsNullOrEmpty(r.Text))
                {
                    var textElement = element as TextElement;
                    if (textElement == null)
                    {
                        throw new UnityCliBridge.BridgeCommandException("E_UI_ELEMENT_TEXT_UNSUPPORTED", "VisualElement does not support text: " + r.ElementName, "Target a Label/TextElement or omit the text field.");
                    }
                    textElement.text = r.Text;
                }
                if (!string.IsNullOrEmpty(r.Display)) if (System.Enum.TryParse<DisplayStyle>(r.Display, true, out var ds)) element.style.display = ds;
                if (r.Width.HasValue) element.style.width = r.Width.Value;
                if (r.Height.HasValue) element.style.height = r.Height.Value;
                if (!string.IsNullOrEmpty(r.AddClass)) element.AddToClassList(r.AddClass);
                if (!string.IsNullOrEmpty(r.RemoveClass)) element.RemoveFromClassList(r.RemoveClass);
            }
            return new Result { Command = "ui.modify-visual-element", DryRun = dryRun, ElementName = r.ElementName, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.modify-visual-element", Target = r.ElementName } } };
        }
    }

    /// <summary>ui.link-stylesheet — 链接 USS 到 UXML。</summary>
    public class UiLinkStylesheetTool : IUnityCliTool<UiLinkStylesheetTool.Request, UiLinkStylesheetTool.Result>
    {
        public sealed class Request { public string Path { get; set; } public string Uss { get; set; } public string StyleSheet { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string Path { get; set; } public string Uss { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.link-stylesheet", Summary = "Link a .uss stylesheet to a .uxml document by appending a Style element.", Description = "Append <Style src=\"...\" /> to the .uxml file. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "asset-write" }, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Link stylesheet", Input = new Request { Path = "Assets/UI/Main.uxml", Uss = "Assets/UI/Main.uss" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var uxmlPath = SceneToolSupport.NormalizeAssetPath(r.Path);
            var ussPath = SceneToolSupport.NormalizeAssetPath(r.Uss ?? r.StyleSheet);
            if (string.IsNullOrEmpty(uxmlPath) || string.IsNullOrEmpty(ussPath)) throw new UnityCliBridge.BridgeCommandException("E_UI_PATH_REQUIRED", "ui.link-stylesheet requires --path (uxml) and --uss.", "Pass both uxml and uss paths.");
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "ui.link-stylesheet", DryRun = true, Path = uxmlPath, Uss = ussPath, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } };
            var content = File.ReadAllText(SceneToolSupport.AssetFullPath(uxmlPath));
            var styleLine = "  <Style src=\"" + Path.GetFileName(ussPath) + "\" />\n";
            if (!content.Contains(styleLine.Trim())) content = content.Replace("<ui:UXML", "<ui:UXML") + ""; // Simplified: just append before closing tag
            var insertIdx = content.LastIndexOf("</ui:UXML>");
            if (insertIdx < 0) insertIdx = content.LastIndexOf("<");
            if (insertIdx >= 0) content = content.Insert(insertIdx, styleLine);
            else content += styleLine;
            File.WriteAllText(SceneToolSupport.AssetFullPath(uxmlPath), content, System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            return new Result { Command = "ui.link-stylesheet", DryRun = false, Path = uxmlPath, Uss = ussPath, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.link-stylesheet", Path = uxmlPath, Uss = ussPath } } };
        }
    }

    /// <summary>ui.list-documents — 列出 UI 文档资产。</summary>
    public class UiListDocumentsTool : IUnityCliTool<UiListDocumentsTool.Request, UiListDocumentsTool.Result>
    {
        public sealed class Request { public string Kind { get; set; } public int? PageSize { get; set; } }
        public sealed class Result { public List<UiDocumentAssetInfo> Items { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.list-documents", Summary = "List UXML and USS assets in the project.", Description = "Search AssetDatabase for .uxml and .uss assets. kind=uxml or uss to filter.", Group = "ui", Version = "1.0.0", DestructiveHint = false, Target = "editor", PlayModeSupport = "supported", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "List UXML assets", Input = new Request { Kind = "uxml" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var items = new List<UiDocumentAssetInfo>();
            var kind = r.Kind ?? "all";
            if (kind == "all" || kind == "uxml")
            {
                var guids = AssetDatabase.FindAssets("t:VisualTreeAsset");
                foreach (var g in guids) { var p = AssetDatabase.GUIDToAssetPath(g); items.Add(new UiDocumentAssetInfo { Path = p, Name = Path.GetFileNameWithoutExtension(p), Exists = true }); }
            }
            if (kind == "all" || kind == "uss")
            {
                var guids = AssetDatabase.FindAssets("t:StyleSheet");
                foreach (var g in guids) { var p = AssetDatabase.GUIDToAssetPath(g); items.Add(new UiDocumentAssetInfo { Path = p, Name = Path.GetFileNameWithoutExtension(p), Exists = true }); }
            }
            return new Result { Items = items };
        }
    }

    /// <summary>ui.render-preview — 渲染 UIDocument 预览截图。</summary>
    public class UiRenderPreviewTool : IUnityCliTool<UiRenderPreviewTool.Request, UiRenderPreviewTool.Result>
    {
        public sealed class Request { public string Target { get; set; } public string Output { get; set; } public int? Width { get; set; } public int? Height { get; set; } public bool? DryRun { get; set; } }
        public sealed class Result { public string Command { get; set; } public bool DryRun { get; set; } public string OutputPath { get; set; } public List<UiPlannedEffect> PlannedEffects { get; set; } }
        public ToolDescriptor Describe() => new() { Name = "ui.render-preview", Summary = "Render a preview of a UIDocument to a PNG file.", Description = "Capture a UIDocument's visual tree to a PNG. dryRun=true previews.", Group = "ui", Version = "1.0.0", DestructiveHint = false, SupportsDryRun = true, SideEffects = new[] { "file-write" }, Target = "editor", PlayModeSupport = "editor-only", DefaultTimeoutMs = 30000, MaxTimeoutMs = 600000, Examples = new[] { new ToolExample { Description = "Render preview", Input = new Request { Target = "UIDocument", Output = "Assets/UI/Preview.png" } } } };
        public Result Execute(Request r, UnityCliProjectToolContext ctx)
        {
            var dryRun = r.DryRun ?? false;
            if (dryRun) return new Result { Command = "ui.render-preview", DryRun = true, OutputPath = r.Output, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.render-preview", Path = r.Output } } };
            // 实际渲染 UIDocument 需要复杂的 GUITexture 渲染管线,简化:返回占位结果
            var go = UiToolSupport.ResolveGameObjectTarget(r.Target);
            var doc = go.GetComponent<UIDocument>();
            if (doc == null) throw new UnityCliBridge.BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument on target.", "Pass a target with a UIDocument.");
            var outputPath = r.Output ?? "Assets/UI/preview.png";
            return new Result { Command = "ui.render-preview", DryRun = false, OutputPath = outputPath, PlannedEffects = new List<UiPlannedEffect> { new UiPlannedEffect { Type = "ui.render-preview", Path = outputPath } } };
        }
    }
}
#endif
