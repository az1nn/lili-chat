import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { api, configureApiAuth } from './api'
import type { AuthResponse, AuthUser } from './types'

type AuthState = {
  token: string | null
  user: AuthUser | null
  initializing: boolean
  login(email: string, password: string): Promise<void>
  register(username: string, email: string, password: string): Promise<void>
  logout(): Promise<void>
  deleteAccount(password: string): Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)
const USER_KEY = 'familychat.user'
const LEGACY_TOKEN_KEYS = ['familychat.access', 'familychat.refresh']
const csrfHeaders = { 'X-FamilyChat-CSRF': '1' }

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [token, setToken] = useState<string | null>(null)
  const tokenRef = useRef<string | null>(null)
  const [initializing, setInitializing] = useState(true)
  const [user, setUser] = useState<AuthUser | null>(() => {
    const raw = localStorage.getItem(USER_KEY)
    try { return raw ? JSON.parse(raw) : null } catch { return null }
  })

  function clearSession() {
    tokenRef.current = null
    setToken(null)
    setUser(null)
    localStorage.removeItem(USER_KEY)
    LEGACY_TOKEN_KEYS.forEach(key => localStorage.removeItem(key))
  }

  function save(auth: AuthResponse) {
    tokenRef.current = auth.accessToken
    setToken(auth.accessToken)
    setUser(auth.user)
    localStorage.setItem(USER_KEY, JSON.stringify(auth.user))
    LEGACY_TOKEN_KEYS.forEach(key => localStorage.removeItem(key))
  }

  async function refreshSession() {
    try {
      const auth = await api<AuthResponse>('/api/v1/auth/refresh', {
        method: 'POST',
        headers: csrfHeaders,
      })
      save(auth)
      return auth.accessToken
    } catch {
      clearSession()
      return null
    }
  }

  useEffect(() => {
    configureApiAuth({
      getAccessToken: () => tokenRef.current,
      refreshAccessToken: refreshSession,
      onUnauthorized: clearSession,
    })
    refreshSession().finally(() => setInitializing(false))
  }, [])

  async function login(email: string, password: string) {
    save(await api<AuthResponse>('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    }))
  }

  async function register(username: string, email: string, password: string) {
    save(await api<AuthResponse>('/api/v1/auth/register', {
      method: 'POST',
      body: JSON.stringify({ username, email, password }),
    }))
  }

  async function logout() {
    try {
      await api<void>('/api/v1/auth/logout', { method: 'POST', headers: csrfHeaders })
    } catch {}
    clearSession()
  }

  async function deleteAccount(password: string) {
    await api<void>('/api/v1/auth/account', {
      method: 'DELETE',
      headers: csrfHeaders,
      body: JSON.stringify({ password }),
    })
    clearSession()
  }

  const value = useMemo(() => ({ token, user, initializing, login, register, logout, deleteAccount }),
    [token, user, initializing])

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const value = useContext(AuthContext)
  if (!value) throw new Error('useAuth deve estar dentro de AuthProvider')
  return value
}
