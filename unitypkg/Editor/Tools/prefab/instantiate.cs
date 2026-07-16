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
            var parentName = request.Parent;
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
            var prefab = AssetDatabase.LoadMainAssetAtPath(path) as GameObject;
            if (prefab == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_NOT_FOUND", "Prefab asset not found: " + path, "Run tools run asset.search --input '{\"filter\":\"t:Prefab\"}' to discover the path.");
            }
            Transform parent = null;
            if (!string.IsNullOrEmpty(parentName))
            {
                var parentGo = PrefabToolSupport.ResolveGameObjectByPath(parentName);
                if (parentGo == null) throw new UnityCliBridge.BridgeCommandException("E_PARENT_NOT_FOUND", "Parent not found: " + parentName, "Pass a valid path.");
                parent = parentGo.transform;
            }
            var instance = EditorApplication.isPlaying
                ? UnityEngine.Object.Instantiate(prefab)
                : (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.gameObject.scene : SceneManager.GetActiveScene());
            if (parent != null) instance.transform.SetParent(parent, false);
            instance.transform.position = new Vector3(posX, posY, posZ);
            if (!string.IsNullOrEmpty(request.Name)) instance.name = request.Name;
            UnityCliSceneMutation.MarkSceneDirty(instance.scene);
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "prefab.instantiate",
                Instance = GameObjectToolSupport.BuildGameObjectRef(instance),
                SourcePath = path,
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.instantiate", Path = path } }
            });
        }
    }
}
#endif
