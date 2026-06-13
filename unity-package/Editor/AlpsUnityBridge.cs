#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.CSharp;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace AlpsUnityCli
{
    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const int Port = 47392;
        private static HttpListener listener;
        private static readonly Queue<BridgeWorkItem> WorkQueue = new Queue<BridgeWorkItem>();
        private static readonly object QueueLock = new object();
        private static readonly string[] CameraCommands = { "camera.list", "camera.info", "camera.screenshot" };
        private static readonly string[] SceneCommands = { "scene.screenshot" };
        private static readonly string[] ScreenshotCommands = { "screenshot.capture" };
        private static readonly string[] PrefabCommands = { "prefab.info", "prefab.create", "prefab.stage-open", "prefab.stage-save", "prefab.stage-close", "prefab.hierarchy" };
        private static readonly string[] MaterialCommands = { "material.info", "material.create", "material.assign", "material.set" };
        private static readonly string[] GraphicsCommands = { "graphics.quality", "graphics.settings", "graphics.screenshot" };
        private static readonly string[] PhysicsCommands = { "physics.settings", "physics.raycast", "physics.simulation" };
        private static readonly string[] CodeCommands = { "code.exec" };
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
                case "code.exec":
                    return CodeExec(request);
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

        private static string CapabilitiesJson()
        {
            return "["
                + CapabilityJson("camera", CameraCommands) + ","
                + CapabilityJson("scene", SceneCommands) + ","
                + CapabilityJson("screenshot", ScreenshotCommands) + ","
                + CapabilityJson("prefab", PrefabCommands) + ","
                + CapabilityJson("material", MaterialCommands) + ","
                + CapabilityJson("graphics", GraphicsCommands) + ","
                + CapabilityJson("physics", PhysicsCommands) + ","
                + CapabilityJson("code", CodeCommands)
                + "]";
        }

        private static bool IsMutatingCommand(string command)
        {
            return command == "prefab.create"
                || command == "prefab.stage-open"
                || command == "prefab.stage-save"
                || command == "prefab.stage-close"
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
                || command == "code.exec";
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

        private static string InstanceId()
        {
            return System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
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

        private static string ErrorJsonWithDetails(string code, string message, string detailsJson, string nextAction)
        {
            return "{\"ok\":false,\"error\":{\"code\":" + Quote(code) + ",\"message\":" + Quote(message) + ",\"details\":" + (string.IsNullOrEmpty(detailsJson) ? "{}" : detailsJson) + "},\"nextActions\":[" + Quote(nextAction) + "]}";
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

            private static string ExtractString(string json, string name)
            {
                var match = Regex.Match(json ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
                return match.Success ? Unescape(match.Groups[1].Value) : null;
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
                        case '\"': builder.Append('\"'); break;
                        case '\\': builder.Append('\\'); break;
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
