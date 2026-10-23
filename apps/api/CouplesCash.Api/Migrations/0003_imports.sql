-- 0003_imports.sql — CouplesCash v3 (PLAN-V3.md "Data model"). Append-only: 0001/0002 are never edited.
-- Bank links (SimpleFIN), external accounts, imported transactions (the review inbox), payee rules, CSV imports.

-- ---------------------------------------------------------------------------
-- Occurrence payments remember where they came from: a user tap, the autopay scheduler, or a bank transaction
-- (inbox confirm or auto-match). `auto` keeps its v2 meaning (no human actor).
-- ---------------------------------------------------------------------------
ALTER TABLE occurrence_payments
    ADD COLUMN IF NOT EXISTS source text NOT NULL DEFAULT 'manual';

ALTER TABLE occurrence_payments
    ADD CONSTRAINT occurrence_payments_source_check CHECK (source IN ('manual', 'autopay', 'bank'));

UPDATE occurrence_payments SET source = 'autopay' WHERE auto AND source = 'manual';

-- ---------------------------------------------------------------------------
-- Bank links: one SimpleFIN access URL per link, stored AES-256-GCM encrypted (Security__DataKey).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bank_links (
    id                  uuid        PRIMARY KEY,
    household_id        uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    provider            text        NOT NULL DEFAULT 'simplefin' CHECK (provider IN ('simplefin')),
    name                text        NOT NULL,
    access_url_enc      bytea       NOT NULL,
    created_by          uuid        NOT NULL REFERENCES users (id),
    created_at          timestamptz NOT NULL DEFAULT now(),
    last_synced_at      timestamptz NULL,
    last_manual_sync_at timestamptz NULL,
    last_attempt_at     timestamptz NULL,
    last_error          text        NULL,
    bridge_errors       text[]      NOT NULL DEFAULT '{}',
    sync_enabled        boolean     NOT NULL DEFAULT true
);

CREATE INDEX IF NOT EXISTS bank_links_household_id_idx ON bank_links (household_id);
CREATE INDEX IF NOT EXISTS bank_links_created_by_idx   ON bank_links (created_by);
CREATE INDEX IF NOT EXISTS bank_links_due_idx          ON bank_links (last_synced_at) WHERE sync_enabled;

-- ---------------------------------------------------------------------------
-- External accounts as the Bridge reports them; account_id is the mapping to a CouplesCash account.
-- balance_cents is the raw Bridge value (credit/loan: negative when owed); the mapped account gets
-- the sign-converted "amount owed" on sync.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS external_accounts (
    id              uuid        PRIMARY KEY,
    bank_link_id    uuid        NOT NULL REFERENCES bank_links (id) ON DELETE CASCADE,
    household_id    uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    external_id     text        NOT NULL,
    org_name        text        NOT NULL DEFAULT '',
    name            text        NOT NULL,
    currency        text        NOT NULL DEFAULT 'USD',
    balance_cents   bigint      NOT NULL DEFAULT 0,
    available_cents bigint      NULL,
    balance_at      timestamptz NOT NULL,
    account_id      uuid        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    updated_at      timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT external_accounts_link_external_key UNIQUE (bank_link_id, external_id)
);

CREATE INDEX IF NOT EXISTS external_accounts_household_id_idx ON external_accounts (household_id);
CREATE INDEX IF NOT EXISTS external_accounts_account_id_idx   ON external_accounts (account_id);

-- ---------------------------------------------------------------------------
-- Imported transactions = the review inbox. amount_cents is signed (negative = money out).
-- fingerprint = sha256(source || scope || external_id) when the source gives an id, else
-- sha256(account_id || posted_on || amount_cents || normalise(description)).
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS imported_transactions (
    id                    uuid        PRIMARY KEY,
    household_id          uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    source                text        NOT NULL CHECK (source IN ('simplefin', 'csv')),
    external_account_id   uuid        NULL REFERENCES external_accounts (id) ON DELETE SET NULL,
    account_id            uuid        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    external_id           text        NULL,
    fingerprint           bytea       NOT NULL,
    posted_on             date        NOT NULL,
    amount_cents          bigint      NOT NULL,
    description           text        NOT NULL,
    payee                 text        NULL,
    memo                  text        NULL,
    pending               boolean     NOT NULL DEFAULT false,
    status                text        NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'confirmed', 'ignored')),
    suggested_category_id uuid        NULL REFERENCES categories (id) ON DELETE SET NULL,
    suggested_item_id     uuid        NULL REFERENCES recurring_items (id) ON DELETE SET NULL,
    suggested_due_date    date        NULL,
    suggested_transfer    boolean     NOT NULL DEFAULT false,
    confidence            smallint    NOT NULL DEFAULT 0 CHECK (confidence BETWEEN 0 AND 100),
    confirmed_expense_id  uuid        NULL REFERENCES expenses (id) ON DELETE SET NULL,
    confirmed_item_id     uuid        NULL REFERENCES recurring_items (id) ON DELETE SET NULL,
    confirmed_due_date    date        NULL,
    confirmed_auto        boolean     NOT NULL DEFAULT false,
    reviewed_by           uuid        NULL REFERENCES users (id) ON DELETE SET NULL,
    reviewed_at           timestamptz NULL,
    created_at            timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT imported_transactions_fingerprint_key UNIQUE (household_id, fingerprint)
);

CREATE INDEX IF NOT EXISTS imported_transactions_household_posted_idx ON imported_transactions (household_id, posted_on DESC, id DESC);
CREATE INDEX IF NOT EXISTS imported_transactions_pending_idx          ON imported_transactions (household_id, posted_on DESC, id DESC) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS imported_transactions_external_account_idx ON imported_transactions (external_account_id);
CREATE INDEX IF NOT EXISTS imported_transactions_account_id_idx       ON imported_transactions (account_id);
CREATE INDEX IF NOT EXISTS imported_transactions_suggested_cat_idx    ON imported_transactions (suggested_category_id);
CREATE INDEX IF NOT EXISTS imported_transactions_suggested_item_idx   ON imported_transactions (suggested_item_id);
CREATE INDEX IF NOT EXISTS imported_transactions_confirmed_exp_idx    ON imported_transactions (confirmed_expense_id);
CREATE INDEX IF NOT EXISTS imported_transactions_confirmed_item_idx   ON imported_transactions (confirmed_item_id, confirmed_due_date);
CREATE INDEX IF NOT EXISTS imported_transactions_reviewed_by_idx      ON imported_transactions (reviewed_by);

-- ---------------------------------------------------------------------------
-- Learned payee rules: normalised payee → category and/or recurring item.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS payee_rules (
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    payee_norm   text        NOT NULL,
    category_id  uuid        NULL REFERENCES categories (id) ON DELETE SET NULL,
    item_id      uuid        NULL REFERENCES recurring_items (id) ON DELETE SET NULL,
    hits         integer     NOT NULL DEFAULT 1,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (household_id, payee_norm)
);

CREATE INDEX IF NOT EXISTS payee_rules_category_id_idx ON payee_rules (category_id);
CREATE INDEX IF NOT EXISTS payee_rules_item_id_idx     ON payee_rules (item_id);

-- ---------------------------------------------------------------------------
-- CSV import history
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS csv_imports (
    id              uuid        PRIMARY KEY,
    household_id    uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    account_id      uuid        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    filename        text        NOT NULL,
    row_count       integer     NOT NULL,
    imported_count  integer     NOT NULL,
    duplicate_count integer     NOT NULL,
    created_by      uuid        NOT NULL REFERENCES users (id),
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS csv_imports_household_created_idx ON csv_imports (household_id, created_at DESC);
CREATE INDEX IF NOT EXISTS csv_imports_account_id_idx        ON csv_imports (account_id);
CREATE INDEX IF NOT EXISTS csv_imports_created_by_idx        ON csv_imports (created_by);
