import { access, mkdir, readFile } from "node:fs/promises";
import { constants, rmSync } from "node:fs";
import path from "node:path";
import { runProcess } from "./instance-validation-process.mjs";

const repo = process.cwd();
const cli = path.join(repo, "dist", "cli.js");
const dataDir = process.env.UNITYCLI_DATA_DIR;
const instanceId = process.env.UNITYCLI_INSTANCE_ID;
if (!dataDir || !path.isAbsolute(dataDir)) throw new Error("UNITYCLI_DATA_DIR must be an absolute validation root.");
if (!/^unity-[0-9a-f]{64}$/.test(instanceId ?? "")) throw new Error("UNITYCLI_INSTANCE_ID must be a full instance ID.");
const validationRoot = path.join(dataDir, ".temp", "instance-validation");
await mkdir(validationRoot, { recursive: true });
const signalPath = path.join(validationRoot, "concurrent-writer.active");
const registryPath = path.join(dataDir, "instances", `${instanceId}.json`);
const env = { ...process.env, UNITYCLI_DATA_DIR: dataDir };

const run = (args) => runProcess(process.execPath, [cli, ...args], { cwd: repo, env, timeoutMs: 200_000 });
process.on("exit", () => rmSync(signalPath, { force: true }));

async function exists(file) {
  try { await access(file, constants.F_OK); return true; } catch { return false; }
}

const code = `
var signal = ${JSON.stringify(signalPath)};
var method = typeof(UnityCli.UnityCliBridge).GetMethod("RegisterInstance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
if (method == null) throw new System.InvalidOperationException("RegisterInstance reflection target is missing.");
System.IO.File.WriteAllText(signal, "active", new System.Text.UTF8Encoding(false));
try
{
    for (var index = 0; index < 200; index++)
    {
        if (!(bool)method.Invoke(null, new object[] { "ready" })) throw new System.InvalidOperationException("Publication failed at " + index.ToString());
        System.Threading.Thread.Sleep(5);
    }
}
finally
{
    if (System.IO.File.Exists(signal)) System.IO.File.Delete(signal);
}
return 200;
`;

const writerPromise = run(["tools", "run", "code.execute", "--instance", instanceId, "--input", JSON.stringify({ code }), "--timeout-ms", "120000"]);
const signalDeadline = Date.now() + 60_000;
while (!(await exists(signalPath))) {
  if (Date.now() >= signalDeadline) throw new Error("Writer signal did not appear before deadline.");
  await new Promise((resolve) => setTimeout(resolve, 10));
}

const readers = await Promise.all(Array.from({ length: 12 }, () => run(["instances", "list", "--all"])));
const writer = await writerPromise;
if (writer.code !== 0) throw new Error(`Writer failed: ${writer.stdout}\n${writer.stderr}`);
for (const [index, reader] of readers.entries()) {
  if (reader.code !== 0) throw new Error(`Reader ${index} exited ${reader.code}: ${reader.stdout}\n${reader.stderr}`);
  const envelope = JSON.parse(reader.stdout);
  if (envelope.ok !== true || envelope.data.invalidRecordCount !== undefined || envelope.data.instances.length !== 1) {
    throw new Error(`Reader ${index} observed invalid state: ${reader.stdout}`);
  }
}
const record = JSON.parse(await readFile(registryPath, "utf8"));
if (record.version !== 3 || record.sessions.length !== 1) throw new Error("Final registry is not one complete v3 record.");
if (await exists(`${registryPath}.tmp`)) throw new Error("Registry temporary file remains.");
if (await exists(signalPath)) throw new Error("Writer signal remains.");
console.log(JSON.stringify({ ok: true, readerCount: readers.length, writerReturn: JSON.parse(writer.stdout).data.runtime.returnValue, revision: record.revision, sessionCount: record.sessions.length, tempExists: false, signalExists: false }));
