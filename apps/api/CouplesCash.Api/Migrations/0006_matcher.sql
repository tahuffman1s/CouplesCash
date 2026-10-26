-- 0006_matcher.sql — CouplesCash v6 (PLAN-V6.md "Data model"). Append-only: 0001–0005 are never edited.
-- Bank names per item (user-entered + learned), "amount varies", the household's auto-match level, the match score/detail
-- the fuzzy matcher (or the built-in fallback) produced for a transaction, and found-bill suggestions.

-- ---------------------------------------------------------------------------
-- Recurring items: "Bank shows it as" becomes a list. payee_hint (v4) stays readable for compatibility but is no longer
-- written; its value seeds bank_names. Learned names are appended when a match is confirmed (max 10, oldest evicted).
-- ---------------------------------------------------------------------------
ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS bank_names text[] NOT NULL DEFAULT '{}';

ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS bank_names_learned text[] NOT NULL DEFAULT '{}';

ALTER TABLE recurring_items
    ADD COLUMN IF NOT EXISTS amount_varies boolean NOT NULL DEFAULT false;

UPDATE recurring_items
SET bank_names = ARRAY[left(btrim(payee_hint), 80)]
WHERE payee_hint IS NOT NULL AND btrim(payee_hint) <> '' AND cardinality(bank_names) = 0;

-- ---------------------------------------------------------------------------
-- Household: off = suggestions only; bills = auto-apply bill/income/one-off matches and transfers; all = also auto-confirm
-- expenses whose payee strongly matches a learned category rule.
-- ---------------------------------------------------------------------------
ALTER TABLE households
    ADD COLUMN IF NOT EXISTS auto_match_level text NOT NULL DEFAULT 'all';

ALTER TABLE households
    ADD CONSTRAINT households_auto_match_level_check CHECK (auto_match_level IN ('off', 'bills', 'all'));

-- ---------------------------------------------------------------------------
-- Imported transactions: the score behind the stored suggestion and how it was reached
--   match_detail = { name, amount, date, reason, engine: 'fuzzy' | 'builtin' }
-- auto_blocked: a person sent the row back to review (unmatch / restore / mark unpaid), so automatic matching never applies
-- it again (suggestions still refresh).
-- ---------------------------------------------------------------------------
ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS match_score smallint NULL;

ALTER TABLE imported_transactions
    ADD CONSTRAINT imported_transactions_match_score_check CHECK (match_score IS NULL OR match_score BETWEEN 0 AND 100);

ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS match_detail jsonb NULL;

ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS auto_blocked boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------------
-- Found bills: regular payments the matcher detected in imported transactions ("We found 3 regular payments").
-- fingerprint = sha256(normalised bank name || '|' || frequency); a dismissed fingerprint is never suggested again.
-- ---------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS bill_suggestions (
    id                     uuid        PRIMARY KEY,
    household_id           uuid        NOT NULL REFERENCES households (id) ON DELETE CASCADE,
    name                   text        NOT NULL,
    kind                   text        NOT NULL CHECK (kind IN ('bill', 'subscription', 'income')),
    amount_cents           bigint      NOT NULL CHECK (amount_cents > 0),
    amount_varies          boolean     NOT NULL DEFAULT false,
    frequency              text        NOT NULL CHECK (frequency IN ('weekly', 'biweekly', 'monthly', 'quarterly', 'yearly', 'once')),
    next_due               date        NOT NULL,
    bank_name              text        NOT NULL,
    sample_transaction_ids uuid[]      NOT NULL DEFAULT '{}',
    last_seen              date        NULL,
    score                  smallint    NOT NULL CHECK (score BETWEEN 0 AND 100),
    status                 text        NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'accepted', 'dismissed')),
    item_id                uuid        NULL REFERENCES recurring_items (id) ON DELETE SET NULL,
    fingerprint            bytea       NOT NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT bill_suggestions_fingerprint_key UNIQUE (household_id, fingerprint)
);

CREATE INDEX IF NOT EXISTS bill_suggestions_household_status_idx ON bill_suggestions (household_id, status, score DESC);
CREATE INDEX IF NOT EXISTS bill_suggestions_item_id_idx          ON bill_suggestions (item_id);
