-- Pay_Yet schema for MySQL 5.7+/8.x and MariaDB 10.4+. Import once (Plesk → Databases → phpMyAdmin → Import).
-- Charset utf8mb4 for Thai text and emoji. All DATETIME values are UTC.

CREATE TABLE users (
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  line_user_id  VARCHAR(64)  NOT NULL,
  display_name  VARCHAR(255) NULL,
  picture_url   VARCHAR(512) NULL,               -- LINE profile picture; NULL = not fetched, '' = none
  promptpay_id  VARCHAR(20)  NULL,
  is_friend     TINYINT(1)   NOT NULL DEFAULT 0,
  last_group_id BIGINT       NULL,
  remind_every_days INT      NOT NULL DEFAULT 1,   -- default reminder frequency of this user's bills (0 = never)
  created_at    DATETIME(3)  NOT NULL,
  UNIQUE KEY ux_users_line (line_user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE `groups` (
  id               BIGINT AUTO_INCREMENT PRIMARY KEY,
  line_group_id    VARCHAR(64)  NOT NULL,
  name             VARCHAR(255) NULL,
  picture_url      VARCHAR(512) NULL,           -- LINE group picture; NULL = not fetched, '' = none
  member_count     INT          NULL,
  member_count_at  DATETIME(3)  NULL,
  active           TINYINT(1)   NOT NULL DEFAULT 1,
  UNIQUE KEY ux_groups_line (line_group_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE group_members (
  group_id      BIGINT      NOT NULL,
  user_id       BIGINT      NOT NULL,
  joined_at     DATETIME(3) NOT NULL,
  source        VARCHAR(16) NOT NULL,           -- LISTED | JOINED | SEEN
  last_seen_at  DATETIME(3) NULL,
  PRIMARY KEY (group_id, user_id),
  CONSTRAINT fk_gm_group FOREIGN KEY (group_id) REFERENCES `groups`(id),
  CONSTRAINT fk_gm_user  FOREIGN KEY (user_id)  REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE bills (
  id                BIGINT AUTO_INCREMENT PRIMARY KEY,
  token             VARCHAR(24)  NOT NULL,      -- public id used in LIFF links
  creator_id        BIGINT       NOT NULL,
  group_id          BIGINT       NULL,          -- NULL = individual bill
  title             VARCHAR(255) NOT NULL,
  due_at            DATETIME(3)  NULL,
  last_reminded_on  DATE         NULL,          -- Bangkok date of the last reminder
  remind_every_days INT          NOT NULL DEFAULT 1,   -- 0 = no reminders, 1 = daily, 3, 7
  status            VARCHAR(16)  NOT NULL DEFAULT 'OPEN',   -- OPEN | CLOSED | CANCELLED
  scope_key         VARCHAR(80)  NULL,          -- only while OPEN: 'G:<creator>:<group>' / 'P:<creator>:<debtor>'
  invite_code       VARCHAR(16)  NULL,
  created_at        DATETIME(3)  NOT NULL,
  UNIQUE KEY ux_bills_token (token),
  UNIQUE KEY ux_bills_scope (scope_key),
  UNIQUE KEY ux_bills_invite (invite_code),
  CONSTRAINT fk_bills_creator FOREIGN KEY (creator_id) REFERENCES users(id),
  CONSTRAINT fk_bills_group   FOREIGN KEY (group_id)   REFERENCES `groups`(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE bill_items (                       -- one row per debtor
  id             BIGINT AUTO_INCREMENT PRIMARY KEY,
  bill_id        BIGINT      NOT NULL,
  debtor_id      BIGINT      NULL,
  amount_satang  BIGINT      NOT NULL DEFAULT 0,
  paid_satang    BIGINT      NOT NULL DEFAULT 0,
  status         VARCHAR(16) NOT NULL DEFAULT 'PENDING', -- PENDING | SUBMITTED | PAID | REJECTED | WAIVED
  paid_at        DATETIME(3) NULL,
  UNIQUE KEY ux_items_bill_debtor (bill_id, debtor_id),
  CONSTRAINT fk_items_bill   FOREIGN KEY (bill_id)   REFERENCES bills(id),
  CONSTRAINT fk_items_debtor FOREIGN KEY (debtor_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE bill_charges (                     -- every "add money" action; many per bill
  id            BIGINT AUTO_INCREMENT PRIMARY KEY,
  bill_id       BIGINT       NOT NULL,
  title         VARCHAR(255) NOT NULL,
  total_satang  BIGINT       NOT NULL,
  split_mode    VARCHAR(16)  NOT NULL,          -- EQUAL | CUSTOM
  created_at    DATETIME(3)  NOT NULL,
  CONSTRAINT fk_charges_bill FOREIGN KEY (bill_id) REFERENCES bills(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE charge_shares (                    -- who owes how much for each charge
  charge_id      BIGINT NOT NULL,
  bill_item_id   BIGINT NOT NULL,
  amount_satang  BIGINT NOT NULL,
  PRIMARY KEY (charge_id, bill_item_id),
  CONSTRAINT fk_shares_charge FOREIGN KEY (charge_id)    REFERENCES bill_charges(id),
  CONSTRAINT fk_shares_item   FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE payments (
  id             BIGINT AUTO_INCREMENT PRIMARY KEY,
  bill_item_id   BIGINT       NOT NULL,
  slip_path      VARCHAR(255) NOT NULL,         -- relative to App_Data/slips
  slip_ref       VARCHAR(64)  NULL,
  batch_id       VARCHAR(32)  NULL,         -- payments made with the same slip (several bills at once)
  slip_hash      CHAR(64)     NOT NULL,         -- SHA-256, blocks identical re-uploads
  media_token    VARCHAR(48)  NOT NULL,         -- random token for the public slip image URL
  amount_satang  BIGINT       NOT NULL,
  verified_by    VARCHAR(16)  NULL,             -- AUTO | CREATOR
  status         VARCHAR(16)  NOT NULL DEFAULT 'SUBMITTED',  -- SUBMITTED | APPROVED | REJECTED
  created_at     DATETIME(3)  NOT NULL,
  UNIQUE KEY ux_pay_hash (slip_hash),
  UNIQUE KEY ux_pay_token (media_token),
  UNIQUE KEY ux_pay_ref (slip_ref),
  CONSTRAINT fk_pay_item FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE conversation_state (               -- wizard progress in the 1:1 chat
  user_id     BIGINT       NOT NULL PRIMARY KEY,
  flow        VARCHAR(32)  NOT NULL,            -- ASK_AMOUNT | GROUP_PICK | CONFIRM
  step        VARCHAR(32)  NOT NULL,
  data        LONGTEXT     NULL,                -- JSON text
  expires_at  DATETIME(3)  NOT NULL,
  CONSTRAINT fk_state_user FOREIGN KEY (user_id) REFERENCES users(id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE processed_events (                 -- webhook idempotency; purge rows older than 7 days
  webhook_event_id  VARCHAR(64) NOT NULL PRIMARY KEY,
  processed_at      DATETIME(3) NOT NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

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

-- OwnTracks family location tracking: see db/migrations/2026-10-19-location-pings.sql
CREATE TABLE location_pings (
  id           BIGINT AUTO_INCREMENT PRIMARY KEY,
  name         VARCHAR(64)  NOT NULL,
  device       VARCHAR(64)  NULL,
  lat          DOUBLE       NOT NULL,
  lon          DOUBLE       NOT NULL,
  accuracy     INT          NULL,
  battery      INT          NULL,
  recorded_at  DATETIME(3)  NOT NULL,
  received_at  DATETIME(3)  NOT NULL,
  KEY ix_ping_name_time (name, recorded_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
