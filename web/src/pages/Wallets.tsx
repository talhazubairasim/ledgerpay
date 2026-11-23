import { useEffect, useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api, money, type Wallet } from '../api';
import { Alert, Card, button, ghost, input } from '../components/ui';

export function Wallets() {
  const [wallets, setWallets] = useState<Wallet[]>([]);
  const [name, setName] = useState('');
  const [currency, setCurrency] = useState('PKR');
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => { api.wallets().then(setWallets).catch((e) => setMessage({ ok: false, text: e.message })); }, [tick]);

  const run = async (p: Promise<unknown>, text: string) => {
    try { await p; setMessage({ ok: true, text }); setTick((t) => t + 1); } catch (e) { setMessage({ ok: false, text: (e as Error).message }); }
  };
  const open = (e: FormEvent) => { e.preventDefault(); run(api.openWallet(name, currency), `${name} wallet opened`); setName(''); };

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Your wallets</h1>
      {message && <Alert kind={message.ok ? 'success' : 'error'}>{message.text}</Alert>}
      <div className="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
        {wallets.map((w) => <WalletCard key={w.id} wallet={w} run={run} />)}
        <Card title="Open a wallet">
          <form onSubmit={open} className="space-y-2">
            <input aria-label="Wallet name" required placeholder="e.g. Savings" className={input} value={name} onChange={(e) => setName(e.target.value)} />
            <select aria-label="Currency" className={input} value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {['PKR', 'USD', 'EUR'].map((c) => <option key={c}>{c}</option>)}
            </select>
            <button className={`${button} w-full`}>Open wallet</button>
          </form>
        </Card>
      </div>
    </div>
  );
}

function WalletCard({ wallet: w, run }: { wallet: Wallet; run: (p: Promise<unknown>, text: string) => void }) {
  const [amount, setAmount] = useState('');
  const value = Number(amount);
  return (
    <article aria-label={w.name} className="rounded-xl bg-white p-4 shadow-sm">
      <div className="flex items-center justify-between">
        <Link to={`/wallets/${w.id}`} className="font-semibold text-indigo-700">{w.name}</Link>
        {w.frozen && <span className="rounded bg-red-100 px-2 text-xs text-red-700">Frozen</span>}
      </div>
      <div className="font-mono text-xs text-slate-500">{w.number}</div>
      <div className="my-3 text-2xl font-bold">{money(w.balance, w.currency)}</div>
      <div className="flex gap-2">
        <input aria-label={`Amount for ${w.name}`} type="number" min="0.01" step="0.01" placeholder="Amount" value={amount}
          onChange={(e) => setAmount(e.target.value)} className="w-28 rounded border px-2 py-1 text-sm" />
        <button className={ghost} disabled={!value} onClick={() => { run(api.deposit(w.id, value, 'Card top-up'), `Added ${money(value, w.currency)}`); setAmount(''); }}>Add money</button>
        <button className={ghost} disabled={!value} onClick={() => { run(api.withdraw(w.id, value, 'Bank withdrawal'), `Withdrew ${money(value, w.currency)}`); setAmount(''); }}>Withdraw</button>
      </div>
    </article>
  );
}
