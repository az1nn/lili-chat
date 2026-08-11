import { describe, expect, it, vi } from 'vitest'
import { ApiError } from './api'
import { waitForProfileProjection } from './profileProjection'
import type { UserProfile } from './types'

const profile: UserProfile = {
  id: 'user',
  publicId: 'ABCDEFGH',
  username: 'alice',
  email: 'alice@example.test',
}

describe('PublicId projection polling', () => {
  it('retries transient 404 responses with bounded backoff', async () => {
    const load = vi.fn()
      .mockRejectedValueOnce(new ApiError(404, 'not projected'))
      .mockRejectedValueOnce(new ApiError(404, 'not projected'))
      .mockResolvedValue(profile)
    const sleep = vi.fn(async () => undefined)

    await expect(waitForProfileProjection('token', { load, sleep }))
      .resolves.toEqual(profile)
    expect(sleep).toHaveBeenNthCalledWith(1, 500, undefined)
    expect(sleep).toHaveBeenNthCalledWith(2, 1000, undefined)
  })

  it('returns a timeout result after the configured attempts', async () => {
    const load = vi.fn().mockRejectedValue(new ApiError(404, 'not projected'))
    const sleep = vi.fn(async () => undefined)

    await expect(waitForProfileProjection('token', { attempts: 3, load, sleep }))
      .resolves.toBeNull()
    expect(load).toHaveBeenCalledTimes(3)
    expect(sleep).toHaveBeenCalledTimes(2)
  })

  it('does not hide authorization or service failures', async () => {
    const error = new ApiError(503, 'unavailable')
    const load = vi.fn().mockRejectedValue(error)

    await expect(waitForProfileProjection('token', { load })).rejects.toBe(error)
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('cancels the backoff when the workspace is disposed', async () => {
    const controller = new AbortController()
    const load = vi.fn(async () => {
      controller.abort()
      throw new ApiError(404, 'not projected')
    })

    await expect(waitForProfileProjection('token', {
      load,
      signal: controller.signal,
    })).rejects.toMatchObject({ name: 'AbortError' })
    expect(load).toHaveBeenCalledTimes(1)
  })
})
