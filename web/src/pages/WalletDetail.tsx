import { useEffect, useState } from 'react';
import { useParams } from 'react-router-dom';
import { api, money, type Activity, type StoredEvent, type Wallet } from '../api';
import { Alert, Card } from '../components/ui';

export function WalletDetail() {
  const { id = '' } = useParams();
  const [wallet, setWallet] = useState<Wallet | null>(null);
  const [activity, setActivity] = useState<Activity[]>([]);
  const [events, setEvents] = useState<StoredEvent[]>([]);
  const [tab, setTab] = useState<'activity' | 'events'>('activity');
  const [error, setError] = useState('');

  useEffect(() => {
    api.wallet(id).then(setWallet).catch((e) => setError(e.message));
    api.activity(id).then(setActivity).catch(() => {});
    api.events(id).then(setEvents).catch(() => {});
  }, [id]);

  if (error) return <Alert>{error}</Alert>;
  if (!wallet) return <p>Loading…</p>;
  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-bold">{wallet.name}</h1>
        <p className="font-mono text-sm text-slate-500">{wallet.number}</p>
        <p className="mt-2 text-3xl font-bold">{money(wallet.balance, wallet.currency)}</p>
      </div>
      <div className="flex gap-2" role="tablist">
        {(['activity', 'events'] as const).map((t) => (
          <button key={t} role="tab" aria-selected={tab === t} onClick={() => setTab(t)}
            className={`rounded-md px-3 py-1.5 text-sm ${tab === t ? 'bg-indigo-600 text-white' : 'bg-white'}`}>
            {t === 'activity' ? 'Activity' : `Event stream (${events.length})`}
          </button>
        ))}
      </div>
      {tab === 'activity' ? (
        <Card>
          <table className="w-full text-sm">
            <thead><tr className="text-left text-slate-500"><th className="py-1">When</th><th>Description</th><th className="text-right">Amount</th><th className="text-right">Balance</th></tr></thead>
            <tbody>
              {activity.map((a) => (
                <tr key={a.id} className="border-t">
                  <td className="py-2">{new Date(a.at).toLocaleString()}</td>
                  <td>{a.description}</td>
                  <td className={`text-right font-medium ${a.amount < 0 ? 'text-red-600' : 'text-emerald-700'}`}>{a.amount > 0 ? '+' : ''}{money(a.amount)}</td>
                  <td className="text-right">{money(a.balanceAfter)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {activity.length === 0 && <p className="py-2 text-sm text-slate-500">No activity yet.</p>}
        </Card>
      ) : (
        <Card title="Every change to this wallet, in order: the balance above is derived from these events">
          <ol className="space-y-2 font-mono text-xs">
            {events.map((e) => (
              <li key={e.position} className="rounded bg-slate-50 p-2">
                <span className="font-semibold text-indigo-700">v{e.version} · {e.type}</span>
                <span className="text-slate-500"> @ {e.position}</span>
                <pre className="mt-1 overflow-x-auto whitespace-pre-wrap">{JSON.stringify(e.data, null, 1)}</pre>
              </li>
            ))}
          </ol>
        </Card>
      )}
    </div>
  );
}
