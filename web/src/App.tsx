import { useEffect, useState, type ReactNode } from 'react'
import { api, ApiError } from './api'
import { PublicShell, Shell } from './components/Shell'
import BuilderPage from './pages/BuilderPage'
import FillPage from './pages/FillPage'
import FormsPage from './pages/FormsPage'
import ListsPage from './pages/ListsPage'
import SubmissionsPage from './pages/SubmissionsPage'
import UsersPage from './pages/UsersPage'
import { usePath } from './router'
import type { Me } from './types'

export default function App() {
  const path = usePath()

  // Form pages are reachable without the app menu (and, for public forms, without signing in).
  const fill = path.match(/^\/f\/([^/]+)\/?$/)
  if (fill) return <FillPage slug={decodeURIComponent(fill[1])} />

  return <SignedIn path={path} />
}

function Notice({ children }: { children: ReactNode }) {
  return (
    <PublicShell>
      <div className="container narrow">{children}</div>
    </PublicShell>
  )
}

function SignedIn({ path }: { path: string }) {
  const [me, setMe] = useState<Me | null>(null)
  const [error, setError] = useState<string | null>(null)

  // The browser signs in with Windows automatically; /api/me says who the app thinks you are.
  useEffect(() => {
    api<Me>('/api/me')
      .then(setMe)
      .catch((e) => setError(e instanceof ApiError && e.status === 401 ? 'Windows sign-in did not complete.' : 'Could not reach the server.'))
  }, [])

  if (error) return <Notice><div className="alert alert-error" role="alert">{error}</div></Notice>
  if (!me) return <Notice><p className="muted">Signing in…</p></Notice>

  if (!me.hasAccess) {
    return (
      <Notice>
        <div className="card card-pad">
          <h1>No access yet</h1>
          <p style={{ marginTop: 8 }}>
            You are signed in as <code>{me.account}</code>, but you have not been given access to FormRelay. Ask an
            administrator to add your account.
          </p>
        </div>
      </Notice>
    )
  }

  const isAdmin = me.role === 'Admin'
  const adminOnly = (page: ReactNode) =>
    isAdmin ? page : <div className="container narrow"><div className="alert alert-error" role="alert">That page is for administrators.</div></div>

  let page: ReactNode
  const sub = path.match(/^\/submissions(?:\/([^/]+))?\/?$/)
  if (path === '/') page = <FormsPage />
  else if (sub) page = <SubmissionsPage slug={sub[1] ? decodeURIComponent(sub[1]) : undefined} />
  else if (path.startsWith('/users')) page = adminOnly(<UsersPage />)
  else if (path.startsWith('/lists')) {
    const l = path.match(/^\/lists(?:\/([^/]+))?\/?$/)
    page = l ? <ListsPage name={l[1] ? decodeURIComponent(l[1]) : undefined} canEdit={isAdmin} /> : <div className="container narrow"><h1>Page not found</h1></div>
  }
  else if (path.startsWith('/builder')) {
    const b = path.match(/^\/builder(?:\/([^/]+))?\/?$/)
    page = b
      ? <BuilderPage slug={b[1] ? decodeURIComponent(b[1]) : undefined} isAdmin={isAdmin} />
      : <div className="container narrow"><h1>Page not found</h1></div>
  }
  else page = <div className="container narrow"><h1>Page not found</h1></div>

  return <Shell me={me} path={path}>{page}</Shell>
}
