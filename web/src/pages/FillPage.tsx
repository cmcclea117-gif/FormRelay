import { useEffect, useState, type FormEvent } from 'react'
import { api, ApiError } from '../api'
import { checkValues, emptyValues, FieldInput, visibleKeys, type Value } from '../components/FieldInput'
import { PublicShell } from '../components/Shell'
import type { FormDetail } from '../types'

type Loaded = { form: FormDetail; viaPublic: boolean }

// Public forms load without signing in. If the slug isn't a public form, try it as an internal form
// (that call uses the visitor's Windows sign-in and fails for people who aren't staff).
async function loadForm(slug: string): Promise<Loaded> {
  const enc = encodeURIComponent(slug)
  try {
    return { form: await api<FormDetail>(`/api/public/forms/${enc}`), viaPublic: true }
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) {
      return { form: await api<FormDetail>(`/api/forms/${enc}`), viaPublic: false }
    }
    throw e
  }
}

export default function FillPage({ slug }: { slug: string }) {
  const [loaded, setLoaded] = useState<Loaded | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [values, setValues] = useState<Record<string, Value>>({})
  const [website, setWebsite] = useState('') // honeypot: real people never see or fill this
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState(false)

  useEffect(() => {
    loadForm(slug)
      .then((l) => {
        setLoaded(l)
        setValues(emptyValues(l.form.definition.fields))
      })
      .catch(() => setLoadError('This form could not be found, or you do not have access to it.'))
  }, [slug])

  const backTo = loaded && !loaded.viaPublic ? { to: '/', label: '← Back to forms' } : undefined

  if (loadError) {
    return (
      <PublicShell>
        <div className="container narrow">
          <div className="alert alert-error" role="alert">{loadError}</div>
        </div>
      </PublicShell>
    )
  }
  if (!loaded) {
    return (
      <PublicShell>
        <div className="container narrow muted">Loading…</div>
      </PublicShell>
    )
  }

  const { form, viaPublic } = loaded
  const fields = form.definition.fields
  const shown = visibleKeys(fields, values)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setFormError(null)
    const problems = checkValues(fields, values)
    setErrors(problems)
    if (Object.keys(problems).length > 0) {
      setFormError('Please fix the highlighted fields.')
      document.getElementById(`f-${Object.keys(problems)[0]}`)?.focus()
      return
    }

    setBusy(true)
    try {
      const path = viaPublic
        ? `/api/public/forms/${encodeURIComponent(slug)}/submissions`
        : `/api/forms/${encodeURIComponent(slug)}/submissions`
      await api(path, { method: 'POST', json: { ...values, website } })
      setDone(true)
    } catch (err) {
      if (err instanceof ApiError && err.status === 400 && err.body && typeof err.body === 'object' && 'errors' in err.body) {
        setErrors(err.body.errors as Record<string, string>)
        setFormError('Please fix the highlighted fields.')
      } else if (err instanceof ApiError && err.status === 429) {
        setFormError('Too many submissions from your network just now. Please wait a few minutes and try again.')
      } else {
        setFormError('Sorry, that did not go through. Please try again.')
      }
    } finally {
      setBusy(false)
    }
  }

  if (done) {
    return (
      <PublicShell backTo={backTo}>
        <div className="container narrow">
          <div className="card card-pad" role="status">
            <h1>Thank you</h1>
            <p className="muted" style={{ margin: '8px 0 20px' }}>
              Your {form.name.toLowerCase()} was received.
            </p>
            <button
              type="button"
              className="btn btn-primary"
              onClick={() => {
                setValues(emptyValues(fields))
                setErrors({})
                setDone(false)
              }}
            >
              Submit another
            </button>
          </div>
        </div>
      </PublicShell>
    )
  }

  return (
    <PublicShell backTo={backTo}>
      <div className="container narrow">
        <div>
          <h1>{form.name}</h1>
          {form.description && <p className="muted" style={{ marginTop: 4 }}>{form.description}</p>}
        </div>

        <form className="card card-pad" onSubmit={submit} noValidate>
          {formError && (
            <div className="alert alert-error" role="alert" style={{ marginBottom: 20 }}>
              {formError}
            </div>
          )}

          <div className="fields">
            {fields.filter((f) => shown.has(f.key)).map((f) => (
              <FieldInput
                key={f.key}
                field={f}
                value={values[f.key] ?? ''}
                error={errors[f.key]}
                onChange={(v) => setValues((prev) => ({ ...prev, [f.key]: v }))}
              />
            ))}
          </div>

          <div className="hp" aria-hidden="true">
            <label>
              Leave this empty
              <input type="text" name="website" tabIndex={-1} autoComplete="off" value={website} onChange={(e) => setWebsite(e.target.value)} />
            </label>
          </div>

          <p className="small muted" style={{ marginBottom: 16 }}>
            <span className="star">*</span> required
          </p>
          <button type="submit" className="btn btn-primary" disabled={busy}>
            {busy ? 'Sending…' : 'Submit'}
          </button>
        </form>
      </div>
    </PublicShell>
  )
}
