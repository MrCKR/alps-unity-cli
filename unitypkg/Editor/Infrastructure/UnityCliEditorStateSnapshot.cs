#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>采集同一时刻的Unity只读状态，供私有命令与Registry publication复用。</summary>
        private static Dictionary<string, object> SampleEditorStateSnapshot(string observedAt = null)
        {
            var scene = SceneManager.GetActiveScene();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            return new Dictionary<string, object>
            {
                { "schemaVersion", 1L },
                { "observedAt", observedAt ?? DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                { "sourceEditorSessionId", EditorSessionId() },
                { "sourceListenerGeneration", CurrentListenerGeneration() },
                { "fields", new Dictionary<string, object>
                    {
                        { "compile", KnownSnapshotField(EditorApplication.isCompiling) },
                        { "import", KnownSnapshotField(EditorApplication.isUpdating) },
                        { "build", KnownSnapshotField(BuildPipeline.isBuildingPlayer) },
                        { "playMode", KnownSnapshotField(IsPlayModeChanging() ? "transitioning" : (EditorApplication.isPlaying ? "play" : "edit")) },
                        { "prefabStage", KnownSnapshotField(prefabStage == null ? null : new Dictionary<string, object> { { "assetPath", prefabStage.assetPath }, { "sceneName", prefabStage.scene.name } }) },
                        { "activeScene", KnownSnapshotField(scene.IsValid() ? new Dictionary<string, object> { { "name", scene.name }, { "path", scene.path }, { "isDirty", scene.isDirty } } : null) },
                        { "runtime", KnownSnapshotField(new Dictionary<string, object> { { "ready", EditorApplication.isPlaying && !IsPlayModeChanging() }, { "sessionId", currentPlayModeSessionId ?? string.Empty } }) }
                    }
                }
            };
        }

        /// <summary>内部快照命令仅供 TS hybrid Context 探测，不能进入公开能力目录。</summary>
        private static void RegisterEditorStateSnapshotCommand(BridgeCommandRegistry registry)
        {
            registry.Register<UnityCli.Protocol.BridgeRequest>(
                "internal.editor.state.snapshot",
                "internal",
                false,
                30000,
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new System.Threading.Tasks.ValueTask<object>(new Dictionary<string, object>
                    {
                        { "snapshot", SampleEditorStateSnapshot() },
                        { "progress", SnapshotProgress() },
                        { "blockers", new List<object>() }
                    });
                });
        }

        private static bool IsEditorStateSnapshotCommand(string command)
        {
            return string.Equals(command, "internal.editor.state.snapshot", StringComparison.Ordinal);
        }

        private static Dictionary<string, object> KnownSnapshotField(object value)
        {
            return new Dictionary<string, object> { { "known", true }, { "value", value } };
        }

        private static List<object> SnapshotProgress()
        {
            var progress = new List<object>();
            if (domainReloading) progress.Add(SnapshotProgressItem("domain-reload", "Reloading assemblies"));
            if (EditorApplication.isCompiling) progress.Add(SnapshotProgressItem("script-compile", "Compiling scripts"));
            if (EditorApplication.isUpdating) progress.Add(SnapshotProgressItem("asset-import", "Importing assets"));
            if (BuildPipeline.isBuildingPlayer) progress.Add(SnapshotProgressItem("build", "Building player"));
            return progress;
        }

        private static Dictionary<string, object> SnapshotProgressItem(string kind, string title)
        {
            return new Dictionary<string, object> { { "kind", kind }, { "title", title }, { "value", null }, { "canWait", true }, { "source", "unity" } };
        }
    }
}
#endif
