export function successEnvelope({ requestId, command, data, diagnostics }) {
  return {
    ok: true,
    requestId,
    command,
    data,
    diagnostics,
  };
}

export function errorEnvelope({ requestId, command, error, diagnostics }) {
  return {
    ok: false,
    requestId,
    command,
    error: {
      code: error.code,
      message: error.message,
      details: error.details,
    },
    diagnostics,
  };
}
