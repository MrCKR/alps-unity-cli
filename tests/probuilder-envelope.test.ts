/**
 * Project tool handler 返回/错误契约测试（止血回归）。
 *
 * 锁定修复后的契约形态：
 * - 成功：handler 返回裸 data JSON 字符串，envelope 由 ProjectToolRun 统一包裹。
 * - 报错：handler 抛 BridgeCommandException，由 ProjectToolRun / RuntimeToolRun 的
 *   catch 块转成标准错误 envelope，避免被外层 SuccessJson 误包成 ok:true。
 *
 * 修复前的 bug：
 * 1. 双重 envelope —— ProBuilder handler 自行 ProBuilderErrors.Success(...) 返回
 *    {ok:true, tool, result}，又被外层 SuccessJson 包成 {ok:true, data:{result:{ok:true...}}}。
 * 2. 错误被吞 —— ProBuilder handler 返回 ProBuilderErrors.TargetNotFound(...) =
 *    {ok:false, error}，被外层 SuccessJson 包成 {ok:true, data:{result:{ok:false...}}}。
 *
 * 本测试通过源码字符串断言锁定新行为，防止回退。
 */

import assert from "node:assert/strict";
import test from "node:test";
import { readFileSync, readdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, "..");

function readSrc(rel: string): string {
  return readFileSync(join(ROOT, rel), "utf8");
}

const PROJECT_TOOL_REGISTRY = readSrc("unitypkg/Editor/ProjectTools/ProjectToolRegistry.cs");
const RUNTIME_EXECUTOR = readSrc("unitypkg/Editor/Infrastructure/UnityCliRuntimeExecutor.cs");
const JSON_INFRA = readSrc("unitypkg/Editor/Infrastructure/UnityCliJson.cs");
const EXCEPTION = readSrc("unitypkg/Editor/Bridge/BridgeCommandException.cs");
const PB_ERRORS = readSrc("extensions/probuilder/unitypkg/Editor/ProBuilder/ProBuilderErrors.cs");

// Stage 3 迁移后,每个工具一个文件(IUnityCliTool<,> 泛型接口自包含形态)。
// 动态读取 ProBuilder 目录下所有非 ProBuilder 前缀的 .cs 文件(即 36 个工具文件,
// 排除 ProBuilderErrors/Input/Reflection/SceneContext 等辅助文件)。
const PB_TOOL_DIR = join(ROOT, "extensions", "probuilder", "unitypkg", "Editor", "ProBuilder");
const PB_TOOL_FILES = readdirSync(PB_TOOL_DIR)
  .filter((f) => f.endsWith(".cs") && !f.startsWith("ProBuilder"))
  .map((f) => {
    const rel = `extensions/probuilder/unitypkg/Editor/ProBuilder/${f}`;
    return { rel, src: readSrc(rel) };
  });

// ─── ProjectToolRun 错误收口 ────────────────────────────────────────────

test("project-tool.run catches BridgeCommandException and converts to error envelope", () => {
  // handler 抛出的结构化错误必须被专门的 catch 块捕获并转成错误 envelope，
  // 否则会被外层 SuccessJson 误包成 ok:true（错误被吞 bug）。
  assert.match(
    PROJECT_TOOL_REGISTRY,
    /catch \(BridgeCommandException bce\)[\s\S]*?return BridgeExceptionErrorJson\(bce\)/,
    "ProjectToolRun must catch BridgeCommandException and return BridgeExceptionErrorJson"
  );
});

test("project-tool.run unwraps BridgeCommandException nested in TargetInvocationException", () => {
  // 反射调用路径下，结构化错误可能被包在 TargetInvocationException.InnerException 里，
  // 必须正确解包而非一律报 E_TOOL_EXECUTION_FAILED。
  assert.match(
    PROJECT_TOOL_REGISTRY,
    /catch \(TargetInvocationException exception\)[\s\S]*?var innerBce = inner as BridgeCommandException[\s\S]*?return BridgeExceptionErrorJson\(innerBce\)/,
    "ProjectToolRun must unwrap BridgeCommandException from TargetInvocationException"
  );
});

test("project-tool.run preserves generic fallback for unexpected exceptions", () => {
  // 兜底 catch(Exception) 必须保留，避免未知异常泄露原始堆栈。
  assert.match(
    PROJECT_TOOL_REGISTRY,
    /catch \(Exception exception\)[\s\S]*?E_TOOL_EXECUTION_FAILED/,
    "ProjectToolRun must keep a generic Exception fallback"
  );
});

// ─── runtime-tool.run 错误收口 ──────────────────────────────────────────

test("runtime-tool.run catches BridgeCommandException and converts to error envelope", () => {
  // runtime 路径与 project 路径共享同一 handler 错误契约，必须一致处理。
  assert.match(
    RUNTIME_EXECUTOR,
    /catch \(BridgeCommandException bce\)[\s\S]*?return BridgeExceptionErrorJson\(bce\)/,
    "RuntimeToolRun must catch BridgeCommandException and return BridgeExceptionErrorJson"
  );
  assert.match(
    RUNTIME_EXECUTOR,
    /var innerBce = inner as BridgeCommandException/,
    "RuntimeToolRun must unwrap BridgeCommandException from TargetInvocationException"
  );
});

// ─── 统一错误 envelope 辅助方法 ─────────────────────────────────────────

test("BridgeExceptionErrorJson transparently forwards details and multiple nextActions", () => {
  // 富错误信息（details、多个 nextActions）必须透传到错误 envelope，
  // 而不是只保留单个 nextAction（会丢失 ProBuilder 的结构化 details）。
  assert.match(
    JSON_INFRA,
    /private static string BridgeExceptionErrorJson\(BridgeCommandException exception\)/,
    "UnityCliJson must expose BridgeExceptionErrorJson helper"
  );
  assert.match(
    JSON_INFRA,
    /UnityCliEnvelope\.Error\(null, exception\.Code, exception\.Message, exception\.DetailsJson/,
    "BridgeExceptionErrorJson must forward DetailsJson to the error envelope"
  );
  assert.match(
    JSON_INFRA,
    /hasMultipleActions/,
    "BridgeExceptionErrorJson must consider multiple nextActions"
  );
});

// ─── BridgeCommandException 可见性与富错误字段 ──────────────────────────

test("BridgeCommandException is internal so project-local handlers can throw it", () => {
  // ProBuilder 等位于独立 namespace（UnityCli.ProBuilder）的 handler 必须能访问该类型，
  // 因此从 private 提升为 internal。
  assert.match(
    EXCEPTION,
    /internal sealed class BridgeCommandException/,
    "BridgeCommandException must be internal (not private) for cross-namespace handlers"
  );
  // 富错误字段必须存在，供 handler 表达 details 与多个 nextActions。
  assert.match(EXCEPTION, /public string DetailsJson/, "must expose DetailsJson field");
  assert.match(EXCEPTION, /public IReadOnlyList<string> NextActions/, "must expose NextActions field");
  // 新构造重载：支持 details + 多个 nextActions。
  assert.match(
    EXCEPTION,
    /IEnumerable<string> nextActions, bool transient = false, int retryAfterMs = 0\)/,
    "must provide a constructor overload accepting details and nextActions"
  );
});

// ─── ProBuilder 错误工厂：返回异常对象，不再自拼 envelope ───────────────

test("ProBuilderErrors returns BridgeCommandException objects instead of error JSON strings", () => {
  // 修复前：错误方法返回 {ok:false, error} 字符串，被外层当成成功 data 包裹 → 错误被吞。
  // 修复后：返回 BridgeCommandException，调用处 throw，由 project-tool.run 的 catch 统一收口。
  const methods = ["PackageNotAvailable", "TargetNotFound", "SchemaInvalid", "OperationFailed", "PackageNotFound"];
  for (const m of methods) {
    assert.match(
      PB_ERRORS,
      new RegExp(`public static UnityCliBridge\\.BridgeCommandException ${m}\\(`),
      `ProBuilderErrors.${m} must return UnityCliBridge.BridgeCommandException`
    );
  }
});

test("ProBuilderErrors no longer wraps success results in a double envelope", () => {
  // Success 方法是双重 envelope 的根源（{ok:true, tool, result}），必须删除。
  // handler 现在直接 return 裸 result，由 ProjectToolRun 统一包裹。
  assert.doesNotMatch(
    PB_ERRORS,
    /public static string Success\(/,
    "ProBuilderErrors.Success must be removed to avoid double envelope"
  );
  // 错误工厂也不再自行拼接 ok:false / ok:true envelope。
  assert.doesNotMatch(PB_ERRORS, /\.Prop\("ok"/, "ProBuilderErrors must not assemble ok envelope itself");
});

test("ProBuilderErrors forwards structured details and nextActions via exception fields", () => {
  // details（如 target / operation / reason / packageName）与多个 nextActions
  // 通过新构造重载透传，保证 AI 拿到可机器解析的错误细节。
  assert.match(PB_ERRORS, /E_TARGET_NOT_FOUND[\s\S]*?details[\s\S]*?new\[\]/, "TargetNotFound must attach details + nextActions");
  assert.match(PB_ERRORS, /E_PROBUILDER_OPERATION_FAILED[\s\S]*?details[\s\S]*?new\[\]/, "OperationFailed must attach details + nextActions");
});

// ─── ProBuilder 工具：throw 错误、return 裸 data ────────────────────────

test("ProBuilder tools throw structured errors instead of returning them", () => {
  // return ProBuilderErrors.X(...) 会让错误被外层 SuccessJson 包成 ok:true。
  // 必须改为 throw，让 catch(BridgeCommandException) 正确转成 ok:false envelope。
  const errorMethods = ["PackageNotAvailable", "TargetNotFound", "SchemaInvalid", "OperationFailed", "PackageNotFound"];
  for (const { rel, src } of PB_TOOL_FILES) {
    for (const m of errorMethods) {
      assert.ok(
        !src.includes(`return ProBuilderErrors.${m}(`),
        `${rel}: must not 'return ProBuilderErrors.${m}(' (should throw)`
      );
    }
  }
});

test("ProBuilder tools throw at least one structured error per file (except ping)", () => {
  // 每个工具文件都应通过 throw 触发结构化错误路径（反向证明改动落地）。
  // ping 是探测工具,不抛 PackageNotAvailable(它就是为了检查可用性)。
  for (const { rel, src } of PB_TOOL_FILES) {
    if (rel.endsWith("ping.cs")) continue;
    assert.match(
      src,
      /throw ProBuilderErrors\./,
      `${rel}: must throw a ProBuilderErrors exception on error paths`
    );
  }
});

test("ProBuilder tools return bare result data without a double envelope", () => {
  // 成功路径直接 return 裸 result JSON，不再调用 ProBuilderErrors.Success。
  for (const { rel, src } of PB_TOOL_FILES) {
    assert.ok(
      !src.includes("ProBuilderErrors.Success"),
      `${rel}: must not call ProBuilderErrors.Success (single envelope only)`
    );
  }
});
