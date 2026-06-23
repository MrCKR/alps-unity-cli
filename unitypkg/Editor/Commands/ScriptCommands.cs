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
            RegisterCommand(registry, "script.hash", "script", false, ScriptHash);
            RegisterCommand(registry, "script.edit", "script", true, ScriptEdit);
            RegisterCommand(registry, "script.apply-edits", "script", true, ScriptApplyEdits);
            RegisterCommand(registry, "script.validate", "script", false, ScriptValidate);
            RegisterCommand(registry, "script.update-or-create", "script", true, ScriptUpdateOrCreate);
            RegisterCommand(registry, "script.find-in-file", "script", false, ScriptFindInFile);
            RegisterCommand(registry, "script.capabilities", "script", false, ScriptCapabilities);
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
                .Prop("sha256", Sha256Hex(content))
                .Prop("encoding", "utf-8")
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ScriptHash(BridgeRequest request)
        {
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var content = File.ReadAllText(AssetFullPath(path), Encoding.UTF8);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", ScriptJson(path))
                .Prop("sha256", Sha256Hex(content))
                .Prop("encoding", "utf-8")
                .Prop("logPath", LogPath())
                .EndObject().ToString());
        }

        private static string ScriptEdit(BridgeRequest request)
        {
            EnsureEditorNotBusy("script.edit");
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var fullPath = AssetFullPath(path);
            var originalContent = File.ReadAllText(fullPath, Encoding.UTF8);
            EnsureScriptPrecondition(request, originalContent);
            var content = request.StringArg("content");
            if (content == null)
            {
                content = ApplyReplacementScriptEdit(request, originalContent);
            }
            WriteScriptContent(path, fullPath, content);
            return SuccessJson(ScriptMutationJson("script.edit", path, originalContent, content));
        }

        private static string ScriptApplyEdits(BridgeRequest request)
        {
            EnsureEditorNotBusy("script.apply-edits");
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var fullPath = AssetFullPath(path);
            var originalContent = File.ReadAllText(fullPath, Encoding.UTF8);
            EnsureScriptPrecondition(request, originalContent);
            var editedContent = ApplyStructuredScriptEdits(request, originalContent);
            WriteScriptContent(path, fullPath, editedContent);
            return SuccessJson(ScriptMutationJson("script.apply-edits", path, originalContent, editedContent));
        }

        /// <summary>更新或创建脚本：存在则覆盖，不存在则创建。对齐 Ivan update-or-create。</summary>
        private static string ScriptUpdateOrCreate(BridgeRequest request)
        {
            EnsureEditorNotBusy("script.update-or-create");
            var path = RequireScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var content = request.StringArg("content");
            var fullPath = AssetFullPath(path);
            var exists = File.Exists(fullPath);
            var beforeSha = exists ? Sha256Hex(File.ReadAllText(fullPath, Encoding.UTF8)) : null;
            if (request.DryRun)
            {
                return SuccessJson(new UnityCliJsonWriter().BeginObject()
                    .Prop("command", "script.update-or-create").Prop("dryRun", true)
                    .Prop("path", path).Prop("exists", exists)
                    .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", exists ? "script.overwrite" : "script.create").Prop("path", path).EndObject())
                    .Prop("logPath", LogPath()).EndObject().ToString());
            }
            CreateAssetDirectory(path);
            File.WriteAllText(fullPath, content ?? string.Empty, Encoding.UTF8);
            AssetDatabase.ImportAsset(path);
            var afterSha = Sha256Hex(content ?? string.Empty);
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", ScriptJson(path))
                .Prop("sha256", afterSha).Prop("previousSha256", beforeSha)
                .Prop("created", !exists)
                .PropArray("plannedEffects", arr => arr.BeginObject().Prop("type", exists ? "script.overwrite" : "script.create").Prop("path", path).EndObject())
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>在脚本文件中查找文本：返回匹配行。对齐 CoplayDev find_in_file。</summary>
        private static string ScriptFindInFile(BridgeRequest request)
        {
            var path = RequireExistingScriptPath(request.StringArg("path") ?? request.StringArg("target"));
            var query = request.StringArg("query") ?? request.StringArg("search") ?? request.StringArg("pattern");
            if (string.IsNullOrEmpty(query))
            {
                throw new BridgeCommandException("E_QUERY_REQUIRED", "script.find-in-file requires a query.", "Pass --query <text>.");
            }
            var regex = request.BoolArg("regex", false);
            var ignoreCase = request.BoolArg("ignoreCase", true);
            var lines = File.ReadAllLines(AssetFullPath(path), Encoding.UTF8);
            var matches = new List<string>();
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var isMatch = regex
                    ? System.Text.RegularExpressions.Regex.IsMatch(line, query, ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None)
                    : line.IndexOf(query, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) >= 0;
                if (isMatch)
                {
                    matches.Add(new UnityCliJsonWriter().BeginObject().Prop("line", (long)(i + 1)).Prop("text", line).EndObject().ToString());
                }
            }
            return SuccessJson(new UnityCliJsonWriter().BeginObject()
                .Prop("command", "script.find-in-file").Prop("path", path).Prop("query", query)
                .Prop("count", (long)matches.Count)
                .PropArray("matches", arr => { foreach (var m in matches) arr.RawValue(m); })
                .Prop("logPath", LogPath()).EndObject().ToString());
        }

        /// <summary>脚本能力描述：列出可用 script.* 工具与约束。对齐 CoplayDev manage_script_capabilities。</summary>
        private static string ScriptCapabilities(BridgeRequest request)
        {
            var caps = new[] {
                new { tool = "script.create", risk = "write", description = "Create a new C# script." },
                new { tool = "script.read", risk = "read-only", description = "Read script content." },
                new { tool = "script.hash", risk = "read-only", description = "Compute SHA-256 of script content." },
                new { tool = "script.edit", risk = "high", description = "Apply find/replace text edits with SHA precondition." },
                new { tool = "script.apply-edits", risk = "high", description = "Apply structured/anchor edits with SHA precondition." },
                new { tool = "script.validate", risk = "read-only", description = "Validate script syntax without compiling." },
                new { tool = "script.update-or-create", risk = "high", description = "Update or create a script, overwriting if exists." },
                new { tool = "script.find-in-file", risk = "read-only", description = "Find text matches in a script file." },
                new { tool = "code.exec", risk = "high", description = "Execute dynamic C# code with ObjectRef parameters." }
            };
            var w = new UnityCliJsonWriter().BeginObject();
            w.Prop("command", "script.capabilities");
            w.PropArray("capabilities", arr =>
            {
                foreach (var c in caps)
                {
                    arr.BeginObject().Prop("tool", c.tool).Prop("risk", c.risk).Prop("description", c.description).EndObject();
                }
            });
            w.Prop("count", (long)caps.Length);
            w.Prop("logPath", LogPath());
            return w.EndObject().ToString();
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

        private static string ScriptMutationJson(string type, string path, string beforeContent = null, string afterContent = null)
        {
            AssetDatabase.Refresh();
            return new UnityCliJsonWriter().BeginObject()
                .PropRaw("script", ScriptJson(path))
                .Prop("sha256", afterContent == null ? null : Sha256Hex(afterContent))
                .Prop("previousSha256", beforeContent == null ? null : Sha256Hex(beforeContent))
                .PropArray("plannedEffects", arr => arr.RawValue(EffectJson(type, "path", Quote(path))))
                .Prop("logPath", LogPath())
                .EndObject().ToString();
        }

        private static void EnsureScriptPrecondition(BridgeRequest request, string currentContent)
        {
            var expected = request.StringArg("preconditionSha256") ?? request.StringArg("sha256");
            if (string.IsNullOrEmpty(expected)) return;
            var actual = Sha256Hex(currentContent);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new BridgeCommandException("E_SCRIPT_PRECONDITION_FAILED", "Script content hash changed before edit.", "Re-read the script, verify the change, then retry with the latest preconditionSha256.");
            }
        }

        private static string ApplyReplacementScriptEdit(BridgeRequest request, string originalContent)
        {
            var find = request.StringArg("find");
            var replace = request.StringArg("replace") ?? string.Empty;
            if (!string.IsNullOrEmpty(find))
            {
                return request.BoolArg("all", false) ? originalContent.Replace(find, replace) : ReplaceFirst(originalContent, find, replace);
            }
            var patch = request.StringArg("patch");
            if (patch == null)
            {
                throw new BridgeCommandException("E_SCRIPT_EDIT_REQUIRED", "script.edit requires --content, --content-file, --find/--replace, --patch, or structured --edits.", "Pass replacement content or a targeted edit.");
            }
            return patch;
        }

        private static string ApplyStructuredScriptEdits(BridgeRequest request, string content)
        {
            var editsJson = request.RawJsonArg("edits");
            if (string.IsNullOrWhiteSpace(editsJson))
            {
                var replacementContent = request.StringArg("content");
                return replacementContent ?? ApplyReplacementScriptEdit(request, content);
            }
            foreach (var editBody in EnumerateStructuredScriptEditBodies(editsJson))
            {
                content = ApplyStructuredScriptEdit(editBody, content);
            }
            return content;
        }

        private static IEnumerable<string> EnumerateStructuredScriptEditBodies(string editsJson)
        {
            var raw = editsJson.Trim();
            if (raw.StartsWith("\"", StringComparison.Ordinal) && raw.EndsWith("\"", StringComparison.Ordinal) && raw.Length >= 2)
            {
                raw = raw.Substring(1, raw.Length - 2).Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");
            }
            if (raw.StartsWith("[", StringComparison.Ordinal))
            {
                foreach (Match match in Regex.Matches(raw, "\\{(?<body>.*?)\\}", RegexOptions.Singleline))
                {
                    yield return match.Groups["body"].Value;
                }
                yield break;
            }
            if (raw.StartsWith("{", StringComparison.Ordinal))
            {
                foreach (Match match in Regex.Matches(raw, "\\{(?<body>.*?)\\}", RegexOptions.Singleline))
                {
                    yield return match.Groups["body"].Value;
                }
                yield break;
            }
            throw new BridgeCommandException("E_SCRIPT_EDIT_INVALID", "Structured edits must be a JSON object or array.", "Pass edits via tools run script.apply-edits --input-file with an edits array.");
        }

        private static string ApplyStructuredScriptEdit(string editBody, string content)
        {
            var kind = JsonField(editBody, "kind") ?? "text";
            if (kind == "text")
            {
                var oldText = JsonField(editBody, "oldText") ?? JsonField(editBody, "find");
                var newText = JsonField(editBody, "newText") ?? JsonField(editBody, "replace") ?? string.Empty;
                if (string.IsNullOrEmpty(oldText)) throw new BridgeCommandException("E_SCRIPT_EDIT_INVALID", "Text edit requires oldText.", "Pass oldText and newText for text edits.");
                return ReplaceFirst(content, oldText, newText);
            }
            if (kind == "anchor")
            {
                var anchor = JsonField(editBody, "anchor");
                var newText = JsonField(editBody, "newText") ?? JsonField(editBody, "content") ?? string.Empty;
                var position = JsonField(editBody, "position") ?? "after";
                return ApplyAnchorEdit(content, anchor, newText, position);
            }
            if (kind == "class" || kind == "method")
            {
                var name = JsonField(editBody, "name");
                var action = JsonField(editBody, "action") ?? "insert";
                var newText = JsonField(editBody, "newText") ?? JsonField(editBody, "content") ?? string.Empty;
                return ApplyDeclarationEdit(content, kind, name, action, newText);
            }
            throw new BridgeCommandException("E_SCRIPT_EDIT_KIND_UNSUPPORTED", "Unsupported script edit kind: " + kind, "Use text, anchor, class, or method edits.");
        }

        private static string ApplyAnchorEdit(string content, string anchor, string newText, string position)
        {
            if (string.IsNullOrEmpty(anchor)) throw new BridgeCommandException("E_SCRIPT_EDIT_INVALID", "Anchor edit requires anchor.", "Pass anchor text and position before|after|replace.");
            var index = content.IndexOf(anchor, StringComparison.Ordinal);
            if (index < 0) throw new BridgeCommandException("E_SCRIPT_ANCHOR_NOT_FOUND", "Anchor text was not found in script.", "Check the anchor text against the current script content.");
            if (position == "before") return content.Substring(0, index) + newText + content.Substring(index);
            if (position == "replace") return content.Substring(0, index) + newText + content.Substring(index + anchor.Length);
            return content.Substring(0, index + anchor.Length) + newText + content.Substring(index + anchor.Length);
        }

        private static string ApplyDeclarationEdit(string content, string kind, string name, string action, string newText)
        {
            if (string.IsNullOrEmpty(name)) throw new BridgeCommandException("E_SCRIPT_EDIT_INVALID", kind + " edit requires name.", "Pass the class or method name to edit.");
            var pattern = kind == "class" ? "\\bclass\\s+" + Regex.Escape(name) + "\\b" : "\\b" + Regex.Escape(name) + "\\s*\\(";
            var match = Regex.Match(content, pattern);
            if (action == "insert")
            {
                var insertAt = FindLastBrace(content);
                return content.Substring(0, insertAt) + newText + content.Substring(insertAt);
            }
            if (!match.Success) throw new BridgeCommandException("E_SCRIPT_DECLARATION_NOT_FOUND", kind + " not found: " + name, "Check the class or method name against the current script content.");
            var blockStart = content.IndexOf('{', match.Index);
            if (blockStart < 0) throw new BridgeCommandException("E_SCRIPT_DECLARATION_NOT_FOUND", kind + " body not found: " + name, "Ensure the declaration has a block body.");
            var blockEnd = FindMatchingBrace(content, blockStart);
            if (action == "delete") return content.Substring(0, match.Index) + content.Substring(blockEnd + 1);
            return content.Substring(0, match.Index) + newText + content.Substring(blockEnd + 1);
        }

        private static int FindLastBrace(string content)
        {
            var index = content.LastIndexOf('}');
            return index < 0 ? content.Length : index;
        }

        private static int FindMatchingBrace(string content, int openIndex)
        {
            var depth = 0;
            for (var i = openIndex; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                if (content[i] == '}') depth--;
                if (depth == 0) return i;
            }
            throw new BridgeCommandException("E_SCRIPT_BRACE_UNBALANCED", "Could not find matching brace for script edit.", "Run script.validate and fix syntax before applying structured edits.");
        }

        private static string JsonField(string body, string name)
        {
            var match = Regex.Match(body ?? string.Empty, "\\\"" + Regex.Escape(name) + "\\\"\\s*:\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"");
            return match.Success ? match.Groups[1].Value.Replace("\\n", "\n").Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
        }

        private static void WriteScriptContent(string path, string fullPath, string content)
        {
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
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
