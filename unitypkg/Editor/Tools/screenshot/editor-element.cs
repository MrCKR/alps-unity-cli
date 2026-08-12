#if UNITY_EDITOR
using System.Collections.Generic;

namespace UnityCli
{
    /// <summary>捕获一个attached Unity Editor UI Toolkit element为bounded PNG artifact。</summary>
    public sealed class ScreenshotEditorElementTool : IUnityCliTool<EditorElementCaptureRequest, EditorElementCaptureResult>
    {
        public ToolDescriptor Describe() => new()
        {
            Name = "screenshot.editor-element",
            Summary = "Capture one bounded Unity Editor UI Toolkit element as a verified PNG artifact.",
            Description = "Unity 6000.7+ only. Selects an attached EditorWindow by exact type name or title, then resolves a UI Toolkit selector with up to 16 type/#id/.class descendant or child segments. The scan is capped at 10000 elements. Capture dimensions default to 2048x2048 maximum and cannot exceed 4096x4096; PNG bytes default to 8 MiB maximum and cannot exceed 32 MiB. The artifact is written only to the request artifact directory; inline or arbitrary output paths are not accepted.",
            Group = "screenshot",
            DestructiveHint = false,
            SupportsDryRun = true,
            SideEffects = new[] { "editor-ui-render", "artifact-write" },
            Permissions = new[] { "artifact-write" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 120000,
            Examples = new[]
            {
                new ToolExample { Description = "Capture the Scene view root element.", Input = new EditorElementCaptureRequest { Window = "SceneView", Selector = "*", MaxWidth = 2048, MaxHeight = 2048 } },
                new ToolExample { Description = "Preview an Inspector selector without rendering an artifact.", Input = new EditorElementCaptureRequest { Window = "InspectorWindow", Selector = ".unity-inspector-element", MaxBytes = 8388608 }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        /// <summary>window、element、dimensions与byte ceiling均在唯一capture effect前冻结。</summary>
        public System.Threading.Tasks.ValueTask<EditorElementCaptureResult> ExecuteAsync(EditorElementCaptureRequest request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var plan = EditorElementCaptureToolSupport.Prepare(request);
            if (ctx.DryRun) return new System.Threading.Tasks.ValueTask<EditorElementCaptureResult>(EditorElementCaptureToolSupport.Preview(plan));
            ctx.CommitEffect();
            return new System.Threading.Tasks.ValueTask<EditorElementCaptureResult>(EditorElementCaptureToolSupport.Capture(plan, ctx));
        }
    }

    public sealed class EditorElementCaptureRequest
    {
        [UnityCliRequired]
        public string Window { get; set; }
        [UnityCliRequired]
        public string Selector { get; set; }
        public long? MaxWidth { get; set; }
        public long? MaxHeight { get; set; }
        public long? MaxBytes { get; set; }
    }

    public sealed class EditorElementWindowIdentity
    {
        public string Type { get; set; }
        public string Title { get; set; }
        public long InstanceId { get; set; }
    }

    public sealed class EditorElementIdentity
    {
        public string Selector { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public List<string> Classes { get; set; }
    }

    public sealed class EditorElementCapturePlannedEffect
    {
        public string Type { get; set; }
        public long WindowInstanceId { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public long MaxBytes { get; set; }
    }

    public sealed class EditorElementCaptureResult
    {
        public bool Available { get; set; }
        public bool DryRun { get; set; }
        public EditorElementWindowIdentity Window { get; set; }
        public EditorElementIdentity Element { get; set; }
        public long Width { get; set; }
        public long Height { get; set; }
        public ScreenshotArtifactProof Artifact { get; set; }
        public List<EditorElementCapturePlannedEffect> PlannedEffects { get; set; }
    }
}
#endif
