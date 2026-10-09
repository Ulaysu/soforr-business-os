/**
 * What the API tells us when a call fails: an RFC 9457 problem with a stable `code` and, for validation problems, the
 * messages per input field. The messages are written by the server for people, so they are shown as they are.
 */
export interface ApiProblem {
  status: number
  /** The stable machine-readable code, for example `identity.invalid_credentials`. */
  code: string | null
  /** A sentence that is safe to show to the user. */
  message: string
  /** Messages per input field name (camelCase, as in the request), or an empty object. */
  fieldErrors: Record<string, string[]>
}

const fallbackByStatus: Record<number, string> = {
  400: 'Some of the details are not valid.',
  401: 'You need to sign in.',
  403: 'You are not allowed to do this.',
  404: 'That could not be found.',
  409: 'That conflicts with something that already exists.',
  422: 'That cannot be done right now.',
  429: 'Too many attempts. Wait a moment and try again.',
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/** Turns a failed response (its parsed body, if any, and its status) into an {@link ApiProblem}. Never throws. */
export function toProblem(body: unknown, status: number): ApiProblem {
  const record = isRecord(body) ? body : {}

  const fieldErrors: Record<string, string[]> = {}
  if (isRecord(record.errors)) {
    for (const [field, messages] of Object.entries(record.errors)) {
      if (Array.isArray(messages)) {
        fieldErrors[field] = messages.filter(
          (message): message is string => typeof message === 'string',
        )
      }
    }
  }

  const detail =
    typeof record.detail === 'string' && record.detail.length > 0 ? record.detail : null
  const firstFieldMessage = Object.values(fieldErrors).flat()[0]
  const fallback =
    fallbackByStatus[status] ??
    (status >= 500 ? 'Something went wrong on our side. Try again.' : 'That did not work.')

  return {
    status,
    code: typeof record.code === 'string' ? record.code : null,
    message: detail ?? firstFieldMessage ?? fallback,
    fieldErrors,
  }
}

/** A network failure (no response at all) as a problem, so screens handle both the same way. */
export function networkProblem(): ApiProblem {
  return {
    status: 0,
    code: null,
    message: 'The server could not be reached. Check the connection and try again.',
    fieldErrors: {},
  }
}
