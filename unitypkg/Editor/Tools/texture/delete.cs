#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace UnityCli
{
    /// <summary>
    /// texture.delete — 删除纹理资产。destructive,支持 dryRun。
    /// </summary>
    public class TextureDeleteTool : IUnityCliTool<TextureDeleteTool.Request, TextureDeleteTool.Result>
    {
        public sealed class Request
        {
            public string Path { get; set; }
            public string Target { get; set; }
        }

        public sealed class Result
        {
            public bool? DryRun { get; set; }
            public TextureInfo Texture { get; set; }
            public List<TexturePlannedEffect> PlannedEffects { get; set; }
        }

        public ToolDescriptor Describe() => new()
        {
            Name = "texture.delete",
            Summary = "Delete an Assets/ texture asset; destructive, supports --dry-run preview.",
            Description = "Delete an Assets/ texture asset through AssetDatabase. Destructive: removes a persisted texture. Supports --dry-run to preview the planned deletion without removing anything.",
            Group = "texture",
            DestructiveHint = true,
            SupportsDryRun = true,
            SideEffects = new[] { "asset-delete" },
            Target = "editor",
            PlayModeSupport = "editor-only",
            DefaultTimeoutMs = 30000,
            MaxTimeoutMs = 600000,
            Examples = new[]
            {
                new ToolExample { Description = "Preview deleting a texture", Input = new Request { Path = "Assets/Textures/Old.png" }, Execution = new ToolExampleExecution { DryRun = true } }
            }
        };

        public async System.Threading.Tasks.ValueTask<Result> ExecuteAsync(Request request, UnityCliProjectToolContext ctx, System.Threading.CancellationToken ct = default)
        {
            TextureToolSupport.EnsureEditorNotBusy("texture.delete");
            var path = TextureToolSupport.ResolveTexturePath(request.Path ?? request.Target);
            if (ctx.DryRun)
            {
                return new Result
                {
                    DryRun = true,
                    Texture = TextureToolSupport.BuildTextureInfo(path, TextureToolSupport.LoadTextureAsset(path)),
                    PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.delete", Path = path } }
                };
            }
            ctx.CommitEffect();
            if (!AssetDatabase.DeleteAsset(path))
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DELETE_FAILED", "Failed to delete texture asset: " + path, "Check Unity Console and file permissions.");
            }
            AssetDatabase.Refresh();
            var fullPath = AssetToolSupport.AssetFullPath(path);
            var deleted = await ctx.Wait.Until(
                () => !File.Exists(fullPath)
                    && !Directory.Exists(fullPath)
                    && !AssetDatabase.IsValidFolder(path)
                    && AssetDatabase.LoadMainAssetAtPath(path) == null,
                TimeSpan.FromMilliseconds(Math.Max(1, ctx.TimeoutMs)),
                ct);
            var observedExists = File.Exists(fullPath)
                || Directory.Exists(fullPath)
                || AssetDatabase.IsValidFolder(path)
                || AssetDatabase.LoadMainAssetAtPath(path) != null;
            if (!deleted || observedExists)
            {
                throw new UnityCliBridge.BridgeCommandException("E_TEXTURE_DELETE_PROOF_MISSING", "Texture deletion was not observed in the file system and AssetDatabase: " + path, "Inspect Unity Console and file permissions before retrying.");
            }
            return new Result
            {
                Texture = new TextureInfo { Path = path, SizeBytes = 0 },
                PlannedEffects = new List<TexturePlannedEffect> { new TexturePlannedEffect { Type = "texture.delete", Path = path } }
            };
        }
    }
}
#endif
