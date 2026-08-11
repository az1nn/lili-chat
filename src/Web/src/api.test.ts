import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api, configureApiAuth } from './api'

describe('API authentication retry', () => {
  beforeEach(() => vi.restoreAllMocks())

  it('uses one refresh for concurrent 401 responses and retries both requests', async () => {
    let token = 'expired-token'
    let releaseRefresh: (() => void) | undefined
    const refreshGate = new Promise<void>(resolve => { releaseRefresh = resolve })
    const refreshAccessToken = vi.fn(async () => {
      await refreshGate
      token = 'fresh-token'
      return token
    })
    const onUnauthorized = vi.fn()
    configureApiAuth({ getAccessToken: () => token, refreshAccessToken, onUnauthorized })

    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      const authorization = new Headers(init?.headers).get('Authorization')
      return authorization === 'Bearer fresh-token'
        ? new Response(JSON.stringify({ ok: true }), {
            status: 200,
            headers: { 'Content-Type': 'application/json' },
          })
        : new Response(null, { status: 401 })
    })
    vi.stubGlobal('fetch', fetchMock)

    const first = api<{ ok: boolean }>('/api/v1/rooms')
    const second = api<{ ok: boolean }>('/api/v1/rooms/another')
    await vi.waitFor(() => expect(refreshAccessToken).toHaveBeenCalledTimes(1))
    releaseRefresh?.()

    await expect(Promise.all([first, second])).resolves.toEqual([{ ok: true }, { ok: true }])
    expect(refreshAccessToken).toHaveBeenCalledTimes(1)
    expect(fetchMock).toHaveBeenCalledTimes(4)
    expect(onUnauthorized).not.toHaveBeenCalled()
  })

  it('refreshes an expired token before retrying account deletion', async () => {
    let token = 'expired-token'
    const refreshAccessToken = vi.fn(async () => (token = 'fresh-token'))
    configureApiAuth({
      getAccessToken: () => token,
      refreshAccessToken,
      onUnauthorized: vi.fn(),
    })
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) =>
      new Headers(init?.headers).get('Authorization') === 'Bearer fresh-token'
        ? new Response(null, { status: 204 })
        : new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    await expect(api<void>('/api/v1/auth/account', { method: 'DELETE' })).resolves.toBeUndefined()
    expect(refreshAccessToken).toHaveBeenCalledOnce()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })
})
