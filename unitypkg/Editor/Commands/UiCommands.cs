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
            RegisterCommand(registry, "ui.hierarchy", "ui", false, UiHierarchy);
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
                return ErrorJson("E_UI_DOCUMENT_PATH_REQUIRED", "UIDocument info requires a target or Assets/*.uxml path.", "Pass unity_cli ui document/info <UIDocument target> or --path Assets/UI/Layout.uxml.");
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
