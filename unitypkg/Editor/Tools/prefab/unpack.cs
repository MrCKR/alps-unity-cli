#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli
{
    /// <summary>
    /// prefab.unpack — 解包场景中的 Prefab 实例为普通 GameObject。支持 dryRun 预览。
    /// </summary>
    public class PrefabUnpackTool : IUnityCliTool<PrefabUnpackTool.Request, PrefabUnpackTool.Result>
    {
        public sealed class Request
        {
            public string Target { get; set; }
            public string Path { get; set; }
        }

        public sealed class Result
        {
            public string Command { get; set; }
            public bool? DryRun { get; set; }
            public GameObjectRef Target { get; set; }
            public List<PrefabPlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "prefab.unpack",
            Summary = "Unpack a prefab instance in the scene into a plain GameObject; supports --dry-run.",
            Description = "Unpack a prefab instance in the scene into a plain GameObject via PrefabUtility.UnpackPrefabInstance (OutermostRoot). The target must be a prefab instance. Supports --dry-run to preview the planned unpack.",
            Group = "prefab",
            Version = "1.0.0",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "scene-modify" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Unpack a prefab instance", Input = new Request { Target = "Player" } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            PrefabToolSupport.EnsureEditorNotBusy("prefab.unpack");
            var gameObject = PrefabToolSupport.ResolveGameObjectByPath(request.Target ?? request.Path);
            if (gameObject == null)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TARGET_NOT_FOUND", "GameObject not found for prefab.unpack.", "Pass a prefab instance path or name.");
            }
            if (!PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                throw new UnityCliBridge.BridgeCommandException("E_NOT_PREFAB_INSTANCE", gameObject.name + " is not a prefab instance.", "Pass a GameObject that is a prefab instance.");
            }
            if (ctx.DryRun)
            {
                return new System.Threading.Tasks.ValueTask<Result>(new Result
                {
                    Command = "prefab.unpack",
                    DryRun = true,
                    Target = GameObjectToolSupport.BuildGameObjectRef(gameObject),
                    PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.unpack", Target = GameObjectToolSupport.HierarchyPath(gameObject) } }
                });
            }
            var gameObjectId = gameObject.GetInstanceID();
            ctx.CommitEffect();
            PrefabUtility.UnpackPrefabInstance(gameObject, PrefabUnpackMode.OutermostRoot, InteractionMode.AutomatedAction);
            UnityCliSceneMutation.MarkSceneDirty(gameObject.scene);
            // 解包后只接受由 immutable ID 重新解析的对象状态。
            var observed = EditorUtility.InstanceIDToObject(gameObjectId) as GameObject;
            if (observed == null || PrefabUtility.IsPartOfPrefabInstance(observed))
                throw new UnityCliBridge.BridgeCommandException("E_PREFAB_UNPACK_STATE_MISMATCH", "Prefab instance remained linked after unpack.", "Inspect the scene object and retry after the Editor is idle.");
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Command = "prefab.unpack",
                Target = GameObjectToolSupport.BuildGameObjectRef(observed),
                PlannedEffects = new List<PrefabPlannedEffect> { new PrefabPlannedEffect { Type = "prefab.unpack", Target = GameObjectToolSupport.HierarchyPath(observed) } }
            });
        }
    }
}
#endif
