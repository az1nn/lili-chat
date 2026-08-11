import { useEffect, useRef, useState } from 'react'
import { HubConnection, HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import { apiUrl } from './api'
import type { Message } from './types'

type HubResult = { success: boolean; error?: string; data?: { messageId?: string; onlineUsers?: string[] } }

export function useChat(
  roomId: string | null,
  token: string | null,
  senderId: string | null,
  onAccessRevoked?: () => void | Promise<void>,
) {
  const connectionRef = useRef<HubConnection | null>(null)
  const persistedIdsRef = useRef(new Set<string>())
  const onAccessRevokedRef = useRef(onAccessRevoked)
  const [messages, setMessages] = useState<Message[]>([])
  const [onlineUsers, setOnlineUsers] = useState<string[]>([])
  const [status, setStatus] = useState('disconnected')

  useEffect(() => {
    onAccessRevokedRef.current = onAccessRevoked
  }, [onAccessRevoked])

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
      setMessages(prev => prev.map(message =>
        message.id === payload.messageId ? { ...message, status: 'persisted' } : message))
    })
    connection.on('RoomAccessRevoked', async () => {
      setStatus('access-revoked')
      setOnlineUsers([])
      await connection.stop()
      await onAccessRevokedRef.current?.()
    })
    connection.onreconnecting(() => setStatus('reconnecting'))
    connection.onreconnected(async () => {
      setStatus('connected')
      await connection.invoke('JoinRoom', roomId)
    })
    connection.onclose(() => setStatus('disconnected'))

    let cancelled = false
    ;(async () => {
      try {
        setStatus('connecting')
        await connection.start()
        if (cancelled) return
        const result = await connection.invoke<HubResult>('JoinRoom', roomId)
        if (!result.success) throw new Error(result.error || 'Falha ao entrar na sala')
        setOnlineUsers(result.data?.onlineUsers || [])
        setStatus('connected')
      } catch (e) {
        if (!cancelled) setStatus(e instanceof Error ? e.message : 'error')
      }
    })()

    return () => {
      cancelled = true
      connection.stop().catch(() => undefined)
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
      if (!result.success) throw new Error(result.error || 'Falha ao enviar')
      setMessages(prev => prev.map(x => x.clientMessageId === clientMessageId ? { ...x, status: 'accepted' } : x))
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
