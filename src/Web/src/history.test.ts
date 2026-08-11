import { describe, expect, it } from 'vitest'
import { oldestPersistedCursor, prependHistory } from './history'
import type { Message } from './types'

const message = (id: string, sentAt: string): Message => ({
  id,
  roomId: 'room',
  senderId: 'sender',
  content: id,
  sentAt,
})

describe('history pagination', () => {
  it('uses the first persisted message as the next cursor', () => {
    const pending = { ...message('pending-client', '2026-08-11T10:02:00Z'), status: 'sending' as const }
    const oldest = message('older-id', '2026-08-11T10:00:00Z')

    expect(oldestPersistedCursor([pending, oldest, message('newer-id', '2026-08-11T10:01:00Z')]))
      .toEqual({ beforeSentAt: oldest.sentAt, beforeId: oldest.id })
  })

  it('prepends persisted rows without duplicating an overlapping cursor row', () => {
    const overlap = message('overlap', '2026-08-11T10:00:00Z')
    const current = [overlap, message('newer', '2026-08-11T10:01:00Z')]

    expect(prependHistory(current, [message('older', '2026-08-11T09:59:00Z'), overlap]))
      .toEqual([
        { ...message('older', '2026-08-11T09:59:00Z'), status: 'persisted' },
        ...current,
      ])
  })
})
