# Template: privacy notice for a Wunschkino installation

> **Not legal advice.** This is a starting point. Replace everything in `[square brackets]`, remove what does not apply to you, and have the finished notice reviewed by a professional before running the portal publicly. Paste the text under **Settings → Legal** as "Privacy notice" (plain text, no HTML; blank lines separate paragraphs). Without your own text, Wunschkino shows a default text at `/datenschutz` that only describes what the software processes. The built-in interface is German; this template is for operators who publish an English text.

## 1. Controller

[Name, address, email address of the operator]

## 2. Purpose of this service

Wunschkino is a portal for [family and friends / members of …] where signed-in users can browse the contents of a Jellyfin server and wish for new films and series.

## 3. Data processed and legal bases

| Data | Purpose | Legal basis | Retention |
|---|---|---|---|
| Username, password (hash only), role, creation date | Account and sign-in | Art. 6(1)(b) GDPR | until the account is removed |
| Wishes with title, optional note, time, votes and status | Wish feature | Art. 6(1)(b) GDPR | until the wish is removed |
| Audit log of admin actions and sign-ins (with IP address only for sign-ins and blocked addresses) | Security, traceability | Art. 6(1)(f) GDPR | entries 365 days, IP addresses 90 days |
| IP address on failed sign-in attempts | Abuse protection (temporary block) | Art. 6(1)(f) GDPR | up to 90 days after the last failed attempt |
| Server system log: time, IP address, requested address, entered username on failed sign-ins | Operation and troubleshooting | Art. 6(1)(f) GDPR | [e.g. 14 days] |
| Reverse proxy log [Nginx Proxy Manager etc.]: IP address, time, request | Operation and security | Art. 6(1)(f) GDPR | [e.g. 7 days, then truncate or wipe IP addresses] |

Wishes appear to other users [anonymously / with names], depending on the operator's setting. Admins always see the name.

## 4. Disclosure to third parties

- **TMDB (The Movie Database)** and, for anime search, **AniList** receive the search terms and IMDb numbers users enter, sent by the server. Users' browsers do not contact these services.
- **GitHub** supplies the server once a day with a public anime mapping list; no user data is sent.
- **Jellyfin** runs at the operator and provides the library and posters.
- [Further recipients, e.g. hosting provider: …]

There is no advertising, no web analytics and no embedded third-party content. Typeface, icons, scripts and images come from the server itself.

## 5. Cookies and browser storage

| Name | Purpose | Lifetime |
|---|---|---|
| `Wunschkino.Auth` (`__Host-Wunschkino.Auth` with secure cookies) | keeps you signed in | until the browser closes, up to 14 days with "stay signed in" (at most 30 days since signing in) |
| `.AspNetCore.Antiforgery.…` | protects forms against forged requests | until the browser closes |
| `Wunschkino.TwoFactor` | only with two-factor sign-in turned on: remembers between password and code who is signing in | at most 5 minutes, deleted after sign-in |

All of them are strictly necessary (Section 25(2) no. 2 TDDDG, the German implementation of the ePrivacy Directive); no consent is needed. No other cookies, `localStorage` or `sessionStorage` are used. The chosen appearance (light, dark, system) is stored with the account in the database.

## 6. Your rights

You have the right of access (Art. 15 GDPR), rectification (Art. 16), removal of your data (Art. 17), restriction of processing (Art. 18), data portability (Art. 20) and to object to processing based on legitimate interests (Art. 21). Contact the controller (section 1). You may also lodge a complaint with a data protection supervisory authority, in [state/country] [authority].

## 7. Notes for operators (remove before publishing)

- Processors (for example a hosting provider) need a contract under Art. 28 GDPR.
- If you allow children, special rules apply (Art. 8 GDPR).
- If you sell nothing and run the portal for a closed group only, an imprint is often not required; check your case.
