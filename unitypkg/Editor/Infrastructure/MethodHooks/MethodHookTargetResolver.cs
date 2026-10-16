#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BridgeCommandException = UnityCli.UnityCliBridge.BridgeCommandException;

namespace UnityCli
{
    /// <summary>exact target 解析结果；Method 只在当前域有效，重挂必须重新解析。</summary>
    internal sealed class MethodHookResolvedTarget
    {
        internal MethodInfo Method;
        internal string ParameterSignature;
        internal string AssemblyIdentity;
    }

    /// <summary>
    /// exact 目标解析与可支持性判定。
    /// 只接受程序集名、类型全名、方法名、参数类型、返回类型与静态性完全匹配的唯一方法；
    /// 同名或同签名歧义、以及无法验证适配的签名都在 effect 前明确失败。
    /// </summary>
    internal static class MethodHookTargetResolver
    {
        private const BindingFlags DeclaredMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        /// <summary>解析 exact target；找不到、歧义或不支持时抛出稳定错误码。</summary>
        internal static MethodHookResolvedTarget Resolve(MethodHookTargetSpec spec)
        {
            if (spec == null) throw new BridgeCommandException("E_SCHEMA_INVALID", "A method hook requires an exact target.", "Pass assemblyName, typeName, methodName, parameterTypes, returnType and isStatic.");
            if (string.IsNullOrWhiteSpace(spec.AssemblyName) || string.IsNullOrWhiteSpace(spec.TypeName) || string.IsNullOrWhiteSpace(spec.MethodName)
                || string.IsNullOrWhiteSpace(spec.ReturnType) || spec.ParameterTypes == null)
                throw new BridgeCommandException("E_SCHEMA_INVALID", "The exact target requires assemblyName, typeName, methodName, parameterTypes, returnType and isStatic.", "Pass every target identity field; parameterTypes may be an empty array but must be present.");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => string.Equals(assembly.GetName().Name, spec.AssemblyName, StringComparison.Ordinal))
                .ToArray();
            if (assemblies.Length == 0)
                throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "No loaded assembly has the exact name " + spec.AssemblyName + ".", null, new[] { "Use debug.method.find or assemblyName from a loaded type to confirm the current domain." });
            if (assemblies.Length > 1)
                throw new BridgeCommandException("E_HOOK_TARGET_AMBIGUOUS", "Multiple loaded assemblies share the exact name " + spec.AssemblyName + ".", null, new[] { "Reduce duplicate assembly names in the project before targeting this method." });

            var type = assemblies[0].GetType(spec.TypeName, false, false);
            if (type == null)
                throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "The exact type " + spec.TypeName + " does not exist in " + spec.AssemblyName + ".", null, new[] { "Use the reflection full name, including '+' for nested types." });

            var candidates = type.GetMethods(DeclaredMembers)
                .Where(method => string.Equals(method.Name, spec.MethodName, StringComparison.Ordinal))
                .Where(method => method.IsStatic == spec.IsStatic)
                .Where(method => string.Equals(NormalizeTypeName(method.ReturnType), NormalizeTypeName(spec.ReturnType), StringComparison.Ordinal))
                .Where(method => SignatureMatches(method, spec.ParameterTypes))
                .ToArray();
            if (candidates.Length == 0)
                throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "No declared method matches the exact target signature " + Describe(spec) + ".", null, new[] { "Copy parameterTypes and returnType from the live method signature; reflection full names are required." });
            if (candidates.Length > 1)
                throw new BridgeCommandException("E_HOOK_TARGET_AMBIGUOUS", "More than one declared method matches the exact target signature " + Describe(spec) + ".", null, new[] { "Narrow the target with exact parameterTypes so exactly one method matches." });

            var method = candidates[0];
            var unsupported = UnsupportedReason(method);
            if (unsupported != null)
                throw new BridgeCommandException("E_HOOK_METHOD_UNSUPPORTED", "The exact target cannot be intercepted: " + unsupported, null, new[] { "Choose a managed instance or static method with an IL body, or observe the calling method instead." });
            return new MethodHookResolvedTarget
            {
                Method = method,
                ParameterSignature = ParameterSignature(method),
                AssemblyIdentity = method.DeclaringType.Assembly.GetName().FullName
            };
        }

        /// <summary>把已解析方法投影成公开摘要。</summary>
        internal static MethodHookTargetSummary Summarize(MethodHookResolvedTarget resolved, MethodHookTargetSpec spec)
        {
            var method = resolved.Method;
            var summary = new MethodHookTargetSummary
            {
                AssemblyName = spec.AssemblyName,
                AssemblyIdentity = resolved.AssemblyIdentity,
                TypeName = spec.TypeName,
                MethodName = spec.MethodName,
                ParameterTypes = spec.ParameterTypes == null ? new List<string>() : new List<string>(spec.ParameterTypes),
                ReturnType = NormalizeTypeName(method.ReturnType),
                IsStatic = method.IsStatic,
                MethodIdentity = MethodHookMethodText.Describe(method)
            };
            return summary;
        }

        /// <summary>判断方法是否属于本次明确不支持的目标类别；返回 null 表示可以拦截。</summary>
        internal static string UnsupportedReason(MethodInfo method)
        {
            if (method == null) return "the target method is missing.";
            var declaring = method.DeclaringType;
            if (declaring == null) return "the target has no declaring type.";
            if (declaring.IsGenericType) return "the declaring type is generic.";
            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters) return "the method is generic.";
            if (method.IsAbstract) return "the method is abstract.";
            if (string.Equals(method.Name, ".ctor", StringComparison.Ordinal) || string.Equals(method.Name, ".cctor", StringComparison.Ordinal))
                return "constructors and type initializers are not interceptable targets.";
            if (declaring.IsValueType && !method.IsStatic) return "value-type instance methods are not supported.";
            if (method.ReturnType.IsByRef) return "by-ref return values are not supported.";
            if (method.ReturnType.IsPointer || method.GetParameters().Any(parameter => parameter.ParameterType.IsPointer))
                return "pointer signatures are not supported.";
            if (IsStateMachineMethod(method)) return "async or coroutine state machine methods are not supported targets.";
            MethodBody body;
            try { body = method.GetMethodBody(); }
            catch (Exception exception) { return "the method body could not be read (" + exception.GetType().Name + ")."; }
            if (body == null) return "the method has no IL body (native, extern or runtime-provided).";
            return null;
        }

        private static bool IsStateMachineMethod(MethodInfo method)
        {
            var declaring = method.DeclaringType;
            if (declaring == null) return false;
            if (!string.Equals(method.Name, "MoveNext", StringComparison.Ordinal)) return false;
            return declaring.IsDefined(typeof(CompilerGeneratedAttribute), false);
        }

        private static bool SignatureMatches(MethodInfo method, List<string> parameterTypes)
        {
            var parameters = method.GetParameters();
            if (parameters.Length != parameterTypes.Count) return false;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (!string.Equals(NormalizeTypeName(parameters[index].ParameterType), NormalizeTypeName(parameterTypes[index]), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>规范类型名：by-ref 用 &amp; 后缀，其余使用反射 FullName。</summary>
        internal static string NormalizeTypeName(Type type)
        {
            if (type == null) return string.Empty;
            if (type.IsByRef) return NormalizeTypeName(type.GetElementType()) + "&";
            if (type.IsArray) return NormalizeTypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            return type.FullName ?? type.Name;
        }

        /// <summary>规范字符串形式的类型名，允许调用方省略 by-ref 的 &amp; 后缀。</summary>
        private static string NormalizeTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return string.Empty;
            var trimmed = typeName.Trim();
            if (string.Equals(trimmed, "void", StringComparison.Ordinal)) return "System.Void";
            return trimmed;
        }

        internal static string ParameterSignature(MethodBase method)
        {
            var parameters = method.GetParameters();
            var names = new List<string>(parameters.Length);
            foreach (var parameter in parameters) names.Add(NormalizeTypeName(parameter.ParameterType));
            return string.Join(",", names.ToArray());
        }

        private static string Describe(MethodHookTargetSpec spec)
        {
            var types = spec.ParameterTypes == null ? string.Empty : string.Join(", ", spec.ParameterTypes.ToArray());
            return spec.AssemblyName + "!" + spec.TypeName + "." + spec.MethodName + "(" + types + ") : " + spec.ReturnType
                + " [" + (spec.IsStatic ? "static" : "instance") + "]";
        }

        /// <summary>字段解析：declaringType 为空时从实例类型沿继承链查找。</summary>
        internal static FieldInfo ResolveField(Type instanceType, string declaringType, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                throw new BridgeCommandException("E_SCHEMA_INVALID", "A capture field requires fieldName.", "Pass fieldName, and declaringType when the field is declared on a base type.");
            Type owner = null;
            if (!string.IsNullOrWhiteSpace(declaringType))
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    owner = assembly.GetType(declaringType, false, false);
                    if (owner != null) break;
                }
                if (owner == null)
                    throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "The capture field declaring type " + declaringType + " does not exist in the current domain.", null, new[] { "Pass the exact reflection type name of the declaring type, or omit declaringType." });
            }
            else if (instanceType != null)
            {
                owner = instanceType;
            }
            if (owner == null)
                throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "A capture field without declaringType requires an instance target.", null, new[] { "Add declaringType for static fields or static target methods." });

            var field = FindField(owner, fieldName);
            if (field == null)
                throw new BridgeCommandException("E_HOOK_TARGET_NOT_FOUND", "Field " + fieldName + " was not found on " + owner.FullName + " or its base types.", null, new[] { "Inspect the declaring type members before capturing this field." });
            return field;
        }

        private static FieldInfo FindField(Type owner, string fieldName)
        {
            for (var current = owner; current != null; current = current.BaseType)
            {
                var field = current.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }
    }
}
#endif
