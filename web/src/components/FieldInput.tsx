import type { Field } from '../types'

export type Value = string | boolean

type Props = {
  field: Field
  value: Value
  error?: string
  onChange: (value: Value) => void
  /** Every field's current value, so one field can depend on another (an order field reads its customer field). */
}

/** Renders one form field from its definition, with its label, help text, and error message. */
export function FieldInput({ field, value, error, onChange }: Props) {
  const id = `f-${field.key}`
  const helpId = field.help ? `${id}-help` : undefined
  const errId = error ? `${id}-err` : undefined
  const described = [helpId, errId].filter(Boolean).join(' ') || undefined
  const common = {
    id,
    'aria-invalid': error ? true : undefined,
    'aria-describedby': described,
    'aria-required': field.required ? true : undefined,
  } as const
  const text = typeof value === 'string' ? value : ''

  let control
  switch (field.type) {
    case 'textarea':
      control = <textarea {...common} value={text} maxLength={field.maxLength ?? 4000} onChange={(e) => onChange(e.target.value)} />
      break
    case 'number':
      control = <input {...common} type="number" min={field.min} max={field.max} value={text} onChange={(e) => onChange(e.target.value)} />
      break
    case 'email':
      control = <input {...common} type="email" inputMode="email" autoComplete="email" maxLength={254} value={text} placeholder={field.placeholder} onChange={(e) => onChange(e.target.value)} />
      break
    case 'date':
      control = <input {...common} type="date" value={text} onChange={(e) => onChange(e.target.value)} />
      break
    case 'dropdown': {
      const options = field.options ?? []
      control = (
        <select {...common} value={text} onChange={(e) => onChange(e.target.value)} disabled={options.length === 0}>
          <option value="">{options.length === 0 ? 'No choices available' : 'Select…'}</option>
          {options.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      )
      break
    }
    case 'checkbox':
      control = (
        <label className="check">
          <input {...common} type="checkbox" checked={value === true} onChange={(e) => onChange(e.target.checked)} />
          Yes
        </label>
      )
      break
    default:
      control = (
        <input
          {...common}
          type="text"
          value={text}
          maxLength={field.maxLength ?? 300}
          placeholder={field.placeholder}
          onChange={(e) => onChange(e.target.value)}
        />
      )
  }

  return (
    <div className={field.type === 'textarea' ? 'field full' : 'field'}>
      <label htmlFor={id}>
        {field.label}
        {field.required && (
          <span className="star" aria-hidden="true">
            {' '}
            *
          </span>
        )}
      </label>
      {control}
      {field.help && (
        <div className="help" id={helpId}>
          {field.help}
        </div>
      )}
      {error && (
        <div className="error-text" id={errId} role="alert">
          {error}
        </div>
      )}
    </div>
  )
}

/** Which fields are showing right now: those with no "show only when" rule, or whose rule is met. */
export function visibleKeys(fields: Field[], values: Record<string, Value>): Set<string> {
  const shown = new Set<string>()
  for (const f of fields) {
    if (!f.showIf) {
      shown.add(f.key)
      continue
    }
    const v = values[f.showIf.field]
    const answer = typeof v === 'boolean' ? String(v) : (v ?? '').trim()
    if (shown.has(f.showIf.field) && answer === f.showIf.equals) shown.add(f.key)
  }
  return shown
}

export function emptyValues(fields: Field[]): Record<string, Value> {
  return Object.fromEntries(fields.map((f) => [f.key, f.type === 'checkbox' ? false : '']))
}

/** Same rules the server enforces, so people see problems before they submit. The server still has the final say. */
export function checkValues(fields: Field[], values: Record<string, Value>): Record<string, string> {
  const errors: Record<string, string> = {}
  const shown = visibleKeys(fields, values)
  for (const f of fields) {
    if (!shown.has(f.key)) continue
    const v = values[f.key]
    const text = typeof v === 'string' ? v.trim() : ''
    if (f.required && (f.type === 'checkbox' ? v !== true : text === '')) errors[f.key] = `${f.label} is required`
    else if (f.type === 'number' && text !== '' && Number.isNaN(Number(text))) errors[f.key] = `${f.label} must be a number`
    else if (f.type === 'number' && text !== '' && f.min != null && Number(text) < f.min) errors[f.key] = `${f.label} must be at least ${f.min}`
    else if (f.type === 'number' && text !== '' && f.max != null && Number(text) > f.max) errors[f.key] = `${f.label} must be at most ${f.max}`
    else if (f.type === 'email' && text !== '' && !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(text)) errors[f.key] = `${f.label} must be an email address`
  }
  return errors
}
