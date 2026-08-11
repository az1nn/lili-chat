import type { Message } from './types'

export function oldestPersistedCursor(messages: Message[]) {
  const oldest = messages.find(message => !message.id.startsWith('pending-'))
  return oldest ? { beforeSentAt: oldest.sentAt, beforeId: oldest.id } : null
}

export function prependHistory(current: Message[], page: Message[]) {
  const existingIds = new Set(current.map(message => message.id))
  const older = page
    .filter(message => !existingIds.has(message.id))
    .map(message => ({ ...message, status: 'persisted' as const }))
  return [...older, ...current]
}
