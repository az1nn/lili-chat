const API_URL = import.meta.env.VITE_API_URL ?? 'http://localhost:5000'

type AuthHandlers = {
  getAccessToken(): string | null
  refreshAccessToken(): Promise<string | null>
  onUnauthorized(): void
}

let authHandlers: AuthHandlers | null = null
let refreshInFlight: Promise<string | null> | null = null
const refreshExcludedPaths = new Set([
  '/api/v1/auth/login',
  '/api/v1/auth/register',
  '/api/v1/auth/refresh',
  '/api/v1/auth/logout',
])

export class ApiError extends Error {
  constructor(public readonly status: number, message: string) {
    super(message)
    this.name = 'ApiError'
  }
}

export function configureApiAuth(handlers: AuthHandlers) {
  authHandlers = handlers
}

async function request(path: string, init: RequestInit, token: string | null) {
  const headers = new Headers(init.headers)
  if (!headers.has('Content-Type') && init.body) headers.set('Content-Type', 'application/json')
  if (token) headers.set('Authorization', `Bearer ${token}`)
  return fetch(`${API_URL}${path}`, { ...init, headers, credentials: 'include' })
}

export async function api<T>(
  path: string,
  init: RequestInit = {},
  token?: string | null,
): Promise<T> {
  let res = await request(path, init, token ?? authHandlers?.getAccessToken() ?? null)

  if (res.status === 401 && !refreshExcludedPaths.has(path) && authHandlers) {
    const handlers = authHandlers
    refreshInFlight ??= handlers.refreshAccessToken().finally(() => {
      refreshInFlight = null
    })
    const refreshedToken = await refreshInFlight
    if (refreshedToken) res = await request(path, init, refreshedToken)
    else handlers.onUnauthorized()
  }

  if (!res.ok) {
    let message = `HTTP ${res.status}`
    try {
      const body = await res.json()
      message = body.error || body.title || message
    } catch {}
    throw new ApiError(res.status, message)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const apiUrl = API_URL
