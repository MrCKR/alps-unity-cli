#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.SceneManagement;

namespace UnityCli
{
    public static partial class UnityCliBridge
    {
        /// <summary>listener id 每次 domain reload 都变化，确保旧 domain 的只读游标不能碰撞复用。</summary>
        internal static string ReadGenerationIdentity()
        {
            return EditorSessionId() + "\0" + ListenerId + "\0" + CurrentListenerGeneration().ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>为只读 continuation 提供 Unity-owned generation；层级或程序集变化立即使旧游标失效。</summary>
    [InitializeOnLoad]
    internal static class UnityCliReadGeneration
    {
        private static long hierarchyGeneration = 1L;
        private static long reflectionGeneration = 1L;

        static UnityCliReadGeneration()
        {
            EditorApplication.hierarchyChanged += InvalidateHierarchy;
            EditorApplication.projectChanged += InvalidateHierarchy;
            EditorApplication.playModeStateChanged += delegate { InvalidateHierarchy(); };
            CompilationPipeline.compilationFinished += delegate { InvalidateReflection(); };
            AssemblyReloadEvents.beforeAssemblyReload += InvalidateReflection;
            AppDomain.CurrentDomain.AssemblyLoad += delegate { InvalidateReflection(); };
        }

        internal static string Hierarchy(Scene scene)
        {
            return UnityCliContinuationToken.Fingerprint(
                UnityCliBridge.ReadGenerationIdentity() + "\0" + scene.handle.ToString(CultureInfo.InvariantCulture) + "\0" + Interlocked.Read(ref hierarchyGeneration).ToString(CultureInfo.InvariantCulture));
        }

        internal static string Reflection()
        {
            return UnityCliContinuationToken.Fingerprint(
                UnityCliBridge.ReadGenerationIdentity() + "\0" + Interlocked.Read(ref reflectionGeneration).ToString(CultureInfo.InvariantCulture));
        }

        internal static string Session(string scope)
        {
            return UnityCliContinuationToken.Fingerprint(UnityCliBridge.ReadGenerationIdentity() + "\0" + (scope ?? string.Empty));
        }

        internal static void InvalidateHierarchy()
        {
            Interlocked.Increment(ref hierarchyGeneration);
        }

        internal static void InvalidateReflection()
        {
            Interlocked.Increment(ref reflectionGeneration);
        }
    }

    /// <summary>AppDomain没有逐项程序集API；每个reflection generation仅创建一次有1024项硬上限的snapshot。</summary>
    internal static class UnityCliAssemblySnapshot
    {
        private const int MaximumAssemblies = 1024;
        private static readonly object SnapshotLock = new object();
        private static string generation;
        private static Assembly[] assemblies;

        internal static Assembly[] Get(UnityCliRequestBudget budget)
        {
            if (budget == null) throw new ArgumentNullException("budget");
            var currentGeneration = UnityCliReadGeneration.Reflection();
            Assembly[] snapshot;
            lock (SnapshotLock)
            {
                if (assemblies == null || !string.Equals(generation, currentGeneration, StringComparison.Ordinal))
                {
                    if (!budget.TryConsumeReflectionCall()) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
                    snapshot = AppDomain.CurrentDomain.GetAssemblies();
                    if (snapshot.Length > MaximumAssemblies)
                        throw new UnityCliBridge.BridgeCommandException("E_REFLECTION_LIMIT_EXCEEDED", "Loaded assembly count exceeds the 1024-item reflection snapshot bound.", "Reduce loaded assemblies before retrying the bounded reflection request.");
                    assemblies = snapshot;
                    generation = currentGeneration;
                }
                snapshot = assemblies;
            }
            if (!budget.TryConsumeScannedRecords(snapshot.Length)) throw new UnityCliRequestBudgetExceededException(budget.ExhaustedDimension);
            return snapshot;
        }
    }
}
#endif
