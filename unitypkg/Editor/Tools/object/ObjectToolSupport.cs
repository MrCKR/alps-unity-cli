#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// object 域 IUnityCliTool 共享的基础设施 helper:序列化选项构造、对象引用解析。
    /// 仅供 object 域自包含工具复用;不实现 IUnityCliTool,不参与工具发现。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper)。
    /// </summary>
    internal static class ObjectToolSupport
    {
        /// <summary>拦截编译/导入/构建/PlayMode 过渡期的写入请求,避免脏状态写入。</summary>
        public static void EnsureEditorNotBusy(string command)
        {
            if (EditorApplication.isCompiling)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_COMPILING", "Unity Editor is compiling scripts.", "Wait for Unity to finish compiling and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isUpdating)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_IMPORTING", "Unity Editor is importing assets.", "Wait for Unity to finish importing and retry.", transient: true, retryAfterMs: 5000);
            }
            if (EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_PLAY_MODE", "Unity Editor is entering or exiting Play mode.", "Wait for Play mode transition to complete and retry.", transient: true, retryAfterMs: 3000);
            }
            if (BuildPipeline.isBuildingPlayer)
            {
                throw new UnityCliBridge.BridgeCommandException("E_UNITY_BUSY", "Unity Editor is building the player.", "Wait for the build to complete and retry.", transient: true, retryAfterMs: 10000);
            }
        }

        /// <summary>Unity 工程根目录(Assets 的父目录)。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>
        /// 从 BridgeRequest 构造 ObjectSerializer.Options,复刻旧 BuildSerializerOptions 逻辑。
        /// 支持 paths/viewQuery 局部读取、namePattern 过滤。
        /// </summary>
        internal static UnityCliBridge.ObjectSerializer.Options BuildSerializerOptions(UnityCliBridge.BridgeRequest request)
        {
            var options = new UnityCliBridge.ObjectSerializer.Options
            {
                MaxDepth = request.IntArg("maxDepth", 4),
                MaxArrayItems = System.Math.Max(0, System.Math.Min(request.IntArg("maxArrayItems", 50), 1000)),
                IncludeFields = request.BoolArg("includeFields", true),
                IncludeProperties = request.BoolArg("includeProperties", true),
                DeepSerialization = request.BoolArg("deepSerialization", true)
            };
            var pathsRaw = request.RawJsonArg("paths");
            var viewQueryRaw = request.RawJsonArg("viewQuery");
            if (!string.IsNullOrEmpty(pathsRaw) && !string.IsNullOrEmpty(viewQueryRaw))
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_READ_SCOPE_CONFLICT", "paths and viewQuery are mutually exclusive.", "Use paths for exact member reads, or viewQuery for one filtered subtree.");
            }
            if (!string.IsNullOrEmpty(pathsRaw))
            {
                options.Paths = new HashSet<string>();
                foreach (var p in JsonStringArray(pathsRaw))
                {
                    var clean = p == null ? null : p.Trim();
                    if (!string.IsNullOrEmpty(clean)) options.Paths.Add(clean);
                }
            }
            if (!string.IsNullOrEmpty(viewQueryRaw))
            {
                var parsed = UnityCliToolJson.Parse(viewQueryRaw) as Dictionary<string, object>;
                if (parsed != null)
                {
                    options.ViewPath = GetStringMember(parsed, "path") ?? GetStringMember(parsed, "Path");
                    options.NamePattern = GetStringMember(parsed, "namePattern") ?? GetStringMember(parsed, "NamePattern");
                    options.TypeFilter = GetStringMember(parsed, "typeFilter") ?? GetStringMember(parsed, "TypeFilter");
                    var maxDepthRaw = GetRawMember(parsed, "maxDepth") ?? GetRawMember(parsed, "MaxDepth");
                    if (!string.IsNullOrEmpty(maxDepthRaw))
                    {
                        int maxDepth;
                        if (int.TryParse(maxDepthRaw.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out maxDepth))
                        {
                            options.MaxDepth = System.Math.Max(0, maxDepth);
                        }
                    }
                }
            }
            var nameFilter = request.StringArg("namePattern");
            if (!string.IsNullOrEmpty(nameFilter))
            {
                options.NameFilters = new HashSet<string>(nameFilter.Split(','));
            }
            return options;
        }

        // 从 JSON 数组字符串提取字符串列表
        private static List<string> JsonStringArray(string json)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json)) return result;
            var parsed = UnityCliToolJson.Parse(json);
            if (parsed is List<object> list)
            {
                foreach (var item in list)
                {
                    result.Add(item as string);
                }
            }
            return result;
        }

        private static string GetStringMember(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            return obj.TryGetValue(key, out var val) ? val as string : null;
        }

        private static string GetRawMember(Dictionary<string, object> obj, string key)
        {
            if (obj == null) return null;
            if (!obj.TryGetValue(key, out var val) || val == null) return null;
            return val.ToString();
        }
    }
}
#endif
