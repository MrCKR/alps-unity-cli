import { CliError, ErrorCodes } from './errors.js';
import { normalizePath } from './platform.js';

const globalOptions = new Set(['--project', '--instance', '--format', '--timeout', '--verbose', '--help', '-h']);
const valueOptions = new Set(['--project', '--instance', '--format', '--timeout']);

export function parseArgs(argv) {
  const options = {
    format: 'text',
    verbose: false,
  };
  const positionals = [];

  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];

    if (!token.startsWith('-')) {
      positionals.push(token);
      continue;
    }

    if (!globalOptions.has(token)) {
      throw new CliError(ErrorCodes.INVALID_ARGS, `Unknown option: ${token}`, { exitCode: 2 });
    }

    if (token === '--help' || token === '-h') {
      options.help = true;
      continue;
    }

    if (token === '--verbose') {
      options.verbose = true;
      continue;
    }

    if (valueOptions.has(token)) {
      const value = argv[index + 1];
      if (!value || value.startsWith('-')) {
        throw new CliError(ErrorCodes.INVALID_ARGS, `Missing value for ${token}`, { exitCode: 2 });
      }

      index += 1;
      assignOption(options, token, value);
    }
  }

  if (options.format !== 'text' && options.format !== 'json') {
    throw new CliError(ErrorCodes.INVALID_ARGS, '--format must be either text or json', { exitCode: 2 });
  }

  if (options.timeout !== undefined && (!Number.isInteger(options.timeout) || options.timeout <= 0)) {
    throw new CliError(ErrorCodes.INVALID_ARGS, '--timeout must be a positive integer in milliseconds', { exitCode: 2 });
  }

  if (positionals.length > 2) {
    throw new CliError(ErrorCodes.INVALID_ARGS, 'Expected at most <domain> <verb>', { exitCode: 2 });
  }

  return {
    domain: positionals[0],
    verb: positionals[1],
    options,
  };
}

function assignOption(options, token, value) {
  switch (token) {
    case '--project':
      options.project = normalizePath(value);
      break;
    case '--instance':
      options.instance = value;
      break;
    case '--format':
      options.format = value;
      break;
    case '--timeout':
      options.timeout = Number(value);
      break;
    default:
      throw new CliError(ErrorCodes.INVALID_ARGS, `Unsupported option: ${token}`, { exitCode: 2 });
  }
}
