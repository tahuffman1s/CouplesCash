-- 0005_transactions.sql — CouplesCash v5 (PLAN-V5.md "Data model"). Append-only: 0001–0004 are never edited.
-- Pending lifecycle (dropped / superseded authorizations, last seen), transfer pairs, learned transfer payees and the
-- household's "auto-ignore transfers" setting.

-- ---------------------------------------------------------------------------
-- Imported transactions: why an ignored row is ignored, its transfer peer, when the Bridge last reported it.
--   manual     = the user tapped Ignore (or bulk ignore)
--   transfer   = a transfer (pair via transfer_peer_id, or one-sided with a NULL peer)
--   dropped    = a pending authorization that fell off the bank's feed (hidden by default)
--   superseded = a pending row whose posted version arrived as a separate row (hidden by default)
-- ---------------------------------------------------------------------------
ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS ignore_reason text NULL;

ALTER TABLE imported_transactions
    ADD CONSTRAINT imported_transactions_ignore_reason_check
    CHECK (ignore_reason IS NULL OR ignore_reason IN ('manual', 'transfer', 'dropped', 'superseded'));

ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS transfer_peer_id uuid NULL REFERENCES imported_transactions (id) ON DELETE SET NULL;

ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS last_seen_at timestamptz NULL;

ALTER TABLE imported_transactions
    ADD COLUMN IF NOT EXISTS transfer_auto boolean NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS imported_transactions_account_posted_idx ON imported_transactions (household_id, account_id, posted_on DESC, id DESC);
CREATE INDEX IF NOT EXISTS imported_transactions_transfer_peer_idx  ON imported_transactions (transfer_peer_id);
CREATE INDEX IF NOT EXISTS imported_transactions_ext_pending_idx    ON imported_transactions (external_account_id, posted_on) WHERE pending;

-- Existing ignored rows were ignored by a person (v3 had no other way); transfer suggestions keep their suggestion flag.
UPDATE imported_transactions SET ignore_reason = 'manual' WHERE status = 'ignored' AND ignore_reason IS NULL;

-- ---------------------------------------------------------------------------
-- Payee rules: "this payee is a transfer" (learned from a one-sided Mark as transfer).
-- ---------------------------------------------------------------------------
ALTER TABLE payee_rules
    ADD COLUMN IF NOT EXISTS is_transfer boolean NOT NULL DEFAULT false;

-- ---------------------------------------------------------------------------
-- Household: ignore detected transfer pairs (and learned transfer payees) automatically.
-- ---------------------------------------------------------------------------
ALTER TABLE households
    ADD COLUMN IF NOT EXISTS auto_ignore_transfers boolean NOT NULL DEFAULT true;
