import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { runCli } from '../src/cli.js';
import { errorEnvelope, successEnvelope } from '../src/envelope.js';
import { ErrorCodes } from '../src/errors.js';
import { defaultLogDirectory, normalizePath } from '../src/platform.js';
import { parseArgs } from '../src/parser.js';

function createIo() {
  let stdout = '';
  let stderr = '';

  return {
    env: {
      LOCALAPPDATA: 'C:\\Users\\tester\\AppData\\Local',
    },
    stdout: {
      write(chunk) {
        stdout += chunk;
      },
    },
    stderr: {
      write(chunk) {
        stderr += chunk;
      },
    },
    get stdoutText() {
      return stdout;
    },
    get stderrText() {
      return stderr;
    },
  };
}

describe('argument parsing', () => {
  it('parses global options consistently around a command', () => {
    const parsed = parseArgs(['--project', 'C:/Games/MyUnityProject', 'query', 'list', '--instance', 'editor-1', '--timeout', '5000', '--verbose']);

    assert.equal(parsed.domain, 'query');
    assert.equal(parsed.verb, 'list');
    assert.equal(parsed.options.project, 'C:\\Games\\MyUnityProject');
    assert.equal(parsed.options.instance, 'editor-1');
    assert.equal(parsed.options.timeout, 5000);
    assert.equal(parsed.options.verbose, true);
  });

  it('rejects unknown options as invalid args', () => {
    assert.throws(
      () => parseArgs(['query', 'list', '--unsafe']),
      (error) => error.code === ErrorCodes.INVALID_ARGS && error.exitCode === 2,
    );
  });
});

describe('envelopes', () => {
  it('creates a success envelope', () => {
    assert.deepEqual(successEnvelope({ requestId: 'req-1', command: 'help', data: { value: true }, diagnostics: { logDirectory: 'C:\\logs' } }), {
      ok: true,
      requestId: 'req-1',
      command: 'help',
      data: { value: true },
      diagnostics: { logDirectory: 'C:\\logs' },
    });
  });

  it('creates an error envelope', () => {
    assert.deepEqual(errorEnvelope({
      requestId: 'req-1',
      command: 'query list',
      error: { code: ErrorCodes.INVALID_ARGS, message: 'bad args', details: { option: '--bad' } },
      diagnostics: { logDirectory: 'C:\\logs' },
    }), {
      ok: false,
      requestId: 'req-1',
      command: 'query list',
      error: { code: ErrorCodes.INVALID_ARGS, message: 'bad args', details: { option: '--bad' } },
      diagnostics: { logDirectory: 'C:\\logs' },
    });
  });
});

describe('windows paths', () => {
  it('normalizes project paths with Windows separators', () => {
    assert.equal(normalizePath('C:/work/project/Assets'), 'C:\\work\\project\\Assets');
  });

  it('uses the Windows local app data log directory', () => {
    assert.equal(defaultLogDirectory({ LOCALAPPDATA: 'C:\\Users\\tester\\AppData\\Local' }), 'C:\\Users\\tester\\AppData\\Local\\AlpsUnityCli\\logs');
  });
});

describe('cli runner', () => {
  it('prints text help with zero exit code', async () => {
    const io = createIo();
    const result = await runCli(['--help'], { ...io, requestId: 'req-help' });

    assert.equal(result.exitCode, 0);
    assert.match(io.stdoutText, /Usage: alps-unity <domain> <verb>/);
    assert.equal(io.stderrText, '');
  });

  it('prints every domain help as a json envelope', async () => {
    for (const [domain, expectedCommand] of [
      ['connect', 'alps-unity connect status'],
      ['query', 'alps-unity query list'],
      ['edit', 'alps-unity edit apply'],
      ['job', 'alps-unity job run'],
      ['automation', 'alps-unity automation run'],
    ]) {
      const io = createIo();
      const result = await runCli([domain, '--help', '--format', 'json'], { ...io, requestId: `req-json-help-${domain}` });
      const output = JSON.parse(io.stdoutText);

      assert.equal(result.exitCode, 0);
      assert.equal(output.ok, true);
      assert.equal(output.requestId, `req-json-help-${domain}`);
      assert.match(output.data.help, new RegExp(expectedCommand));
      assert.equal(output.diagnostics.logDirectory, 'C:\\Users\\tester\\AppData\\Local\\AlpsUnityCli\\logs');
    }
  });

  it('returns invalid args envelope and exit code 2', async () => {
    const io = createIo();
    const result = await runCli(['query', 'list', '--format', 'json', '--timeout', '0'], { ...io, requestId: 'req-invalid' });
    const output = JSON.parse(io.stderrText);

    assert.equal(result.exitCode, 2);
    assert.equal(output.ok, false);
    assert.equal(output.error.code, ErrorCodes.INVALID_ARGS);
  });

  it('registers commands without implementing Unity business behavior', async () => {
    const io = createIo();
    const result = await runCli(['job', 'run', '--format', 'json'], { ...io, requestId: 'req-unimplemented' });
    const output = JSON.parse(io.stderrText);

    assert.equal(result.exitCode, 1);
    assert.equal(output.ok, false);
    assert.equal(output.error.code, ErrorCodes.NOT_IMPLEMENTED);
    assert.deepEqual(output.error.details, { domain: 'job', verb: 'run' });
  });
});
