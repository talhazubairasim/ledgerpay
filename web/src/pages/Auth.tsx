import { useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api';
import { useAuth } from '../auth';
import { Alert, Field, button, input } from '../components/ui';

export function AuthPage({ mode }: { mode: 'login' | 'register' }) {
  const { signIn } = useAuth();
  const navigate = useNavigate();
  const [form, setForm] = useState({ fullName: '', email: '', password: '' });
  const [error, setError] = useState('');
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    try {
      signIn(mode === 'login' ? await api.login(form.email, form.password) : await api.register(form.email, form.fullName, form.password));
      navigate('/');
    } catch (err) { setError((err as Error).message); }
  };
  const set = (k: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement>) => setForm({ ...form, [k]: e.target.value });
  return (
    <div className="flex min-h-screen items-center justify-center bg-gradient-to-br from-indigo-700 to-violet-800 p-4">
      <form onSubmit={submit} className="w-full max-w-sm space-y-3 rounded-2xl bg-white p-8 shadow-xl">
        <div className="text-sm font-semibold text-indigo-700">LedgerPay</div>
        <h1 className="text-2xl font-bold">{mode === 'login' ? 'Sign in' : 'Open an account'}</h1>
        {error && <Alert>{error}</Alert>}
        {mode === 'register' && <Field label="Full name"><input required className={input} value={form.fullName} onChange={set('fullName')} /></Field>}
        <Field label="Email"><input required type="email" className={input} value={form.email} onChange={set('email')} /></Field>
        <Field label="Password"><input required type="password" className={input} value={form.password} onChange={set('password')} /></Field>
        <button className={`${button} w-full`}>{mode === 'login' ? 'Sign in' : 'Create account'}</button>
        <p className="text-center text-sm">
          {mode === 'login' ? <Link className="text-indigo-700" to="/register">New here? Open an account</Link> : <Link className="text-indigo-700" to="/login">I already have an account</Link>}
        </p>
      </form>
    </div>
  );
}
