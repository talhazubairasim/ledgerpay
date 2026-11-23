export type TransferStatus = 'Requested' | 'PendingReview' | 'Approved' | 'Completed' | 'Rejected';
export interface Session { token: string; userId: string; email: string; fullName: string; role: 'customer' | 'admin' }
export interface Wallet { id: string; ownerId: string; number: string; name: string; currency: string; balance: number; frozen: boolean; openedAt: string }
export interface Activity { id: number; position: number; kind: string; amount: number; balanceAfter: number; description: string; at: string }
export interface Payee { id: string; number: string; name: string; currency: string; owner: string }
export interface TransferView { id: string; status: TransferStatus; amount: number; currency: string; reasons: string[]; rejectionReason: string | null; replayed: boolean }
export interface TransferRow { id: string; requestedBy: string; fromAccount: string; fromNumber: string; toAccount: string; toNumber: string; amount: number; currency: string; memo: string; status: TransferStatus; reasons: string[]; score: number; rejectionReason: string | null; requestedAt: string; updatedAt: string }
export interface StoredEvent { position: number; streamId?: string; version: number; type: string; createdAt: string; data: Record<string, unknown> }
export interface TrialBalance { lines: { currency: string; kind: string; total: number }[]; balanced: boolean; walletsChecked: number; mismatches: string[] }
export interface Accepted<T> { result: T; position: number }

export class ApiError extends Error {
  constructor(public status: number, message: string) { super(message); }
}

const KEY = 'ledgerpay.session';
export const session = {
  get(): Session | null {
    try { return JSON.parse(localStorage.getItem(KEY) ?? 'null'); } catch { return null; }
  },
  set(s: Session | null) { if (s) localStorage.setItem(KEY, JSON.stringify(s)); else localStorage.removeItem(KEY); },
};

/** The newest event position this tab has written; reads wait until the projection has caught up to it. */
let lastPosition = 0;
export const positions = { get: () => lastPosition, note: (p: number) => { lastPosition = Math.max(lastPosition, p); }, reset: () => { lastPosition = 0; } };

async function errorMessage(res: Response): Promise<string> {
  try {
    const body = await res.json();
    if (body.errors) return Object.values(body.errors as Record<string, string[]>).flat().join(' ');
    return body.title ?? res.statusText;
  } catch {
    return res.status === 401 ? 'Please sign in.' : res.statusText || 'Request failed';
  }
}

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const token = session.get()?.token;
  const res = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}), ...init.headers },
  });
  if (!res.ok) throw new ApiError(res.status, await errorMessage(res));
  const text = res.status === 204 ? '' : await res.text();
  const body = (text ? JSON.parse(text) : undefined) as T;
  if (body && typeof body === 'object' && 'position' in body && typeof (body as { position: unknown }).position === 'number')
    positions.note((body as { position: number }).position);
  return body;
}

const read = <T>(path: string) => request<T>(`${path}${path.includes('?') ? '&' : '?'}after=${positions.get()}`);
const post = <T>(path: string, body: unknown = {}, headers: Record<string, string> = {}) =>
  request<T>(path, { method: 'POST', body: JSON.stringify(body), headers });

export const api = {
  register: (email: string, fullName: string, password: string) => post<Session>('/api/auth/register', { email, fullName, password }),
  login: (email: string, password: string) => post<Session>('/api/auth/login', { email, password }),
  wallets: () => read<Wallet[]>('/api/accounts'),
  wallet: (id: string) => read<Wallet>(`/api/accounts/${id}`),
  openWallet: (name: string, currency: string) => post<Accepted<Wallet>>('/api/accounts', { name, currency }),
  deposit: (id: string, amount: number, reference: string) => post<Accepted<unknown>>(`/api/accounts/${id}/deposit`, { amount, reference }),
  withdraw: (id: string, amount: number, reference: string) => post<Accepted<unknown>>(`/api/accounts/${id}/withdraw`, { amount, reference }),
  activity: (id: string) => read<Activity[]>(`/api/accounts/${id}/activity`),
  events: (id: string) => request<StoredEvent[]>(`/api/accounts/${id}/events`),
  payee: (number: string) => request<Payee>(`/api/payees/${encodeURIComponent(number)}`),
  transfer: (fromAccountId: string, toAccountNumber: string, amount: number, memo: string, idempotencyKey: string) =>
    post<Accepted<TransferView>>('/api/transfers', { fromAccountId, toAccountNumber, amount, memo }, { 'Idempotency-Key': idempotencyKey }),
  transfers: () => read<TransferRow[]>('/api/transfers'),
  reviews: () => read<TransferRow[]>('/api/admin/reviews'),
  approve: (id: string) => post<Accepted<TransferView>>(`/api/admin/transfers/${id}/approve`),
  reject: (id: string, reason: string) => post<Accepted<TransferView>>(`/api/admin/transfers/${id}/reject`, { reason }),
  freeze: (id: string, reason: string) => post<Accepted<unknown>>(`/api/admin/accounts/${id}/freeze`, { reason }),
  unfreeze: (id: string) => post<Accepted<unknown>>(`/api/admin/accounts/${id}/unfreeze`),
  trialBalance: () => read<TrialBalance>('/api/admin/trial-balance'),
  log: (after = 0) => request<StoredEvent[]>(`/api/admin/events?after=${after}&take=100`),
  rebuild: () => post<{ replayed: number }>('/api/admin/projections/rebuild'),
};

export const money = (n: number, currency?: string) =>
  `${currency ? `${currency} ` : ''}${n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
export const newKey = () => (globalThis.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random()}`);
