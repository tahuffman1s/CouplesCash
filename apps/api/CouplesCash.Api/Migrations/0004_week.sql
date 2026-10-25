-- 0004_week.sql — CouplesCash v4 (PLAN-V4.md "Data model"). Append-only: 0001–0003 are never edited.
-- Weekly budgets (a second budget period per category), the household's weekly spending target, payee hints on
-- planned one-offs, the weekly-summary notification preference and its idempotency log.

-- ---------------------------------------------------------------------------
-- Budgets: one row per (category, period). Existing rows are the v2 monthly budgets.
-- ---------------------------------------------------------------------------
ALTER TABLE budgets
    ADD COLUMN IF NOT EXISTS period text NOT NULL DEFAULT 'monthly';

ALTER TABLE budgets
    ADD CONSTRAINT budgets_period_check CHECK (period IN ('monthly', 'weekly'));

ALTER TABLE budgets DROP CONSTRAINT budgets_pkey;
ALTER TABLE budgets ADD PRIMARY KEY (household_id, category_id, period);

-- ---------------------------------------------------------------------------
-- Household: overall weekly spending target (null = none).
-- ---------------------------------------------------------------------------
ALTER TABLE households
    ADD COLUMN IF NOT EXISTS weekly_spend_cents bigint NULL;

ALTER TABLE households
    ADD CONSTRAINT households_weekly_spend_cents_check CHECK (weekly_spend_cents IS NULL OR weekly_spend_cents >= 0);

-- ---------------------------------------------------------------------------
-- Recurring items: "Bank shows it as" hint. Saving it seeds payee_rules(payee_norm(hint), item_id) so the first
-- matching bank transaction auto-applies; clearing it (or archiving the item) removes that rule.
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS payee_hint text NULL;

-- ---------------------------------------------------------------------------
-- Week summaries: per-user opt-out plus once-per-(user, week, kind) log.
-- ---------------------------------------------------------------------------
ALTER TABLE notification_prefs
    ADD COLUMN IF NOT EXISTS week_summary_enabled boolean NOT NULL DEFAULT true;

CREATE TABLE IF NOT EXISTS week_summary_log (
    user_id    uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    week_start date        NOT NULL,
    kind       text        NOT NULL CHECK (kind IN ('ahead', 'left')),
    sent_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (user_id, week_start, kind)
);
