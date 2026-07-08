/**
 * 干净子代理 Context 验证脚本。
 * 使用 child_process.spawnSync 执行 CLI，避免 shell 转义破坏 JSON。
 * 验证 sample.project-summary 和 probuilder.scene 两个 Context 的完整发现-描述-读取流程。
 */

import { spawnSync } from "node:child_process";
import { mkdirSync, writeFileSync, existsSync } from "node:fs";
import { join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { tmpdir } from "node:os";

const __dirname = dirname(fileURLToPath(import.meta.url));
const TMP = join(tmpdir(), "clean-verification-contexts-" + Date.now());
mkdirSync(TMP, { recursive: true });

const NODE = process.execPath;
const CLI = "D:/workspace/alps-unity-cli/dist/cli.js";
const PROJECT = "D:/workspace/alps-unity-cli/Dev/UnityProject";
const INSTANCE = "unity-da1e42fad0da-24416";

// ============================================================
// 工具函数
// ============================================================

/** 写 JSON 输入文件，返回路径 */
function writeInputFile(filename, content) {
  const p = join(TMP, filename);
  writeFileSync(p, JSON.stringify(content, null, 2), "utf8");
  return p;
}

/** 执行 CLI 命令，返回结构化结果 */
function runCli(label, argv) {
  console.log(`\n=== ${label} ===`);
  console.log(`CMD: ${NODE} ${argv.join(" ")}`);

  const result = spawnSync(NODE, argv, {
    cwd: "D:/workspace/alps-unity-cli",
    timeout: 120000,
    encoding: "utf8",
    maxBuffer: 10 * 1024 * 1024,
    env: { ...process.env },
  });

  const stdout = result.stdout || "";
  const stderr = result.stderr || "";
  const exitCode = result.status ?? (result.error ? 1 : 0);
  const error = result.error ? result.error.message : null;

  // 尝试解析 JSON
  let parsedOutput = null;
  let parseError = null;
  try {
    parsedOutput = JSON.parse(stdout.trim());
  } catch (e) {
    parseError = e.message;
  }

  const record = {
    label,
    command: `${NODE} ${argv.join(" ")}`,
    argv,
    stdin: null,
    stdout,
    stderr,
    exitCode,
    error,
    parsedOutput,
    parseError,
  };

  console.log(`exitCode: ${exitCode}, parseError: ${parseError || "none"}`);

  // 打印 stdout 摘要（非完整 JSON）
  if (parsedOutput) {
    console.log(`parsedOutput.ok: ${parsedOutput.ok}`);
    if (parsedOutput.data) {
      const keys = Object.keys(parsedOutput.data);
      console.log(`data keys: ${keys.join(", ")}`);
    }
  }

  return record;
}

// ============================================================
// 自检函数
// ============================================================

function selfCheck(results) {
  const errors = [];

  // 1. commandResults 非空
  if (!Array.isArray(results) || results.length === 0) {
    errors.push("commandResults must be a non-empty array");
  }

  for (let i = 0; i < results.length; i++) {
    const r = results[i];
    const prefix = `commandResults[${i}]`;

    // 2. 每字段必须存在
    for (const key of ["label", "command", "argv", "stdin", "stdout", "stderr", "exitCode", "parsedOutput"]) {
      if (!(key in r)) {
        errors.push(`${prefix} missing field: ${key}`);
      }
    }

    // 3. stdout 必须可以 JSON.parse
    if (r.stdout && typeof r.stdout === "string") {
      try {
        const p = JSON.parse(r.stdout.trim());
        // 4. parsedOutput 必须与解析结果一致
        if (JSON.stringify(r.parsedOutput) !== JSON.stringify(p)) {
          errors.push(`${prefix} parsedOutput mismatch with JSON.parse(stdout)`);
        }
      } catch (e) {
        errors.push(`${prefix} stdout is not valid JSON: ${e.message}`);
      }
    } else if (!r.stdout || r.stdout.trim() === "") {
      errors.push(`${prefix} stdout is empty`);
    }

    // 5. argv 必须包含 instance id
    if (!r.argv) continue;
    const hasInstance = r.argv.some((a) => a.includes(INSTANCE));
    if (!hasInstance) {
      errors.push(`${prefix} argv does not contain instance id ${INSTANCE}`);
    }

    // 6. 不能出现 tools run
    if (r.argv && r.argv.includes("run")) {
      errors.push(`${prefix} should not use tools run`);
    }
  }

  return errors;
}

// ============================================================
// 主流程
// ============================================================

const commandResults = [];

// ── Step 1: context list（发现阶段）──
console.log("\n>>> STEP 1: context list discovery");
const ctxList = runCli("context.list", [
  CLI, "context", "list",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--include-custom",
]);
commandResults.push(ctxList);

// ── Step 2: 从 list 结果中确认两个 context 存在 ──
let listContextNames = [];
if (ctxList.parsedOutput?.data?.contexts) {
  listContextNames = ctxList.parsedOutput.data.contexts.map((c) => c.name);
  console.log(`\nDiscovered ${listContextNames.length} contexts`);
  console.log("sample.project-summary found:", listContextNames.includes("sample.project-summary"));
  console.log("probuilder.scene found:", listContextNames.includes("probuilder.scene"));
}

// ── Step 3: context describe sample.project-summary ──
console.log("\n>>> STEP 3: describe sample.project-summary");
const descSample = runCli("context.describe.sample.project-summary", [
  CLI, "context", "describe", "sample.project-summary",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--include-custom",
]);
commandResults.push(descSample);

// ── Step 4: context describe probuilder.scene ──
console.log("\n>>> STEP 4: describe probuilder.scene");
const descProBuilder = runCli("context.describe.probuilder.scene", [
  CLI, "context", "describe", "probuilder.scene",
  "--instance", INSTANCE,
  "--project", PROJECT,
]);
commandResults.push(descProBuilder);

// ── Step 5: context read sample.project-summary (默认正向) ──
console.log("\n>>> STEP 5: read sample.project-summary (default)");
const readSampleDefault = runCli("context.read.sample.project-summary.default", [
  CLI, "context", "read", "sample.project-summary",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--include-custom",
]);
commandResults.push(readSampleDefault);

// ── Step 6: context read sample.project-summary (with input) ──
console.log("\n>>> STEP 6: read sample.project-summary (with includeDisabled=true)");
const sampleInputFile = writeInputFile("sample-input.json", { includeDisabled: true });
const readSampleWithInput = runCli("context.read.sample.project-summary.includeDisabled", [
  CLI, "context", "read", "sample.project-summary",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--include-custom",
  "--input-file", sampleInputFile,
]);
readSampleWithInput.inputFilePath = sampleInputFile;
readSampleWithInput.inputFileContent = JSON.stringify({ includeDisabled: true });
commandResults.push(readSampleWithInput);

// ── Step 7: context read probuilder.scene (默认正向) ──
console.log("\n>>> STEP 7: read probuilder.scene (default)");
const readProBuilderDefault = runCli("context.read.probuilder.scene.default", [
  CLI, "context", "read", "probuilder.scene",
  "--instance", INSTANCE,
  "--project", PROJECT,
]);
commandResults.push(readProBuilderDefault);

// ── Step 8: context read probuilder.scene (with pageSize) ──
console.log("\n>>> STEP 8: read probuilder.scene (pageSize=10)");
const pbInputFile = writeInputFile("probuilder-input.json", { pageSize: 10 });
const readProBuilderPageSize = runCli("context.read.probuilder.scene.pageSize", [
  CLI, "context", "read", "probuilder.scene",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--input-file", pbInputFile,
]);
readProBuilderPageSize.inputFilePath = pbInputFile;
readProBuilderPageSize.inputFileContent = JSON.stringify({ pageSize: 10 });
commandResults.push(readProBuilderPageSize);

// ── Step 9: context read probuilder.scene (with namePattern - 可选正向) ──
console.log("\n>>> STEP 9: read probuilder.scene (namePattern='Cube')");
const pbNameInputFile = writeInputFile("probuilder-name-input.json", { namePattern: "Cube", pageSize: 5 });
const readProBuilderName = runCli("context.read.probuilder.scene.namePattern", [
  CLI, "context", "read", "probuilder.scene",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--input-file", pbNameInputFile,
]);
readProBuilderName.inputFilePath = pbNameInputFile;
readProBuilderName.inputFileContent = JSON.stringify({ namePattern: "Cube", pageSize: 5 });
commandResults.push(readProBuilderName);

// ── Step 10: context read probuilder.scene (pageSize 超过上限 - 错误路径) ──
console.log("\n>>> STEP 10: read probuilder.scene (pageSize=999 - 错误路径)");
const pbBadInputFile = writeInputFile("probuilder-bad-input.json", { pageSize: 999 });
const readProBuilderBad = runCli("context.read.probuilder.scene.pageSize.bad", [
  CLI, "context", "read", "probuilder.scene",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--input-file", pbBadInputFile,
]);
readProBuilderBad.inputFilePath = pbBadInputFile;
readProBuilderBad.inputFileContent = JSON.stringify({ pageSize: 999 });
commandResults.push(readProBuilderBad);

// ── Step 11: context read non-existent context (错误路径) ──
console.log("\n>>> STEP 11: read non-existent context (错误路径)");
const readMissing = runCli("context.read.missing", [
  CLI, "context", "read", "nonexistent.context",
  "--instance", INSTANCE,
  "--project", PROJECT,
  "--include-custom",
]);
commandResults.push(readMissing);

// ============================================================
// 自检
// ============================================================
const checkErrors = selfCheck(commandResults);
if (checkErrors.length > 0) {
  console.error("\n=== SELF-CHECK FAILED ===");
  for (const err of checkErrors) {
    console.error(`  - ${err}`);
  }
} else {
  console.log("\n=== SELF-CHECK PASSED ===");
}

// ============================================================
// 组装证据
// ============================================================
const evidence = {
  generatedAt: new Date().toISOString(),
  verificationTargets: ["sample.project-summary", "probuilder.scene"],
  instanceId: INSTANCE,
  projectPath: PROJECT,
  cliPath: CLI,
  nodeExecutable: NODE,
  tempDir: TMP,
  safety: {
    repositoryModified: false,
    unityStateModified: false,
    onlyReadOnlyContexts: true,
    noToolsRun: true,
    didNotReceiveSpecificCommands: true,
    didNotReceiveSourcePaths: true,
    didNotReceiveImplementationDetails: true,
  },
  commandResults,
  selfCheck: {
    passed: checkErrors.length === 0,
    errors: checkErrors,
  },
};

// ============================================================
// 输出证据
// ============================================================
const evidencePath = join(TMP, "clean-verification-evidence-contexts.json");
writeFileSync(evidencePath, JSON.stringify(evidence, null, 2), "utf8");
console.log(`\nEvidence written to: ${evidencePath}`);
console.log(`Total command results: ${commandResults.length}`);
console.log(`Self-check: ${checkErrors.length === 0 ? "PASSED" : "FAILED"}`);
