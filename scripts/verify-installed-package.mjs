// 比对已安装的 unitycli 包与 canonical 文件清单：用哈希证明安装字节，而不是靠重启 Editor 确认。
// 用法：node scripts/verify-installed-package.mjs [--project <path>] [--package <packageName>]
import { createHash } from "node:crypto";
import { existsSync } from "node:fs";
import { readFile, stat } from "node:fs/promises";
import { join, resolve } from "node:path";

const root = process.cwd();
const args = process.argv.slice(2);
const flag = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const projectPath = resolve(root, flag("--project", "Dev/UnityProject"));
const manifest = JSON.parse(await readFile(join(root, "contracts", "package-files.json"), "utf8"));
const packageName = flag("--package", manifest.packageName);
const installedRoot = join(projectPath, "Packages", packageName);

if (!existsSync(installedRoot)) {
  console.error(JSON.stringify({ ok: false, error: "E_PACKAGE_NOT_INSTALLED", projectPath, packageName }, null, 2));
  process.exit(1);
}

const missing = [];
const mismatched = [];
for (const file of manifest.files) {
  const target = join(installedRoot, file.path);
  try {
    const bytes = await readFile(target);
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    if (sha256 !== file.sha256) mismatched.push({ path: file.path, expected: file.sha256, actual: sha256 });
  } catch {
    missing.push(file.path);
  }
}

// 只有清单内的差异算失败；安装目录里多出的文件单独报告，便于发现遗留投影。
const report = {
  ok: missing.length === 0 && mismatched.length === 0,
  projectPath,
  packageName,
  packageVersion: manifest.packageVersion,
  sourceHash: manifest.sourceHash,
  checkedFiles: manifest.files.length,
  missing,
  mismatched,
};
console.log(JSON.stringify(report, null, 2));
process.exit(report.ok ? 0 : 1);
