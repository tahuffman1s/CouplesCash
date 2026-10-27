-- The bank feed's own category for a transaction (Plaid's personal_finance_category: detailed code such as
-- FOOD_AND_DRINK_COFFEE and its confidence VERY_HIGH | HIGH | MEDIUM | LOW | UNKNOWN). Used to suggest — and, when Plaid is very
-- sure, to apply — one of the household's categories. NULL for SimpleFIN / CSV / added rows.
ALTER TABLE imported_transactions ADD COLUMN provider_category text NULL;
ALTER TABLE imported_transactions ADD COLUMN provider_category_confidence text NULL;
