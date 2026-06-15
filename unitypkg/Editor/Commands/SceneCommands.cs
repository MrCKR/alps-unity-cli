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
        private static void RegisterSceneCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "scene.hierarchy", "scene", false, SceneHierarchy);
            RegisterCommand(registry, "scene.screenshot", "scene", true, SceneScreenshot);
            RegisterCommand(registry, "screenshot.capture", "scene", true, SceneScreenshot);
        }

        private static string SceneScreenshot(BridgeRequest request)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : Camera.main;
            if (camera == null)
            {
                return ErrorJson("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or main Camera is available for screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            }

            var capture = CaptureCamera(camera, request);
            return SuccessJson(ScreenshotJson(capture, "scene", camera.name));
        }

        private static string SceneHierarchy(BridgeRequest request)
        {
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            var total = 0;
            foreach (var root in roots)
            {
                total += CountHierarchy(root.transform);
            }
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var pageSize = Math.Max(1, Math.Min(request.IntArg("pageSize", 100), 500));
            var items = new List<string>();
            var counter = new Counter();
            foreach (var root in roots)
            {
                if (items.Count >= pageSize)
                {
                    break;
                }
                CollectHierarchy(root.transform, items, cursor, pageSize, counter);
            }
            var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
            return SuccessJson("{\"scene\":{\"name\":" + Quote(SceneManager.GetActiveScene().name) + ",\"path\":" + Quote(SceneManager.GetActiveScene().path) + "},\"items\":[" + string.Join(",", items.ToArray()) + "],\"pageInfo\":{\"pageSize\":" + pageSize.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":" + cursor.ToString(CultureInfo.InvariantCulture) + ",\"nextCursor\":" + Quote(nextCursor) + ",\"total\":" + total.ToString(CultureInfo.InvariantCulture) + "},\"logPath\":" + Quote(LogPath()) + "}");
        }

    }
}
#endif
