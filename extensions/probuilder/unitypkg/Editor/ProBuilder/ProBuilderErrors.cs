#if UNITY_EDITOR
using System.Collections.Generic;
namespace UnityCli.ProBuilder
{
    /// <summary>
    /// ProBuilder 工具 handler 的结构化错误工厂。
    /// 统一返回 <see cref="UnityCliBridge.BridgeCommandException"/>，由调用处 throw 出去，
    /// 经 catalog.editor.run / catalog.player.run 的 catch 块转成标准错误 envelope。
    /// 错误码统一使用当前 extension dependency、target、schema、operation 与 outcome unknown 契约，
    /// details 与多个 nextActions 通过 exception 字段透传。
    /// </summary>
    internal static class ProBuilderErrors
    {
        public static UnityCliBridge.BridgeCommandException PackageNotAvailable()
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "packageName", "com.unity.probuilder" },
                { "expectedVersion", ProBuilderReflection.SupportedPackageVersion },
                { "actualVersion", ProBuilderReflection.InstalledPackageVersion ?? "" },
                { "available", false }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_EXTENSION_DEPENDENCY_MISSING",
                "ProBuilder package com.unity.probuilder must be installed at the exact supported version " + ProBuilderReflection.SupportedPackageVersion + ".",
                details,
                new[] { "install_com_unity_probuilder_5_2_4_via_package_manager", "run_unitycli_extensions_doctor_to_verify_setup" });
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

        /// <summary>effect 已提交后，调用、读回或后置条件不能证明时禁止自动重放。</summary>
        public static UnityCliBridge.BridgeCommandException OutcomeUnknown(string operation, string stage, string reason)
        {
            var details = UnityCliSerializer.Serialize(new Dictionary<string, object>
            {
                { "operation", operation ?? "unknown" },
                { "stage", stage ?? "postcondition" },
                { "reason", reason ?? "unknown" }
            });
            return new UnityCliBridge.BridgeCommandException(
                "E_REQUEST_OUTCOME_UNKNOWN",
                "ProBuilder operation '" + (operation ?? "unknown") + "' may have changed the mesh but its final result was not proven.",
                details,
                new[] { "inspect_the_target_mesh_before_submitting_a_new_request" });
        }
    }
}
#endif
