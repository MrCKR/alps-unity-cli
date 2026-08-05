#if UNITY_EDITOR
using UnityEngine.SceneManagement;

namespace UnityCli
{
    /// <summary>
    /// scene.active — 返回当前激活场景的 name/path/buildIndex/isLoaded/isDirty。
    /// </summary>
    public class SceneActiveTool : IUnityCliTool<SceneActiveTool.Request, SceneActiveTool.Result>
    {
        public sealed class Request
        {
            // scene.active 无入参
        }

        public sealed class Result
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public long BuildIndex { get; set; }
            public bool IsLoaded { get; set; }
            public bool IsDirty { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "scene.active",
            Summary = "Return name, path and build index of the currently active scene.",
            Description = "Return name, path and build index of the currently active scene. Read-only; use scene.list-opened for all loaded scenes and scene.set-active to change the active scene.",
            Group = "scene",
            DestructiveHint = false,
            Target = "editor",
            PlayModeSupport = "supported",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Get the active scene", Input = new Request { } }
            }
        };

        public System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            var scene = SceneManager.GetActiveScene();
            return new System.Threading.Tasks.ValueTask<Result>(new Result
            {
                Name = scene.name,
                Path = NormalizeAssetPath(scene.path),
                BuildIndex = scene.buildIndex,
                IsLoaded = scene.isLoaded,
                IsDirty = scene.isDirty
            });
        }

        private static string NormalizeAssetPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace("\\", "/");
        }
    }
}
#endif
