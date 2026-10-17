-- Reminder frequency (per user default + per bill) and recurring bills. Additive: safe while the old app runs.
-- remind_every_days: 0 = never, 1 = daily (the old behaviour), 3, 7.

ALTER TABLE users ADD COLUMN remind_every_days INT NOT NULL DEFAULT 1;
ALTER TABLE bills ADD COLUMN remind_every_days INT NOT NULL DEFAULT 1;

CREATE TABLE recurring_bills (
  id             BIGINT AUTO_INCREMENT PRIMARY KEY,
  creator_id     BIGINT       NOT NULL,
  group_id       BIGINT       NULL,
  dest           VARCHAR(2)   NOT NULL DEFAULT 'G',
  title          VARCHAR(255) NOT NULL,
  amount_satang  BIGINT       NOT NULL,
  per_person     TINYINT(1)   NOT NULL DEFAULT 0,
  user_ids_json  TEXT         NOT NULL,
  amounts_json   TEXT         NULL,
  frequency      VARCHAR(8)   NOT NULL,        -- WEEKLY | MONTHLY
  anchor_day     INT          NOT NULL DEFAULT 1,
  next_run_on    DATE         NOT NULL,
  active         TINYINT(1)   NOT NULL DEFAULT 1,
  last_run_on    DATE         NULL,
  last_error     VARCHAR(255) NULL,
  created_at     DATETIME(3)  NOT NULL,
  KEY ix_recurring_due (active, next_run_on),
  CONSTRAINT fk_rec_creator FOREIGN KEY (creator_id) REFERENCES users(id),
  CONSTRAINT fk_rec_group   FOREIGN KEY (group_id)   REFERENCES `groups`(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
