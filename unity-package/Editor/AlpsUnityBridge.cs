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
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace AlpsUnityCli
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AlpsCustomToolAttribute : Attribute
    {
        public AlpsCustomToolAttribute(string name)
        {
            Name = name;
            Version = "1.0.0";
            InputSchema = "{}";
            OutputSchema = "{}";
            CapabilityType = "project-write";
            DefaultTimeoutMs = 30000;
            MaxTimeoutMs = 600000;
            SupportsJob = true;
            ArtifactKinds = "json,log,file,image";
        }

        public string Name { get; private set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string ArtifactKinds { get; set; }
        public string RequiredInput { get; set; }
    }

    public sealed class AlpsCustomToolContext
    {
        public string RequestId { get; set; }
        public string ProjectPath { get; set; }
        public string InputJson { get; set; }
        public string ArtifactDir { get; set; }
        public string LogPath { get; set; }
    }

    public sealed class AlpsCustomToolDefinition
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string InputSchema { get; set; }
        public string OutputSchema { get; set; }
        public string CapabilityType { get; set; }
        public int DefaultTimeoutMs { get; set; }
        public int MaxTimeoutMs { get; set; }
        public bool SupportsJob { get; set; }
        public string[] ArtifactKinds { get; set; }
        public string[] RequiredInput { get; set; }
        public string RegisteredBy { get; set; }
        public string Assembly { get; set; }
        public string Method { get; set; }
        public Func<AlpsCustomToolContext, string> Execute { get; set; }
    }

    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const int Port = 47392;
        private static readonly object QueueLock = new object();
        private static readonly string[] CameraCommands = { "camera.list", "camera.info", "camera.screenshot" };
        private static readonly string[] SceneCommands = { "scene.screenshot" };
        private static readonly string[] ScreenshotCommands = { "screenshot.capture" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.create", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close", "prefab.hierarchy" };
        private static readonly string[] UiCommands = { "ui.canvas.create", "ui.text.create", "ui.button.create", "ui.image.create", "ui.document.info", "ui.document.create", "ui.hierarchy" };
        private static readonly string[] MaterialCommands = { "material.info", "material.create", "material.assign", "material.set" };
        private static readonly string[] GraphicsCommands = { "graphics.quality", "graphics.settings", "graphics.screenshot" };
        private static readonly string[] PhysicsCommands = { "physics.settings", "physics.raycast", "physics.simulation" };
        private static readonly string[] AnimationCommands = { "animation.clip", "animation.state", "animation.play", "animation.stop" };
        private static readonly string[] VfxCommands = { "vfx.graph", "vfx.effect" };
        private static readonly string[] ShaderCommands = { "shader.info", "shader.read", "shader.create", "shader.update" };
        private static readonly string[] TextureCommands = { "texture.info", "texture.create", "texture.sprite", "texture.import-settings" };
        private static readonly string[] CodeCommands = { "code.exec" };
        private static readonly string[] CustomToolCommands = { "custom-tool.list", "custom-tool.run" };
        private static readonly Dictionary<string, AlpsCustomToolDefinition> CustomTools = new Dictionary<string, AlpsCustomToolDefinition>(StringComparer.Ordinal);
        private static readonly Queue<BridgeWorkItem> WorkQueue = new Queue<BridgeWorkItem>();
        private static HttpListener listener;
        private static bool started;
        private static bool customToolsDiscovered;

        static AlpsUnityBridge()
        {
            EditorApplication.update += ProcessQueuedWork;
            AssemblyReloadEvents.beforeAssemblyReload += StopListener;
            EditorApplication.quitting += StopListener;
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

        private static void StopListener()
        {
            try
            {
                if (listener != null)
                {
                    listener.Stop();
                    listener.Close();
                    listener = null;
                }
            }
            catch (Exception exception)
            {
                WriteLog("bridge_stop_failed " + exception.GetType().Name + ": " + exception.Message);
            }
            started = false;
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
                case "ui.canvas.create":
                    return UiCanvasCreate(request);
                case "ui.text.create":
                    return UiElementCreate(request, "Text");
                case "ui.button.create":
                    return UiElementCreate(request, "Button");
                case "ui.image.create":
                    return UiElementCreate(request, "Image");
                case "ui.document.info":
                    return UiDocumentInfo(request);
                case "ui.document.create":
                    return UiDocumentCreate(request);
                case "ui.hierarchy":
                    return UiHierarchy(request);
                case "material.info":
                    return MaterialInfo(request);
                case "material.create":
                    return MaterialCreate(request);
                case "material.assign":
                    return MaterialAssign(request);
                case "material.set":
                    return MaterialSet(request);
                case "graphics.quality":
                    return GraphicsQuality(request);
                case "graphics.settings":
                    return GraphicsSettingsCommand(request);
                case "graphics.screenshot":
                    return GraphicsScreenshot(request);
                case "physics.settings":
                    return PhysicsSettingsCommand(request);
                case "physics.raycast":
                    return PhysicsRaycast(request);
                case "physics.simulation":
                    return PhysicsSimulation(request);
                case "animation.clip":
                    return AnimationClipCommand(request);
                case "animation.state":
                    return AnimationState(request);
                case "animation.play":
                    return AnimationPlay(request);
                case "animation.stop":
                    return AnimationStop(request);
                case "vfx.graph":
                    return VfxGraph(request);
                case "vfx.effect":
                    return VfxEffect(request);
                case "shader.info":
                    return ShaderInfo(request);
                case "shader.read":
                    return ShaderRead(request);
                case "shader.create":
                    return ShaderCreate(request);
                case "shader.update":
                    return ShaderUpdate(request);
                case "texture.info":
                    return TextureInfo(request);
                case "texture.create":
                    return TextureCreate(request);
                case "texture.sprite":
                    return TextureSprite(request);
                case "texture.import-settings":
                    return TextureImportSettings(request);
                case "editor.state":
                    return SuccessJson(EditorStateJson());
                case "editor.play":
                    return SuccessJson(EditorPlayJson(request));
                case "editor.pause":
                    return SuccessJson(EditorPauseJson(request));
                case "editor.stop":
                    return SuccessJson(EditorStopJson(request));
                case "editor.menu":
                    return SuccessJson(EditorMenuJson(request));
                case "code.exec":
                    return CodeExec(request);
                case "custom-tool.list":
                    return CustomToolList(request);
                case "custom-tool.run":
                    return CustomToolRun(request);
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

        private static string MaterialInfo(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var material = LoadMaterialAsset(path);
            return SuccessJson("{\"material\":" + MaterialJson(path, material) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string MaterialCreate(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var shaderName = request.StringArg("shader");
            var overwrite = request.BoolArg("overwrite", false);
            if (string.IsNullOrEmpty(shaderName))
            {
                return ErrorJson("E_SHADER_REQUIRED", "Material create requires --shader <name>.", "Pass a Unity shader name such as Standard or Universal Render Pipeline/Lit.");
            }
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return ErrorJson("E_SHADER_NOT_FOUND", "Shader not found: " + shaderName, "Check the shader name in Unity or import the required render pipeline package.");
            }
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null && !overwrite)
            {
                return ErrorJson("E_MATERIAL_OUTPUT_EXISTS", "Material output already exists: " + path, "Pass --overwrite or choose a different Assets/*.mat path.");
            }

            CreateAssetDirectory(path);
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }
            var material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            WriteLog("material_created path=" + path + " shader=" + shaderName);
            return SuccessJson(MaterialMutationJson("material.create", path, MaterialJson(path, LoadMaterialAsset(path))));
        }

        private static string MaterialAssign(BridgeRequest request)
        {
            var target = request.StringArg("target");
            var materialPath = RequireMaterialPath(request.StringArg("material"));
            var material = LoadMaterialAsset(materialPath);
            var gameObject = ResolveUniqueGameObject(target);
            if (gameObject == null)
            {
                return ErrorJson("E_TARGET_NOT_FOUND", "Material assign target not found: " + target, "Pass a GameObject name, hierarchy path or instance id.");
            }

            var renderer = gameObject.GetComponent<Renderer>();
            if (renderer != null)
            {
                var slot = Math.Max(0, request.IntArg("slot", 0));
                var materials = renderer.sharedMaterials;
                if (slot >= materials.Length)
                {
                    return ErrorJson("E_MATERIAL_SLOT_INVALID", "Renderer material slot is out of range: " + slot.ToString(CultureInfo.InvariantCulture), "Pass --slot between 0 and " + Math.Max(0, materials.Length - 1).ToString(CultureInfo.InvariantCulture) + ".");
                }
                materials[slot] = material;
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
                AssetDatabase.SaveAssets();
                WriteLog("material_assigned target=" + target + " material=" + materialPath + " component=Renderer slot=" + slot.ToString(CultureInfo.InvariantCulture));
                return SuccessJson(MaterialAssignJson(gameObject, materialPath, "Renderer", slot));
            }

            var graphic = FindUiGraphic(gameObject);
            if (graphic != null)
            {
                var property = graphic.GetType().GetProperty("material", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    return ErrorJson("E_MATERIAL_ASSIGN_UNSUPPORTED", "UI Graphic material property is not writable on target: " + target, "Assign to a Renderer or UI Graphic component with a writable material property.");
                }
                property.SetValue(graphic, material, null);
                EditorUtility.SetDirty(graphic);
                AssetDatabase.SaveAssets();
                WriteLog("material_assigned target=" + target + " material=" + materialPath + " component=Graphic");
                return SuccessJson(MaterialAssignJson(gameObject, materialPath, "Graphic", 0));
            }

            return ErrorJson("E_MATERIAL_TARGET_UNSUPPORTED", "Target has no Renderer or UI Graphic component: " + target, "Add a Renderer/UI Graphic component or choose another target.");
        }

        private static string MaterialSet(BridgeRequest request)
        {
            var path = RequireMaterialPath(request.StringArg("path"));
            var propertyName = request.StringArg("property");
            var value = request.StringArg("value");
            var requestedType = request.StringArg("type");
            var material = LoadMaterialAsset(path);
            if (string.IsNullOrEmpty(propertyName))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_REQUIRED", "Material set requires --property <name>.", "Run alps-unity material info <path> to inspect shader properties.");
            }
            if (!material.HasProperty(propertyName))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_NOT_FOUND", "Material property not found: " + propertyName, "Run alps-unity material info <path> to inspect shader properties.");
            }

            var propertyType = MaterialPropertyType(material, propertyName);
            var normalizedType = NormalizeMaterialValueType(string.IsNullOrEmpty(requestedType) ? propertyType : requestedType);
            if (string.IsNullOrEmpty(normalizedType))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property type for " + propertyName + ": " + propertyType, "Pass --type color|float|vector|texture when the shader property cannot be inferred.");
            }
            if (!TypesCompatible(propertyType, normalizedType))
            {
                return ErrorJson("E_MATERIAL_PROPERTY_TYPE_MISMATCH", "Material property " + propertyName + " is " + propertyType + ", not " + requestedType + ".", "Use the property type reported by alps-unity material info <path>.");
            }

            var appliedValue = ApplyMaterialValue(material, propertyName, normalizedType, value);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            WriteLog("material_set path=" + path + " property=" + propertyName + " type=" + normalizedType);
            return SuccessJson("{\"material\":" + MaterialJson(path, material) + ",\"property\":" + Quote(propertyName) + ",\"propertyType\":" + Quote(propertyType) + ",\"appliedValue\":" + appliedValue + ",\"plannedEffects\":[{\"type\":\"material.set\",\"path\":" + Quote(path) + ",\"property\":" + Quote(propertyName) + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ShaderInfo(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            var shader = LoadShaderLikeAsset(path) as Shader;
            return SuccessJson("{\"shader\":" + ShaderAssetJson(path, shader, false) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ShaderRead(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            LoadShaderLikeAsset(path);
            var fullPath = AssetFullPath(path);
            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to read Shader file: " + exception.Message, "Check file encoding and permissions: " + path);
            }
            return SuccessJson("{\"shader\":" + ShaderAssetJson(path, AssetDatabase.LoadAssetAtPath<Shader>(path), true) + ",\"content\":" + Quote(content) + ",\"encoding\":\"utf-8\",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string ShaderCreate(BridgeRequest request)
        {
            var path = RequireShaderPath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (AssetExists(path) && !overwrite)
            {
                return ErrorJson("E_SHADER_OUTPUT_EXISTS", "Shader output already exists: " + path, "Pass --overwrite or choose a different Assets/*.shader path.");
            }

            var content = request.StringArg("content");
            if (string.IsNullOrEmpty(content))
            {
                if (path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJson("E_SHADER_CONTENT_REQUIRED", "ShaderGraph creation requires --content or --content-file.", "Pass a ShaderGraph JSON payload via --content-file.");
                }
                content = DefaultShaderContent(request.StringArg("name"), path);
            }

            WriteTextAsset(path, content, overwrite);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            WriteLog("shader_created path=" + path);
            return SuccessJson(ShaderMutationJson("shader.create", path));
        }

        private static string ShaderUpdate(BridgeRequest request)
        {
            var path = ResolveShaderPath(request.StringArg("path") ?? request.StringArg("target"));
            LoadShaderLikeAsset(path);
            var content = request.StringArg("content");
            if (content == null)
            {
                return ErrorJson("E_SHADER_CONTENT_REQUIRED", "Shader update requires --content or --content-file.", "Pass replacement Shader content via --content-file.");
            }

            WriteTextAsset(path, content, true);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.SaveAssets();
            WriteLog("shader_updated path=" + path);
            return SuccessJson(ShaderMutationJson("shader.update", path));
        }

        private static string TextureInfo(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            var texture = LoadTextureAsset(path);
            return SuccessJson("{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string TextureCreate(BridgeRequest request)
        {
            var path = RequireTexturePath(request.StringArg("path"));
            var overwrite = request.BoolArg("overwrite", false);
            if (AssetExists(path) && !overwrite)
            {
                return ErrorJson("E_TEXTURE_OUTPUT_EXISTS", "Texture output already exists: " + path, "Pass --overwrite or choose a different texture asset path.");
            }

            CreateAssetDirectory(path);
            var source = request.StringArg("source");
            if (!string.IsNullOrEmpty(source))
            {
                CopyTextureSource(source, path, overwrite);
            }
            else
            {
                WriteGeneratedTexture(path, request);
            }

            try
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                ApplyTextureSettings(path, request, true);
                AssetDatabase.SaveAssets();
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to import texture: " + path + " (" + exception.Message + ")", "Check texture file contents, importer settings and Unity Console diagnostics.");
            }
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                return ErrorJson("E_IMPORT_FAILED", "Texture import did not produce a Texture2D asset: " + path, "Check texture file contents and Unity Console diagnostics.");
            }
            WriteLog("texture_created path=" + path);
            return SuccessJson(TextureMutationJson("texture.create", path, texture));
        }

        private static string TextureSprite(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            LoadTextureAsset(path);
            var importer = RequireTextureImporter(path);
            importer.textureType = TextureImporterType.Sprite;
            var spriteMode = request.StringArg("spriteMode");
            if (!string.IsNullOrEmpty(spriteMode))
            {
                importer.spriteImportMode = string.Equals(spriteMode, "multiple", StringComparison.OrdinalIgnoreCase) ? SpriteImportMode.Multiple : SpriteImportMode.Single;
            }
            var pixelsPerUnit = request.IntArg("pixelsPerUnit", -1);
            if (pixelsPerUnit > 0)
            {
                importer.spritePixelsPerUnit = pixelsPerUnit;
            }
            importer.spritePivot = SpritePivot(request, importer.spritePivot);
            try
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to reimport texture as Sprite: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            WriteLog("texture_sprite path=" + path);
            return SuccessJson(TextureMutationJson("texture.sprite", path, LoadTextureAsset(path)));
        }

        private static string TextureImportSettings(BridgeRequest request)
        {
            var path = ResolveTexturePath(request.StringArg("path") ?? request.StringArg("target"));
            LoadTextureAsset(path);
            bool changed;
            try
            {
                changed = ApplyTextureSettings(path, request, true);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_IMPORT_FAILED", "Failed to apply texture import settings: " + path + " (" + exception.Message + ")", "Check TextureImporter settings and Unity Console diagnostics.");
            }
            var texture = LoadTextureAsset(path);
            return SuccessJson("{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"changed\":" + Bool(changed) + ",\"plannedEffects\":" + (changed ? "[{\"type\":\"texture.import-settings\",\"path\":" + Quote(path) + "}]" : "[]") + ",\"logPath\":" + Quote(LogPath()) + "}");
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
                return SuccessJson("{\"document\":" + UiDocumentJson(document) + ",\"target\":" + UiGameObjectJson(gameObject, "UIDocument") + ",\"logPath\":" + Quote(LogPath()) + "}");
            }

            if (string.IsNullOrEmpty(path))
            {
                return ErrorJson("E_UI_DOCUMENT_PATH_REQUIRED", "UIDocument info requires a target or Assets/*.uxml path.", "Pass alps-unity ui document/info <UIDocument target> or --path Assets/UI/Layout.uxml.");
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
            return SuccessJson("{\"document\":" + UiDocumentJson(document) + ",\"target\":" + UiGameObjectJson(gameObject, "UIDocument") + ",\"styleSheet\":" + UiStyleSheetJson(ussPath, string.IsNullOrEmpty(ussPath) ? null : LoadStyleSheet(ussPath)) + ",\"plannedEffects\":[{\"type\":\"ui.document.create\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"uxml\":" + Quote(uxmlPath) + ",\"uss\":" + Quote(ussPath) + "}],\"logPath\":" + Quote(LogPath()) + "}");
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
            return SuccessJson("{\"items\":[" + string.Join(",", items.ToArray()) + "],\"pageInfo\":{\"pageSize\":" + pageSize.ToString(CultureInfo.InvariantCulture) + ",\"cursor\":" + cursor.ToString(CultureInfo.InvariantCulture) + ",\"nextCursor\":" + Quote(nextCursor) + ",\"total\":" + summaries.Count.ToString(CultureInfo.InvariantCulture) + "},\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string RequireShaderPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_SHADER_PATH_REQUIRED", "Pass an Assets/*.shader or Assets/*.shadergraph path.", "Use alps-unity shader info <path> or pass --path Assets/Shaders/Example.shader.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || (!path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)))
            {
                throw new BridgeCommandException("E_SHADER_PATH_INVALID", "Shader path must be under Assets/ and end with .shader or .shadergraph: " + path, "Pass a Unity asset path such as Assets/Shaders/Example.shader.");
            }
            return path;
        }

        private static string ResolveShaderPath(string rawPath)
        {
            var path = RequireShaderPath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run alps-unity asset search --type shader.");
            }
            return path;
        }

        private static UnityEngine.Object LoadShaderLikeAsset(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset == null)
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found: " + path, "Check the path or run alps-unity asset search --type shader.");
            }
            return asset;
        }

        private static Shader LoadShaderAsset(string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                throw new BridgeCommandException("E_SHADER_NOT_FOUND", "Shader asset not found or not compiled as a Shader: " + path, "Check ShaderGraph imports or Unity Console diagnostics.");
            }
            return shader;
        }

        private static string RequireTexturePath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_REQUIRED", "Pass a texture path under Assets/.", "Use alps-unity texture info <path> or pass --path Assets/Textures/Example.png.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !IsTextureExtension(path))
            {
                throw new BridgeCommandException("E_TEXTURE_PATH_INVALID", "Texture path must be under Assets/ and use a supported texture extension: " + path, "Use .png, .jpg, .jpeg, .tga, .psd or .exr under Assets/.");
            }
            return path;
        }

        private static string ResolveTexturePath(string rawPath)
        {
            var path = RequireTexturePath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + path, "Check the path or run alps-unity asset search --type texture.");
            }
            return path;
        }

        private static Texture2D LoadTextureAsset(string path)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            if (texture == null)
            {
                throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found or failed to import: " + path, "Check importer diagnostics in Unity Console and AlpsUnityBridge.log.");
            }
            return texture;
        }

        private static TextureImporter RequireTextureImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                throw new BridgeCommandException("E_TEXTURE_IMPORTER_NOT_FOUND", "TextureImporter not found for: " + path, "Reimport the asset or verify it is a texture file.");
            }
            return importer;
        }

        private static bool IsTextureExtension(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".psd" || extension == ".exr";
        }

        private static bool AssetExists(string assetPath)
        {
            return File.Exists(AssetFullPath(assetPath)) || !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));
        }

        private static string AssetFullPath(string assetPath)
        {
            return Path.Combine(ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static void WriteTextAsset(string path, string content, bool overwrite)
        {
            CreateAssetDirectory(path);
            var fullPath = AssetFullPath(path);
            if (File.Exists(fullPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Output already exists: " + path, "Pass --overwrite or choose a different asset path.");
            }
            try
            {
                File.WriteAllText(fullPath, content, new UTF8Encoding(false));
                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to write asset file: " + exception.Message, "Check destination path and permissions: " + path);
            }
        }

        private static string DefaultShaderContent(string rawName, string path)
        {
            var name = string.IsNullOrEmpty(rawName) ? Path.GetFileNameWithoutExtension(path) : rawName;
            return "Shader \"Alps/" + EscapeShaderName(name) + "\" {\n    Properties {\n        _Color (\"Color\", Color) = (1,1,1,1)\n    }\n    SubShader {\n        Tags { \"RenderType\"=\"Opaque\" }\n        Pass {\n            CGPROGRAM\n            #include \"UnityCG.cginc\"\n            #pragma vertex vert\n            #pragma fragment frag\n            struct appdata { float4 vertex : POSITION; };\n            struct v2f { float4 vertex : SV_POSITION; };\n            fixed4 _Color;\n            v2f vert (appdata v) { v2f o; o.vertex = UnityObjectToClipPos(v.vertex); return o; }\n            fixed4 frag (v2f i) : SV_Target { return _Color; }\n            ENDCG\n        }\n    }\n}\n";
        }

        private static string EscapeShaderName(string name)
        {
            return (name ?? "Shader").Replace("\\", "/").Replace("\"", "");
        }

        private static string ShaderMutationJson(string type, string path)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            return "{\"shader\":" + ShaderAssetJson(path, shader, false) + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"path\":" + Quote(path) + ",\"refresh\":true}],\"logPath\":" + Quote(LogPath()) + "}";
        }

        private static string ShaderAssetJson(string path, Shader shader, bool includeContentMetadata)
        {
            var fullPath = AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ","
                + "\"name\":" + Quote(shader != null ? shader.name : (asset != null ? asset.name : Path.GetFileNameWithoutExtension(path))) + ","
                + "\"assetType\":" + Quote(asset == null ? null : asset.GetType().Name) + ","
                + "\"isShaderGraph\":" + Bool(path.EndsWith(".shadergraph", StringComparison.OrdinalIgnoreCase)) + ","
                + "\"exists\":" + Bool(fileInfo.Exists) + ","
                + "\"sizeBytes\":" + (fileInfo.Exists ? fileInfo.Length.ToString(CultureInfo.InvariantCulture) : "0") + ","
                + "\"encoding\":" + Quote(includeContentMetadata ? "utf-8" : null) + ","
                + "\"diagnostics\":" + ShaderDiagnosticsJson(shader)
                + "}";
        }

        private static string ShaderDiagnosticsJson(Shader shader)
        {
            if (shader == null)
            {
                return "{\"supported\":null,\"messageCount\":0,\"messages\":[]}";
            }
            var messages = ShaderMessagesJson(shader);
            return "{\"supported\":" + Bool(shader.isSupported) + ",\"messageCount\":" + messages.Count.ToString(CultureInfo.InvariantCulture) + ",\"messages\":[" + string.Join(",", messages.ToArray()) + "]}";
        }

        private static List<string> ShaderMessagesJson(Shader shader)
        {
            var items = new List<string>();
            var getMessages = ShaderMessagesMethod();
            if (getMessages == null)
            {
                return items;
            }
            var messages = getMessages.Invoke(null, new object[] { shader }) as Array;
            if (messages == null)
            {
                return items;
            }
            foreach (var message in messages)
            {
                items.Add(ShaderMessageJson(message));
            }
            return items;
        }

        private static MethodInfo ShaderMessagesMethod()
        {
            var shaderUtil = Type.GetType("UnityEditor.ShaderUtil,UnityEditor");
            if (shaderUtil == null)
            {
                return null;
            }
            foreach (var method in shaderUtil.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name != "GetShaderMessages")
                {
                    continue;
                }
                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Shader))
                {
                    return method;
                }
            }
            return null;
        }

        private static string ShaderMessageJson(object message)
        {
            var type = message.GetType();
            return "{"
                + "\"severity\":" + Quote(ReflectionValue(type, message, "severity")) + ","
                + "\"message\":" + Quote(ReflectionValue(type, message, "message")) + ","
                + "\"file\":" + Quote(ReflectionValue(type, message, "file")) + ","
                + "\"line\":" + Quote(ReflectionValue(type, message, "line")) + ","
                + "\"platform\":" + Quote(ReflectionValue(type, message, "platform"))
                + "}";
        }

        private static string ReflectionValue(Type type, object target, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var value = field.GetValue(target);
                return value == null ? null : value.ToString();
            }
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                var value = property.GetValue(target, null);
                return value == null ? null : value.ToString();
            }
            return null;
        }

        private static void CopyTextureSource(string source, string destinationPath, bool overwrite)
        {
            var sourcePath = NormalizeAssetPath(source);
            var fullSource = sourcePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ? AssetFullPath(sourcePath) : Path.GetFullPath(source.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));
            var fullDestination = AssetFullPath(destinationPath);
            if (!File.Exists(fullSource))
            {
                throw new BridgeCommandException("E_TEXTURE_SOURCE_NOT_FOUND", "Texture source file not found: " + source, "Pass --source with an existing file path or Assets/ texture path.");
            }
            if (File.Exists(fullDestination) && overwrite)
            {
                File.Delete(fullDestination);
            }
            File.Copy(fullSource, fullDestination, overwrite);
            AssetDatabase.Refresh();
        }

        private static void WriteGeneratedTexture(string path, BridgeRequest request)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg")
            {
                throw new BridgeCommandException("E_TEXTURE_FORMAT_UNSUPPORTED", "Generated textures can only be written as .png or .jpg: " + path, "Pass --source for other texture formats.");
            }
            var width = Math.Max(1, request.IntArg("width", 1));
            var height = Math.Max(1, request.IntArg("height", 1));
            var color = ParseTextureColor(request.StringArg("color"));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var index = 0; index < pixels.Length; index++)
                {
                    pixels[index] = color;
                }
                texture.SetPixels(pixels);
                texture.Apply();
                var bytes = extension == ".png" ? texture.EncodeToPNG() : texture.EncodeToJPG(95);
                File.WriteAllBytes(AssetFullPath(path), bytes);
                AssetDatabase.Refresh();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Color ParseTextureColor(string rawColor)
        {
            if (string.IsNullOrEmpty(rawColor))
            {
                return Color.white;
            }
            Color color;
            if (ColorUtility.TryParseHtmlString(rawColor.StartsWith("#", StringComparison.Ordinal) ? rawColor : "#" + rawColor, out color))
            {
                return color;
            }
            throw new BridgeCommandException("E_TEXTURE_COLOR_INVALID", "Texture color must be a hex RGB/RGBA value: " + rawColor, "Use values like --color ffffff or --color #ff00ffff.");
        }

        private static bool ApplyTextureSettings(string path, BridgeRequest request, bool saveAndReimport)
        {
            var importer = RequireTextureImporter(path);
            var changed = false;
            changed = ApplyTextureType(importer, request.StringArg("textureType")) || changed;
            changed = ApplyBool(request, "readable", importer.isReadable, value => importer.isReadable = value) || changed;
            changed = ApplyBool(request, "mipmap", importer.mipmapEnabled, value => importer.mipmapEnabled = value) || changed;
            changed = ApplyBool(request, "sRGB", importer.sRGBTexture, value => importer.sRGBTexture = value) || changed;
            changed = ApplyBool(request, "alphaIsTransparency", importer.alphaIsTransparency, value => importer.alphaIsTransparency = value) || changed;
            changed = ApplyEnum(request.StringArg("wrapMode"), importer.wrapMode.ToString(), value => importer.wrapMode = (TextureWrapMode)Enum.Parse(typeof(TextureWrapMode), value, true)) || changed;
            changed = ApplyEnum(request.StringArg("filterMode"), importer.filterMode.ToString(), value => importer.filterMode = (FilterMode)Enum.Parse(typeof(FilterMode), value, true)) || changed;
            changed = ApplyEnum(request.StringArg("compression"), importer.textureCompression.ToString(), value => importer.textureCompression = (TextureImporterCompression)Enum.Parse(typeof(TextureImporterCompression), value, true)) || changed;
            var maxTextureSize = request.IntArg("maxTextureSize", -1);
            if (maxTextureSize > 0 && importer.maxTextureSize != maxTextureSize)
            {
                importer.maxTextureSize = maxTextureSize;
                changed = true;
            }
            if (saveAndReimport && changed)
            {
                importer.SaveAndReimport();
                AssetDatabase.Refresh();
                WriteLog("texture_import_settings path=" + path);
            }
            return changed;
        }

        private static bool ApplyTextureType(TextureImporter importer, string rawType)
        {
            if (string.IsNullOrEmpty(rawType))
            {
                return false;
            }
            var normalized = string.Equals(rawType, "sprite", StringComparison.OrdinalIgnoreCase) ? "Sprite" : rawType;
            var next = (TextureImporterType)Enum.Parse(typeof(TextureImporterType), normalized, true);
            if (importer.textureType == next)
            {
                return false;
            }
            importer.textureType = next;
            return true;
        }

        private delegate void BoolSetter(bool value);
        private delegate void StringSetter(string value);

        private static bool ApplyBool(BridgeRequest request, string name, bool current, BoolSetter setter)
        {
            if (!request.HasArg(name))
            {
                return false;
            }
            var next = request.BoolArg(name, current);
            if (current == next)
            {
                return false;
            }
            setter(next);
            return true;
        }

        private static bool ApplyEnum(string rawValue, string current, StringSetter setter)
        {
            if (string.IsNullOrEmpty(rawValue) || string.Equals(rawValue, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            setter(rawValue);
            return true;
        }

        private static Vector2 SpritePivot(BridgeRequest request, Vector2 fallback)
        {
            var x = ParseFloat(request.StringArg("pivotX"), fallback.x);
            var y = ParseFloat(request.StringArg("pivotY"), fallback.y);
            return new Vector2(Mathf.Clamp01(x), Mathf.Clamp01(y));
        }

        private static float ParseFloat(string rawValue, float fallback)
        {
            float parsed;
            return !string.IsNullOrEmpty(rawValue) && float.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static string TextureMutationJson(string type, string path, Texture2D texture)
        {
            return "{\"texture\":" + TextureAssetJson(path, texture) + ",\"importSettings\":" + TextureImporterJson(RequireTextureImporter(path)) + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"path\":" + Quote(path) + ",\"refresh\":true}],\"logPath\":" + Quote(LogPath()) + "}";
        }

        private static string TextureAssetJson(string path, Texture2D texture)
        {
            var fileInfo = new FileInfo(AssetFullPath(path));
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ","
                + "\"name\":" + Quote(texture == null ? Path.GetFileNameWithoutExtension(path) : texture.name) + ","
                + "\"width\":" + (texture == null ? "0" : texture.width.ToString(CultureInfo.InvariantCulture)) + ","
                + "\"height\":" + (texture == null ? "0" : texture.height.ToString(CultureInfo.InvariantCulture)) + ","
                + "\"format\":" + Quote(texture == null ? null : texture.format.ToString()) + ","
                + "\"sizeBytes\":" + (fileInfo.Exists ? fileInfo.Length.ToString(CultureInfo.InvariantCulture) : "0")
                + "}";
        }

        private static string TextureImporterJson(TextureImporter importer)
        {
            return "{"
                + "\"textureType\":" + Quote(importer.textureType.ToString()) + ","
                + "\"spriteImportMode\":" + Quote(importer.spriteImportMode.ToString()) + ","
                + "\"pixelsPerUnit\":" + importer.spritePixelsPerUnit.ToString(CultureInfo.InvariantCulture) + ","
                + "\"pivot\":{\"x\":" + importer.spritePivot.x.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + importer.spritePivot.y.ToString(CultureInfo.InvariantCulture) + "},"
                + "\"readable\":" + Bool(importer.isReadable) + ","
                + "\"mipmap\":" + Bool(importer.mipmapEnabled) + ","
                + "\"sRGB\":" + Bool(importer.sRGBTexture) + ","
                + "\"alphaIsTransparency\":" + Bool(importer.alphaIsTransparency) + ","
                + "\"wrapMode\":" + Quote(importer.wrapMode.ToString()) + ","
                + "\"filterMode\":" + Quote(importer.filterMode.ToString()) + ","
                + "\"maxTextureSize\":" + importer.maxTextureSize.ToString(CultureInfo.InvariantCulture) + ","
                + "\"compression\":" + Quote(importer.textureCompression.ToString())
                + "}";
        }

        private static string CapabilitiesJson()
        {
            return "["
                + CapabilityJson("camera", CameraCommands) + ","
                + CapabilityJson("scene", SceneCommands) + ","
                + CapabilityJson("screenshot", ScreenshotCommands) + ","
                + CapabilityJson("prefab", PrefabCommands) + ","
                + CapabilityJson("ui", UiCommands) + ","
                + CapabilityJson("material", MaterialCommands) + ","
                + CapabilityJson("graphics", GraphicsCommands) + ","
                + CapabilityJson("physics", PhysicsCommands) + ","
                + CapabilityJson("animation", AnimationCommands) + ","
                + CapabilityJson("vfx", VfxCommands) + ","
                + CapabilityJson("shader", ShaderCommands) + ","
                + CapabilityJson("texture", TextureCommands) + ","
                + CapabilityJson("code", CodeCommands) + ","
                + CapabilityJson("custom-tool", CustomToolCommands)
                + "]";
        }

        private static bool IsMutatingCommand(string command)
        {
            return command == "prefab.create"
                || command == "prefab.stage-open"
                || command == "prefab.stage-save"
                || command == "prefab.stage-close"
                || command == "ui.canvas.create"
                || command == "ui.text.create"
                || command == "ui.button.create"
                || command == "ui.image.create"
                || command == "ui.document.create"
                || command == "material.create"
                || command == "material.assign"
                || command == "material.set"
                || command == "camera.screenshot"
                || command == "scene.screenshot"
                || command == "screenshot.capture"
                || command == "graphics.quality"
                || command == "graphics.settings"
                || command == "graphics.screenshot"
                || command == "physics.settings"
                || command == "physics.simulation"
                || command == "animation.clip"
                || command == "animation.play"
                || command == "animation.stop"
                || command == "vfx.graph"
                || command == "vfx.effect"
                || command == "shader.create"
                || command == "shader.update"
                || command == "texture.create"
                || command == "texture.sprite"
                || command == "texture.import-settings"
                || command == "code.exec"
                || command == "custom-tool.run";
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

        public static void RegisterCustomTool(AlpsCustomToolDefinition definition)
        {
            if (definition == null || string.IsNullOrEmpty(definition.Name) || definition.Execute == null)
            {
                throw new ArgumentException("Custom tool registration requires name and Execute delegate.");
            }
            if (definition.ArtifactKinds == null)
            {
                definition.ArtifactKinds = new[] { "json", "log", "file", "image" };
            }
            if (definition.RequiredInput == null)
            {
                definition.RequiredInput = new string[0];
            }
            definition.DisplayName = string.IsNullOrEmpty(definition.DisplayName) ? definition.Name : definition.DisplayName;
            definition.Version = string.IsNullOrEmpty(definition.Version) ? "1.0.0" : definition.Version;
            definition.InputSchema = string.IsNullOrEmpty(definition.InputSchema) ? "{}" : definition.InputSchema;
            definition.OutputSchema = string.IsNullOrEmpty(definition.OutputSchema) ? "{}" : definition.OutputSchema;
            definition.CapabilityType = string.IsNullOrEmpty(definition.CapabilityType) ? "project-write" : definition.CapabilityType;
            definition.DefaultTimeoutMs = definition.DefaultTimeoutMs <= 0 ? 30000 : definition.DefaultTimeoutMs;
            definition.MaxTimeoutMs = definition.MaxTimeoutMs <= 0 ? 600000 : definition.MaxTimeoutMs;
            definition.RegisteredBy = string.IsNullOrEmpty(definition.RegisteredBy) ? "unity-editor-package" : definition.RegisteredBy;
            CustomTools[definition.Name] = definition;
        }

        private static void DiscoverCustomTools()
        {
            if (customToolsDiscovered)
            {
                return;
            }
            customToolsDiscovered = true;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var attribute = Attribute.GetCustomAttribute(method, typeof(AlpsCustomToolAttribute)) as AlpsCustomToolAttribute;
                        if (attribute == null) continue;
                        TryRegisterAttributedCustomTool(assembly, type, method, attribute);
                    }
                }
            }
        }

        private static void TryRegisterAttributedCustomTool(Assembly assembly, Type type, MethodInfo method, AlpsCustomToolAttribute attribute)
        {
            var parameters = method.GetParameters();
            if (method.ReturnType != typeof(string) || parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(AlpsCustomToolContext)))
            {
                WriteLog("custom_tool_registration_skipped name=" + attribute.Name + " reason=signature");
                return;
            }

            RegisterCustomTool(new AlpsCustomToolDefinition
            {
                Name = attribute.Name,
                DisplayName = attribute.DisplayName,
                Description = attribute.Description,
                Version = attribute.Version,
                InputSchema = attribute.InputSchema,
                OutputSchema = attribute.OutputSchema,
                CapabilityType = attribute.CapabilityType,
                DefaultTimeoutMs = attribute.DefaultTimeoutMs,
                MaxTimeoutMs = attribute.MaxTimeoutMs,
                SupportsJob = attribute.SupportsJob,
                ArtifactKinds = SplitCsv(attribute.ArtifactKinds),
                RequiredInput = SplitCsv(attribute.RequiredInput),
                RegisteredBy = "unity-editor-package",
                Assembly = assembly.GetName().Name,
                Method = type.FullName + "." + method.Name,
                Execute = delegate(AlpsCustomToolContext context)
                {
                    var result = parameters.Length == 0 ? method.Invoke(null, null) : method.Invoke(null, new object[] { context });
                    return result == null ? "null" : (string)result;
                }
            });
        }

        private static string CustomToolList(BridgeRequest request)
        {
            DiscoverCustomTools();
            var tools = new List<string>();
            foreach (var pair in CustomTools)
            {
                tools.Add(CustomToolDefinitionJson(pair.Value));
            }
            tools.Sort(StringComparer.Ordinal);
            return SuccessJson("{\"tools\":[" + string.Join(",", tools.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string CustomToolRun(BridgeRequest request)
        {
            DiscoverCustomTools();
            var name = request.StringArg("name");
            if (string.IsNullOrEmpty(name) || !CustomTools.ContainsKey(name))
            {
                return ErrorJson("E_TOOL_NOT_FOUND", "Tool not found: " + name, "Run alps-unity custom-tool list to inspect registered tools.");
            }

            var definition = CustomTools[name];
            var timeoutMs = request.IntArg("timeoutMs", definition.DefaultTimeoutMs);
            if (timeoutMs <= 0 || timeoutMs > definition.MaxTimeoutMs)
            {
                return ErrorJson("E_SCHEMA_INVALID", "timeoutMs exceeds the registered tool limit.", "Use a timeout between 1 and " + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.");
            }

            if (request.BoolArgAtPath(new[] { "args", "execution", "preferJob" }, false))
            {
                return ErrorJson("E_JOB_UNSUPPORTED", "--job / Local Agent custom-tool execution is not implemented by the bundled Live Bridge yet.", "Run without --job for synchronous Live Bridge execution, or wait for Local Agent job support before relying on asynchronous custom-tool semantics.");
            }

            var inputJson = request.RawJsonArg("input") ?? "{}";
            var schemaError = ValidateCustomToolInput(definition, inputJson);
            if (!string.IsNullOrEmpty(schemaError))
            {
                return ErrorJson("E_TOOL_SCHEMA_INVALID", schemaError, "Run alps-unity custom-tool list to inspect the tool inputSchema.");
            }

            var startedAt = DateTime.UtcNow;
            var artifactDir = ResolveArtifactDir(request.StringArg("artifactDir"), request.RequestId);
            try
            {
                Directory.CreateDirectory(artifactDir);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to create artifact directory: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }

            string resultJson;
            try
            {
                WriteLog("custom_tool_run requestId=" + request.RequestId + " name=" + name + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                resultJson = definition.Execute(new AlpsCustomToolContext
                {
                    RequestId = request.RequestId,
                    ProjectPath = request.StringArg("projectPath") ?? ProjectRoot(),
                    InputJson = inputJson,
                    ArtifactDir = artifactDir,
                    LogPath = LogPath()
                });
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                return ErrorJson("E_TOOL_EXECUTION_FAILED", inner.GetType().Name + ": " + inner.Message, "Check Unity Console and AlpsUnityBridge.log with the returned requestId.");
            }
            catch (Exception exception)
            {
                return ErrorJson("E_TOOL_EXECUTION_FAILED", exception.GetType().Name + ": " + exception.Message, "Check Unity Console and AlpsUnityBridge.log with the returned requestId.");
            }

            var endedAt = DateTime.UtcNow;
            var durationMs = (long)(endedAt - startedAt).TotalMilliseconds;
            var manifest = CustomToolManifestJson(request.RequestId, definition, artifactDir, startedAt, endedAt);
            try
            {
                File.WriteAllText(Path.Combine(artifactDir, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                return ErrorJson("E_ARTIFACT_WRITE_FAILED", "Failed to write artifact manifest: " + exception.Message, "Pass --artifact-dir to a writable directory.");
            }
            if (durationMs > timeoutMs)
            {
                WriteLog("custom_tool_timeout requestId=" + request.RequestId + " name=" + name + " durationMs=" + durationMs.ToString(CultureInfo.InvariantCulture) + " timeoutMs=" + timeoutMs.ToString(CultureInfo.InvariantCulture));
                return ErrorJson("E_EXECUTION_TIMEOUT", "Tool exceeded timeoutMs after completion: " + durationMs.ToString(CultureInfo.InvariantCulture) + "ms > " + timeoutMs.ToString(CultureInfo.InvariantCulture) + "ms.", "Inspect bridge logs and artifact manifest with the returned requestId before retrying.");
            }

            return SuccessJson("{"
                + "\"command\":\"custom-tool.run\","
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"version\":" + Quote(definition.Version) + "},"
                + "\"execution\":{\"channel\":\"live\",\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + durationMs.ToString(CultureInfo.InvariantCulture) + ",\"exitState\":\"completed\"},"
                + "\"result\":" + JsonLiteralOrString(resultJson) + ","
                + "\"artifacts\":[{\"id\":\"manifest\",\"kind\":\"json\",\"path\":" + Quote(Path.Combine(artifactDir, "manifest.json")) + ",\"description\":\"custom-tool artifact manifest\"}],"
                + "\"audit\":" + CustomToolAuditJson(request, definition, artifactDir, inputJson, timeoutMs, startedAt, endedAt, "completed") + ","
                + "\"logs\":{\"bridge\":" + Quote(LogPath()) + "}"
                + "}");
        }

        private static string CustomToolDefinitionJson(AlpsCustomToolDefinition definition)
        {
            return "{"
                + "\"name\":" + Quote(definition.Name) + ","
                + "\"displayName\":" + Quote(definition.DisplayName) + ","
                + "\"description\":" + Quote(definition.Description) + ","
                + "\"version\":" + Quote(definition.Version) + ","
                + "\"inputSchema\":" + JsonLiteralOrString(definition.InputSchema) + ","
                + "\"outputSchema\":" + JsonLiteralOrString(definition.OutputSchema) + ","
                + "\"capabilityType\":" + Quote(definition.CapabilityType) + ","
                + "\"defaultTimeoutMs\":" + definition.DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture) + ","
                + "\"maxTimeoutMs\":" + definition.MaxTimeoutMs.ToString(CultureInfo.InvariantCulture) + ","
                + "\"supportsJob\":" + Bool(definition.SupportsJob) + ","
                + "\"artifactKinds\":" + StringArrayJson(definition.ArtifactKinds) + ","
                + "\"registeredBy\":" + Quote(definition.RegisteredBy) + ","
                + "\"assembly\":" + Quote(definition.Assembly) + ","
                + "\"method\":" + Quote(definition.Method)
                + "}";
        }

        private static string CustomToolManifestJson(string requestId, AlpsCustomToolDefinition definition, string artifactDir, DateTime startedAt, DateTime endedAt)
        {
            return "{"
                + "\"requestId\":" + Quote(requestId) + ","
                + "\"command\":\"custom-tool.run\","
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"version\":" + Quote(definition.Version) + "},"
                + "\"artifact\":[{\"id\":\"manifest\",\"kind\":\"json\",\"relativePath\":\"manifest.json\",\"producer\":\"unity-editor-package\"}],"
                + "\"createdAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"root\":" + Quote(artifactDir)
                + "}";
        }

        private static string CustomToolAuditJson(BridgeRequest request, AlpsCustomToolDefinition definition, string artifactDir, string inputJson, int timeoutMs, DateTime startedAt, DateTime endedAt, string exitState)
        {
            return "{"
                + "\"requestId\":" + Quote(request.RequestId) + ","
                + "\"command\":\"custom-tool.run\","
                + "\"projectPath\":" + Quote(request.StringArg("projectPath") ?? ProjectRoot()) + ","
                + "\"instanceId\":" + Quote(request.StringArg("instanceId")) + ","
                + "\"unityVersion\":" + Quote(Application.unityVersion) + ","
                + "\"bridgeVersion\":\"com.alps.unity-cli.bridge\","
                + "\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ","
                + "\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ","
                + "\"initiator\":{\"cliUser\":\"local\",\"cwd\":" + Quote(Directory.GetCurrentDirectory()) + "},"
                + "\"input\":{\"sourceKind\":\"json\",\"sizeBytes\":" + Encoding.UTF8.GetByteCount(inputJson ?? string.Empty).ToString(CultureInfo.InvariantCulture) + "},"
                + "\"execution\":{\"channel\":\"live\",\"timeoutMs\":" + timeoutMs.ToString(CultureInfo.InvariantCulture) + ",\"exitState\":" + Quote(exitState) + "},"
                + "\"effects\":{\"fileWrites\":[],\"assetRefresh\":false,\"domainReload\":false,\"playModeChanged\":false,\"packagesChanged\":false},"
                + "\"artifacts\":[{\"kind\":\"json\",\"path\":" + Quote(Path.Combine(artifactDir, "manifest.json")) + "}],"
                + "\"tool\":{\"name\":" + Quote(definition.Name) + ",\"capabilityType\":" + Quote(definition.CapabilityType) + "}"
                + "}";
        }

        private static string ValidateCustomToolInput(AlpsCustomToolDefinition definition, string inputJson)
        {
            var schemaJson = definition.InputSchema;
            if (!string.IsNullOrEmpty(schemaJson) && schemaJson.Trim() != "{}")
            {
                var schemaType = JsonStringProperty(schemaJson, "type");
                if (!string.IsNullOrEmpty(schemaType) && !JsonValueMatchesType(inputJson, schemaType))
                {
                    return "Input does not match inputSchema; expected $ to be " + schemaType + ".";
                }

                var requiredJson = JsonRawProperty(schemaJson, "required");
                foreach (var key in JsonStringArray(requiredJson))
                {
                    if (JsonRawProperty(inputJson, key) == null)
                    {
                        return "Input does not match inputSchema; missing required property: " + key;
                    }
                }

                var propertiesJson = JsonRawProperty(schemaJson, "properties");
                foreach (var key in JsonObjectPropertyNames(propertiesJson))
                {
                    var valueJson = JsonRawProperty(inputJson, key);
                    if (valueJson == null)
                    {
                        continue;
                    }
                    var propertySchema = JsonRawProperty(propertiesJson, key);
                    var propertyType = JsonStringProperty(propertySchema, "type");
                    if (!string.IsNullOrEmpty(propertyType) && !JsonValueMatchesType(valueJson, propertyType))
                    {
                        return "Input does not match inputSchema; property " + key + " must be " + propertyType + ".";
                    }
                }
            }

            foreach (var key in definition.RequiredInput ?? new string[0])
            {
                if (JsonRawProperty(inputJson, key) == null)
                {
                    return "Input does not match inputSchema; missing required property: " + key;
                }
            }
            return null;
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
            return ScreenshotDataJson(capture, source, target).TrimEnd('}') + ",\"logPath\":" + Quote(LogPath()) + "}";
        }

        private static string ScreenshotDataJson(ScreenshotResult capture, string source, string target)
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
                + "\"target\":" + Quote(target)
                + "}";
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
            return "{"
                + "\"created\":" + UiGameObjectJson(gameObject, kind) + ","
                + "\"rectTransform\":" + RectTransformJson(gameObject.GetComponent<RectTransform>()) + ","
                + "\"plannedEffects\":[{\"type\":" + Quote(command) + ",\"target\":" + Quote(HierarchyPath(gameObject)) + "}],"
                + "\"logPath\":" + Quote(LogPath())
                + "}";
        }

        private static string UiGameObjectJson(GameObject gameObject, string kind)
        {
            return "{"
                + "\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ","
                + "\"name\":" + Quote(gameObject.name) + ","
                + "\"path\":" + Quote(HierarchyPath(gameObject)) + ","
                + "\"kind\":" + Quote(kind) + ","
                + "\"active\":" + Bool(gameObject.activeSelf) + ","
                + "\"components\":" + ComponentNamesJson(gameObject)
                + "}";
        }

        private static string RectTransformJson(RectTransform rectTransform)
        {
            if (rectTransform == null)
            {
                return "null";
            }
            return "{"
                + "\"width\":" + rectTransform.rect.width.ToString(CultureInfo.InvariantCulture) + ","
                + "\"height\":" + rectTransform.rect.height.ToString(CultureInfo.InvariantCulture) + ","
                + "\"x\":" + rectTransform.anchoredPosition.x.ToString(CultureInfo.InvariantCulture) + ","
                + "\"y\":" + rectTransform.anchoredPosition.y.ToString(CultureInfo.InvariantCulture)
                + "}";
        }

        private static string ComponentNamesJson(GameObject gameObject)
        {
            var componentNames = new List<string>();
            foreach (var component in gameObject.GetComponents<Component>())
            {
                componentNames.Add(Quote(component != null ? component.GetType().Name : "MissingScript"));
            }
            return "[" + string.Join(",", componentNames.ToArray()) + "]";
        }

        private static string UiDocumentAssetInfoJson(string path, VisualTreeAsset visualTree, string uss, StyleSheet styleSheet)
        {
            return "{"
                + "\"document\":{"
                + UiAssetFieldsJson(path, visualTree) + ","
                + "\"styleSheet\":" + UiStyleSheetJson(uss, styleSheet)
                + "},"
                + "\"diagnostics\":" + UiDocumentReadDiagnosticsJson() + ","
                + "\"logPath\":" + Quote(LogPath())
                + "}";
        }

        private static string UiDocumentReadDiagnosticsJson()
        {
            return "[{"
                + "\"level\":\"info\","
                + "\"code\":\"UI_DOCUMENT_READ_OK\","
                + "\"message\":\"UI Toolkit assets are readable.\""
                + "}]";
        }

        private static string UiDocumentJson(UIDocument document)
        {
            var visualTreePath = document.visualTreeAsset == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(document.visualTreeAsset));
            var panelSettingsPath = document.panelSettings == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(document.panelSettings));
            return "{"
                + "\"visualTreeAsset\":" + UiAssetReferenceJson(visualTreePath, document.visualTreeAsset) + ","
                + "\"panelSettings\":" + UiAssetReferenceJson(panelSettingsPath, document.panelSettings) + ","
                + "\"rootVisualElement\":{\"name\":" + Quote(document.rootVisualElement == null ? null : document.rootVisualElement.name) + "}"
                + "}";
        }

        private static string UiAssetFieldsJson(string path, UnityEngine.Object asset)
        {
            return "\"path\":" + Quote(path) + ",\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ",\"type\":" + Quote(asset == null ? null : asset.GetType().Name) + ",\"exists\":" + Bool(asset != null);
        }

        private static string UiAssetReferenceJson(string path, UnityEngine.Object asset)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "null";
            }
            return "{" + UiAssetFieldsJson(path, asset) + "}";
        }

        private static string UiStyleSheetJson(string path, StyleSheet styleSheet)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "null";
            }
            return "{" + UiAssetFieldsJson(path, styleSheet) + "}";
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
                items.Add("{\"item\":" + UiGameObjectJson(gameObject, kind) + ",\"rectTransform\":" + RectTransformJson(gameObject.GetComponent<RectTransform>()) + ",\"document\":" + (gameObject.GetComponent<UIDocument>() == null ? "null" : UiDocumentJson(gameObject.GetComponent<UIDocument>())) + "}");
            }
            foreach (Transform child in transform)
            {
                CollectUiHierarchy(child, items);
            }
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
            return ResolveGameObjectTarget(target).Target;
        }

        private static TargetResolution ResolveGameObjectTarget(string target)
        {
            var matches = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
            {
                return TargetResolution.FromMatches(matches);
            }

            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            return TargetResolution.FromMatches(matches);
        }

        private static string GraphicsQuality(BridgeRequest request)
        {
            var effects = new List<string>();
            var requestedQualityLevel = request.OptionalIntArg("qualityLevel");
            if (requestedQualityLevel.HasValue)
            {
                SetQualityLevel(requestedQualityLevel.Value, effects);
            }
            ApplyQualityInt(request, "vSyncCount", "vSyncCount", delegate(int value) { QualitySettings.vSyncCount = value; }, effects);
            ApplyQualityInt(request, "antiAliasing", "antiAliasing", delegate(int value) { QualitySettings.antiAliasing = value; }, effects);
            ApplyQualityFloat(request, "shadowDistance", "shadowDistance", delegate(float value) { QualitySettings.shadowDistance = value; }, effects);
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.masterTextureLimit = value; }, effects);
            ApplyQualityFloat(request, "lodBias", "lodBias", delegate(float value) { QualitySettings.lodBias = value; }, effects);
            ApplyQualityInt(request, "maximumLODLevel", "maximumLODLevel", delegate(int value) { QualitySettings.maximumLODLevel = value; }, effects);
            ApplyQualityEnum<AnisotropicFiltering>(request, "anisotropicFiltering", "anisotropicFiltering", delegate(AnisotropicFiltering value) { QualitySettings.anisotropicFiltering = value; }, effects);
            ApplyQualityEnum<ShadowQuality>(request, "shadows", "shadows", delegate(ShadowQuality value) { QualitySettings.shadows = value; }, effects);
            ApplyQualityEnum<ShadowResolution>(request, "shadowResolution", "shadowResolution", delegate(ShadowResolution value) { QualitySettings.shadowResolution = value; }, effects);
            ApplyQualityBool(request, "realtimeReflectionProbes", "realtimeReflectionProbes", delegate(bool value) { QualitySettings.realtimeReflectionProbes = value; }, effects);
            ApplyQualityBool(request, "softParticles", "softParticles", delegate(bool value) { QualitySettings.softParticles = value; }, effects);
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("graphics_quality_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson("{\"graphics\":" + GraphicsSummaryJson() + ",\"qualitySettings\":" + QualitySettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string GraphicsSettingsCommand(BridgeRequest request)
        {
            var effects = new List<string>();
            ApplyQualityInt(request, "antiAliasing", "antiAliasing", delegate(int value) { QualitySettings.antiAliasing = value; }, effects);
            ApplyQualityFloat(request, "shadowDistance", "shadowDistance", delegate(float value) { QualitySettings.shadowDistance = value; }, effects);
            ApplyQualityInt(request, "vSyncCount", "vSyncCount", delegate(int value) { QualitySettings.vSyncCount = value; }, effects);
            ApplyQualityInt(request, "masterTextureLimit", "masterTextureLimit", delegate(int value) { QualitySettings.masterTextureLimit = value; }, effects);
            ApplyQualityFloat(request, "lodBias", "lodBias", delegate(float value) { QualitySettings.lodBias = value; }, effects);
            ApplyQualityInt(request, "maximumLODLevel", "maximumLODLevel", delegate(int value) { QualitySettings.maximumLODLevel = value; }, effects);
            if (request.HasArg("renderPipeline"))
            {
                var value = request.StringArg("renderPipeline");
                if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "built-in", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(value))
                {
                    GraphicsSettings.defaultRenderPipeline = null;
                }
                else
                {
                    var pipeline = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(NormalizeAssetPath(value));
                    if (pipeline == null)
                    {
                        return ErrorJson("E_RENDER_PIPELINE_NOT_FOUND", "RenderPipelineAsset not found: " + value, "Pass an Assets/*.asset RenderPipelineAsset path or use --render-pipeline built-in.");
                    }
                    GraphicsSettings.defaultRenderPipeline = pipeline;
                }
                effects.Add(EffectJson("graphics.settings", "renderPipeline", value));
            }
            if (request.HasArg("property"))
            {
                ApplyGenericGraphicsSetting(request, effects);
            }
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("graphics_settings_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson("{\"graphics\":" + GraphicsSummaryJson() + ",\"qualitySettings\":" + QualitySettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string GraphicsScreenshot(BridgeRequest request)
        {
            var camera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : ResolveCamera(null);
            if (camera == null)
            {
                return ErrorJson("E_SCENE_VIEW_NOT_AVAILABLE", "No SceneView or Camera is available for graphics screenshot capture.", "Open a SceneView or create an active Camera in the scene.");
            }
            var capture = CaptureCamera(camera, request);
            return SuccessJson("{\"diagnostics\":" + GraphicsSummaryJson() + ",\"qualitySettings\":" + QualitySettingsJson() + ",\"screenshot\":" + ScreenshotDataJson(capture, "graphics", camera.name) + ",\"screenshotPath\":" + Quote(capture.Path) + ",\"artifactPath\":" + Quote(capture.Path) + ",\"plannedEffects\":[" + EffectJson("graphics.screenshot", "path", capture.Path) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string PhysicsSettingsCommand(BridgeRequest request)
        {
            var effects = new List<string>();
            ApplyPhysicsVector3(request, "gravity", effects);
            ApplyPhysicsVector2(request, "gravity2d", effects);
            ApplyPhysicsFloat(request, "defaultContactOffset", "defaultContactOffset", delegate(float value) { Physics.defaultContactOffset = value; }, effects);
            ApplyPhysicsFloat(request, "bounceThreshold", "bounceThreshold", delegate(float value) { Physics.bounceThreshold = value; }, effects);
            ApplyPhysicsInt(request, "defaultSolverIterations", "defaultSolverIterations", delegate(int value) { Physics.defaultSolverIterations = value; }, effects);
            ApplyPhysicsInt(request, "defaultSolverVelocityIterations", "defaultSolverVelocityIterations", delegate(int value) { Physics.defaultSolverVelocityIterations = value; }, effects);
            ApplyPhysicsBool(request, "queriesHitTriggers", "queriesHitTriggers", delegate(bool value) { Physics.queriesHitTriggers = value; }, effects);
            ApplyPhysicsBool(request, "queriesHitBackfaces", "queriesHitBackfaces", delegate(bool value) { Physics.queriesHitBackfaces = value; }, effects);
            ApplyPhysicsBool(request, "queriesStartInColliders", "queriesStartInColliders", delegate(bool value) { Physics2D.queriesStartInColliders = value; }, effects);
            ApplyPhysicsBool(request, "autoSimulation", "autoSimulation", delegate(bool value) { Physics.autoSimulation = value; }, effects);
            if (request.HasArg("property"))
            {
                ApplyGenericPhysicsSetting(request, effects);
            }
            if (effects.Count > 0)
            {
                AssetDatabase.SaveAssets();
                WriteLog("physics_settings_updated effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson("{\"physics\":" + PhysicsSettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string PhysicsRaycast(BridgeRequest request)
        {
            var mode = NormalizeMode(request.StringArg("mode"));
            var distance = Math.Max(0f, request.FloatArg("distance", 100f));
            var layerMask = request.IntArg("layerMask", Physics.DefaultRaycastLayers);
            if (mode == "2d")
            {
                var origin = new Vector2(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f));
                var direction = NormalizeVector2(new Vector2(request.FloatArg("directionX", 1f), request.FloatArg("directionY", 0f)));
                if (request.BoolArg("all", false))
                {
                    var hits = Physics2D.RaycastAll(origin, direction, distance, layerMask);
                    return SuccessJson("{\"mode\":\"2d\",\"query\":" + Raycast2DQueryJson(origin, direction, distance, layerMask) + ",\"hitCount\":" + hits.Length.ToString(CultureInfo.InvariantCulture) + ",\"hits\":" + Raycast2DHitsJson(hits) + ",\"logPath\":" + Quote(LogPath()) + "}");
                }
                var hit = Physics2D.Raycast(origin, direction, distance, layerMask);
                return SuccessJson("{\"mode\":\"2d\",\"query\":" + Raycast2DQueryJson(origin, direction, distance, layerMask) + ",\"hit\":" + Bool(hit.collider != null) + ",\"hitCount\":" + (hit.collider != null ? "1" : "0") + ",\"hits\":[" + (hit.collider != null ? Raycast2DHitJson(hit) : string.Empty) + "],\"nextActions\":" + RaycastNextActionsJson(hit.collider != null) + ",\"logPath\":" + Quote(LogPath()) + "}");
            }

            var origin3d = new Vector3(request.FloatArg("originX", 0f), request.FloatArg("originY", 0f), request.FloatArg("originZ", 0f));
            var direction3d = NormalizeVector3(new Vector3(request.FloatArg("directionX", 0f), request.FloatArg("directionY", 0f), request.FloatArg("directionZ", 1f)));
            if (request.BoolArg("all", false))
            {
                var hits = Physics.RaycastAll(origin3d, direction3d, distance, layerMask);
                Array.Sort(hits, delegate(RaycastHit a, RaycastHit b) { return a.distance.CompareTo(b.distance); });
                return SuccessJson("{\"mode\":\"3d\",\"query\":" + Raycast3DQueryJson(origin3d, direction3d, distance, layerMask) + ",\"hitCount\":" + hits.Length.ToString(CultureInfo.InvariantCulture) + ",\"hits\":" + Raycast3DHitsJson(hits) + ",\"logPath\":" + Quote(LogPath()) + "}");
            }
            RaycastHit hit3d;
            var didHit = Physics.Raycast(origin3d, direction3d, out hit3d, distance, layerMask);
            return SuccessJson("{\"mode\":\"3d\",\"query\":" + Raycast3DQueryJson(origin3d, direction3d, distance, layerMask) + ",\"hit\":" + Bool(didHit) + ",\"hitCount\":" + (didHit ? "1" : "0") + ",\"hits\":[" + (didHit ? Raycast3DHitJson(hit3d) : string.Empty) + "],\"nextActions\":" + RaycastNextActionsJson(didHit) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string PhysicsSimulation(BridgeRequest request)
        {
            var steps = Math.Max(1, request.IntArg("steps", 1));
            var deltaTime = Math.Max(0.0001f, request.FloatArg("deltaTime", Time.fixedDeltaTime));
            var simulate = request.BoolArg("simulate", false);
            var effects = new List<string>();
            if (simulate)
            {
                for (var i = 0; i < steps; i++)
                {
                    Physics.Simulate(deltaTime);
                    Physics2D.Simulate(deltaTime);
                }
                effects.Add(EffectJson("physics.simulation", "steps", steps.ToString(CultureInfo.InvariantCulture)));
                WriteLog("physics_simulated steps=" + steps.ToString(CultureInfo.InvariantCulture) + " deltaTime=" + deltaTime.ToString(CultureInfo.InvariantCulture));
            }
            return SuccessJson("{\"simulation\":{\"simulate\":" + Bool(simulate) + ",\"steps\":" + steps.ToString(CultureInfo.InvariantCulture) + ",\"deltaTime\":" + deltaTime.ToString(CultureInfo.InvariantCulture) + ",\"fixedDeltaTime\":" + Time.fixedDeltaTime.ToString(CultureInfo.InvariantCulture) + ",\"autoSimulation\":" + Bool(Physics.autoSimulation) + "},\"physics\":" + PhysicsSettingsJson() + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string AnimationClipCommand(BridgeRequest request)
        {
            var action = string.Equals(request.StringArg("action"), "create", StringComparison.OrdinalIgnoreCase) ? "create" : "info";
            var path = RequireAnimationClipPath(request.StringArg("path"));
            if (action == "create")
            {
                var overwrite = request.BoolArg("overwrite", false);
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null && !overwrite)
                {
                    return ErrorJson("E_ANIMATION_CLIP_EXISTS", "AnimationClip already exists: " + path, "Pass --overwrite or choose a different Assets/*.anim path.");
                }
                CreateAssetDirectory(path);
                if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                var clip = new AnimationClip();
                clip.name = Path.GetFileNameWithoutExtension(path);
                clip.frameRate = Math.Max(1f, request.FloatArg("frameRate", 60f));
                clip.legacy = request.BoolArg("legacy", false);
                AssetDatabase.CreateAsset(clip, path);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                WriteLog("animation_clip_created path=" + path);
                return SuccessJson("{\"clip\":" + AnimationClipJson(path, LoadAnimationClipAsset(path)) + ",\"plannedEffects\":[{\"type\":\"animation.clip.create\",\"path\":" + Quote(path) + "}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            return SuccessJson("{\"clip\":" + AnimationClipJson(path, LoadAnimationClipAsset(path)) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string AnimationState(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation state requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var layer = Mathf.Clamp(request.IntArg("layer", 0), 0, Math.Max(0, animator.layerCount - 1));
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, layer) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string AnimationPlay(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation play requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            var state = request.StringArg("state");
            if (string.IsNullOrEmpty(state))
            {
                return ErrorJson("E_ANIMATION_STATE_REQUIRED", "Animation play requires --state <name>.", "Pass an Animator state name or hash-compatible state path.");
            }
            var layer = request.IntArg("layer", 0);
            if (layer < 0 || layer >= animator.layerCount)
            {
                return ErrorJson("E_ANIMATION_LAYER_INVALID", "Animator layer is out of range: " + layer.ToString(CultureInfo.InvariantCulture), "Run alps-unity animation state --target <go> to inspect layer count.");
            }
            animator.Play(state, layer, request.FloatArg("normalizedTime", 0f));
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            WriteLog("animation_play target=" + HierarchyPath(gameObject) + " state=" + state + " layer=" + layer.ToString(CultureInfo.InvariantCulture));
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, layer) + ",\"plannedEffects\":[{\"type\":\"animation.play\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"state\":" + Quote(state) + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string AnimationStop(BridgeRequest request)
        {
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "Animation stop requires --target <go>.");
            var animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return ErrorJson("E_ANIMATOR_NOT_FOUND", "Animator component not found on target: " + HierarchyPath(gameObject), "Add an Animator component or choose another target.");
            }
            animator.Rebind();
            animator.Update(0f);
            EditorUtility.SetDirty(animator);
            WriteLog("animation_stop target=" + HierarchyPath(gameObject));
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"animator\":" + AnimatorJson(animator, 0) + ",\"plannedEffects\":[{\"type\":\"animation.stop\",\"target\":" + Quote(HierarchyPath(gameObject)) + "}],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string VfxGraph(BridgeRequest request)
        {
            var action = string.Equals(request.StringArg("action"), "create", StringComparison.OrdinalIgnoreCase) ? "create" : "info";
            var path = RequireVfxGraphPath(request.StringArg("path"));
            var assetType = RequireVfxAssetType();
            if (action == "create")
            {
                var overwrite = request.BoolArg("overwrite", false);
                if (AssetDatabase.LoadAssetAtPath(path, assetType) != null && !overwrite)
                {
                    return ErrorJson("E_VFX_GRAPH_EXISTS", "VFX Graph already exists: " + path, "Pass --overwrite or choose a different Assets/*.vfx path.");
                }
                CreateAssetDirectory(path);
                if (AssetDatabase.LoadAssetAtPath(path, assetType) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                }
                var created = CreateVfxGraphAsset(path, assetType);
                if (created == null)
                {
                    return ErrorJson("E_VFX_GRAPH_CREATE_FAILED", "VFX Graph asset creation did not produce a readable VisualEffectAsset: " + path, "Check Unity Console and AlpsUnityBridge.log; the Visual Effect Graph package may have changed its internal authoring API.");
                }
                WriteLog("vfx_graph_created path=" + path);
                return SuccessJson("{\"graph\":" + VfxGraphJson(path, created) + ",\"plannedEffects\":[{\"type\":\"vfx.graph.create\",\"path\":" + Quote(path) + "}],\"logPath\":" + Quote(LogPath()) + "}");
            }
            var asset = AssetDatabase.LoadAssetAtPath(path, assetType);
            if (asset == null)
            {
                return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + path, "Check the path or create one with alps-unity vfx graph <path> --create.");
            }
            return SuccessJson("{\"graph\":" + VfxGraphJson(path, asset) + ",\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string VfxEffect(BridgeRequest request)
        {
            var visualEffectType = RequireVisualEffectType();
            var gameObject = ResolveUniqueTarget(request.StringArg("target"), "VFX effect requires --target <go>.");
            var component = gameObject.GetComponent(visualEffectType);
            var effects = new List<string>();
            if (component == null && request.BoolArg("add", false))
            {
                component = gameObject.AddComponent(visualEffectType);
                effects.Add("{\"type\":\"vfx.effect.add\",\"target\":" + Quote(HierarchyPath(gameObject)) + "}");
            }
            if (component == null)
            {
                return ErrorJson("E_VFX_COMPONENT_NOT_FOUND", "VisualEffect component not found on target: " + HierarchyPath(gameObject), "Pass --add to add one or choose a target with VisualEffect.");
            }
            var graphPath = request.StringArg("graph");
            if (!string.IsNullOrEmpty(graphPath))
            {
                var assetType = RequireVfxAssetType();
                var graph = AssetDatabase.LoadAssetAtPath(NormalizeAssetPath(graphPath), assetType);
                if (graph == null)
                {
                    return ErrorJson("E_VFX_GRAPH_NOT_FOUND", "VFX Graph asset not found: " + graphPath, "Pass an existing Assets/*.vfx path or create one with alps-unity vfx graph <path> --create.");
                }
                var property = visualEffectType.GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    return ErrorJson("E_VFX_ASSIGN_UNSUPPORTED", "VisualEffect asset property is not writable in this Unity version.", "Assign the VFX Graph in Unity or upgrade Visual Effect Graph package.");
                }
                property.SetValue(component, graph, null);
                effects.Add("{\"type\":\"vfx.effect.assign\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"graph\":" + Quote(NormalizeAssetPath(graphPath)) + "}");
            }
            EditorUtility.SetDirty(component);
            AssetDatabase.SaveAssets();
            WriteLog("vfx_effect target=" + HierarchyPath(gameObject) + " effects=" + effects.Count.ToString(CultureInfo.InvariantCulture));
            return SuccessJson("{\"target\":" + GameObjectTargetJson(gameObject) + ",\"effect\":" + VfxEffectJson(component) + ",\"plannedEffects\":[" + string.Join(",", effects.ToArray()) + "],\"logPath\":" + Quote(LogPath()) + "}");
        }

        private static string RequireAnimationClipPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_PATH_REQUIRED", "Pass an Assets/*.anim path.", "Use alps-unity animation clip <path> or pass --path Assets/Animations/Example.anim.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_PATH_INVALID", "AnimationClip path must be under Assets/ and end with .anim: " + path, "Pass a Unity asset path such as Assets/Animations/Player.anim.");
            }
            return path;
        }

        private static AnimationClip LoadAnimationClipAsset(string path)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null)
            {
                throw new BridgeCommandException("E_ANIMATION_CLIP_NOT_FOUND", "AnimationClip asset not found: " + path, "Check the path or create one with alps-unity animation clip <path> --create.");
            }
            return clip;
        }

        private static string AnimationClipJson(string path, AnimationClip clip)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ","
                + "\"name\":" + Quote(clip.name) + ","
                + "\"length\":" + Number(clip.length) + ","
                + "\"frameRate\":" + Number(clip.frameRate) + ","
                + "\"legacy\":" + Bool(clip.legacy) + ","
                + "\"bindings\":" + AnimationBindingsJson(clip, bindings)
                + "}";
        }

        private static string AnimationBindingsJson(AnimationClip clip, EditorCurveBinding[] bindings)
        {
            var items = new List<string>();
            foreach (var binding in bindings)
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                items.Add("{\"path\":" + Quote(binding.path) + ",\"property\":" + Quote(binding.propertyName) + ",\"type\":" + Quote(binding.type == null ? null : binding.type.Name) + ",\"keyCount\":" + (curve == null ? "0" : curve.keys.Length.ToString(CultureInfo.InvariantCulture)) + "}");
            }
            return "{\"count\":" + bindings.Length.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static string AnimatorJson(Animator animator, int layer)
        {
            var controller = animator.runtimeAnimatorController;
            var controllerPath = controller == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(controller));
            var layerCount = animator.layerCount;
            var safeLayer = layerCount <= 0 ? 0 : Mathf.Clamp(layer, 0, layerCount - 1);
            var state = layerCount > 0 ? animator.GetCurrentAnimatorStateInfo(safeLayer) : default(AnimatorStateInfo);
            return "{"
                + "\"enabled\":" + Bool(animator.enabled) + ","
                + "\"layerCount\":" + layerCount.ToString(CultureInfo.InvariantCulture) + ","
                + "\"selectedLayer\":" + safeLayer.ToString(CultureInfo.InvariantCulture) + ","
                + "\"runtimeAnimatorController\":{\"name\":" + Quote(controller == null ? null : controller.name) + ",\"path\":" + Quote(controllerPath) + ",\"guid\":" + Quote(string.IsNullOrEmpty(controllerPath) ? null : AssetDatabase.AssetPathToGUID(controllerPath)) + "},"
                + "\"currentState\":{\"fullPathHash\":" + state.fullPathHash.ToString(CultureInfo.InvariantCulture) + ",\"shortNameHash\":" + state.shortNameHash.ToString(CultureInfo.InvariantCulture) + ",\"normalizedTime\":" + Number(state.normalizedTime) + ",\"length\":" + Number(state.length) + "}"
                + "}";
        }

        private static string RequireVfxGraphPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_VFX_GRAPH_PATH_REQUIRED", "Pass an Assets/*.vfx path.", "Use alps-unity vfx graph <path> or pass --path Assets/VFX/Effect.vfx.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_VFX_GRAPH_PATH_INVALID", "VFX Graph path must be under Assets/ and end with .vfx: " + path, "Pass a Unity asset path such as Assets/VFX/Explosion.vfx.");
            }
            return path;
        }

        private static UnityEngine.Object CreateVfxGraphAsset(string path, Type assetType)
        {
            try
            {
                var created = CreateVfxGraphAssetWithUnityUtility(path, assetType);
                if (created != null)
                {
                    return created;
                }

                WriteMinimalVfxGraphYaml(path);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();
                return AssetDatabase.LoadAssetAtPath(path, assetType);
            }
            catch (BridgeCommandException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_VFX_GRAPH_CREATE_FAILED", "Failed to create VFX Graph asset: " + exception.GetType().Name + ": " + exception.Message, "Check Unity Console and AlpsUnityBridge.log; the Visual Effect Graph package import pipeline may have changed.");
            }
        }

        private static UnityEngine.Object CreateVfxGraphAssetWithUnityUtility(string path, Type assetType)
        {
            var utilityType = FindType("UnityEditor.VisualEffectAssetEditorUtility");
            if (utilityType == null)
            {
                return null;
            }
            var createMethod = utilityType.GetMethod("CreateNewAsset", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (createMethod == null)
            {
                return null;
            }
            var created = createMethod.Invoke(null, new object[] { path }) as UnityEngine.Object;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            AssetDatabase.Refresh();
            return created == null || !assetType.IsInstanceOfType(created) ? AssetDatabase.LoadAssetAtPath(path, assetType) : created;
        }

        private static void WriteMinimalVfxGraphYaml(string path)
        {
            var fullPath = Path.Combine(ProjectRoot(), path.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath,
                "%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!114 &114350483966674976\n"
                + "MonoBehaviour:\n"
                + "  m_Script: {fileID: 11500000, guid: 7d4c867f6b72b714dbb5fd1780afe208, type: 3}\n"
                + "--- !u!2058629511 &1\n"
                + "VisualEffectResource:\n"
                + "  m_Graph: {fileID: 114350483966674976}\n");
        }

        private static Type RequireVfxAssetType()
        {
            RequireVfxPackageInstalled();
            var type = FindType("UnityEngine.VFX.VisualEffectAsset");
            if (type == null)
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is installed in the manifest but its editor/runtime assemblies are not loaded.", "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        private static Type RequireVisualEffectType()
        {
            RequireVfxPackageInstalled();
            var type = FindType("UnityEngine.VFX.VisualEffect");
            if (type == null)
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is installed in the manifest but its runtime assembly is not loaded.", "Wait for Unity PackageManager resolve/import to finish, then retry.");
            }
            return type;
        }

        private static void RequireVfxPackageInstalled()
        {
            if (!PackageManifestContains("com.unity.visualeffectgraph") && !PackageLockContains("com.unity.visualeffectgraph"))
            {
                throw new BridgeCommandException("E_VFX_PACKAGE_MISSING", "Visual Effect Graph package is not installed in this Unity project.", "Install com.unity.visualeffectgraph with alps-unity package install com.unity.visualeffectgraph, run package resolve, then retry.");
            }
        }

        private static bool PackageManifestContains(string packageName)
        {
            return FileContains(Path.Combine(ProjectRoot(), "Packages", "manifest.json"), "\"" + packageName + "\"");
        }

        private static bool PackageLockContains(string packageName)
        {
            return FileContains(Path.Combine(ProjectRoot(), "Packages", "packages-lock.json"), "\"" + packageName + "\"");
        }

        private static bool FileContains(string path, string value)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch
            {
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        private static string VfxGraphJson(string path, UnityEngine.Object asset)
        {
            return "{\"path\":" + Quote(path) + ",\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ",\"name\":" + Quote(asset == null ? null : asset.name) + ",\"type\":" + Quote(asset == null ? null : asset.GetType().FullName) + "}";
        }

        private static string VfxEffectJson(Component component)
        {
            var property = component.GetType().GetProperty("visualEffectAsset", BindingFlags.Instance | BindingFlags.Public);
            var graph = property == null ? null : property.GetValue(component, null) as UnityEngine.Object;
            var graphPath = graph == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(graph));
            return "{\"componentType\":" + Quote(component.GetType().FullName) + ",\"enabled\":" + Bool(ComponentEnabled(component)) + ",\"graph\":{\"name\":" + Quote(graph == null ? null : graph.name) + ",\"path\":" + Quote(graphPath) + ",\"guid\":" + Quote(string.IsNullOrEmpty(graphPath) ? null : AssetDatabase.AssetPathToGUID(graphPath)) + "}}";
        }

        private static bool ComponentEnabled(Component component)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            return property == null || (bool)property.GetValue(component, null);
        }

        private static GameObject ResolveUniqueTarget(string target, string requiredMessage)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", requiredMessage, "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            if (matches.Count == 0)
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Target not found: " + target, "Run alps-unity scene hierarchy or pass a hierarchy path / instance id.");
            }
            return matches[0];
        }

        private static string GameObjectTargetJson(GameObject gameObject)
        {
            return "{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + "}";
        }


        private static string CodeExec(BridgeRequest request)
        {
            var startedAt = DateTime.UtcNow;
            var source = request.StringArg("sourceText");
            var sourcePath = request.StringArg("sourcePath");
            if (string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(sourcePath))
            {
                try
                {
                    source = File.ReadAllText(sourcePath, Encoding.UTF8);
                }
                catch (Exception exception)
                {
                    return ErrorJson("E_CODE_SOURCE_INVALID", "Code source is not readable: " + exception.Message, "Check that --file points to a UTF-8 C# source file.");
                }
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                return ErrorJson("E_CODE_SOURCE_INVALID", "Code source is empty.", "Pass --file, --stdin or --code with non-empty C# source.");
            }

            var result = ExecuteCodeSource(request, source, startedAt);
            WriteLog("code_exec requestId=" + request.RequestId + " ok=" + result.Ok + " exitState=" + result.ExitState);
            if (!result.Ok)
            {
                return ErrorJsonWithDetails(result.ErrorCode, result.ErrorMessage, result.DetailsJson, result.NextAction);
            }
            return SuccessJson(result.DataJson);
        }

        private static CodeExecutionResult ExecuteCodeSource(BridgeRequest request, string source, DateTime startedAt)
        {
            var requestId = request.RequestId;
            var argsJson = request.StringArg("argsJson");
            var provider = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateExecutable = false,
                GenerateInMemory = true,
                TreatWarningsAsErrors = false
            };
            var references = DefaultCodeReferences(request.StringArg("refs")).ToList();
            var referenceError = ValidateCodeReferences(references);
            if (!string.IsNullOrEmpty(referenceError))
            {
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", referenceError, "{\"referenceError\":" + Quote(referenceError) + "}", "Check Unity/Editor/project assembly references and --refs.");
            }
            foreach (var reference in references)
            {
                parameters.ReferencedAssemblies.Add(reference);
            }
            foreach (var define in SplitCsv(request.StringArg("defines")))
            {
                parameters.CompilerOptions += " /define:" + define;
            }

            CompilerResults compiled;
            try
            {
                compiled = provider.CompileAssemblyFromSource(parameters, WrapCodeSource(source));
            }
            catch (Exception exception)
            {
                return CodeExecutionResult.Fail("E_REFERENCE_RESOLUTION_FAILED", "Failed to compile Runtime Code: " + exception.Message, CodeDetailsJson("[]", Quote(exception.GetType().Name), Quote(exception.Message), Quote(exception.StackTrace), "", ""), "Check Unity/Editor/project assembly references and --refs.");
            }

            if (compiled.Errors.HasErrors)
            {
                var diagnostics = CompilerDiagnosticsJson(compiled.Errors);
                return CodeExecutionResult.Fail("E_CODE_COMPILE_FAILED", "Runtime Code compilation failed.", "{\"diagnostics\":" + diagnostics + "}", "Fix compiler diagnostics and rerun code exec.");
            }

            var stdout = new StringWriter(CultureInfo.InvariantCulture);
            var stderr = new StringWriter(CultureInfo.InvariantCulture);
            var previousOut = Console.Out;
            var previousError = Console.Error;
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var type = compiled.CompiledAssembly.GetType("AlpsUnityCli.RuntimeCode.Script", true);
                var method = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
                var returnValue = method.Invoke(null, new object[] { argsJson ?? "{}" });
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var endedAt = DateTime.UtcNow;
                var dataJson = CodeSuccessDataJson(request, startedAt, endedAt, stdout.ToString(), stderr.ToString(), returnValue);
                return CodeExecutionResult.Success(dataJson);
            }
            catch (TargetInvocationException exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                var inner = exception.InnerException ?? exception;
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", inner.GetType().FullName + ": " + inner.Message, CodeDetailsJson("[]", Quote(inner.GetType().FullName), Quote(inner.Message), Quote(inner.StackTrace), stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
            catch (Exception exception)
            {
                Console.SetOut(previousOut);
                Console.SetError(previousError);
                return CodeExecutionResult.Fail("E_CODE_RUNTIME_EXCEPTION", exception.GetType().FullName + ": " + exception.Message, CodeDetailsJson("[]", Quote(exception.GetType().FullName), Quote(exception.Message), Quote(exception.StackTrace), stdout.ToString(), stderr.ToString()), "Inspect runtime stdout/stderr and exception stackTrace.");
            }
        }

        private static string WrapCodeSource(string source)
        {
            if (Regex.IsMatch(source, "\\bpublic\\s+static\\s+object\\s+Run\\s*\\("))
            {
                return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace AlpsUnityCli.RuntimeCode { public static class Script { " + source + " } }";
            }
            return "using System; using System.IO; using System.Linq; using System.Collections.Generic; using UnityEngine; using UnityEditor; namespace AlpsUnityCli.RuntimeCode { public static class Script { public static object Run(string argsJson) { " + source + " } } }";
        }

        private static IEnumerable<string> DefaultCodeReferences(string refsCsv)
        {
            var references = new List<string>();
            AddReference(references, typeof(object).Assembly.Location);
            AddReference(references, typeof(Enumerable).Assembly.Location);
            AddReference(references, typeof(UnityEngine.Object).Assembly.Location);
            AddReference(references, typeof(EditorApplication).Assembly.Location);
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "netstandard.dll"));
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.dll"));
            AddReferenceIfExists(references, Path.Combine(EditorApplication.applicationContentsPath, "Managed", "UnityEngine", "UnityEngine.CoreModule.dll"));
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)) continue;
                var name = assembly.GetName().Name;
                if (name.Equals("netstandard", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase))
                {
                    AddReference(references, assembly.Location);
                }
            }
            var scriptAssemblies = Path.Combine(ProjectRoot(), "Library", "ScriptAssemblies");
            if (Directory.Exists(scriptAssemblies))
            {
                foreach (var assemblyPath in Directory.GetFiles(scriptAssemblies, "*.dll"))
                {
                    AddReference(references, assemblyPath);
                }
            }
            foreach (var reference in SplitCsv(refsCsv))
            {
                AddReference(references, reference);
            }
            return references;
        }

        private static void AddReference(List<string> references, string reference)
        {
            if (string.IsNullOrEmpty(reference)) return;
            if (!references.Contains(reference)) references.Add(reference);
        }

        private static void AddReferenceIfExists(List<string> references, string reference)
        {
            if (!string.IsNullOrEmpty(reference) && File.Exists(reference)) AddReference(references, reference);
        }

        private static string ValidateCodeReferences(IEnumerable<string> references)
        {
            foreach (var reference in references)
            {
                try
                {
                    if (!File.Exists(reference)) return "Runtime Code reference was not found: " + reference;
                    using (File.Open(reference, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                }
                catch (Exception exception)
                {
                    return "Runtime Code reference is not readable: " + reference + " (" + exception.Message + ")";
                }
            }
            return null;
        }

        private static IEnumerable<string> SplitCsv(string value)
        {
            if (string.IsNullOrEmpty(value)) yield break;
            var parts = value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (!string.IsNullOrEmpty(trimmed)) yield return trimmed;
            }
        }

        private static string CompilerDiagnosticsJson(CompilerErrorCollection errors)
        {
            var items = new List<string>();
            foreach (CompilerError error in errors)
            {
                items.Add("{\"severity\":" + Quote(error.IsWarning ? "warning" : "error") + ",\"id\":" + Quote(error.ErrorNumber) + ",\"message\":" + Quote(error.ErrorText) + ",\"file\":" + Quote(error.FileName) + ",\"line\":" + error.Line.ToString(CultureInfo.InvariantCulture) + ",\"column\":" + error.Column.ToString(CultureInfo.InvariantCulture) + ",\"span\":null}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string CodeSuccessDataJson(BridgeRequest request, DateTime startedAt, DateTime endedAt, string stdout, string stderr, object returnValue)
        {
            var timeoutMs = request.OptionalIntArg("timeoutMs");
            var timeoutJson = timeoutMs.HasValue ? timeoutMs.Value.ToString(CultureInfo.InvariantCulture) : "null";
            var argsJson = request.StringArg("argsJson");
            return "{\"command\":\"code.exec\",\"mode\":\"live\",\"execution\":{\"channel\":\"live\",\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",\"timeoutMs\":" + timeoutJson + ",\"exitState\":\"completed\"},\"compile\":{\"ok\":true,\"diagnostics\":[]},\"runtime\":{\"ok\":true,\"returnValue\":" + ReturnValueJson(returnValue) + ",\"stdout\":" + Quote(stdout) + ",\"stderr\":" + Quote(stderr) + "},\"artifacts\":[],\"audit\":{\"requestId\":" + Quote(request.RequestId) + ",\"projectPath\":" + Quote(ProjectRoot()) + ",\"instanceId\":" + Quote(InstanceId()) + ",\"unityVersion\":" + Quote(Application.unityVersion) + ",\"bridgeVersion\":\"com.alps.unity-cli.bridge\",\"initiator\":\"cli.live\",\"input\":{\"sourceKind\":" + Quote(request.StringArg("sourceKind")) + ",\"sourcePath\":" + Quote(request.StringArg("sourcePath")) + ",\"sourceSha256\":" + Quote(request.StringArg("sourceSha256")) + ",\"args\":" + (string.IsNullOrEmpty(argsJson) ? "{}" : argsJson) + "},\"execution\":{\"timeoutMs\":" + timeoutJson + ",\"exitState\":\"completed\"},\"artifacts\":[],\"startedAt\":" + Quote(startedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"endedAt\":" + Quote(endedAt.ToString("O", CultureInfo.InvariantCulture)) + ",\"durationMs\":" + ((long)(endedAt - startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + ",\"effects\":{\"fileWrites\":[],\"assetRefresh\":false,\"domainReload\":false,\"playModeChanged\":false,\"packagesChanged\":false}},\"logs\":{\"bridge\":" + Quote(LogPath()) + "},\"nextActions\":[]}";
        }

        private static string CodeDetailsJson(string diagnosticsJson, string exceptionTypeJson, string messageJson, string stackTraceJson, string stdout, string stderr)
        {
            return "{\"diagnostics\":" + diagnosticsJson + ",\"exceptionType\":" + exceptionTypeJson + ",\"message\":" + messageJson + ",\"stackTrace\":" + stackTraceJson + ",\"stdout\":" + Quote(stdout) + ",\"stderr\":" + Quote(stderr) + "}";
        }

        private static string ReturnValueJson(object value)
        {
            if (value == null) return "null";
            if (value is bool) return Bool((bool)value);
            if (value is int || value is long || value is float || value is double || value is decimal) return Convert.ToString(value, CultureInfo.InvariantCulture);
            return Quote(Convert.ToString(value, CultureInfo.InvariantCulture));
        }


        private static string EditorStateJson()
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            return $"\"playMode\":{{\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)},\"isPlayingOrWillChangePlaymode\":{Bool(EditorApplication.isPlayingOrWillChangePlaymode)}}},\"busy\":{{\"isBusy\":{Bool(EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || BuildPipeline.isBuildingPlayer)},\"compiling\":{Bool(EditorApplication.isCompiling)},\"updating\":{Bool(EditorApplication.isUpdating)},\"building\":{Bool(BuildPipeline.isBuildingPlayer)},\"domainReloading\":{Bool(EditorApplication.isCompiling)},\"playModeChanging\":{Bool(EditorApplication.isPlayingOrWillChangePlaymode)}}},\"activeScene\":{{\"name\":{Quote(scene.name)},\"path\":{Quote(scene.path)},\"isLoaded\":{Bool(scene.isLoaded)},\"isDirty\":{Bool(scene.isDirty)}}},\"prefabStage\":{PrefabStageJson(prefabStage)},\"plannedEffects\":[]";
        }

        private static string EditorPlayJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.play");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = true;
            WaitForPlayModeState(true, TimeoutMs(request));
            return $"\"plannedEffects\":[{{\"type\":\"editor.play\",\"from\":{Bool(before)},\"to\":true}}],\"result\":{{\"requested\":\"play\",\"changed\":{Bool(before != EditorApplication.isPlaying)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{{{EditorStateJson()}}}";
        }

        private static string EditorPauseJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.pause");
            var before = EditorApplication.isPaused;
            EditorApplication.isPaused = !EditorApplication.isPaused;
            return $"\"plannedEffects\":[{{\"type\":\"editor.pause\",\"from\":{Bool(before)},\"to\":{Bool(EditorApplication.isPaused)}}}],\"result\":{{\"requested\":\"pause\",\"changed\":{Bool(before != EditorApplication.isPaused)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{{{EditorStateJson()}}}";
        }

        private static string EditorStopJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.stop");
            var before = EditorApplication.isPlaying;
            EditorApplication.isPaused = false;
            EditorApplication.isPlaying = false;
            WaitForPlayModeState(false, TimeoutMs(request));
            return $"\"plannedEffects\":[{{\"type\":\"editor.stop\",\"from\":{Bool(before)},\"to\":false}}],\"result\":{{\"requested\":\"stop\",\"changed\":{Bool(before != EditorApplication.isPlaying)},\"isPlaying\":{Bool(EditorApplication.isPlaying)},\"isPaused\":{Bool(EditorApplication.isPaused)}}},\"state\":{{{EditorStateJson()}}}";
        }

        private static string EditorMenuJson(BridgeRequest request)
        {
            EnsureEditorNotBusy("editor.menu");
            var path = request.StringArg("path");
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new BridgeCommandException("E_SCHEMA_INVALID", "Editor menu path is required.", "Use alps-unity editor menu <path>, for example File/Save.");
            }

            var startedAt = DateTime.UtcNow;
            var executed = EditorApplication.ExecuteMenuItem(path);
            if (!executed)
            {
                throw new BridgeCommandException("E_MENU_NOT_FOUND", "Unity menu item was not found or could not be executed.", "Use the exact Unity Editor menu path, such as File/Save or Assets/Refresh.");
            }

            var elapsedMs = (int)Math.Round((DateTime.UtcNow - startedAt).TotalMilliseconds);
            var timeoutMs = TimeoutMs(request);
            if (elapsedMs > timeoutMs)
            {
                throw new BridgeCommandException("E_MENU_TIMEOUT", "Unity menu item execution exceeded the requested timeout.", "Check Unity Editor and Alps Bridge logs for long-running menu work.");
            }

            return $"\"plannedEffects\":[{{\"type\":\"editor.menu\",\"path\":{Quote(path)}}}],\"result\":{{\"path\":{Quote(path)},\"executed\":true,\"elapsedMs\":{elapsedMs}}},\"summary\":{{\"affected\":\"Unity Editor menu item executed\"}},\"state\":{{{EditorStateJson()}}}";
        }

        private static string PrefabStageJson(object prefabStage)
        {
            if (prefabStage == null)
            {
                return "null";
            }
            var type = prefabStage.GetType();
            var assetPath = type.GetProperty("assetPath")?.GetValue(prefabStage)?.ToString();
            var sceneValue = type.GetProperty("scene")?.GetValue(prefabStage);
            var scenePath = sceneValue is Scene scene ? scene.path : null;
            return $"{{\"assetPath\":{Quote(assetPath)},\"scenePath\":{Quote(scenePath)}}}";
        }

        private static int TimeoutMs(BridgeRequest request)
        {
            var value = request.OptionalIntArg("timeoutMs");
            return value > 0 ? Math.Min(value.Value, 7000) : 7000;
        }

        private static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || BuildPipeline.isBuildingPlayer || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new BridgeCommandException("E_UNITY_BUSY", "Unity Editor is busy and cannot run this editor command right now.", "Wait for compiling, asset importing, build, PlayMode switching, or domain reload to complete, then retry.");
            }
        }

        private static void WaitForPlayModeState(bool expectedPlaying, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow <= deadline)
            {
                var reached = expectedPlaying ? (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) : (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode);
                if (reached)
                {
                    return;
                }
                Thread.Sleep(25);
            }
            throw new BridgeCommandException("E_PLAYMODE_TIMEOUT", "Unity PlayMode did not reach the requested state before the timeout.", "Wait for PlayMode switching or domain reload to finish, then retry.");
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

        private static string RequireMaterialPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_MATERIAL_PATH_REQUIRED", "Pass an Assets/*.mat path.", "Use alps-unity material info <path> or pass --path Assets/Materials/Example.mat.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_MATERIAL_PATH_INVALID", "Material path must be under Assets/ and end with .mat: " + path, "Pass a Unity asset path such as Assets/Materials/Player.mat.");
            }
            return path;
        }

        private static Material LoadMaterialAsset(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new BridgeCommandException("E_MATERIAL_NOT_FOUND", "Material asset not found: " + path, "Check the path or run alps-unity asset search --type Material.");
            }
            return material;
        }

        private static GameObject ResolveUniqueGameObject(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new BridgeCommandException("E_TARGET_NOT_FOUND", "Material assign requires --target <go>.", "Pass a GameObject name, hierarchy path or instance id.");
            }
            var matches = new List<GameObject>();
            foreach (var gameObject in UnityEngine.Object.FindObjectsOfType<GameObject>())
            {
                if (gameObject.name == target || gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) == target || HierarchyPath(gameObject) == target)
                {
                    matches.Add(gameObject);
                }
            }
            if (matches.Count > 1)
            {
                throw new BridgeCommandException("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched: " + target, "Pass a hierarchy path or instance id.");
            }
            return matches.Count == 1 ? matches[0] : null;
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

        private static string MaterialJson(string path, Material material)
        {
            var shader = material.shader;
            var dependencies = AssetDatabase.GetDependencies(path, false);
            return "{"
                + "\"path\":" + Quote(path) + ","
                + "\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(path)) + ","
                + "\"name\":" + Quote(material.name) + ","
                + "\"shader\":" + Quote(shader == null ? null : shader.name) + ","
                + "\"renderQueue\":" + material.renderQueue.ToString(CultureInfo.InvariantCulture) + ","
                + "\"keywords\":[" + string.Join(",", QuoteArray(material.shaderKeywords).ToArray()) + "],"
                + "\"properties\":" + MaterialPropertiesJson(material) + ","
                + "\"textureReferences\":" + MaterialTextureReferencesJson(material) + ","
                + "\"dependencies\":" + DependencySummaryJson(dependencies)
                + "}";
        }

        private static List<string> QuoteArray(string[] values)
        {
            var items = new List<string>();
            foreach (var value in values)
            {
                items.Add(Quote(value));
            }
            return items;
        }

        private static string MaterialPropertiesJson(Material material)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return "{\"count\":0,\"items\":[]}";
            }
            var items = new List<string>();
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                var type = ShaderUtil.GetPropertyType(shader, index).ToString();
                var description = ShaderUtil.GetPropertyDescription(shader, index);
                var valueJson = material.HasProperty(name) ? MaterialPropertyValueJson(material, name, NormalizeMaterialValueType(type)) : "null";
                items.Add("{\"name\":" + Quote(name) + ",\"displayName\":" + Quote(description) + ",\"type\":" + Quote(type) + ",\"value\":" + valueJson + "}");
            }
            return "{\"count\":" + count.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static string MaterialTextureReferencesJson(Material material)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return "{\"count\":0,\"items\":[]}";
            }
            var items = new List<string>();
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                var name = ShaderUtil.GetPropertyName(shader, index);
                if (NormalizeMaterialValueType(ShaderUtil.GetPropertyType(shader, index).ToString()) != "texture" || !material.HasProperty(name))
                {
                    continue;
                }
                var texture = material.GetTexture(name);
                var texturePath = texture == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                items.Add("{\"property\":" + Quote(name) + ",\"path\":" + Quote(texturePath) + ",\"guid\":" + Quote(string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath)) + "}");
            }
            return "{\"count\":" + items.Count.ToString(CultureInfo.InvariantCulture) + ",\"items\":[" + string.Join(",", items.ToArray()) + "]}";
        }

        private static string MaterialPropertyType(Material material, string propertyName)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return null;
            }
            var count = ShaderUtil.GetPropertyCount(shader);
            for (var index = 0; index < count; index++)
            {
                if (ShaderUtil.GetPropertyName(shader, index) == propertyName)
                {
                    return ShaderUtil.GetPropertyType(shader, index).ToString();
                }
            }
            return null;
        }

        private static string NormalizeMaterialValueType(string type)
        {
            if (string.IsNullOrEmpty(type))
            {
                return null;
            }
            var normalized = type.ToLowerInvariant();
            if (normalized == "color")
            {
                return "color";
            }
            if (normalized == "float" || normalized == "range")
            {
                return "float";
            }
            if (normalized == "vector")
            {
                return "vector";
            }
            if (normalized == "texture" || normalized == "texenv" || normalized == "texture2d")
            {
                return "texture";
            }
            return null;
        }

        private static bool TypesCompatible(string shaderType, string requestedType)
        {
            var inferred = NormalizeMaterialValueType(shaderType);
            return string.IsNullOrEmpty(inferred) || inferred == requestedType;
        }

        private static string ApplyMaterialValue(Material material, string propertyName, string type, string value)
        {
            if (type == "color")
            {
                var color = ParseColor(value);
                material.SetColor(propertyName, color);
                return ColorJson(color);
            }
            if (type == "float")
            {
                var number = ParseFloat(value, "E_MATERIAL_VALUE_INVALID", "Pass a numeric value for float/range Material properties.");
                material.SetFloat(propertyName, number);
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (type == "vector")
            {
                var vector = ParseVector(value);
                material.SetVector(propertyName, vector);
                return VectorJson(vector);
            }
            if (type == "texture")
            {
                var texturePath = NormalizeAssetPath(value);
                Texture texture = null;
                if (!string.IsNullOrEmpty(texturePath) && !string.Equals(texturePath, "null", StringComparison.OrdinalIgnoreCase))
                {
                    texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
                    if (texture == null)
                    {
                        throw new BridgeCommandException("E_TEXTURE_NOT_FOUND", "Texture asset not found: " + texturePath, "Pass an existing Assets texture path or value null to clear the texture.");
                    }
                }
                material.SetTexture(propertyName, texture);
                return "{\"path\":" + Quote(texturePath) + ",\"guid\":" + Quote(texture == null ? null : AssetDatabase.AssetPathToGUID(texturePath)) + "}";
            }
            throw new BridgeCommandException("E_MATERIAL_PROPERTY_TYPE_UNSUPPORTED", "Unsupported Material property value type: " + type, "Pass --type color|float|vector|texture.");
        }

        private static string MaterialPropertyValueJson(Material material, string propertyName, string type)
        {
            try
            {
                if (type == "color")
                {
                    return ColorJson(material.GetColor(propertyName));
                }
                if (type == "float")
                {
                    return material.GetFloat(propertyName).ToString(CultureInfo.InvariantCulture);
                }
                if (type == "vector")
                {
                    return VectorJson(material.GetVector(propertyName));
                }
                if (type == "texture")
                {
                    var texture = material.GetTexture(propertyName);
                    var texturePath = texture == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(texture));
                    return "{\"path\":" + Quote(texturePath) + ",\"guid\":" + Quote(string.IsNullOrEmpty(texturePath) ? null : AssetDatabase.AssetPathToGUID(texturePath)) + "}";
                }
            }
            catch
            {
            }
            return "null";
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a color value.", "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
            }
            Color color;
            if (ColorUtility.TryParseHtmlString(value, out color))
            {
                return color;
            }
            var parts = value.Split(',');
            if (parts.Length == 3 || parts.Length == 4)
            {
                return new Color(ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric."), parts.Length == 4 ? ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Color values must be numeric.") : 1f);
            }
            throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Invalid color value: " + value, "Use #RRGGBB, #RRGGBBAA or comma-separated r,g,b[,a].");
        }

        private static Vector4 ParseVector(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Pass a vector value.", "Use comma-separated x,y,z,w numbers.");
            }
            var parts = value.Split(',');
            if (parts.Length != 4)
            {
                throw new BridgeCommandException("E_MATERIAL_VALUE_INVALID", "Vector values require four numbers: " + value, "Use comma-separated x,y,z,w numbers.");
            }
            return new Vector4(ParseFloat(parts[0], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[1], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[2], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."), ParseFloat(parts[3], "E_MATERIAL_VALUE_INVALID", "Vector values must be numeric."));
        }

        private static float ParseFloat(string value, string code, string nextAction)
        {
            float parsed;
            if (!float.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException(code, "Invalid numeric Material value: " + value, nextAction);
            }
            return parsed;
        }

        private static string ColorJson(Color color)
        {
            return "{\"r\":" + color.r.ToString(CultureInfo.InvariantCulture) + ",\"g\":" + color.g.ToString(CultureInfo.InvariantCulture) + ",\"b\":" + color.b.ToString(CultureInfo.InvariantCulture) + ",\"a\":" + color.a.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string VectorJson(Vector4 vector)
        {
            return "{\"x\":" + vector.x.ToString(CultureInfo.InvariantCulture) + ",\"y\":" + vector.y.ToString(CultureInfo.InvariantCulture) + ",\"z\":" + vector.z.ToString(CultureInfo.InvariantCulture) + ",\"w\":" + vector.w.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string MaterialMutationJson(string type, string path, string materialJson)
        {
            return "{\"material\":" + materialJson + ",\"plannedEffects\":[{\"type\":" + Quote(type) + ",\"path\":" + Quote(path) + "}],\"logPath\":" + Quote(LogPath()) + "}";
        }

        private static string MaterialAssignJson(GameObject gameObject, string materialPath, string componentType, int slot)
        {
            return "{"
                + "\"target\":{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + "},"
                + "\"material\":{\"path\":" + Quote(materialPath) + ",\"guid\":" + Quote(AssetDatabase.AssetPathToGUID(materialPath)) + "},"
                + "\"componentType\":" + Quote(componentType) + ","
                + "\"slot\":" + slot.ToString(CultureInfo.InvariantCulture) + ","
                + "\"plannedEffects\":[{\"type\":\"material.assign\",\"target\":" + Quote(HierarchyPath(gameObject)) + ",\"material\":" + Quote(materialPath) + "}],"
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

        private static string TargetAmbiguousErrorJson(string label, string target, TargetResolution resolution, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":\"E_TARGET_AMBIGUOUS\",\"message\":" + Quote(label + " target is ambiguous: " + target) + ",\"target\":" + Quote(target) + ",\"matchCount\":" + resolution.Matches.Count.ToString(CultureInfo.InvariantCulture) + ",\"matches\":" + TargetMatchesJson(resolution.Matches) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
        }

        private static string TargetMatchesJson(List<GameObject> matches)
        {
            var items = new List<string>();
            var limit = Math.Min(matches.Count, 10);
            for (var index = 0; index < limit; index++)
            {
                var gameObject = matches[index];
                items.Add("{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + ",\"components\":" + ComponentNamesJson(gameObject) + "}");
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Quote(string value)
        {
            if (value == null)
            {
                return "null";
            }
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }

        private static string JsonLiteralOrString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "null";
            }
            var trimmed = value.Trim();
            if ((trimmed.StartsWith("{") && trimmed.EndsWith("}")) || (trimmed.StartsWith("[") && trimmed.EndsWith("]")) || trimmed == "true" || trimmed == "false" || trimmed == "null" || Regex.IsMatch(trimmed, "^-?\\d+(?:\\.\\d+)?$"))
            {
                return trimmed;
            }
            return Quote(value);
        }

        private static string StringArrayJson(string[] values)
        {
            var items = new List<string>();
            foreach (var value in values ?? new string[0])
            {
                if (!string.IsNullOrEmpty(value))
                {
                    items.Add(Quote(value));
                }
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string JsonRawProperty(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return null;
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return null;
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != (char)34) return null;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) return null;
                var key = JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') return null;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) return null;
                if (string.Equals(key, name, StringComparison.Ordinal)) return trimmed.Substring(index, valueEnd - index).Trim();
                index = valueEnd;
            }
            return null;
        }

        private static string JsonStringProperty(string json, string name)
        {
            var raw = JsonRawProperty(json, name);
            if (string.IsNullOrEmpty(raw)) return null;
            var trimmed = raw.Trim();
            return trimmed.Length >= 2 && trimmed[0] == (char)34 && trimmed[trimmed.Length - 1] == (char)34 ? JsonUnescape(trimmed.Substring(1, trimmed.Length - 2)) : null;
        }

        private static string[] JsonStringArray(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var values = new List<string>();
            foreach (Match match in Regex.Matches(json, "\\\"((?:\\\\.|[^\\\"])*)\\\""))
            {
                values.Add(JsonUnescape(match.Groups[1].Value));
            }
            return values.ToArray();
        }

        private static string[] JsonObjectPropertyNames(string json)
        {
            if (string.IsNullOrEmpty(json)) return new string[0];
            var names = new List<string>();
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return names.ToArray();
            var index = 1;
            while (index < trimmed.Length - 1)
            {
                while (index < trimmed.Length && (char.IsWhiteSpace(trimmed[index]) || trimmed[index] == ',')) index++;
                if (index >= trimmed.Length - 1 || trimmed[index] != (char)34) break;
                var keyEnd = FindJsonStringEnd(trimmed, index);
                if (keyEnd <= index) break;
                names.Add(JsonUnescape(trimmed.Substring(index + 1, keyEnd - index - 1)));
                index = keyEnd + 1;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                if (index >= trimmed.Length || trimmed[index] != ':') break;
                index++;
                while (index < trimmed.Length && char.IsWhiteSpace(trimmed[index])) index++;
                var valueEnd = FindJsonValueEnd(trimmed, index);
                if (valueEnd <= index) break;
                index = valueEnd;
            }
            return names.ToArray();
        }

        private static bool JsonValueMatchesType(string json, string expectedType)
        {
            var value = (json ?? string.Empty).Trim();
            if (expectedType == "object") return value.StartsWith("{") && value.EndsWith("}");
            if (expectedType == "array") return value.StartsWith("[") && value.EndsWith("]");
            if (expectedType == "string") return value.StartsWith("\"") && value.EndsWith("\"");
            if (expectedType == "boolean") return value == "true" || value == "false";
            if (expectedType == "null") return value == "null";
            if (expectedType == "integer") return Regex.IsMatch(value, "^-?\\d+$");
            if (expectedType == "number") return Regex.IsMatch(value, "^-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?$");
            return true;
        }

        private static int FindJsonStringEnd(string json, int start)
        {
            var escaping = false;
            for (var index = start + 1; index < json.Length; index++)
            {
                var ch = json[index];
                if (escaping) escaping = false;
                else if (ch == '\\') escaping = true;
                else if (ch == (char)34) return index;
            }
            return -1;
        }

        private static int FindJsonValueEnd(string json, int start)
        {
            var depth = 0;
            var inString = false;
            var escaping = false;
            for (var index = start; index < json.Length; index++)
            {
                var ch = json[index];
                if (inString)
                {
                    if (escaping) escaping = false;
                    else if (ch == '\\') escaping = true;
                    else if (ch == (char)34) inString = false;
                    continue;
                }
                if (ch == (char)34) { inString = true; continue; }
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']')
                {
                    if (depth == 0) return index;
                    depth--;
                    if (depth == 0) return index + 1;
                    continue;
                }
                if (depth == 0 && ch == ',') return index;
            }
            return json.Length;
        }

        private static string JsonUnescape(string value)
        {
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\/", "/").Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        }

        private static string ResolveArtifactDir(string rawPath, string requestId)
        {
            if (!string.IsNullOrEmpty(rawPath))
            {
                var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
            }
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(ProjectRoot(), "Library", "AlpsUnityCLI");
            }
            return Path.Combine(root, "AlpsUnityCLI", "artifacts", requestId ?? Guid.NewGuid().ToString("N"));
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Number(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string Vector2Json(Vector2 value)
        {
            return "{\"x\":" + Number(value.x) + ",\"y\":" + Number(value.y) + "}";
        }

        private static string Vector3Json(Vector3 value)
        {
            return "{\"x\":" + Number(value.x) + ",\"y\":" + Number(value.y) + ",\"z\":" + Number(value.z) + "}";
        }

        private static string EffectJson(string type, string property, string value)
        {
            return "{\"type\":" + Quote(type) + ",\"property\":" + Quote(property) + ",\"value\":" + Quote(value) + "}";
        }

        private static string GraphicsSummaryJson()
        {
            var pipeline = GraphicsSettings.defaultRenderPipeline;
            var pipelinePath = pipeline == null ? null : NormalizeAssetPath(AssetDatabase.GetAssetPath(pipeline));
            return "{\"renderPipeline\":{\"active\":" + Quote(RenderPipelineSummaryName()) + ",\"defaultAsset\":" + Quote(pipeline == null ? null : pipeline.name) + ",\"defaultAssetPath\":" + Quote(pipelinePath) + "},\"currentQualityLevel\":" + QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture) + ",\"qualityLevelName\":" + Quote(QualitySettings.names.Length > QualitySettings.GetQualityLevel() ? QualitySettings.names[QualitySettings.GetQualityLevel()] : null) + "}";
        }

        private static string RenderPipelineSummaryName()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                return GraphicsSettings.currentRenderPipeline.GetType().Name;
            }
            return GraphicsSettings.defaultRenderPipeline != null ? GraphicsSettings.defaultRenderPipeline.GetType().Name : "Built-in";
        }

        private static string QualitySettingsJson()
        {
            var names = new List<string>();
            foreach (var name in QualitySettings.names)
            {
                names.Add(Quote(name));
            }
            return "{\"names\":[" + string.Join(",", names.ToArray()) + "],\"currentLevel\":" + QualitySettings.GetQualityLevel().ToString(CultureInfo.InvariantCulture) + ",\"vSyncCount\":" + QualitySettings.vSyncCount.ToString(CultureInfo.InvariantCulture) + ",\"antiAliasing\":" + QualitySettings.antiAliasing.ToString(CultureInfo.InvariantCulture) + ",\"shadowDistance\":" + Number(QualitySettings.shadowDistance) + ",\"masterTextureLimit\":" + QualitySettings.masterTextureLimit.ToString(CultureInfo.InvariantCulture) + ",\"lodBias\":" + Number(QualitySettings.lodBias) + ",\"maximumLODLevel\":" + QualitySettings.maximumLODLevel.ToString(CultureInfo.InvariantCulture) + ",\"anisotropicFiltering\":" + Quote(QualitySettings.anisotropicFiltering.ToString()) + ",\"shadows\":" + Quote(QualitySettings.shadows.ToString()) + ",\"shadowResolution\":" + Quote(QualitySettings.shadowResolution.ToString()) + ",\"realtimeReflectionProbes\":" + Bool(QualitySettings.realtimeReflectionProbes) + ",\"softParticles\":" + Bool(QualitySettings.softParticles) + "}";
        }

        private static string PhysicsSettingsJson()
        {
            return "{\"mode\":\"3d+2d\",\"gravity\":" + Vector3Json(Physics.gravity) + ",\"gravity2d\":" + Vector2Json(Physics2D.gravity) + ",\"defaultContactOffset\":" + Number(Physics.defaultContactOffset) + ",\"bounceThreshold\":" + Number(Physics.bounceThreshold) + ",\"defaultSolverIterations\":" + Physics.defaultSolverIterations.ToString(CultureInfo.InvariantCulture) + ",\"defaultSolverVelocityIterations\":" + Physics.defaultSolverVelocityIterations.ToString(CultureInfo.InvariantCulture) + ",\"queriesHitTriggers\":" + Bool(Physics.queriesHitTriggers) + ",\"queriesHitBackfaces\":" + Bool(Physics.queriesHitBackfaces) + ",\"queriesStartInColliders\":" + Bool(Physics2D.queriesStartInColliders) + ",\"autoSimulation\":" + Bool(Physics.autoSimulation) + "}";
        }

        private static void SetQualityLevel(int level, List<string> effects)
        {
            if (level < 0 || level >= QualitySettings.names.Length)
            {
                throw new BridgeCommandException("E_QUALITY_LEVEL_INVALID", "Quality level is out of range: " + level.ToString(CultureInfo.InvariantCulture), "Run alps-unity graphics quality to inspect available quality level names and indexes.");
            }
            QualitySettings.SetQualityLevel(level, true);
            effects.Add(EffectJson("graphics.quality", "qualityLevel", level.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyGenericGraphicsSetting(BridgeRequest request, List<string> effects)
        {
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (property == "vSyncCount")
            {
                QualitySettings.vSyncCount = ParseInt(value, property);
            }
            else if (property == "antiAliasing")
            {
                QualitySettings.antiAliasing = ParseInt(value, property);
            }
            else if (property == "shadowDistance")
            {
                QualitySettings.shadowDistance = ParseFloat(value, property);
            }
            else
            {
                throw new BridgeCommandException("E_GRAPHICS_SETTING_UNSUPPORTED", "Unsupported graphics setting: " + property, "Use named flags such as --anti-aliasing, --shadow-distance or --render-pipeline.");
            }
            effects.Add(EffectJson("graphics.settings", property, value));
        }

        private static void ApplyGenericPhysicsSetting(BridgeRequest request, List<string> effects)
        {
            var property = request.StringArg("property");
            var value = request.StringArg("value");
            if (property == "gravityY")
            {
                Physics.gravity = new Vector3(Physics.gravity.x, ParseFloat(value, property), Physics.gravity.z);
            }
            else if (property == "defaultContactOffset")
            {
                Physics.defaultContactOffset = ParseFloat(value, property);
            }
            else if (property == "queriesHitTriggers")
            {
                Physics.queriesHitTriggers = ParseBool(value, property);
            }
            else
            {
                throw new BridgeCommandException("E_PHYSICS_SETTING_UNSUPPORTED", "Unsupported physics setting: " + property, "Use named flags such as --gravity-y, --default-contact-offset or --queries-hit-triggers.");
            }
            effects.Add(EffectJson("physics.settings", property, value));
        }

        private static void ApplyQualityInt(BridgeRequest request, string arg, string property, Action<int> setter, List<string> effects)
        {
            var value = request.OptionalIntArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, value.Value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyQualityFloat(BridgeRequest request, string arg, string property, Action<float> setter, List<string> effects)
        {
            var value = request.OptionalFloatArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, Number(value.Value)));
        }

        private static void ApplyQualityBool(BridgeRequest request, string arg, string property, Action<bool> setter, List<string> effects)
        {
            var value = request.OptionalBoolArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("graphics.quality", property, Bool(value.Value)));
        }

        private static void ApplyQualityEnum<T>(BridgeRequest request, string arg, string property, Action<T> setter, List<string> effects) where T : struct
        {
            if (!request.HasArg(arg)) return;
            T parsed;
            if (!Enum.TryParse<T>(request.StringArg(arg), true, out parsed))
            {
                throw new BridgeCommandException("E_GRAPHICS_SETTING_INVALID", "Invalid " + property + " value: " + request.StringArg(arg), "Use a Unity enum value for " + property + ".");
            }
            setter(parsed);
            effects.Add(EffectJson("graphics.quality", property, parsed.ToString()));
        }

        private static void ApplyPhysicsInt(BridgeRequest request, string arg, string property, Action<int> setter, List<string> effects)
        {
            var value = request.OptionalIntArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, value.Value.ToString(CultureInfo.InvariantCulture)));
        }

        private static void ApplyPhysicsFloat(BridgeRequest request, string arg, string property, Action<float> setter, List<string> effects)
        {
            var value = request.OptionalFloatArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, Number(value.Value)));
        }

        private static void ApplyPhysicsBool(BridgeRequest request, string arg, string property, Action<bool> setter, List<string> effects)
        {
            var value = request.OptionalBoolArg(arg);
            if (!value.HasValue) return;
            setter(value.Value);
            effects.Add(EffectJson("physics.settings", property, Bool(value.Value)));
        }

        private static void ApplyPhysicsVector3(BridgeRequest request, string property, List<string> effects)
        {
            var x = request.OptionalFloatArg(property + "X");
            var y = request.OptionalFloatArg(property + "Y");
            var z = request.OptionalFloatArg(property + "Z");
            if (!x.HasValue && !y.HasValue && !z.HasValue) return;
            Physics.gravity = new Vector3(x ?? Physics.gravity.x, y ?? Physics.gravity.y, z ?? Physics.gravity.z);
            effects.Add(EffectJson("physics.settings", property, Vector3Json(Physics.gravity)));
        }

        private static void ApplyPhysicsVector2(BridgeRequest request, string property, List<string> effects)
        {
            var x = request.OptionalFloatArg(property + "X");
            var y = request.OptionalFloatArg(property + "Y");
            if (!x.HasValue && !y.HasValue) return;
            Physics2D.gravity = new Vector2(x ?? Physics2D.gravity.x, y ?? Physics2D.gravity.y);
            effects.Add(EffectJson("physics.settings", property, Vector2Json(Physics2D.gravity)));
        }

        private static string NormalizeMode(string mode)
        {
            return string.Equals(mode, "2d", StringComparison.OrdinalIgnoreCase) ? "2d" : "3d";
        }

        private static Vector2 NormalizeVector2(Vector2 value)
        {
            return value.sqrMagnitude <= 0f ? Vector2.right : value.normalized;
        }

        private static Vector3 NormalizeVector3(Vector3 value)
        {
            return value.sqrMagnitude <= 0f ? Vector3.forward : value.normalized;
        }

        private static string RaycastNextActionsJson(bool hit)
        {
            return hit ? "[]" : "[" + Quote("No Physics collider matched the ray; adjust origin, direction, distance or layer mask.") + "]";
        }

        private static string Raycast3DQueryJson(Vector3 origin, Vector3 direction, float distance, int layerMask)
        {
            return "{\"origin\":" + Vector3Json(origin) + ",\"direction\":" + Vector3Json(direction) + ",\"distance\":" + Number(distance) + ",\"layerMask\":" + layerMask.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string Raycast2DQueryJson(Vector2 origin, Vector2 direction, float distance, int layerMask)
        {
            return "{\"origin\":" + Vector2Json(origin) + ",\"direction\":" + Vector2Json(direction) + ",\"distance\":" + Number(distance) + ",\"layerMask\":" + layerMask.ToString(CultureInfo.InvariantCulture) + "}";
        }

        private static string Raycast3DHitsJson(RaycastHit[] hits)
        {
            var items = new List<string>();
            foreach (var hit in hits)
            {
                items.Add(Raycast3DHitJson(hit));
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Raycast3DHitJson(RaycastHit hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return "{\"distance\":" + Number(hit.distance) + ",\"point\":" + Vector3Json(hit.point) + ",\"normal\":" + Vector3Json(hit.normal) + ",\"collider\":" + ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name) + "}";
        }

        private static string Raycast2DHitsJson(RaycastHit2D[] hits)
        {
            var items = new List<string>();
            foreach (var hit in hits)
            {
                items.Add(Raycast2DHitJson(hit));
            }
            return "[" + string.Join(",", items.ToArray()) + "]";
        }

        private static string Raycast2DHitJson(RaycastHit2D hit)
        {
            var gameObject = hit.collider != null ? hit.collider.gameObject : null;
            return "{\"distance\":" + Number(hit.distance) + ",\"point\":" + Vector2Json(hit.point) + ",\"normal\":" + Vector2Json(hit.normal) + ",\"collider\":" + ColliderJson(gameObject, hit.collider == null ? null : hit.collider.GetType().Name) + "}";
        }

        private static string ColliderJson(GameObject gameObject, string colliderType)
        {
            if (gameObject == null)
            {
                return "null";
            }
            return "{\"id\":" + gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ",\"name\":" + Quote(gameObject.name) + ",\"path\":" + Quote(HierarchyPath(gameObject)) + ",\"type\":" + Quote(colliderType) + "}";
        }

        private static int ParseInt(string value, string property)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected integer for " + property + ": " + value, "Pass a valid numeric setting value.");
            }
            return parsed;
        }

        private static float ParseFloat(string value, string property)
        {
            float parsed;
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected number for " + property + ": " + value, "Pass a valid numeric setting value.");
            }
            return parsed;
        }

        private static bool ParseBool(string value, string property)
        {
            bool parsed;
            if (!bool.TryParse(value, out parsed))
            {
                throw new BridgeCommandException("E_SETTING_VALUE_INVALID", "Expected boolean for " + property + ": " + value, "Pass true or false.");
            }
            return parsed;
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

        private sealed class CodeExecutionResult
        {
            public bool Ok;
            public string DataJson;
            public string ErrorCode;
            public string ErrorMessage;
            public string DetailsJson;
            public string NextAction;
            public string ExitState;

            public static CodeExecutionResult Success(string dataJson)
            {
                return new CodeExecutionResult { Ok = true, DataJson = dataJson, ExitState = "completed" };
            }

            public static CodeExecutionResult Fail(string code, string message, string detailsJson, string nextAction)
            {
                return new CodeExecutionResult { Ok = false, ErrorCode = code, ErrorMessage = message, DetailsJson = detailsJson, NextAction = nextAction, ExitState = "failed" };
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
                var parsed = OptionalIntArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public int? OptionalIntArg(string name)
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
                return null;
            }

            public float FloatArg(string name, float fallback)
            {
                var parsed = OptionalFloatArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public float? OptionalFloatArg(string name)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(?:\\\")?(-?\\d+(?:\\.\\d+)?)(?:\\\")?");
                if (match.Success)
                {
                    float parsed;
                    if (float.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
                return null;
            }

            public bool BoolArg(string name, bool fallback)
            {
                var parsed = OptionalBoolArg(name);
                return parsed.HasValue ? parsed.Value : fallback;
            }

            public bool? OptionalBoolArg(string name)
            {
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                return match.Success ? (bool?)string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : null;
            }

            public bool HasArg(string name)
            {
                return Regex.IsMatch(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:");
            }

            public string RawJsonArg(string name)
            {
                return ExtractRawJson(body, name);
            }

            public bool BoolArgAtPath(string[] path, bool fallback)
            {
                var current = body;
                foreach (var segment in path ?? new string[0])
                {
                    current = JsonRawProperty(current, segment);
                    if (string.IsNullOrEmpty(current)) return fallback;
                }
                if (string.Equals(current, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(current, "false", StringComparison.OrdinalIgnoreCase)) return false;
                return fallback;
            }

            private static string ExtractString(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
                return match.Success ? Unescape(match.Groups[1].Value) : null;
            }

            private static string ExtractRawJson(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:");
                if (!match.Success)
                {
                    return null;
                }
                var start = match.Index + match.Length;
                while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
                if (start >= json.Length) return null;
                var end = FindJsonValueEnd(json, start);
                return end > start ? json.Substring(start, end - start).Trim() : null;
            }

            private static string Unescape(string value)
            {
                var builder = new StringBuilder();
                for (var index = 0; index < value.Length; index++)
                {
                    var current = value[index];
                    if (current != '\\' || index + 1 >= value.Length)
                    {
                        builder.Append(current);
                        continue;
                    }

                    var escaped = value[++index];
                    switch (escaped)
                    {
                        case (char)34: builder.Append((char)34); break;
                        case '\\': builder.Append((char)92); break;
                        case '/': builder.Append('/'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        default:
                            builder.Append('\\');
                            builder.Append(escaped);
                            break;
                    }
                }
                return builder.ToString();
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

        private sealed class TargetResolution
        {
            public readonly GameObject Target;
            public readonly List<GameObject> Matches;

            private TargetResolution(List<GameObject> matches)
            {
                Matches = matches;
                Target = matches.Count == 1 ? matches[0] : null;
            }

            public bool Ambiguous
            {
                get { return Matches.Count > 1; }
            }

            public static TargetResolution Single(GameObject target)
            {
                var matches = new List<GameObject>();
                if (target != null)
                {
                    matches.Add(target);
                }
                return new TargetResolution(matches);
            }

            public static TargetResolution FromMatches(List<GameObject> matches)
            {
                return new TargetResolution(matches);
            }
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
