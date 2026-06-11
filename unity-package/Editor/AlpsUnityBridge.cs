using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;

namespace Alps.UnityCli.Bridge
{
    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const string BridgeVersion = "1.0.0";
        private const string DefaultAgentEndpoint = "http://127.0.0.1:47391";
        private const string BridgeEndpoint = "http://127.0.0.1:47392/";
        private const int DefaultPageSize = 100;
        private const int MaxPageSize = 500;
        private static readonly string LogPath = Path.Combine(Application.dataPath, "../Logs/AlpsUnityBridge.log");
        private static readonly object QueueLock = new object();
        private static readonly Queue<PendingRequest> PendingRequests = new Queue<PendingRequest>();
        private static double nextHeartbeatAt;
        private static HttpListener listener;
        private static Thread listenerThread;
        private static bool listenerRunning;

        static AlpsUnityBridge()
        {
            EditorApplication.update += Heartbeat;
            EditorApplication.update += DrainPendingRequests;
            StartBridgeServer();
            Heartbeat();
        }

        private static void Heartbeat()
        {
            if (EditorApplication.timeSinceStartup < nextHeartbeatAt)
            {
                return;
            }

            nextHeartbeatAt = EditorApplication.timeSinceStartup + 5;
            var payload = BuildPayload();
            WriteBridgeLog($"heartbeat {JsonUtility.ToJson(payload)}");
            EditorCoroutineRunner.Run(PostHeartbeat(payload));
        }

        private static HeartbeatPayload BuildPayload()
        {
            return new HeartbeatPayload
            {
                bridgeVersion = BridgeVersion,
                projectPath = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath,
                unityVersion = Application.unityVersion,
                editorPid = System.Diagnostics.Process.GetCurrentProcess().Id,
                bridgeLog = LogPath,
                busy = EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode,
                compiling = EditorApplication.isCompiling,
                importing = EditorApplication.isUpdating,
                building = BuildPipeline.isBuildingPlayer,
                playModeChanging = EditorApplication.isPlayingOrWillChangePlaymode,
                domainReloading = EditorApplication.isCompiling,
                prefabStage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage() != null,
                transport = "http",
                endpoint = BridgeEndpoint.TrimEnd('/')
            };
        }

        private static IEnumerator PostHeartbeat(HeartbeatPayload payload)
        {
            var json = JsonUtility.ToJson(payload);
            using (var request = new UnityWebRequest($"{DefaultAgentEndpoint}/bridge/heartbeat", "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    yield return null;
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    WriteBridgeLog($"heartbeat_failed {request.error}");
                }
                else
                {
                    WriteBridgeLog("heartbeat_registered");
                }
            }
        }

        private static void StartBridgeServer()
        {
            if (listenerRunning)
            {
                return;
            }

            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add(BridgeEndpoint);
                listener.Start();
                listenerRunning = true;
                listenerThread = new Thread(ListenLoop) { IsBackground = true, Name = "AlpsUnityBridgeHttp" };
                listenerThread.Start();
                WriteBridgeLog($"bridge_listening {BridgeEndpoint}");
            }
            catch (Exception exception)
            {
                listenerRunning = false;
                WriteBridgeLog($"bridge_listen_failed {exception.Message}");
            }
        }

        private static void ListenLoop()
        {
            while (listenerRunning && listener != null)
            {
                try
                {
                    var context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleHttpContext(context));
                }
                catch (Exception exception)
                {
                    if (listenerRunning)
                    {
                        WriteBridgeLog($"bridge_accept_failed {exception.Message}");
                    }
                }
            }
        }

        private static void HandleHttpContext(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "POST" || context.Request.Url == null || context.Request.Url.AbsolutePath != "/bridge/execute")
            {
                WriteHttpResponse(context, 404, "{\"ok\":false,\"error\":{\"code\":\"E_NOT_FOUND\",\"message\":\"Unknown bridge route.\"}}");
                return;
            }

            string raw;
            using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
            {
                raw = reader.ReadToEnd();
            }

            ExecuteRequest request;
            try
            {
                request = JsonUtility.FromJson<ExecuteRequest>(raw);
            }
            catch (Exception exception)
            {
                WriteHttpResponse(context, 400, ErrorJson("", "E_SCHEMA_INVALID", $"Bridge request JSON is invalid: {exception.Message}"));
                return;
            }

            var pending = new PendingRequest { Request = request ?? new ExecuteRequest(), Completed = new ManualResetEvent(false) };
            lock (QueueLock)
            {
                PendingRequests.Enqueue(pending);
            }

            if (!pending.Completed.WaitOne(TimeSpan.FromSeconds(8)))
            {
                WriteHttpResponse(context, 504, ErrorJson(request?.requestId, "E_CONNECTION_FAILED", "Unity Editor Bridge execution timed out."));
                return;
            }

            WriteHttpResponse(context, pending.StatusCode, pending.ResponseJson);
        }

        private static void DrainPendingRequests()
        {
            while (true)
            {
                PendingRequest pending;
                lock (QueueLock)
                {
                    if (PendingRequests.Count == 0)
                    {
                        return;
                    }
                    pending = PendingRequests.Dequeue();
                }

                try
                {
                    pending.ResponseJson = ExecuteCommand(pending.Request);
                    pending.StatusCode = 200;
                }
                catch (BridgeError error)
                {
                    pending.ResponseJson = ErrorJson(pending.Request.requestId, error.Code, error.Message, error.DetailsJson, error.NextActionsJson);
                    pending.StatusCode = 200;
                }
                catch (Exception exception)
                {
                    pending.ResponseJson = ErrorJson(pending.Request.requestId, "E_CONNECTION_FAILED", exception.Message);
                    pending.StatusCode = 200;
                    WriteBridgeLog($"execute_failed {exception}");
                }
                finally
                {
                    pending.Completed.Set();
                }
            }
        }

        private static string ExecuteCommand(ExecuteRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.command))
            {
                throw new BridgeError("E_SCHEMA_INVALID", "Bridge command is required.");
            }

            WriteBridgeLog($"execute {request.requestId} {request.command}");
            switch (request.command)
            {
                case "scene.active":
                    return OkJson(request.requestId, SceneActiveJson());
                case "scene.hierarchy":
                    return OkJson(request.requestId, SceneHierarchyJson(request.args));
                case "scene.build-settings":
                    return OkJson(request.requestId, SceneBuildSettingsJson(request.args));
                case "go.find":
                    return OkJson(request.requestId, GameObjectFindJson(request.args));
                case "go.inspect":
                    return OkJson(request.requestId, GameObjectInspectJson(request.args));
                case "asset.search":
                    return OkJson(request.requestId, AssetSearchJson(request.args));
                case "asset.info":
                    return OkJson(request.requestId, AssetInfoJson(request.args));
                case "editor.console":
                    return OkJson(request.requestId, ConsoleJson(request.args));
                default:
                    throw new BridgeError("E_SCHEMA_INVALID", $"Unsupported bridge command: {request.command}");
            }
        }

        private static string SceneActiveJson()
        {
            var scene = SceneManager.GetActiveScene();
            return $"\"activeScene\":{{\"name\":{JsonString(scene.name)},\"path\":{JsonString(scene.path)},\"buildIndex\":{scene.buildIndex},\"isLoaded\":{JsonBool(scene.isLoaded)},\"isDirty\":{JsonBool(scene.isDirty)},\"rootCount\":{scene.rootCount}}}";
        }

        private static string SceneHierarchyJson(QueryArgs args)
        {
            var scene = SceneManager.GetActiveScene();
            var roots = scene.GetRootGameObjects().SelectMany(FlattenHierarchy).ToList();
            if (!string.IsNullOrWhiteSpace(args?.filter))
            {
                roots = roots.Where(item => Contains(item.Name, args.filter) || Contains(item.Path, args.filter) || item.Components.Any(component => Contains(component, args.filter))).ToList();
            }

            var page = Page(roots, args);
            var items = page.Items.Select(item => $"{{\"id\":{JsonString(item.Id)},\"name\":{JsonString(item.Name)},\"path\":{JsonString(item.Path)},\"active\":{JsonBool(item.Active)},\"tag\":{JsonString(item.Tag)},\"layer\":{item.Layer},\"componentTypes\":{JsonArray(item.Components)}}}");
            return $"\"scene\":{{\"name\":{JsonString(scene.name)},\"path\":{JsonString(scene.path)}}},\"items\":[{string.Join(",", items)}],{PageInfoJson(page)}";
        }

        private static string SceneBuildSettingsJson(QueryArgs args)
        {
            var scenes = EditorBuildSettings.scenes.Select((scene, index) => new BuildSceneInfo
            {
                Index = index,
                Path = scene.path,
                Enabled = scene.enabled,
                Guid = AssetDatabase.AssetPathToGUID(scene.path)
            }).ToList();
            if (!string.IsNullOrWhiteSpace(args?.filter))
            {
                scenes = scenes.Where(scene => Contains(scene.Path, args.filter)).ToList();
            }

            var page = Page(scenes, args);
            var items = page.Items.Select(scene => $"{{\"index\":{scene.Index},\"path\":{JsonString(scene.Path)},\"enabled\":{JsonBool(scene.Enabled)},\"guid\":{JsonString(scene.Guid)}}}");
            return $"\"items\":[{string.Join(",", items)}],{PageInfoJson(page)}";
        }

        private static string GameObjectFindJson(QueryArgs args)
        {
            var matches = ResolveGameObjects(args).ToList();
            var page = Page(matches, args);
            var items = page.Items.Select(item => $"{{\"id\":{JsonString(item.GetInstanceID().ToString(CultureInfo.InvariantCulture))},\"name\":{JsonString(item.name)},\"path\":{JsonString(GameObjectPath(item))},\"active\":{JsonBool(item.activeInHierarchy)},\"tag\":{JsonString(SafeTag(item))},\"layer\":{item.layer},\"componentTypes\":{JsonArray(ComponentTypes(item))}}}");
            return $"\"items\":[{string.Join(",", items)}],{PageInfoJson(page)}";
        }

        private static string GameObjectInspectJson(QueryArgs args)
        {
            var matches = ResolveGameObjects(args).ToList();
            if (matches.Count == 0)
            {
                throw new BridgeError("E_TARGET_NOT_FOUND", "No GameObject matched the target selector.", TargetDetailsJson(args), "[{\"action\":\"adjustTarget\",\"description\":\"Use --name, --tag, --layer, --component, --path, or --id with a selector that exists in the active scene.\"}]");
            }
            if (matches.Count > 1)
            {
                var sample = matches.Take(20).Select(item => $"{{\"id\":{JsonString(item.GetInstanceID().ToString(CultureInfo.InvariantCulture))},\"name\":{JsonString(item.name)},\"path\":{JsonString(GameObjectPath(item))}}}");
                throw new BridgeError("E_TARGET_AMBIGUOUS", "Multiple GameObjects matched the target selector.", $"{{\"matchCount\":{matches.Count},\"matches\":[{string.Join(",", sample)}]}}", "[{\"action\":\"narrowTarget\",\"description\":\"Pass --path or --id to select one GameObject.\"}]");
            }

            var go = matches[0];
            var transform = go.transform;
            var components = go.GetComponents<Component>().Select(component => component == null ? "MissingComponent" : component.GetType().FullName).ToList();
            return $"\"gameObject\":{{\"id\":{JsonString(go.GetInstanceID().ToString(CultureInfo.InvariantCulture))},\"name\":{JsonString(go.name)},\"path\":{JsonString(GameObjectPath(go))},\"activeSelf\":{JsonBool(go.activeSelf)},\"activeInHierarchy\":{JsonBool(go.activeInHierarchy)},\"tag\":{JsonString(SafeTag(go))},\"layer\":{go.layer},\"transform\":{{\"position\":{VectorJson(transform.localPosition)},\"rotation\":{VectorJson(transform.localEulerAngles)},\"scale\":{VectorJson(transform.localScale)}}},\"componentTypes\":{JsonArray(components)}}}";
        }

        private static string AssetSearchJson(QueryArgs args)
        {
            var filter = BuildAssetFilter(args);
            var guids = AssetDatabase.FindAssets(filter).Select(guid => CreateAssetRecord(guid)).Where(record => record != null).ToList();
            if (!string.IsNullOrWhiteSpace(args?.path))
            {
                var normalized = NormalizeAssetPath(args.path);
                guids = guids.Where(record => record.Path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            var page = Page(guids, args);
            var items = page.Items.Select(AssetRecordJson);
            return $"\"query\":{JsonString(filter)},\"items\":[{string.Join(",", items)}],{PageInfoJson(page)}";
        }

        private static string AssetInfoJson(QueryArgs args)
        {
            AssetRecord record = null;
            if (!string.IsNullOrWhiteSpace(args?.guid))
            {
                record = CreateAssetRecord(args.guid);
            }
            else
            {
                var target = args?.path ?? args?.target ?? args?.query;
                var path = NormalizeAssetPath(target);
                record = CreateAssetRecord(AssetDatabase.AssetPathToGUID(path));
            }

            if (record == null || string.IsNullOrWhiteSpace(record.Guid))
            {
                throw new BridgeError("E_TARGET_NOT_FOUND", "Asset was not found.", TargetDetailsJson(args), "[{\"action\":\"adjustAssetTarget\",\"description\":\"Pass an existing Assets/... or Packages/... path, --guid, or --query selector.\"}]");
            }

            return $"\"asset\":{AssetRecordJson(record)}";
        }

        private static string ConsoleJson(QueryArgs args)
        {
            if (args != null && args.clear)
            {
                ClearConsole();
            }

            var entries = ReadConsoleEntries();
            if (!string.IsNullOrWhiteSpace(args?.type))
            {
                entries = entries.Where(entry => string.Equals(entry.Type, args.type, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            if (!string.IsNullOrWhiteSpace(args?.filter))
            {
                entries = entries.Where(entry => Contains(entry.Message, args.filter) || Contains(entry.File, args.filter)).ToList();
            }

            var limitArgs = args ?? new QueryArgs();
            if (limitArgs.count > 0 && limitArgs.pageSize <= 0)
            {
                limitArgs.pageSize = limitArgs.count;
            }
            var page = Page(entries, limitArgs);
            var items = page.Items.Select(entry => $"{{\"type\":{JsonString(entry.Type)},\"message\":{JsonString(entry.Message)},\"file\":{JsonString(entry.File)},\"line\":{entry.Line},\"mode\":{entry.Mode}}}");
            return $"\"cleared\":{JsonBool(args != null && args.clear)},\"items\":[{string.Join(",", items)}],{PageInfoJson(page)}";
        }

        private static IEnumerable<HierarchyItem> FlattenHierarchy(GameObject root)
        {
            var stack = new Stack<GameObject>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                yield return new HierarchyItem
                {
                    Id = current.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                    Name = current.name,
                    Path = GameObjectPath(current),
                    Active = current.activeInHierarchy,
                    Tag = SafeTag(current),
                    Layer = current.layer,
                    Components = ComponentTypes(current)
                };
                for (var index = current.transform.childCount - 1; index >= 0; index -= 1)
                {
                    stack.Push(current.transform.GetChild(index).gameObject);
                }
            }
        }

        private static IEnumerable<GameObject> ResolveGameObjects(QueryArgs args)
        {
            var objects = SceneManager.GetActiveScene().GetRootGameObjects().SelectMany(root => FlattenTransforms(root.transform)).Select(transform => transform.gameObject);
            if (args == null)
            {
                return objects;
            }
            if (!string.IsNullOrWhiteSpace(args.id))
            {
                return objects.Where(item => item.GetInstanceID().ToString(CultureInfo.InvariantCulture) == args.id);
            }
            if (!string.IsNullOrWhiteSpace(args.path))
            {
                return objects.Where(item => string.Equals(GameObjectPath(item), args.path, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(args.name))
            {
                objects = objects.Where(item => Contains(item.name, args.name));
            }
            if (!string.IsNullOrWhiteSpace(args.target))
            {
                objects = objects.Where(item => Contains(item.name, args.target) || Contains(GameObjectPath(item), args.target));
            }
            if (!string.IsNullOrWhiteSpace(args.tag))
            {
                objects = objects.Where(item => string.Equals(SafeTag(item), args.tag, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(args.layer) && int.TryParse(args.layer, out var layer))
            {
                objects = objects.Where(item => item.layer == layer);
            }
            if (!string.IsNullOrWhiteSpace(args.component))
            {
                objects = objects.Where(item => ComponentTypes(item).Any(component => component.EndsWith(args.component, StringComparison.OrdinalIgnoreCase) || string.Equals(component, args.component, StringComparison.OrdinalIgnoreCase)));
            }
            return objects;
        }

        private static IEnumerable<Transform> FlattenTransforms(Transform root)
        {
            yield return root;
            for (var index = 0; index < root.childCount; index += 1)
            {
                foreach (var child in FlattenTransforms(root.GetChild(index)))
                {
                    yield return child;
                }
            }
        }

        private static string GameObjectPath(GameObject item)
        {
            var names = new Stack<string>();
            var current = item.transform;
            while (current != null)
            {
                names.Push(current.name);
                current = current.parent;
            }
            return string.Join("/", names.ToArray());
        }

        private static string SafeTag(GameObject item)
        {
            try
            {
                return item.tag;
            }
            catch
            {
                return "Untagged";
            }
        }

        private static List<string> ComponentTypes(GameObject item)
        {
            return item.GetComponents<Component>().Select(component => component == null ? "MissingComponent" : component.GetType().FullName).ToList();
        }

        private static string BuildAssetFilter(QueryArgs args)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(args?.query))
            {
                parts.Add(args.query);
            }
            else if (!string.IsNullOrWhiteSpace(args?.target))
            {
                parts.Add(args.target);
            }
            else if (!string.IsNullOrWhiteSpace(args?.filter))
            {
                parts.Add(args.filter);
            }
            if (!string.IsNullOrWhiteSpace(args?.type))
            {
                parts.Add($"t:{args.type}");
            }
            return string.Join(" ", parts).Trim();
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new BridgeError("E_PATH_INVALID", "Asset path is required.");
            }
            var normalized = assetPath.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeError("E_PATH_INVALID", "Asset path must be relative to Assets/ or Packages/.", $"{{\"path\":{JsonString(assetPath)}}}", "[{\"action\":\"useUnityAssetPath\",\"description\":\"Use a Unity asset path such as Assets/Scenes/Main.unity.\"}]");
            }
            return normalized;
        }

        private static AssetRecord CreateAssetRecord(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid))
            {
                return null;
            }
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return new AssetRecord
            {
                Guid = guid,
                Path = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Type = type == null ? "Unknown" : type.FullName
            };
        }

        private static string AssetRecordJson(AssetRecord record)
        {
            return $"{{\"guid\":{JsonString(record.Guid)},\"path\":{JsonString(record.Path)},\"name\":{JsonString(record.Name)},\"type\":{JsonString(record.Type)}}}";
        }

        private static List<ConsoleEntry> ReadConsoleEntries()
        {
            var result = new List<ConsoleEntry>();
            var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
            var logEntry = Type.GetType("UnityEditor.LogEntry,UnityEditor");
            if (logEntries == null || logEntry == null)
            {
                return result;
            }

            var getCount = logEntries.GetMethod("GetCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var startGettingEntries = logEntries.GetMethod("StartGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var endGettingEntries = logEntries.GetMethod("EndGettingEntries", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getEntryInternal = logEntries.GetMethod("GetEntryInternal", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (getCount == null || getEntryInternal == null)
            {
                return result;
            }

            var count = Convert.ToInt32(getCount.Invoke(null, null), CultureInfo.InvariantCulture);
            startGettingEntries?.Invoke(null, null);
            try
            {
                for (var index = 0; index < count; index += 1)
                {
                    var entry = Activator.CreateInstance(logEntry);
                    getEntryInternal.Invoke(null, new[] { index, entry });
                    var mode = FieldInt(logEntry, entry, "mode");
                    result.Add(new ConsoleEntry
                    {
                        Message = FieldString(logEntry, entry, "condition"),
                        File = FieldString(logEntry, entry, "file"),
                        Line = FieldInt(logEntry, entry, "line"),
                        Mode = mode,
                        Type = ConsoleType(mode)
                    });
                }
            }
            finally
            {
                endGettingEntries?.Invoke(null, null);
            }
            return result;
        }

        private static void ClearConsole()
        {
            var logEntries = Type.GetType("UnityEditor.LogEntries,UnityEditor");
            var clear = logEntries?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            clear?.Invoke(null, null);
        }

        private static string ConsoleType(int mode)
        {
            if ((mode & 1) != 0 || (mode & 64) != 0)
            {
                return "error";
            }
            if ((mode & 2) != 0)
            {
                return "warning";
            }
            return "log";
        }

        private static int FieldInt(Type type, object entry, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? 0 : Convert.ToInt32(field.GetValue(entry), CultureInfo.InvariantCulture);
        }

        private static string FieldString(Type type, object entry, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return field?.GetValue(entry)?.ToString() ?? "";
        }

        private static PageResult<T> Page<T>(List<T> items, QueryArgs args)
        {
            var pageSize = args?.pageSize > 0 ? args.pageSize : DefaultPageSize;
            pageSize = Math.Min(Math.Max(pageSize, 1), MaxPageSize);
            var offset = 0;
            if (!string.IsNullOrWhiteSpace(args?.cursor))
            {
                int.TryParse(args.cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);
            }
            offset = Math.Max(offset, 0);
            var pageItems = items.Skip(offset).Take(pageSize).ToList();
            var nextOffset = offset + pageItems.Count;
            return new PageResult<T>
            {
                Items = pageItems,
                Total = items.Count,
                PageSize = pageSize,
                Cursor = offset,
                NextCursor = nextOffset < items.Count ? nextOffset.ToString(CultureInfo.InvariantCulture) : null
            };
        }

        private static string PageInfoJson<T>(PageResult<T> page)
        {
            return $"\"pageInfo\":{{\"pageSize\":{page.PageSize},\"cursor\":{JsonString(page.Cursor.ToString(CultureInfo.InvariantCulture))},\"nextCursor\":{JsonString(page.NextCursor)},\"total\":{page.Total},\"hasMore\":{JsonBool(page.NextCursor != null)}}}";
        }

        private static string TargetDetailsJson(QueryArgs args)
        {
            if (args == null)
            {
                return "{}";
            }
            return $"{{\"target\":{JsonString(args.target)},\"name\":{JsonString(args.name)},\"tag\":{JsonString(args.tag)},\"layer\":{JsonString(args.layer)},\"component\":{JsonString(args.component)},\"path\":{JsonString(args.path)},\"id\":{JsonString(args.id)},\"guid\":{JsonString(args.guid)}}}";
        }

        private static string OkJson(string requestId, string dataProperties)
        {
            return $"{{\"ok\":true,\"requestId\":{JsonString(requestId)},\"data\":{{{dataProperties}}},\"nextActions\":[]}}";
        }

        private static string ErrorJson(string requestId, string code, string message, string detailsJson = null, string nextActionsJson = null)
        {
            return $"{{\"ok\":false,\"requestId\":{JsonString(requestId)},\"error\":{{\"code\":{JsonString(code)},\"message\":{JsonString(message)},\"details\":{(string.IsNullOrWhiteSpace(detailsJson) ? "null" : detailsJson)}}},\"nextActions\":{(string.IsNullOrWhiteSpace(nextActionsJson) ? "[]" : nextActionsJson)}}}";
        }

        private static string JsonString(string value)
        {
            if (value == null)
            {
                return "null";
            }
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")}\"";
        }

        private static string JsonArray(IEnumerable<string> values)
        {
            return $"[{string.Join(",", values.Select(JsonString))}]";
        }

        private static string JsonBool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string VectorJson(Vector3 value)
        {
            return $"{{\"x\":{value.x.ToString(CultureInfo.InvariantCulture)},\"y\":{value.y.ToString(CultureInfo.InvariantCulture)},\"z\":{value.z.ToString(CultureInfo.InvariantCulture)}}}";
        }

        private static bool Contains(string left, string right)
        {
            return left != null && right != null && left.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void WriteHttpResponse(HttpListenerContext context, int statusCode, string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void WriteBridgeLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, $"{DateTime.UtcNow:o} {message}{Environment.NewLine}");
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Alps Unity Bridge log write failed: {exception.Message}");
            }
        }

        [Serializable]
        private sealed class ExecuteRequest
        {
            public string requestId;
            public string command;
            public QueryArgs args;
        }

        [Serializable]
        private sealed class QueryArgs
        {
            public string target;
            public string name;
            public string tag;
            public string layer;
            public string component;
            public string path;
            public string id;
            public string query;
            public string type;
            public string guid;
            public string filter;
            public string cursor;
            public int pageSize;
            public int count;
            public bool clear;
        }

        [Serializable]
        private sealed class HeartbeatPayload
        {
            public string bridgeVersion;
            public string projectPath;
            public string unityVersion;
            public int editorPid;
            public string bridgeLog;
            public bool busy;
            public bool compiling;
            public bool importing;
            public bool building;
            public bool playModeChanging;
            public bool domainReloading;
            public bool prefabStage;
            public string transport;
            public string endpoint;
        }

        private sealed class PendingRequest
        {
            public ExecuteRequest Request;
            public ManualResetEvent Completed;
            public string ResponseJson;
            public int StatusCode;
        }

        private sealed class BridgeError : Exception
        {
            public BridgeError(string code, string message, string detailsJson = null, string nextActionsJson = null) : base(message)
            {
                Code = code;
                DetailsJson = detailsJson;
                NextActionsJson = nextActionsJson;
            }

            public string Code { get; }
            public string DetailsJson { get; }
            public string NextActionsJson { get; }
        }

        private sealed class HierarchyItem
        {
            public string Id;
            public string Name;
            public string Path;
            public bool Active;
            public string Tag;
            public int Layer;
            public List<string> Components;
        }

        private sealed class BuildSceneInfo
        {
            public int Index;
            public string Path;
            public bool Enabled;
            public string Guid;
        }

        private sealed class AssetRecord
        {
            public string Guid;
            public string Path;
            public string Name;
            public string Type;
        }

        private sealed class ConsoleEntry
        {
            public string Type;
            public string Message;
            public string File;
            public int Line;
            public int Mode;
        }

        private sealed class PageResult<T>
        {
            public List<T> Items;
            public int Total;
            public int PageSize;
            public int Cursor;
            public string NextCursor;
        }

        private sealed class EditorCoroutineRunner
        {
            private readonly IEnumerator routine;

            private EditorCoroutineRunner(IEnumerator routine)
            {
                this.routine = routine;
                EditorApplication.update += Update;
            }

            public static void Run(IEnumerator routine)
            {
                _ = new EditorCoroutineRunner(routine);
            }

            private void Update()
            {
                if (!routine.MoveNext())
                {
                    EditorApplication.update -= Update;
                }
            }
        }
    }
}
