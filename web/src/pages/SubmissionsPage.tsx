import { Fragment, useCallback, useEffect, useState } from 'react'
import { api, localTime } from '../api'
import { navigate } from '../router'
import type { AppUser, Field, FormDetail, FormSummary, Status, Submission } from '../types'

const STATUSES: Status[] = ['New', 'In progress', 'Done']
const CHIP: Record<Status, string> = { New: 'chip-new', 'In progress': 'chip-prog', Done: 'chip-done' }

function show(v: unknown): string {
  if (v === true) return 'Yes'
  if (v === false || v === null || v === undefined) return ''
  return String(v)
}

/** Columns for the table: the form's own "listColumns" if it sets them, otherwise its first few short fields. */
function listColumns(form: FormDetail): Field[] {
  const fields = form.definition.fields
  const named = form.definition.listColumns?.map((k) => fields.find((f) => f.key === k)).filter((f): f is Field => !!f)
  if (named && named.length > 0) return named
  return fields.filter((f) => f.type !== 'textarea').slice(0, 4)
}

export default function SubmissionsPage({ slug }: { slug?: string }) {
  const [forms, setForms] = useState<FormSummary[]>([])
  const [form, setForm] = useState<FormDetail | null>(null)
  const [rows, setRows] = useState<Submission[] | null>(null)
  const [people, setPeople] = useState<AppUser[]>([])
  const [filter, setFilter] = useState<'All' | Status>('All')
  const [showAll, setShowAll] = useState(false)
  const [query, setQuery] = useState('')
  const [who, setWho] = useState('') // '' = anyone, '-' = unassigned, otherwise an account
  const [open, setOpen] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api<FormSummary[]>('/api/forms').then((all) => setForms(all.filter((f) => f.canManage))).catch((e) => setError(e.message))
    api<{ account: string; displayName?: string | null }[]>('/api/assignees').then((u) => setPeople(u.map((x, i) => ({ id: i, account: x.account, displayName: x.displayName, role: 'User' as const, isActive: true, createdUtc: '' })))).catch(() => undefined)
  }, [])

  const load = useCallback(() => {
    if (!slug) return Promise.resolve()
    const enc = encodeURIComponent(slug)
    return Promise.all([
      api<FormDetail>(`/api/forms/${enc}`),
      api<Submission[]>(`/api/forms/${enc}/submissions${showAll ? '?days=0' : ''}`),
    ])
      .then(([f, s]) => {
        setForm(f)
        setRows(s)
        setError(null)
      })
      .catch((e) => setError(e.message ?? String(e)))
  }, [slug, showAll])

  // Load now, then keep checking for new requests.
  useEffect(() => {
    setRows(null)
    setOpen(null)
    load()
    const timer = setInterval(load, 30_000)
    return () => clearInterval(timer)
  }, [load])

  const track = async (s: Submission, patch: { status?: Status; assignedTo?: string }) => {
    try {
      await api(`/api/submissions/${s.id}/tracking`, { method: 'PUT', json: patch })
      setRows((prev) =>
        prev?.map((r) =>
          r.id === s.id
            ? { ...r, status: patch.status ?? r.status, assignedTo: patch.assignedTo === undefined ? r.assignedTo : patch.assignedTo || null }
            : r,
        ) ?? null,
      )
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    }
  }

  const needle = query.trim().toLowerCase()
  const matches = (r: Submission) =>
    (who === '' || (who === '-' ? !r.assignedTo : r.assignedTo === who)) &&
    (needle === '' ||
      `${r.id} ${r.submittedBy ?? ''} ${Object.values(r.payload).map(show).join(' ')}`.toLowerCase().includes(needle))
  const inView = rows?.filter(matches) ?? []
  const count2 = (s: 'All' | Status) => (s === 'All' ? inView.length : inView.filter((r) => r.status === s).length)
  const shown = inView.filter((r) => filter === 'All' || r.status === filter)
  const cols = form ? listColumns(form) : []
  const label = (key: string) => {
    const own = form?.definition.fields.find((f) => f.key === key)?.label
    if (own) return own
    // The saved snapshot of a linked record is stored as "<field>_details".
    const base = key.endsWith('_details') ? form?.definition.fields.find((f) => f.key === key.slice(0, -8)) : undefined
    return base ? `${base.label} (details when submitted)` : key
  }

  return (
    <div className="container wide">
      <div className="page-head">
        <div>
          <h1>Submissions</h1>
          <p className="muted">Forms you own (admins see every form). Click a status to move a request along.</p>
        </div>
        {slug && (
          <a className="btn" href={`/api/forms/${encodeURIComponent(slug)}/submissions.csv${showAll ? '?days=0' : ''}`}>
            Export CSV
          </a>
        )}
      </div>

      <div className="field" style={{ maxWidth: 420, marginBottom: 0 }}>
        <label htmlFor="pick-form">Form</label>
        <select id="pick-form" value={slug ?? ''} onChange={(e) => navigate(e.target.value ? `/submissions/${e.target.value}` : '/submissions')}>
          <option value="">Choose a form…</option>
          {forms.map((f) => (
            <option key={f.slug} value={f.slug}>
              {f.name}
              {f.newSubmissions ? ` (${f.newSubmissions} new)` : ''}
            </option>
          ))}
        </select>
      </div>

      {error && <div className="alert alert-error" role="alert">{error}</div>}
      {slug && !rows && !error && <p className="muted">Loading…</p>}

      {rows && form && (
        <>
          <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', margin: '16px 0 0' }}>
            <div className="field" style={{ flex: '1 1 260px', maxWidth: 420, marginBottom: 0 }}>
              <label htmlFor="sub-search">Search</label>
              <input id="sub-search" type="search" placeholder="Any word in a request, or who sent it" value={query} onChange={(e) => setQuery(e.target.value)} />
            </div>
            <div className="field" style={{ marginBottom: 0 }}>
              <label htmlFor="sub-who">Assigned to</label>
              <select id="sub-who" value={who} onChange={(e) => setWho(e.target.value)}>
                <option value="">Anyone</option>
                <option value="-">Unassigned</option>
                {people.map((p) => (
                  <option key={p.id} value={p.account}>{p.displayName && p.displayName !== p.account ? p.displayName : p.account}</option>
                ))}
              </select>
            </div>
          </div>

          <div className="filters" role="group" aria-label="Filter by status">
            {(['All', ...STATUSES] as const).map((s) => (
              <button key={s} type="button" className="filter" aria-pressed={filter === s} onClick={() => setFilter(s)}>
                {s} {count2(s)}
              </button>
            ))}
          </div>

          <div className="card table-wrap">
            <table>
              <thead>
                <tr>
                  <th scope="col">#</th>
                  <th scope="col">Received</th>
                  {cols.map((c) => (
                    <th key={c.key} scope="col">{c.label}</th>
                  ))}
                  <th scope="col">Assigned to</th>
                  <th scope="col">Status</th>
                  <th scope="col"><span className="sr-only">Details</span></th>
                </tr>
              </thead>
              <tbody>
                {shown.map((s) => (
                  <Fragment key={s.id}>
                    <tr>
                      <td className="muted">{s.id}</td>
                      <td className="nowrap muted">{localTime(s.submittedUtc)}</td>
                      {cols.map((c, i) => (
                        <td key={c.key} className={i === 0 ? 'strong' : 'muted'}>{show(s.payload[c.key])}</td>
                      ))}
                      <td>
                        <select
                          aria-label={`Assign request ${s.id}`}
                          value={s.assignedTo ?? ''}
                          onChange={(e) => track(s, { assignedTo: e.target.value })}
                          style={{ minWidth: 150 }}
                        >
                          <option value="">Unassigned</option>
                          {people.map((p) => (
                            <option key={p.id} value={p.account}>{p.displayName && p.displayName !== p.account ? p.displayName : p.account}</option>
                          ))}
                          {s.assignedTo && !people.some((p) => p.account === s.assignedTo) && <option value={s.assignedTo}>{s.assignedTo}</option>}
                        </select>
                      </td>
                      <td>
                        <button
                          type="button"
                          className={`chip ${CHIP[s.status]}`}
                          onClick={() => track(s, { status: STATUSES[(STATUSES.indexOf(s.status) + 1) % STATUSES.length] })}
                          aria-label={`Request ${s.id} is ${s.status}. Mark as ${STATUSES[(STATUSES.indexOf(s.status) + 1) % STATUSES.length]}`}
                        >
                          {s.status}
                        </button>
                      </td>
                      <td>
                        <button type="button" className="btn btn-quiet" aria-expanded={open === s.id} onClick={() => setOpen(open === s.id ? null : s.id)}>
                          {open === s.id ? 'Hide' : 'Details'}
                        </button>
                      </td>
                    </tr>
                    {open === s.id && (
                      <tr className="detail">
                        <td colSpan={cols.length + 5}>
                          <dl className="details">
                            {Object.entries(s.payload).map(([k, v]) => (
                              <div key={k}>
                                <dt>{label(k)}</dt>
                                <dd>{show(v) || '—'}</dd>
                              </div>
                            ))}
                            <div>
                              <dt>Submitted by</dt>
                              <dd>{s.submittedBy ?? 'Public web form'}</dd>
                            </div>
                            <div>
                              <dt>Form version</dt>
                              <dd>v{s.formVersion}</dd>
                            </div>
                            {s.statusUpdatedUtc && (
                              <div>
                                <dt>Last status change</dt>
                                <dd>{localTime(s.statusUpdatedUtc)} by {s.statusUpdatedBy}</dd>
                              </div>
                            )}
                          </dl>
                        </td>
                      </tr>
                    )}
                  </Fragment>
                ))}
              </tbody>
            </table>
            {shown.length === 0 && <p className="muted" style={{ padding: 28, textAlign: 'center' }}>{needle || who ? 'No requests match.' : 'Nothing here.'}</p>}
          </div>

          <p className="small muted">
            {showAll ? 'Showing everything.' : 'Showing the last 90 days.'}{' '}
            <button type="button" className="btn btn-quiet" onClick={() => setShowAll(!showAll)}>
              {showAll ? 'Show last 90 days' : 'Show all'}
            </button>
          </p>
        </>
      )}
    </div>
  )
}
