-- Profile / group pictures shown in the people-picker page (LINE profile and group summary URLs).
-- NULL = not fetched yet, '' = fetched but the user/group has no picture. Additive: safe while the old app runs.

ALTER TABLE users ADD COLUMN picture_url VARCHAR(512) NULL;
ALTER TABLE `groups` ADD COLUMN picture_url VARCHAR(512) NULL;
