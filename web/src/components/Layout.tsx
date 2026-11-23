import { NavLink, Outlet, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth';

const link = ({ isActive }: { isActive: boolean }) =>
  `rounded-md px-3 py-2 text-sm font-medium ${isActive ? 'bg-white/15 text-white' : 'text-indigo-100 hover:bg-white/10'}`;

export function Layout() {
  const { user, isAdmin, signOut } = useAuth();
  const navigate = useNavigate();
  return (
    <div className="min-h-screen">
      <header className="bg-indigo-700">
        <div className="mx-auto flex max-w-6xl items-center gap-2 px-4 py-3">
          <span className="mr-4 text-lg font-bold text-white">LedgerPay</span>
          <NavLink to="/" end className={link}>Wallets</NavLink>
          <NavLink to="/send" className={link}>Send</NavLink>
          <NavLink to="/transfers" className={link}>Transfers</NavLink>
          {isAdmin && <NavLink to="/admin" className={link}>Back office</NavLink>}
          <span className="ml-auto text-sm text-indigo-100">{user?.fullName}</span>
          <button className="rounded-md border border-white/30 px-3 py-1 text-sm text-white" onClick={() => { signOut(); navigate('/login'); }}>Sign out</button>
        </div>
      </header>
      <main className="mx-auto max-w-6xl p-4"><Outlet /></main>
    </div>
  );
}
