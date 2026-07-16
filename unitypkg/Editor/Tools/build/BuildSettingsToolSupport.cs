#if UNITY_EDITOR
using UnityEditor;
namespace UnityCli { public static class BuildSettingsToolSupport { public static BuildSettingsResult Read() => new BuildSettingsResult { ProductName = PlayerSettings.productName, BundleVersion = PlayerSettings.bundleVersion, CompanyId = PlayerSettings.companyName, ApplicationIdentifier = PlayerSettings.applicationIdentifier, ScriptingBackend = PlayerSettings.GetScriptingBackend(BuildTargetGroup.Unknown).ToString(), ApiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Unknown).ToString() }; } }
#endif
