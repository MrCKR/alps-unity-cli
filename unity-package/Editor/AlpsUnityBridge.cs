using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Alps.UnityCli.Bridge
{
    [InitializeOnLoad]
    public static class AlpsUnityBridge
    {
        private const string BridgeVersion = "1.0.0";
        private const string DefaultAgentEndpoint = "http://127.0.0.1:47391";
        private static readonly string LogPath = Path.Combine(Application.dataPath, "../Logs/AlpsUnityBridge.log");
        private static double nextHeartbeatAt;

        static AlpsUnityBridge()
        {
            EditorApplication.update += Heartbeat;
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
                endpoint = DefaultAgentEndpoint
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
