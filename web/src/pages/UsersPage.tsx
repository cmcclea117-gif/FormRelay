import { useEffect, useState, type FormEvent } from 'react'
import { api, localTime } from '../api'
import type { AppUser } from '../types'

export default function UsersPage() {
  const [users, setUsers] = useState<AppUser[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [account, setAccount] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [role, setRole] = useState<'User' | 'Admin'>('User')

  const load = () =>
    api<AppUser[]>('/api/users')
      .then(setUsers)
      .catch((e) => setError(e.message ?? String(e)))

  useEffect(() => {
    load()
  }, [])

  const change = async (u: AppUser, patch: { role?: 'Admin' | 'User'; isActive?: boolean }) => {
    setError(null)
    try {
      await api(`/api/users/${u.id}`, { method: 'PUT', json: patch })
      await load()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const add = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    try {
      await api('/api/users', { method: 'POST', json: { account, displayName, role } })
      setAccount('')
      setDisplayName('')
      setRole('User')
      await load()
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err))
    }
  }

  return (
    <div className="container">
      <div>
        <h1>Users and access</h1>
        <p className="muted">
          Everyone signs in with their Windows account. Here you decide who can use the app and what they can do.
        </p>
      </div>

      {error && <div className="alert alert-error" role="alert">{error}</div>}

      <form className="card card-pad row" onSubmit={add}>
        <div className="field">
          <label htmlFor="u-account">Windows account</label>
          <input id="u-account" type="text" placeholder="RMT2K\jsmith" value={account} onChange={(e) => setAccount(e.target.value)} required />
        </div>
        <div className="field">
          <label htmlFor="u-name">Name (optional)</label>
          <input id="u-name" type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
        </div>
        <div className="field" style={{ flex: '0 1 160px' }}>
          <label htmlFor="u-role">Role</label>
          <select id="u-role" value={role} onChange={(e) => setRole(e.target.value as 'User' | 'Admin')}>
            <option value="User">User</option>
            <option value="Admin">Admin</option>
          </select>
        </div>
        <button type="submit" className="btn btn-primary">Add user</button>
      </form>

      {!users && !error && <p className="muted">Loading…</p>}
      {users && (
        <div className="card table-wrap">
          <table>
            <thead>
              <tr>
                <th scope="col">Account</th>
                <th scope="col">Name</th>
                <th scope="col">Last sign-in</th>
                <th scope="col">Role</th>
                <th scope="col">Access</th>
              </tr>
            </thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.id}>
                  <td className="strong">{u.account}</td>
                  <td className="muted">{u.displayName && u.displayName !== u.account ? u.displayName : ''}</td>
                  <td className="muted">{localTime(u.lastSignInUtc) || '—'}</td>
                  <td>
                    <button
                      type="button"
                      className={`chip ${u.role === 'Admin' ? 'chip-prog' : ''}`}
                      style={u.role === 'Admin' ? undefined : { background: 'var(--surface)', color: 'var(--ink)', borderColor: 'var(--field-border)' }}
                      onClick={() => change(u, { role: u.role === 'Admin' ? 'User' : 'Admin' })}
                      aria-label={`${u.account} is ${u.role}. Change role`}
                    >
                      {u.role}
                    </button>
                  </td>
                  <td>
                    <button
                      type="button"
                      className={`chip ${u.isActive ? 'chip-done' : ''}`}
                      style={u.isActive ? undefined : { background: 'var(--surface2)', color: 'var(--muted)', borderColor: 'var(--border)' }}
                      onClick={() => change(u, { isActive: !u.isActive })}
                      aria-pressed={u.isActive}
                      aria-label={`${u.account} is ${u.isActive ? 'active' : 'disabled'}. Toggle`}
                    >
                      {u.isActive ? 'Active' : 'Disabled'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="small muted">
        Admin: everything, including this page and all submissions. User: fill out forms, and create or edit them. A
        person who signs in but isn't listed here sees a "no access" page.
      </p>
    </div>
  )
}
