-- v7 follow-up: the savings sweep moves to the last day of the pay week ("what is left on Thursday goes to savings on Thursday").
-- Rows frozen before held the previous week's leftover on payday; under the new rule they would read as this week's move, so they go
-- (their bank transfers stay, as ignored transfers). The sweep-day push gets its own idempotency kind.
DELETE FROM sweeps;

ALTER TABLE week_summary_log DROP CONSTRAINT IF EXISTS week_summary_log_kind_check;
ALTER TABLE week_summary_log
    ADD CONSTRAINT week_summary_log_kind_check CHECK (kind IN ('ahead', 'left', 'payday', 'sweep'));
