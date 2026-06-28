#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterUiCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "ui.canvas.create", "ui", true, UiCanvasCreate);
            RegisterCommand(registry, "ui.text.create", "ui", true, delegate(BridgeRequest request) { return UiElementCreate(request, "Text"); });
            RegisterCommand(registry, "ui.button.create", "ui", true, delegate(BridgeRequest request) { return UiElementCreate(request, "Button"); });
            RegisterCommand(registry, "ui.image.create", "ui", true, delegate(BridgeRequest request) { return UiElementCreate(request, "Image"); });
            RegisterCommand(registry, "ui.document.info", "ui", false, UiDocumentInfo);
            RegisterCommand(registry, "ui.document.create", "ui", true, UiDocumentCreate);
            RegisterCommand(registry, "ui.document.update", "ui", true, UiDocumentUpdate);
            RegisterCommand(registry, "ui.document.delete", "ui", true, UiDocumentDelete);
            RegisterCommand(registry, "ui.hierarchy", "ui", false, UiHierarchy);
            RegisterCommand(registry, "ui.attach-document", "ui", false, UiAttachDocument);
            RegisterCommand(registry, "ui.detach-document", "ui", false, UiDetachDocument);
            RegisterCommand(registry, "ui.create-panel-settings", "ui", true, UiCreatePanelSettings);
            RegisterCommand(registry, "ui.update-panel-settings", "ui", true, UiUpdatePanelSettings);
            RegisterCommand(registry, "ui.get-visual-tree", "ui", false, UiGetVisualTree);
            RegisterCommand(registry, "ui.modify-visual-element", "ui", false, UiModifyVisualElement);
            RegisterCommand(registry, "ui.link-stylesheet", "ui", true, UiLinkStylesheet);
            RegisterCommand(registry, "ui.list-documents", "ui", false, UiListDocuments);
            RegisterCommand(registry, "ui.render-preview", "ui", true, UiRenderPreview);
        }

        private static string UiCanvasCreate(BridgeRequest request)
        {
            var name = string.IsNullOrEmpty(request.StringArg("name")) ? "Canvas" : request.StringArg("name");
            var canvasObject = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(UiCanvasScaler), typeof(UiGraphicRaycaster));
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasObject.GetComponent<UiCanvasScaler>();
            scaler.uiScaleMode = UiCanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            Selection.activeGameObject = canvasObject;
            WriteLog("ui_canvas_created name=" + name);
            return SuccessJson(UiCreateJson("ui.canvas.create", canvasObject, "Canvas"));
        }

        private static string UiElementCreate(BridgeRequest request, string kind)
        {
            var parentResolution = ResolveUiParent(request.StringArg("parent"));
            if (parentResolution.Ambiguous)
            {
                return TargetAmbiguousErrorJson("UI parent", request.StringArg("parent"), parentResolution, "Pass a hierarchy path or instance id for the intended Canvas/RectTransform parent.");
            }
            var parent = parentResolution.Target == null ? null : parentResolution.Target.transform;
            if (parent == null)
            {
                return ErrorJson("E_UI_PARENT_NOT_FOUND", "UI parent Canvas or RectTransform not found: " + request.StringArg("parent"), "Create a Canvas first or pass --parent with a Canvas/RectTransform hierarchy path, name or instance id.");
            }

            var name = string.IsNullOrEmpty(request.StringArg("name")) ? kind : request.StringArg("name");
            var gameObject = new GameObject(name, typeof(RectTransform));
            gameObject.transform.SetParent(parent.transform, false);
            ApplyRectTransform(gameObject.GetComponent<RectTransform>(), request);

            if (kind == "Text")
            {
                var text = gameObject.AddComponent<UiText>();
                text.text = string.IsNullOrEmpty(request.StringArg("text")) ? name : request.StringArg("text");
                text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                text.fontSize = request.IntArg("fontSize", 24);
                text.color = ParseColor(request.StringArg("color"), Color.black);
            }
            else if (kind == "Button")
            {
                var image = gameObject.AddComponent<UiImage>();
                image.color = ParseColor(request.StringArg("color"), new Color(1f, 1f, 1f, 1f));
                gameObject.AddComponent<UiButton>();
                var labelObject = new GameObject("Text", typeof(RectTransform));
                labelObject.transform.SetParent(gameObject.transform, false);
                StretchRect(labelObject.GetComponent<RectTransform>());
                var label = labelObject.AddComponent<UiText>();
                label.text = string.IsNullOrEmpty(request.StringArg("text")) ? name : request.StringArg("text");
                label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                label.fontSize = request.IntArg("fontSize", 24);
                label.alignment = TextAnchor.MiddleCenter;
                label.color = Color.black;
            }
            else if (kind == "Image")
            {
                var image = gameObject.AddComponent<UiImage>();
                image.color = ParseColor(request.StringArg("color"), Color.white);
                var spritePath = NormalizeAssetPath(request.StringArg("sprite"));
                if (!string.IsNullOrEmpty(spritePath))
                {
                    var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(spritePath);
                    if (sprite == null)
                    {
                        UnityEngine.Object.DestroyImmediate(gameObject);
                        return ErrorJson("E_UI_SPRITE_NOT_FOUND", "Sprite asset not found: " + spritePath, "Pass an Assets/* sprite path or omit --sprite.");
                    }
                    image.sprite = sprite;
                }
            }

            Selection.activeGameObject = gameObject;
            WriteLog("ui_element_created kind=" + kind + " name=" + name + " parent=" + HierarchyPath(parent.gameObject));
            return SuccessJson(UiCreateJson("ui." + kind.ToLowerInvariant() + ".create", gameObject, kind));
        }

        private static string UiDocumentInfo(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var path = NormalizeAssetPath(request.StringArg("path"));
            var uss = NormalizeAssetPath(request.StringArg("uss"));
            if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Ambiguous)
                {
                    return TargetAmbiguousErrorJson("UIDocument", target, resolution, "Pass a hierarchy path or instance id for the intended UIDocument GameObject.");
                }
                var gameObject = resolution.Target;
                var document = gameObject == null ? null : gameObject.GetComponent<UIDocument>();
                if (document == null)
                {
                    return ErrorJson("E_UI_DOCUMENT_NOT_FOUND", "UIDocument target not found: " + target, "Pass a UIDocument GameObject name, hierarchy path, instance id or an Assets/*.uxml path.");
                }
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("document", UiDocumentJson(document))
                    .PropRaw("target", UiGameObjectJson(gameObject, "UIDocument"))
                    .Prop("logPath", LogPath())
                    .EndObject().ToString());
            }

            if (string.IsNullOrEmpty(path))
            {
                return ErrorJson("E_UI_DOCUMENT_PATH_REQUIRED", "UIDocument info requires a target or Assets/*.uxml path.", "Pass a UIDocument GameObject name/path/instance id or an Assets/*.uxml path.");
            }

            var visualTree = LoadVisualTreeAsset(path);
            var styleSheet = string.IsNullOrEmpty(uss) ? null : LoadStyleSheet(uss);
            return SuccessJson(UiDocumentAssetInfoJson(path, visualTree, uss, styleSheet));
        }

        private static string UiDocumentCreate(BridgeRequest request)
        {
            var name = string.IsNullOrEmpty(request.StringArg("name")) ? "UIDocument" : request.StringArg("name");
            var uxmlPath = RequireUxmlPath(request.StringArg("uxml"));
            var ussPath = NormalizeOptionalUssPath(request.StringArg("uss"));
            var overwrite = request.BoolArg("overwrite", false);
            EnsureTextAsset(uxmlPath, DefaultUxml(ussPath), overwrite, "E_UI_UXML_EXISTS");
            if (!string.IsNullOrEmpty(ussPath))
            {
                EnsureTextAsset(ussPath, DefaultUss(), overwrite, "E_UI_USS_EXISTS");
            }

            var panelSettingsPath = NormalizeAssetPath(request.StringArg("panelSettings"));
            PanelSettings panelSettings = null;
            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                if (panelSettings == null)
                {
                    return ErrorJson("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + panelSettingsPath, "Create a PanelSettings asset in Unity or pass a valid --panel-settings Assets/*.asset path.");
                }
            }

            var gameObject = new GameObject(name, typeof(UIDocument));
            var document = gameObject.GetComponent<UIDocument>();
            document.visualTreeAsset = LoadVisualTreeAsset(uxmlPath);
            if (panelSettings != null)
            {
                document.panelSettings = panelSettings;
            }
            Selection.activeGameObject = gameObject;
            WriteLog("ui_document_created name=" + name + " uxml=" + uxmlPath + " uss=" + ussPath);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("document", UiDocumentJson(document))
                .PropRaw("target", UiGameObjectJson(gameObject, "UIDocument"))
                .PropRaw("styleSheet", UiStyleSheetJson(ussPath, string.IsNullOrEmpty(ussPath) ? null : LoadStyleSheet(ussPath)))
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", "ui.document.create").Prop("target", HierarchyPath(gameObject)).Prop("uxml", uxmlPath).Prop("uss", ussPath).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        /// <summary>更新已有 UXML 文本资产，并触发 Unity reimport。</summary>
        private static string UiDocumentUpdate(BridgeRequest request)
        {
            var uxmlPath = RequireExistingUxmlPath(request.StringArg("uxml") ?? request.StringArg("path"));
            var content = request.StringArg("content");
            if (content == null)
            {
                throw new BridgeCommandException("E_UI_DOCUMENT_CONTENT_REQUIRED", "ui.document.update requires content.", "Pass --content/--content-file or structured input content.");
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "ui.document.update").Prop("dryRun", true).Prop("uxml", uxmlPath).Prop("sizeBytes", (long)Encoding.UTF8.GetByteCount(content))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.document.update").Prop("uxml", uxmlPath).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            File.WriteAllText(AssetFullPath(uxmlPath), content, Encoding.UTF8);
            AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("document", UiAssetFieldsJson(uxmlPath, LoadVisualTreeAsset(uxmlPath)))
                .Prop("sizeBytes", (long)Encoding.UTF8.GetByteCount(content))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.document.update").Prop("uxml", uxmlPath).Prop("refresh", true).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>删除 UXML/USS UI Toolkit 文档资产。</summary>
        private static string UiDocumentDelete(BridgeRequest request)
        {
            var rawPath = request.StringArg("uxml") ?? request.StringArg("path");
            var path = RequireExistingUiDocumentPath(rawPath);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "ui.document.delete").Prop("dryRun", true).PropRaw("document", UiAssetFieldsJson(path, AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path)))
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.document.delete").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new BridgeCommandException("E_UI_DOCUMENT_DELETE_FAILED", "Failed to delete UI document asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropObject("document", doc => doc.Prop("path", path).Prop("exists", false))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.document.delete").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>将 UIDocument 挂载到 GameObject。对齐 CoplayDev manage_ui.attach_ui_document。</summary>
        private static string UiAttachDocument(BridgeRequest request)
        {
            EnsureEditorNotBusy("ui.attach-document");
            var target = request.StringArg("target");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null) throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ui.attach-document requires a target GameObject.", "Pass --target <path>.");
            var sourceAsset = request.StringArg("sourceAsset") ?? request.StringArg("uxml");
            var panelSettingsPath = request.StringArg("panelSettings");
            var sortOrder = request.IntArg("sortOrder", 0);
            var uiDoc = go.GetComponent<UIDocument>() ?? go.AddComponent<UIDocument>();
            if (!string.IsNullOrEmpty(sourceAsset))
            {
                var uxml = AssetDatabase.LoadMainAssetAtPath(sourceAsset) as VisualTreeAsset;
                if (uxml != null) uiDoc.visualTreeAsset = uxml;
            }
            if (!string.IsNullOrEmpty(panelSettingsPath))
            {
                var panel = AssetDatabase.LoadMainAssetAtPath(panelSettingsPath) as PanelSettings;
                if (panel != null) uiDoc.panelSettings = panel;
            }
            else if (uiDoc.panelSettings == null)
            {
                // Unity 2022 没有 PanelSettings.GetDefaultOrCreate；创建临时默认设置以保证 UIDocument 可用。
                var defaults = ScriptableObject.CreateInstance<PanelSettings>();
                defaults.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                defaults.referenceResolution = new Vector2Int(1920, 1080);
                uiDoc.panelSettings = defaults;
            }
            uiDoc.sortingOrder = sortOrder;
            EditorSceneManager.MarkSceneDirty(go.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "ui.attach-document")
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("sourceAsset", sourceAsset)
                .Prop("hasPanelSettings", uiDoc.panelSettings != null)
                .Prop("sortOrder", (long)sortOrder)
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>从 GameObject 移除 UIDocument 组件，只影响当前场景会话。</summary>
        private static string UiDetachDocument(BridgeRequest request)
        {
            EnsureEditorNotBusy("ui.detach-document");
            var target = request.StringArg("target");
            var resolution = ResolveGameObjectTarget(target);
            if (resolution.Ambiguous)
            {
                return TargetAmbiguousErrorJson("UIDocument", target, resolution, "Pass a hierarchy path or instance id for the intended UIDocument GameObject.");
            }
            var go = resolution.Target;
            if (go == null) throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ui.detach-document requires a target GameObject.", "Pass --target <path>.");
            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null) throw new BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument component found on: " + target, "Choose a GameObject with UIDocument or call ui.attach-document first.");
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.detach-document").Prop("dryRun", true).PropRaw("target", GameObjectTargetJson(go)).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.detach-document").Prop("target", HierarchyPath(go)).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            Undo.DestroyObjectImmediate(uiDoc);
            EditorSceneManager.MarkSceneDirty(go.scene);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "ui.detach-document")
                .PropRaw("target", GameObjectTargetJson(go))
                .Prop("removed", true)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.detach-document").Prop("target", HierarchyPath(go)).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>创建 PanelSettings 资产。对齐 CoplayDev manage_ui.create_panel_settings。</summary>
        private static string UiCreatePanelSettings(BridgeRequest request)
        {
            EnsureEditorNotBusy("ui.create-panel-settings");
            var path = request.StringArg("path");
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new BridgeCommandException("E_PANEL_PATH_REQUIRED", "ui.create-panel-settings requires an Assets/*.asset path.", "Pass Assets/UI/Panel.asset.");
            var scaleMode = request.StringArg("scaleMode") ?? "ScaleWithScreenSize";
            var refW = request.IntArg("referenceWidth", 1920);
            var refH = request.IntArg("referenceHeight", 1080);
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            panel.scaleMode = (PanelScaleMode)Enum.Parse(typeof(PanelScaleMode), scaleMode, true);
            panel.referenceResolution = new Vector2Int(refW, refH);
            ApplyOptionalPanelSettings(panel, request);
            CreateAssetDirectory(path);
            AssetDatabase.CreateAsset(panel, path);
            AssetDatabase.Refresh();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "ui.create-panel-settings")
                .Prop("path", path).Prop("scaleMode", scaleMode)
                .Prop("referenceResolution", refW + "x" + refH)
                .PropRaw("panelSettings", PanelSettingsJson(path, panel))
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>更新已有 PanelSettings 资产的缩放配置。</summary>
        private static string UiUpdatePanelSettings(BridgeRequest request)
        {
            EnsureEditorNotBusy("ui.update-panel-settings");
            var path = RequirePanelSettingsPath(request.StringArg("path"));
            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (panel == null) throw new BridgeCommandException("E_UI_PANEL_SETTINGS_NOT_FOUND", "PanelSettings asset not found: " + path, "Create it with ui.create-panel-settings or pass an existing PanelSettings asset path.");
            var scaleMode = request.StringArg("scaleMode");
            var refW = request.IntArg("referenceWidth", panel.referenceResolution.x);
            var refH = request.IntArg("referenceHeight", panel.referenceResolution.y);
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.update-panel-settings").Prop("dryRun", true).Prop("path", path).Prop("scaleMode", string.IsNullOrEmpty(scaleMode) ? panel.scaleMode.ToString() : scaleMode).Prop("referenceResolution", refW + "x" + refH).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.update-panel-settings").Prop("path", path).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (!string.IsNullOrEmpty(scaleMode)) panel.scaleMode = (PanelScaleMode)Enum.Parse(typeof(PanelScaleMode), scaleMode, true);
            panel.referenceResolution = new Vector2Int(refW, refH);
            ApplyOptionalPanelSettings(panel, request);
            EditorUtility.SetDirty(panel);
            AssetDatabase.SaveAssets();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "ui.update-panel-settings")
                .Prop("path", path).Prop("scaleMode", panel.scaleMode.ToString()).Prop("referenceResolution", panel.referenceResolution.x + "x" + panel.referenceResolution.y)
                .PropRaw("panelSettings", PanelSettingsJson(path, panel))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.update-panel-settings").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>修改 UIDocument 当前 visual tree 中的元素属性/classes/styles，不写回 UXML asset。</summary>
        private static string UiModifyVisualElement(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var elementName = request.StringArg("elementName") ?? request.StringArg("name");
            if (string.IsNullOrEmpty(elementName)) throw new BridgeCommandException("E_UI_ELEMENT_NAME_REQUIRED", "ui.modify-visual-element requires an elementName.", "Pass the VisualElement name from ui.get-visual-tree.");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null) throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ui.modify-visual-element requires a UIDocument GameObject target.", "Pass --target <UIDocument GameObject>.");
            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null || uiDoc.rootVisualElement == null) throw new BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument with rootVisualElement on target: " + target, "Attach a UIDocument first or wait until its panel is initialized.");
            var element = FindVisualElementByName(uiDoc.rootVisualElement, elementName);
            if (element == null) throw new BridgeCommandException("E_UI_ELEMENT_NOT_FOUND", "VisualElement not found: " + elementName, "Run ui.get-visual-tree and pass an existing element name.");
            var addClass = request.StringArg("addClass");
            var removeClass = request.StringArg("removeClass");
            var text = request.StringArg("text");
            var display = request.StringArg("display");
            var width = request.OptionalFloatArg("width");
            var height = request.OptionalFloatArg("height");
            var backgroundColor = request.StringArg("backgroundColor") ?? request.StringArg("color");
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.modify-visual-element").Prop("dryRun", true).PropRaw("target", GameObjectTargetJson(go)).PropRaw("element", VisualElementSummaryJson(element)).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.modify-visual-element").Prop("target", HierarchyPath(go)).Prop("elementName", elementName).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (!string.IsNullOrEmpty(addClass)) element.AddToClassList(addClass);
            if (!string.IsNullOrEmpty(removeClass)) element.RemoveFromClassList(removeClass);
            if (text != null)
            {
                var textElement = element as TextElement;
                if (textElement == null) throw new BridgeCommandException("E_UI_ELEMENT_TEXT_UNSUPPORTED", "Target VisualElement does not support text: " + elementName, "Choose a Label/TextElement or omit text.");
                textElement.text = text;
            }
            if (!string.IsNullOrEmpty(display)) element.style.display = string.Equals(display, "none", StringComparison.OrdinalIgnoreCase) ? DisplayStyle.None : DisplayStyle.Flex;
            if (width.HasValue) element.style.width = width.Value;
            if (height.HasValue) element.style.height = height.Value;
            if (!string.IsNullOrEmpty(backgroundColor)) element.style.backgroundColor = ParseColor(backgroundColor, Color.clear);
            element.MarkDirtyRepaint();
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "ui.modify-visual-element")
                .PropRaw("target", GameObjectTargetJson(go))
                .PropRaw("element", VisualElementSummaryJson(element))
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.modify-visual-element").Prop("target", HierarchyPath(go)).Prop("elementName", elementName).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>检查 UIDocument 的 visual tree。对齐 CoplayDev manage_ui.get_visual_tree。</summary>
        private static string UiGetVisualTree(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var go = string.IsNullOrEmpty(target) ? null : ObjectRefResolver.FindGameObjectByPath(target);
            if (go == null) throw new BridgeCommandException("E_TARGET_NOT_FOUND", "ui.get-visual-tree requires a target GameObject.", "Pass --target <path>.");
            var uiDoc = go.GetComponent<UIDocument>();
            if (uiDoc == null || uiDoc.rootVisualElement == null)
                return ErrorJson("E_UI_DOCUMENT_NOT_FOUND", "No UIDocument with a root visual element on " + go.name, "Attach a UIDocument first via ui.attach-document.");
            var maxDepth = Math.Max(1, Math.Min(request.IntArg("maxDepth", 10), 50));
            var items = new List<string>();
            CollectVisualElement(uiDoc.rootVisualElement, 0, maxDepth, items);
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "ui.get-visual-tree");
            w.PropRaw("target", GameObjectTargetJson(go));
            w.Prop("count", (long)items.Count);
            w.PropArray("elements", arr => { foreach (var i in items) arr.RawValue(i); });
            w.Prop("logPath", LogPath());
            return SuccessJson(w.EndObject().ToString());
        }

        private static void CollectVisualElement(VisualElement ve, int depth, int maxDepth, List<string> items)
        {
            if (ve == null || depth > maxDepth) return;
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("name", ve.name).Prop("type", ve.GetType().Name).Prop("depth", (long)depth);
            w.PropArray("classes", arr => { foreach (var c in ve.GetClasses()) arr.Value(c); });
            w.Prop("childCount", (long)ve.childCount);
            w.EndObject();
            items.Add(w.ToString());
            for (var i = 0; i < ve.childCount; i++) CollectVisualElement(ve[i], depth + 1, maxDepth, items);
        }

        private static string UiHierarchy(BridgeRequest request)
        {
            var pageSize = Math.Max(1, request.IntArg("pageSize", 100));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var target = request.StringArg("target");
            var roots = new List<GameObject>();
            if (!string.IsNullOrEmpty(target))
            {
                var resolution = ResolveGameObjectTarget(target);
                if (resolution.Ambiguous)
                {
                    return TargetAmbiguousErrorJson("UI hierarchy", target, resolution, "Pass a hierarchy path or instance id for the intended UI hierarchy root.");
                }
                var targetObject = resolution.Target;
                if (targetObject == null)
                {
                    return ErrorJson("E_TARGET_NOT_FOUND", "UI hierarchy target not found: " + target, "Pass a Canvas, UIDocument, RectTransform or UI GameObject target.");
                }
                roots.Add(targetObject);
            }
            else
            {
                foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    roots.Add(canvas.gameObject);
                }
                foreach (var document in UnityEngine.Object.FindObjectsOfType<UIDocument>())
                {
                    if (!roots.Contains(document.gameObject))
                    {
                        roots.Add(document.gameObject);
                    }
                }
            }

            var summaries = new List<string>();
            foreach (var root in roots)
            {
                CollectUiHierarchy(root.transform, summaries);
            }
            var items = new List<string>();
            for (var index = cursor; index < summaries.Count && items.Count < pageSize; index++)
            {
                items.Add(summaries[index]);
            }
            var nextCursor = cursor + items.Count < summaries.Count ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropArray("items", arr =>
                {
                    foreach (var item in items)
                    {
                        arr.RawValue(item);
                    }
                })
                .PropObject("pageInfo", pg =>
                {
                    pg.Prop("pageSize", (long)pageSize).Prop("cursor", (long)cursor).Prop("nextCursor", nextCursor).Prop("total", (long)summaries.Count);
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static TargetResolution ResolveUiParent(string target)
        {
            if (!string.IsNullOrEmpty(target))
            {
                return ResolveGameObjectTarget(target);
            }
            var canvas = UnityEngine.Object.FindObjectOfType<Canvas>();
            return TargetResolution.Single(canvas == null ? null : canvas.gameObject);
        }

        private static void ApplyRectTransform(RectTransform rectTransform, BridgeRequest request)
        {
            rectTransform.sizeDelta = new Vector2(Math.Max(1, request.IntArg("width", 160)), Math.Max(1, request.IntArg("height", 40)));
            rectTransform.anchoredPosition = new Vector2(request.IntArg("x", 0), request.IntArg("y", 0));
        }

        private static void StretchRect(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
        }

        private static Color ParseColor(string value, Color fallback)
        {
            if (string.IsNullOrEmpty(value))
            {
                return fallback;
            }
            Color color;
            return ColorUtility.TryParseHtmlString(value, out color) ? color : fallback;
        }

        private static string UiCreateJson(string command, GameObject gameObject, string kind)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("created", UiGameObjectJson(gameObject, kind))
                .PropRaw("rectTransform", RectTransformJson(gameObject.GetComponent<RectTransform>()))
                .PropArray("plannedEffects", arr =>
                {
                    arr.BeginObject().Prop("type", command).Prop("target", HierarchyPath(gameObject)).EndObject();
                })
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static string UiGameObjectJson(GameObject gameObject, string kind)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("id", (long)gameObject.GetInstanceID())
                .Prop("name", gameObject.name)
                .Prop("path", HierarchyPath(gameObject))
                .Prop("kind", kind)
                .Prop("active", gameObject.activeSelf)
                .PropRaw("components", ComponentNamesJson(gameObject))
                .EndObject().ToString();
        }

        private static string RectTransformJson(RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return "null";
            }
            return new UnityCliJsonWriter().BeginObject()
                .Prop("width", (double)rectTransform.rect.width)
                .Prop("height", (double)rectTransform.rect.height)
                .Prop("x", (double)rectTransform.anchoredPosition.x)
                .Prop("y", (double)rectTransform.anchoredPosition.y)
                .EndObject().ToString();
        }

        /// <summary>把 USS 链接写入 UXML 文件，避免 AI 手写 project://database 路径格式。</summary>
        private static string UiLinkStylesheet(BridgeRequest request)
        {
            var uxmlPath = RequireExistingUxmlPath(request.StringArg("uxml") ?? request.StringArg("path"));
            var ussPath = NormalizeOptionalUssPath(request.StringArg("uss") ?? request.StringArg("styleSheet"));
            if (string.IsNullOrEmpty(ussPath)) throw new BridgeCommandException("E_UI_USS_PATH_REQUIRED", "ui.link-stylesheet requires a USS path.", "Pass --uss Assets/UI/Styles.uss.");
            LoadStyleSheet(ussPath);
            var fullPath = AssetFullPath(uxmlPath);
            var content = File.ReadAllText(fullPath, Encoding.UTF8);
            var styleLine = "  <Style src=\"project://database/" + ussPath + "\" />";
            var changed = !content.Contains(ussPath);
            if (changed)
            {
                var insertAt = content.IndexOf('>');
                content = insertAt >= 0 ? content.Insert(insertAt + 1, "\n" + styleLine) : styleLine + "\n" + content;
            }
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.link-stylesheet").Prop("dryRun", true).Prop("uxml", uxmlPath).Prop("uss", ussPath).Prop("changed", changed).PropArray("plannedEffects", arr => { if (changed) arr.BeginObject().Prop("type", "ui.link-stylesheet").Prop("uxml", uxmlPath).Prop("uss", ussPath).EndObject(); }).Prop("logPath", LogPath()).EndObject().ToString());
            }
            if (changed)
            {
                File.WriteAllText(fullPath, content, Encoding.UTF8);
                AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.SaveAssets();
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("document", UiAssetFieldsJson(uxmlPath, LoadVisualTreeAsset(uxmlPath)))
                .PropRaw("styleSheet", UiStyleSheetJson(ussPath, LoadStyleSheet(ussPath)))
                .Prop("changed", changed)
                .PropArray("plannedEffects", arr => { if (changed) arr.BeginObject().Prop("type", "ui.link-stylesheet").Prop("uxml", uxmlPath).Prop("uss", ussPath).EndObject(); })
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>列出 UI Toolkit 相关资产，供 AI 发现 UXML/USS/PanelSettings。</summary>
        private static string UiListDocuments(BridgeRequest request)
        {
            var kind = request.StringArg("kind") ?? "all";
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var items = new List<string>();
            if (string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "uxml", StringComparison.OrdinalIgnoreCase)) CollectUiAssets("t:VisualTreeAsset", "uxml", pageSize, items);
            if (items.Count < pageSize && (string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "uss", StringComparison.OrdinalIgnoreCase))) CollectUiAssets("t:StyleSheet", "uss", pageSize, items);
            if (items.Count < pageSize && (string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(kind, "panelSettings", StringComparison.OrdinalIgnoreCase))) CollectUiAssets("t:PanelSettings", "panelSettings", pageSize, items);
            return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.list-documents").Prop("kind", kind).Prop("count", (long)items.Count).PropArray("items", arr => { foreach (var item in items) arr.RawValue(item); }).Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>为 UI Toolkit asset 生成 AssetPreview PNG；无法生成时返回稳定错误。</summary>
        private static string UiRenderPreview(BridgeRequest request)
        {
            var target = request.StringArg("target") ?? request.StringArg("uxml") ?? request.StringArg("path");
            var output = NormalizeAssetPath(request.StringArg("output"));
            if (string.IsNullOrEmpty(output)) output = "Assets/UnityCliArtifacts/UI/ui-preview.png";
            if (!output.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !output.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new BridgeCommandException("E_UI_PREVIEW_OUTPUT_INVALID", "ui.render-preview output must be an Assets/*.png path.", "Pass --output Assets/UnityCliArtifacts/UI/preview.png.");
            var assetPath = NormalizeAssetPath(target);
            UnityEngine.Object previewTarget = null;
            if (!string.IsNullOrEmpty(assetPath)) previewTarget = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (previewTarget == null && !string.IsNullOrEmpty(target))
            {
                var go = ObjectRefResolver.FindGameObjectByPath(target);
                var doc = go == null ? null : go.GetComponent<UIDocument>();
                previewTarget = doc == null ? null : doc.visualTreeAsset;
                assetPath = previewTarget == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(previewTarget));
            }
            if (previewTarget == null) throw new BridgeCommandException("E_UI_PREVIEW_TARGET_NOT_FOUND", "UI preview target not found: " + target, "Pass an Assets/*.uxml path or a UIDocument GameObject.");
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.render-preview").Prop("dryRun", true).Prop("target", target).Prop("output", output).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.render-preview").Prop("output", output).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            var texture = UnityEditor.AssetPreview.GetAssetPreview(previewTarget) ?? UnityEditor.AssetPreview.GetMiniThumbnail(previewTarget);
            if (texture == null) throw new BridgeCommandException("E_UI_PREVIEW_UNAVAILABLE", "Unity did not provide an AssetPreview for: " + target, "Open/import the UI asset in Unity or retry after editor is idle.", true, 1000);
            CreateAssetDirectory(output);
            var temp = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
            try
            {
                temp.SetPixels(texture.GetPixels());
                temp.Apply();
                var bytes = temp.EncodeToPNG();
                File.WriteAllBytes(AssetFullPath(output), bytes);
                AssetDatabase.ImportAsset(output, ImportAssetOptions.ForceUpdate);
                return SuccessJson(new UnityCliJsonWriter().BeginObject().Prop("command", "ui.render-preview").Prop("target", target).Prop("sourceAsset", assetPath).Prop("output", output).Prop("width", (long)temp.width).Prop("height", (long)temp.height).Prop("sizeBytes", (long)bytes.Length).PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", "ui.render-preview").Prop("output", output).EndObject()).Prop("logPath", LogPath()).EndObject().ToString());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(temp);
            }
        }

        private static void CollectUiAssets(string filter, string kind, int pageSize, List<string> items)
        {
            foreach (var guid in AssetDatabase.FindAssets(filter))
            {
                if (items.Count >= pageSize) return;
                var path = NormalizeAssetPath(AssetDatabase.GUIDToAssetPath(guid));
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                items.Add(new UnityCliJsonWriter().BeginObject().Prop("kind", kind).Prop("path", path).Prop("guid", guid).Prop("type", asset == null ? null : asset.GetType().Name).EndObject().ToString());
            }
        }

        private static void ApplyOptionalPanelSettings(PanelSettings panel, BridgeRequest request)
        {
            SetOptionalIntProperty(panel, "sortingOrder", request.OptionalIntArg("sortingOrder") ?? request.OptionalIntArg("sortOrder"));
            SetOptionalStringProperty(panel, "sortingLayerName", request.StringArg("sortingLayerName"));
            SetOptionalFloatProperty(panel, "match", request.OptionalFloatArg("match"));
            SetOptionalEnumProperty(panel, "screenMatchMode", request.StringArg("screenMatchMode"));
            SetOptionalColorProperty(panel, "clearColor", request.StringArg("clearColor"));
            var targetTexturePath = NormalizeAssetPath(request.StringArg("targetTexture"));
            if (!string.IsNullOrEmpty(targetTexturePath))
            {
                var renderTexture = AssetDatabase.LoadAssetAtPath<RenderTexture>(targetTexturePath);
                if (renderTexture == null) throw new BridgeCommandException("E_UI_TARGET_TEXTURE_NOT_FOUND", "RenderTexture asset not found: " + targetTexturePath, "Pass an existing RenderTexture asset path or omit targetTexture.");
                SetOptionalObjectProperty(panel, "targetTexture", renderTexture);
            }
        }

        private static string PanelSettingsJson(string path, PanelSettings panel)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("scaleMode", panel.scaleMode.ToString())
                .Prop("referenceResolution", panel.referenceResolution.x + "x" + panel.referenceResolution.y)
                .PropRaw("optional", new UnityCliJsonWriter().BeginObject()
                    .Prop("sortingOrder", ReflectionPropertyString(panel, "sortingOrder") ?? ReflectionPropertyString(panel, "sortOrder"))
                    .Prop("sortingLayerName", ReflectionPropertyString(panel, "sortingLayerName"))
                    .Prop("screenMatchMode", ReflectionPropertyString(panel, "screenMatchMode"))
                    .Prop("match", ReflectionPropertyString(panel, "match"))
                    .Prop("targetTexture", ReflectionObjectAssetPath(panel, "targetTexture"))
                    .EndObject().ToString())
                .EndObject().ToString();
        }

        private static void SetOptionalIntProperty(object target, string name, int? value)
        {
            if (!value.HasValue) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType == typeof(int)) property.SetValue(target, value.Value, null);
        }

        private static void SetOptionalFloatProperty(object target, string name, float? value)
        {
            if (!value.HasValue) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType == typeof(float)) property.SetValue(target, value.Value, null);
        }

        private static void SetOptionalStringProperty(object target, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType == typeof(string)) property.SetValue(target, value, null);
        }

        private static void SetOptionalObjectProperty(object target, string name, UnityEngine.Object value)
        {
            if (value == null) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType.IsAssignableFrom(value.GetType())) property.SetValue(target, value, null);
        }

        private static void SetOptionalEnumProperty(object target, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType.IsEnum) property.SetValue(target, Enum.Parse(property.PropertyType, value, true), null);
        }

        private static void SetOptionalColorProperty(object target, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite && property.PropertyType == typeof(Color)) property.SetValue(target, ParseColor(value, Color.clear), null);
        }

        private static string ReflectionPropertyString(object target, string name)
        {
            if (target == null) return null;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanRead) return null;
            var value = property.GetValue(target, null);
            return value == null ? null : value.ToString();
        }

        private static string ReflectionObjectAssetPath(object target, string name)
        {
            if (target == null) return null;
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanRead) return null;
            var value = property.GetValue(target, null) as UnityEngine.Object;
            return value == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(value));
        }

        private static VisualElement FindVisualElementByName(VisualElement root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindVisualElementByName(root[i], name);
                if (found != null) return found;
            }
            return null;
        }

        private static string VisualElementSummaryJson(VisualElement element)
        {
            return new UnityCliJsonWriter().BeginObject()
                .Prop("name", element.name)
                .Prop("type", element.GetType().Name)
                .PropArray("classes", arr => { foreach (var c in element.GetClasses()) arr.Value(c); })
                .Prop("childCount", (long)element.childCount)
                .EndObject().ToString();
        }

        private static string ComponentNamesJson(GameObject gameObject)
        {
            var w = new UnityCliJsonWriter().BeginArray();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                w.Value(component != null ? component.GetType().Name : "MissingScript");
            }
            return w.EndArray().ToString();
        }

        private static string UiDocumentAssetInfoJson(string path, VisualTreeAsset visualTree, string uss, StyleSheet styleSheet)
        {
            return new UnityCliJsonWriter().BeginObject()
                .PropObject("document", doc =>
                {
                    WriteAssetFields(doc, path, visualTree);
                    doc.PropRaw("styleSheet", UiStyleSheetJson(uss, styleSheet));
                })
                .PropRaw("diagnostics", UiDocumentReadDiagnosticsJson())
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static string UiDocumentReadDiagnosticsJson()
        {
            return new UnityCliJsonWriter().BeginArray()
                .BeginObject()
                    .Prop("level", "info")
                    .Prop("code", "UI_DOCUMENT_READ_OK")
                    .Prop("message", "UI Toolkit assets are readable.")
                .EndObject()
                .EndArray().ToString();
        }

        private static string UiDocumentJson(UIDocument document)
        {
            var visualTreePath = document.visualTreeAsset == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(document.visualTreeAsset));
            var panelSettingsPath = document.panelSettings == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(document.panelSettings));
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("visualTreeAsset", UiAssetReferenceJson(visualTreePath, document.visualTreeAsset))
                .PropRaw("panelSettings", UiAssetReferenceJson(panelSettingsPath, document.panelSettings))
                .PropObject("rootVisualElement", w => w.Prop("name", document.rootVisualElement == null ? null : document.rootVisualElement.name))
                .EndObject().ToString();
        }

        private static void WriteAssetFields(UnityCliJsonWriter w, string path, UnityEngine.Object asset)
        {
            w.Prop("path", path)
             .Prop("guid", AssetDatabase.AssetPathToGUID(path))
             .Prop("type", asset == null ? null : asset.GetType().Name)
             .Prop("exists", asset != null);
        }

        private static string UiAssetFieldsJson(string path, UnityEngine.Object asset)
        {
            var w = new UnityCliJsonWriter().BeginObject();
            WriteAssetFields(w, path, asset);
            return w.EndObject().ToString();
        }

        private static string UiAssetReferenceJson(string path, UnityEngine.Object asset)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "null";
            }
            return UiAssetFieldsJson(path, asset);
        }

        private static string UiStyleSheetJson(string path, StyleSheet styleSheet)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "null";
            }
            return UiAssetFieldsJson(path, styleSheet);
        }

        private static VisualTreeAsset LoadVisualTreeAsset(string path)
        {
            path = RequireUxmlPath(path);
            var asset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_UI_UXML_NOT_FOUND", "UXML asset not found: " + path, "Pass an existing Assets/*.uxml path or run ui document/create with --overwrite when appropriate.");
            }
            return asset;
        }

        private static StyleSheet LoadStyleSheet(string path)
        {
            path = NormalizeOptionalUssPath(path);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            var asset = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_UI_USS_NOT_FOUND", "USS asset not found: " + path, "Pass an existing Assets/*.uss path or create it with ui document/create.");
            }
            return asset;
        }

        private static string RequireExistingUxmlPath(string rawPath)
        {
            var path = RequireUxmlPath(rawPath);
            if (AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(path) == null)
            {
                throw new BridgeCommandException("E_UI_UXML_NOT_FOUND", "UXML asset not found: " + path, "Pass an existing Assets/*.uxml path or create it with ui.document.create.");
            }
            return path;
        }

        private static string RequireExistingUiDocumentPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path)) throw new BridgeCommandException("E_UI_DOCUMENT_PATH_REQUIRED", "Pass an Assets/*.uxml or Assets/*.uss path.", "Use --uxml/--path with an existing UI Toolkit asset.");
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || (!path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BridgeCommandException("E_UI_DOCUMENT_PATH_INVALID", "UI document path must end with .uxml or .uss: " + path, "Pass a Unity asset path such as Assets/UI/Layout.uxml.");
            }
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) == null)
            {
                throw new BridgeCommandException("E_UI_DOCUMENT_NOT_FOUND", "UI document asset not found: " + path, "Pass an existing UXML/USS path or create it first.");
            }
            return path;
        }

        private static string RequirePanelSettingsPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PANEL_PATH_REQUIRED", "PanelSettings path must be an Assets/*.asset path.", "Pass Assets/UI/Panel.asset.");
            }
            return path;
        }

        private static string RequireUxmlPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_UI_UXML_PATH_REQUIRED", "Pass an Assets/*.uxml path.", "Use --uxml Assets/UI/Layout.uxml or --path Assets/UI/Layout.uxml.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_UI_UXML_PATH_INVALID", "UXML path must be under Assets/ and end with .uxml: " + path, "Pass a Unity asset path such as Assets/UI/Layout.uxml.");
            }
            return path;
        }

        private static string NormalizeOptionalUssPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_UI_USS_PATH_INVALID", "USS path must be under Assets/ and end with .uss: " + path, "Pass a Unity asset path such as Assets/UI/Styles.uss.");
            }
            return path;
        }

        private static void EnsureTextAsset(string assetPath, string contents, bool overwrite, string existsCode)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath) != null && !overwrite)
            {
                throw new BridgeCommandException(existsCode, "UI asset already exists: " + assetPath, "Pass --overwrite or choose a different Assets path.");
            }
            CreateAssetDirectory(assetPath);
            File.WriteAllText(Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar)), contents, Encoding.UTF8);
            AssetDatabase.ImportAsset(assetPath);
            AssetDatabase.SaveAssets();
        }

        private static string DefaultUxml(string ussPath)
        {
            var style = string.IsNullOrEmpty(ussPath) ? string.Empty : "  <Style src=\"project://database/" + ussPath + "\" />\n";
            return "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\">\n" + style + "  <ui:VisualElement name=\"Root\" />\n</ui:UXML>\n";
        }

        private static string DefaultUss()
        {
            return "#Root { flex-grow: 1; }\n";
        }

        private static void CollectUiHierarchy(Transform transform, List<string> items)
        {
            var gameObject = transform.gameObject;
            if (gameObject.GetComponent<Canvas>() != null || gameObject.GetComponent<RectTransform>() != null || gameObject.GetComponent<UIDocument>() != null)
            {
                var kind = gameObject.GetComponent<UIDocument>() != null ? "UIDocument" : gameObject.GetComponent<Canvas>() != null ? "Canvas" : "RectTransform";
                var documentComponent = gameObject.GetComponent<UIDocument>();
                items.Add(new UnityCliJsonWriter().BeginObject()
                    .PropRaw("item", UiGameObjectJson(gameObject, kind))
                    .PropRaw("rectTransform", RectTransformJson(gameObject.GetComponent<RectTransform>()))
                    .PropRaw("document", documentComponent == null ? "null" : UiDocumentJson(documentComponent))
                    .EndObject().ToString());
            }
            foreach (Transform child in transform)
            {
                CollectUiHierarchy(child, items);
            }
        }

        private static Component FindUiGraphic(GameObject gameObject)
        {
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }
                var type = component.GetType();
                while (type != null)
                {
                    if (type.FullName == "UnityEngine.UI.Graphic")
                    {
                        return component;
                    }
                    type = type.BaseType;
                }
            }
            return null;
        }
    }
}
#endif
