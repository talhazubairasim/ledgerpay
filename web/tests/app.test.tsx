import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError, positions, request, session } from '../src/api';
import { admin, customer, mockApi, renderAt } from './helpers';

const wallet = (over: Record<string, unknown> = {}) => ({
  id: 'w1', ownerId: 'u1', number: 'LP0000000001', name: 'Main', currency: 'PKR', balance: 1500.5, frozen: false, openedAt: '2030-01-01T00:00:00Z', ...over,
});
const row = (over: Record<string, unknown> = {}) => ({
  id: 't1', requestedBy: 'u1', fromAccount: 'w1', fromNumber: 'LP0000000001', toAccount: 'w2', toNumber: 'LP0000000002', amount: 15000, currency: 'PKR',
  memo: 'Rent', status: 'PendingReview', reasons: ['Large amount (≥ 10,000.00)'], score: 60, rejectionReason: null, requestedAt: '2030-01-01T00:00:00Z', updatedAt: '', ...over,
});

beforeEach(() => positions.reset());

describe('auth', () => {
  it('registers, signs in and out', async () => {
    const user = userEvent.setup();
    const calls = mockApi({
      'POST /api/auth/register': { status: 400, body: { errors: { Password: ['Password needs 8+ characters with an uppercase letter and a digit.'] } } },
      'POST /api/auth/login': customer,
      'GET /api/accounts': [],
    });
    renderAt('/register');
    await user.type(screen.getByLabelText('Full name'), 'Ali');
    await user.type(screen.getByLabelText('Email'), 'ali@x.dev');
    await user.type(screen.getByLabelText('Password'), 'weak');
    await user.click(screen.getByRole('button', { name: 'Create account' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('uppercase letter');
    await user.click(screen.getByText('I already have an account'));
    await user.type(screen.getByLabelText('Email'), 'ali@x.dev');
    await user.type(screen.getByLabelText('Password'), 'Passw0rd!');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(await screen.findByText('Your wallets')).toBeInTheDocument();
    expect(calls.find((c) => c.key === 'POST /api/auth/login')!.body).toEqual({ email: 'ali@x.dev', password: 'Passw0rd!' });
    await user.click(screen.getByText('Sign out'));
    expect(session.get()).toBeNull();
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });
});

describe('wallets', () => {
  it('opens wallets, adds money and waits for its own writes', async () => {
    const user = userEvent.setup();
    let balance = 1500.5;
    const calls = mockApi({
      'GET /api/accounts': () => ({ body: [wallet({ balance })] }),
      'POST /api/accounts': { status: 201, body: { result: wallet({ id: 'w2', name: 'Savings' }), position: 7 } },
      'POST /api/accounts/w1/deposit': () => { balance += 100; return { body: { result: {}, position: 9 } }; },
      'POST /api/accounts/w1/withdraw': { status: 409, body: { title: 'Insufficient funds.' } },
    });
    renderAt('/', customer);
    const card = await screen.findByRole('article', { name: 'Main' });
    expect(within(card).getByText('PKR 1,500.50')).toBeInTheDocument();

    await user.type(screen.getByLabelText('Wallet name'), 'Savings');
    await user.selectOptions(screen.getByLabelText('Currency'), 'USD');
    await user.click(screen.getByRole('button', { name: 'Open wallet' }));
    expect(await screen.findByText('Savings wallet opened')).toBeInTheDocument();
    expect(calls.find((c) => c.key === 'POST /api/accounts')!.body).toEqual({ name: 'Savings', currency: 'USD' });

    await user.type(within(card).getByLabelText('Amount for Main'), '100');
    await user.click(within(card).getByText('Add money'));
    expect(await within(card).findByText('PKR 1,600.50')).toBeInTheDocument();
    expect(calls.filter((c) => c.key === 'GET /api/accounts').at(-1)!.url).toContain('after=9');

    await user.type(within(card).getByLabelText('Amount for Main'), '99999');
    await user.click(within(card).getByText('Withdraw'));
    expect(await screen.findByRole('alert')).toHaveTextContent('Insufficient funds.');
  });

  it('shows activity and the raw event stream', async () => {
    const user = userEvent.setup();
    mockApi({
      'GET /api/accounts/w1': wallet(),
      'GET /api/accounts/w1/activity': [
        { id: 2, position: 5, kind: 'TransferOut', amount: -200, balanceAfter: 1500.5, description: 'Transfer to LP0000000002 · Rent', at: '2030-01-02T00:00:00Z' },
        { id: 1, position: 3, kind: 'Deposit', amount: 1700.5, balanceAfter: 1700.5, description: 'Deposit · card', at: '2030-01-01T00:00:00Z' },
      ],
      'GET /api/accounts/w1/events': [{ position: 1, version: 0, type: 'AccountOpened', createdAt: '', data: { number: 'LP0000000001' } }],
    });
    renderAt('/wallets/w1', customer);
    expect(await screen.findByText('Transfer to LP0000000002 · Rent')).toBeInTheDocument();
    expect(screen.getByText('-200.00')).toBeInTheDocument();
    expect(screen.getByText('+1,700.50')).toBeInTheDocument();
    await user.click(screen.getByRole('tab', { name: /Event stream/ }));
    expect(await screen.findByText('v0 · AccountOpened')).toBeInTheDocument();
  });

  it('reports a missing wallet', async () => {
    mockApi({ 'GET /api/accounts/zz': { status: 404 }, 'GET /api/accounts/zz/activity': [], 'GET /api/accounts/zz/events': [] });
    renderAt('/wallets/zz', customer);
    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});

describe('sending money', () => {
  it('confirms the payee, sends with an idempotency key and shows the outcome', async () => {
    const user = userEvent.setup();
    let status = 'Completed';
    const calls = mockApi({
      'GET /api/accounts': [wallet()],
      'GET /api/payees/LP0000000002': { id: 'w2', number: 'LP0000000002', name: 'Main', currency: 'PKR', owner: 'Sara Ahmed' },
      'POST /api/transfers': () => ({ status: 201, body: { result: { id: 't1', status, amount: 25000, currency: 'PKR', reasons: status === 'PendingReview' ? ['Large amount (≥ 10,000.00)'] : [], rejectionReason: null }, position: 12 } }),
    });
    renderAt('/send', customer);
    await screen.findByRole('option', { name: /Main · PKR 1,500.50/ });
    await user.type(screen.getByLabelText('To wallet number'), 'lp0000000002');
    expect(await screen.findByText('Sara Ahmed')).toBeInTheDocument();
    await user.type(screen.getByLabelText('Amount'), '250');
    await user.type(screen.getByLabelText('Note'), 'Dinner');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText(/The money is in the payee's wallet/)).toBeInTheDocument();

    const sent = calls.filter((c) => c.key === 'POST /api/transfers');
    expect(sent[0].body).toEqual({ fromAccountId: 'w1', toAccountNumber: 'LP0000000002', amount: 250, memo: 'Dinner' });
    status = 'PendingReview';
    await user.type(screen.getByLabelText('Amount'), '15000');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText(/security review/)).toBeInTheDocument();
    expect(screen.getByText('Large amount (≥ 10,000.00)')).toBeInTheDocument();
    const keys = calls.filter((c) => c.key === 'POST /api/transfers').map((c) => c.headers['Idempotency-Key']);
    expect(keys[0]).toBeTruthy();
    expect(keys[0]).not.toEqual(keys[1]);
  });

  it('keeps the same key when a send fails, and shows rejections', async () => {
    const user = userEvent.setup();
    let attempt = 0;
    const calls = mockApi({
      'GET /api/accounts': [wallet({ currency: 'USD' })],
      'GET /api/payees/LP0000000002': { id: 'w2', number: 'LP0000000002', name: 'Main', currency: 'PKR', owner: 'Sara' },
      'POST /api/transfers': () => (++attempt === 1 ? { status: 503, body: { title: 'Try again' } }
        : { status: 201, body: { result: { id: 't', status: 'Rejected', amount: 100, currency: 'USD', reasons: [], rejectionReason: 'Insufficient funds.' }, position: 3 } }),
    });
    renderAt('/send', customer);
    await screen.findByRole('option', { name: /Main/ });
    await user.type(screen.getByLabelText('To wallet number'), 'LP0000000002');
    expect(await screen.findByText(/Currencies differ/)).toBeInTheDocument();
    await user.type(screen.getByLabelText('Amount'), '1');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Try again');
    await user.click(screen.getByRole('button', { name: 'Send' }));
    expect(await screen.findByText('Insufficient funds.')).toBeInTheDocument();
    const keys = calls.filter((c) => c.key === 'POST /api/transfers').map((c) => c.headers['Idempotency-Key']);
    expect(keys[0]).toEqual(keys[1]);
  });

  it('lists transfers', async () => {
    mockApi({ 'GET /api/transfers': [row({ status: 'Rejected', reasons: [], rejectionReason: 'Insufficient funds.' })] });
    renderAt('/transfers', customer);
    expect(await screen.findByText('Insufficient funds.')).toBeInTheDocument();
    expect(screen.getByText('PKR 15,000.00')).toBeInTheDocument();
  });
});

describe('back office', () => {
  it('reviews transfers, shows the trial balance and rebuilds projections', async () => {
    const user = userEvent.setup();
    let queue = [row(), row({ id: 't2', memo: 'Car' })];
    const calls = mockApi({
      'GET /api/admin/reviews': () => ({ body: queue }),
      'GET /api/admin/trial-balance': { lines: [{ currency: 'PKR', kind: 'external', total: -900 }, { currency: 'PKR', kind: 'wallet', total: 900 }, { currency: 'PKR', kind: 'total', total: 0 }], balanced: true, walletsChecked: 3, mismatches: [] },
      'GET /api/admin/events': [{ position: 1, streamId: 'account-w1', version: 0, type: 'AccountOpened', createdAt: '', data: {} }],
      'POST /api/admin/transfers/t1/approve': () => { queue = queue.slice(1); return { body: { result: {}, position: 20 } }; },
      'POST /api/admin/transfers/t2/reject': { status: 409, body: { title: "A Completed transfer can't be rejected." } },
      'POST /api/admin/projections/rebuild': { replayed: 42 },
    });
    renderAt('/admin', admin);
    expect(await screen.findByText('Review queue (2)')).toBeInTheDocument();
    expect(screen.getByText(/all 3 wallets match/)).toBeInTheDocument();
    expect(screen.getByText('account-w1 v0')).toBeInTheDocument();

    await user.click(screen.getAllByText('Approve')[0]);
    expect(await screen.findByText('Approved PKR 15,000.00')).toBeInTheDocument();
    expect(await screen.findByText('Review queue (1)')).toBeInTheDocument();
    await waitFor(() => expect(calls.filter((c) => c.key === 'GET /api/admin/reviews').at(-1)!.url).toContain('after=20'));
    await user.click(screen.getByText('Reject'));
    expect(await screen.findByRole('alert')).toHaveTextContent("can't be rejected");
    await user.click(screen.getByText('Rebuild projections'));
    expect(await screen.findByText(/rebuilt from the event log/)).toBeInTheDocument();
  });

  it('flags an unbalanced ledger and hides the back office from customers', async () => {
    mockApi({
      'GET /api/admin/reviews': [], 'GET /api/admin/events': [],
      'GET /api/admin/trial-balance': { lines: [], balanced: false, walletsChecked: 1, mismatches: ['LP0000000009'] },
      'GET /api/accounts': [],
    });
    renderAt('/admin', admin);
    expect(await screen.findByText('Out of balance: LP0000000009')).toBeInTheDocument();
    expect(screen.getByText('Nothing here.')).toBeInTheDocument();
  });

  it('redirects customers away from the back office', async () => {
    mockApi({ 'GET /api/accounts': [] });
    renderAt('/admin', customer);
    expect(await screen.findByText('Your wallets')).toBeInTheDocument();
    expect(screen.queryByText('Back office')).not.toBeInTheDocument();
  });
});

describe('api client', () => {
  it('maps errors, tracks positions and tolerates empty bodies', async () => {
    mockApi({ 'GET /a': { status: 401 }, 'GET /b': { status: 409, body: { title: 'Nope' } }, 'GET /c': { status: 204 }, 'GET /d': { status: 200, body: { result: 1, position: 33 } } });
    await expect(request('/a')).rejects.toMatchObject({ status: 401, message: 'Please sign in.' });
    await expect(request('/b')).rejects.toEqual(new ApiError(409, 'Nope'));
    await expect(request('/c')).resolves.toBeUndefined();
    await request('/d');
    expect(positions.get()).toBe(33);
    localStorage.setItem('ledgerpay.session', '{bad');
    expect(session.get()).toBeNull();
  });
});
