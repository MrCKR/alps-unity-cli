#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Harmony = HarmonyLib.Harmony;

namespace UnityCli.Hooks
{
    /// <summary>
    /// Harmony 后端：按 exact target 建立只属于本 CLI 的 Prefix/Postfix/内部 Finalizer Adapter。
    ///
    /// 约束：
    /// - owner 固定为 unitycli.method-hook；撤销只针对自己安装的 patch，不调用无 owner 的 UnpatchAll。
    /// - 每个 exact target 只安装一套 Adapter，重复请求返回同一句柄，不产生双重应用。
    /// - Finalizer 只记录异常结束并原样返回原异常，不吞异常、不替换异常类型。
    /// - 不注册 Tool、不持久化定义、不启动 transport；后端缺失时基础能力不受影响。
    /// </summary>
    public sealed class MethodHookHarmonyBackend : UnityCli.IUnityCliMethodHookBackend
    {
        internal const string PatchOwnerId = "unitycli.method-hook";

        private readonly object gate = new object();
        private readonly Harmony harmony = new Harmony(PatchOwnerId);
        private readonly Dictionary<MethodBase, Attachment> attachments = new Dictionary<MethodBase, Attachment>();

        private sealed class Attachment
        {
            internal MethodBase Target;
            internal MethodInfo Replacement;
            internal MethodInfo Prefix;
            internal MethodInfo Postfix;
        }

        public bool Available { get { return true; } }

        public string UnavailableReason { get { return string.Empty; } }

        public string Version
        {
            get
            {
                var name = typeof(Harmony).Assembly.GetName();
                return name.Version == null ? name.Name : name.Name + " " + name.Version.ToString();
            }
        }

        /// <summary>已加载依赖的 exact identity，供 core 与 Package 来源核对。</summary>
        public string DependencyIdentity { get { return typeof(Harmony).Assembly.GetName().FullName; } }

        /// <summary>已加载依赖的物理位置，供 core 核对它确实来自本 Package。</summary>
        public string DependencyLocation
        {
            get
            {
                try { return typeof(Harmony).Assembly.Location ?? string.Empty; }
                catch { return string.Empty; }
            }
        }

        public object Attach(MethodInfo target)
        {
            if (target == null) throw new ArgumentNullException("target");
            lock (gate)
            {
                Attachment existing;
                if (attachments.TryGetValue(target, out existing)) return existing;

                var returnType = target.ReturnType;
                MethodInfo prefix;
                MethodInfo postfix;
                if (returnType == typeof(void))
                {
                    // void 结果使用内置 Adapter：无法声明 __result，其余参数与 ref 回写仍按同一形状处理。
                    if (target.GetParameters().Any(parameter => parameter.ParameterType.IsByRef))
                    {
                        var voidAdapter = MethodHookAdapterEmitter.ForVoidWithByRef(target);
                        prefix = voidAdapter.Prefix;
                        postfix = voidAdapter.Postfix;
                    }
                    else
                    {
                        prefix = VoidPrefixMethod;
                        postfix = VoidPostfixMethod;
                    }
                }
                else
                {
                    var adapter = MethodHookAdapterEmitter.For(target);
                    prefix = adapter.Prefix;
                    postfix = adapter.Postfix;
                }

                var processor = harmony.CreateProcessor(target);
                processor.AddPrefix(new HarmonyMethod(prefix));
                processor.AddPostfix(new HarmonyMethod(postfix));
                processor.AddFinalizer(new HarmonyMethod(FinalizerMethod));
                var replacement = processor.Patch();
                if (replacement == null)
                    throw new UnityCli.UnityCliBridge.BridgeCommandException("E_HOOK_PATCH_FAILED", "Harmony did not return a replacement method for the exact target.", "Inspect the target signature and Unity Console before retrying.");

                var attachment = new Attachment { Target = target, Replacement = replacement, Prefix = prefix, Postfix = postfix };
                attachments.Add(target, attachment);
                return attachment;
            }
        }

        public void Detach(MethodInfo target)
        {
            if (target == null) return;
            lock (gate)
            {
                Attachment existing;
                if (!attachments.TryGetValue(target, out existing)) return;
                attachments.Remove(target);
                try
                {
                    // 只撤销本 owner 在该 exact target 上的 patch，不触碰其他 owner，也不影响其他 target。
                    harmony.Unpatch(target, HarmonyPatchType.All, PatchOwnerId);
                }
                catch (Exception exception)
                {
                    throw new UnityCli.UnityCliBridge.BridgeCommandException(
                        "E_HOOK_PATCH_FAILED",
                        "Owned patch could not be removed from the exact target: " + exception.GetType().Name + ".",
                        null,
                        new[] { "Inspect Unity Console before submitting another mutation." });
                }
                // 撤销必须可读回；否则下一次 attach 会在旧链上叠加同一 Adapter。
                if (OwnPatchCount(target) != 0)
                    throw new UnityCli.UnityCliBridge.BridgeCommandException(
                        "E_HOOK_PATCH_FAILED",
                        "Owned patch is still present on the exact target after removal.",
                        null,
                        new[] { "Inspect whether another mechanism rewrote the target before retrying." });
            }
        }

        public int OwnPatchCount(MethodInfo target)
        {
            var info = PatchInfo(target);
            if (info == null) return 0;
            return CountOwned(info.Prefixes) + CountOwned(info.Postfixes) + CountOwned(info.Finalizers) + CountOwned(info.Transpilers);
        }

        /// <summary>本 CLI 在该 exact target 上恰好只建立一套 Prefix/Postfix/Finalizer；重复 attach 必须 fail closed。</summary>
        public bool OwnedExactlyOnce(MethodInfo target)
        {
            var info = PatchInfo(target);
            if (info == null) return false;
            return CountOwned(info.Prefixes) == 1
                && CountOwned(info.Postfixes) == 1
                && CountOwned(info.Finalizers) == 1
                && CountOwned(info.Transpilers) == 0;
        }

        public string OwnerSummary(MethodInfo target)
        {
            var info = PatchInfo(target);
            if (info == null) return string.Empty;
            var owners = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
            AccumulateForeignOwners(info.Prefixes, 0, owners);
            AccumulateForeignOwners(info.Postfixes, 1, owners);
            AccumulateForeignOwners(info.Finalizers, 2, owners);
            if (owners.Count == 0) return string.Empty;
            var builder = new StringBuilder();
            var index = 0;
            foreach (var pair in owners)
            {
                if (index > 0) builder.Append("; ");
                if (index >= DebugLimits.MaxForeignOwnerEntries) { builder.Append("..."); break; }
                builder.Append(pair.Key).Append(" (prefix ").Append(pair.Value[0].ToString(CultureInfo.InvariantCulture))
                    .Append(", postfix ").Append(pair.Value[1].ToString(CultureInfo.InvariantCulture))
                    .Append(", finalizer ").Append(pair.Value[2].ToString(CultureInfo.InvariantCulture)).Append(')');
                index++;
            }
            return builder.ToString();
        }

        private static void AccumulateForeignOwners(IEnumerable<Patch> patches, int slot, SortedDictionary<string, int[]> owners)
        {
            if (patches == null) return;
            foreach (var patch in patches)
            {
                if (patch == null || string.Equals(patch.owner, PatchOwnerId, StringComparison.Ordinal)) continue;
                int[] counts;
                if (!owners.TryGetValue(patch.owner ?? "<unknown>", out counts)) { counts = new int[3]; owners.Add(patch.owner ?? "<unknown>", counts); }
                counts[slot]++;
            }
        }

        private static int CountOwned(IEnumerable<Patch> patches)
        {
            return patches == null ? 0 : patches.Count(patch => patch != null && string.Equals(patch.owner, PatchOwnerId, StringComparison.Ordinal));
        }

        private static Patches PatchInfo(MethodBase target)
        {
            try { return Harmony.GetPatchInfo(target); }
            catch { return null; }
        }

        /// <summary>void 结果的 Prefix：没有 __result 可写，仍可改参、跳过原方法与记录事件。</summary>
        public static bool VoidPrefix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            object resultOut;
            bool writeResult;
            return UnityCli.MethodHookInterception.Prefix(__instance, __args, __originalMethod, null, out resultOut, out writeResult);
        }

        /// <summary>void 结果的 Postfix：只观察与记录，不替换结果。</summary>
        public static void VoidPostfix(object __instance, object[] __args, MethodBase __originalMethod)
        {
            object resultOut;
            bool writeResult;
            UnityCli.MethodHookInterception.Postfix(__instance, __args, __originalMethod, null, out resultOut, out writeResult);
        }

        /// <summary>内部 Finalizer 原样返回原异常，只额外记录异常结束事件。</summary>
        public static Exception Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            return UnityCli.MethodHookInterception.Finalizer(__exception, __originalMethod);
        }

        internal static MethodInfo VoidPrefixMethod { get { return typeof(MethodHookHarmonyBackend).GetMethod("VoidPrefix", BindingFlags.Public | BindingFlags.Static); } }

        internal static MethodInfo VoidPostfixMethod { get { return typeof(MethodHookHarmonyBackend).GetMethod("VoidPostfix", BindingFlags.Public | BindingFlags.Static); } }

        internal static MethodInfo FinalizerMethod { get { return typeof(MethodHookHarmonyBackend).GetMethod("Finalizer", BindingFlags.Public | BindingFlags.Static); } }
    }
}
#endif
