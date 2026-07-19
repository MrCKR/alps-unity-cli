#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// prefab.instantiate — 在场景中实例化 Prefab。支持 dryRun 预览。
    /// </summary>
    public class PrefabInstantiateTool : IUnityCliTool<PrefabInstantiateTool.Request, PrefabInstantiateTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Parent { get; set; }
            public string Name { get; set; }
            public double? X { get; set; }
            public double? Y { get; set; }
            public double? Z { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public GameObjectRef Instance { get; set; }
            public string SourcePath { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.instantiate",
            Summary = "Instantiate an Assets/*.prefab into the active scene with optional parent, position and name; supports --dry-run.",
            Description = "Instantiate an Assets/*.prefab into the active scene. Optionally parent under a scene GameObject (name/path/instance id), set world position, and rename the instance. Supports --dry-run to preview the planned instantiation.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "session-affecting",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Instantiate a prefab", Input = new Request { Path = "Assets/Prefabs/Enemy.prefab", X = 0, Y = 0, Z = 0 } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.instantiate");
            var path = PrefabToolSupport.RequirePrefabPath(request.Path);
            GameObjectToolSupport.ValidateTransformMutationValues(request.X, request.Y, request.Z);
            var sourceGuid = AssetDatabase.AssetPathToGUID(path);
            var prefab = PrefabToolSupport.LoadPrefabAsset(path);
            if (string.IsNullOrEmpty(sourceGuid) || AssetDatabase.GUIDToAssetPath(sourceGuid) != path)
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab source GUID could not be resolved: " + path, "Pass an existing canonical Assets/*.prefab path.");
            var parentName = request.Parent;
            Transform parent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = GameObjectToolSupport.ResolveUniqueTarget(parentName, "Parent GameObject is required.");
                parent = parentGo.transform;
            }
            var posX = (float)(request.X ?? 0f);
            var posY = (float)(request.Y ?? 0f);
            var posZ = (float)(request.Z ?? 0f);
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "prefab.instantiate",
                    DryRun = true,
                    SourcePath = path,
                    PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.instantiate", Path = path } }
                });
            }
            ctx.CommitEffect();
            var instance = EditorApplication.isPlaying
                ? UnityEngine.Object.Instantiate(prefab)
                : (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
            if (parent != null) instance.transform.SetParent(parent, false);
            instance.transform.position = new Vector3(posX, posY, posZ);
            if (!string.IsNullOrEmpty(request.Name)) instance.name = request.Name;
            UnityCliSceneMutation.MarkSceneDirty(instance.scene);
            var observedInstance = EditorUtility.InstanceIDToObject(instance.GetInstanceID()) as GameObject;
            var observedSourcePath = AssetDatabase.GUIDToAssetPath(sourceGuid);
            var observedPrefab = string.IsNullOrEmpty(observedSourcePath) ? null : PrefabToolSupport.LoadPrefabAsset(observedSourcePath);
            if (observedInstance == null || observedSourcePath != path || observedPrefab != prefab || PrefabUtility.GetCorrespondingObjectFromSource(observedInstance) != observedPrefab ||
                observedInstance.transform.position != new Vector3(posX, posY, posZ) ||
                (parent != null && observedInstance.transform.parent != parent) ||
                (!string.IsNullOrEmpty(request.Name) && observedInstance.name != request.Name))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_INSTANTIATE_STATE_MISMATCH", "Prefab instance readback did not match the request.", "Inspect the active scene and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "prefab.instantiate",
                Instance = GameObjectToolSupport.BuildGameObjectRef(observedInstance),
                SourcePath = path,
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.instantiate", Path = path } }
            });
        }
    }
}
#endif
