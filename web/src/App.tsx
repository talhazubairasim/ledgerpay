import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom';
import type { ReactNode } from 'react';
import { AuthProvider, useAuth } from './auth';
import { Layout } from './components/Layout';
import { AuthPage } from './pages/Auth';
import { Wallets } from './pages/Wallets';
import { WalletDetail } from './pages/WalletDetail';
import { Send } from './pages/Send';
import { Transfers } from './pages/Transfers';
import { Admin } from './pages/Admin';

function Guard({ admin = false, children }: { admin?: boolean; children: ReactNode }) {
  const { user, isAdmin } = useAuth();
  if (!user) return <Navigate to="/login" replace />;
  if (admin && !isAdmin) return <Navigate to="/" replace />;
  return <>{children}</>;
}

export function AppRoutes() {
  return (
    <Routes>
      <Route path="/login" element={<AuthPage key="login" mode="login" />} />
      <Route path="/register" element={<AuthPage key="register" mode="register" />} />
      <Route element={<Guard><Layout /></Guard>}>
        <Route index element={<Wallets />} />
        <Route path="wallets/:id" element={<WalletDetail />} />
        <Route path="send" element={<Send />} />
        <Route path="transfers" element={<Transfers />} />
        <Route path="admin" element={<Guard admin><Admin /></Guard>} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}><AppRoutes /></BrowserRouter>
    </AuthProvider>
  );
}
