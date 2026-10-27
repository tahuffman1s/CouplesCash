-- Transactions added by hand (the "+" sheet): a charge the bank feed has not shown yet. They sit on an account with
-- source 'added', count in the plan until the bank's own row arrives, and are then hidden (ignored / superseded) with
-- replaced_by pointing at that bank row.
ALTER TABLE imported_transactions DROP CONSTRAINT IF EXISTS imported_transactions_source_check;
ALTER TABLE imported_transactions
    ADD CONSTRAINT imported_transactions_source_check CHECK (source IN ('simplefin', 'csv', 'added'));

ALTER TABLE imported_transactions
    ADD COLUMN replaced_by uuid NULL REFERENCES imported_transactions (id) ON DELETE SET NULL;

CREATE INDEX imported_transactions_added_open_idx ON imported_transactions (household_id, account_id)
    WHERE source = 'added' AND ignore_reason IS NULL;
