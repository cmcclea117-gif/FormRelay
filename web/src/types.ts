export type Me = { account: string; displayName?: string | null; role?: string | null; hasAccess: boolean }

export type Option = { value: string; label: string }

export type FieldType = 'text' | 'textarea' | 'email' | 'number' | 'date' | 'dropdown' | 'checkbox'

export type Field = {
  key: string
  type: FieldType
  label: string
  required?: boolean
  help?: string
  placeholder?: string
  maxLength?: number
  lookup?: string
  options?: Option[]
  /** number only: smallest and largest allowed values. */
  min?: number
  max?: number
  /** Show this field only when the field above named here has exactly this answer. */
  showIf?: { field: string; equals: string }
}

export type Audience = 'internal' | 'public'

export type FormDefinition = { fields: Field[]; listColumns?: string[] }

export type FormDetail = {
  slug: string
  name: string
  description?: string | null
  audience: Audience
  version: number
  definition: FormDefinition
  canManage?: boolean
  ownerAccount?: string | null
  ownerName?: string | null
}

export type FormSummary = {
  slug: string
  name: string
  description?: string | null
  audience: Audience
  createdUtc: string
  latestVersion?: number | null
  ownerAccount?: string | null
  ownerName?: string | null
  /** True for admins and for the form's owner; only then are the submission numbers present. */
  canManage?: boolean
  // Present only when canManage.
  totalSubmissions?: number
  newSubmissions?: number
  lastSubmittedUtc?: string | null
}

export type Status = 'New' | 'In progress' | 'Done'

export type Submission = {
  id: number
  formVersion: number
  submittedUtc: string
  submittedBy?: string | null
  status: Status
  assignedTo?: string | null
  statusUpdatedUtc?: string | null
  statusUpdatedBy?: string | null
  payload: Record<string, unknown>
}

export type AppUser = {
  id: number
  account: string
  displayName?: string | null
  role: 'Admin' | 'User'
  isActive: boolean
  createdUtc: string
  lastSignInUtc?: string | null
}
