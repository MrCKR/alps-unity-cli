import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import test from "node:test";
import { bridgeCommandRegistry } from "../src/bridge/index.js";
import { commandContracts, findCommandContract } from "../src/contracts/command-contract.js";
import { generatedToolCompletionContract } from "../src/tools/.generated-completion-registry.js";
import { existsSync } from "node:fs";

/**
 * ISSUE-081 / ISSUE-084 改名映射全量覆盖：两个 specs/catalog-rename-mapping-*.json 中每一条改名
 * 都必须（1）出现在当前 command contract，（2）有对应的 bridge spec 路由，
 * （3）登记了 completion contract；（4）csharp_handler=present 的工具有对应 C# 源文件。
 */
const mappingFiles = ["specs/catalog-rename-mapping-0.5.0.json", "specs/catalog-rename-mapping-0.7.0.json", "specs/catalog-rename-mapping-0.9.0.json"];
const renames = mappingFiles
  .flatMap((file) => (JSON.parse(readFileSync(file, "utf8")) as { mappings: Array<{ old: string; new: string; final?: string }> }).mappings)
  .filter((m) => m.old !== m.new);
const renameTargets = new Map(renames.map(({ old: oldName, new: newName, final }) => [oldName, final ?? newName] as const));

/** 早期映射的 new 名可能被后续映射再次改名；final 列已沿链冻结到当前名，缺失时回退到链式解析。 */
function finalToolName(name: string): string {
  const visited = new Set<string>();
  let current = name;
  while (renameTargets.has(current) && !visited.has(current)) {
    visited.add(current);
    current = renameTargets.get(current)!;
  }
  return current;
}

test("rename mapping covers renamed entries with canonical contract, bridge spec and completion registration", () => {
  assert.equal(renames.length, 297);
  // 通用 catalog transport 是所有 bridge 工具的兑底路由；无专用 spec 的工具仍可经 --input 调用。
  const genericRun = bridgeCommandRegistry.lookupBridgeCommand("catalog.editor.run");
  assert.ok(genericRun, "generic catalog.editor.run transport must exist");
  for (const { old: oldName, new: rawNewName } of renames) {
    const newName = finalToolName(rawNewName);
    // (1) command contract：改名后的命令必须在当前契约中且标记为已实现
    const contract = findCommandContract(newName);
    assert.ok(contract, `${oldName} must resolve to the current command contract entry ${newName}`);

    // (2) bridge spec：名称首段作为内部 group/command 键，toolName 必须路由到正确 transport
    const dot = newName.indexOf(".");
    const spec = bridgeCommandRegistry.lookup(newName.slice(0, dot), newName.slice(dot + 1));
    const expectedTransport = newName.startsWith("player-runtime.") ? "catalog.player.run" : "catalog.editor.run";
    if (spec) {
      // 本地 typed 工具的 spec 用 toolName 携带公开名；其余工具 bridgeCommand 即公开名。
      assert.ok(spec.toolName === newName || (!spec.toolName && spec.bridgeCommand === newName), `${newName} bridge spec toolName`);
    } else if (contract.handler_type === "bridge") {
      // 无专用 spec 的 bridge 工具必须能经通用 catalog transport 调用。
      assert.ok(genericRun, `${newName} relies on the generic catalog transport`);
    } else {
      // TS-local 工具（offline/hybrid/bridge-runtime/cli_only）不依赖 bridge spec，由 metadata 分派。
      // 此分支内 handler_type 已被类型系统收窄为非 bridge 值，无需额外断言。
    }

    // (3) completion contract（catalog.* 为内部 transport 命令，不进入公开 completion registry）
    if (!newName.startsWith("catalog.")) {
      assert.ok(generatedToolCompletionContract(newName), `${newName} should have a completion contract`);
    }

    // (4) C# 源文件存在性：csharp_handler=present 时源路径必须真实存在
    if (contract.csharp_handler === "present" && !newName.startsWith("catalog.")) {
      const completionEntry = (JSON.parse(readFileSync("contracts/tool-completion-contract.json", "utf8")).tools as Array<{ toolName: string; source: string }>)
        .find((item) => item.toolName === newName);
      assert.ok(completionEntry, `${newName} should have a completion contract entry with source`);
      assert.ok(existsSync(completionEntry.source), `${newName} C# source should exist: ${completionEntry.source}`);
    }
  }
});

test("old catalog identities are absent from the current contract and bridge registry", () => {
  for (const { old: oldName } of renames) {
    const dot = oldName.indexOf(".");
    const stale = bridgeCommandRegistry.lookup(oldName.slice(0, dot), oldName.slice(dot + 1));
    const message = `${oldName} should not resolve under its old name`;
    if (findCommandContract(oldName) === undefined) {
      assert.equal(stale, undefined, message);
    }
  }
});

/** 残留扫描允许命中白名单：迁移记录、本测试自身、问题登记历史。 */
const RESIDUE_ALLOWED = (rel: string) =>
  rel === "specs/catalog-rename-mapping-0.5.0.json" ||
  rel === "specs/catalog-rename-mapping-0.7.0.json" ||
  rel === "specs/catalog-rename-mapping-0.9.0.json" ||
  rel === "tests/rename-mapping-coverage.test.ts" ||
  rel.startsWith(".issues");

/** 残留扫描覆盖的产品面文本根目录与扩展名；二进制与依赖目录排除。 */
const RESIDUE_ROOTS = ["src", "unitypkg", "extensions", "contracts", "docs", "scripts", "tests", "specs", "skills", ".pi", "schemas", "Packages"];
const RESIDUE_EXTENSIONS = new Set([".ts", ".cs", ".json", ".md", ".mjs", ".js", ".cjs", ".txt", ".yml", ".yaml", ".asmdef", ".ps1", ".jsonl"]);
const RESIDUE_SKIP_DIRS = new Set([".git", "node_modules", "dist", "coverage", "Dev", "Library", "Temp", "obj", "bin", ".temp", ".codegraph"]);

/** 递归收集残留扫描目标文件相对路径。 */
function collectResidueFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.isDirectory()) {
      if (RESIDUE_SKIP_DIRS.has(entry.name)) continue;
      collectResidueFiles(`${dir}/${entry.name}`, out);
    } else if (RESIDUE_EXTENSIONS.has(entry.name.slice(entry.name.lastIndexOf(".")).toLowerCase())) {
      out.push(`${dir}/${entry.name}`.replaceAll("\\", "/"));
    }
  }
  return out;
}

const escapeRegex = (value: string) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");

/** 按行聚合命中，产出 file:line [token] 行文本 形式的可操作失败信息。 */
function scanResidue(files: string[], pattern: RegExp): string[] {
  // 新公开名可能以旧名结尾（例如 debug.console-read 以 console.read 结尾），且内部 transport 仍与 command 段同名，
  // 所以扫描前必须先把当前 Tool 身份整体屏蔽，否则会把合法的当前身份误报成旧名残留。
  const identities = currentToolIdentities();
  const failures: string[] = [];
  for (const file of files) {
    const rel = file.replaceAll("\\", "/");
    if (RESIDUE_ALLOWED(rel)) continue;
    let text: string;
    try {
      text = readFileSync(file, "utf8");
    } catch {
      continue;
    }
    for (const line of text.split("\n")) {
      pattern.lastIndex = 0;
      const match = pattern.exec(maskCurrentIdentities(line, identities));
      if (match) failures.push(`${rel} [${match[0]}] ${line.trim().slice(0, 160)}`);
    }
  }
  return failures;
}

/** 当前公开 Tool 身份清单：完整名、group.command 与 command 段都必须参与屏蔽；内部 transport 名也属于当前身份。 */
function currentToolIdentities(): string[] {
  const identities = new Set<string>();
  for (const entry of commandContracts()) {
    identities.add(entry.bridgeCommand);
    identities.add(`${entry.group}.${entry.command}`);
    identities.add(entry.command);
  }
  for (const spec of bridgeCommandRegistry.specs()) {
    identities.add(spec.bridgeCommand);
    if (spec.toolName) identities.add(spec.toolName);
  }
  return [...identities].filter((value) => value.length > 0).sort((left, right) => right.length - left.length);
}

/** 用等长空白替换当前身份，保留行内偏移便于报告原行。 */
function maskCurrentIdentities(line: string, identities: string[]): string {
  let masked = line;
  for (const identity of identities) {
    if (masked.includes(identity)) masked = masked.split(identity).join(" ".repeat(identity.length));
  }
  return masked;
}

test("superseded catalog identities leave no residue in product-facing text", () => {
  // 词边界完整匹配旧点分工具名：两侧只排除标识符字符与 `-`，不能把 `.` 也排除，
  // 否则「旧名 + 句点」（旧名出现在句子末尾或写入嵌套路径）会被整个漏掉。
  // `-` 必须保留在两侧字符类中，用于放行 game-object / animator-controller / player-runtime / runtime-reflection 等新名。
  const dotted = new RegExp(
    `(?<![A-Za-z0-9_-])(${renames.map((m) => escapeRegex(m.old)).join("|")})(?![A-Za-z0-9_-])`, "g",
  );
  const files = [
    ...RESIDUE_ROOTS.map((root) => collectResidueFiles(root)).flat(),
    "README.md",
    "AGENTS.md",
    "package.json",
    "package-lock.json",
    "tsconfig.json",
    "tsconfig.test.json",
  ].filter((file) => statSync(file).isFile());
  const failures = scanResidue(files, dotted);
  assert.deepEqual(failures, [], `old tool-name residue found:\n${failures.join("\n")}`);
});

test("renamed old group names leave no backtick-prefixed glob residue", () => {
  // 反引号 + 旧组名 + 点：捉住 `go.*`、`project.*-settings` 这类 glob 风格旧引用；
  // 当前组如 project-settings 因连字符不匹配，不会误报。
  const contract = JSON.parse(readFileSync("contracts/command-contract.json", "utf8")) as { commands: Array<{ group: string }> };
  const currentGroups = new Set(contract.commands.map((entry) => entry.group));
  const oldGroups = [...new Set(renames.map((m) => m.old.split(".")[0]))].filter((group) => !currentGroups.has(group));
  assert.ok(oldGroups.length > 0, "expected at least one renamed group");
  const globPattern = new RegExp("`(" + oldGroups.map(escapeRegex).join("|") + ")\\.", "g");
  const files = [
    ...collectResidueFiles("docs"),
    ...collectResidueFiles("skills"),
    ...collectResidueFiles(".pi"),
    "README.md",
    "AGENTS.md",
  ].filter((file) => statSync(file).isFile());
  const failures = scanResidue(files, globPattern);
  assert.deepEqual(failures, [], `old group glob residue found:\n${failures.join("\n")}`);
});
