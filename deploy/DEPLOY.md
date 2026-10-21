# Deploy details (local reference — contains no secrets)

Secrets live only in `deploy/appsettings.Production.json` (git-ignored). Never paste them into chat or commit them.
FTP credentials live only in `deploy.credentials.ps1` (git-ignored).

## Environment

| Item | Value |
|---|---|
| Public URL | `https://gateway.drivetodev.online/payyet` (FTP folder `gateway.drivetodev.online/payyet`; the old `api.drivetodev.online` site is the `-ApiTarget api` fallback) |
| Host | Plesk on Windows, IIS 10, ASP.NET Core Module v2 installed |
| Why a subdomain | `drivetodev.online/lineapi` failed: IIS locks `system.webServer/handlers` for the apex site (500.19, lock violation) |
| Build | `win-x86` **self-contained**, out-of-process (the shared app pool is 32-bit and has no x86 runtime) |
| Database | MariaDB 11.8.8, database `thaipes_payyet`, schema imported (10 tables), app connects via `localhost` |
| App pool | identity `IWPD_289`, shared with the other sites of the `thaipesleague.com` subscription |

## Web app subdomain

`web/` (landing page + LIFF pay page, static) is deployed to the `/payyet` folder of the `drivetodev.online` site (FTP folder `drivetodev.online/payyet`); `api.drivetodev.online` serves the API and a status page at `/` (online; during a deploy IIS shows `app_offline.htm` = "updating"). The web pages call the API cross-origin; the API allows `App:WebBaseUrl` (default `https://drivetodev.online/payyet`). Create the subdomain in Plesk with Let's Encrypt before the first deploy, then change the LIFF Endpoint URL. The old `/liff/pay` on the API redirects to the new page.

## One-time setup on the server

1. Plesk → Websites & Domains → `api.drivetodev.online` (subdomain already created).
2. **SSL/TLS Certificates → Let's Encrypt** for `api.drivetodev.online`, and enable "Redirect from HTTP to HTTPS". LINE rejects invalid certificates, and the deploy health check needs a valid one.
3. Hosting Settings: leave ASP.NET enabled; no PHP needed.

## LINE settings (developers.line.biz)

| Where | Setting |
|---|---|
| Messaging API channel | Webhook URL `https://api.drivetodev.online/api/line/webhook`, Use webhook ON, press Verify |
| LINE Official Account Manager | Auto-reply OFF, Greeting OFF, Allow bot to join group chats ON |
| LIFF app (Login channel) | Endpoint URL `https://drivetodev.online/payyet/liff/pay.html` (size Full, scope `openid profile`, bot link to this bot) |

Config values go into `deploy/appsettings.Production.json`: `Line:ChannelSecret`, `Line:ChannelAccessToken`, `Line:LoginChannelId`, `Liff:Id`; `Jobs:Key` and the DB connection are already set; `App:PublicBaseUrl = https://api.drivetodev.online`, `App:PathBase = ""`.

## Deploy / redeploy

```powershell
.\deploy-api.ps1    # API only (tests, publish, offline → upload → online, health checks)
.\deploy-web.ps1    # static web app only (web/ → drivetodev.online/payyet, no downtime for the API)
.\deploy-all.ps1    # API first, then the web app
```

FTP credentials live in `deploy.credentials.ps1` (git-ignored; define `$D2D_DEPLOY_FTP_SERVER/USERNAME/PASSWORD`). `deploy-api|web|all.ps1` → `deploy-single-host.core.ps1` (workflow, `-Component api|web|all`) → `upload-ftp.ps1` (FTP uploader). Never run two deploys at once: the API deploy leaves the site in maintenance mode if interrupted. The workflow:
validate config → run tests → `dotnet publish` (win-x86 self-contained) → patch `web.config` → upload `app_offline.htm` → upload files (prunes stale root files) → remove `app_offline.htm` → health checks (`/health`, LIFF page, config, unsigned webhook = 401, JSON error body). If a check fails the site is put back into maintenance mode.

- Dry run (no remote changes): `$env:PAYYET_DEPLOY_DRY_RUN = "1"` before running.
- The script refuses any `RemotePath` other than the pinned one of the chosen `-ApiTarget` (`gateway.drivetodev.online/payyet` or `api.drivetodev.online`).
- `StdoutLog = $true` in `deploy-api.ps1` / `deploy-all.ps1` keeps ANCM logs in `logs/` on the server; remove it once stable.

## After the app is up

1. LINE Developers → webhook → **Verify**.
2. Plesk → Scheduled Tasks → *Fetch a URL*, hourly: `https://api.drivetodev.online/jobs/reminders?key=<Jobs:Key>`.
3. Smoke test: add the bot as a friend → `พร้อมเพย์ <phone>` → invite the bot to a test group → two people post a message → in private chat `เก็บ 20 ทดสอบ` → confirm → open the card's "จ่ายเงิน" button → upload a slip → approve as the collector.

## Security to do

- Change the database password (it was shared in chat) and update `appsettings.Production.json`, then redeploy.
- Plesk → Tools & Settings → Database Servers: restrict remote access to port 3306 (the app uses `localhost`).
- Rotate the FTP password if it was shared; keep `deploy.credentials.ps1` out of version control.
- `App_Data/slips` is readable by other sites in the same subscription (shared app-pool identity).

## Troubleshooting

| Symptom | Likely cause / action |
|---|---|
| Health check cannot connect / TLS error | no valid certificate yet on `api.drivetodev.online` — issue Let's Encrypt in Plesk |
| 500.19 "Lock violation" | the host locks a config section — note which element in `web.config` (handlers vs httpErrors); drop the `httpErrors` block in the core script if that is the locked one |
| 500.30 / 502.5 / 500.0 | read `logs/stdout*` on the server (FTP); check `appsettings.Production.json` is present and valid JSON |
| `/health` returns 503 `{"db":false}` | DB credentials/host in `ConnectionStrings:Default`, or the DB user lost privileges |
| Webhook Verify fails | wrong Channel secret, app not running, certificate not valid, or URL typo |
| LIFF page says the link is invalid | opened without `?b=` — open it from the bill card button |
| `เก็บ` replies "ยังไม่รู้จักกลุ่ม" | the collector has not posted in the group since the bot joined |
| Reminders never arrive | scheduled task URL/key wrong (expect 401), or the bill was created today (first reminder is next day at 10:00 Bangkok) |

## API at gateway.drivetodev.online/payyet (the default target now)

`.\deploy-api.ps1` (or `.\deploy-all.ps1`) publishes the API to the `payyet` folder of `gateway.drivetodev.online` using `deploy\appsettings.Gateway.json`
(a git-ignored copy of the production config with `App:PublicBaseUrl = https://gateway.drivetodev.online/payyet` and `App:PathBase = /payyet`;
it may also carry the `Gateway` section so the DB string and LINE keys are read from the gateway). The static web app goes to `drivetodev.online/payyet`.
`deploy-all.ps1` deploys the API first and the web only after `/health` answers; `.\deploy-web.ps1` alone refuses to run while the API is down.
The old `api.drivetodev.online` site stays available with `-ApiTarget api` (it uses `deploy\appsettings.Production.json`). Dry run: `$env:PAYYET_DEPLOY_DRY_RUN = "1"`.

One-time switch-over (after the first successful gateway deploy, `/health` answers at `https://gateway.drivetodev.online/payyet/health`):
1. LINE Developers → Messaging API → Webhook URL `https://gateway.drivetodev.online/payyet/api/line/webhook` → Verify.
2. External cron: `https://gateway.drivetodev.online/payyet/jobs/reminders` (header `X-Job-Key`); stop the old one (both hit the same database).
3. Run `.\deploy-web.ps1` so the pages call the gateway API (`var API` in `web/liff/pay.html`, config fetch in `web/index.html`).
4. Check one slip card: slip images use `App:PublicBaseUrl`.
5. If IIS answers 500.19 for the `/payyet` folder (locked `handlers`), make it an application in Plesk (Virtual Directories) and redeploy.
