#if UNITY_EDITOR
using System;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.SceneManagement;

namespace UnityCli
{
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
        }

        internal static string Hierarchy(Scene scene)
        {
            return UnityCliContinuationToken.Fingerprint(
                UnityCliBridge.EditorSessionId() + "\0" + scene.handle.ToString(CultureInfo.InvariantCulture) + "\0" + Interlocked.Read(ref hierarchyGeneration).ToString(CultureInfo.InvariantCulture));
        }

        internal static string Reflection()
        {
            return UnityCliContinuationToken.Fingerprint(
                UnityCliBridge.EditorSessionId() + "\0" + AppDomain.CurrentDomain.GetAssemblies().Length.ToString(CultureInfo.InvariantCulture) + "\0" + Interlocked.Read(ref reflectionGeneration).ToString(CultureInfo.InvariantCulture));
        }

        internal static string Session(string scope)
        {
            return UnityCliContinuationToken.Fingerprint(UnityCliBridge.EditorSessionId() + "\0" + (scope ?? string.Empty));
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
}
#endif
