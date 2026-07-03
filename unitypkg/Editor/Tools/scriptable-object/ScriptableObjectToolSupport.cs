#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// scriptable-object 域 IUnityCliTool 共享的基础设施 helper:路径校验、类型解析、
    /// 资产加载、属性序列化。仅供 scriptable-object 域自包含工具复用;
    /// 不实现 IUnityCliTool,不参与工具发现。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper)。
    /// </summary>
    public static class ScriptableObjectToolSupport
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

        /// <summary>Assets/ 路径规范化:反斜杠转正斜杠并去首尾空白。</summary>
        public static string NormalizeAssetPath(string rawPath)
        {
            return string.IsNullOrEmpty(rawPath) ? null : rawPath.Replace('\\', '/').Trim();
        }

        /// <summary>Unity 工程根目录(Assets 的父目录)。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        /// <summary>确保资产目录存在,缺失则创建并刷新 AssetDatabase。</summary>
        public static void CreateAssetDirectory(string assetPath)
        {
            var directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory) || AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
            {
                return;
            }
            Directory.CreateDirectory(Path.Combine(ProjectRoot(), directory.Replace('/', Path.DirectorySeparatorChar)));
            AssetDatabase.Refresh();
        }

        /// <summary>校验 Assets/*.asset 路径。</summary>
        public static string RequireScriptableObjectPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".asset", System.StringComparison.OrdinalIgnoreCase))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_PATH_INVALID", "ScriptableObject path must be under Assets/ and end with .asset: " + path, "Pass a Unity asset path such as Assets/Data/Config.asset.");
            }
            return path;
        }

        /// <summary>按全名或类名查找 ScriptableObject 类型。</summary>
        public static System.Type ResolveScriptableObjectType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_REQUIRED", "scriptable-object.create requires type <ScriptableObjectType>.", "Pass a full type name or a unique class name deriving from ScriptableObject.");
            }
            // 先在所有程序集中按全名查找
            System.Type type = null;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type candidate;
                try { candidate = assembly.GetType(typeName, false); } catch { continue; }
                if (candidate != null) { type = candidate; break; }
            }
            // 回退:按类名匹配
            if (type == null)
            {
                foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    System.Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException exception) { types = exception.Types; }
                    catch { continue; }
                    foreach (var candidate in types)
                    {
                        if (candidate != null && string.Equals(candidate.Name, typeName, System.StringComparison.Ordinal))
                        {
                            type = candidate;
                            break;
                        }
                    }
                    if (type != null) break;
                }
            }
            if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_TYPE_NOT_FOUND", "ScriptableObject type not found: " + typeName, "Use a type deriving from UnityEngine.ScriptableObject.");
            }
            return type;
        }

        /// <summary>加载 ScriptableObject 资产,不存在抛错。</summary>
        public static ScriptableObject LoadScriptableObject(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(path);
            if (asset == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_SCRIPTABLE_OBJECT_NOT_FOUND", "ScriptableObject asset not found: " + path, "Check the path or run tools run asset.search --input '{\"filter\":\"t:ScriptableObject\"}' to discover the path.");
            }
            return asset;
        }

        /// <summary>构造 ScriptableObject 摘要 DTO。</summary>
        public static ScriptableObjectInfo BuildScriptableObjectInfo(string path, ScriptableObject asset)
        {
            var info = new ScriptableObjectInfo
            {
                Path = path,
                Guid = AssetDatabase.AssetPathToGUID(path),
                Name = asset.name,
                Type = asset.GetType().FullName,
                Properties = new Dictionary<string, string>()
            };
            foreach (var field in asset.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                info.Properties[field.Name] = Convert.ToString(field.GetValue(asset), CultureInfo.InvariantCulture);
            }
            foreach (var property in asset.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0) continue;
                try { info.Properties[property.Name] = Convert.ToString(property.GetValue(asset, null), CultureInfo.InvariantCulture); }
                catch { }
            }
            return info;
        }

        /// <summary>输出 ScriptableObject 类型摘要 DTO。</summary>
        public static ScriptableObjectTypeEntry BuildTypeEntry(System.Type type)
        {
            return new ScriptableObjectTypeEntry
            {
                Name = type.Name,
                FullName = type.FullName,
                Assembly = type.Assembly.GetName().Name,
                Namespace = type.Namespace,
                Abstract = type.IsAbstract
            };
        }
    }

    /// <summary>ScriptableObject 摘要信息,scriptable-object 域工具 Result 共享。</summary>
    public sealed class ScriptableObjectInfo
    {
        public string Path { get; set; }
        public string Guid { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    /// <summary>ScriptableObject 类型摘要,scriptable-object.list-types 工具 Result 共享。</summary>
    public sealed class ScriptableObjectTypeEntry
    {
        public string Name { get; set; }
        public string FullName { get; set; }
        public string Assembly { get; set; }
        public string Namespace { get; set; }
        public bool Abstract { get; set; }
    }
}
#endif
