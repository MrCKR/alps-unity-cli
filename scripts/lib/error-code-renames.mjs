/**
 * 事实词表扩容与显式改名表。
 *
 * 扩容：把已在使用的末位词登记为闭集事实，避免为了"命名整齐"去改数百个公开码。
 * 改名：只对同义拼写、无语事实词、状态门族收敛做显式映射，逐条可审。
 */
export const FACT_ADDITIONS = [
  // 同义拼写归一（rename 由 SYNONYM_TARGETS 决定，不在此处登记）
  // 通用事实
  ["ALREADY_CONSUMED", "false", false],
  ["ALREADY_SATISFIED", "false", false],
  ["NO_CONTROLLER", "false", false],
  ["UNOBSERVABLE", "unknown", false],
  ["NOT_PREFAB", "false", false],
  ["SOURCE_EQUALS_DESTINATION", "false", false],
  ["INTERNAL_ERROR", "unknown", false],
  ["PROTOCOL", "false", false],
  ["SKIPPED", "false", false],
  ["TOO_LARGE", "false", false],
  ["EXCEPTION", "unknown", false],
  ["INCOMPLETE", "unknown", false],
  ["OBSERVER_ASYNC", "false", false],
  ["DUPLICATE", "false", false],
  ["REJECTED", "false", false],
  ["NESTED", "false", false],
  ["PAUSED", "false", true],
  ["REQUIRES_INVOCATION", "false", false],
  ["NOT_PREPARED", "false", false],
  ["INTERRUPTED", "unknown", false],
  ["STOPPED", "true", false],
  ["UNBOUND", "false", false],
  ["UNREADABLE", "false", false],
  ["WRONG_STATE", "false", false],
  ["RECURSION", "false", false],
  ["REPLACED", "false", false],
  ["UNVERIFIABLE", "unknown", false],
  ["NOT_INSTALLED", "false", false],
  ["DEADLINE", "unknown", false],
  ["REQUIRES_PAUSE", "false", false],
  ["REQUIRES_EDIT_MODE", "false", false],
  ["REQUIRES_STOP", "false", false],
  ["CYCLE", "false", false],
  ["OVERRUN", "unknown", false],
  ["NOT_REARMED", "false", false],
  ["OWNED_BY_PROGRAM", "false", false],
  ["SUPPRESSED", "false", true],
  ["RISK", "false", false],
  ["UNEXPECTED", "false", false],
  ["FORBIDDEN", "false", false],
  ["IMMUTABLE", "false", false],
  ["OCCUPIED", "false", true],
  ["UNRESPONSIVE", "unknown", true],
  ["DIRTY", "false", false],
  ["UNHEALTHY", "unknown", true],
  ["UNSAFE", "false", false],
  ["OVERLAPS_SOURCE", "false", false],
  ["NULL", "false", false],
  ["NOT_A_BLEND_TREE", "false", false],
  ["NOT_PREFAB_INSTANCE", "false", false],
  ["UNKNOWN_INSTALLED", "false", false],
  ["QUERY_TOO_LONG", "false", false],
  ["SAVE_ALL_UNTITLED", "false", false],
  ["UNLOAD_LAST", "false", false],
  ["VIEW_NOT_AVAILABLE", "false", false],
  ["PENDING_SCAN_LIMIT", "false", false],
  ["FORCE_KINEMATIC", "false", false],
  ["NO_DATA", "false", false],
  ["NO_FIELDS", "false", false],
  ["GO_PARENT_CYCLE", "false", false],
  ["ELEMENT_CAPTURE_BOUNDS", "false", false],
  ["ELEMENT_SELECTOR_BOUNDS", "false", false],
  ["PANEL_BOUNDS", "false", false]
];

/** 同义尾词 → 闭集事实；只改尾部，不改 owner 与主语。 */
export const SYNONYM_TARGETS = {
  EXCEEDED: "LIMIT_EXCEEDED",
  EXHAUSTED: "BUDGET_EXHAUSTED",
  TIMED_OUT: "TIMEOUT"
};

/** 站点级语义 override：事实默认派不出来的真实信封语义，必须带理由。 */
export const SEMANTIC_OVERRIDES = {
  E_INIT_EDITOR_RUNNING: {
    transient: false,
    reason: "Retrying without changing the Editor state can never succeed, so this code is not transient even though the RUNNING fact defaults to transient."
  }
};

/** 已应用的改名记录（旧 → 新）。审计用它拦住旧名的回流；不得用作运行时映射。 */
export const RETIRED_CODES = {
  // 无语事实词
  E_BRIDGE_PROTOCOL: "E_BRIDGE_PROTOCOL_INVALID",
  E_INTERNAL: "E_SHELL_INTERNAL_ERROR",
  E_IO_ERROR: "E_SHELL_IO_ERROR",
  E_TAG_MANAGER_SCHEMA: "E_EDITOR_TAG_MANAGER_SCHEMA_INVALID",
  E_SKILL_INSTALL_IO: "E_SKILL_INSTALL_IO_EXCEPTION",
  E_ANIMATION_NO_CONTROLLER: "E_ANIMATION_NO_CONTROLLER",
  E_ANIMATOR_NO_CONTROLLER: "E_ANIMATOR_NO_CONTROLLER",
  // 状态门族收敛：谓词进 details，码面统一为 UNAVAILABLE
  E_RUNTIME_NOT_READY: "E_RUNTIME_UNAVAILABLE",
  E_PLAYMODE_NOT_READY: "E_PLAYMODE_UNAVAILABLE",
  E_SCENE_NOT_LOADED: "E_SCENE_UNAVAILABLE",
  E_PACKAGE_NOT_AVAILABLE: "E_PACKAGE_UNAVAILABLE",
  E_SKYBOX_NOT_SET: "E_SKYBOX_UNAVAILABLE",
  E_VFX_PARAMETER_NOT_EXPOSED: "E_VFX_PARAMETER_UNAVAILABLE",
  E_PROJECT_SETTINGS_NO_FIELDS: "E_PROJECT_SETTINGS_EMPTY",
  E_UNDO_NOT_PERFORMED: "E_UNDO_NOOP",
  E_REDO_NOT_PERFORMED: "E_REDO_NOOP",
  E_PREFAB_MODIFY_NOOP: "E_PREFAB_MODIFY_NOOP",
  // P2：共享编译预算归一到唯一 owner 命名，阶段用 details.stage 区分
  E_HOOK_COMPILE_BUDGET_EXHAUSTED: "E_COMPILER_BUDGET_EXHAUSTED",
  E_DEBUG_COMPILE_BUDGET_EXCEEDED: "E_COMPILER_BUDGET_EXHAUSTED",
  E_DEBUG_TRIGGER_BUDGET_EXCEEDED: "E_DEBUG_TRIGGER_BUDGET_EXHAUSTED",
  E_HOOK_RESTORE_BUDGET_EXHAUSTED: "E_COMPILER_RESTORE_BUDGET_EXHAUSTED",
  // 无语事实词 / 事实打头
  E_COMPONENT_REMOVE_DEPENDENCY: "E_COMPONENT_REMOVE_CONFLICT",
  E_TOOL_NOT_FOUND_FOR_TARGET: "E_TOOL_TARGET_NOT_FOUND",
  E_NOT_A_BLEND_TREE: "E_BLEND_TREE_UNSUPPORTED",
  E_NOT_PREFAB_INSTANCE: "E_PREFAB_INSTANCE_NOT_FOUND",
  E_UNKNOWN_OPTION: "E_SHELL_OPTION_UNKNOWN",
  E_OBSOLETE_OPTION: "E_SHELL_OPTION_OBSOLETE",
  E_OBSOLETE_LOCAL_STATE: "E_SHELL_LOCAL_STATE_OBSOLETE"
};
