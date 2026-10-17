# Pay_Yet — LINE Payment Collection Bot (System Design)

## 1. Goals

- Collect money from **individuals** or from **members of a LINE group**.
- **Creating / managing a collection is allowed only in the 1:1 chat with the bot** (keeps groups clean and prevents misuse).
- Automatic **reminders** until each debtor has paid.
- **Balance checks**: "what do I owe", "who still owes me", "status of this group's bill".
- Payment via **PromptPay QR** + **slip upload**, confirmed by the collector (optionally auto-verified).

Non-goals (v1): moving money on behalf of users, multi-currency, bank integrations beyond slip verification.

## 2. Architecture

```
LINE Platform ──webhook──▶ ASP.NET Core Web API (Plesk / IIS)
     ▲                         │  ├─ Webhook controller (signature check)
     │ reply / push            │  ├─ Event router (user / group / postback)
     └─────────────────────────┤  ├─ Conversation state machine (wizard)
                               │  ├─ Bill service / Payment service
                               │  └─ Notification service (LINE REST via HttpClient)
                               ▼
                         MySQL 8 ◀── Plesk Scheduled Task (hourly)
                               ▲        └─ POST /jobs/reminders (secret key) → push
                    App_Data/slips (local disk, not web-accessible)
                    Slip-verify API (optional: SlipOK / EasySlip)
LIFF pages: served by the same app under /liff/* (pay page: QR + slip upload; later: bill dashboard)
```

| Component | Choice | Note |
|---|---|---|
| Hosting model | **Single host**: one ASP.NET Core app on one Plesk site / domain | webhook, jobs, media, LIFF pages in one process |
| Runtime | ASP.NET Core Web API (.NET 8 LTS or newer), **self-contained publish** | runtime ships with the app; host .NET version does not matter |
| LINE client | thin wrapper over `HttpClient` (`IHttpClientFactory`) | no official .NET SDK; Messaging API is plain REST |
| ORM | EF Core + `Pomelo.EntityFrameworkCore.MySql` | migrations via `dotnet ef` |
| DB | MySQL 8, `utf8mb4` | money stored as integer satang (`BIGINT`) |
| Scheduler | Plesk Scheduled Task → "Fetch a URL" | IIS idle timeout / app-pool recycle kills in-process timers |
| QR | `QRCoder` | PNG served from `GET /qr/{token}.png` |
| Storage | local disk `App_Data/slips` | outside `wwwroot` |
| LIFF | LIFF app under a **LINE Login** channel (free; works with an unverified OA), served by the same app | pay page: QR + slip upload |
| HTTPS | Plesk Let's Encrypt | required for webhook and LIFF |

## 3. Chat-context rules

| Context (`source.type`) | Allowed |
|---|---|
| `user` (1:1) | create / edit / cancel bills, confirm or reject payments, view all balances, settings |
| `group` | `ยอด` (group bill status), "จ่ายเงิน" button on the bill card → opens the **LIFF pay page** (QR + slip upload), so nothing else is posted in the group; no manual registration needed; any create command → reply "please create bills in private chat with the bot" |

## 4. Key flows

### 4.1 Register users and groups
- `follow` event → upsert `users` (displayName, picture).
- Bot `join` group → upsert `groups` and **auto-register every member**, no button needed:
  - `getGroupMemberIds` (paged) + `getGroupMemberProfile` for each → upsert `users` (`is_friend = 0` if they never added the bot) and `group_members`.
  - `memberJoined` / `memberLeft` events keep the list in sync; any message in the group also upserts its sender.
- **Unverified OA (current case)**: `getGroupMemberIds` is not available (verified / premium only), so the bot **learns members passively** instead. Config flag `Line:CanListGroupMembers = false` skips the listing call; the rest is identical.
  - **Sources** (all upsert `users` + `group_members`, profile via `getGroupMemberProfile`, which works for any member without friendship): `join` (the person who invited the bot, when LINE provides `source.userId`), `memberJoined`, **any event with a `source.userId` in the group** — messages, images, stickers, postbacks — and the "จ่ายเงิน" button press.
  - `memberLeft` removes the member.
  - **Coverage tracking**: `getGroupMembersCount` (works for all accounts) is stored in `groups.member_count`; known members = `COUNT(group_members)`. Coverage = known / count.
  - **Greeting on join**: bot posts once, "ให้ทุกคนพิมพ์อะไรก็ได้ 1 ครั้งเพื่อให้บอทรู้จัก" — a nudge only, not a registration step. Silent members stay unknown until they speak, join, or press a button.
  - **At bill creation** (§4.2 step 4) the collector sees "รู้จักแล้ว 7/10 คน" and, if coverage < 100 %, an option to post the nudge in the group again. Unknown members cannot be selected as debtors.
  - **Adding late-discovered members**: once someone is known, the collector can add them to the open bill through a new charge (§4.2.1). Equal-split amounts of existing items are never recalculated automatically, so nobody's amount changes without the collector's action.
  - Tip: most groups reach near-full coverage within a day or two of normal chatting; applying for OA verification later switches to full listing with no other change.
- Members do not need to be friends with the bot for group bills (everything happens in the group). Friendship is only needed for **individual** bills (private push).

### 4.2 Create a bill — quick entry (1:1 chat)
Goal: **one message + one tap**. Only the amount is required; everything else has a default.

The collector types one line (`เก็บ` or `เก็บเงิน`). With the rich-menu button "เก็บเงิน" the bot asks just one question: "ยอดเท่าไหร่?".

| Collector types | Meaning |
|---|---|
| `เก็บ ค่าอาหาร 200` | **เก็บ = per person**: 200 for each person picked (usually one) → the bot opens the picker page |
| `หาร ค่าเครื่องดื่ม 300` | **หาร = split**: 300 divided by the number of people picked → picker page |
| `หาร 600 ค่าข้าว ทุกคน` | split among everyone in the group, no picker |
| `หาร 600 ยกเว้น สมชาย` | split among everyone except สมชาย |
| `เก็บ 200 สมชาย` | names typed → no picker; private bill to one person |
| `เก็บ สมชาย 200 มาลี 300` | custom amount per person |
| `หาร 500 กลุ่มเพื่อน ...` | choose the group by name (counts as "everyone in that group") |

**Picker page** (`liff/pay?mode=pick`, used when the command names nobody): the bot replies with a button; the LIFF page shows the collector's group members with checkboxes (search, "select whole group", live summary such as "3 คน หารคนละประมาณ ฿100"), where to announce (a group that contains everyone picked, or private messages). `POST /api/pay/pick` re-validates every id on the server (only people from the collector's groups), stores the confirm draft and pushes the confirm card to the chat.

- **Defaults**: group = the only group the collector shares with the bot, else the last one used, else one Quick Reply tap with group names. Debtors = all **known** members except the collector. Title = "ยอดเรียกเก็บ <วันที่>". No due date (daily 10:00 reminders run anyway; a due date can be set later from a button on the card).
- **Destination rule**: names only → individual (private) bill; no names or a group name → group bill.
- **Parsing**: simple rules, no AI needed — amount (accepts commas, `บาท`, `฿`, Thai digits), keywords `คนละ` / `ยกเว้น`, and names fuzzy-matched against display names of people who share a group with the collector. Ambiguous or unknown name → one Quick Reply with candidates. Unparseable text → bot replies with 2 example lines.
- **Confirmation card** (the only tap), always showing the computed result so mistakes are obvious:
  ```
  เก็บเงิน: ค่าข้าว
  500 บาท ÷ 5 คน = 100 บาท/คน
  กลุ่ม: เพื่อนสนิท (รู้จักแล้ว 5/6 คน)
  [ยืนยัน]  [ยกเลิก]
  ```
  The card shows coverage only when below 100 % (§4.1; with an unverified OA, offers "ให้บอทเรียกทุกคนพิมพ์ทักทาย").
- **Undo**: after confirming, the collector gets `[ยกเลิกรายการนี้]` for 10 minutes (removes the charge if nobody has paid it yet).

On confirm, the bot posts **one Flex card to the group** (items, each person's remaining amount, "จ่ายเงิน" button that opens the LIFF pay page, §4.4). **No private push** — group bills are announced and reminded in the group only.

### 4.2.1 Every bill stands alone, reminders group them
- Each time a collector bills a group or a person a **new bill** is created (one charge, one `bill_items` row per debtor). Nothing is merged into an earlier bill, so each bill has its own card, payments, history and undo. `bills.scope_key` is no longer used (older merged bills keep working).
- **Reminders are what group bills.** The daily job sends ONE card per group listing every unpaid bill ("ค้างอยู่ N บิล · รวม ฿X", each bill with collector and amount, and who owes N bills), and ONE private message per debtor for their private bills. A bill reminds on its own schedule (`remind_every_days`); when any bill of a group is due, all unpaid bills of the group are listed and marked as reminded.

### 4.3 Create an individual bill (1:1 chat)
- Type the debtor's name in quick entry (`เก็บ 200 สมชาย`), matched against people who share a group with the collector, **or**
- Bot generates an invite code / link (`https://line.me/R/oaMessage/@bot/?PAY-8F3K2`) that the collector sends to anyone; when the debtor opens it and sends the code, the bill is bound to them.
- Individual bills are announced and reminded **privately** to the debtor (requires `is_friend = 1`; if not, the collector is told the debtor must add the bot). Each charge is its own bill; the reminder lists everything the debtor owes (§4.2.1).

### 4.4 Pay
Payment happens on a **LIFF page**, not in the chat, so the group stays clean (no QR, no slip photos posted).

1. Debtor taps **"จ่ายเงิน"** on the card (a URI action: `https://liff.line.me/<liffId>?b=<billToken>`) → the LIFF page opens inside LINE.
2. The page identifies the user via LIFF (`liff.getIDToken()`); the server verifies it with LINE (`POST /oauth2/v2.1/verify`, `client_id` = Login channel ID) and takes `sub` as the LINE user ID. It shows: bill items, the user's **remaining** amount, a PromptPay QR (EMVCo payload, generated server-side) with the receiver's PromptPay ID as copyable text, and the user's payment history on this bill.
3. User pays in their banking app, returns, taps **"แนบสลิป"** (camera/gallery) and submits. The page resizes the image client-side (≤ 1600 px JPEG) and uploads it to `POST /api/pay/slip` (multipart, ID token in `Authorization`). Status → `SUBMITTED`; the page shows "รอผู้เก็บตรวจสอบ".
4. (Optional) Slip-verify API checks amount + receiver account → auto-approve if it matches, otherwise left for manual review.
5. Collector receives a **private** push with the slip image (served via a short-lived `/media/{token}`) → **[ยืนยัน] [ปฏิเสธ]** (the collector is always a friend of the bot).
6. On approval: `paid_satang += amount`; item is `PAID` when `paid ≥ total`. The debtor sees the result on the LIFF page (and gets a private push if `is_friend = 1`). **Nothing is posted to the group per payment**; the paid/unpaid state shows up in the next 10:00 digest and in `ยอด`. When everyone has paid, bill → `CLOSED` and one summary line is posted to the group.
7. Individual bills use the same LIFF page (button in the private chat). Sending a slip image directly in a 1:1 chat is still accepted as a fallback when the user has exactly one pending item.

Server-side checks on `POST /api/pay/slip`: the verified user must be the `debtor_id` of that bill item; item must be `PENDING`/`REJECTED`/partially paid; file ≤ 5 MB, JPEG/PNG by magic bytes (not by extension); SHA-256 stored in `payments.slip_hash` (unique) to block re-submitting the same image; rate limit per user.

### 4.4.0 App screens (LIFF)
One LIFF page, several screens selected by `?mode=`: `home` (hub: new bill, manage, what I owe, PromptPay setting), `new` (form), `manage` (§4.4.1), `owe`, plus `pick` (the people picker used by the typed commands). No parameters = `home`.
- **New bill form** — `GET /api/app/new` (groups, members, pictures, whether PromptPay is set) → choose the group → title, amount, "หารยอดรวม" (split a total) or "เก็บคนละ" (each pays), people, destination → `POST /api/app/create`. The server re-validates everyone picked, then `BillCreationService` creates and announces the bill exactly like the chat confirm button; the response carries undo handles (`POST /api/app/undo`, 10 minutes).
- **PromptPay** — `POST /api/app/promptpay` (collectors must set it before the first bill; the number is shown masked).
- **What I owe** — `GET /api/app/owe` lists open items for the signed-in user with the token that opens the pay page.
- Anyone who opens the app before ever messaging the bot is created from the LINE profile in the ID token.
Rich-menu buttons can point straight at `https://liff.line.me/<liffId>?mode=home|new|manage|owe`.

### 4.4.1 Managing a bill afterwards
`จัดการ` (private chat) opens the LIFF page `?mode=manage`: the collector's open bills with every charge and person, their state and what is still owed.
- **Cash received** — `POST /api/manage/paid {itemId, amount?}` adds an `APPROVED` payment (`verified_by = CASH`, no slip) and updates `paid_satang`; partial amounts are allowed. Refused while a slip is `SUBMITTED` for that person.
- **Waive** — the person's remaining debt is forgiven (`WAIVED`); the paid part stays.
- **Cancel one charge** — no time limit (the chat undo is 10 minutes): amounts shrink by each share; refused if someone has a slip waiting or paid more than the new amount. People who already paid enough become `PAID`. Cancelling the last charge cancels the bill.
- **Cancel the whole bill** — status `CANCELLED`, scope freed; money already received is not refunded by the bot.
Every action re-checks that the caller created the bill. Group bills announce the change in the group, private bills notify the affected debtors who are bot friends.

### 4.5 Reminders
- **Once a day at 10:00 (Asia/Bangkok)**, for every OPEN bill that still has unpaid items, starting the day after the bill was created.
- Plesk Scheduled Task calls `POST /jobs/reminders` (`X-Job-Key` header) **hourly**. The job does nothing before 10:00 Bangkok. After that, for each OPEN bill it atomically claims the day:
  `UPDATE bills SET last_reminded_on = <today Bangkok> WHERE id = ? AND (last_reminded_on IS NULL OR last_reminded_on < <today>)` — only if 1 row was updated does it send. This makes the job idempotent and lets a later run catch up if 10:00 was missed (server downtime).
- **Group bill** → one message in the **group**: "ยังไม่จ่าย" list with each person's remaining amount (+ ⚠️ overdue marker after `due_at`). One push per group, no private messages.
- **Individual bill** → one **private** push to the debtor.
- A new charge is announced in the group immediately (not delayed to 10:00). Individual payment results are **not** posted to the group (§4.4).
- Collector can still trigger **"ทวงตอนนี้"** manually (rate-limited, once per 6 h per bill).

### 4.6 Balance check
| Command | Where | Result |
|---|---|---|
| `ยอด` / `ยอดค้าง` | 1:1 | what I owe (grouped by collector) + what others owe me |
| `ยอด` | group | status of open bills in this group (paid ✅ / pending ⏳) |
| `ประวัติ` | 1:1 | last 10 settled bills |
| Rich menu | 1:1 | buttons: เก็บเงิน · ยอดของฉัน · บิลที่ฉันเก็บ · ตั้งค่า |

## 5. Data model

MySQL 8, InnoDB, `utf8mb4_unicode_ci` (Thai names + emoji). All `DATETIME` values are UTC.

```sql
CREATE TABLE users (
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  line_user_id  VARCHAR(64)  NOT NULL UNIQUE,
  display_name  VARCHAR(255),
  promptpay_id  VARCHAR(20),                 -- phone / national ID for receiving money
  is_friend     TINYINT(1)   NOT NULL DEFAULT 1,
  created_at    DATETIME(3)  NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
);

CREATE TABLE `groups` (                      -- GROUPS is a reserved word in MySQL 8
  id             BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  line_group_id  VARCHAR(64)  NOT NULL UNIQUE,
  name           VARCHAR(255),
  member_count   INT NULL,                         -- from getGroupMembersCount (refreshed on events, ≤ once/hour)
  active         TINYINT(1)   NOT NULL DEFAULT 1   -- 0 after bot leaves
);

CREATE TABLE group_members (
  group_id   BIGINT UNSIGNED NOT NULL,
  user_id    BIGINT UNSIGNED NOT NULL,
  joined_at  DATETIME(3) NOT NULL,                  -- first time the bot saw this member
  source     ENUM('LISTED','JOINED','SEEN') NOT NULL, -- how the bot learned of them
  last_seen_at DATETIME(3) NULL,
  PRIMARY KEY (group_id, user_id),
  FOREIGN KEY (group_id) REFERENCES `groups`(id),
  FOREIGN KEY (user_id)  REFERENCES users(id)
);

CREATE TABLE bills (
  id                      BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  creator_id              BIGINT UNSIGNED NOT NULL,
  group_id                BIGINT UNSIGNED NULL,          -- NULL = individual bill
  title                   VARCHAR(255) NOT NULL,         -- from the first charge; items live in bill_charges
  due_at                  DATETIME(3),
  last_reminded_on        DATE NULL,                     -- Bangkok date of the last daily reminder
  status                  ENUM('OPEN','CLOSED','CANCELLED') NOT NULL DEFAULT 'OPEN',
  scope_key               VARCHAR(80) NULL UNIQUE,       -- set only while OPEN: 'G:<creator>:<group>' or 'P:<creator>:<debtor>' → one open bill per scope; NULL when closed
  invite_code             VARCHAR(16) NULL UNIQUE,
  created_at              DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (creator_id) REFERENCES users(id),
  FOREIGN KEY (group_id)   REFERENCES `groups`(id)
);

CREATE TABLE bill_items (                    -- one row per debtor
  id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_id         BIGINT UNSIGNED NOT NULL,
  debtor_id       BIGINT UNSIGNED NULL,       -- NULL until invite code is claimed
  amount_satang   BIGINT NOT NULL,              -- sum of this debtor's charge_shares
  paid_satang     BIGINT NOT NULL DEFAULT 0,    -- remaining = amount_satang - paid_satang
  status          ENUM('PENDING','SUBMITTED','PAID','REJECTED','WAIVED') NOT NULL DEFAULT 'PENDING',
  paid_at         DATETIME(3),
  FOREIGN KEY (bill_id)   REFERENCES bills(id),
  FOREIGN KEY (debtor_id) REFERENCES users(id),
  UNIQUE KEY uq_item (bill_id, debtor_id)
);

CREATE TABLE bill_charges (                  -- each "add money" action; many per bill
  id            BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_id       BIGINT UNSIGNED NOT NULL,
  title         VARCHAR(255) NOT NULL,
  total_satang  BIGINT NOT NULL,
  split_mode    ENUM('EQUAL','CUSTOM') NOT NULL,
  created_at    DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (bill_id) REFERENCES bills(id)
);

CREATE TABLE charge_shares (                 -- who owes how much for each charge
  charge_id      BIGINT UNSIGNED NOT NULL,
  bill_item_id   BIGINT UNSIGNED NOT NULL,
  amount_satang  BIGINT NOT NULL,
  PRIMARY KEY (charge_id, bill_item_id),
  FOREIGN KEY (charge_id)    REFERENCES bill_charges(id),
  FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
);

CREATE TABLE payments (
  id             BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  bill_item_id   BIGINT UNSIGNED NOT NULL,
  slip_path      VARCHAR(255) NOT NULL,      -- relative path under App_Data/slips
  slip_ref       VARCHAR(64) NULL UNIQUE,    -- transRef from slip; blocks reused slips
  slip_hash      CHAR(64) NOT NULL UNIQUE,   -- SHA-256 of the image; blocks identical re-uploads
  amount_satang  BIGINT,
  verified_by    ENUM('AUTO','CREATOR'),
  status         ENUM('SUBMITTED','APPROVED','REJECTED') NOT NULL DEFAULT 'SUBMITTED',
  created_at     DATETIME(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3),
  FOREIGN KEY (bill_item_id) REFERENCES bill_items(id)
);

CREATE TABLE conversation_state (            -- wizard progress in 1:1 chat
  user_id     BIGINT UNSIGNED PRIMARY KEY,
  flow        VARCHAR(32) NOT NULL,          -- CREATE_BILL | SUBMIT_SLIP ...
  step        VARCHAR(32) NOT NULL,
  data        JSON,
  expires_at  DATETIME(3) NOT NULL,          -- reset after 30 min idle
  FOREIGN KEY (user_id) REFERENCES users(id)
);

CREATE TABLE processed_events (              -- idempotency; purge rows older than 7 days
  webhook_event_id VARCHAR(64) PRIMARY KEY,
  processed_at     DATETIME(3) NOT NULL
);
```

Internal IDs are auto-increment and appear in postback data, so every action re-checks ownership server-side (see §8). Public-facing tokens (invite codes, QR/slip URLs) are random, never sequential IDs.

Equal split (per charge): `floor(total / n)` per person, the remainder satang goes to the first debtors so the sum always equals the charge total.

Adding a charge runs in one transaction: lock the OPEN bill by `scope_key` → insert `bill_charges` + `charge_shares` → upsert each debtor's `bill_items` and add to `amount_satang` → reopen any `PAID` item whose `paid_satang < amount_satang` back to `PENDING`.

## 6. State machines

```
bill_item:  PENDING ──slip──▶ SUBMITTED ──approve──▶ PAID
               ▲                  │
               └──── REJECTED ◀───┘ reject (debtor may resend)
            PENDING/REJECTED ──collector waives──▶ WAIVED
            PAID ──new charge adds debt (paid < total)──▶ PENDING

bill:       OPEN ──all items PAID/WAIVED──▶ CLOSED (scope_key = NULL)
            OPEN ──collector cancels──────▶ CANCELLED (scope_key = NULL, notify where the bill lives)
            OPEN ──new charge──▶ OPEN (same bill, items recalculated)
```

## 7. LINE platform constraints

- **Push message quota**: free plan has a small monthly quota; a push to a group counts once per group member.
  → Prefer **reply** messages (free) whenever responding to a user event. Group bills are announced and reminded with **one group message per day**, so cost scales with the number of groups, not debtors. Check the member count of large groups against your plan's quota.
- Push to a user only works if they **added the bot as a friend** (`is_friend`, updated by `follow`/`unfollow`). Group bills avoid this entirely.
- `getGroupMemberIds` (list all members) requires a verified/premium account; unverified OAs use passive discovery with coverage tracking (see §4.1).
- Sent messages cannot be edited → post a new status card instead.
- Reply token is valid only for a short time → process quickly, do heavy work async.

## 8. Security & reliability

- Verify `X-Line-Signature` on every webhook.
- LIFF endpoints (`/api/pay/*`) accept only a valid LINE ID token (verified server-side against the Login channel); never trust a user ID sent in the request body or URL.
- Dedupe by `webhookEventId` (`processed_events`); LINE may redeliver.
- Authorization: only `bills.creator_id` can edit/cancel/approve; only `bill_items.debtor_id` can submit a slip.
- Postback data carries IDs only (`action=approve&payment=<id>`); always re-check permission and current status server-side.
- Reject duplicate slips via `slip_ref`; check amount and receiver when auto-verifying.
- Money in integer satang; all times stored UTC, displayed Asia/Bangkok.
- Slip images stored in `App_Data/slips` (not web-accessible). LINE image messages need a public HTTPS URL, so slips and QR images are served through `GET /media/{token}` with a random, short-lived token.
- `/jobs/*` endpoints require a secret `X-Job-Key` header; reject everything else.

## 9. Project structure (proposed)

```
PayYet.sln
src/PayYet.Api/
  Program.cs                       # DI, routing, config binding
  Controllers/
    LineWebhookController.cs       # POST /api/line/webhook
    JobsController.cs              # POST /jobs/reminders (X-Job-Key)
    MediaController.cs             # GET /media/{token} (slip images for the collector's push)
    PayController.cs               # GET /api/pay/{billToken}, POST /api/pay/slip (LIFF, ID-token auth)
  Line/
    LineMessagingClient.cs         # reply / push / profile / content download (HttpClient)
    LineSignatureValidator.cs      # HMAC-SHA256 of raw body vs X-Line-Signature
    Flex/                          # Flex message builders (bill card, QR, summary)
  Handlers/
    UserMessageHandler.cs          # 1:1 commands + wizard
    GroupMessageHandler.cs         # group commands
    PostbackHandler.cs             # button actions
    LifecycleHandler.cs            # follow/unfollow/join/leave/memberJoined
  Services/
    BillService.cs
    PaymentService.cs
    ReminderService.cs
    PromptPayPayload.cs            # EMVCo payload + CRC16
    SlipVerifyClient.cs            # optional external API
  Data/
    AppDbContext.cs
    Entities/
    Migrations/
  Pages/Liff/                      # LIFF pay page (Razor Pages + small JS using the LIFF SDK)
  wwwroot/                         # static assets for LIFF pages
tests/PayYet.Tests/
```

## 10. Plesk deployment notes

- **Single host**: everything runs in one app on one Plesk site, one domain, one deployment.
  - Routes: `/api/line/webhook`, `/jobs/*`, `/media/*`, `/liff/*` — same origin, so no CORS setup. Public URL = `https://api.drivetodev.online/...`, see "Hosting decision" below.
  - One IIS app pool = one instance, so in-memory state (`Channel<T>` queue, rate-limit cache) is safe. Do not enable web garden / multiple worker processes.
  - Anything that must survive a restart (wizard state, pending slip checks, reminder schedule) lives in MySQL, not memory.
  - If the bot ever needs more than one instance, move the queue and caches to MySQL first.

- Hosting: Plesk **Windows**. Build locally as **self-contained** and upload the output folder (FTP / Web Deploy / File Manager zip):
  ```bash
  dotnet publish src/PayYet.Api -c Release -r win-x64 --self-contained true -o publish
  ```
  - The .NET runtime is bundled, so the host only needs IIS with the **ASP.NET Core Module (ANCM)** — present on any Plesk that lists ASP.NET Core support. The generated `web.config` points at `PayYet.Api.exe`.
  - Do not use `PublishSingleFile` — keep the normal folder layout for IIS.
  - Use `win-x64`. **Checked on the host: the current app pool runs 32-bit** (`Is64BitProcess = False`), so either turn off "Enable 32-bit applications" for this domain's app pool (Plesk → IIS Application Pool) or publish `win-x86` instead. ASP.NET Core runtimes 8.0 / 9.0 / 10.0 are installed on the host, so a framework-dependent publish would also work; self-contained stays the default.
  - The app pool identity (`IWPD_289`) is shared by every site in the `thaipesleague.com` subscription, so other sites' code can read this app's files. Keep secrets in environment variables / `appsettings.Production.json` outside the web root if possible, and treat `App_Data/slips` as readable by those sites.
  - Output is ~70–100 MB; runtime security patches are applied by rebuilding with a newer SDK and redeploying.
  - To redeploy without file-lock errors, upload `app_offline.htm` first, replace files, then delete it.
- **Hosting decision:** the app first targeted `https://drivetodev.online/lineapi`, but IIS locks `system.webServer/handlers` for that site, so a `web.config` in a sub-folder cannot register the ASP.NET Core handler (HTTP 500.19, lock violation 0x80070021). It now runs on its own subdomain site, **`https://api.drivetodev.online`** (docroot folder `api.drivetodev.online`), where nothing else is hosted.
  - `App:PathBase` stays empty. `app.UsePathBase(...)` is still supported for a sub-folder layout, should the lock ever be lifted.
  - Never hard-code root-relative URLs for links sent to LINE; build them from `App:PublicBaseUrl = https://api.drivetodev.online`.
  - Public URLs: webhook `https://api.drivetodev.online/api/line/webhook`; LIFF endpoint `https://api.drivetodev.online/liff/pay`; reminder job `https://api.drivetodev.online/jobs/reminders?key=...`.
  - The subdomain needs its own TLS certificate (Plesk → api.drivetodev.online → SSL/TLS Certificates → Let's Encrypt); LINE rejects invalid certificates.
- Webhook URL: `https://api.drivetodev.online/api/line/webhook`; enable Let's Encrypt and "redirect HTTP → HTTPS" in Plesk.
- LIFF setup: create a **LINE Login** channel in the same provider, add a LIFF app (size Full, scope `openid profile`, endpoint `https://api.drivetodev.online/liff/pay`), and link the Login channel to the bot. Config: `Liff:Id`, `Line:LoginChannelId`.
- Secrets (`Line:ChannelSecret`, `Line:ChannelAccessToken`, `ConnectionStrings:Default`, `Jobs:Key`) go in `appsettings.Production.json` on the server or environment variables in `web.config` — never committed.
- MySQL: create the database and user in Plesk → Databases; connection string uses `utf8mb4`; app stores UTC and converts to Asia/Bangkok only for display.
- Reminders: Plesk → Scheduled Tasks → "Fetch a URL" `https://api.drivetodev.online/jobs/reminders` **hourly** (the job itself only sends after 10:00 Bangkok, once per bill per day — see §4.5). Do **not** rely on `BackgroundService` timers — the IIS app pool may idle-stop or recycle.
- Webhook handling: respond `200` quickly. Normal events are processed inline; slow work (slip verification) goes to an in-memory `Channel<T>` worker. If the app recycles mid-job, the reminder job also re-processes `SUBMITTED` payments that were never verified.
- Logging: Serilog rolling file under `logs/` (viewable from Plesk File Manager); keep `stdoutLogEnabled` off in production.
- `App_Data/` must be writable by the app-pool identity and excluded from deployment overwrites.

## 11. Roadmap

1. **MVP**: auto-register group members, create group & individual bills (equal split), LIFF pay page (PromptPay QR + slip upload), manual slip approval, `ยอด`, daily 10:00 reminders (group digest / private).
2. Custom split, invite codes, rich menu, "ทวงตอนนี้".
3. Auto slip verification, LIFF bill dashboard / history, export (CSV).
