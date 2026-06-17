#if UNITY_EDITOR
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CSharp;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using UiButton = UnityEngine.UI.Button;
using UiCanvasScaler = UnityEngine.UI.CanvasScaler;
using UiGraphicRaycaster = UnityEngine.UI.GraphicRaycaster;
using UiImage = UnityEngine.UI.Image;
using UiText = UnityEngine.UI.Text;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterScriptCommands(BridgeCommandRegistry registry)
        {
            RegisterCommand(registry, "script.create", "script", true, ScriptCreate);
            RegisterCommand(registry, "script.read", "script", false, ScriptRead);
            RegisterCommand(registry, "script.edit", "script", true, ScriptEdit);
            RegisterCommand(registry, "script.validate", "script", false, ScriptValidate);
        }

        private static string ScriptCreate(BridgeRequest request)
        {
            EnsureEditorNotBusy("script.create");
            var path = RequireScriptPath(request.StringArg("path"));
            if (AssetExists(path))
            {
                throw new BridgeCommandException("E_SCRIPT_OUTPUT_EXISTS", "Script already exists: " + path, "Choose a different Assets/*.cs path or use script edit.");
            }
            var content = request.StringArg("content");
            if (content == null)
            {
                content = DefaultScriptContent(request.StringArg("name"), path);
            }
            WriteTextAsset(path, content, false);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return SuccessJson(ScriptMutationJson("script.create", path));
        }

        private static string ScriptRead(BridgeRequest request)
        {
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            string content;
            try
            {
                content = File.ReadAllText(AssetFullPath(path), Encoding.UTF8);
            }
            catch (Exception exception)
            {
                throw new BridgeCommandException("E_IO_ERROR", "Failed to read script file: " + exception.Message, "Check file encoding and permissions: " + path);
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", ScriptJson(path))
                .Prop("content", content)
                .Prop("encoding", "utf-8")
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ScriptEdit(BridgeRequest request)
        {
            EnsureEditorNotBusy("script.edit");
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var fullPath = AssetFullPath(path);
            var content = request.StringArg("content");
            if (content == null)
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
                var find = request.StringArg("find");
                var replace = request.StringArg("replace") ?? string.Empty;
                if (!string.IsNullOrEmpty(find))
                {
                    content = request.BoolArg("all", false) ? content.Replace(find, replace) : ReplaceFirst(content, find, replace);
                }
                else
                {
                    var patch = request.StringArg("patch");
                    if (patch == null)
                    {
                        throw new BridgeCommandException("E_SCRIPT_EDIT_REQUIRED", "script.edit requires --content, --content-file, --find/--replace or --patch.", "Pass replacement content or a targeted edit.");
                    }
                    content = patch;
                }
            }
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return SuccessJson(ScriptMutationJson("script.edit", path));
        }

        private static string ScriptValidate(BridgeRequest request)
        {
            var path = request.StringArg("path") ?? request.StringArg("target");
            var content = request.StringArg("content");
            if (content == null)
            {
                path = RequireExistingScriptPath(path);
                content = File.ReadAllText(AssetFullPath(path), Encoding.UTF8);
            }
            var diagnostics = new List<string>();
            if (string.IsNullOrWhiteSpace(content))
            {
                diagnostics.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("level", "error")
                    .Prop("code", "SCRIPT_EMPTY")
                    .Prop("message", "Script content is empty.")
                    .EndObject().ToString());
            }
            if (!HasBalancedDelimiters(content, '{', '}'))
            {
                diagnostics.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("level", "error")
                    .Prop("code", "CS_BRACE_UNBALANCED")
                    .Prop("message", "Curly braces are not balanced.")
                    .EndObject().ToString());
            }
            if (!HasBalancedDelimiters(content, '(', ')'))
            {
                diagnostics.Add(new UnityCliJsonWriter().BeginObject()
                    .Prop("level", "error")
                    .Prop("code", "CS_PAREN_UNBALANCED")
                    .Prop("message", "Parentheses are not balanced.")
                    .EndObject().ToString());
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", string.IsNullOrEmpty(path) ? "null" : ScriptJson(RequireScriptPath(path)))
                .Prop("valid", diagnostics.Count == 0)
                .PropArray("diagnostics", arr => { foreach (var d in diagnostics) arr.RawValue(d); })
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string RequireScriptPath(string rawPath)
        {
            var path = NormalizeAssetPath(rawPath);
            if (string.IsNullOrEmpty(path))
            {
                throw new BridgeCommandException("E_SCRIPT_PATH_REQUIRED", "Pass an Assets/*.cs script path.", "Use unity_cli script read Assets/Scripts/Example.cs.");
            }
            if (!path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_SCRIPT_PATH_INVALID", "Script path must be under Assets/ and end with .cs: " + path, "Pass a Unity C# script asset path such as Assets/Scripts/Example.cs.");
            }
            return path;
        }

        private static string RequireExistingScriptPath(string rawPath)
        {
            var path = RequireScriptPath(rawPath);
            if (!AssetExists(path))
            {
                throw new BridgeCommandException("E_SCRIPT_NOT_FOUND", "Script not found: " + path, "Check the path or run unity_cli asset search --type MonoScript.");
            }
            return path;
        }

        private static string DefaultScriptContent(string rawName, string path)
        {
            var name = string.IsNullOrEmpty(rawName) ? Path.GetFileNameWithoutExtension(path) : rawName;
            name = Regex.Replace(name ?? "NewScript", "[^A-Za-z0-9_]", string.Empty);
            if (string.IsNullOrEmpty(name) || char.IsDigit(name[0]))
            {
                name = "NewScript" + name;
            }
            return "using UnityEngine;\n\npublic class " + name + " : MonoBehaviour\n{\n}\n";
        }

        private static string ScriptJson(string path)
        {
            var fullPath = AssetFullPath(path);
            var fileInfo = new FileInfo(fullPath);
            return new UnityCliJsonWriter().BeginObject()
                .Prop("path", path)
                .Prop("guid", AssetDatabase.AssetPathToGUID(path))
                .Prop("name", Path.GetFileNameWithoutExtension(path))
                .Prop("exists", fileInfo.Exists)
                .Prop("sizeBytes", fileInfo.Exists ? (long)fileInfo.Length : 0L)
                .EndObject().ToString();
        }

        private static string ScriptMutationJson(string type, string path)
        {
            AssetDatabase.Refresh();
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", ScriptJson(path))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson(type, "path", Quote(path))))
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static string ReplaceFirst(string content, string find, string replace)
        {
            var index = content.IndexOf(find, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new BridgeCommandException("E_SCRIPT_FIND_NOT_FOUND", "Find text was not found in script.", "Check --find text or pass --content-file for full replacement.");
            }
            return content.Substring(0, index) + replace + content.Substring(index + find.Length);
        }

        private static bool HasBalancedDelimiters(string content, char open, char close)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            foreach (var ch in content ?? string.Empty)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (ch == '\\') escaped = true;
                    else if (ch == (char)34) inString = false;
                    continue;
                }
                if (ch == (char)34)
                {
                    inString = true;
                    continue;
                }
                if (ch == open) depth++;
                else if (ch == close) depth--;
                if (depth < 0) return false;
            }
            return depth == 0;
        }
    }
}
#endif
