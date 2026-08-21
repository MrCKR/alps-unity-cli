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

        [SerializeField]
        private int taskPoolMaxActive = 5;

        [SerializeField]
        private int taskPoolReopenAt = 3;

        internal static UnityCliProjectSettings Current { get { return instance; } }
        internal bool BridgeAutoStart { get { return bridgeAutoStart; } }
        internal bool PipelineHandoffApplied { get { return pipelineHandoffApplied; } }
        internal int TaskPoolMaxActive { get { return taskPoolMaxActive; } }
        internal int TaskPoolReopenAt { get { return taskPoolReopenAt; } }

        /// <summary>持久化下一次Bridge bootstrap使用的AutoStart值。</summary>
        internal void SetBridgeAutoStart(bool value)
        {
            if (bridgeAutoStart == value) return;
            bridgeAutoStart = value;
            Save(true);
        }

        /// <summary>持久化下一次Bridge bootstrap与Runtime build使用的合法高低水位。</summary>
        internal void SetTaskPool(int maxActive, int reopenAt)
        {
            if (maxActive < 2 || reopenAt < 1 || reopenAt >= maxActive) throw new System.ArgumentOutOfRangeException("maxActive", "Task pool requires maxActive >= 2 and 1 <= reopenAt < maxActive.");
            if (taskPoolMaxActive == maxActive && taskPoolReopenAt == reopenAt) return;
            taskPoolMaxActive = maxActive;
            taskPoolReopenAt = reopenAt;
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
                keywords = new[] { "Unity", "CLI", "Bridge", "Auto Start", "Task Pool", "Admission" }
            };
        }

        /// <summary>绘制项目级Bridge与准入池配置；修改值只在下次reload或build生效。</summary>
        private static void DrawSettings(string searchContext)
        {
            var settings = Current;
            EditorGUI.BeginChangeCheck();
            var autoStart = EditorGUILayout.Toggle("Bridge Auto Start", settings.BridgeAutoStart);
            if (EditorGUI.EndChangeCheck()) settings.SetBridgeAutoStart(autoStart);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Task Admission Pool", EditorStyles.boldLabel);
            var maxActive = EditorGUILayout.IntField("Maximum Active", settings.TaskPoolMaxActive);
            var reopenAt = EditorGUILayout.IntField("Reopen At", settings.TaskPoolReopenAt);
            var valid = maxActive >= 2 && reopenAt >= 1 && reopenAt < maxActive;
            if (!valid)
            {
                EditorGUILayout.HelpBox("Maximum Active must be at least 2, and Reopen At must be between 1 and Maximum Active - 1.", MessageType.Error);
                return;
            }
            if (maxActive != settings.TaskPoolMaxActive || reopenAt != settings.TaskPoolReopenAt)
                settings.SetTaskPool(maxActive, reopenAt);
        }
    }
}
#endif
