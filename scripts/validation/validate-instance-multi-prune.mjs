import { createHash } from "node:crypto";
import { mkdir, readdir, rm, utimes, writeFile } from "node:fs/promises";
import path from "node:path";
import { runProcess } from "./instance-validation-process.mjs";

const repo = process.cwd();
const cli = path.join(repo, "dist", "cli.js");
const dataRoot = process.env.UNITYCLI_DATA_DIR;
const instanceId = process.env.UNITYCLI_INSTANCE_ID;
if (!dataRoot || !path.isAbsolute(dataRoot)) throw new Error("UNITYCLI_DATA_DIR must be an absolute validation root.");
if (!/^unity-[0-9a-f]{64}$/.test(instanceId ?? "")) throw new Error("UNITYCLI_INSTANCE_ID must be a full instance ID.");
const instances = path.join(dataRoot, "instances");
const validationRoot = path.join(dataRoot, ".temp", "instance-validation");
await mkdir(validationRoot, { recursive: true });
const env = { ...process.env, UNITYCLI_DATA_DIR: dataRoot };

const run = (args) => runProcess(process.execPath, [cli, ...args], { cwd: repo, env, timeoutMs: 200_000 });

const canonicalNames = () => readdir(instances).then((names) => names.filter((name) => /^unity-[0-9a-f]{64}\.json$/.test(name)).sort());
const baseline = await canonicalNames();
if (baseline.length !== 1 || baseline[0] !== `${instanceId}.json`) throw new Error(`Validation root must contain only the selected current instance: ${JSON.stringify(baseline)}`);
const fixtures = [];
try {
  for (let index = 0; index < 12; index += 1) {
    const canonicalProjectPath = `d:\\unitycli-prune-validation\\expired-${index}`;
    const id = `unity-${createHash("sha256").update(canonicalProjectPath).digest("hex")}`;
    const file = path.join(instances, `${id}.json`);
    const publishedAt = new Date(Date.now() - (40 + index) * 86_400_000);
    await writeFile(file, `${JSON.stringify({ version: 3, revision: 1, canonicalizationVersion: 1, identity: { instanceId: id, canonicalProjectPath, displayProjectPath: `D:\\UnityCli-Prune-Validation\\expired-${index}` }, sessions: [], publishedAt: publishedAt.toISOString() })}\n`, "utf8");
    await utimes(file, publishedAt, publishedAt);
    fixtures.push(file);
  }
  const results = await Promise.all(Array.from({ length: 6 }, () => run(["instances", "prune", "--instance", instanceId, "--timeout-ms", "60000"])));
  const envelopes = results.map((result, index) => {
    if (result.code !== 0) throw new Error(`prune ${index} exit ${result.code}: ${result.stdout}\n${result.stderr}`);
    const value = JSON.parse(result.stdout); if (value.ok !== true) throw new Error(`prune ${index} failed: ${result.stdout}`); return value;
  });
  const removed = envelopes.reduce((sum, value) => sum + value.data.removed, 0);
  const finalNames = await canonicalNames();
  const tmp = (await readdir(instances)).filter((name) => name.endsWith(".tmp"));
  if (removed !== fixtures.length || JSON.stringify(finalNames) !== JSON.stringify(baseline) || tmp.length !== 0) throw new Error(JSON.stringify({ removed, finalNames, baseline, tmp }));
  console.log(JSON.stringify({ ok: true, processes: results.length, removed, perCall: envelopes.map((value) => value.data.removed), deferred: envelopes.reduce((sum, value) => sum + value.data.deferred, 0), finalNames, tmpFiles: tmp.length }));
} finally {
  for (const file of fixtures) await rm(file, { force: true });
}
