import { useEffect, useRef, useState, type FormEvent } from 'react';
import { api, money, newKey, type Payee, type TransferView, type Wallet } from '../api';
import { Alert, Badge, Card, Field, button, input } from '../components/ui';

export function Send() {
  const [wallets, setWallets] = useState<Wallet[]>([]);
  const [from, setFrom] = useState('');
  const [to, setTo] = useState('');
  const [payee, setPayee] = useState<Payee | null>(null);
  const [amount, setAmount] = useState('');
  const [memo, setMemo] = useState('');
  const [result, setResult] = useState<TransferView | null>(null);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  // One key per intended transfer: a retry after a network error reuses it, so money can't move twice.
  const key = useRef(newKey());

  useEffect(() => {
    api.wallets().then((w) => { setWallets(w); if (w[0]) setFrom((f) => f || w[0].id); }).catch((e) => setError(e.message));
  }, []);

  useEffect(() => {
    setPayee(null);
    const number = to.trim().toUpperCase();
    if (!/^LP\d{10}$/.test(number)) return;
    const t = setTimeout(() => api.payee(number).then(setPayee).catch(() => setPayee(null)), 200);
    return () => clearTimeout(t);
  }, [to]);

  const source = wallets.find((w) => w.id === from);
  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setError('');
    setBusy(true);
    try {
      const { result } = await api.transfer(from, to.trim().toUpperCase(), Number(amount), memo, key.current);
      setResult(result);
      key.current = newKey();
      setAmount('');
      setMemo('');
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="grid gap-4 md:grid-cols-2">
      <Card title="Send money">
        {error && <Alert>{error}</Alert>}
        <form onSubmit={submit} className="space-y-3">
          <Field label="From">
            <select aria-label="From wallet" className={input} value={from} onChange={(e) => setFrom(e.target.value)}>
              {wallets.map((w) => <option key={w.id} value={w.id}>{w.name} · {money(w.balance, w.currency)}</option>)}
            </select>
          </Field>
          <Field label="To wallet number">
            <input aria-label="To wallet number" required placeholder="LP0123456789" className={`${input} font-mono`} value={to} onChange={(e) => setTo(e.target.value)} />
          </Field>
          {payee && <p className="text-sm text-emerald-700">Paying <strong>{payee.owner}</strong> · {payee.name} ({payee.currency})</p>}
          {payee && source && payee.currency !== source.currency && <p className="text-sm text-red-700">Currencies differ: choose a {payee.currency} wallet.</p>}
          <Field label={`Amount${source ? ` (${source.currency})` : ''}`}>
            <input aria-label="Amount" required type="number" min="0.01" step="0.01" className={input} value={amount} onChange={(e) => setAmount(e.target.value)} />
          </Field>
          <Field label="Note"><input aria-label="Note" maxLength={140} className={input} value={memo} onChange={(e) => setMemo(e.target.value)} /></Field>
          <button disabled={busy || !from} className={`${button} w-full`}>{busy ? 'Sending…' : 'Send'}</button>
        </form>
      </Card>
      {result && (
        <Card title="Transfer">
          <div className="mb-2 flex items-center gap-2"><Badge status={result.status} /><span className="font-semibold">{money(result.amount / 100, result.currency)}</span></div>
          {result.status === 'Completed' && <p className="text-sm">Done. The money is in the payee's wallet.</p>}
          {result.status === 'PendingReview' && (
            <div className="text-sm">
              <p>Held for a quick security review. Nothing has left your wallet yet.</p>
              <ul className="mt-1 list-disc pl-5 text-amber-800">{result.reasons.map((r) => <li key={r}>{r}</li>)}</ul>
            </div>
          )}
          {result.status === 'Rejected' && <p className="text-sm text-red-700">{result.rejectionReason}</p>}
        </Card>
      )}
    </div>
  );
}
