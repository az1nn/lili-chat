import { describe, expect, it } from 'vitest'
import { markAccepted, markTimedOut, reconcilePersistedMessages } from './chatReconciliation'
import type { Message } from './types'

const message = (id: string, status: Message['status'], clientMessageId?: string): Message => ({
  id, clientMessageId, roomId: 'room', senderId: 'sender', content: id,
  sentAt: `2026-08-11T00:00:0${id === 'old' ? 0 : 1}Z`, status,
})

describe('chat reconciliation', () => {
  it('reconciles a missed persisted event without losing the client id', () => {
    const result = reconcilePersistedMessages(
      [message('server-id', 'accepted', 'client-id')],
      [message('server-id', undefined)])

    expect(result).toEqual([{ ...message('server-id', undefined), clientMessageId: 'client-id', status: 'persisted' }])
  })

  it('does not downgrade persisted when the send response arrives late', () => {
    const result = markAccepted(
      [message('server-id', 'persisted', 'client-id')], 'client-id', 'server-id')

    expect(result[0].status).toBe('persisted')
  })

  it('fails only messages whose accepted deadline expired', () => {
    const result = markTimedOut([
      message('accepted', 'accepted', 'late'),
      message('persisted', 'persisted', 'done'),
    ], 'late')

    expect(result.map(value => value.status)).toEqual(['failed', 'persisted'])
  })
})
