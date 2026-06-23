import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdirSync, writeFileSync, rmSync, existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const project = resolve(process.argv[2] || 'Dev/UnityProject');
const unity = process.argv[3] || 'C:/Program Files/Unity/Unity-2022.3.61f1/Editor/Unity.exe';
const outDir = resolve('.tmp/evidence/offline-tool-smoke');
const dataDir = join(outDir, 'data');
const docsDir = join(outDir, 'docs-cache');
const reflectDir = join(outDir, 'reflect-cache');
const batchPath = join(outDir, 'smoke-batch.json');
const cliPath = resolve('dist/cli.js');
mkdirSync(outDir, { recursive: true });
mkdirSync(dataDir, { recursive: true });
mkdirSync(docsDir, { recursive: true });
mkdirSync(reflectDir, { recursive: true });

writeFileSync(join(docsDir, 'docs-index.json'), JSON.stringify({
  unityVersion: '2022.3',
  entries: [{
    name: 'UnityEngine.Vector3',
    title: 'Vector3',
    summary: 'Smoke documentation entry.',
    content: '# Vector3\n\nSmoke documentation entry.',
    kind: 'class',
    namespace: 'UnityEngine',
    version: '2022.3'
  }]
}, null, 2));
writeFileSync(join(reflectDir, 'reflect-index.json'), JSON.stringify({
  unityVersion: '2022.3',
  assemblies: [{ name: 'UnityEngine.CoreModule', status: 'loaded' }],
  entries: [{
    name: 'Vector3',
    fullName: 'UnityEngine.Vector3',
    namespace: 'UnityEngine',
    kind: 'struct',
    assembly: 'UnityEngine.CoreModule',
    signature: 'public struct Vector3',
    summary: 'Smoke reflection entry.',
    members: [{ name: 'magnitude', kind: 'property', signature: 'public float magnitude', returnType: 'float' }]
  }]
}, null, 2));
writeFileSync(batchPath, JSON.stringify({
  steps: [{ id: 'docs', command: ['tools', 'run', 'docs.list'] }]
}, null, 2));

const server = createServer((req, res) => {
  if (req.url === '/health') {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end('{"ok":true}');
    return;
  }
  if (req.url?.startsWith('/-/v1/search')) {
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ objects: [{ package: { name: 'com.unity.test-framework', version: '1.1.33', description: 'Smoke package result' } }] }));
    return;
  }
  res.writeHead(404, { 'content-type': 'application/json' });
  res.end('{"error":"not_found"}');
});
await new Promise((resolveListen) => server.listen(0, '127.0.0.1', resolveListen));
const port = server.address().port;
const endpoint = `http://127.0.0.1:${port}`;
const env = { ...process.env, UNITY_CLI_DATA_DIR: dataDir, UNITY_EXE: unity };
const rows = [];
const cleanupJobIds = new Set();

function seedInstances() {
  const heartbeat = new Date().toISOString();
  writeFileSync(join(dataDir, 'instances.json'), JSON.stringify({
    version: 1,
    instances: [
      { instanceId: 'smoke-ready', projectName: 'UnityProject', projectPath: project, bridgeEndpoint: endpoint, status: 'ready', unityVersion: '2022.3.61f1', packageVersion: '0.1.0', lastSeenAt: heartbeat },
      { instanceId: 'smoke-stale', projectName: 'UnityProject', projectPath: project, bridgeEndpoint: 'http://127.0.0.1:9', status: 'stale', unityVersion: '2022.3.61f1', packageVersion: '0.1.0', lastSeenAt: '2000-01-01T00:00:00.000Z' }
    ]
  }, null, 2));
}

function parseJson(stdout) {
  const text = (stdout || '').trim();
  if (!text) return undefined;
  return JSON.parse(text);
}

async function run(tool, args) {
  rmSync(join(project, 'Temp', 'UnityLockfile'), { force: true });
  const startedAt = Date.now();
  const result = await new Promise((resolveRun) => {
    const child = spawn(process.execPath, [cliPath, ...args], { env, windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk.toString(); });
    child.stderr.on('data', (chunk) => { stderr += chunk.toString(); });
    child.on('close', (status) => resolveRun({ status, stdout, stderr }));
    child.on('error', (error) => resolveRun({ status: 1, stdout, stderr: `${stderr}\n${error.stack ?? error.message}` }));
  });
  let envelope;
  try { envelope = parseJson(result.stdout); } catch (error) { envelope = { ok: false, parseError: String(error), stdout: result.stdout, stderr: result.stderr }; }
  const row = { tool, args, status: result.status, ok: result.status === 0 && envelope?.ok === true, durationMs: Date.now() - startedAt, envelope };
  rows.push(row);
  const jobId = envelope?.data?.jobId;
  if (jobId) cleanupJobIds.add(jobId);
  return row;
}

async function runExpect(tool, args) {
  const row = await run(tool, args);
  return row.envelope;
}

function seedQueuedJob(id, kind) {
  const jobsPath = join(dataDir, 'jobs.json');
  const existing = existsSync(jobsPath) ? JSON.parse(readFileSync(jobsPath, 'utf8')) : { jobs: [] };
  existing.jobs = (existing.jobs ?? []).filter((job) => job.id !== id);
  const now = new Date().toISOString();
  existing.jobs.push({
    id,
    kind,
    status: 'queued',
    requestId: `${id}_request`,
    createdAt: now,
    updatedAt: now,
    projectPath: project,
    unityPath: unity,
    logPath: join(dataDir, 'logs', `${id}.log`),
    command: [unity, '-batchmode'],
    result: { action: `${kind}.cancel-smoke` }
  });
  writeFileSync(jobsPath, JSON.stringify(existing, null, 2));
}

try {
  seedInstances();
  await run('tools.group', ['tools', 'group']);
  await run('tools.group-list', ['tools', 'group', 'list', 'scene']);
  await run('tools.search', ['tools', 'search', 'scene']);
  await run('tools.describe', ['tools', 'describe', 'scene.active']);
  await run('tools.run', ['tools', 'run', 'docs.list']);

  await run('docs.list', ['tools', 'run', 'docs.list']);
  await run('docs.topic', ['tools', 'run', 'docs.topic', 'tool-execution']);
  await run('docs.get', ['tools', 'run', 'docs.get', 'UnityEngine.Vector3', '--docs-dir', docsDir]);
  await run('docs.search', ['tools', 'run', 'docs.search', 'Vector3', '--docs-dir', docsDir]);
  await run('reflect.type', ['tools', 'run', 'reflect.type', 'UnityEngine.Vector3', '--reflect-dir', reflectDir]);
  await run('reflect.member', ['tools', 'run', 'reflect.member', 'UnityEngine.Vector3.magnitude', '--reflect-dir', reflectDir]);
  await run('reflect.search', ['tools', 'run', 'reflect.search', 'Vector3', '--reflect-dir', reflectDir]);

  await run('package.list', ['tools', 'run', 'package.list', '--project', project]);
  await run('package.status', ['tools', 'run', 'package.status', '--project', project]);
  await run('package.info', ['tools', 'run', 'package.info', 'com.unity.test-framework', '--project', project]);
  await run('package.search', ['tools', 'run', 'package.search', 'com.unity.test-framework', '--project', project, '--registry', endpoint]);
  await run('package.registry', ['tools', 'run', 'package.registry', '--project', project]);
  await run('package.scoped-registry', ['tools', 'run', 'package.scoped-registry', '--project', project]);

  await run('batch schema', ['batch', 'schema']);
  await run('batch run', ['batch', 'run', batchPath, '--dry-run', '--yes']);

  seedInstances();
  await run('target alias set', ['target', 'alias', 'set', 'smoke-ready', 'smoke']);
  await run('target alias list', ['target', 'alias', 'list']);
  await run('target set', ['target', 'set', 'smoke']);
  await run('target clear', ['target', 'clear']);
  await run('target alias unset', ['target', 'alias', 'unset', 'smoke']);
  seedInstances();
  await run('instances prune', ['instances', 'prune', '--yes']);
  await run('doctor', ['doctor', '--project', project]);
  await run('diagnose.capabilities', ['tools', 'run', 'diagnose.capabilities', '--project', project]);

  const refresh = await runExpect('editor.refresh', ['tools', 'run', 'editor.refresh', '--project', project, '--unity', unity, '--timeout-ms', '600000']);
  const refreshJobId = refresh?.data?.jobId;
  if (refreshJobId) {
    await run('jobs status', ['jobs', 'status', refreshJobId]);
    await run('jobs logs', ['jobs', 'logs', refreshJobId, '--tail', '5']);
    await run('jobs describe', ['jobs', 'describe', refreshJobId]);
    seedQueuedJob('smoke-job-cancel', 'refresh');
    await run('jobs cancel refresh', ['jobs', 'cancel', 'smoke-job-cancel', '--yes']);
  }

  const testRun = await runExpect('test.run', ['tools', 'run', 'test.run', '--project', project, '--unity', unity, '--timeout-ms', '600000']);
  const testJobId = testRun?.data?.jobId;
  if (testJobId) {
    await run('jobs status test', ['jobs', 'status', testJobId]);
    await run('jobs logs test', ['jobs', 'logs', testJobId, '--tail', '5']);
    seedQueuedJob('smoke-test-cancel', 'test');
    await run('jobs cancel test', ['jobs', 'cancel', 'smoke-test-cancel', '--yes']);
  }

  const buildRun = await runExpect('build.run', ['tools', 'run', 'build.run', '--project', project, '--unity', unity, '--target', 'StandaloneWindows64', '--output', join(outDir, 'Build.exe'), '--timeout-ms', '600000']);
  const buildJobId = buildRun?.data?.jobId;
  if (buildJobId) {
    await run('jobs status build', ['jobs', 'status', buildJobId]);
    await run('jobs logs build', ['jobs', 'logs', buildJobId, '--tail', '5']);
    seedQueuedJob('smoke-build-cancel', 'build');
    await run('jobs cancel build', ['jobs', 'cancel', 'smoke-build-cancel', '--yes']);
  }

  const install = await runExpect('package.install', ['tools', 'run', 'package.install', 'com.unity.cli.smoke', '--version', '0.0.1', '--project', project, '--unity', unity, '--timeout-ms', '600000']);
  if (install?.data?.jobId) cleanupJobIds.add(install.data.jobId);
  const remove = await runExpect('package.remove', ['tools', 'run', 'package.remove', 'com.unity.cli.smoke', '--project', project, '--unity', unity, '--timeout-ms', '600000', '--yes']);
  if (remove?.data?.jobId) cleanupJobIds.add(remove.data.jobId);
  const resolve = await runExpect('package.resolve', ['tools', 'run', 'package.resolve', '--project', project, '--unity', unity, '--timeout-ms', '600000']);
  if (resolve?.data?.jobId) cleanupJobIds.add(resolve.data.jobId);

  for (const jobId of [...cleanupJobIds]) {
    await run(`cleanup jobs cancel ${jobId}`, ['jobs', 'cancel', jobId, '--yes']);
  }
} finally {
  await new Promise((resolveClose) => server.close(resolveClose));
}

const expectedTools = [
  'editor.refresh','package.list','package.status','package.info','package.search','package.install','package.remove','package.resolve','package.registry','package.scoped-registry','batch schema','batch run','docs.list','docs.topic','docs.get','docs.search','reflect.type','reflect.member','reflect.search','jobs status','jobs logs','jobs describe','jobs cancel refresh','instances prune','target set','target clear','target alias set','target alias unset','target alias list','doctor','diagnose.capabilities','test.run','jobs status test','jobs logs test','jobs cancel test','build.run','jobs status build','jobs logs build','jobs cancel build','tools.group','tools.group-list','tools.search','tools.describe','tools.run'
];
const covered = new Set(rows.filter((row) => expectedTools.includes(row.tool)).map((row) => row.tool));
const missing = expectedTools.filter((tool) => !covered.has(tool));
const failed = rows.filter((row) => expectedTools.includes(row.tool) && !row.ok);
writeFileSync(join(outDir, 'offline-results.json'), JSON.stringify(rows, null, 2));
writeFileSync(join(outDir, 'offline-summary.json'), JSON.stringify({ total: expectedTools.length, covered: covered.size, missing, failed: failed.map((row) => ({ tool: row.tool, code: row.envelope?.error?.code ?? row.envelope?.parseError, message: row.envelope?.error?.message })) }, null, 2));
console.log(JSON.stringify({ ok: missing.length === 0 && failed.length === 0, total: expectedTools.length, covered: covered.size, missing: missing.length, failed: failed.length, outDir }, null, 2));
if (missing.length || failed.length) process.exit(2);
