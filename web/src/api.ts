export class ApiError extends Error {
  status: number
  body: unknown

  constructor(status: number, body: unknown, message: string) {
    super(message)
    this.status = status
    this.body = body
  }
}

type Options = Omit<RequestInit, 'body'> & { json?: unknown }

/** Calls the API (same origin, so Windows sign-in rides along) and returns parsed JSON. */
export async function api<T>(path: string, options: Options = {}): Promise<T> {
  const { json, ...init } = options
  const headers = new Headers(init.headers)
  let body: string | undefined
  if (json !== undefined) {
    headers.set('Content-Type', 'application/json')
    body = JSON.stringify(json)
  }

  const res = await fetch(path, { ...init, headers, body })
  const text = await res.text()
  let data: unknown = null
  if (text) {
    try {
      data = JSON.parse(text)
    } catch {
      data = text
    }
  }
  if (!res.ok) {
    const message =
      data && typeof data === 'object' && 'error' in data && typeof data.error === 'string'
        ? data.error
        : `Request failed (HTTP ${res.status})`
    throw new ApiError(res.status, data, message)
  }
  return data as T
}

/** The API stores times in UTC without a zone marker; show them in the viewer's local time. */
export function localTime(utc?: string | null): string {
  if (!utc) return ''
  const d = new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(utc) ? utc : utc + 'Z')
  return d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
}
