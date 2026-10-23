-- 0002_v2.sql — CouplesCash v2 (PLAN-V2.md "Data model additions"). Append-only: 0001 is never edited.
-- Household settings, activity feed, expenses, budgets, push, to-pay, goals, passkeys, scheduler logs.

-- ---------------------------------------------------------------------------
-- Household settings
-- ---------------------------------------------------------------------------
ALTER TABLE households
    ADD COLUMN IF NOT EXISTS currency           text     NOT NULL DEFAULT 'USD',
    ADD COLUMN IF NOT EXISTS week_start         smallint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS timezone           text     NOT NULL DEFAULT 'UTC',
    ADD COLUMN IF NOT EXISTS default_account_id uuid     NULL REFERENCES accounts (id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS pay_period         text     NOT NULL DEFAULT 'monthly',
    ADD COLUMN IF NOT EXISTS pay_anchor_date    date     NULL;

ALTER TABLE households
    ADD CONSTRAINT households_currency_check   CHECK (currency ~ '^[A-Z]{3}$'),
    ADD CONSTRAINT households_week_start_check CHECK (week_start IN (0, 1)),
    ADD CONSTRAINT households_pay_period_check CHECK (pay_period IN ('weekly', 'biweekly', 'bimonthly', 'monthly'));

CREATE INDEX IF NOT EXISTS households_default_account_id_idx ON households (default_account_id);

-- ---------------------------------------------------------------------------
-- Autopay: scheduler payments have no actor
-- ---------------------------------------------------------------------------
ALTER TABLE occurrence_payments
    ADD COLUMN IF NOT EXISTS auto boolean NOT NULL DEFAULT false;

ALTER TABLE occurrence_payments
    ALTER COLUMN paid_by DROP NOT NULL;

-- Undo guard: un-marking an autopay occurrence records a skip so the scheduler does not re-pay it a
-- minute later. Marking the occurrence paid by hand removes the skip again.
CREATE TABLE IF NOT EXISTS autopay_skips (
    recurring_item_id uuid        NOT NULL REFERENCES recurring_items (id) ON DELETE CASCADE,
    due_date          date        NOT NULL,
    skipped_by        uuid        NULL REFERENCES users (id) ON DELETE SET NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (recurring_item_id, due_date)
);

CREATE INDEX IF NOT EXISTS autopay_skips_skipped_by_idx ON autopay_skips (skipped_by);

-- ---------------------------------------------------------------------------
-- Activity feed
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS activity_events (
    id           uuid        PRIMARY KEY,
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    actor_id     uuid        NULL REFERENCES users (id) ON DELETE SET NULL,
    kind         text        NOT NULL,
    entity_type  text        NOT NULL,
    entity_id    uuid        NULL,
    summary      text        NOT NULL,
    data         jsonb       NOT NULL DEFAULT '{}'::jsonb,
    created_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS activity_events_household_created_idx ON activity_events (household_id, created_at DESC, id DESC);
CREATE INDEX IF NOT EXISTS activity_events_actor_id_idx          ON activity_events (actor_id);

-- ---------------------------------------------------------------------------
-- One-off expenses
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS expenses (
    id           uuid        PRIMARY KEY,
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    name         text        NOT NULL,
    amount_cents bigint      NOT NULL CHECK (amount_cents > 0),
    category_id  uuid        NULL REFERENCES categories (id) ON DELETE SET NULL,
    account_id   uuid        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    spent_on     date        NOT NULL,
    note         text        NULL,
    deducted     boolean     NOT NULL DEFAULT false,
    created_by   uuid        NOT NULL REFERENCES users (id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS expenses_household_spent_on_idx ON expenses (household_id, spent_on DESC);
CREATE INDEX IF NOT EXISTS expenses_category_id_idx        ON expenses (category_id);
CREATE INDEX IF NOT EXISTS expenses_account_id_idx         ON expenses (account_id);
CREATE INDEX IF NOT EXISTS expenses_created_by_idx         ON expenses (created_by);

-- ---------------------------------------------------------------------------
-- Budgets (one monthly amount per category)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS budgets (
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    category_id  uuid        NOT NULL REFERENCES categories (id) ON DELETE CASCADE,
    amount_cents bigint      NOT NULL CHECK (amount_cents >= 0),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (household_id, category_id)
);

CREATE INDEX IF NOT EXISTS budgets_category_id_idx ON budgets (category_id);

-- ---------------------------------------------------------------------------
-- Web Push
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS push_subscriptions (
    id           uuid        PRIMARY KEY,
    user_id      uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    endpoint     text        NOT NULL,
    p256dh       text        NOT NULL,
    auth         text        NOT NULL,
    user_agent   text        NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_used_at timestamptz NULL,
    failures     integer     NOT NULL DEFAULT 0,
    CONSTRAINT push_subscriptions_endpoint_key UNIQUE (endpoint)
);

CREATE INDEX IF NOT EXISTS push_subscriptions_user_id_idx ON push_subscriptions (user_id);

CREATE TABLE IF NOT EXISTS notification_prefs (
    user_id           uuid        PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    reminders_enabled boolean     NOT NULL DEFAULT true,
    days_before       smallint[]  NOT NULL DEFAULT '{1}',
    hour_local        smallint    NOT NULL DEFAULT 9 CHECK (hour_local BETWEEN 0 AND 23),
    activity_enabled  boolean     NOT NULL DEFAULT true,
    updated_at        timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS reminder_log (
    recurring_item_id uuid        NOT NULL REFERENCES recurring_items (id) ON DELETE CASCADE,
    due_date          date        NOT NULL,
    user_id           uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    sent_at           timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (recurring_item_id, due_date, user_id)
);

CREATE INDEX IF NOT EXISTS reminder_log_user_id_idx ON reminder_log (user_id);

-- ---------------------------------------------------------------------------
-- To-pay checklist
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS todos (
    id           uuid        PRIMARY KEY,
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    title        text        NOT NULL,
    amount_cents bigint      NULL,
    due_date     date        NULL,
    sort_order   integer     NOT NULL DEFAULT 0,
    done_at      timestamptz NULL,
    done_by      uuid        NULL REFERENCES users (id) ON DELETE SET NULL,
    created_by   uuid        NOT NULL REFERENCES users (id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS todos_household_open_idx ON todos (household_id, sort_order) WHERE done_at IS NULL;
CREATE INDEX IF NOT EXISTS todos_household_done_idx ON todos (household_id, done_at DESC) WHERE done_at IS NOT NULL;
CREATE INDEX IF NOT EXISTS todos_done_by_idx        ON todos (done_by);
CREATE INDEX IF NOT EXISTS todos_created_by_idx     ON todos (created_by);

-- ---------------------------------------------------------------------------
-- Savings goals
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS savings_goals (
    id           uuid        PRIMARY KEY,
    household_id uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    name         text        NOT NULL,
    target_cents bigint      NOT NULL CHECK (target_cents > 0),
    target_date  date        NULL,
    account_id   uuid        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    saved_cents  bigint      NOT NULL DEFAULT 0,
    color        text        NOT NULL,
    icon         text        NOT NULL,
    created_by   uuid        NOT NULL REFERENCES users (id),
    created_at   timestamptz NOT NULL DEFAULT now(),
    updated_at   timestamptz NOT NULL DEFAULT now(),
    archived_at  timestamptz NULL
);

CREATE INDEX IF NOT EXISTS savings_goals_household_id_idx ON savings_goals (household_id);
CREATE INDEX IF NOT EXISTS savings_goals_account_id_idx   ON savings_goals (account_id);
CREATE INDEX IF NOT EXISTS savings_goals_created_by_idx   ON savings_goals (created_by);

CREATE TABLE IF NOT EXISTS goal_contributions (
    id             uuid        PRIMARY KEY,
    goal_id        uuid        NOT NULL REFERENCES savings_goals (id) ON DELETE CASCADE,
    amount_cents   bigint      NOT NULL CHECK (amount_cents <> 0),
    contributed_by uuid        NOT NULL REFERENCES users (id),
    created_at     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS goal_contributions_goal_idx           ON goal_contributions (goal_id, created_at);
CREATE INDEX IF NOT EXISTS goal_contributions_contributed_by_idx ON goal_contributions (contributed_by);

-- ---------------------------------------------------------------------------
-- Passkeys (WebAuthn)
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS webauthn_credentials (
    id            uuid        PRIMARY KEY,
    user_id       uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    credential_id bytea       NOT NULL,
    public_key    bytea       NOT NULL,
    sign_count    bigint      NOT NULL DEFAULT 0,
    aaguid        uuid        NULL,
    transports    text[]      NOT NULL DEFAULT '{}',
    name          text        NOT NULL,
    backed_up     boolean     NOT NULL DEFAULT false,
    created_at    timestamptz NOT NULL DEFAULT now(),
    last_used_at  timestamptz NULL,
    CONSTRAINT webauthn_credentials_credential_id_key UNIQUE (credential_id)
);

CREATE INDEX IF NOT EXISTS webauthn_credentials_user_id_idx ON webauthn_credentials (user_id);

CREATE TABLE IF NOT EXISTS webauthn_challenges (
    id         uuid        PRIMARY KEY,
    challenge  bytea       NOT NULL,
    user_id    uuid        NULL REFERENCES users (id) ON DELETE CASCADE,
    kind       text        NOT NULL CHECK (kind IN ('register', 'login')),
    options    jsonb       NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS webauthn_challenges_expires_at_idx ON webauthn_challenges (expires_at);
CREATE INDEX IF NOT EXISTS webauthn_challenges_user_id_idx    ON webauthn_challenges (user_id);
