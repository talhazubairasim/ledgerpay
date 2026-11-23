import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../src/auth';
import { AppRoutes } from '../src/App';
import { session, type Session } from '../src/api';

type Handler = (init: RequestInit | undefined, url: URL) => { status?: number; body?: unknown };

/** A plain `{ status, body? }` object is treated as a response spec rather than a JSON body. */
const isSpec = (v: unknown): v is { status: number; body?: unknown } =>
  !!v && typeof v === 'object' && !Array.isArray(v) && 'status' in v && typeof (v as { status: unknown }).status === 'number'
  && Object.keys(v).every((k) => k === 'status' || k === 'body');

/** Routes fetch calls to handlers keyed by "METHOD /path" (query string ignored). */
export function mockApi(routes: Record<string, Handler | unknown>) {
  const calls: { key: string; url: string; body: unknown; headers: Record<string, string> }[] = [];
  vi.spyOn(globalThis, 'fetch').mockImplementation(async (input, init) => {
    const url = new URL(String(input), 'http://localhost');
    const key = `${init?.method ?? 'GET'} ${url.pathname}`;
    calls.push({ key, url: url.toString(), body: init?.body ? JSON.parse(String(init.body)) : undefined, headers: (init?.headers ?? {}) as Record<string, string> });
    const route = routes[key];
    if (route === undefined) return new Response(JSON.stringify({ error: `no mock for ${key}` }), { status: 500 });
    const { status = 200, body } = typeof route === 'function' ? (route as Handler)(init, url) : isSpec(route) ? route : { body: route };
    return new Response(body === undefined ? null : JSON.stringify(body), { status });
  });
  return calls;
}

export const customer: Session = { token: 't', userId: 'u1', email: 'ali@x.dev', fullName: 'Ali Khan', role: 'customer' };
export const admin: Session = { ...customer, userId: 'a1', fullName: 'Reviewer', role: 'admin' };

export function renderAt(path: string, user: Session | null = null) {
  if (user) session.set(user);
  return render(
    <AuthProvider>
      <MemoryRouter initialEntries={[path]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AppRoutes />
      </MemoryRouter>
    </AuthProvider>,
  );
}
