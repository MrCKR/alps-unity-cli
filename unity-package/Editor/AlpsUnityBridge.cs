#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AlpsUnityCli
{
    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const int Port = 47392;
        private static readonly object QueueLock = new object();
        private static readonly string[] CameraCommands = { "camera.list", "camera.info", "camera.screenshot" };
        private static readonly string[] SceneCommands = { "scene.screenshot" };
        private static readonly string[] ScreenshotCommands = { "screenshot.capture" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.create", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close", "prefab.hierarchy" };
        private static readonly Queue<BridgeWorkItem> WorkQueue = new Queue<BridgeWorkItem>();
        private static HttpListener listener;
        private static bool started;

        static AlpsUnityBridge()
        {
            EditorApplication.update += ProcessQueuedWork;
            StartListener();
        }

        private static void StartListener()
        {
            if (started)
            {
                return;
            }

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                listener.Start();
                started = true;
                Task.Run(ListenLoop);
                WriteLog("bridge_started port=" + Port);
            }
            catch (Exception exception)
            {
                WriteLog("bridge_start_failed " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static async Task ListenLoop()
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    var context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleHttp(context));
                }
                catch (Exception exception)
                {
                    WriteLog("bridge_listen_failed " + exception.GetType().Name + ": " + exception.Message);
                }
            }
        }

        private static void HandleHttp(HttpListenerContext context)
        {
            if (context.Request.HttpMethod == "GET" && context.Request.Url.AbsolutePath == "/health")
            {
                WriteJson(context, "{\"ok\":true,\"data\":{\"bridge\":\"alps-unity-cli\",\"port\":" + Port + ",\"route\":\"POST /bridge/execute\",\"capabilities\":" + CapabilitiesJson() + "}}", 200);
                return;
            }

            if (context.Request.HttpMethod != "POST" || context.Request.Url.AbsolutePath != "/bridge/execute")
            {
                WriteJson(context, "{\"ok\":false,\"error\":{\"code\":\"E_BRIDGE_ROUTE_NOT_FOUND\",\"message\":\"Expected POST /bridge/execute.\"}}", 404);
                return;
            }

            var body = ReadBody(context.Request);
            var request = BridgeRequest.Parse(body);
            var item = new BridgeWorkItem(request);
            lock (QueueLock)
            {
                WorkQueue.Enqueue(item);
            }

            var timeout = TimeSpan.FromSeconds(60);
            if (!item.Completed.Wait(timeout))
            {
                if (item.CancelBeforeStart())
                {
                    WriteLog("bridge_request_timeout_cancelled requestId=" + request.RequestId + " command=" + request.Command);
                    WriteJson(context, ErrorJson("E_UNITY_BUSY", "Unity Editor did not start the bridge request within 60 seconds; the request was cancelled before execution.", "No Unity side effects were applied. Wait for Unity to finish compiling/importing and retry."), 200);
                    return;
                }

                if (item.Completed.IsSet)
                {
                    WriteJson(context, item.Response ?? ErrorJson("E_UNITY_BUSY", "Unity Editor did not finish the bridge request within 60 seconds.", "Wait for Unity to finish compiling/importing and retry."), 200);
                    return;
                }

                item.MarkTimedOut();
                WriteLog("bridge_request_timeout_started requestId=" + request.RequestId + " command=" + request.Command + " mutating=" + IsMutatingCommand(request.Command));
                if (IsMutatingCommand(request.Command))
                {
                    WriteJson(context, SuccessJson("{\"status\":\"running\",\"requestId\":" + Quote(request.RequestId) + ",\"command\":" + Quote(request.Command) + ",\"timeoutSeconds\":60,\"sideEffectSemantics\":\"started-before-http-timeout\",\"message\":\"Unity started this mutating request before the HTTP wait timeout; it may still complete. This is not a failure envelope. Inspect logPath and requestId before retrying.\",\"logPath\":" + Quote(LogPath()) + "}"), 200);
                    return;
                }

                WriteJson(context, ErrorJson("E_UNITY_BUSY", "Unity Editor started the bridge request but did not finish within 60 seconds.", "Wait for Unity to finish compiling/importing and retry."), 200);
                return;
            }

            WriteJson(context, item.Response ?? ErrorJson("E_UNITY_BUSY", "Unity Editor did not finish the bridge request within 60 seconds.", "Wait for Unity to finish compiling/importing and retry."), 200);
        }

        private static void ProcessQueuedWork()
        {
            while (true)
            {
                BridgeWorkItem item;
                lock (QueueLock)
                {
                    if (WorkQueue.Count == 0)
                    {
                        return;
                    }
                    item = WorkQueue.Dequeue();
                }

                if (!item.TryStart())
                {
                    WriteLog("bridge_request_skipped_expired requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    item.Response = item.Response ?? ErrorJson("E_BRIDGE_REQUEST_EXPIRED", "Bridge request expired before Unity started executing it.", "Retry after Unity is no longer busy.");
                    item.Completed.Set();
                    continue;
                }

                try
                {
                    item.Response = Execute(item.Request);
                }
                catch (BridgeCommandException exception)
                {
                    item.Response = ErrorJson(exception.Code, exception.Message, exception.NextAction ?? "Check output path, permissions, format parameters and AlpsUnityBridge.log.");
                }
                catch (Exception exception)
                {
                    item.Response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and AlpsUnityBridge.log.");
                }
                finally
                {
                    if (item.TimedOut)
                    {
                        WriteLog("bridge_request_completed_after_http_timeout requestId=" + item.Request.RequestId + " command=" + item.Request.Command);
                    }
                    item.Completed.Set();
                }
            }
        }

        private static string Execute(BridgeRequest request)
        {
            WriteLog("execute requestId=" + request.RequestId + " command=" + request.Command);

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return ErrorJson("E_UNITY_BUSY", "Unity Editor is compiling or importing assets.", "Wait for Unity to finish compiling/importing and retry.");
            }

            switch (request.Command)
            {
                case "camera.list":
                    return SuccessJson(CameraListJson());
                case "camera.info":
                    return CameraInfo(request);
                case "camera.screenshot":
                    return CameraScreenshot(request);
                case "scene.screenshot":
                case "screenshot.capture":
                    return SceneScreenshot(request);
                case "prefab.info":
                    return PrefabInfo(request);
                case "prefab.create":
                    return PrefabCreate(request);
                case "prefab.stage-open":
                    return PrefabStageOpen(request);
                case "prefab.stage-save":
                    return PrefabStageSave(request);
                case "prefab.stage-close":
                    return PrefabStageClose(request);
                case "prefab.hierarchy":
                    return PrefabHierarchy(request);
                default:
                    return ErrorJson("E_BRIDGE_COMMAND_UNSUPPORTED", "Unsupported bridge command: " + request.Command, "Upgrade the Alps Unity Bridge package or use a registered CLI command.");
            }
        }

        private static string CameraInfo(BridgeRequest request)
        {
            var camera = ResolveCamera(request.StringArg("target"));
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + request.StringArg("target"), "Run alps-unity camera list to inspect available cameras.");
            }
            return SuccessJson("{\"camera\":" + CameraJson(camera) + "}");
        }

        private static string CameraScreenshot(BridgeRequest request)
        {
            var target = request.StringArg("target");
            if (string.IsNullOrEmpty(target))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera screenshot requires a target Camera.", "Pass a Camera name, hierarchy path or instance id. Run alps-unity camera list to inspect available cameras.");
            }

            var camera = ResolveCamera(target);
            if (camera == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Camera not found: " + target, "Run alps-unity camera list to inspect available cameras.");
            }

            var capture = CaptureCamera(camera, request);
            return SuccessJson(ScreenshotJson(capture, "camera", camera.name));
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

        private static ScreenshotResult CaptureCamera(Camera camera, BridgeRequest request)
        {
            var outputPath = ResolveScreenshotOutputPath(request.StringArg("path"));
            var format = NormalizeFormat(request.StringArg("format"), outputPath);
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath);
            }

            CreateScreenshotDirectory(outputPath);
            var width = Math.Max(1, request.IntArg("width", camera.pixelWidth > 0 ? camera.pixelWidth : 1280));
            var height = Math.Max(1, request.IntArg("height", camera.pixelHeight > 0 ? camera.pixelHeight : 720));
            var transparent = request.BoolArg("transparent", false);
            var quality = Mathf.Clamp(request.IntArg("quality", 95), 1, 100);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousClearFlags = camera.clearFlags;
            var previousBackground = camera.backgroundColor;
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, transparent ? TextureFormat.ARGB32 : TextureFormat.RGB24, false);

            try
            {
                camera.targetTexture = renderTexture;
                if (transparent)
                {
                    camera.clearFlags = CameraClearFlags.SolidColor;
                    camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                }
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                var bytes = format == "jpg" ? texture.EncodeToJPG(quality) : texture.EncodeToPNG();
                WriteScreenshotBytes(outputPath, bytes);
            }
            finally
            {
                RenderTexture.active = previousActive;
                camera.targetTexture = previousTarget;
                camera.clearFlags = previousClearFlags;
                camera.backgroundColor = previousBackground;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

            WriteLog("screenshot_written path=" + outputPath + " width=" + width + " height=" + height + " format=" + format);
            return new ScreenshotResult(outputPath, format, width, height, transparent, quality);
        }

        private static string PrefabInfo(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            var prefab = LoadPrefabAsset(path);
            return SuccessJson("{\"prefab\":" + PrefabAssetJson(path, prefab) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string PrefabCreate(BridgeRequest request)
        {
            var source = request.StringArg("source");
            var path = RequirePrefabOutputPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (string.IsNullOrEmpty(source))
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Prefab create requires a source GameObject or Prefab asset path.", "Pass a hierarchy path, GameObject name, instance id, or existing Assets/*.prefab path.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null && !overwrite)
            {
                return ErrorJson("E_PREFAB_OUTPUT_EXISTS", "Prefab output already exists: " + path, "Pass --overwrite or choose a different Assets/*.prefab path.");
            }

            CreateAssetDirectory(path);
            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(NormalizeAssetPath(source));
            if (sourcePrefab != null)
            {
                if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                if (!AssetDatabase.CopyAsset(NormalizeAssetPath(source), path))
                {
                    return ErrorJson("E_PREFAB_CREATE_FAILED", "Failed to copy Prefab asset to: " + path, "Check destination path and Unity Console diagnostics.");
                }
                AssetDatabase.SaveAssets();
                WriteLog("prefab_created source=" + source + " path=" + path + " mode=copy");
                return SuccessJson(PrefabCreateJson(source, path, "copy"));
            }

            var gameObject = ResolveGameObject(source);
            if (gameObject == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Source GameObject not found: " + source, "Run alps-unity scene hierarchy or pass a hierarchy path / instance id.");
            }
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var saved = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, path, InteractionMode.AutomatedAction);
            if (saved == null)
            {
                return ErrorJson("E_PREFAB_CREATE_FAILED", "Failed to create Prefab at: " + path, "Check destination path, scene object validity and Unity Console diagnostics.");
            }
            AssetDatabase.SaveAssets();
            WriteLog("prefab_created source=" + source + " path=" + path + " mode=scene");
            return SuccessJson(PrefabCreateJson(source, path, "scene"));
        }

        private static string PrefabStageOpen(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var stage = CurrentPrefabStage();
            if (stage != null && StageIsDirty(stage) && !string.Equals(StageAssetPath(stage), path, StringComparison.OrdinalIgnoreCase))
            {
                return ErrorJson("E_PREFAB_DIRTY_CONFLICT", "Current Prefab Stage has unsaved changes.", "Run alps-unity prefab stage-save, or close/save the current Prefab Stage in Unity before opening another Prefab.");
            }

            if (!OpenPrefabStage(path))
            {
                return ErrorJson("E_PREFAB_STAGE_OPEN_FAILED", "Failed to open Prefab Stage: " + path, "Check Unity Console and AlpsUnityBridge.log.");
            }
            WriteLog("prefab_stage_open path=" + path);
            return SuccessJson(PrefabStageJson("open", path, false));
        }

        private static string PrefabStageSave(BridgeRequest request)
        {
            var stage = RequireCurrentPrefabStage(request.StringArg("path"));
            var path = StageAssetPath(stage);
            if (!SavePrefabStage(stage))
            {
                return ErrorJson("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage: " + path, "Check Unity Console and AlpsUnityBridge.log.");
            }
            AssetDatabase.SaveAssets();
            WriteLog("prefab_stage_save path=" + path);
            return SuccessJson(PrefabStageJson("save", path, false));
        }

        private static string PrefabStageClose(BridgeRequest request)
        {
            var stage = RequireCurrentPrefabStage(request.StringArg("path"));
            var path = StageAssetPath(stage);
            var save = request.BoolArg("save", false);
            if (StageIsDirty(stage) && !save)
            {
                return ErrorJson("E_PREFAB_DIRTY_CONFLICT", "Prefab Stage has unsaved changes: " + path, "Run alps-unity prefab stage-close --save or alps-unity prefab stage-save before closing.");
            }
            if (save && !SavePrefabStage(stage))
            {
                return ErrorJson("E_PREFAB_STAGE_SAVE_FAILED", "Failed to save Prefab Stage before close: " + path, "Check Unity Console and AlpsUnityBridge.log.");
            }
            if (!ClosePrefabStage())
            {
                return ErrorJson("E_PREFAB_STAGE_CLOSE_FAILED", "Failed to close Prefab Stage: " + path, "Close the Prefab Stage in Unity or check AlpsUnityBridge.log.");
            }
            WriteLog("prefab_stage_close path=" + path + " save=" + save);
            return SuccessJson(PrefabStageJson("close", path, false));
        }

        private static string PrefabHierarchy(BridgeRequest request)
        {
            var path = RequirePrefabPath(request.StringArg("path"));
            LoadPrefabAsset(path);
            var pageSize = Math.Max(1, request.IntArg("pageSize", 100));
            var cursor = Math.Max(0, request.IntArg("cursor", 0));
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var items = new List<string>();
                var total = CountHierarchy(root.transform);
                CollectHierarchy(root.transform, items, cursor, pageSize, new Counter());
                var nextCursor = cursor + items.Count < total ? (cursor + items.Count).ToString(CultureInfo.InvariantCulture) : null;
                return SuccessJson("{\"prefab\":" + PrefabAssetJson(path, root, root) + ",\"items\":[" + string.Join(",", items.ToArray()) + "],\"pageInfo\":{\"pageSize\":" + pageSize.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":" + cursor.ToString(CultureInfo.InvariantCulture) + ",\"nextCursor\":" + Quote(nextCursor) + ",\"total\":" + total.ToString(CultureInfo.InvariantCulture) + "},\"logPath\":" + Quote(LogPath()) + "}");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string CapabilitiesJson()
        {
            return "["
                + CapabilityJson("camera", CameraCommands) + ","
                + CapabilityJson("scene", SceneCommands) + ","
                + CapabilityJson("screenshot", ScreenshotCommands) + ","
                + CapabilityJson("prefab", PrefabCommands)
                + "]";
        }

        private static bool IsMutatingCommand(string command)
        {
            return command == "prefab.create"
                || command == "prefab.stage-open"
                || command == "prefab.stage-save"
                || command == "prefab.stage-close"
                || command == "camera.screenshot"
                || command == "scene.screenshot"
                || command == "screenshot.capture";
        }

        private static string CapabilityJson(string domain, string[] commands)
        {
            var quotedCommands = new List<string>();
            foreach (var command in commands)
            {
                quotedCommands.Add(Quote(command));
            }
            return "{\"domain\":" + Quote(domain) + ",\"route\":\"POST /bridge/execute\",\"commands\":[" + string.Join(",", quotedCommands.ToArray()) + "]}";
        }

        private static Camera ResolveCamera(string target)
        {
            var cameras = Camera.allCameras;
            if (string.IsNullOrEmpty(target))
            {
                return Camera.main ?? (cameras.Length > 0 ? cameras[0] : null);
            }

            foreach (var camera in cameras)
            {
                if (camera.name == target || camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(camera.gameObject) == target)
                {
                    return camera;
                }
            }

            return null;
        }

        private static string CameraListJson()
        {
            var cameras = Camera.allCameras;
            var items = new List<string>();
            foreach (var camera in cameras)
            {
                items.Add(CameraJson(camera));
            }
            return "{\"cameras\":[" + string.Join(",", items.ToArray()) + "],\"count\":" + cameras.Length + "}";
        }

        private static string CameraJson(Camera camera)
        {
            var targetTexture = camera.targetTexture != null ? camera.targetTexture.name : null;
            return "{"
                + "\"id\":" + camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ","
                + "\"name\":" + Quote(camera.name) + ","
                + "\"path\":" + Quote(HierarchyPath(camera.gameObject)) + ","
                + "\"tag\":" + Quote(camera.tag) + ","
                + "\"enabled\":" + Bool(camera.enabled) + ","
                + "\"clearFlags\":" + Quote(camera.clearFlags.ToString()) + ","
                + "\"projection\":" + Quote(camera.orthographic ? "orthographic" : "perspective") + ","
                + "\"fieldOfView\":" + camera.fieldOfView.ToString(CultureInfo.InvariantCulture) + ","
                + "\"orthographicSize\":" + camera.orthographicSize.ToString(CultureInfo.InvariantCulture) + ","
                + "\"depth\":" + camera.depth.ToString(CultureInfo.InvariantCulture) + ","
                + "\"targetTexture\":" + Quote(targetTexture) + ","
                + "\"pixelWidth\":" + camera.pixelWidth.ToString(CultureInfo.InvariantCulture) + ","
                + "\"pixelHeight\":" + camera.pixelHeight.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        private static string ScreenshotJson(ScreenshotResult capture, string source, string target)
        {
            return "{"
                + "\"screenshotPath\":" + Quote(capture.Path) + ","
                + "\"artifactPath\":" + Quote(capture.Path) + ","
                + "\"format\":" + Quote(capture.Format) + ","
                + "\"width\":" + capture.Width.ToString(CultureInfo.InvariantCulture) + ","
                + "\"height\":" + capture.Height.ToString(CultureInfo.InvariantCulture) + ","
                + "\"transparent\":" + Bool(capture.Transparent) + ","
                + "\"quality\":" + capture.Quality.ToString(CultureInfo.InvariantCulture) + ","
                + "\"source\":" + Quote(source) + ","
                + "\"target\":" + Quote(target) + ","
                + "\"logPath\":" + Quote(LogPath())
                + "}";
        }

        private static string RequirePrefabPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_PREFAB_PATH_REQUIRED", "Pass an Assets/*.prefab path.", "Use alps-unity prefab info <path> or pass --path Assets/Example.prefab.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PREFAB_PATH_INVALID", "Prefab path must be under Assets/ and end with .prefab: " + path, "Pass a Unity asset path such as Assets/Prefabs/Player.prefab.");
            }
            return path;
        }

        private static string RequirePrefabOutputPath(string rawPath)
        {
            return RequirePrefabPath(rawPath);
        }

        private static GameObject LoadPrefabAsset(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                throw new BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Check the path or run alps-unity asset search --type prefab.");
            }
            if (PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.NotAPrefab)
            {
                throw new BridgeCommandException("E_ASSET_NOT_PREFAB", "Asset is not a Prefab: " + path, "Pass an Assets/*.prefab asset path.");
            }
            return prefab;
        }

        private static string NormalizeAssetPath(string rawPath)
        {
            return string.IsNullOrEmpty(rawPath) ? null : rawPath.Replace('\\', '/').Trim();
        }

        private static void CreateAssetDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(ProjectRoot(), directory.Replace('/', Path.DirectorySeparatorChar)));
            AssetDatabase.Refresh();
        }

        private static GameObject ResolveGameObject(string target)
        {
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    return gameObject;
                }
            }
            return null;
        }

        private static string PrefabAssetJson(string path, GameObject prefab)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return PrefabAssetJson(path, prefab, null, dependencies);
        }

        private static string PrefabAssetJson(string path, GameObject prefab, GameObject contentsRoot)
        {
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return PrefabAssetJson(path, prefab, contentsRoot, dependencies);
        }

        private static string PrefabAssetJson(string path, GameObject prefab, GameObject contentsRoot, string[] dependencies)
        {
            var guid = AssetDatabase.AssetPathToGUID(path);
            var assetType = PrefabUtility.GetPrefabAssetType(prefab).ToString();
            var instanceStatus = PrefabUtility.GetPrefabInstanceStatus(prefab).ToString();
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(guid) + ","
                + "\"rootName\":" + Quote(prefab.name) + ","
                + "\"assetType\":" + Quote(assetType) + ","
                + "\"instanceStatus\":" + Quote(instanceStatus) + ","
                + "\"isVariant\":" + Bool(assetType == "Variant") + ","
                + "\"variant\":" + PrefabVariantJson(prefab) + ","
                + "\"dependencyCount\":" + dependencies.Length.ToString(CultureInfo.InvariantCulture) + ","
                + "\"dependencies\":" + DependencySummaryJson(dependencies) + ","
                + "\"nestedPrefabs\":" + NestedPrefabReferencesJson(path, contentsRoot)
                + "}";
        }

        private static string PrefabVariantJson(GameObject prefab)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefab);
            var sourcePath = source == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(source));
            var sourceGuid = string.IsNullOrEmpty(sourcePath) ? null : AssetDatabase.AssetPathToGUID(sourcePath);
            return "{"
                + "\"isVariant\":" + Bool(PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Variant) + ","
                + "\"basePath\":" + Quote(sourcePath) + ","
                + "\"baseGuid\":" + Quote(sourceGuid) + ","
                + "\"sourcePath\":" + Quote(sourcePath) + ","
                + "\"sourceGuid\":" + Quote(sourceGuid)
                + "}";
        }

        private static string DependencySummaryJson(string[] dependencies)
        {
            var items = new List<string>();
            foreach (var dependency in dependencies)
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(dependency);
                items.Add("{\"path\":" + Quote(NormalizeAssetPath(dependency)) + ",\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(dependency)) + ",\"type\":" + Quote(type == null ? null : type.Name) + "}");
            }
            return "{\"count\":" + dependencies.Length.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static string NestedPrefabReferencesJson(string prefabPath, GameObject contentsRoot)
        {
            if (contentsRoot != null)
            {
                return NestedPrefabReferencesJsonFromRoot(prefabPath, contentsRoot);
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return NestedPrefabReferencesJsonFromRoot(prefabPath, root);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static string NestedPrefabReferencesJsonFromRoot(string prefabPath, GameObject root)
        {
            var items = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNestedPrefabReferences(prefabPath, root.transform, root.transform, items, seen);
            return "{\"count\":" + items.Count.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static void CollectNestedPrefabReferences(string prefabPath, Transform root, Transform current, List<string> items, HashSet<string> seen)
        {
            if (current != root && PrefabUtility.IsAnyPrefabInstanceRoot(current.gameObject))
            {
                var sourcePath = NormalizeAssetPath(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(current.gameObject));
                if (!string.IsNullOrEmpty(sourcePath) && !string.Equals(sourcePath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    var key = sourcePath + "|" + HierarchyPath(current.gameObject);
                    if (seen.Add(key))
                    {
                        items.Add(NestedPrefabReferenceJson(current.gameObject, sourcePath));
                    }
                }
            }

            foreach (Transform child in current)
            {
                CollectNestedPrefabReferences(prefabPath, root, child, items, seen);
            }
        }

        private static string NestedPrefabReferenceJson(GameObject gameObject, string sourcePath)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            return "{"
                + "\"name\":" + Quote(gameObject.name) + ","
                + "\"path\":" + Quote(HierarchyPath(gameObject)) + ","
                + "\"sourcePath\":" + Quote(sourcePath) + ","
                + "\"sourceGuid\":" + Quote(AssetDatabase.AssetPathToGUID(sourcePath)) + ","
                + "\"assetType\":" + Quote(source == null ? null : PrefabUtility.GetPrefabAssetType(source).ToString()) + ","
                + "\"instanceStatus\":" + Quote(PrefabUtility.GetPrefabInstanceStatus(gameObject).ToString())
                + "}";
        }

        private static string PrefabCreateJson(string source, string path, string mode)
        {
            return "{"
                + "\"prefab\":" + PrefabAssetJson(path, LoadPrefabAsset(path)) + ","
                + "\"source\":" + Quote(source) + ","
                + "\"mode\":" + Quote(mode) + ","
                + "\"plannedEffects\":[{\"type\":\"prefab.create\",\"path\":" + Quote(path) + ",\"source\":" + Quote(source) + "}],"
                + "\"logPath\":" + Quote(LogPath())
                + "}";
        }

        private static string PrefabStageJson(string action, string path, bool dirty)
        {
            return "{"
                + "\"stage\":{\"action\":" + Quote(action) + ",\"path\":" + Quote(path) + ",\"dirty\":" + Bool(dirty) + "},"
                + "\"plannedEffects\":[{\"type\":" + Quote("prefab.stage-" + action) + ",\"path\":" + Quote(path) + "}],"
                + "\"logPath\":" + Quote(LogPath())
                + "}";
        }

        private static string HierarchyItemJson(Transform transform)
        {
            var componentNames = new List<string>();
            foreach (var component in transform.GetComponents<Component>())
            {
                componentNames.Add(Quote(component != null ? component.GetType().Name : "MissingScript"));
            }
            var gameObject = transform.gameObject;
            return "{"
                + "\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ","
                + "\"name\":" + Quote(gameObject.name) + ","
                + "\"path\":" + Quote(HierarchyPath(gameObject)) + ","
                + "\"active\":" + Bool(gameObject.activeSelf) + ","
                + "\"tag\":" + Quote(gameObject.tag) + ","
                + "\"layer\":" + gameObject.layer.ToString(CultureInfo.InvariantCulture) + ","
                + "\"componentCount\":" + componentNames.Count.ToString(CultureInfo.InvariantCulture) + ","
                + "\"components\":[" + string.Join(",", componentNames.ToArray()) + "]"
                + "}";
        }

        private static int CountHierarchy(Transform transform)
        {
            var count = 1;
            foreach (Transform child in transform)
            {
                count += CountHierarchy(child);
            }
            return count;
        }

        private static void CollectHierarchy(Transform transform, List<string> items, int cursor, int pageSize, Counter counter)
        {
            if (counter.Value >= cursor && items.Count < pageSize)
            {
                items.Add(HierarchyItemJson(transform));
            }
            counter.Value++;
            foreach (Transform child in transform)
            {
                if (items.Count >= pageSize)
                {
                    return;
                }
                CollectHierarchy(child, items, cursor, pageSize, counter);
            }
        }

        private sealed class Counter
        {
            public int Value;
        }

        private static object CurrentPrefabStage()
        {
            return InvokePrefabStageUtility("GetCurrentPrefabStage", null);
        }

        private static object RequireCurrentPrefabStage(string requestedPath)
        {
            var stage = CurrentPrefabStage();
            if (stage == null)
            {
                throw new BridgeCommandException("E_PREFAB_STAGE_NOT_OPEN", "No Prefab Stage is currently open.", "Run alps-unity prefab stage-open <path> first.");
            }
            var path = StageAssetPath(stage);
            if (!string.IsNullOrEmpty(requestedPath) && !string.Equals(NormalizeAssetPath(requestedPath), path, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_PREFAB_STAGE_MISMATCH", "Current Prefab Stage is " + path + ", not " + requestedPath, "Run stage-save/stage-close without --path or use the currently open Prefab path.");
            }
            return stage;
        }

        private static bool OpenPrefabStage(string path)
        {
            return InvokePrefabStageUtility("OpenPrefab", new object[] { path }) != null;
        }

        private static bool SavePrefabStage(object stage)
        {
            var method = stage.GetType().GetMethod("SavePrefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method != null)
            {
                method.Invoke(stage, null);
                return true;
            }
            var root = StageRoot(stage);
            var path = StageAssetPath(stage);
            if (root == null || string.IsNullOrEmpty(path))
            {
                return false;
            }
            PrefabUtility.SaveAsPrefabAsset(root, path);
            return true;
        }

        private static bool ClosePrefabStage()
        {
            if (InvokePrefabStageUtilityVoid("CloseCurrentPrefabStage", null))
            {
                return true;
            }
            var method = typeof(StageUtility).GetMethod("GoBackToPreviousStage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, null);
            return true;
        }

        private static object InvokePrefabStageUtility(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            return method == null ? null : method.Invoke(null, args);
        }

        private static bool InvokePrefabStageUtilityVoid(string methodName, object[] args)
        {
            var method = PrefabStageUtilityMethod(methodName, args == null ? 0 : args.Length);
            if (method == null)
            {
                return false;
            }
            method.Invoke(null, args);
            return true;
        }

        private static MethodInfo PrefabStageUtilityMethod(string methodName, int parameterCount)
        {
            var type = typeof(PrefabStageUtility);
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(flags))
            {
                if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                {
                    return method;
                }
            }
            return null;
        }

        private static string StageAssetPath(object stage)
        {
            var property = stage.GetType().GetProperty("assetPath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : NormalizeAssetPath(property.GetValue(stage, null) as string);
        }

        private static GameObject StageRoot(object stage)
        {
            var property = stage.GetType().GetProperty("prefabContentsRoot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(stage, null) as GameObject;
        }

        private static bool StageIsDirty(object stage)
        {
            var root = StageRoot(stage);
            return root != null && root.scene.isDirty;
        }

        private static string ResolveScreenshotOutputPath(string rawPath)
        {
            try
            {
                return ResolveOutputPath(rawPath);
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to resolve screenshot output path: " + exception.Message);
            }
        }

        private static string ResolveOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                throw new BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Pass --output <path> or a positional screenshot path.");
            }

            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
        }

        private static void CreateScreenshotDirectory(string outputPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to create screenshot output directory: " + exception.Message);
            }
        }

        private static void WriteScreenshotBytes(string outputPath, byte[] bytes)
        {
            try
            {
                File.WriteAllBytes(outputPath, bytes);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to write screenshot output: " + exception.Message);
            }
        }

        private static string NormalizeFormat(string rawFormat, string outputPath)
        {
            var format = string.IsNullOrEmpty(rawFormat) ? Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant() : rawFormat.ToLowerInvariant();
            if (string.IsNullOrEmpty(format))
            {
                format = "png";
            }
            if (format == "jpeg")
            {
                format = "jpg";
            }
            if (format != "png" && format != "jpg")
            {
                throw new BridgeCommandException("E_SCREENSHOT_FORMAT_UNSUPPORTED", "Screenshot format must be png or jpg.");
            }
            return format;
        }

        private static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteJson(HttpListenerContext context, string json, int statusCode)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static string SuccessJson(string dataJson)
        {
            return "{\"ok\":true,\"data\":" + dataJson + "}";
        }

        private static string ErrorJson(string code, string message, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string LogPath()
        {
            return Path.Combine(ProjectRoot(), "Logs", "AlpsUnityBridge.log");
        }

        private static void WriteLog(string line)
        {
            try
            {
                var path = LogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
            catch
            {
            }
        }

        private sealed class BridgeWorkItem
        {
            private readonly object StateLock = new object();
            private bool cancelled;
            private bool started;

            public BridgeWorkItem(BridgeRequest request)
            {
                Request = request;
            }

            public BridgeRequest Request { get; private set; }
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            public string Response { get; set; }
            public bool TimedOut { get; private set; }

            public bool TryStart()
            {
                lock (StateLock)
                {
                    if (cancelled)
                    {
                        return false;
                    }
                    started = true;
                    return true;
                }
            }

            public bool CancelBeforeStart()
            {
                lock (StateLock)
                {
                    if (started)
                    {
                        return false;
                    }
                    cancelled = true;
                    return true;
                }
            }

            public void MarkTimedOut()
            {
                TimedOut = true;
            }
        }

        private sealed class BridgeRequest
        {
            private readonly string body;

            private BridgeRequest(string body, string requestId, string command)
            {
                this.body = body;
                RequestId = requestId;
                Command = command;
            }

            public string RequestId { get; private set; }
            public string Command { get; private set; }

            public static BridgeRequest Parse(string body)
            {
                return new BridgeRequest(body ?? string.Empty, ExtractString(body, "requestId"), ExtractString(body, "command"));
            }

            public string StringArg(string name)
            {
                return ExtractString(body, name);
            }

            public int IntArg(string name, int fallback)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?:\\\")?(-?\\d+)(?:\\\")?");
                if (match.Success)
                {
                    int parsed;
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
                return fallback;
            }

            public bool BoolArg(string name, bool fallback)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                return match.Success ? string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : fallback;
            }

            private static string ExtractString(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
                return match.Success ? Unescape(match.Groups[1].Value) : null;
            }

            private static string Unescape(string value)
            {
                return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
            }
        }

        private sealed class ScreenshotResult
        {
            public ScreenshotResult(string path, string format, int width, int height, bool transparent, int quality)
            {
                Path = path;
                Format = format;
                Width = width;
                Height = height;
                Transparent = transparent;
                Quality = quality;
            }

            public string Path { get; private set; }
            public string Format { get; private set; }
            public int Width { get; private set; }
            public int Height { get; private set; }
            public bool Transparent { get; private set; }
            public int Quality { get; private set; }
        }

        private sealed class BridgeCommandException : Exception
        {
            public BridgeCommandException(string code, string message, string nextAction = null) : base(message)
            {
                Code = code;
                NextAction = nextAction;
            }

            public string Code { get; private set; }
            public string NextAction { get; private set; }
        }
    }
}
#endif
