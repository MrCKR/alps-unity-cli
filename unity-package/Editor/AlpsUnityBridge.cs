#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AlpsUnityCli
{
    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const int Port = 47392;
        private static readonly object QueueLock = new object();
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
                WriteJson(context, "{\"ok\":true,\"data\":{\"bridge\":\"alps-unity-cli\",\"port\":" + Port + "}}", 200);
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

            item.Completed.Wait(TimeSpan.FromSeconds(60));
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

                try
                {
                    item.Response = Execute(item.Request);
                }
                catch (BridgeCommandException exception)
                {
                    item.Response = ErrorJson(exception.Code, exception.Message, "Check output path, permissions, format parameters and AlpsUnityBridge.log.");
                }
                catch (Exception exception)
                {
                    item.Response = ErrorJson("E_BRIDGE_COMMAND_FAILED", exception.Message, "Check Unity Console and AlpsUnityBridge.log.");
                }
                finally
                {
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
            var camera = ResolveCamera(request.StringArg("target"));
            if (camera == null)
            {
                return ErrorJson("E_CAMERA_NOT_FOUND", "Camera not found: " + request.StringArg("target"), "Run alps-unity camera list to inspect available cameras.");
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
            var outputPath = ResolveOutputPath(request.StringArg("path"));
            var format = NormalizeFormat(request.StringArg("format"), outputPath);
            var overwrite = request.BoolArg("overwrite", false);
            if (File.Exists(outputPath) && !overwrite)
            {
                throw new BridgeCommandException("E_OUTPUT_EXISTS", "Screenshot output already exists: " + outputPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
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
                File.WriteAllBytes(outputPath, bytes);
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

        private static string ResolveOutputPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                throw new BridgeCommandException("E_OUTPUT_PATH_REQUIRED", "Pass --output <path> or a positional screenshot path.");
            }

            var normalized = rawPath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.IsPathRooted(normalized) ? normalized : Path.Combine(ProjectRoot(), normalized));
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
            public BridgeWorkItem(BridgeRequest request)
            {
                Request = request;
            }

            public BridgeRequest Request { get; private set; }
            public ManualResetEventSlim Completed { get; private set; } = new ManualResetEventSlim(false);
            public string Response { get; set; }
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
                var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*(-?\\d+)");
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
            public BridgeCommandException(string code, string message) : base(message)
            {
                Code = code;
            }

            public string Code { get; private set; }
        }
    }
}
#endif
