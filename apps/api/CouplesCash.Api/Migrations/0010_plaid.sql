-- Plaid as a second bank provider next to SimpleFIN. The access token is stored encrypted in access_url_enc (the column holds
-- "the credential" of either provider). refresh_until: after linking, reconnecting or "Sync now" the scheduler checks the link
-- every few minutes until then (Plaid fetches from the bank in the background). needs_reconnect: the bank wants the user to
-- sign in again through Plaid Link (update mode).
ALTER TABLE bank_links DROP CONSTRAINT IF EXISTS bank_links_provider_check;
ALTER TABLE bank_links ADD CONSTRAINT bank_links_provider_check CHECK (provider IN ('simplefin', 'plaid'));
ALTER TABLE bank_links ADD COLUMN plaid_item_id text NULL;
ALTER TABLE bank_links ADD COLUMN refresh_until timestamptz NULL;
ALTER TABLE bank_links ADD COLUMN needs_reconnect boolean NOT NULL DEFAULT false;

ALTER TABLE imported_transactions DROP CONSTRAINT IF EXISTS imported_transactions_source_check;
ALTER TABLE imported_transactions
    ADD CONSTRAINT imported_transactions_source_check CHECK (source IN ('simplefin', 'csv', 'added', 'plaid'));
