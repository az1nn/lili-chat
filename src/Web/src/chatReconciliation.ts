import type { Message } from './types'

export function reconcilePersistedMessages(current: Message[], persisted: Message[]) {
  const byId = new Map(current.map(message => [message.id, message]))
  for (const message of persisted) {
    const existing = byId.get(message.id)
    byId.set(message.id, {
      ...existing,
      ...message,
      clientMessageId: existing?.clientMessageId ?? message.clientMessageId,
      status: 'persisted',
    })
  }
  return [...byId.values()].sort((left, right) =>
    left.sentAt.localeCompare(right.sentAt) || left.id.localeCompare(right.id))
}

export function markAccepted(
  messages: Message[], clientMessageId: string, messageId: string,
) {
  return messages.map(message => message.clientMessageId === clientMessageId
    ? { ...message, id: messageId, status: message.status === 'persisted' ? 'persisted' : 'accepted' } as Message
    : message)
}

export function markTimedOut(messages: Message[], clientMessageId: string) {
  return messages.map(message =>
    message.clientMessageId === clientMessageId && message.status === 'accepted'
      ? { ...message, status: 'failed' as const }
      : message)
}
