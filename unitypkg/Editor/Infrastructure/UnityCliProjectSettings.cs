#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// 保存项目级Bridge启动策略与一次性Pipeline接管结果。
    /// AutoStart只在下一次Domain Reload或Editor启动时读取，避免现场中断已接受请求。
    /// </summary>
    [FilePath("ProjectSettings/UnityCliSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class UnityCliProjectSettings : ScriptableSingleton<UnityCliProjectSettings>
    {
        [SerializeField]
        private bool bridgeAutoStart = true;

        [SerializeField, HideInInspector]
        private bool pipelineHandoffApplied;

        internal static UnityCliProjectSettings Current { get { return instance; } }
        internal bool BridgeAutoStart { get { return bridgeAutoStart; } }
        internal bool PipelineHandoffApplied { get { return pipelineHandoffApplied; } }

        /// <summary>持久化下一次Bridge bootstrap使用的AutoStart值。</summary>
        internal void SetBridgeAutoStart(bool value)
        {
            if (bridgeAutoStart == value) return;
            bridgeAutoStart = value;
            Save(true);
        }

        /// <summary>仅在Pipeline配置与停服全部验证成功后冻结一次性接管事实。</summary>
        internal void MarkPipelineHandoffApplied()
        {
            if (pipelineHandoffApplied) return;
            pipelineHandoffApplied = true;
            Save(true);
        }

        [SettingsProvider]
        private static SettingsProvider CreateSettingsProvider()
        {
            return new SettingsProvider("Project/Unity CLI", SettingsScope.Project)
            {
                label = "Unity CLI",
                guiHandler = DrawSettings,
                keywords = new[] { "Unity", "CLI", "Bridge", "Auto Start" }
            };
        }

        /// <summary>绘制项目级Bridge AutoStart开关；修改值不即时启停listener。</summary>
        private static void DrawSettings(string searchContext)
        {
            var settings = Current;
            EditorGUI.BeginChangeCheck();
            var value = EditorGUILayout.Toggle("Bridge Auto Start", settings.BridgeAutoStart);
            if (EditorGUI.EndChangeCheck()) settings.SetBridgeAutoStart(value);
        }
    }
}
#endif
