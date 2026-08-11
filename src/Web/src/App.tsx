import { FormEvent, useCallback, useEffect, useRef, useState } from 'react'
import { api } from './api'
import { useAuth } from './AuthContext'
import { oldestPersistedCursor, prependHistory } from './history'
import { useChat } from './useChat'
import type { Room, Message, RoomMember, UserProfile } from './types'
import './styles.css'

function AuthScreen() {
  const { login, register } = useAuth()
  const [mode, setMode] = useState<'login' | 'register'>('login')
  const [username, setUsername] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')

  async function submit(e: FormEvent) {
    e.preventDefault()
    setError('')
    try {
      if (mode === 'login') await login(email, password)
      else await register(username, email, password)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha')
    }
  }

  return <main className="auth-shell">
    <form className="card auth-card" onSubmit={submit}>
      <div className="logo">FC</div>
      <h1>Family Chat</h1>
      <p className="muted">Chat privado para sua família.</p>
      {mode === 'register' && <input placeholder="Username" minLength={3} maxLength={100} required value={username} onChange={e => setUsername(e.target.value)} />}
      <input type="email" placeholder="Email" maxLength={255} required value={email} onChange={e => setEmail(e.target.value)} />
      <input type="password" placeholder="Senha" minLength={8} maxLength={128} required value={password} onChange={e => setPassword(e.target.value)} />
      {error && <div className="error">{error}</div>}
      <button>{mode === 'login' ? 'Entrar' : 'Criar conta'}</button>
      <button type="button" className="ghost" onClick={() => setMode(mode === 'login' ? 'register' : 'login')}>
        {mode === 'login' ? 'Criar uma conta' : 'Já tenho conta'}
      </button>
    </form>
  </main>
}

function ChatPanel({ room, token, me, onRoomChanged, onRoomClosed }: {
  room: Room
  token: string
  me: string
  onRoomChanged(room: Room): void
  onRoomClosed(): Promise<void>
}) {
  const chat = useChat(room.id, token, me, onRoomClosed)
  const [input, setInput] = useState('')
  const [members, setMembers] = useState<RoomMember[]>([])
  const [publicId, setPublicId] = useState('')
  const [error, setError] = useState('')
  const [hasOlder, setHasOlder] = useState(false)
  const [loadingOlder, setLoadingOlder] = useState(false)
  const loadingOlderRef = useRef(false)
  const messagesRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    api<Message[]>(`/api/v1/messages/room/${room.id}?take=50`, {}, token)
      .then(rows => {
        chat.setMessages(rows.map(m => ({ ...m, status: 'persisted' })))
        setHasOlder(rows.length === 50)
        requestAnimationFrame(() => {
          const viewport = messagesRef.current
          if (viewport) viewport.scrollTop = viewport.scrollHeight
        })
      })
      .catch(e => setError(e.message))
    api<RoomMember[]>(`/api/v1/rooms/${room.id}/members`, {}, token)
      .then(setMembers)
      .catch(() => undefined)
  }, [room.id, token])

  const loadOlder = useCallback(async () => {
    const cursor = oldestPersistedCursor(chat.messages)
    if (!cursor || !hasOlder || loadingOlderRef.current) return
    const viewport = messagesRef.current
    const previousHeight = viewport?.scrollHeight ?? 0
    const previousTop = viewport?.scrollTop ?? 0
    try {
      loadingOlderRef.current = true
      setLoadingOlder(true)
      const query = new URLSearchParams({
        take: '50',
        beforeSentAt: cursor.beforeSentAt,
        beforeId: cursor.beforeId,
      })
      const rows = await api<Message[]>(`/api/v1/messages/room/${room.id}?${query}`, {}, token)
      chat.setMessages(current => prependHistory(current, rows))
      setHasOlder(rows.length === 50)
      requestAnimationFrame(() => {
        const currentViewport = messagesRef.current
        if (currentViewport) {
          currentViewport.scrollTop = previousTop + currentViewport.scrollHeight - previousHeight
        }
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao carregar histórico')
    } finally {
      loadingOlderRef.current = false
      setLoadingOlder(false)
    }
  }, [chat.messages, chat.setMessages, hasOlder, room.id, token])

  useEffect(() => {
    const viewport = messagesRef.current
    if (!viewport || !hasOlder || loadingOlder) return
    if (viewport.scrollHeight <= viewport.clientHeight) void loadOlder()
  }, [chat.messages.length, hasOlder, loadingOlder, loadOlder])

  async function submit(e: FormEvent) {
    e.preventDefault()
    if (!input.trim()) return
    const ok = await chat.send(input.trim())
    if (ok) setInput('')
  }

  async function addMember(e: FormEvent) {
    e.preventDefault()
    try {
      setError('')
      await api(`/api/v1/rooms/${room.id}/members/by-public-id`, {
        method: 'POST',
        body: JSON.stringify({ publicId, role: 'Member' }),
      }, token)
      setPublicId('')
      setMembers(await api<RoomMember[]>(`/api/v1/rooms/${room.id}/members`, {}, token))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha')
    }
  }

  async function renameRoom() {
    const name = window.prompt('Novo nome da sala', room.name)?.trim()
    if (!name || name === room.name) return
    try {
      const updated = await api<Room>(`/api/v1/rooms/${room.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ name, description: room.description || '' }),
      }, token)
      onRoomChanged(updated)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao renomear sala')
    }
  }

  async function closeRoom() {
    const owner = room.ownerId === me
    if (!window.confirm(owner ? 'Arquivar esta sala?' : 'Sair desta sala?')) return
    try {
      await api<void>(`/api/v1/rooms/${room.id}${owner ? '' : '/leave'}`, {
        method: owner ? 'DELETE' : 'POST',
      }, token)
      await onRoomClosed()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao atualizar sala')
    }
  }

  async function updateMemberRole(member: RoomMember, role: 'Admin' | 'Member' | 'Muted') {
    try {
      await api(`/api/v1/rooms/${room.id}/members/${member.userId}/role`, {
        method: 'PATCH',
        body: JSON.stringify({ role }),
      }, token)
      setMembers(await api<RoomMember[]>(`/api/v1/rooms/${room.id}/members`, {}, token))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao alterar role')
    }
  }

  async function removeMember(member: RoomMember) {
    if (!window.confirm(`Remover ${member.username} da sala?`)) return
    try {
      await api<void>(`/api/v1/rooms/${room.id}/members/${member.userId}`, { method: 'DELETE' }, token)
      setMembers(current => current.filter(x => x.userId !== member.userId))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao remover membro')
    }
  }

  return <section className="chat">
    <header className="chat-header">
      <div>
        <h2>{room.name}</h2>
        <div className="muted">{chat.onlineUsers.length} online · {chat.status}</div>
        <div className="room-actions">
          {room.role === 'Admin' && <button className="ghost compact" onClick={renameRoom}>Renomear</button>}
          <button className="ghost compact danger" onClick={closeRoom}>
            {room.ownerId === me ? 'Arquivar' : 'Sair'}
          </button>
        </div>
      </div>
      <form className="invite" onSubmit={addMember}>
        <input placeholder="PublicId do familiar" minLength={8} maxLength={8} required value={publicId} onChange={e => setPublicId(e.target.value.toUpperCase())} />
        <button>Adicionar</button>
      </form>
    </header>

    {error && <div className="error inline">{error}</div>}

    <div className="messages" ref={messagesRef}
      onScroll={event => {
        if (event.currentTarget.scrollTop <= 80) void loadOlder()
      }}>
      {loadingOlder && <div className="muted history-loading">Carregando mensagens anteriores...</div>}
      {chat.messages.map(m => <div key={m.id} className={`message ${m.senderId === me ? 'mine' : ''}`}>
        <div>{m.content}</div>
        <small>
          {new Date(m.sentAt).toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' })}
          {m.senderId === me && m.status && ` · ${m.status}`}
          {m.status === 'failed' && m.clientMessageId && <button className="retry" onClick={() => chat.retry(m.clientMessageId!)}>tentar novamente</button>}
        </small>
      </div>)}
      {!chat.messages.length && <div className="empty">Nenhuma mensagem ainda.</div>}
    </div>

    <form className="composer" onSubmit={submit}>
      <input placeholder="Digite uma mensagem..." maxLength={2000} required value={input} onChange={e => setInput(e.target.value)} />
      <button disabled={chat.status !== 'connected'}>Enviar</button>
    </form>

    <aside className="member-strip">
      {members.map(m => <span key={m.userId}>
        {m.username} · {m.role}
        {room.role === 'Admin' && m.userId !== room.ownerId && m.userId !== me && <span className="member-actions">
          {m.role === 'Muted'
            ? <button onClick={() => updateMemberRole(m, 'Member')}>desmutar</button>
            : <button onClick={() => updateMemberRole(m, 'Muted')}>silenciar</button>}
          {room.ownerId === me && <button onClick={() => updateMemberRole(m, m.role === 'Admin' ? 'Member' : 'Admin')}>
            {m.role === 'Admin' ? 'rebaixar' : 'admin'}
          </button>}
          <button onClick={() => removeMember(m)}>remover</button>
        </span>}
      </span>)}
    </aside>
  </section>
}

function Workspace() {
  const { token, user, logout } = useAuth()
  const [rooms, setRooms] = useState<Room[]>([])
  const [selected, setSelected] = useState<Room | null>(null)
  const [profile, setProfile] = useState<UserProfile | null>(null)
  const [name, setName] = useState('')
  const [error, setError] = useState('')

  async function loadRooms() {
    if (!token) return
    const r = await api<Room[]>('/api/v1/rooms', {}, token)
    setRooms(r)
    if (selected) setSelected(r.find(x => x.id === selected.id) || null)
  }

  useEffect(() => {
    if (!token) return
    let cancelled = false
    loadRooms().catch(e => setError(e.message))
    ;(async () => {
      for (let attempt = 0; attempt < 10 && !cancelled; attempt++) {
        try {
          const value = await api<UserProfile>('/api/v1/users/me', {}, token)
          if (!cancelled) setProfile(value)
          return
        } catch {
          if (!cancelled) setProfile(null)
          await new Promise(resolve => setTimeout(resolve, Math.min(1000 * (attempt + 1), 5000)))
        }
      }
    })()
    return () => { cancelled = true }
  }, [token])

  async function createRoom(e: FormEvent) {
    e.preventDefault()
    try {
      const room = await api<Room>('/api/v1/rooms', {
        method: 'POST',
        body: JSON.stringify({ name, description: '' }),
      }, token)
      setName('')
      await loadRooms()
      setSelected(room)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha')
    }
  }

  function roomChanged(room: Room) {
    setRooms(current => current.map(item => item.id === room.id ? room : item))
    setSelected(room)
  }

  async function roomClosed() {
    setSelected(null)
    await loadRooms()
  }

  return <div className="app-shell">
    <aside className="sidebar">
      <div className="brand"><span className="logo small">FC</span><strong>Family Chat</strong></div>
      <div className="profile">
        <strong>{user?.username}</strong>
        <span>{user?.email}</span>
        <code>{profile?.publicId || 'PublicId sincronizando...'}</code>
      </div>

      <form className="new-room" onSubmit={createRoom}>
        <input placeholder="Nova sala" maxLength={100} required value={name} onChange={e => setName(e.target.value)} />
        <button>+</button>
      </form>

      <nav>
        {rooms.map(room => <button key={room.id} className={`room-button ${selected?.id === room.id ? 'active' : ''}`} onClick={() => setSelected(room)}>
          <span>{room.name}</span><small>{room.membersCount} membros</small>
        </button>)}
      </nav>

      {error && <div className="error">{error}</div>}
      <button className="ghost logout" onClick={logout}>Sair</button>
    </aside>

    {selected && token && user
      ? <ChatPanel room={selected} token={token} me={user.id}
          onRoomChanged={roomChanged} onRoomClosed={roomClosed} />
      : <section className="welcome"><div><h1>Selecione ou crie uma sala</h1><p className="muted">Compartilhe seu PublicId para reunir a família.</p></div></section>}
  </div>
}

export default function App() {
  const { token, initializing } = useAuth()
  if (initializing) return <main className="auth-shell"><div className="card auth-card">Carregando sessão...</div></main>
  return token ? <Workspace /> : <AuthScreen />
}
