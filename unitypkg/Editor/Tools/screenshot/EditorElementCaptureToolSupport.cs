#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if UNITY_6000_7_OR_NEWER
using UnityEngine.UIElements;
#endif

namespace UnityCli
{
    /// <summary>Unity 6000.7+ UI Toolkit element解析、bounded capture和artifact proof owner。</summary>
    internal static class EditorElementCaptureToolSupport
    {
        internal const int MaxDimension = 4096;
        internal const long MaxArtifactBytes = 32L * 1024L * 1024L;

        internal sealed class Plan
        {
#if UNITY_6000_7_OR_NEWER
            internal VisualElement Element;
#endif
            internal string WindowType, WindowTitle, Selector, ElementType, ElementName;
            internal List<string> ElementClasses;
            internal long WindowInstanceId, MaxBytes;
            internal int Width, Height;
        }

        internal static Plan Prepare(EditorElementCaptureRequest request)
        {
#if !UNITY_6000_7_OR_NEWER
            throw Unavailable();
#else
            EditorToolSupport.EnsureEditorNotBusy("screenshot.editor-element");
            if (request == null || string.IsNullOrWhiteSpace(request.Window)) throw Error("E_EDITOR_ELEMENT_WINDOW_REQUIRED", "window is required.");
            if (string.IsNullOrWhiteSpace(request.Selector) || request.Selector.Length > 1024) throw Error("E_EDITOR_ELEMENT_SELECTOR_INVALID", "selector must contain 1..1024 characters.");
            var maxWidth = request.MaxWidth ?? 2048L;
            var maxHeight = request.MaxHeight ?? 2048L;
            var maxBytes = request.MaxBytes ?? (8L * 1024L * 1024L);
            if (maxWidth < 1 || maxWidth > MaxDimension || maxHeight < 1 || maxHeight > MaxDimension || maxBytes < 1024 || maxBytes > MaxArtifactBytes)
                throw Error("E_EDITOR_ELEMENT_LIMIT_INVALID", "maxWidth/maxHeight must be 1..4096 and maxBytes must be 1024..33554432.");
            var candidates = Resources.FindObjectsOfTypeAll<EditorWindow>()
                .Where(value => value != null && value.rootVisualElement != null && value.rootVisualElement.panel != null)
                .Where(value => string.Equals(value.GetType().Name, request.Window, StringComparison.Ordinal) || string.Equals(value.titleContent == null ? null : value.titleContent.text, request.Window, StringComparison.Ordinal))
                .OrderBy(value => value.GetInstanceID()).Take(257).ToList();
            if (candidates.Count == 0) throw Error("E_EDITOR_ELEMENT_WINDOW_NOT_FOUND", "No attached EditorWindow exactly matches '" + request.Window + "'.");
            if (candidates.Count > 256) throw Error("E_EDITOR_ELEMENT_LIMIT_EXCEEDED", "More than 256 EditorWindow candidates matched the bounded scan.");
            var window = candidates[0];
            var element = Resolve(window.rootVisualElement, request.Selector);
            if (element == null) throw Error("E_EDITOR_ELEMENT_NOT_FOUND", "No UI Toolkit element matched selector '" + request.Selector + "'.");
            var width = Mathf.CeilToInt(element.worldBound.width);
            var height = Mathf.CeilToInt(element.worldBound.height);
            if (width < 1 || height < 1 || width > maxWidth || height > maxHeight || (long)width * height > 16777216L)
                throw Error("E_EDITOR_ELEMENT_LIMIT_EXCEEDED", "Resolved element dimensions " + width + "x" + height + " exceed the requested capture bounds.");
            return new Plan { Element = element, WindowType = window.GetType().Name, WindowTitle = window.titleContent == null ? null : window.titleContent.text, WindowInstanceId = window.GetInstanceID(), Selector = request.Selector, ElementType = element.GetType().Name, ElementName = element.name, ElementClasses = element.GetClasses().Take(128).Select(Bound).ToList(), Width = width, Height = height, MaxBytes = maxBytes };
#endif
        }

        /// <summary>调用方必须先跨barrier；render state和temporary texture总在finally恢复。</summary>
        internal static EditorElementCaptureResult Capture(Plan plan, UnityCliProjectToolContext ctx)
        {
#if !UNITY_6000_7_OR_NEWER
            throw Unavailable();
#else
            if (plan == null || plan.Element == null || plan.Element.panel == null) throw Error("E_EDITOR_ELEMENT_TARGET_CHANGED", "The frozen UI Toolkit element is no longer attached.");
            RenderTexture texture = null;
            Texture2D readable = null;
            var previous = RenderTexture.active;
            byte[] png;
            try
            {
                texture = plan.Element.CaptureToRenderTexture();
                if (texture == null || texture.width != plan.Width || texture.height != plan.Height) throw Error("E_EDITOR_ELEMENT_TARGET_CHANGED", "The captured element dimensions changed after the effect barrier.");
                RenderTexture.active = texture;
                readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                readable.Apply(false, false);
                png = readable.EncodeToPNG();
            }
            catch (UnityCliBridge.BridgeCommandException) { throw; }
            catch (Exception exception) { throw Error("E_EDITOR_ELEMENT_CAPTURE_FAILED", "UI Toolkit capture failed: " + exception.GetType().Name + "."); }
            finally
            {
                RenderTexture.active = previous;
                if (texture != null) { texture.Release(); UnityEngine.Object.DestroyImmediate(texture); }
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
            if (png == null || png.LongLength < 8 || png.LongLength > plan.MaxBytes) throw Error("E_EDITOR_ELEMENT_LIMIT_EXCEEDED", "Encoded PNG is empty or exceeds maxBytes.");
            if (string.IsNullOrWhiteSpace(ctx.ArtifactDir)) throw Error("E_EDITOR_ELEMENT_ARTIFACT_UNAVAILABLE", "The request artifact directory is unavailable.");
            Directory.CreateDirectory(ctx.ArtifactDir);
            var output = Path.Combine(ctx.ArtifactDir, "editor-element.png");
            var artifact = ScreenshotToolSupport.WriteScreenshotBytes(output, png);
            if (artifact.SizeBytes > plan.MaxBytes || artifact.SizeBytes != png.LongLength)
            {
                try { File.Delete(output); } catch { }
                throw Error("E_EDITOR_ELEMENT_ARTIFACT_INVALID", "The persisted PNG does not match the bounded encoded artifact.");
            }
            return Result(plan, false, artifact);
#endif
        }

        internal static EditorElementCaptureResult Preview(Plan plan) => Result(plan, true, null);

        private static EditorElementCaptureResult Result(Plan plan, bool dryRun, ScreenshotArtifactProof artifact)
        {
            return new EditorElementCaptureResult { Available = true, Window = new EditorElementWindowIdentity { Type = plan.WindowType, Title = plan.WindowTitle, InstanceId = plan.WindowInstanceId }, Element = new EditorElementIdentity { Selector = plan.Selector, Type = plan.ElementType, Name = plan.ElementName, Classes = plan.ElementClasses }, Width = plan.Width, Height = plan.Height, Artifact = artifact, DryRun = dryRun, PlannedEffects = new List<EditorElementCapturePlannedEffect> { new EditorElementCapturePlannedEffect { Type = "screenshot.editor-element", WindowInstanceId = plan.WindowInstanceId, Width = plan.Width, Height = plan.Height, MaxBytes = plan.MaxBytes } } };
        }

#if UNITY_6000_7_OR_NEWER
        /// <summary>实现type/#id/.class与descendant/child链；全查询最多16段、10000候选。</summary>
        private static VisualElement Resolve(VisualElement root, string selector)
        {
            var tokens = selector.Replace(">", " > ").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var segments = tokens.Count(token => token != ">");
            if (segments < 1 || segments > 16) throw Error("E_EDITOR_ELEMENT_SELECTOR_INVALID", "selector must contain 1..16 simple segments.");
            var current = new List<VisualElement> { root };
            var direct = false;
            var visited = 0;
            foreach (var token in tokens)
            {
                if (token == ">") { direct = true; continue; }
                var next = new List<VisualElement>();
                foreach (var parent in current)
                {
                    var values = direct ? parent.Children() : Descendants(parent, ref visited);
                    foreach (var value in values)
                    {
                        if (++visited > 10000) throw Error("E_EDITOR_ELEMENT_LIMIT_EXCEEDED", "selector scan exceeded 10000 UI elements.");
                        if (Matches(value, token)) next.Add(value);
                        if (next.Count > 256) throw Error("E_EDITOR_ELEMENT_LIMIT_EXCEEDED", "selector produced more than 256 intermediate matches.");
                    }
                }
                current = next;
                direct = false;
                if (current.Count == 0) return null;
            }
            return current.FirstOrDefault();
        }

        private static IEnumerable<VisualElement> Descendants(VisualElement root, ref int visited)
        {
            var result = new List<VisualElement>();
            var stack = new Stack<VisualElement>(root.Children().Reverse());
            while (stack.Count > 0)
            {
                var value = stack.Pop();
                result.Add(value);
                if (result.Count + visited > 10000) break;
                foreach (var child in value.Children().Reverse()) stack.Push(child);
            }
            return result;
        }

        private static bool Matches(VisualElement value, string simple)
        {
            var typeEnd = simple.IndexOfAny(new[] { '#', '.' });
            var type = typeEnd < 0 ? simple : simple.Substring(0, typeEnd);
            if (!string.IsNullOrEmpty(type) && type != "*" && !string.Equals(value.GetType().Name, type, StringComparison.Ordinal)) return false;
            var position = Math.Max(0, typeEnd);
            while (position < simple.Length)
            {
                var marker = simple[position++];
                var next = simple.IndexOfAny(new[] { '#', '.' }, position);
                var name = simple.Substring(position, (next < 0 ? simple.Length : next) - position);
                if (string.IsNullOrEmpty(name)) return false;
                if (marker == '#' && !string.Equals(value.name, name, StringComparison.Ordinal)) return false;
                if (marker == '.' && !value.ClassListContains(name)) return false;
                position = next < 0 ? simple.Length : next;
            }
            return true;
        }
#endif

        private static string Bound(string value) => value == null || value.Length <= 1024 ? value : value.Substring(0, 1024);
        private static UnityCliBridge.BridgeCommandException Error(string code, string message) => new UnityCliBridge.BridgeCommandException(code, message, "Inspect the target Editor window and tighten the bounded selector before retrying.");
        private static UnityCliBridge.BridgeCommandException Unavailable() => new UnityCliBridge.BridgeCommandException("E_EDITOR_ELEMENT_CAPTURE_UNAVAILABLE", "screenshot.editor-element requires Unity 6000.7 or newer.", "Use a Unity 6000.7+ Editor instance.");
    }
}
#endif
