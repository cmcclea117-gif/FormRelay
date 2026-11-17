import { useEffect, useState, type ReactNode } from 'react'
import { Link } from '../router'
import type { Me } from '../types'

type Theme = 'light' | 'dark'

function systemTheme(): Theme {
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
}

function savedTheme(): Theme | null {
  try {
    const t = localStorage.getItem('fr-theme')
    return t === 'light' || t === 'dark' ? t : null
  } catch {
    return null
  }
}

/** Light/dark switch. Remembers the choice on this device; with no choice it follows the operating system. */
function ThemeButton() {
  const [theme, setTheme] = useState<Theme>(() => savedTheme() ?? systemTheme())

  useEffect(() => {
    document.documentElement.dataset.theme = theme
  }, [theme])

  const flip = () => {
    const next: Theme = theme === 'dark' ? 'light' : 'dark'
    setTheme(next)
    try {
      localStorage.setItem('fr-theme', next)
    } catch {
      /* private window or blocked storage: the choice just won't be remembered */
    }
  }

  return (
    <button type="button" className="btn" onClick={flip} aria-pressed={theme === 'dark'}>
      {theme === 'dark' ? 'Switch to light' : 'Switch to dark'}
    </button>
  )
}

/** Header-only shell for pages that don't need the app menu (public forms, sign-in problems). */
export function PublicShell({ children, backTo }: { children: ReactNode; backTo?: { to: string; label: string } }) {
  return (
    <>
      <header className="header">
        <span className="brand">
          <span className="logo">RF</span>FormRelay
        </span>
        {backTo && (
          <Link to={backTo.to} className="btn btn-quiet">
            {backTo.label}
          </Link>
        )}
        <span className="spacer" />
        <ThemeButton />
      </header>
      <main>{children}</main>
    </>
  )
}

export function Shell({ me, path, children }: { me: Me; path: string; children: ReactNode }) {
  const isAdmin = me.role === 'Admin'
  const item = (to: string, label: string, active: boolean) => (
    <Link to={to} aria-current={active ? 'page' : undefined}>
      {label}
    </Link>
  )

  return (
    <>
      <header className="header">
        <Link to="/" className="brand">
          <span className="logo">RF</span>FormRelay
        </Link>
        <nav className="nav" aria-label="Main">
          {item('/', 'Forms', path === '/')}
          {item('/submissions', 'Submissions', path.startsWith('/submissions'))}
          {isAdmin && item('/users', 'Users', path.startsWith('/users'))}
          {item('/builder', 'Builder', path.startsWith('/builder'))}
          {item('/lists', 'Lists', path.startsWith('/lists'))}
        </nav>
        <span className="spacer" />
        <ThemeButton />
        <span className="whoami">
          {me.displayName ?? me.account} · {me.role}
        </span>
      </header>
      <main>{children}</main>
    </>
  )
}
