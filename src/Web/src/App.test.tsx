import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { AuthProvider } from './AuthContext'

describe('authentication screen', () => {
  beforeEach(() => {
    localStorage.clear()
    vi.restoreAllMocks()
  })

  it('clears legacy tokens and shows login when the refresh cookie is unavailable', async () => {
    localStorage.setItem('familychat.access', 'legacy-access')
    localStorage.setItem('familychat.refresh', 'legacy-refresh')
    const fetchMock = vi.fn().mockResolvedValue(new Response(null, { status: 401 }))
    vi.stubGlobal('fetch', fetchMock)

    render(<AuthProvider><App /></AuthProvider>)

    expect(await screen.findByRole('heading', { name: 'Family Chat' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Entrar' })).toBeInTheDocument()
    expect(localStorage.getItem('familychat.access')).toBeNull()
    expect(localStorage.getItem('familychat.refresh')).toBeNull()
    expect(fetchMock).toHaveBeenCalledWith(
      'http://localhost:5000/api/v1/auth/refresh',
      expect.objectContaining({ credentials: 'include', method: 'POST' }),
    )
  })

  it('exposes bounded registration fields', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 401 })))
    render(<AuthProvider><App /></AuthProvider>)

    fireEvent.click(await screen.findByRole('button', { name: 'Criar uma conta' }))

    expect(screen.getByPlaceholderText('Username')).toHaveAttribute('minLength', '3')
    expect(screen.getByPlaceholderText('Username')).toHaveAttribute('maxLength', '100')
    expect(screen.getByPlaceholderText('Email')).toHaveAttribute('maxLength', '255')
    expect(screen.getByPlaceholderText('Senha')).toHaveAttribute('minLength', '8')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Criar conta' })).toBeEnabled())
  })
})
