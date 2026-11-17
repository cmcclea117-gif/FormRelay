import { useCallback, useEffect, useMemo, useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'
import { emptyValues, FieldInput, visibleKeys, type Value } from '../components/FieldInput'
import { Link, navigate } from '../router'
import type { Audience, Field, FieldType, FormDetail, Option } from '../types'

// ---------------------------------------------------------------- helpers

type LookupRow = { name: string; isPublicSafe: boolean }
type LookupItem = { value: string; label: string }

type Item = {
  id: number // stays the same while the label (and so the key) changes
  f: Field
  isNew: boolean // keys of saved fields are locked: submissions are stored under them
  inTable: boolean // shown as a column in the admin submissions table
}

let nextId = 1

const TYPES: { type: FieldType; name: string }[] = [
  { type: 'text', name: 'Short text' },
  { type: 'textarea', name: 'Long text' },
  { type: 'email', name: 'Email address' },
  { type: 'number', name: 'Number' },
  { type: 'date', name: 'Date' },
  { type: 'dropdown', name: 'Dropdown list' },
  { type: 'checkbox', name: 'Yes / no box' },
]
const typeName = (t: FieldType) => TYPES.find((x) => x.type === t)?.name ?? t

function makeKey(label: string, taken: Set<string>): string {
  let base = label.toLowerCase().replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '').slice(0, 40)
  if (!base || /^[0-9]/.test(base)) base = `field_${base}`.replace(/_$/, '')
  if (base === 'website' || base.endsWith('_details')) base += '_field'
  let key = base
  for (let n = 2; taken.has(key); n++) key = `${base}_${n}`
  return key
}

/** Only what belongs to the field's type, and no empty values. */
function cleanField(f: Field): Field {
  const out: Field = { key: f.key, type: f.type, label: f.label.trim() }
  if (f.required) out.required = true
  if (f.help?.trim()) out.help = f.help.trim()
  if (f.type === 'text' || f.type === 'email') if (f.placeholder?.trim()) out.placeholder = f.placeholder.trim()
  if ((f.type === 'text' || f.type === 'textarea') && f.maxLength) out.maxLength = f.maxLength
  if (f.type === 'dropdown' && f.lookup) out.lookup = f.lookup
  if (f.type === 'number') {
    if (typeof f.min === 'number') out.min = f.min
    if (typeof f.max === 'number') out.max = f.max
  }
  if (f.showIf?.field && f.showIf.equals !== '') out.showIf = { field: f.showIf.field, equals: f.showIf.equals }
  return out
}

function errorText(e: unknown, fallback: string): string {
  return e instanceof ApiError || e instanceof Error ? e.message : fallback
}

// ---------------------------------------------------------------- page

// Administrators only (the app and the API both enforce it).
export default function BuilderPage({ slug, isAdmin }: { slug?: string; isAdmin: boolean }) {
  return slug ? <Editor slug={slug} isAdmin={isAdmin} /> : <NewForm isAdmin={isAdmin} />
}

function NewForm({ isAdmin }: { isAdmin: boolean }) {
  const [name, setName] = useState('')
  const [slug, setSlug] = useState('')
  const [slugTouched, setSlugTouched] = useState(false)
  const [description, setDescription] = useState('')
  const [audience, setAudience] = useState<Audience>('internal')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const suggested = name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 60)
  const address = slugTouched ? slug : suggested

  const create = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setBusy(true)
    try {
      await api('/api/forms', { method: 'POST', json: { slug: address, name: name.trim(), description: description.trim() || null, audience } })
      // A form needs at least one field to exist, so start with a title field.
      await api(`/api/forms/${encodeURIComponent(address)}/definition`, {
        method: 'PUT',
        json: { fields: [{ key: 'title', type: 'text', label: 'Title', required: true }] },
      })
      navigate(`/builder/${address}`)
    } catch (err) {
      setError(errorText(err, 'Could not create the form'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="container narrow">
      <div>
        <h1>New form</h1>
        <p className="muted">Name it, choose who can fill it out, then add fields on the next screen.</p>
      </div>
      <form className="card card-pad" onSubmit={create}>
        {error && <div className="alert alert-error" role="alert" style={{ marginBottom: 16 }}>{error}</div>}
        <div className="field">
          <label htmlFor="nf-name">Form name</label>
          <input id="nf-name" type="text" maxLength={200} required value={name} onChange={(e) => setName(e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="nf-slug">Web address</label>
          <input
            id="nf-slug"
            type="text"
            maxLength={100}
            required
            aria-describedby="nf-slug-help"
            value={address}
            onChange={(e) => {
              setSlugTouched(true)
              setSlug(e.target.value.toLowerCase())
            }}
          />
          <div className="help" id="nf-slug-help">/f/{address || 'your-form'}. Lowercase letters, numbers and dashes. It can't be changed later.</div>
        </div>
        <div className="field">
          <label htmlFor="nf-desc">Description (optional)</label>
          <input id="nf-desc" type="text" maxLength={1000} value={description} onChange={(e) => setDescription(e.target.value)} />
        </div>
        <fieldset className="field" style={{ border: 0, padding: 0 }}>
          <legend className="label">Who fills it out?</legend>
          <label className="check">
            <input type="radio" name="aud" checked={audience === 'internal'} onChange={() => setAudience('internal')} />
            Internal: signed-in staff only.
          </label>
          <label className="check">
            <input type="radio" name="aud" checked={audience === 'public'} disabled={!isAdmin} onChange={() => setAudience('public')} />
            Public: anyone with the link, no sign-in.{!isAdmin && ' (Administrators only.)'}
          </label>
          <div className="help">{isAdmin ? 'You can change this later from the form’s page.' : 'You will own this form. An administrator can make it public later.'}</div>
        </fieldset>
        <div style={{ display: 'flex', gap: 12 }}>
          <button type="submit" className="btn btn-primary" disabled={busy || !name.trim() || !address}>
            {busy ? 'Creating…' : 'Create and add fields'}
          </button>
          <Link to="/" className="btn">Cancel</Link>
        </div>
      </form>
    </div>
  )
}

function Editor({ slug, isAdmin }: { slug: string; isAdmin: boolean }) {
  const enc = encodeURIComponent(slug)
  const [form, setForm] = useState<(FormDetail & { notifyTo?: string | null; notifySubject?: string | null }) | null>(null)
  const [items, setItems] = useState<Item[]>([])
  const [notifyTo, setNotifyTo] = useState('')
  const [notifySubject, setNotifySubject] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  const [dragId, setDragId] = useState<number | null>(null)
  const [overId, setOverId] = useState<number | null>(null)
  const [tab, setTab] = useState<'build' | 'preview'>('build')
  const [lookups, setLookups] = useState<LookupRow[]>([])
  const [people, setPeople] = useState<{ account: string; displayName?: string | null }[]>([])
  const [saved, setSaved] = useState('')
  const [busy, setBusy] = useState(false)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  const snapshot = (list: Item[], to: string, subject: string) =>
    JSON.stringify({ fields: list.map((i) => cleanField(i.f)), cols: list.filter((i) => i.inTable).map((i) => i.f.key), to: to.trim(), subject: subject.trim() })

  const load = useCallback(async () => {
    const f = await api<FormDetail & { notifyTo?: string | null; notifySubject?: string | null }>(`/api/forms/${enc}`)
    const cols = new Set(f.definition.listColumns ?? [])
    const list: Item[] = f.definition.fields.map((x) => {
      const copy = { ...x }
      delete copy.options // dropdown choices come from their list; they are not part of the saved definition
      return { id: nextId++, f: copy, isNew: false, inTable: cols.has(x.key) }
    })
    setForm(f)
    setItems(list)
    setNotifyTo(f.notifyTo ?? '')
    setNotifySubject(f.notifySubject ?? '')
    setSaved(snapshot(list, f.notifyTo ?? '', f.notifySubject ?? ''))
  }, [enc])

  useEffect(() => {
    load().catch(() => setLoadError('This form could not be found.'))
    api<LookupRow[]>('/api/lookups').then(setLookups).catch(() => undefined)
  }, [load])

  const dirty = form !== null && snapshot(items, notifyTo, notifySubject) !== saved
  useEffect(() => {
    if (!dirty) return
    const warn = (e: BeforeUnloadEvent) => e.preventDefault()
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  useEffect(() => {
    if (!isAdmin) return
    api<{ account: string; displayName?: string | null }[]>('/api/assignees').then(setPeople).catch(() => undefined)
  }, [isAdmin])

  if (loadError) return <div className="container narrow"><div className="alert alert-error" role="alert">{loadError}</div></div>
  if (!form) return <div className="container narrow muted">Loading…</div>

  if (form.canManage === false) {
    return (
      <div className="container narrow">
        <div className="alert alert-error" role="alert">
          Only this form’s owner{form.ownerName || form.ownerAccount ? ` (${form.ownerName || form.ownerAccount})` : ''} and administrators can edit it.
        </div>
        <Link to="/" className="btn">Back to forms</Link>
      </div>
    )
  }

  const isPublic = form.audience === 'public'
  const changeOwner = async (owner: string) => {
    setError(null)
    setMessage(null)
    try {
      await api(`/api/forms/${enc}/owner`, { method: 'PUT', json: { owner: owner || null } })
      await load()
      setMessage(owner ? 'Owner changed. They can now edit this form and see its submissions.' : 'Owner removed. Only administrators can manage this form now.')
    } catch (e) {
      setError(errorText(e, 'Could not change the owner.'))
    }
  }
  const current = items.find((i) => i.id === selected) ?? null
  const taken = (except?: number) => new Set(items.filter((i) => i.id !== except).map((i) => i.f.key))

  const patch = (id: number, change: Partial<Field>) =>
    setItems((prev) =>
      prev.map((i) => {
        if (i.id !== id) return i
        const f = { ...i.f, ...change }
        // New fields follow their label; saved fields keep their key.
        if (i.isNew && change.label !== undefined) f.key = makeKey(change.label, taken(id))
        return { ...i, f }
      }),
    )

  const add = (type: FieldType) => {
    const label = typeName(type).replace(/ \(.*\)$/, '')
    const item: Item = {
      id: nextId++,
      f: { key: makeKey(label, taken()), type, label },
      isNew: true,
      inTable: false,
    }
    setItems((prev) => [...prev, item])
    setSelected(item.id)
    setTab('build')
  }

  const move = (id: number, by: -1 | 1) =>
    setItems((prev) => {
      const at = prev.findIndex((i) => i.id === id)
      const to = at + by
      if (at < 0 || to < 0 || to >= prev.length) return prev
      const copy = [...prev]
      ;[copy[at], copy[to]] = [copy[to], copy[at]]
      return copy
    })

  // Drag a field and drop it onto another: it takes that field's place.
  const dropOn = (targetId: number) => {
    const from = dragId
    setDragId(null)
    setOverId(null)
    if (from === null || from === targetId) return
    setItems((prev) => {
      const at = prev.findIndex((i) => i.id === from)
      const to = prev.findIndex((i) => i.id === targetId)
      if (at < 0 || to < 0) return prev
      const copy = [...prev]
      const [moved] = copy.splice(at, 1)
      copy.splice(to, 0, moved)
      return copy
    })
  }

  const remove = (it: Item) => {
    const warning = it.isNew
      ? `Remove "${it.f.label}"?`
      : `Remove "${it.f.label}"? Answers already submitted keep their data, but new submissions won't have this field.`
    if (!window.confirm(warning)) return
    setItems((prev) => prev.filter((i) => i.id !== it.id))
    setSelected(null)
  }

  const save = async () => {
    setError(null)
    setMessage(null)
    const blank = items.find((i) => !i.f.label.trim())
    if (blank) {
      setSelected(blank.id)
      setError('Every field needs a label.')
      return
    }
    setBusy(true)
    try {
      const before = JSON.parse(saved) as { fields: unknown; cols: string[]; to: string; subject: string }
      const fields = items.map((i) => cleanField(i.f))
      const cols = items.filter((i) => i.inTable).map((i) => i.f.key)
      let version = form.version
      if (JSON.stringify(before.fields) !== JSON.stringify(fields) || JSON.stringify(before.cols) !== JSON.stringify(cols)) {
        const def = { ...form.definition, fields, listColumns: cols.length > 0 ? cols : undefined }
        const r = await api<{ version: number }>(`/api/forms/${enc}/definition`, { method: 'PUT', json: def })
        version = r.version
      }
      if (before.to !== notifyTo.trim() || before.subject !== notifySubject.trim()) {
        await api(`/api/forms/${enc}/notifications`, { method: 'PUT', json: { to: notifyTo.trim() || null, subject: notifySubject.trim() || null } })
      }
      await load()
      setMessage(`Saved${version !== form.version ? ` as version ${version}` : ''}. New submissions use it right away.`)
    } catch (e) {
      setError(errorText(e, 'Could not save.'))
    } finally {
      setBusy(false)
    }
  }

  const changeAudience = async () => {
    const to: Audience = isPublic ? 'internal' : 'public'
    const ask = to === 'public'
      ? `Make "${form.name}" public? Anyone on the internet who has the link will be able to open and submit it.`
      : `Make "${form.name}" internal? The public link will stop working. People will need to sign in with Windows to use it.`
    if (!window.confirm(ask)) return
    setError(null)
    setMessage(null)
    try {
      await api(`/api/forms/${enc}/audience`, { method: 'PUT', json: { audience: to } })
      await load()
      setMessage(`This form is now ${to}.`)
    } catch (e) {
      setError(errorText(e, 'Could not change who the form is for.'))
    }
  }

  const lists = lookups.filter((l) => !isPublic || l.isPublicSafe)
  const addable = TYPES

  return (
    <div className="container wide">
      <div className="page-head">
        <div>
          <h1>{form.name}</h1>
          <p className="muted">
            <span className={`pill pill-${form.audience}`}>{isPublic ? 'Public' : 'Internal'}</span>{' '}
            /f/{form.slug} · version {form.version}
          </p>
          {isAdmin ? (
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', marginTop: 6 }}>
              <label htmlFor="owner" className="small muted">Owner</label>
              <select id="owner" value={form.ownerAccount ?? ''} disabled={busy || dirty} onChange={(e) => changeOwner(e.target.value)}>
                <option value="">No owner (admins only)</option>
                {form.ownerAccount && !people.some((p) => p.account === form.ownerAccount) && <option value={form.ownerAccount}>{form.ownerAccount}</option>}
                {people.map((p) => <option key={p.account} value={p.account}>{p.displayName && p.displayName !== p.account ? `${p.displayName} (${p.account})` : p.account}</option>)}
              </select>
            </div>
          ) : (
            <p className="small muted">Owner: {form.ownerName || form.ownerAccount || '—'}</p>
          )}
        </div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          {isAdmin && (
            <button type="button" className="btn" onClick={changeAudience} disabled={busy || dirty} title={dirty ? 'Save your changes first' : undefined}>
              {isPublic ? 'Make internal' : 'Make public'}
            </button>
          )}
          <Link to="/" className="btn">Back to forms</Link>
          <Link to={`/f/${form.slug}`} className="btn">Open form</Link>
          <button type="button" className="btn btn-primary" onClick={save} disabled={busy || !dirty}>
            {busy ? 'Saving…' : dirty ? 'Save changes' : 'Saved'}
          </button>
        </div>
      </div>

      {error && <div className="alert alert-error" role="alert">{error}</div>}
      {message && !dirty && <div className="alert alert-ok" role="status">{message}</div>}

      <div className="filters" role="group" aria-label="View">
        <button type="button" className="filter" aria-pressed={tab === 'build'} onClick={() => setTab('build')}>Build</button>
        <button type="button" className="filter" aria-pressed={tab === 'preview'} onClick={() => setTab('preview')}>Preview</button>
      </div>

      {tab === 'preview' ? (
        <Preview items={items} />
      ) : (
        <div className="builder">
          <section aria-labelledby="b-add" className="card card-pad">
            <h2 id="b-add">Add a field</h2>
            <div className="b-types">
              {addable.map((t) => (
                <button key={t.type} type="button" className="btn" onClick={() => add(t.type)}>
                  + {t.name}
                </button>
              ))}
            </div>
            {isPublic && <p className="help" style={{ marginTop: 12 }}>Public forms only use lists an admin approved.</p>}
          </section>

          <section aria-labelledby="b-fields" className="card card-pad">
            <h2 id="b-fields">Fields</h2>
            {items.length === 0 && <p className="muted">No fields yet. Add one from the left.</p>}
            <ol className="b-list">
              {items.map((it, idx) => (
                <li
                  key={it.id}
                  className={`b-field${it.id === selected ? ' selected' : ''}${it.id === dragId ? ' dragging' : ''}${overId === it.id && dragId !== it.id ? ' over' : ''}`}
                  draggable
                  onDragStart={(e) => {
                    setDragId(it.id)
                    e.dataTransfer.effectAllowed = 'move'
                    e.dataTransfer.setData('text/plain', String(it.id))
                  }}
                  onDragOver={(e) => {
                    if (dragId === null) return
                    e.preventDefault()
                    e.dataTransfer.dropEffect = 'move'
                    if (overId !== it.id) setOverId(it.id)
                  }}
                  onDrop={(e) => {
                    e.preventDefault()
                    dropOn(it.id)
                  }}
                  onDragEnd={() => {
                    setDragId(null)
                    setOverId(null)
                  }}
                >
                  <span className="b-grip" aria-hidden="true" title="Drag to reorder">⋮⋮</span>
                  <button type="button" className="b-pick" aria-pressed={it.id === selected} onClick={() => setSelected(it.id)}>
                    <span className="strong">
                      {it.f.label || '(no label)'}
                      {it.f.required && <span className="star" aria-hidden="true"> *</span>}
                    </span>
                    <span className="small muted">{typeName(it.f.type)}{it.isNew ? ' · new' : ''}</span>
                  </button>
                  <div className="b-actions">
                    <button type="button" className="btn btn-quiet" aria-label={`Move ${it.f.label} up`} disabled={idx === 0} onClick={() => move(it.id, -1)}>↑</button>
                    <button type="button" className="btn btn-quiet" aria-label={`Move ${it.f.label} down`} disabled={idx === items.length - 1} onClick={() => move(it.id, 1)}>↓</button>
                  </div>
                </li>
              ))}
            </ol>
          </section>

          <aside className="b-side">
            <section aria-labelledby="b-props" className="card card-pad">
              <h2 id="b-props">Field settings</h2>
              {!current && <p className="muted">Choose a field to change its label, rules and choices.</p>}
              {current && (
                <>
                  <div className="field">
                    <label htmlFor="p-label">Label</label>
                    <input id="p-label" type="text" maxLength={200} value={current.f.label} onChange={(e) => patch(current.id, { label: e.target.value })} />
                  </div>
                  <div className="field">
                    <label htmlFor="p-key">Field key</label>
                    <input id="p-key" type="text" readOnly value={current.f.key} aria-describedby="p-key-help" />
                    <div className="help" id="p-key-help">
                      {current.isNew
                        ? 'Made from the label. It is locked once the form is saved, because submissions are stored under it.'
                        : 'Locked: submissions are stored under this key.'}{' '}
                      Use <code>{`{{${current.f.key}}}`}</code> in the email subject.
                    </div>
                  </div>
                  <label className="check">
                    <input type="checkbox" checked={!!current.f.required} onChange={(e) => patch(current.id, { required: e.target.checked })} />
                    Required
                  </label>
                  <div className="field">
                    <label htmlFor="p-help">Help text</label>
                    <input id="p-help" type="text" maxLength={300} value={current.f.help ?? ''} onChange={(e) => patch(current.id, { help: e.target.value })} />
                  </div>
                  {(current.f.type === 'text' || current.f.type === 'email') && (
                    <div className="field">
                      <label htmlFor="p-ph">Placeholder</label>
                      <input id="p-ph" type="text" maxLength={100} value={current.f.placeholder ?? ''} onChange={(e) => patch(current.id, { placeholder: e.target.value })} />
                    </div>
                  )}
                  {(current.f.type === 'text' || current.f.type === 'textarea') && (
                    <div className="field">
                      <label htmlFor="p-max">Longest answer (characters)</label>
                      <input
                        id="p-max"
                        type="number"
                        min={1}
                        max={4000}
                        placeholder={current.f.type === 'textarea' ? '4000' : '300'}
                        value={current.f.maxLength ?? ''}
                        onChange={(e) => patch(current.id, { maxLength: e.target.value ? Number(e.target.value) : undefined })}
                      />
                    </div>
                  )}
                  {current.f.type === 'dropdown' && (
                    <div className="field">
                      <label htmlFor="p-list">Choices come from</label>
                      <select id="p-list" value={current.f.lookup ?? ''} onChange={(e) => patch(current.id, { lookup: e.target.value || undefined })}>
                        <option value="">Choose a list…</option>
                        {lists.map((l) => <option key={l.name} value={l.name}>{l.name}</option>)}
                      </select>
                      <div className="help">
                        {isPublic ? 'Only lists an admin approved for public forms appear here. ' : ''}
                        Need a new list, or different choices? <Link to="/lists">Edit lists</Link> (save this form's changes first).
                      </div>
                    </div>
                  )}
                  {current.f.type === 'number' && (
                    <div className="field">
                      <label htmlFor="p-min">Smallest and largest allowed</label>
                      <div style={{ display: 'flex', gap: 8 }}>
                        <input id="p-min" type="number" placeholder="Min" aria-label="Smallest allowed" value={current.f.min ?? ''}
                          onChange={(e) => patch(current.id, { min: e.target.value === '' ? undefined : Number(e.target.value) })} />
                        <input id="p-maxn" type="number" placeholder="Max" aria-label="Largest allowed" value={current.f.max ?? ''}
                          onChange={(e) => patch(current.id, { max: e.target.value === '' ? undefined : Number(e.target.value) })} />
                      </div>
                    </div>
                  )}
                  <ShowWhen
                    current={current}
                    earlier={items.slice(0, items.findIndex((i) => i.id === current.id)).filter((i) => ['text', 'dropdown', 'checkbox', 'number', 'date', 'email'].includes(i.f.type))}
                    onChange={(showIf) => patch(current.id, { showIf })}
                  />
                  <label className="check">
                    <input
                      type="checkbox"
                      checked={current.inTable}
                      onChange={(e) => setItems((prev) => prev.map((i) => (i.id === current.id ? { ...i, inTable: e.target.checked } : i)))}
                    />
                    Show as a column in the submissions table
                  </label>
                  <button type="button" className="btn btn-danger" style={{ marginTop: 12 }} onClick={() => remove(current)}>
                    Remove field
                  </button>
                </>
              )}
            </section>

            <section aria-labelledby="b-mail" className="card card-pad">
              <h2 id="b-mail">Email</h2>
              <div className="field">
                <label htmlFor="m-to">Send submissions to</label>
                <input id="m-to" type="text" maxLength={1000} value={notifyTo} onChange={(e) => setNotifyTo(e.target.value)} aria-describedby="m-to-help" />
                <div className="help" id="m-to-help">One mailbox, or several separated by commas. Leave empty for no email.</div>
              </div>
              <div className="field" style={{ marginBottom: 0 }}>
                <label htmlFor="m-sub">Subject</label>
                <input id="m-sub" type="text" maxLength={300} value={notifySubject} onChange={(e) => setNotifySubject(e.target.value)} aria-describedby="m-sub-help" />
                <div className="help" id="m-sub-help">Fill in answers with field keys, like <code>{'{{request_type}}'}</code>.</div>
              </div>
            </section>
          </aside>
        </div>
      )}
    </div>
  )
}

/** The form as people will see it. Nothing here is saved or sent. */
function Preview({ items }: { items: Item[] }) {
  const [options, setOptions] = useState<Record<string, Option[]>>({})
  const [values, setValues] = useState<Record<string, Value>>({})
  const fields = useMemo(() => items.map((i) => i.f), [items])
  const names = useMemo(() => [...new Set(fields.filter((f) => f.type === 'dropdown' && f.lookup).map((f) => f.lookup!))], [fields])

  useEffect(() => {
    for (const name of names) {
      if (options[name]) continue
      api<LookupItem[]>(`/api/lookups/${encodeURIComponent(name)}/items`)
        .then((rows) => setOptions((prev) => ({ ...prev, [name]: rows.map((r) => ({ value: r.value, label: r.label })) })))
        .catch(() => undefined)
    }
  }, [names, options])

  const shown = fields.map((f) => (f.type === 'dropdown' && f.lookup ? { ...f, options: options[f.lookup] ?? [] } : f))
  const all = { ...emptyValues(fields), ...values }
  const visible = visibleKeys(fields, all)

  return (
    <div className="card card-pad" style={{ maxWidth: 760 }}>
      <p className="alert alert-ok" role="note" style={{ marginBottom: 20 }}>Preview only. Nothing you type here is saved or sent.</p>
      <div className="fields">
        {shown.filter((f) => visible.has(f.key)).map((f) => (
          <FieldInput key={f.key} field={f} value={all[f.key] ?? ''} onChange={(v) => setValues((prev) => ({ ...prev, [f.key]: v }))} />
        ))}
      </div>
    </div>
  )
}


/** "Show only when…": the field appears only if an earlier field has a chosen answer. */
function ShowWhen({ current, earlier, onChange }: { current: Item; earlier: Item[]; onChange: (rule: Field['showIf']) => void }) {
  const rule = current.f.showIf
  const target = earlier.find((i) => i.f.key === rule?.field)
  const [choices, setChoices] = useState<LookupItem[]>([])

  useEffect(() => {
    if (target?.f.type !== 'dropdown' || !target.f.lookup) {
      setChoices([])
      return
    }
    let stale = false
    api<LookupItem[]>(`/api/lookups/${encodeURIComponent(target.f.lookup)}/items`)
      .then((r) => !stale && setChoices(r))
      .catch(() => !stale && setChoices([]))
    return () => {
      stale = true
    }
  }, [target?.f.type, target?.f.lookup])

  if (earlier.length === 0 && !rule) return null
  return (
    <div className="field">
      <label htmlFor="p-when">Show this field</label>
      <select
        id="p-when"
        value={rule?.field ?? ''}
        onChange={(e) => onChange(e.target.value ? { field: e.target.value, equals: '' } : undefined)}
      >
        <option value="">Always</option>
        {rule && !target && <option value={rule.field}>{rule.field} (missing or below this field)</option>}
        {earlier.map((i) => <option key={i.id} value={i.f.key}>Only when "{i.f.label}" is…</option>)}
      </select>
      {rule && (
        target?.f.type === 'checkbox' ? (
          <select aria-label="Answer" value={rule.equals} onChange={(e) => onChange({ ...rule, equals: e.target.value })}>
            <option value="">Choose…</option>
            <option value="true">Ticked (yes)</option>
            <option value="false">Not ticked (no)</option>
          </select>
        ) : choices.length > 0 ? (
          <select aria-label="Answer" value={rule.equals} onChange={(e) => onChange({ ...rule, equals: e.target.value })}>
            <option value="">Choose…</option>
            {choices.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
          </select>
        ) : (
          <input type="text" aria-label="Answer" maxLength={200} placeholder="Exact answer" value={rule.equals} onChange={(e) => onChange({ ...rule, equals: e.target.value })} />
        )
      )}
      {rule && rule.equals === '' && <div className="help">Pick the answer that shows this field. Until you do, the rule is not saved.</div>}
      <div className="help">Hidden fields are not asked and are not required. Fields can only depend on one above them.</div>
    </div>
  )
}
