import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ChatPanel } from './App'
import type { Room, RoomRole } from './types'

const setMessages = vi.fn()

vi.mock('./api', () => ({
  api: vi.fn(async () => []),
}))

vi.mock('./useChat', () => ({
  useChat: () => ({
    messages: [],
    setMessages,
    onlineUsers: [],
    status: 'connected',
    send: vi.fn(),
    retry: vi.fn(),
  }),
}))

const room = (role: RoomRole): Room => ({
  id: 'room',
  name: 'Família',
  ownerId: 'owner',
  membersCount: 1,
  role,
  createdAt: '2026-08-11T00:00:00Z',
})

function renderPanel(role: RoomRole) {
  render(<ChatPanel
    room={room(role)}
    token="token"
    me={role === 'Admin' ? 'owner' : 'member'}
    onRoomChanged={vi.fn()}
    onRoomClosed={vi.fn(async () => undefined)}
  />)
}

describe('ChatPanel authorization controls', () => {
  beforeEach(() => setMessages.mockClear())

  it('shows room management controls only to admins', () => {
    renderPanel('Member')
    expect(screen.queryByPlaceholderText('PublicId do familiar')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Renomear' })).not.toBeInTheDocument()
  })

  it('allows admins to invite and rename', () => {
    renderPanel('Admin')
    expect(screen.getByPlaceholderText('PublicId do familiar')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Renomear' })).toBeInTheDocument()
  })

  it('disables composition for muted members', () => {
    renderPanel('Muted')
    expect(screen.getByPlaceholderText('Você está silenciado nesta sala')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Enviar' })).toBeDisabled()
  })
})
