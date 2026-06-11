import { commandName, executeCommand, resolveCommand } from './commands.js';
import { errorEnvelope, successEnvelope } from './envelope.js';
import { CliError, ErrorCodes } from './errors.js';
import { createRequestId, defaultLogDirectory } from './platform.js';
import { parseArgs } from './parser.js';

export async function runCli(argv, io = {}) {
  const stdout = io.stdout ?? process.stdout;
  const stderr = io.stderr ?? process.stderr;
  const env = io.env ?? process.env;
  const requestId = io.requestId ?? createRequestId();
  let parsed;
  let command = { kind: 'root-help' };

  try {
    parsed = parseArgs(argv);
    command = resolveCommand(parsed.domain, parsed.verb);

    if (parsed.options.help) {
      command = parsed.domain ? { kind: 'domain-help', domain: parsed.domain } : { kind: 'root-help' };
    }

    const diagnostics = createDiagnostics({ requestId, parsed, env });
    const data = executeCommand(command, parsed.options);
    writeSuccess(stdout, parsed.options.format, { requestId, command: commandName(command), data, diagnostics });
    return { exitCode: 0, requestId };
  } catch (error) {
    const cliError = normalizeError(error);
    const options = parsed?.options ?? { format: argv.includes('--format') && argv.includes('json') ? 'json' : 'text' };
    const diagnostics = createDiagnostics({ requestId, parsed, env });
    writeError(stderr, options.format, { requestId, command: commandName(command), error: cliError, diagnostics });
    return { exitCode: cliError.exitCode, requestId };
  }
}

function createDiagnostics({ requestId, parsed, env }) {
  return {
    requestId,
    logDirectory: defaultLogDirectory(env),
    verbose: Boolean(parsed?.options?.verbose),
  };
}

function normalizeError(error) {
  if (error instanceof CliError) {
    return error;
  }

  return new CliError(ErrorCodes.INTERNAL, 'Unexpected CLI error', {
    exitCode: 1,
    details: { cause: error instanceof Error ? error.message : String(error) },
  });
}

function writeSuccess(stdout, format, payload) {
  if (format === 'json') {
    stdout.write(`${JSON.stringify(successEnvelope(payload))}\n`);
    return;
  }

  stdout.write(`${payload.data.help ?? 'OK'}\n`);
}

function writeError(stderr, format, payload) {
  if (format === 'json') {
    stderr.write(`${JSON.stringify(errorEnvelope(payload))}\n`);
    return;
  }

  stderr.write(`${payload.error.code}: ${payload.error.message}\n`);
}
