-- Write side: the append-only event log. Nothing here is ever updated or deleted.
CREATE TABLE IF NOT EXISTS events (
    position    bigserial   PRIMARY KEY,
    tx_id       xid8        NOT NULL DEFAULT pg_current_xact_id(),
    stream_id   text        NOT NULL,
    version     int         NOT NULL,
    type        text        NOT NULL,
    data        jsonb       NOT NULL,
    created_at  timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT events_stream_version UNIQUE (stream_id, version)
);
CREATE INDEX IF NOT EXISTS events_tx_position ON events (tx_id, position);

CREATE TABLE IF NOT EXISTS users (
    id            uuid PRIMARY KEY,
    email         text NOT NULL UNIQUE,
    full_name     text NOT NULL,
    password_hash text NOT NULL,
    role          text NOT NULL,
    created_at    timestamptz NOT NULL
);

-- Read side: disposable projections, rebuilt from the log at any time.
CREATE TABLE IF NOT EXISTS projection_checkpoint (
    name     text PRIMARY KEY,
    tx_id    xid8   NOT NULL,
    position bigint NOT NULL
);
INSERT INTO projection_checkpoint VALUES ('main', '0'::xid8, 0) ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS rm_accounts (
    id        uuid PRIMARY KEY,
    owner_id  uuid NOT NULL,
    number    text NOT NULL UNIQUE,
    name      text NOT NULL,
    currency  text NOT NULL,
    balance   bigint NOT NULL DEFAULT 0,
    frozen    boolean NOT NULL DEFAULT false,
    opened_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS rm_accounts_owner ON rm_accounts (owner_id);

CREATE TABLE IF NOT EXISTS rm_activity (
    id            bigserial PRIMARY KEY,
    account_id    uuid NOT NULL,
    position      bigint NOT NULL,
    kind          text NOT NULL,
    amount        bigint NOT NULL,
    balance_after bigint NOT NULL,
    description   text NOT NULL,
    at            timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS rm_activity_account ON rm_activity (account_id, id DESC);

CREATE TABLE IF NOT EXISTS rm_ledger (
    id       bigserial PRIMARY KEY,
    position bigint NOT NULL,
    account  text NOT NULL,
    currency text NOT NULL,
    amount   bigint NOT NULL,
    at       timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS rm_ledger_account ON rm_ledger (account);

CREATE TABLE IF NOT EXISTS rm_transfers (
    id               uuid PRIMARY KEY,
    requested_by     uuid NOT NULL,
    from_account     uuid NOT NULL,
    to_account       uuid NOT NULL,
    amount           bigint NOT NULL,
    currency         text NOT NULL,
    memo             text NOT NULL,
    status           text NOT NULL,
    reasons          jsonb NOT NULL DEFAULT '[]',
    score            int NOT NULL DEFAULT 0,
    rejection_reason text,
    requested_at     timestamptz NOT NULL,
    updated_at       timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS rm_transfers_status ON rm_transfers (status);
