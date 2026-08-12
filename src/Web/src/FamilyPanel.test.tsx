import { render, screen } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from './api'
import { FamilyPanel } from './FamilyPanel'
import type { FamilyDetails, FamilyRole } from './types'

vi.mock('./api', () => ({ api: vi.fn() }))

const family = (role: FamilyRole): FamilyDetails => ({
  id: 'family',
  name: 'Casa Sá',
  description: 'Família principal',
  role,
  membersCount: 2,
  createdAt: '2026-08-12T00:00:00Z',
  members: [
    {
      userId: 'head',
      publicId: 'ABCD2345',
      username: 'Head User',
      role: 'Head',
      joinedAt: '2026-08-12T00:00:00Z',
    },
    {
      userId: 'member',
      publicId: 'EFGH6789',
      username: 'Member User',
      role: 'Member',
      joinedAt: '2026-08-12T01:00:00Z',
    },
  ],
})

function renderPanel(role: FamilyRole) {
  vi.mocked(api).mockResolvedValue(family(role))
  render(<FamilyPanel
    familyId="family"
    token="token"
    me={role === 'Head' ? 'head' : 'member'}
    onChanged={vi.fn(async () => undefined)}
    onClosed={vi.fn(async () => undefined)}
  />)
}

describe('FamilyPanel governance controls', () => {
  beforeEach(() => vi.mocked(api).mockReset())

  it('shows mutation controls to the Head', async () => {
    renderPanel('Head')

    expect(await screen.findByRole('heading', { name: 'Casa Sá' })).toBeInTheDocument()
    expect(screen.getByPlaceholderText('PublicId do familiar')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Editar' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Tornar Head' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Remover' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Excluir família' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Sair da família' })).not.toBeInTheDocument()
  })

  it('shows read-only membership and leave action to a Member', async () => {
    renderPanel('Member')

    expect(await screen.findByRole('heading', { name: 'Casa Sá' })).toBeInTheDocument()
    expect(screen.queryByPlaceholderText('PublicId do familiar')).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Editar' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Tornar Head' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Sair da família' })).toBeInTheDocument()
  })
})
