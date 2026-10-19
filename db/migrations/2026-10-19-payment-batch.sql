-- One slip can pay several bills of the same collector: its payments share a batch_id and are approved or rejected together.
-- Additive: safe while the old app runs.

ALTER TABLE payments ADD COLUMN batch_id VARCHAR(32) NULL;
ALTER TABLE payments ADD KEY ix_pay_batch (batch_id);
