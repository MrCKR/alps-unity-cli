import assert from "node:assert/strict";
import test from "node:test";
import { generatedToolCompletionContract } from "../src/tools/.generated-completion-registry.js";
import { validateLocalCompletionProof } from "../src/tools/dispatcher.js";
import { StructuredToolError } from "../src/tools/errors.js";

/** 使用生成契约验证跨重载 Tool 的公开终态。 */
function validate(name: string, result: unknown, input: Record<string, unknown>): void {
  const completion = generatedToolCompletionContract(name);
  assert.ok(completion, name);
  validateLocalCompletionProof(name, completion, result, input);
}

/** 断言伪造或不匹配的终态被统一 completion gate 拒绝。 */
async function invalid(name: string, result: unknown, input: Record<string, unknown>): Promise<void> {
  await assert.rejects(
    async () => validate(name, result, input),
    (error: unknown) => error instanceof StructuredToolError && error.code === "E_TOOL_COMPLETION_PROOF_INVALID",
  );
}

test("asset.refresh validates requested path and terminal import effect", async () => {
  const result = {
    path: "Assets/Editor/Probe.cs",
    options: "ForceUpdate",
    refreshed: true,
    plannedEffects: [{ type: "asset.import", path: "Assets/Editor/Probe.cs" }],
  };
  validate("asset.refresh", result, { path: "Assets/Editor/Probe.cs", forceUpdate: true });
  await invalid("asset.refresh", result, { path: "Assets/Editor/Other.cs" });
  await invalid("asset.refresh", { ...result, refreshed: false }, { path: "Assets/Editor/Probe.cs" });
});

test("package.embed validates package identity, local path, and terminal effect", async () => {
  const result = {
    packageId: "com.unity.probuilder",
    embeddedPath: "D:\\Project\\Packages\\com.unity.probuilder",
    plannedEffects: [{ type: "package.embed", path: "com.unity.probuilder" }],
  };
  validate("package.embed", result, { package: "com.unity.probuilder" });
  await invalid("package.embed", result, { package: "com.unity.cinemachine" });
  await invalid("package.embed", { ...result, embeddedPath: "D:\\Project\\Library\\PackageCache\\com.unity.probuilder" }, { package: "com.unity.probuilder" });
});
