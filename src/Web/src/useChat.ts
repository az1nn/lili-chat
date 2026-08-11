import { useEffect, useRef, useState } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import { api, apiUrl } from './api'
import { markAccepted, markTimedOut, reconcilePersistedMessages } from './chatReconciliation'
import type { Message, RoomRole } from './types'

type HubResult = { success: boolean; error?: string; data?: { messageId?: string; onlineUsers?: string[]; role?: RoomRole } }

export function useChat(
  roomId: string | null,
  token: string | null,
  senderId: string | null,
  onAccessRevoked?: () => void | Promise<void>,
  onRoleChanged?: (role: RoomRole) => void,
) {
  const connectionRef = useRef<HubConnection | null>(null)
  const persistedIdsRef = useRef(new Set<string>())
  const persistenceTimersRef = useRef(new Map<string, ReturnType<typeof setTimeout>>())
  const closeStatusRef = useRef<string | null>(null)
  const onAccessRevokedRef = useRef(onAccessRevoked)
  const onRoleChangedRef = useRef(onRoleChanged)
  const [messages, setMessages] = useState<Message[]>([])
  const [onlineUsers, setOnlineUsers] = useState<string[]>([])
  const [status, setStatus] = useState('disconnected')

  useEffect(() => {
    onAccessRevokedRef.current = onAccessRevoked
  }, [onAccessRevoked])

  useEffect(() => {
    onRoleChangedRef.current = onRoleChanged
  }, [onRoleChanged])

  useEffect(() => {
    if (!roomId || !token) return
    persistedIdsRef.current.clear()

    const connection = new HubConnectionBuilder()
      .withUrl(`${apiUrl}/hubs/chat`, { accessTokenFactory: () => token })
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .build()

    connectionRef.current = connection

    connection.on('MessageReceived', (msg: Message) => {
      const received = persistedIdsRef.current.has(msg.id)
        ? { ...msg, status: 'persisted' as const }
        : msg
      if (received.status === 'persisted' && received.clientMessageId) {
        clearTimeout(persistenceTimersRef.current.get(received.clientMessageId))
        persistenceTimersRef.current.delete(received.clientMessageId)
      }
      setMessages(prev => {
        const optimisticIndex = received.clientMessageId
          ? prev.findIndex(x => x.clientMessageId === received.clientMessageId)
          : -1
        if (optimisticIndex >= 0) {
          const next = [...prev]
          next[optimisticIndex] = received
          return next
        }
        return prev.some(x => x.id === received.id) ? prev : [...prev, received]
      })
    })
    connection.on('PresenceUpdated', (payload: { onlineUsers: string[] }) => {
      setOnlineUsers(payload.onlineUsers || [])
    })
    connection.on('MessagePersisted', (payload: { messageId: string }) => {
      persistedIdsRef.current.add(payload.messageId)
      setMessages(prev => prev.map(message => {
        if (message.id !== payload.messageId) return message
        if (message.clientMessageId) {
          clearTimeout(persistenceTimersRef.current.get(message.clientMessageId))
          persistenceTimersRef.current.delete(message.clientMessageId)
        }
        return { ...message, status: 'persisted' }
      }))
    })
    connection.on('RoomAccessRevoked', async () => {
      closeStatusRef.current = 'access-revoked'
      setStatus('access-revoked')
      setOnlineUsers([])
      await connection.stop()
      await onAccessRevokedRef.current?.()
    })
    connection.on('RoomRoleChanged', (payload: { role: RoomRole }) => {
      onRoleChangedRef.current?.(payload.role)
    })
    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(async () => {
      setStatus('rejoining')
      try {
        const result = await connection.invoke<HubResult>('JoinRoom', roomId)
        if (!result.success) {
          closeStatusRef.current = 'access-revoked'
          setStatus('access-revoked')
          await connection.stop()
          await onAccessRevokedRef.current?.()
          return
        }
        const persisted = await api<Message[]>(
          `/api/v1/messages/room/${roomId}?take=50`, {}, token)
        setMessages(current => reconcilePersistedMessages(current, persisted))
        setOnlineUsers(result.data?.onlineUsers || [])
        if (result.data?.role) onRoleChangedRef.current?.(result.data.role)
        setStatus('connected')
      } catch (error) {
        const message = error instanceof Error ? error.message : 'Falha ao reentrar na sala'
        closeStatusRef.current = message
        setStatus(message)
        await connection.stop()
      }
    })
    connection.onclose(() => {
      setStatus(closeStatusRef.current ?? 'disconnected')
      closeStatusRef.current = null
    })

    let cancelled = false
    ;(async () => {
      try {
        setStatus('connecting')
        await connection.start()
        if (cancelled) return
        const result = await connection.invoke<HubResult>('JoinRoom', roomId)
        if (!result.success) throw new Error(result.error || 'Falha ao entrar na sala')
        setOnlineUsers(result.data?.onlineUsers || [])
        if (result.data?.role) onRoleChangedRef.current?.(result.data.role)
        setStatus('connected')
      } catch (e) {
        if (!cancelled) setStatus(e instanceof Error ? e.message : 'error')
      }
    })()

    return () => {
      cancelled = true
      closeStatusRef.current = null
      connection.stop().catch(() => undefined)
      for (const timer of persistenceTimersRef.current.values()) clearTimeout(timer)
      persistenceTimersRef.current.clear()
      connectionRef.current = null
      setOnlineUsers([])
    }
  }, [roomId, token])

  async function dispatch(content: string, clientMessageId: string) {
    const connection = connectionRef.current
    if (!roomId || !connection || connection.state !== HubConnectionState.Connected) {
      setMessages(prev => prev.map(x => x.clientMessageId === clientMessageId ? { ...x, status: 'failed' } : x))
      return false
    }
    try {
      const result = await connection.invoke<HubResult>('SendMessage', roomId, content, clientMessageId)
      if (!result.success || !result.data?.messageId)
        throw new Error(result.error || 'Falha ao enviar')
      setMessages(prev => markAccepted(prev, clientMessageId, result.data!.messageId!))
      clearTimeout(persistenceTimersRef.current.get(clientMessageId))
      if (persistedIdsRef.current.has(result.data.messageId)) return true
      persistenceTimersRef.current.set(clientMessageId, setTimeout(() => {
        setMessages(prev => markTimedOut(prev, clientMessageId))
        persistenceTimersRef.current.delete(clientMessageId)
      }, 30_000))
      return true
    } catch {
      setMessages(prev => prev.map(x => x.clientMessageId === clientMessageId ? { ...x, status: 'failed' } : x))
      return false
    }
  }

  async function send(content: string) {
    if (!roomId || !senderId) return false
    const clientMessageId = crypto.randomUUID()
    setMessages(prev => [...prev, {
      id: `pending-${clientMessageId}`,
      clientMessageId,
      roomId,
      senderId,
      content,
      sentAt: new Date().toISOString(),
      status: 'sending',
    }])
    return dispatch(content, clientMessageId)
  }

  async function retry(clientMessageId: string) {
    const message = messages.find(x => x.clientMessageId === clientMessageId)
    if (!message) return false
    setMessages(prev => prev.map(x => x.clientMessageId === clientMessageId ? { ...x, status: 'sending' } : x))
    return dispatch(message.content, clientMessageId)
  }

  return { messages, setMessages, onlineUsers, status, send, retry }
}
