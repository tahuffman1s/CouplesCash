-- v7 "Our Friday, automated" (docs/PLAN-V7.md): account roles + owners, the payday-anchored pay week, recurring transfers,
-- the savings sweep, bank-feed diagnostics and the payday push log.

-- ---------------------------------------------------------------------------
-- Accounts: role (exactly one active 'bills' account per household) and owner (fun money).
-- ---------------------------------------------------------------------------
ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS role text NOT NULL DEFAULT 'other';

ALTER TABLE accounts
    ADD CONSTRAINT accounts_role_check CHECK (role IN ('bills', 'savings', 'fun', 'other'));

ALTER TABLE accounts
    ADD COLUMN IF NOT EXISTS owner_user_id uuid NULL REFERENCES users (id) ON DELETE SET NULL;

-- Archived accounts keep their role but never block a new bills account.
CREATE UNIQUE INDEX IF NOT EXISTS accounts_one_bills_per_household_idx ON accounts (household_id) WHERE role = 'bills' AND archived_at IS NULL;
CREATE INDEX IF NOT EXISTS accounts_owner_user_id_idx ON accounts (owner_user_id);

-- ---------------------------------------------------------------------------
-- Households: payday weekday (0 Sunday … 6 Saturday; default Friday). Replaces week_start for the week view.
-- ---------------------------------------------------------------------------
ALTER TABLE households
    ADD COLUMN IF NOT EXISTS payday_weekday smallint NOT NULL DEFAULT 5;

ALTER TABLE households
    ADD CONSTRAINT households_payday_weekday_check CHECK (payday_weekday BETWEEN 0 AND 6);

-- A weekly / biweekly pay period already names the payday: keep its weekday (Postgres DOW: 0 = Sunday, like .NET DayOfWeek).
UPDATE households
SET payday_weekday = EXTRACT(DOW FROM pay_anchor_date)::smallint
WHERE pay_period IN ('weekly', 'biweekly') AND pay_anchor_date IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Recurring transfers: kind 'transfer' with a from and a to account.
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS from_account_id uuid NULL REFERENCES accounts (id) ON DELETE SET NULL;

ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS to_account_id uuid NULL REFERENCES accounts (id) ON DELETE SET NULL;

ALTER TABLE recurring_items DROP CONSTRAINT IF EXISTS recurring_items_kind_check;

ALTER TABLE recurring_items
    ADD CONSTRAINT recurring_items_kind_check CHECK (kind IN ('bill', 'subscription', 'income', 'transfer'));

CREATE INDEX IF NOT EXISTS recurring_items_from_account_id_idx ON recurring_items (from_account_id);
CREATE INDEX IF NOT EXISTS recurring_items_to_account_id_idx   ON recurring_items (to_account_id);

-- ---------------------------------------------------------------------------
-- Savings sweep: one row per pay week. planned_cents = max(0, left_cents), frozen at payday 00:00 household time from the
-- previous pay week's left-at-end (left_cents keeps the raw, possibly negative, number). done_by NULL with done_at set = auto.
-- auto_blocked: a person unticked it, so the bank-feed detection leaves the week alone.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS sweeps (
    id             uuid        PRIMARY KEY,
    household_id   uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    week_start     date        NOT NULL,
    planned_cents  bigint      NOT NULL CHECK (planned_cents >= 0),
    left_cents     bigint      NOT NULL,
    actual_cents   bigint      NULL CHECK (actual_cents IS NULL OR actual_cents >= 0),
    done_at        timestamptz NULL,
    done_by        uuid        NULL REFERENCES users (id) ON DELETE SET NULL,
    transaction_id uuid        NULL REFERENCES imported_transactions (id) ON DELETE SET NULL,
    auto_blocked   boolean     NOT NULL DEFAULT false,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT sweeps_household_week_key UNIQUE (household_id, week_start)
);

CREATE INDEX IF NOT EXISTS sweeps_done_by_idx        ON sweeps (done_by);
CREATE INDEX IF NOT EXISTS sweeps_transaction_id_idx ON sweeps (transaction_id);

-- ---------------------------------------------------------------------------
-- Feed diagnostics: what the last Bridge response contained per external account { pending, posted, availableReported, at }.
-- ---------------------------------------------------------------------------
ALTER TABLE external_accounts
    ADD COLUMN IF NOT EXISTS last_feed jsonb NULL;

-- ---------------------------------------------------------------------------
-- The payday push is logged like the v4 week summaries (once per user and pay week).
-- ---------------------------------------------------------------------------
ALTER TABLE week_summary_log DROP CONSTRAINT IF EXISTS week_summary_log_kind_check;

ALTER TABLE week_summary_log
    ADD CONSTRAINT week_summary_log_kind_check CHECK (kind IN ('ahead', 'left', 'payday'));

-- ---------------------------------------------------------------------------
-- Role backfill for existing households: the linked checking account with the most matched bill payments is the bills account
-- (ties → largest balance), savings accounts are savings; everything else stays 'other' (setup confirms the fun accounts).
-- ---------------------------------------------------------------------------
WITH ranked AS (
    SELECT a.id,
           row_number() OVER (
               PARTITION BY a.household_id
               ORDER BY (SELECT count(*)
                         FROM imported_transactions t
                         JOIN recurring_items i ON i.id = t.confirmed_item_id
                         WHERE t.household_id = a.household_id AND t.account_id = a.id AND t.status = 'confirmed'
                           AND i.kind IN ('bill', 'subscription')) DESC,
                        a.balance_cents DESC,
                        a.id) AS rn
    FROM accounts a
    WHERE a.archived_at IS NULL
      AND a.type = 'checking'
      AND EXISTS (SELECT 1 FROM external_accounts e WHERE e.account_id = a.id AND e.household_id = a.household_id)
)
UPDATE accounts
SET role = 'bills'
FROM ranked
WHERE accounts.id = ranked.id AND ranked.rn = 1;

UPDATE accounts SET role = 'savings' WHERE type = 'savings' AND role = 'other';
