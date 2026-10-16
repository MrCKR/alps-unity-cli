import { access, mkdir, readFile, rm } from "node:fs/promises";
import { rmSync } from "node:fs";
import path from "node:path";
import { runProcess } from "./instance-validation-process.mjs";

const repo = process.cwd();
const cli = path.join(repo, "dist", "cli.js");
const dataRoot = process.env.UNITYCLI_DATA_DIR;
const instanceId = process.env.UNITYCLI_INSTANCE_ID;
if (!dataRoot || !path.isAbsolute(dataRoot)) throw new Error("UNITYCLI_DATA_DIR must be an absolute validation root.");
if (!/^unity-[0-9a-f]{64}$/.test(instanceId ?? "")) throw new Error("UNITYCLI_INSTANCE_ID must be a full instance ID.");
const root = path.join(dataRoot, ".temp", "instance-validation");
await mkdir(root, { recursive: true });
const registryPath = path.join(dataRoot, "instances", `${instanceId}.json`);
const productReady = path.join(root, "dual-product-ready");
const writerReady = path.join(root, "dual-writer-ready.json");
const writerReleased = path.join(root, "dual-writer-released.json");
const writerScript = path.join(repo, "scripts", "validation", "validate-instance-competing-writer.ps1");
const env = { ...process.env, UNITYCLI_DATA_DIR: dataRoot };

for (const file of [productReady, writerReady, writerReleased]) await rm(file, { force: true });

const run = (executable, args, options = {}) => runProcess(executable, args, { cwd: repo, env: options.env ?? env, timeoutMs: 200_000 });
process.on("exit", () => {
  for (const file of [productReady, writerReady, writerReleased]) rmSync(file, { force: true });
});

async function exists(file) {
  try { await access(file); return true; } catch { return false; }
}

async function waitFor(predicate, timeoutMs, message) {
  const deadline = Date.now() + timeoutMs;
  while (!(await predicate())) {
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise((resolve) => setTimeout(resolve, 20));
  }
}

const initial = JSON.parse(await readFile(registryPath, "utf8"));
if (initial.sessions.length !== 1) throw new Error("Dual-writer fixture requires one initial session.");
const currentPid = initial.sessions[0].processId;
const bridgeLog = path.join(initial.identity.displayProjectPath, "Library", "UnityCli", "UnityCliBridge.log");
const initialSequence = initial.sessions[0].publicationSequence;
const code = `
var productReady = ${JSON.stringify(productReady)};
var writerReady = ${JSON.stringify(writerReady)};
var method = typeof(UnityCli.UnityCliBridge).GetMethod("RegisterInstance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
if (method == null) throw new System.InvalidOperationException("RegisterInstance reflection target is missing.");
System.IO.File.WriteAllText(productReady, "ready", new System.Text.UTF8Encoding(false));
try
{
    var deadline = System.DateTime.UtcNow.AddSeconds(30);
    while (!System.IO.File.Exists(writerReady))
    {
        if (System.DateTime.UtcNow >= deadline) throw new System.TimeoutException("Competing writer did not become ready.");
        System.Threading.Thread.Sleep(10);
    }
    return (bool)method.Invoke(null, new object[] { "ready" });
}
finally
{
    if (System.IO.File.Exists(productReady)) System.IO.File.Delete(productReady);
}
`;
const productPromise = run(process.execPath, [cli, "tools", "run", "debug.code.execute", "--instance", instanceId, "--input", JSON.stringify({ code }), "--timeout-ms", "120000"]);
await waitFor(() => exists(productReady), 60_000, "Product writer did not reach the coordinated call.");

const writerEnv = {
  ...process.env,
  UNITYCLI_DUAL_WRITER_DATA_ROOT: dataRoot,
  UNITYCLI_DUAL_WRITER_INSTANCE_ID: instanceId,
  UNITYCLI_DUAL_WRITER_READY: writerReady,
  UNITYCLI_DUAL_WRITER_RELEASED: writerReleased,
};
const competingPromise = run("pwsh", ["-NoLogo", "-NoProfile", "-File", writerScript], { env: writerEnv });
await waitFor(() => exists(writerReady), 20_000, "Competing writer did not acquire and publish under the shared mutex.");
const afterCompeting = JSON.parse(await readFile(registryPath, "utf8"));
if (afterCompeting.sessions.length !== 2) throw new Error("Competing writer did not publish two claims.");
const competingPid = JSON.parse(await readFile(writerReady, "utf8")).pid;

const product = await productPromise;
if (product.code !== 0) throw new Error(`Product writer call failed: ${product.stdout}\n${product.stderr}`);
if (JSON.parse(product.stdout).data.runtime.returnValue !== false) throw new Error("First product publication should time out on the held mutex.");
await waitFor(() => exists(writerReleased), 20_000, "Competing writer did not release the mutex.");
await waitFor(async () => {
  const record = JSON.parse(await readFile(registryPath, "utf8"));
  const current = record.sessions.find((session) => session.processId === currentPid);
  return record.revision > afterCompeting.revision && record.sessions.length === 2 && current?.publicationSequence > initialSequence;
}, 20_000, "Product retry did not re-read and preserve the competing live claim.");

const list = await run(process.execPath, [cli, "instances", "list", "--all"]);
if (list.code !== 0) throw new Error(`Concurrent-claim list failed: ${list.stdout}\n${list.stderr}`);
const listEnvelope = JSON.parse(list.stdout);
if (listEnvelope.ok !== true || listEnvelope.data.invalidRecordCount !== undefined || listEnvelope.data.instances.length !== 1 || listEnvelope.data.instances[0].status === "ready") {
  throw new Error(`Concurrent claims were not reported conservatively: ${list.stdout}`);
}
const competing = await competingPromise;
if (competing.code !== 0) throw new Error(`Competing writer failed: ${competing.stdout}\n${competing.stderr}`);
await waitFor(async () => (JSON.parse(await readFile(registryPath, "utf8"))).sessions.length === 1, 45_000, "Dead competing claim was not removed.");
const finalRecord = JSON.parse(await readFile(registryPath, "utf8"));
const retryLines = (await readFile(bridgeLog, "utf8")).split(/\r?\n/).filter((line) => line.includes("instance_register_failed kind=lock-timeout") || line.includes("instance_publication_retry_succeeded"));
for (const file of [productReady, writerReady, writerReleased]) await rm(file, { force: true });
if (await exists(`${registryPath}.tmp`)) throw new Error("Registry temporary file remains after dual-writer contention.");
console.log(JSON.stringify({ ok: true, competingPid, sharedRecordClaims: 2, publicStatus: listEnvelope.data.instances[0].status, finalClaims: finalRecord.sessions.length, tempExists: false, retryLines: retryLines.slice(-4) }));
