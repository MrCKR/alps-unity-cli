#if UNITY_EDITOR
using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace AlpsUnityCli
{
    public static class Batch
    {
        public static void ResolvePackages()
        {
            Client.Resolve();
            var request = Client.List(false, true);
            WaitForRequest(request);

            if (request.Status == StatusCode.Failure)
            {
                var error = request.Error != null ? request.Error.message : "Unity PackageManager resolve failed.";
                Console.Error.WriteLine("E_PACKAGE_RESOLVE_FAILED: " + error);
                EditorApplication.Exit(1);
                return;
            }

            AssetDatabase.Refresh();
            Console.WriteLine("Package resolve completed.");
            EditorApplication.Exit(0);
        }

        private static void WaitForRequest(Request request)
        {
            while (!request.IsCompleted)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }
}
#endif
