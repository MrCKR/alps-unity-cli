#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace UnityCli.Hooks
{
    /// <summary>
    /// 按 exact target 生成类型化 Adapter。
    ///
    /// Harmony 的 patch 方法必须与目标签名可绑定：
    /// - 所有参数值统一经 object[] __args 传入与写回，因此普通参数与私有参数类型都能改参与观察；
    /// - by-ref 参数额外声明同名 index 的类型化 `ref` 参数。Dev 实测确认只靠 __args 时原方法对 ref 参数的
    ///   写回不会回到调用方，因此这里在派发前后用类型化 ref 参数做精确回写；
    /// - 结果只用与目标返回类型完全一致的 ref __result，绝不使用会猜错返回类型的通用签名。
    ///
    /// 每个独特的签名形状只生成一次并复用；void 结果使用后端里的静态方法。
    /// </summary>
    internal static class MethodHookAdapterEmitter
    {
        private const string AdapterNamespace = "UnityCli.HookAdapters";

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, AdapterMethods> Cache = new Dictionary<string, AdapterMethods>(StringComparer.Ordinal);
        private static AssemblyBuilder assembly;
        private static ModuleBuilder module;

        internal sealed class AdapterMethods
        {
            internal MethodInfo Prefix;
            internal MethodInfo Postfix;
        }

        /// <summary>返回该 target 可用的 Prefix/Postfix 方法；同一签名形状只生成一次。</summary>
        internal static AdapterMethods For(MethodInfo target)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (target.ReturnType == typeof(void)) throw new InvalidOperationException("void 结果必须使用后端内置的 void Adapter。");
            return ForShape(target, false);
        }

        /// <summary>void 结果但含 by-ref 参数时同样需要类型化 ref 回写，因此单独生成无 __result 的 Adapter。</summary>
        internal static AdapterMethods ForVoidWithByRef(MethodInfo target)
        {
            if (target == null) throw new ArgumentNullException("target");
            if (target.ReturnType != typeof(void)) throw new InvalidOperationException("该形状只用于 void 结果。");
            return ForShape(target, true);
        }

        private static AdapterMethods ForShape(MethodInfo target, bool voidResult)
        {
            var key = (voidResult ? "void|" : "value|") + SignatureKey(target);
            lock (Gate)
            {
                AdapterMethods cached;
                if (Cache.TryGetValue(key, out cached)) return cached;
                var created = Emit(target, key, voidResult);
                Cache.Add(key, created);
                return created;
            }
        }

        /// <summary>签名形状键只包含返回类型与 by-ref 参数类型，普通参数共用同一 Adapter。</summary>
        internal static string SignatureKey(MethodInfo target)
        {
            var builder = new StringBuilder();
            builder.Append(Normalize(target.ReturnType)).Append('|');
            foreach (var parameter in target.GetParameters())
                builder.Append(parameter.ParameterType.IsByRef ? Normalize(parameter.ParameterType.GetElementType()) + "&" : "-").Append(',');
            return builder.ToString();
        }

        private static string Normalize(Type type)
        {
            return type == null ? string.Empty : type.FullName ?? type.Name;
        }

        private static AdapterMethods Emit(MethodInfo target, string key, bool voidResult)
        {
            if (assembly == null)
            {
                assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("UnityCli.HookAdapters.Generated"), AssemblyBuilderAccess.Run);
                module = assembly.DefineDynamicModule("UnityCli.HookAdapters.Generated");
            }
            var suffix = StableSuffix(key);
            var typeName = AdapterNamespace + ".Adapter" + Cache.Count.ToString(CultureInfo.InvariantCulture) + "_" + suffix;
            var builder = module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit, typeof(object));
            var prefix = DefinePatchMethod(builder, "Prefix", target, MethodHookInterception.PrefixEntry, true, voidResult);
            var postfix = DefinePatchMethod(builder, "Postfix", target, MethodHookInterception.PostfixEntry, false, voidResult);
            var built = builder.CreateType();
            return new AdapterMethods { Prefix = built.GetMethod("Prefix"), Postfix = built.GetMethod("Postfix") };
        }

        private static string StableSuffix(string key)
        {
            return UnityCliSerializer.Sha256Canonical(key).Substring(0, 8);
        }

        /// <summary>
        /// 生成 Adapter 方法体：
        /// 1. 把 by-ref 参数的当前值同步进 __args；
        /// 2. 调用 host 派发，按需要写回 by-ref 参数与结果；
        /// 3. Prefix 返回是否继续执行原方法。
        /// </summary>
        private static MethodBuilder DefinePatchMethod(TypeBuilder builder, string name, MethodInfo target, MethodInfo interception, bool returnsRunOriginal, bool voidResult)
        {
            var parameters = target.GetParameters();
            var returnType = target.ReturnType;
            var byRefIndexes = new List<int>();
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].ParameterType.IsByRef) byRefIndexes.Add(index);

            var parameterList = new List<KeyValuePair<string, Type>>
            {
                new KeyValuePair<string, Type>("__instance", typeof(object)),
                new KeyValuePair<string, Type>("__args", typeof(object[]))
            };
            var argsArgument = 1;
            var resultArgument = voidResult ? -1 : 2;
            if (!voidResult) parameterList.Add(new KeyValuePair<string, Type>("__result", returnType.MakeByRefType()));

            // by-ref 参数的声明位置必须与下面计算的下标一致：统一放在 __args/__result 之后。
            var argumentIndex = new Dictionary<int, int>();
            var nextArgumentIndex = voidResult ? 2 : 3;
            foreach (var index in byRefIndexes)
            {
                argumentIndex[index] = nextArgumentIndex++;
                parameterList.Add(new KeyValuePair<string, Type>("__" + index.ToString(CultureInfo.InvariantCulture), parameters[index].ParameterType));
            }
            var originalMethodArgument = nextArgumentIndex;
            parameterList.Add(new KeyValuePair<string, Type>("__originalMethod", typeof(MethodBase)));

            var method = builder.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
                returnsRunOriginal ? typeof(bool) : typeof(void), parameterList.Select(pair => pair.Value).ToArray());
            for (var index = 0; index < parameterList.Count; index++)
                method.DefineParameter(index + 1, ParameterAttributes.None, parameterList[index].Key);

            var il = method.GetILGenerator();
            var resultIn = il.DeclareLocal(typeof(object));
            var resultOut = il.DeclareLocal(typeof(object));
            var writeResult = il.DeclareLocal(typeof(bool));

            // __args[i] = (object)__i
            foreach (var index in byRefIndexes)
            {
                var elementType = parameters[index].ParameterType.GetElementType();
                il.Emit(OpCodes.Ldarg, argsArgument);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldarg, argumentIndex[index]);
                il.Emit(OpCodes.Ldobj, elementType);
                if (elementType.IsValueType) il.Emit(OpCodes.Box, elementType);
                il.Emit(OpCodes.Stelem_Ref);
            }

            // resultIn = (object)__result
            if (voidResult)
            {
                il.Emit(OpCodes.Ldnull);
            }
            else
            {
                il.Emit(OpCodes.Ldarg, resultArgument);
                il.Emit(OpCodes.Ldobj, returnType);
                if (returnType.IsValueType) il.Emit(OpCodes.Box, returnType);
            }
            il.Emit(OpCodes.Stloc, resultIn);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg, argsArgument);
            il.Emit(OpCodes.Ldarg, originalMethodArgument);
            il.Emit(OpCodes.Ldloc, resultIn);
            il.Emit(OpCodes.Ldloca, resultOut);
            il.Emit(OpCodes.Ldloca, writeResult);
            il.Emit(OpCodes.Call, interception);
            LocalBuilder runOriginal = null;
            if (returnsRunOriginal)
            {
                runOriginal = il.DeclareLocal(typeof(bool));
                il.Emit(OpCodes.Stloc, runOriginal);
            }

            // __i = (T)__args[i]
            foreach (var index in byRefIndexes)
            {
                var elementType = parameters[index].ParameterType.GetElementType();
                il.Emit(OpCodes.Ldarg, argumentIndex[index]);
                il.Emit(OpCodes.Ldarg, argsArgument);
                il.Emit(OpCodes.Ldc_I4, index);
                il.Emit(OpCodes.Ldelem_Ref);
                il.Emit(elementType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, elementType);
                il.Emit(OpCodes.Stobj, elementType);
            }

            if (!voidResult)
            {
                var skipResult = il.DefineLabel();
                il.Emit(OpCodes.Ldloc, writeResult);
                il.Emit(OpCodes.Brfalse, skipResult);
                il.Emit(OpCodes.Ldarg, resultArgument);
                il.Emit(OpCodes.Ldloc, resultOut);
                il.Emit(returnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, returnType);
                il.Emit(OpCodes.Stobj, returnType);
                il.MarkLabel(skipResult);
            }

            if (returnsRunOriginal) il.Emit(OpCodes.Ldloc, runOriginal);
            il.Emit(OpCodes.Ret);
            return method;
        }
    }
}
#endif
