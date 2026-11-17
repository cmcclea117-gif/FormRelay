import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { Link, navigate } from '../router'

type ListSummary = {
  name: string
  source: string
  isPublicSafe: boolean
  itemCount: number
  usedBy: { slug: string; name: string }[]
}
type ServerItem = { value: string; label: string }

// One choice being edited. `original` is how a choice that already existed was stored, so untouched choices
// keep their stored value, and edited or new ones take their value from the text.
type Row = { id: number; label: string; original?: ServerItem }
let nextId = 1

const NEW = '__new__'
const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60)
const errorText = (e: unknown, fallback: string) => (e instanceof ApiError || e instanceof Error ? e.message : fallback)

export default function ListsPage({ name, canEdit }: { name?: string; canEdit: boolean }) {
  const [lists, setLists] = useState<ListSummary[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => api<ListSummary[]>('/api/lookups').then(setLists).catch((e) => setError(errorText(e, 'Could not load lists'))), [])
  useEffect(() => {
    load()
  }, [load])

  const current = name === NEW ? null : lists?.find((l) => l.name === name)

  return (
    <div className="container wide">
      <div className="page-head">
        <div>
          <h1>Lists</h1>
          <p className="muted">The choices behind dropdown fields. {canEdit ? 'Changes apply to forms right away; past submissions keep what was chosen.' : 'Only administrators can change them.'}</p>
        </div>
        {canEdit && (
          <button type="button" className="btn btn-primary" onClick={() => navigate(`/lists/${NEW}`)}>
            New list
          </button>
        )}
      </div>
      {error && <div className="alert alert-error" role="alert">{error}</div>}
      {!lists && !error && <p className="muted">Loading…</p>}

      {lists && (
        <div className="lists-grid">
          <nav className="card card-pad" aria-label="Lists">
            <ul className="b-list">
              {lists.map((l) => (
                <li key={l.name}>
                  <Link to={`/lists/${l.name}`} className={`list-link${l.name === name ? ' selected' : ''}`} aria-current={l.name === name ? 'page' : undefined}>
                    <span className="strong">{l.name}</span>
                    <span className="small muted">
                      {l.itemCount} {l.itemCount === 1 ? 'choice' : 'choices'}
                      {l.isPublicSafe ? ' · public OK' : ''}
                      {l.source !== 'manual' ? ' · from a connector' : ''}
                    </span>
                  </Link>
                </li>
              ))}
              {lists.length === 0 && <li className="muted">No lists yet.</li>}
            </ul>
          </nav>

          {name === NEW && canEdit ? (
            <ListEditor key={NEW} list={null} canEdit onSaved={(n) => load().then(() => navigate(`/lists/${n}`))} onDeleted={() => undefined} />
          ) : current ? (
            <ListEditor key={current.name} list={current} canEdit={canEdit} onSaved={() => load()} onDeleted={() => load().then(() => navigate('/lists'))} />
          ) : (
            <div className="card card-pad muted">{name ? 'That list was not found.' : (canEdit ? 'Choose a list to edit its choices, or start a new one.' : 'Choose a list to see its choices.')}</div>
          )}
        </div>
      )}
    </div>
  )
}

function ListEditor({ list, canEdit, onSaved, onDeleted }: { list: ListSummary | null; canEdit: boolean; onSaved: (name: string) => void; onDeleted: () => void }) {
  const isNew = list === null
  const readOnly = !isNew && (!canEdit || list.source !== 'manual')
  const [name, setName] = useState(list?.name ?? '')
  const [nameTouched, setNameTouched] = useState(false)
  const [rows, setRows] = useState<Row[]>([])
  const [loaded, setLoaded] = useState(isNew)
  const [publicSafe, setPublicSafe] = useState(list?.isPublicSafe ?? false)
  const [bulk, setBulk] = useState('')
  const [dragId, setDragId] = useState<number | null>(null)
  const [overId, setOverId] = useState<number | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [saved, setSaved] = useState('')

  const snap = (r: Row[], pub: boolean) => JSON.stringify({ r: r.map((x) => [x.label.trim(), x.original?.value ?? null]), pub })

  const loadItems = useCallback(() => {
    if (!list) return Promise.resolve()
    return api<ServerItem[]>(`/api/lookups/${encodeURIComponent(list.name)}/items`).then((items) => {
      const r: Row[] = items.map((i) => ({ id: nextId++, label: i.label, original: i }))
      setRows(r)
      setSaved(snap(r, list.isPublicSafe))
      setLoaded(true)
    })
  }, [list])

  useEffect(() => {
    loadItems().catch((e) => setError(errorText(e, 'Could not load the choices')))
  }, [loadItems])

  const address = nameTouched ? name : slugify(name)
  const dirty = isNew ? rows.length > 0 || address !== '' : snap(rows, publicSafe) !== saved

  const addLines = () => {
    const have = new Set(rows.map((r) => r.label.trim().toLowerCase()))
    const added: Row[] = []
    for (const line of bulk.split(/\r?\n/)) {
      const text = line.trim()
      if (text && !have.has(text.toLowerCase())) {
        have.add(text.toLowerCase())
        added.push({ id: nextId++, label: text.slice(0, 200) })
      }
    }
    setRows((prev) => [...prev, ...added])
    setBulk('')
  }

  const move = (id: number, by: -1 | 1) =>
    setRows((prev) => {
      const at = prev.findIndex((r) => r.id === id)
      const to = at + by
      if (at < 0 || to < 0 || to >= prev.length) return prev
      const copy = [...prev]
      ;[copy[at], copy[to]] = [copy[to], copy[at]]
      return copy
    })

  const dropOn = (targetId: number) => {
    const from = dragId
    setDragId(null)
    setOverId(null)
    if (from === null || from === targetId) return
    setRows((prev) => {
      const at = prev.findIndex((r) => r.id === from)
      const to = prev.findIndex((r) => r.id === targetId)
      if (at < 0 || to < 0) return prev
      const copy = [...prev]
      const [m] = copy.splice(at, 1)
      copy.splice(to, 0, m)
      return copy
    })
  }

  const save = async () => {
    setError(null)
    setMessage(null)
    const target = isNew ? address : list.name
    const texts = rows.map((r) => r.label.trim())
    if (texts.some((t) => !t)) return setError('Every choice needs some text. Remove the empty ones.')
    if (new Set(texts.map((t) => t.toLowerCase())).size !== texts.length) return setError('Two choices are the same. Each one must be different.')
    setBusy(true)
    try {
      const items = rows.map((r) => {
        const label = r.label.trim()
        const value = r.original && r.original.label === label ? r.original.value : label
        return { value, label }
      })
      await api(`/api/lookups/${encodeURIComponent(target)}`, { method: 'PUT', json: { isPublicSafe: publicSafe, items } })
      setMessage('Saved. Forms using this list show the new choices right away.')
      if (isNew) onSaved(target)
      else {
        await loadItems()
        onSaved(target)
      }
    } catch (e) {
      setError(errorText(e, 'Could not save.'))
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    setBusy(true)
    setError(null)
    try {
      await api(`/api/lookups/${encodeURIComponent(list!.name)}`, { method: 'DELETE' })
      onDeleted()
    } catch (e) {
      setError(errorText(e, 'Could not delete.'))
      setConfirmDelete(false)
    } finally {
      setBusy(false)
    }
  }

  const used = list?.usedBy ?? []

  return (
    <section className="card card-pad" aria-labelledby="le-title">
      <h2 id="le-title">{isNew ? 'New list' : list.name}</h2>
      {error && <div className="alert alert-error" role="alert" style={{ marginBottom: 16 }}>{error}</div>}
      {message && !dirty && <div className="alert alert-ok" role="status" style={{ marginBottom: 16 }}>{message}</div>}

      {isNew && (
        <div className="field">
          <label htmlFor="ln-name">List name</label>
          <input
            id="ln-name"
            type="text"
            maxLength={100}
            value={address}
            aria-describedby="ln-name-help"
            onChange={(e) => {
              setNameTouched(true)
              setName(e.target.value.toLowerCase())
            }}
          />
          <div className="help" id="ln-name-help">Lowercase letters, numbers and dashes, like <code>yard-names</code>. It can't be changed later.</div>
        </div>
      )}

      {!isNew && (
        <p className="small muted" style={{ marginBottom: 16 }}>
          {used.length > 0 ? (
            <>
              Used by:{' '}
              {used.map((f, i) => (
                <span key={f.slug}>
                  {i > 0 && ', '}
                  <Link to={`/builder/${f.slug}`}>{f.name}</Link>
                </span>
              ))}
              .
            </>
          ) : (
            'Not used by any form yet.'
          )}
        </p>
      )}

      {readOnly && <div className="alert alert-ok" role="note" style={{ marginBottom: 16 }}>{list?.source !== 'manual' ? "This list is filled by a connector, so it can't be edited by hand." : 'You can look at this list. Only administrators can change it.'}</div>}

      {!loaded ? (
        <p className="muted">Loading…</p>
      ) : (
        <>
          <h3 className="label" style={{ margin: '0 0 8px' }}>Choices, in the order people see them</h3>
          {rows.length === 0 && <p className="muted">No choices yet. Add some below.</p>}
          <ol className="b-list">
            {rows.map((r, idx) => (
              <li
                key={r.id}
                className={`b-field${r.id === dragId ? ' dragging' : ''}${overId === r.id && dragId !== r.id ? ' over' : ''}`}
                draggable={!readOnly}
                onDragStart={(e) => {
                  if (readOnly) return
                  setDragId(r.id)
                  e.dataTransfer.effectAllowed = 'move'
                  e.dataTransfer.setData('text/plain', String(r.id))
                }}
                onDragOver={(e) => {
                  if (dragId === null) return
                  e.preventDefault()
                  if (overId !== r.id) setOverId(r.id)
                }}
                onDrop={(e) => {
                  e.preventDefault()
                  dropOn(r.id)
                }}
                onDragEnd={() => {
                  setDragId(null)
                  setOverId(null)
                }}
              >
                <span className="b-grip" aria-hidden="true" title="Drag to reorder">⋮⋮</span>
                <input
                  type="text"
                  className="choice-input"
                  aria-label={`Choice ${idx + 1}`}
                  maxLength={200}
                  readOnly={readOnly}
                  value={r.label}
                  onChange={(e) => setRows((prev) => prev.map((x) => (x.id === r.id ? { ...x, label: e.target.value } : x)))}
                />
                {!readOnly && (
                  <div className="b-actions">
                    <button type="button" className="btn btn-quiet" aria-label={`Move ${r.label} up`} disabled={idx === 0} onClick={() => move(r.id, -1)}>↑</button>
                    <button type="button" className="btn btn-quiet" aria-label={`Move ${r.label} down`} disabled={idx === rows.length - 1} onClick={() => move(r.id, 1)}>↓</button>
                    <button type="button" className="btn btn-quiet" aria-label={`Remove ${r.label}`} onClick={() => setRows((prev) => prev.filter((x) => x.id !== r.id))}>✕</button>
                  </div>
                )}
              </li>
            ))}
          </ol>

          {!readOnly && (
            <div className="field" style={{ marginTop: 16 }}>
              <label htmlFor="ln-bulk">Add choices (one per line)</label>
              <textarea id="ln-bulk" value={bulk} onChange={(e) => setBulk(e.target.value)} placeholder={'Yard A\nYard B'} />
              <div>
                <button type="button" className="btn" disabled={!bulk.trim()} onClick={addLines}>Add to list</button>
              </div>
            </div>
          )}

          {!readOnly && (
            <div className="field">
              <label className="check">
                <input type="checkbox" checked={publicSafe} onChange={(e) => setPublicSafe(e.target.checked)} aria-describedby="ln-pub-help" />
                Approved for public forms
              </label>
              <div className="help" id="ln-pub-help">
                When on, public forms can use this list, and anyone who opens such a form will see these choices. Keep it off for anything internal.
              </div>
            </div>
          )}

          {!readOnly && (
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'center' }}>
              <button type="button" className="btn btn-primary" onClick={save} disabled={busy || !dirty || (isNew && (!address || rows.length === 0))}>
                {busy ? 'Saving…' : isNew ? 'Create list' : dirty ? 'Save changes' : 'Saved'}
              </button>
              {!isNew && !confirmDelete && (
                <button type="button" className="btn btn-danger" disabled={used.length > 0} onClick={() => setConfirmDelete(true)}>
                  Delete list
                </button>
              )}
              {!isNew && used.length > 0 && <span className="small muted">Can't delete while a form uses it.</span>}
              {confirmDelete && (
                <>
                  <span className="small">Delete "{list!.name}" for good?</span>
                  <button type="button" className="btn btn-danger" onClick={remove} disabled={busy}>Yes, delete</button>
                  <button type="button" className="btn" onClick={() => setConfirmDelete(false)}>Keep it</button>
                </>
              )}
            </div>
          )}
        </>
      )}
    </section>
  )
}
