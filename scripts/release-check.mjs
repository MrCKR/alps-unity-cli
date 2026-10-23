// 发布前聚合扫描：把现有门禁、descriptor 预算与版本同步读成一份报告。
// 开发期只输出 WARN，不阻断；发布前 blockers 非空即拒绝发布，warnings 必须告知用户。
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";

const root = process.cwd();
const checks = [];
const blockers = [];
const warnings = [];

const npmCommand = process.platform === "win32" ? "npm.cmd" : "npm";

/** 通过 npm_execpath 调用 npm，避免 shell 拼接；直接运行时回退到 npm 可执行文件。 */
function npmInvocation(args) {
  const execPath = process.env.npm_execpath;
  return execPath ? { command: process.execPath, args: [execPath, ...args], shell: false } : { command: npmCommand, args, shell: true };
}

/** 运行一条门禁命令并按退出码记录结论。 */
function check(name, command, args, options = {}) {
  try {
    const stdout = execFileSync(command, args, { cwd: root, encoding: "utf8", maxBuffer: 1 << 28, shell: options.shell === true, stdio: ["ignore", "pipe", "pipe"] });
    checks.push({ name, status: "pass" });
    return stdout;
  } catch (error) {
    const output = `${error.stdout ?? ""}${error.stderr ?? ""}`.trim();
    checks.push({ name, status: "fail", detail: output.slice(-2000) });
    blockers.push(`${name}: ${output.split("\n").slice(-3).join(" ").slice(0, 400)}`);
    return undefined;
  }
}

/** 读取 JSON 报告；解析失败按该门禁失败处理。 */
function readJsonReport(name, stdout) {
  if (stdout === undefined) return undefined;
  try {
    return JSON.parse(stdout);
  } catch {
    checks.push({ name: `${name}:parse`, status: "fail" });
    blockers.push(`${name}: report is not valid JSON`);
    return undefined;
  }
}

// 1. 版本同步：三处版本号、兼容范围与 trust report 必须指向同一版本。
const version = JSON.parse(readFileSync("package.json", "utf8")).version;
const unityPackageVersion = JSON.parse(readFileSync("unitypkg/package.json", "utf8")).version;
const manifestVersion = JSON.parse(readFileSync("skill-factory/unitycli/manifest.json", "utf8")).version;
const [major, minor] = version.split(".");
const expectedRange = `>=${major}.${minor}.0 <${major}.${Number(minor) + 1}.0`;
const packageManagerSource = readFileSync("src/package-manager.ts", "utf8");
const trustReport = readFileSync("skill-factory/unitycli/reports/trust-report.md", "utf8");
const versionIssues = [];
if (unityPackageVersion !== version) versionIssues.push(`unitypkg/package.json is ${unityPackageVersion}, expected ${version}`);
if (manifestVersion !== version) versionIssues.push(`skill-factory manifest is ${manifestVersion}, expected ${version}`);
if (!packageManagerSource.includes(`cliCompatibilityRange = "${expectedRange}"`)) versionIssues.push(`cliCompatibilityRange must be ${expectedRange}`);
if (!trustReport.includes(`for release ${version}`)) versionIssues.push(`trust report must mention release ${version}`);
if (versionIssues.length > 0) {
  checks.push({ name: "version-sync", status: "fail", detail: versionIssues });
  blockers.push(...versionIssues.map((issue) => `version-sync: ${issue}`));
} else {
  checks.push({ name: "version-sync", status: "pass" });
}

// 2. 架构、契约与完成契约门禁。
const architecture = npmInvocation(["run", "architecture:validate"]);
check("architecture:validate", architecture.command, architecture.args, { shell: architecture.shell });
check("command-contract", process.execPath, ["dist/contracts/validate-command-contract.js"]);

// 3. descriptor 文本预算：开发期只报告，发布前作为阻断项。
const budgetReport = readJsonReport("descriptor-budget", check("descriptor-budget", process.execPath, ["scripts/audit-descriptor-budget.mjs"]));
if (budgetReport) {
  blockers.push(...budgetReport.failures.map((failure) => `descriptor-budget: ${failure}`));
  warnings.push(...budgetReport.warnings.map((warning) => `descriptor-budget: ${warning}`));
}

// 4. Issue 台账。
const issuesReport = readJsonReport("issue-ledger", check("issue-ledger", process.execPath, [".pi/skills/unity-cli-issue-lifecycle/scripts/validate-issues.mjs"]));
if (issuesReport) blockers.push(...(issuesReport.failures ?? []).map((failure) => `issue-ledger: ${failure}`));

// 5. 发布凭据。
const auth = npmInvocation(["whoami"]);
const whoami = check("npm-auth", auth.command, auth.args, { shell: auth.shell });
if (whoami !== undefined) {
  const registry = npmInvocation(["config", "get", "registry"]);
  checks.push({ name: "npm-registry", status: "pass", detail: execFileSync(registry.command, registry.args, { encoding: "utf8", shell: registry.shell }).trim() });
}

const report = { version, pass: blockers.length === 0, blockers, warnings, checks };
console.log(JSON.stringify(report, null, 2));
if (blockers.length > 0) process.exitCode = 1;
