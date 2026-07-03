#if UNITY_EDITOR
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// build 域 IUnityCliTool 共享的基础设施 helper:Build Profile 反射、路径规范化、忙状态拦截。
    /// 仅供 build 域自包含工具复用;不实现 IUnityCliTool,不参与工具发现。
    /// 设计依据:.plan/工具统一形态.md(自包含工具共享只读 helper)。
    /// </summary>
    public static class BuildToolSupport
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

        /// <summary>在已加载程序集中查找 Unity 类型,兼容不同 Editor 版本的命名空间变化。</summary>
        public static System.Type FindUnityType(string fullName)
        {
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type type = null;
                try { type = assembly.GetType(fullName, false); } catch { }
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>通过反射读取当前激活 Build Profile;旧 Unity 没有该属性时返回 null。</summary>
        public static UnityEngine.Object ActiveBuildProfile(System.Type profileType)
        {
            var prop = typeof(EditorUserBuildSettings).GetProperty("activeBuildProfile", BindingFlags.Public | BindingFlags.Static);
            return prop != null && profileType.IsAssignableFrom(prop.PropertyType) ? prop.GetValue(null, null) as UnityEngine.Object : null;
        }

        /// <summary>通过反射设置当前 Build Profile;API 不存在时抛稳定错误。</summary>
        public static void SetActiveBuildProfile(System.Type profileType, UnityEngine.Object profile)
        {
            var prop = typeof(EditorUserBuildSettings).GetProperty("activeBuildProfile", BindingFlags.Public | BindingFlags.Static);
            if (prop == null || !prop.CanWrite || !profileType.IsAssignableFrom(prop.PropertyType))
            {
                throw new UnityCliBridge.BridgeCommandException("E_BUILD_PROFILE_SWITCH_UNSUPPORTED", "This Unity version does not expose EditorUserBuildSettings.activeBuildProfile.", "Use build.platform/build.settings or upgrade Unity.");
            }
            prop.SetValue(null, profile, null);
        }

        /// <summary>Unity 工程根目录(Assets 的父目录)。</summary>
        public static string ProjectRoot()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }
    }
}
#endif
