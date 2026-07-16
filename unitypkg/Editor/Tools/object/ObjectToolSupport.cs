#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// object 域 IUnityCliTool 共享的基础设施 helper:序列化选项构造、对象引用解析。
    /// 仅供 object 域自包含工具复用;不实现 IUnityCliTool,不参与工具发现。
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
            if (UnityCliBridge.IsPlayModeChanging())
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
        /// 从 UnityCli.Protocol.BridgeRequest 构造 ObjectSerializer.Options,复刻旧 BuildSerializerOptions 逻辑。
        /// 支持 paths/viewQuery 局部读取、namePattern 过滤。
        /// </summary>
        internal static UnityCliBridge.ObjectSerializer.Options BuildSerializerOptions(IUnityCliObjectSerializationInput request)
        {
            var options = new UnityCliBridge.ObjectSerializer.Options
            {
                MaxDepth = request.MaxDepth ?? 4,
                MaxArrayItems = System.Math.Max(0, System.Math.Min(request.MaxArrayItems ?? 50, 1000)),
                IncludeFields = request.IncludeFields ?? true,
                IncludeProperties = request.IncludeProperties ?? true,
                DeepSerialization = request.DeepSerialization ?? true
            };
            if (request.Paths != null && request.Paths.Count > 0 && request.ViewQuery != null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_OBJECT_READ_SCOPE_CONFLICT", "paths and viewQuery are mutually exclusive.", "Use paths for exact member reads, or viewQuery for one filtered subtree.");
            }
            if (request.Paths != null)
            {
                options.Paths = new HashSet<string>(request.Paths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()));
            }
            var query = request.ViewQuery as Dictionary<string, object>;
            if (query != null)
            {
                options.ViewPath = GetStringMember(query, "path");
                options.NamePattern = GetStringMember(query, "namePattern");
                options.TypeFilter = GetStringMember(query, "typeFilter");
                object maxDepth;
                if (query.TryGetValue("maxDepth", out maxDepth) && maxDepth != null)
                {
                    options.MaxDepth = System.Math.Max(0, System.Convert.ToInt32(maxDepth, CultureInfo.InvariantCulture));
                }
            }
            if (!string.IsNullOrEmpty(request.NamePattern))
            {
                options.NameFilters = new HashSet<string>(request.NamePattern.Split(',').Select(value => value.Trim()));
            }
            return options;
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
