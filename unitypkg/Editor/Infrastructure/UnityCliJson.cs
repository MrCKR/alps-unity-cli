#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static List<object> CapabilitiesData()
        {
            return new List<object> {
                CapabilityData("camera", CameraCommands), CapabilityData("scene", SceneCommands), CapabilityData("go", GameObjectCommands), CapabilityData("asset", AssetCommands),
                CapabilityData("screenshot", ScreenshotCommands), CapabilityData("prefab", PrefabCommands), CapabilityData("ui", UiCommands),
                CapabilityData("material", MaterialCommands), CapabilityData("graphics", GraphicsCommands), CapabilityData("physics", PhysicsCommands), CapabilityData("scriptable-object", ScriptableObjectCommands),
                CapabilityData("profiler", ProfilerCommands), CapabilityData("animation", AnimationCommands), CapabilityData("shader", ShaderCommands), CapabilityData("texture", TextureCommands),
                CapabilityData("editor", EditorCommands), CapabilityData("code", CodeCommands), CapabilityData("project-tool", ProjectToolCommands), CapabilityData("runtime-tool", RuntimeToolCommands)
            };
        }

        private static bool IsMutatingCommand(string command) => CommandRegistry.IsMutating(command);

        private static Dictionary<string, object> CapabilityData(string domain, string[] commands)
        {
            return new Dictionary<string, object> { { "domain", domain }, { "route", "POST /bridge/execute" }, { "commands", (commands ?? Array.Empty<string>()).Cast<object>().ToList() } };
        }

        private static string HierarchyPath(GameObject gameObject)
        {
            var names = new Stack<string>();
            var current = gameObject.transform;
            while (current != null) { names.Push(current.name); current = current.parent; }
            return string.Join("/", names.ToArray());
        }

        /// <summary>读取 HTTP 收到的 exact bytes；chunked body 同样受 1 MiB hard limit 约束。</summary>
        private static byte[] ReadBodyBytes(HttpListenerRequest request)
        {
            const int maximumBytes = 1024 * 1024;
            using (var stream = request.InputStream)
            {
                if (request.ContentLength64 >= 0)
                {
                    var length = request.ContentLength64;
                    if (length > maximumBytes) throw new InvalidOperationException("Bridge request body is too large.");
                    var bytes = new byte[(int)length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) throw new EndOfStreamException("Request body ended before Content-Length bytes were read.");
                        offset += read;
                    }
                    return bytes;
                }
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[8192];
                    for (;;)
                    {
                        var read = stream.Read(chunk, 0, chunk.Length);
                        if (read <= 0) break;
                        if (buffer.Length + read > maximumBytes) throw new InvalidOperationException("Bridge request body is too large.");
                        buffer.Write(chunk, 0, read);
                    }
                    return buffer.ToArray();
                }
            }
        }

        /// <summary>协议只接受无 BOM 的 strict UTF-8；digest 仍使用解码前 exact bytes。</summary>
        private static string DecodeRequestBody(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                throw new FormatException("Bridge request body must not contain a UTF-8 BOM.");
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        // HTTP 边界接收最终 JSON response body，不在业务 helper 中拼接 JSON。
        private static void WriteJson(HttpListenerContext context, string json, int statusCode)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Response.StatusCode = statusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                // transport attestation 由 supervisor 消费，禁止写入公开 business envelope。
                context.Response.Headers["x-unity-cli-instance-id"] = InstanceId();
                context.Response.Headers["x-unity-cli-session-id"] = EditorSessionId();
                context.Response.Headers["x-unity-cli-target"] = "editor";
                context.Response.Headers["x-unity-cli-listener-id"] = ListenerId;
                context.Response.Headers["x-unity-cli-listener-generation"] = CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture);
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.OutputStream.Close();
            }
            catch (Exception exception) { WriteLog("write_json_failed " + exception.GetType().Name + ": " + exception.Message); }
        }

        private static void TryWriteSerializeErrorEnvelope(HttpListenerContext context, string code, string message, string nextAction)
        {
            try { WriteJson(context, SerializeErrorEnvelope(code, message, nextAction), 200); }
            catch (Exception exception) { WriteLog("try_write_error_json_failed " + exception.GetType().Name + ": " + exception.Message); }
        }

        private static string SerializeSuccessEnvelope(object data)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Success(string.Empty, data));
        }

        private static string SerializeTransientErrorEnvelope(string requestId, string code, string message, string nextAction, int retryAfterMs)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(requestId, code, message, nextActions: new[] { nextAction }, transient: true, retryAfterMs: retryAfterMs));
        }

        private static string SerializeErrorEnvelope(string code, string message, string nextAction)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(string.Empty, code, message, nextActions: string.IsNullOrWhiteSpace(nextAction) ? Array.Empty<string>() : new[] { nextAction }));
        }

        private static string SerializeBridgeExceptionEnvelope(string requestId, BridgeCommandException exception)
        {
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(requestId, exception.Code, exception.Message, exception.Details, exception.NextActions, exception.Transient, exception.RetryAfterMs > 0 ? (double?)exception.RetryAfterMs : null));
        }

        private static string TargetAmbiguousSerializeErrorEnvelope(string label, string target, TargetResolution resolution, string nextAction)
        {
            var details = new Dictionary<string, object> { { "label", label }, { "target", target }, { "count", resolution.Matches.Count }, { "matches", TargetMatchesData(resolution.Matches) } };
            return UnityCliSerializer.Serialize(UnityCli.Protocol.ProtocolEnvelopeFactory.Error(string.Empty, "E_TARGET_AMBIGUOUS", "Multiple targets matched.", details, new[] { nextAction }));
        }

        private static List<object> TargetMatchesData(List<GameObject> matches)
        {
            var items = new List<object>();
            var limit = Math.Min(matches.Count, 10);
            for (var index = 0; index < limit; index++)
            {
                var gameObject = matches[index];
                items.Add(new Dictionary<string, object> { { "id", (long)gameObject.GetInstanceID() }, { "name", gameObject.name }, { "path", HierarchyPath(gameObject) }, { "components", ComponentNamesData(gameObject) } });
            }
            return items;
        }

        // 仅供文本日志使用，保持既有小写布尔格式，不承担 JSON 构造职责。
        private static string Bool(bool value) => value ? "true" : "false";
    }
}
#endif
