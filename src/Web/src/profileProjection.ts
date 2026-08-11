import { api, ApiError } from './api'
import type { UserProfile } from './types'

type ProjectionOptions = {
  attempts?: number
  signal?: AbortSignal
  sleep?: (delayMs: number, signal?: AbortSignal) => Promise<void>
  load?: (signal?: AbortSignal) => Promise<UserProfile>
}

function abortableSleep(delayMs: number, signal?: AbortSignal) {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal?.reason ?? new DOMException('Aborted', 'AbortError'))
      return
    }
    const onAbort = () => {
      window.clearTimeout(timer)
      reject(signal?.reason ?? new DOMException('Aborted', 'AbortError'))
    }
    const timer = window.setTimeout(() => {
      signal?.removeEventListener('abort', onAbort)
      resolve()
    }, delayMs)
    signal?.addEventListener('abort', onAbort, { once: true })
  })
}

export async function waitForProfileProjection(
  token: string,
  options: ProjectionOptions = {},
): Promise<UserProfile | null> {
  const attempts = options.attempts ?? 8
  const sleep = options.sleep ?? abortableSleep
  const load = options.load ?? (signal => api<UserProfile>('/api/v1/users/me', { signal }, token))

  for (let attempt = 0; attempt < attempts; attempt++) {
    try {
      return await load(options.signal)
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 404) throw error
      if (attempt === attempts - 1) return null
      await sleep(Math.min(500 * 2 ** attempt, 4000), options.signal)
    }
  }
  return null
}
