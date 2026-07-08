#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UnityCli.ProBuilder
{
    /// <summary>
    /// ProBuilder 工具 handler 的结构化错误工厂。
    /// 统一返回 <see cref="UnityCliBridge.BridgeCommandException"/>，由调用处 throw 出去，
    /// 经 project-tool.run / runtime-tool.run 的 catch 块转成标准错误 envelope。
    /// 错误码沿用 E_EXTENSION_DEPENDENCY_MISSING / E_PROBUILDER_PACKAGE_NOT_FOUND /
    /// E_TARGET_NOT_FOUND / E_SCHEMA_INVALID / E_PROBUILDER_OPERATION_FAILED，
    /// details 与多个 nextActions 通过 exception 字段透传。
    /// </summary>
    internal static class ProBuilderErrors
    {
        public static UnityCliBridge.BridgeCommandException PackageNotAvailable()
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "packageName", "com.unity.probuilder" },
                { "available", false }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_EXTENSION_DEPENDENCY_MISSING",
                "ProBuilder package (com.unity.probuilder) is not installed or not available.",
                details,
                new[] { "install_com_unity_probuilder_via_package_manager", "run_unity_cli_extensions_doctor_to_verify_setup" });
        }

        public static UnityCliBridge.BridgeCommandException TargetNotFound(string target)
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "target", target ?? "" }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_TARGET_NOT_FOUND",
                "ProBuilder target not found: " + (target ?? "(null)"),
                details,
                new[] { "verify_target_gameobject_exists_in_active_scene", "use_scene_hierarchy_path_or_instance_id" });
        }

        public static UnityCliBridge.BridgeCommandException SchemaInvalid(string message)
        {
            return new UnityCliBridge.BridgeCommandException(
                "E_SCHEMA_INVALID",
                message ?? "Input schema validation failed.",
                UnityCliSerializer.Serialize(new Dictionary<string, object>()),
                new[] { "check_tool_input_schema_with_tools_describe", "ensure_all_required_properties_are_provided" });
        }

        public static UnityCliBridge.BridgeCommandException OperationFailed(string operation, string reason)
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "operation", operation ?? "unknown" },
                { "reason", reason ?? "unknown" }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_PROBUILDER_OPERATION_FAILED",
                "ProBuilder operation '" + (operation ?? "unknown") + "' failed: " + (reason ?? "unknown error"),
                details,
                new[] { "check_unity_console_for_errors", "verify_probuilder_mesh_is_valid", "try_repair_mesh_before_operation" });
        }

        public static UnityCliBridge.BridgeCommandException PackageNotFound(string message)
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "packageName", "com.unity.probuilder" }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_PROBUILDER_PACKAGE_NOT_FOUND",
                message ?? "Unity ProBuilder package is not installed.",
                details,
                new[] { "install_com_unity_probuilder_with_unity_cli_package_install" });
        }
    }
}
#endif
