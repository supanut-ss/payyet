# Pay_Yet — LINE payment collection bot

ASP.NET Core 8 + MariaDB/MySQL, hosted as a single app at `https://api.drivetodev.online` (own subdomain site) on Plesk (Windows).
Design: [docs/DESIGN.md](docs/DESIGN.md) · Thai: [docs/DESIGN.th.md](docs/DESIGN.th.md)

## What the MVP does

- Learns group members passively (works without a verified OA); no manual registration.
- Collector creates bills **in the private chat** with one line. **`เก็บ` = the amount is for one person** (each person picked pays it), **`หาร` = the amount is a total split among the people picked**: `เก็บ ค่าอาหาร 200`, `หาร ค่าเครื่องดื่ม 300`. Without names the bot opens a LIFF page where the collector ticks people from the group members. Shortcuts: `หาร 600 ค่าข้าว ทุกคน`, `หาร 600 ยกเว้น สมชาย`, `เก็บ 200 สมชาย` (named, no picker; the person must be a member of one of your groups), `เก็บ สมชาย 200 มาลี 300` (custom amounts). One confirm tap, 10-minute undo.
- Every bill is separate; the daily reminder groups them into one card showing how many bills are unpaid.
- Group bills: one card in the group; individual bills: private message.
- Payment on a LIFF page: PromptPay QR (remaining amount) + slip upload. The collector approves/rejects in the private chat.
- **App screens (no typing needed)**, all in one LIFF page, opened from the bot or a rich-menu button: `?mode=home` (hub with PromptPay setting), `?mode=new` (new-bill form: group → title, amount, split/each, people, destination → one tap creates the bill, 10-minute undo), `?mode=manage` (below) and `?mode=owe` (what I still have to pay, with a pay button). Opening the page with no parameters shows the hub. Typed commands keep working as shortcuts.
- **Manage bills** (`จัดการ` → LIFF page): see who paid, record cash received (full or partial), waive a person's debt, cancel one charge or the whole bill; the group / debtors are told. Cancelling a charge is refused while a slip is waiting or someone already paid more than the new amount (waive them instead).
- `ยอด` shows balances (private chat: what I owe / who owes me; group: open bills).
- Daily 10:00 (Asia/Bangkok) reminder: one digest in the group, or one private message for individual bills.

Not in the MVP (see roadmap in the design doc): rich menu, "ทวงตอนนี้", `[ยกเว้น…]` checkbox button, due dates, automatic slip verification.

## Verification status

- **Verified locally:** build, 33 automated tests (parser, split maths, PromptPay payload/CRC, signature check, bill merge/undo, reminder idempotency; run on SQLite built from the EF model), and a smoke run of the published app (path base `/lineapi`, LIFF page, webhook signature, job key).
- **Also verified on the real database** (MariaDB 11.8.8 at the host): schema import, Thai/emoji text, bill merge, unique-key rules, reminder claim, JSON state, duplicate-event rejection.
- **Not verified:** real LINE webhook/push/LIFF flow, Flex rendering on a phone, QR scanning in a banking app, the Plesk sub-path hosting. Do these in the checklist below.

## Deploy checklist

1. **Database** — ✅ done for this host: MariaDB 11.8.8, database `thaipes_payyet`, schema imported (10 tables) and the app's queries verified against it. For another host: create a DB + user, import [db/schema.sql](db/schema.sql), and set `Db:ServerVersion` (e.g. `8.0.36-mysql` or `10.6.0-mariadb`).
2. **LINE Developers**
   - Messaging API channel: copy *Channel secret* and *Channel access token (long-lived)*. Turn **off** auto-reply and greeting messages; turn **on** "Allow bot to join group chats". Webhook URL: `https://api.drivetodev.online/api/line/webhook`, enable "Use webhook", press Verify.
   - LINE Login channel (same provider): add a LIFF app — size *Full*, scope `openid profile`, endpoint URL `https://drivetodev.online/payyet/liff/pay.html`. Note the *LIFF ID* and the Login *Channel ID*. Link the Login channel to the bot.
3. **Secrets** — fill in `deploy/appsettings.Production.json` (DB and `Jobs:Key` are already set; paste the four LINE values) and upload it next to the app. It is git-ignored; delete your local copy afterwards. Shape:
   ```json
   {
     "ConnectionStrings": { "Default": "Server=...;Database=...;User=...;Password=...;CharSet=utf8mb4" },
     "Line": { "ChannelSecret": "...", "ChannelAccessToken": "...", "LoginChannelId": "..." },
     "Liff": { "Id": "..." },
     "Jobs": { "Key": "<long random string>" }
   }
   ```
4. **TLS** — Plesk → `api.drivetodev.online` → SSL/TLS Certificates → Let's Encrypt (+ redirect HTTP→HTTPS). LINE rejects invalid certificates.
5. **Build, upload, verify** — one command (see [deploy/DEPLOY.md](deploy/DEPLOY.md)):
   ```powershell
   .\deploy-all.ps1     # or .\deploy-api.ps1 / .\deploy-web.ps1 separately
   ```
   The API deploy runs the tests, publishes `win-x86` self-contained (the shared app pool is 32-bit), uploads to the `api.drivetodev.online` folder via FTP and checks `/health`, the status page, the config and the webhook guard. The web deploy uploads `web/` to `drivetodev.online/payyet` and checks the pages and CORS. FTP credentials go in `deploy.credentials.ps1` (git-ignored). Set `$env:PAYYET_DEPLOY_DRY_RUN = "1"` for a dry run.
6. **Scheduled task** — Plesk → Scheduled Tasks → *Fetch a URL*, hourly: `https://api.drivetodev.online/jobs/reminders?key=<Jobs:Key>` (Plesk cannot send headers, so the key goes in the query; the `X-Job-Key` header also works).
7. **Try it** — add the bot, send `พร้อมเพย์ <your phone>`, invite the bot to a test group, have two people type a message there, then send `เก็บ 20 ทดสอบ` to the bot in private.

## Differences from the design doc

- `bills.token` (public id for LIFF links) and `users.last_group_id`, `groups.member_count_at` exist in the schema.
- The collector sets the PromptPay receiver with `พร้อมเพย์ <number>` in the private chat (required before the first bill).
- The reminder job accepts `GET` and `?key=`; slip image URLs (`/media/{token}`) are valid for 7 days because LINE re-fetches images whenever a chat is opened.
- Slip upload lets the debtor enter the amount (default: remaining), so partial payments work; the collector confirms the amount by looking at the slip.
- Group bills show payment progress in the daily digest and `ยอด`; no per-payment messages are posted to the group (only a final "all paid" line).

## Layout

```
src/PayYet.Api/     Controllers, Handlers (webhook), Services, Line (client + messages), Data (EF), Domain (parser, QR, split), web/liff/pay.html (static web app, separate subdomain), wwwroot/index.html (API status page)
tests/PayYet.Tests/ xUnit tests
db/schema.sql       MySQL schema
deploy-check/       one-off diagnostic page for the Plesk host (delete after use)
```
"# payyet" 
