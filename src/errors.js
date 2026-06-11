export const ErrorCodes = Object.freeze({
  INVALID_ARGS: 'E_INVALID_ARGS',
  NOT_IMPLEMENTED: 'E_NOT_IMPLEMENTED',
  INTERNAL: 'E_INTERNAL',
});

export class CliError extends Error {
  constructor(code, message, options = {}) {
    super(message);
    this.name = 'CliError';
    this.code = code;
    this.exitCode = options.exitCode ?? 1;
    this.details = options.details;
  }
}
