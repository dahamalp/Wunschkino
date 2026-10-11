# Wunschkino – a wish portal for Jellyfin

[Deutsch](README.md) · **English**

> A small, self-hosted portal where family and friends wish for new movies, series and anime. It does not download anything; an admin keeps track of the wishes.

Wunschkino ("wish cinema") is a web portal for **family and friends** that sits on top of an existing [Jellyfin](https://jellyfin.org/) installation. Everyone can see which movies and series are already available and **wish** for new titles. An admin keeps track of the wishes. No account with any third-party service is needed; the admin creates the users.

## Features

- **Library:** poster grid of all movies and series from Jellyfin with search, filter (movie/series) and sorting. Images are served through the server, so the Jellyfin key never reaches the browser.
- **Wishing:** search by title or IMDb number (`tt0133093`). Every result shows whether the title is already in Jellyfin, already wished for, or still wishable. Optional note.
- **No duplicate wishes, "+1":** each title can be wished for only once. Other users join with "+1".
- **Anime:** a separate search through [AniList](https://anilist.co/) (including romaji and Japanese titles), no account needed. The whole series is wished for via its TMDB number.
- **Wish lists:** "All wishes" and "My wishes" with status (open, accepted, rejected, done), filter and sorting.
- **Admin:** change the status with one click (green check, yellow timer, red cross), comment, delete, user management (create, lock, reset password, roles).
- **Automatically done:** when a wished title shows up in Jellyfin after the next sync, Wunschkino sets the wish to "done" by itself.
- **Missing episodes:** admins see which episodes of a series are missing in Jellyfin (compared with TMDB), split into certain cases and uncertain numbering, as it happens with anime.
- **Built with security in mind:** Argon2 password hashes, optional two-factor sign-in (TOTP) per account, staged blocking of addresses after failed attempts (up to permanent, unblockable by an admin), rate limiting, strict security headers (CSP), API keys encrypted in the database, provenance attestation of the releases.

> The user interface is currently in German.

## Screenshots

All images show demo data with self-drawn posters.

![Library, light mode](docs/screenshots/bibliothek-hell.png)
![Library, dark mode](docs/screenshots/bibliothek-dunkel.png)
![Wish list as seen by an admin](docs/screenshots/wunschliste-admin.png)

<img src="docs/screenshots/bibliothek-handy.png" alt="Library on a phone" width="260"> <img src="docs/screenshots/wunschliste-handy.png" alt="Wish list on a phone, dark mode" width="260">

## Requirements

- A server running **Debian 12**, **Ubuntu 24.04** or a derivative (x86-64 or arm64) with systemd. Debian 12 and Ubuntu 24.04 are tested.
- A reachable **Jellyfin** server and an **API key** (Jellyfin → Dashboard → API Keys).
- A free **TMDB API key** ([themoviedb.org](https://www.themoviedb.org/settings/api)). Both the v3 key and the v4 read access token work.
- A **reverse proxy with HTTPS**, for example [Nginx Proxy Manager](https://nginxproxymanager.com/). For security reasons, sign-in only works over HTTPS (see [Troubleshooting](#troubleshooting)).
- A current **browser** for users (Chromium-based, Firefox or Safari, the last two major versions each). Testing uses Chromium.

## Installation

One command on the server installs everything (as root or with `sudo`):

If your reverse proxy runs on **another server** (or in Docker):

```bash
curl -fsSL https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh | sudo bash -s -- --proxy-mode remote --proxy <IP-of-the-proxy-server>
```

Replace `<IP-of-the-proxy-server>` with the IP address of the machine your reverse proxy runs on. If the proxy runs in Docker, that is the address of the server hosting the container. **Without it, all users appear to come from the proxy's address and share one login rate limit and block.**

If your reverse proxy runs on the **same server** (without Docker), use `--proxy-mode local` instead. The app then listens on `127.0.0.1` only, so no port is open to the outside:

```bash
curl -fsSL https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh | sudo bash -s -- --proxy-mode local
```

The script creates a system user, `/opt/wunschkino`, `/var/lib/wunschkino` and `/etc/wunschkino`, downloads the latest release, verifies its checksum and origin, sets up a hardened systemd service and waits until the service is healthy. At the end it prints the address, the **setup token** and instructions for the proxy. It also creates the helper command `wunschkino-admin` (see [Blocked addresses](#blocked-addresses)).

**Important until setup is done:** while the setup wizard is open, anyone who can reach the server's port can open the setup page. Finish the setup right after installing, and do not leave the port openly reachable (`--proxy-mode local` or a firewall that lets in only the proxy server). The script warns when the app is open and no proxy was given.

The installer works from the first published release on. `--help` lists all options:

```bash
curl -fsSL https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh | bash -s -- --help
```

### Verifying where the package comes from

Every release contains checksums and a **provenance attestation**: it proves that the packages and the install script were built by this repository's release workflow. The installer checks it with the [GitHub CLI](https://cli.github.com/) (`gh`) if it is installed; otherwise it prints a note. By hand:

```bash
gh attestation verify wunschkino-vX.Y.Z-linux-x64.tar.gz --repo dahamalp/Wunschkino
gh attestation verify install.sh --repo dahamalp/Wunschkino
```

If you want to be careful, do not run the script straight from the pipe; download it first: `curl -fsSLO https://github.com/dahamalp/Wunschkino/releases/latest/download/install.sh`, then `gh attestation verify install.sh --repo dahamalp/Wunschkino`, then `sudo bash install.sh …`.

With `--require-attestation true` the installer aborts if the origin cannot be confirmed. `--require-attestation false` turns the check off (for example if `gh` is not signed in to GitHub or there is no network). The checksum alone only protects against damaged downloads, not against a tampered release.

### Options

Every option is also available as an environment variable `WK_<NAME>` (for example `WK_PORT=8080`).

| Option | Meaning |
|---|---|
| `--proxy-mode MODE` | `local`: the reverse proxy runs on the same server, the app listens on `127.0.0.1` only. `remote`: the proxy runs elsewhere, `--proxy` is then required. The installer rejects contradictory options. |
| `--proxy LIST` | IP address(es) or network(s) of your reverse proxy, e.g. `192.168.1.10` or `10.0.0.0/24` (the network address, not a host inside it). The app takes the real client IP only from these; they are never blocked. |
| `--allow-downgrade` | Allows an older version than the installed one. Without it the script refuses a downgrade. |
| `--require-attestation M` | `auto` (default): verify the package origin with `gh` if available. `true`: abort without a successful check. `false`: do not check. |
| `--domain HOSTS` | Public domain(s), comma-separated. Restricts the accepted Host headers (`localhost` stays allowed for the health check). |
| `--port N` | HTTP port of the app, default `8080`. |
| `--bind ADDRESS` | Listen address: an IP, `localhost` or `*` (all interfaces, default). `--proxy-mode local` sets it to `127.0.0.1`. |
| `--secure-cookies B` | `true` (default) or `false`. Only for deliberately running without HTTPS on a private network. |
| `--version vX.Y.Z` | A specific version instead of the latest. |
| `--repo OWNER/NAME` | GitHub repository of the releases, for example for a fork. |
| `--source-url URL` | Source code address the app shows to all users (AGPL). Default: `https://github.com/<--repo>`; only needed for other hosts such as Codeberg. |
| `--user`, `--install-dir`, `--data-dir`, `--config-dir` | System user and paths if the defaults do not fit. |
| `--uninstall` / `--purge` | Remove (data stays) or remove completely (also data, backups, configuration, system user). |

### Setting up Nginx Proxy Manager

1. **Hosts → Proxy Hosts → Add Proxy Host**
2. **Domain Names:** your domain, for example `wunsch.example.org`
3. **Scheme** `http`, **Forward Hostname/IP:** IP of the Wunschkino server, **Forward Port:** `8080` (or your `--port`)
4. **Websockets Support: turn on.** The interface does not work without WebSockets.
5. **SSL** tab: request a new certificate (Let's Encrypt), turn on **Force SSL** and **HSTS Enabled**. Turn on **HSTS Subdomains** only if every subdomain of your domain really serves HTTPS.
6. The port must be reachable from the proxy server, but **only** from there: allow it in the Wunschkino server's firewall for the proxy server's IP alone, for example with `sudo ufw allow from <proxy IP> to any port 8080 proto tcp`.

Then enter the proxy server's IP with `--proxy` if you did not do so during installation.

### First-time setup

Open Wunschkino through your domain. The setup wizard walks through five steps:

1. Enter the **setup token** (printed at the end of the installation and stored in `/var/lib/wunschkino/setup-token`).
2. Create the **admin account** (password of at least 12 characters).
3. Connect **Jellyfin**: address (for example `http://192.168.1.10:8096`) and API key. The connection is tested right away.
4. Enter the **TMDB key**, also with a test.
5. Finish. The wizard is then locked and the token deleted.

Create more users under **Benutzer** (users). There is deliberately no self-registration. You can change the connections and the sync interval later under **Einstellungen** (settings).

## Operation

**Update:** run the same install command again. On an update the script backs up the database and keys, checks the settings, swaps the version and verifies that the service is healthy. If anything fails it **rolls back automatically** (program, database and settings). Existing settings and paths are kept, so you do not need to repeat options. A value changes only if you pass it; `--domain ''` removes a setting.

**Uninstall:** `… | sudo bash -s -- --uninstall` (data stays) or `--uninstall --purge` (delete everything).

| What | Where |
|---|---|
| Program | `/opt/wunschkino` |
| Data (database, keys) | `/var/lib/wunschkino` |
| Backups before updates (last 5) | `/var/lib/wunschkino-backups` |
| Daily backup by the app (last 7, database only) | `/var/lib/wunschkino/backups` |
| Installation settings | `/etc/wunschkino/wunschkino.env` |
| Logs | `journalctl -u wunschkino -f` |
| Status | `curl http://127.0.0.1:8080/health` |

Wunschkino downloads the anime mapping list (a public file on GitHub, about 6 MB) once a day by itself.

### Blocked addresses

Wunschkino blocks sender addresses that sign in wrongly several times or overload the app. After every **3 failed attempts** the address is blocked for **1 minute**, **5 minutes**, **15 minutes**, **1 hour** and **24 hours**, then **permanently** until an admin unblocks it. This never locks an account; it only affects the address. The server itself and the proxies given with `--proxy` are never blocked. A successful sign-in lowers the address's counter by one; 30 days without a failed attempt reset it.

- **In the interface:** admins see all addresses with their state under **Gesperrte Adressen** (blocked addresses) and unblock them with one click.
- **On the server:** if the admin is blocked themselves, use the helper command the installer creates:

  ```bash
  sudo wunschkino-admin --list-blocks          # all addresses with failed attempts and blocks
  sudo wunschkino-admin --unblock 203.0.113.7  # unblock one address (IPv4 or IPv6)
  sudo wunschkino-admin --unblock-all          # unblock all
  ```

  The running app picks this up within a few seconds.
- **Fail2ban and similar:** every failed attempt appears in the log (`journalctl -u wunschkino`) as a fixed line: `LoginFailed ip=203.0.113.7 user="anna"`. A block shows up as `IpBlocked ip=… failures=… until=…`.
- **General limit:** more than 1500 requests per minute from one address (default) are rejected and count like failed attempts. Adjust with `Wunschkino__RequestLimitPerMinute` in `/etc/wunschkino/wunschkino.env` (`0` turns it off); then `sudo systemctl restart wunschkino`.
- **Behind a proxy** the app needs the proxy's address (`--proxy`). Without it all users look like one address and an attacker could get them blocked together; the app then warns in the log (`ProxyNotTrusted`) and shows it as critical in the security check.
- **Session:** a sign-in lasts at most 30 days, even with daily use (`Wunschkino__SessionMaxDays`, 1 to 365). "Sign out" ends every session of the account, including on other devices. Changing the Jellyfin address or keys, creating accounts, setting passwords, changing roles and disabling accounts ask for the admin's password again. A new Jellyfin address requires entering the key again.
- **Foreign sites:** requests to the web interface with a foreign `Origin` (for example from a sibling subdomain) are rejected with 403. The reverse proxy must therefore pass on the public domain's Host header (Nginx Proxy Manager does by default). If the proxy runs on the same server, it must send `X-Forwarded-For`, otherwise every visitor counts as local; the security page reports this as critical.
- **Poster memory:** Jellyfin posters are kept in memory for up to 6 hours (`Wunschkino__PosterCacheMegabytes`, default 64, `0` turns it off).
- **Passwords:** at least 12 characters. When an admin sets a password, the user must choose their own after signing in. Passwords from a built-in list of commonly used passwords (about 47,000, checked locally only) are rejected. Turn off with `Wunschkino__CheckCommonPasswords=false`.
- **Outgoing connections** (Jellyfin, TMDB, AniList) must not reach link-local or cloud metadata addresses (`169.254.0.0/16`, `fe80::/10`), not even through a host name. Private networks such as `192.168.x.x` stay allowed. If Jellyfin really runs on such an address: `Wunschkino__AllowLinkLocalUrls=true`.
- **Fail2ban:** behind a proxy all requests come from the proxy's address, so a firewall block on the Wunschkino server would hit the proxy instead of the attacker. If you want Fail2ban, run it on the proxy server and evaluate Wunschkino's log there. The fixed line can be matched with the pattern `LoginFailed ip=<HOST> user=`.

### Wishes anonymous or with names

Under **Einstellungen → Wünsche** (settings, and already in the setup wizard) you choose whether users see who made a wish or a note. **The default is anonymous**, also after an update: normal users then see no one else's name, their own contributions appear as "du" (you). This is enforced on the server; the names are not even sent to the browser. **Admins always see who a wish comes from.**

### Limits per user

Under **Einstellungen → Wünsche → Begrenzungen pro Nutzer** you set how many **wishes** a user may make per day (default 20, counting new wishes and "+1" of the last 24 hours, **admins are exempt**) and how many **TMDB searches** per minute (default 10). A value of 0 means no limit. This keeps one person from flooding the wish list or using up your TMDB quota.

### Audit log

Under **Protokoll** (log) admins see the last 200 security-relevant actions: sign-ins, creating, blocking and releasing users, changing roles and passwords, changed connection and wish settings, blocking and unblocking addresses, including those done from the console. Secrets (passwords, API keys) never appear in it. Entries are kept for 365 days, IP addresses (for sign-ins and blocked addresses) for 90 days. Failed sign-ins appear only in the server log (`journalctl -u wunschkino`).

### Backups and restoring

The app **backs up the database daily by itself** to `/var/lib/wunschkino/backups` (`wunschkino-YYYYMMDD-HHMMSS.db`) and keeps the last 7. Adjust it with `Wunschkino__BackupDirectory` (preferably another drive), `Wunschkino__BackupKeepCount` (1 to 100) and `Wunschkino__BackupWarnAfterDays` (1 to 365) in `/etc/wunschkino/wunschkino.env`; then `sudo systemctl restart wunschkino`. The **security check** shows age, size and state and warns when the backup is too old, fails or is on the same drive as the database. It also has "Jetzt sichern" (back up now). Before every update the installer additionally backs up to `/var/lib/wunschkino-backups`.

**Important:** only the database is backed up, **never the `keys` folder**. The API keys in the database are encrypted with those keys; backing up both together unprotected would undo the encryption. Back up the key folder **separately** (for example encrypted on another device):

```bash
sudo tar -czf wunschkino-keys.tar.gz -C /var/lib/wunschkino keys
```

If it is missing on a restore, everything works except the stored API keys; you then enter them again under **Einstellungen**.

**Restoring:**

```bash
sudo systemctl stop wunschkino
sudo cp /var/lib/wunschkino/backups/wunschkino-YYYYMMDD-HHMMSS.db /var/lib/wunschkino/wunschkino.db
sudo rm -f /var/lib/wunschkino/wunschkino.db-wal /var/lib/wunschkino/wunschkino.db-shm
sudo chown wunschkino:wunschkino /var/lib/wunschkino/wunschkino.db
sudo systemctl start wunschkino
```

### Languages

The interface is available in **German, English, French, Spanish and Dutch**. Every user picks their language in the header menu ("translate" icon); the choice is stored with the account in the database, not in the browser. Without a choice, the browser language applies, and if that is not offered, the **default language** under **Settings → Language** (German at first). Search results and titles from TMDB come in the user's language. Log lines and the console commands stay German.

Adding or improving translations: the texts live in `Wunschkino/Resources/Texts.<language>.resx`. For a new language, create the file and add the language to `Wunschkino/Localization/AppLanguages.cs`; a test checks that no texts and no placeholders are missing. Corrections are welcome.

### Two-factor sign-in

Every account can turn on a second factor under **My account → Two-factor sign-in**: scan the QR code with an authenticator app (for example Aegis, 2FAS, Google Authenticator or a password manager), confirm one code, done. From then on, sign-in asks for the six-digit code after the password. The QR code is generated on the server, no third-party service sees the key; it is stored encrypted like the API keys.

- Turning it on shows **10 recovery codes** once. Each works one time if the phone is missing.
- Wrong codes count like wrong passwords towards blocking the address; every code works only once.
- **Lost the phone and the codes?** An admin resets two-factor sign-in under **Users**. If the only admin locked themselves out: `sudo wunschkino-admin --reset-2fa <user>`.
- Important: if the key folder `keys` is lost, the app codes stop working, the recovery codes keep working.
- The security check recommends two-factor sign-in for all admins.

### Security check

The **Sicherheit** (security) page checks proxy, domain, secure cookies, request limit, backup and blocked addresses and says in plain words what to do. Every check shows its state as text (OK, note, warning, critical), not only as a colour. For anything critical, admins see a red banner with a link at the top of every page.

### Console commands on the server

The installer creates `wunschkino-admin`. It runs as the service user with the service's configuration and needs `sudo`:

| Command | Effect |
|---|---|
| `sudo wunschkino-admin --list-blocks` | show blocked addresses and failed attempts |
| `sudo wunschkino-admin --unblock <address>` | unblock one address (IPv4 or IPv6) |
| `sudo wunschkino-admin --unblock-all` | unblock all addresses |
| `sudo wunschkino-admin --list-users` | show all users with role and state |
| `sudo wunschkino-admin --reset-password <user>` | set a new password (asked for, at least 12 characters); signs out all existing sessions of that account and lifts a lock |
| `sudo wunschkino-admin --reset-2fa <user>` | turn off two-factor sign-in (phone and recovery codes lost); signs out all sessions of that account |

The password is never passed as an argument (it would show up in the process list and shell history); it is asked for at the terminal without echo or read from standard input: `printf '%s\n' "$PASSWORD" | sudo wunschkino-admin --reset-password anna`. Every change appears in the audit log (as "Konsole", with the name of the sudo user). **Give `wunschkino-admin` only to people you fully trust:** anyone who may run it can reset any password, including the admins'.

## Privacy and external services

Wunschkino's **server** talks to: your Jellyfin, [TMDB](https://www.themoviedb.org/) (search and posters), [AniList](https://anilist.co/) (anime search, anonymous, no account) and GitHub (daily anime mapping list). Users' **browsers** talk only to Wunschkino itself; posters are served through the server. The API keys are stored encrypted in the database and never leave the server.

### Privacy and legal notes for operators

Whoever runs Wunschkino carries the legal duties, not the project. The software helps where it can but is **not legal advice**; ask a professional before running it publicly.

- **No third parties in the browser:** the typeface (Geist), icons, scripts and posters all come from your own server; there is no analytics, advertising or embedded content. This avoids the well-known warning letters over dynamically loaded fonts (Google Fonts) and makes a cookie banner unnecessary. Automated tests check every page.
- **Cookies:** only the sign-in cookie `Wunschkino.Auth` (`__Host-Wunschkino.Auth` with secure cookies), an antiforgery cookie and, with two-factor sign-in turned on, `Wunschkino.TwoFactor` for at most 5 minutes, all strictly necessary. The chosen appearance (light, dark, system) is stored with the account in the database, not in the browser.
- **Imprint and privacy notice:** under **Settings → Legal** you can store an imprint, your own privacy notice and a contact (plain text, no HTML). They appear without signing in at `/impressum` and `/datenschutz`; the privacy link is in every footer, the imprint link only if you stored one. Without your own text, `/datenschutz` shows a default text describing what the software processes. Whether you need an imprint depends on your case (a private portal for family and friends behind a login usually does not, but the line is blurry).
- **Template:** [`docs/legal/privacy-template.en.md`](docs/legal/privacy-template.en.md) (and [German](docs/legal/datenschutz-vorlage.de.md)) is a starting point for your own notice. It also covers what your reverse proxy and operating system log.
- **Logs:** Wunschkino writes IP addresses to its system log (`journalctl -u wunschkino`) and, limited to 90 days, to the audit log on the admin page. Set a short retention for the system log (for example `MaxRetentionSec=14day` in `/etc/systemd/journald.conf`) and truncate or delete IP addresses in your proxy log after a few days.
- **Children:** if you allow children, special rules apply (for example GDPR Art. 8). That is the operator's decision.
- **Accessibility:** the target is WCAG 2.1 level AA; automated checks run with every test. For a free, privately run portal the European accessibility legislation usually does not apply.

## Troubleshooting

- **Sign-in does nothing:** the sign-in cookie is stored only over HTTPS. Open Wunschkino through the proxy with HTTPS. If you deliberately run without HTTPS on a private network, install with `--secure-cookies false`.
- **"Too many sign-in attempts" or "address blocked" for everyone at once:** the app does not know your proxy and counts all users as one address. Set `--proxy <IP-of-the-proxy-server>` and unblock with `sudo wunschkino-admin --unblock-all`.
- **Locked out (address blocked):** wait for the time shown, or unblock on the server with `sudo wunschkino-admin --unblock <address>`.
- **The page loads but stays empty or does not react:** WebSockets are not enabled in the proxy.
- **"Bad Request / Invalid Hostname":** the hostname used is not listed in `--domain`. Add it or remove the setting.
- **The message shown when the request limit is exceeded and the page for blocked addresses are always in German:** both are sent before the user's language is known. This is a known limitation.
- **Library stays empty:** check the Jellyfin address and API key under **Einstellungen** (the test shows the reason), then "Jetzt aktualisieren" (refresh now).
- **Posters are missing:** titles without an image in Jellyfin show a placeholder. Fix the image in Jellyfin (for anime, "Identify" helps).
- **More hints:** `journalctl -u wunschkino -n 100`.

## License

Wunschkino is licensed under the **[GNU Affero General Public License, version 3](LICENSE)** (AGPL-3.0). Copyright © 2026 dahamalp.

You are free to use, modify and share Wunschkino. If you run a **modified version** as a service for others, you must offer those users the source code of your changes (section 13 of the license), for example with a link to your repository. Running the unmodified version privately creates no such obligation.

For this purpose the app shows a link to the source code in its footer and on the "Über Wunschkino" (About) page. If you install with `--repo`, it points to your own repository automatically; use `--source-url` (or `Wunschkino__SourceUrl`) to set a different address.

## Credits and trademarks

- This application uses TMDB and the TMDB APIs but is not endorsed, certified, or otherwise approved by TMDB.
- The TMDB terms of use require your own API key per operator and allow non-commercial use only. Wunschkino refreshes the TMDB data stored with wishes after 6 months at the latest.
- Anime search uses the public API of [AniList](https://anilist.co/). The anime ID mapping comes from the community list [Fribb/anime-lists](https://github.com/Fribb/anime-lists).
- Wunschkino is an independent project and is not affiliated with Jellyfin, TMDB or AniList. The names belong to their respective owners.

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project Wunschkino                                  # http://localhost:5087
dotnet run --project Wunschkino --no-launch-profile -- --port 8080
dotnet test                                                      # required before every commit
deploy/test/run-container-tests.sh                               # install script in Debian/Ubuntu containers (Docker)
```

Stack: .NET 10, Blazor Web App (Interactive Server), MudBlazor, EF Core with SQLite. A release is created when a tag `vX.Y.Z` is set on a commit of the default branch.
