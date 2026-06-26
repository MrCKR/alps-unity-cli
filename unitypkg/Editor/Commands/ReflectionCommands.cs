#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        private static void RegisterReflectionCommands(BridgeCommandRegistry registry)
        {
            // live 反射：对齐 IvanMurzak reflection-method-find/call 与 type-get-json-schema
            RegisterCommand(registry, "reflection.method.find", "reflection", false, ReflectionMethodFind);
            RegisterCommand(registry, "reflection.method.call", "reflection", true, ReflectionMethodCall);
            RegisterCommand(registry, "type.schema", "type", false, TypeSchema);
        }

        /// <summary>查找方法：不限制 public/private/static/instance。只读。</summary>
        private static string ReflectionMethodFind(BridgeRequest request)
        {
            var typeName = request.StringArg("typeName");
            if (string.IsNullOrEmpty(typeName))
            {
                throw new BridgeCommandException("E_TYPE_NAME_REQUIRED",
                    "reflection.method.find requires typeName.",
                    "Pass --input '{\"typeName\":\"UnityEngine.Transform\",\"methodName\":\"Translate\"}'.");
            }
            var methodName = request.StringArg("methodName");
            var includePrivate = request.BoolArg("includePrivate", false);
            var includeStatic = request.BoolArg("includeStatic", true);
            var includeInstance = request.BoolArg("includeInstance", true);

            var bindings = BindingFlags.Public | BindingFlags.NonPublic;
            if (includeStatic) bindings |= BindingFlags.Static;
            if (includeInstance) bindings |= BindingFlags.Instance;
            if (!includePrivate) bindings = BindingFlags.Public | (includeStatic ? BindingFlags.Static : BindingFlags.Default) | (includeInstance ? BindingFlags.Instance : BindingFlags.Default);

            return SuccessJson(ReflectionInvoker.FindMethods(typeName, methodName, bindings));
        }

        /// <summary>调用方法：不做范围限制，高风险，要求 --yes。返回 audit、异常与结果。</summary>
        private static string ReflectionMethodCall(BridgeRequest request)
        {
            EnsureEditorNotBusy("reflection.method.call");
            var typeName = request.StringArg("typeName");
            var methodName = request.StringArg("methodName");
            if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(methodName))
            {
                throw new BridgeCommandException("E_METHOD_TARGET_REQUIRED",
                    "reflection.method.call requires typeName and methodName.",
                    "Run reflection.method.find to discover methods, then call with --yes.");
            }

            // 高风险确认：dry-run 返回预览；真实执行使用 CLI 统一 destructive proof。
            if (request.DryRun || !request.SecurityConfirmed(DestructiveCommandFingerprint))
            {
                var preview = new UnityCliJsonWriter().BeginObject();
                preview.Prop("command", "reflection.method.call");
                preview.Prop("confirmed", false);
                preview.Prop("requiresConfirmation", true);
                preview.Prop("riskLevel", "critical");
                preview.Prop("destructiveHint", true);
                preview.Prop("typeName", typeName);
                preview.Prop("methodName", methodName);
                preview.Prop("confirmationFlag", "--yes");
                preview.PropArray("plannedEffects", arr => arr.BeginObject()
                    .Prop("type", "reflection.method.call")
                    .Prop("target", typeName + "." + methodName)
                    .Prop("sideEffects", "arbitrary-method-invocation")
                    .EndObject());
                preview.Prop("logPath", LogPath());
                preview.EndObject();
                return SuccessJson(preview.ToString());
            }

            var argsJson = request.RawJsonArg("args");
            var instanceRefJson = request.RawJsonArg("instanceRef");
            var bindings = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

            var result = ReflectionInvoker.CallMethod(typeName, methodName, false, argsJson, instanceRefJson, bindings);
            return SuccessJson(result);
        }

        /// <summary>生成类型 JSON Schema。</summary>
        private static string TypeSchema(BridgeRequest request)
        {
            var typeName = request.StringArg("typeName");
            if (string.IsNullOrEmpty(typeName))
            {
                throw new BridgeCommandException("E_TYPE_NAME_REQUIRED",
                    "type.schema requires typeName.",
                    "Pass --input '{\"typeName\":\"UnityEngine.Rigidbody\"}'.");
            }
            var maxDepth = request.IntArg("maxDepth", 3);
            return SuccessJson(TypeSchemaGenerator.Generate(typeName, maxDepth));
        }

        /// <summary>计算高风险操作的确认指纹。</summary>
        private static string SecurityFingerprint(params string[] parts)
        {
            var combined = string.Join("|", parts);
            return Sha256Hex(combined);
        }
    }
}
#endif
