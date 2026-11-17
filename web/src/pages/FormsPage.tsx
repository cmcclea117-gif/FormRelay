import { useEffect, useState } from 'react'
import { api, localTime } from '../api'
import { Link } from '../router'
import type { FormSummary } from '../types'

export default function FormsPage() {
  const [forms, setForms] = useState<FormSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [copied, setCopied] = useState<string | null>(null)

  useEffect(() => {
    api<FormSummary[]>('/api/forms')
      .then(setForms)
      .catch((e) => setError(String(e.message ?? e)))
  }, [])

  const copyLink = async (slug: string) => {
    try {
      await navigator.clipboard.writeText(`${location.origin}/f/${slug}`)
      setCopied(slug)
      setTimeout(() => setCopied(null), 2000)
    } catch {
      /* clipboard blocked: the link is also shown on the Open page's address bar */
    }
  }

  const anyManage = forms?.some((f) => f.canManage) ?? false

  return (
    <div className="container">
      <div className="page-head">
        <div>
          <h1>Forms</h1>
          <p className="muted">Open a form to fill it out. Forms you own also show what has come in.</p>
        </div>
        <Link to="/builder" className="btn btn-primary">
          New form
        </Link>
      </div>

      {error && <div className="alert alert-error" role="alert">{error}</div>}
      {!forms && !error && <p className="muted">Loading…</p>}
      {forms?.length === 0 && <p className="muted">No forms yet.</p>}

      {forms && forms.length > 0 && (
        <div className="card table-wrap">
          <table>
            <thead>
              <tr>
                <th scope="col">Form</th>
                <th scope="col">Audience</th>
                <th scope="col">Version</th>
                <th scope="col">Owner</th>
                {anyManage && <th scope="col">New</th>}
                {anyManage && <th scope="col">Total</th>}
                {anyManage && <th scope="col">Last submission</th>}
                <th scope="col" style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {forms.map((f) => (
                <tr key={f.slug}>
                  <td>
                    <div className="strong" style={{ fontWeight: 600 }}>{f.name}</div>
                    <div className="small muted">/f/{f.slug}</div>
                  </td>
                  <td>
                    <span className={`pill pill-${f.audience}`}>{f.audience === 'public' ? 'Public' : 'Internal'}</span>
                  </td>
                  <td className="muted">v{f.latestVersion ?? 0}</td>
                  <td className="muted">{f.ownerName || f.ownerAccount || '—'}</td>
                  {anyManage && (
                    <td>
                      {f.canManage && <span className={`pill pill-count${(f.newSubmissions ?? 0) > 0 ? ' hot' : ''}`}>{f.newSubmissions ?? 0}</span>}
                    </td>
                  )}
                  {anyManage && <td className="muted">{f.canManage ? f.totalSubmissions ?? 0 : ''}</td>}
                  {anyManage && <td className="muted">{f.canManage ? localTime(f.lastSubmittedUtc) || '—' : ''}</td>}
                  <td className="nowrap" style={{ textAlign: 'right' }}>
                    <Link to={`/f/${f.slug}`} className="btn btn-quiet">Open</Link>
                    {f.canManage && <Link to={`/builder/${f.slug}`} className="btn btn-quiet">Edit</Link>}
                    {f.audience === 'public' && (
                      <button type="button" className="btn btn-quiet" onClick={() => copyLink(f.slug)}>
                        {copied === f.slug ? 'Copied' : 'Copy link'}
                      </button>
                    )}
                    {f.canManage && (
                      <Link to={`/submissions/${f.slug}`} className="btn btn-quiet">Submissions</Link>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
