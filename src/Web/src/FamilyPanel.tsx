import { FormEvent, useEffect, useState } from 'react'
import { api } from './api'
import type { FamilyDetails, FamilyMember } from './types'

export function FamilyPanel({ familyId, token, me, onChanged, onClosed }: {
  familyId: string
  token: string
  me: string
  onChanged(): Promise<void>
  onClosed(): Promise<void>
}) {
  const [family, setFamily] = useState<FamilyDetails | null>(null)
  const [publicId, setPublicId] = useState('')
  const [error, setError] = useState('')
  const isHead = family?.role === 'Head'

  async function load() {
    setFamily(await api<FamilyDetails>(`/api/v1/families/${familyId}`, {}, token))
  }

  useEffect(() => {
    setFamily(null)
    setError('')
    load().catch(e => setError(e instanceof Error ? e.message : 'Falha ao carregar família'))
  }, [familyId, token])

  async function editFamily() {
    if (!family) return
    const name = window.prompt('Nome da família', family.name)?.trim()
    if (!name) return
    const description = window.prompt('Descrição da família', family.description || '')
    if (description === null) return
    try {
      const updated = await api<FamilyDetails>(`/api/v1/families/${family.id}`, {
        method: 'PATCH',
        body: JSON.stringify({ name, description }),
      }, token)
      setFamily(updated)
      await onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao editar família')
    }
  }

  async function addMember(e: FormEvent) {
    e.preventDefault()
    if (!family) return
    try {
      setError('')
      await api(`/api/v1/families/${family.id}/members`, {
        method: 'POST',
        body: JSON.stringify({ publicId }),
      }, token)
      setPublicId('')
      await load()
      await onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao adicionar familiar')
    }
  }

  async function transferHead(member: FamilyMember) {
    if (!family || !window.confirm(`Transferir a liderança para ${member.username}?`)) return
    try {
      const updated = await api<FamilyDetails>(`/api/v1/families/${family.id}/head`, {
        method: 'POST',
        body: JSON.stringify({ userId: member.userId }),
      }, token)
      setFamily(updated)
      await onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao transferir liderança')
    }
  }

  async function removeMember(member: FamilyMember) {
    if (!family || !window.confirm(`Remover ${member.username} da família?`)) return
    try {
      await api<void>(`/api/v1/families/${family.id}/members/${member.userId}`, {
        method: 'DELETE',
      }, token)
      await load()
      await onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao remover familiar')
    }
  }

  async function leaveFamily() {
    if (!family || !window.confirm('Sair desta família?')) return
    try {
      await api<void>(`/api/v1/families/${family.id}/leave`, { method: 'POST' }, token)
      await onClosed()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao sair da família')
    }
  }

  async function deleteFamily() {
    if (!family || !window.confirm(`Excluir a família "${family.name}"?`)) return
    try {
      await api<void>(`/api/v1/families/${family.id}`, { method: 'DELETE' }, token)
      await onClosed()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao excluir família')
    }
  }

  if (!family) {
    return <section className="family-panel"><div className="muted">Carregando família...</div>{error && <div className="error">{error}</div>}</section>
  }

  return <section className="family-panel">
    <header className="family-header">
      <div>
        <h2>{family.name}</h2>
        <p className="muted">{family.description || 'Sem descrição'} · {family.membersCount} membros · {family.role}</p>
      </div>
      <div className="room-actions">
        {isHead && <button className="ghost compact" onClick={editFamily}>Editar</button>}
        {isHead
          ? <button className="ghost compact danger" onClick={deleteFamily}>Excluir família</button>
          : <button className="ghost compact danger" onClick={leaveFamily}>Sair da família</button>}
      </div>
    </header>

    {error && <div className="error inline">{error}</div>}

    {isHead && <form className="family-invite" onSubmit={addMember}>
      <input placeholder="PublicId do familiar" minLength={8} maxLength={8} required
        value={publicId} onChange={e => setPublicId(e.target.value.toUpperCase())} />
      <button>Adicionar familiar</button>
    </form>}

    <div className="family-members">
      {family.members.map(member => <div className="family-member" key={member.userId}>
        <div>
          <strong>{member.username}</strong>
          <span>{member.publicId || 'PublicId indisponível'} · {member.role}</span>
        </div>
        {isHead && member.userId !== me && member.role !== 'Head' && <div className="room-actions">
          <button className="ghost compact" onClick={() => transferHead(member)}>Tornar Head</button>
          <button className="ghost compact danger" onClick={() => removeMember(member)}>Remover</button>
        </div>}
      </div>)}
    </div>
  </section>
}
