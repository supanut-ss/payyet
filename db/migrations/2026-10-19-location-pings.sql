-- OwnTracks family location tracking (POST /owntracks). Additive: safe while the old app runs.

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
