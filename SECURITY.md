# Sicherheit / Security

## Deutsch

### Eine Sicherheitslücke melden
Bitte **nicht** als öffentliches Issue melden. Nutze stattdessen den Reiter **Security → Report a vulnerability** dieses Repositories (private Meldung an die Betreiber). Beschreibe, was passiert, wie man es nachstellt und welche Version betroffen ist. Zugangsdaten, Schlüssel oder echte Nutzerdaten gehören nicht in die Meldung.

Du bekommst eine Rückmeldung, sobald sie jemand gelesen hat; das Projekt wird in der Freizeit gepflegt, es gibt keine feste Antwortzeit. Nach der Korrektur wird die Meldung auf Wunsch mit Namensnennung veröffentlicht.

### Unterstützte Versionen
Es wird nur die jeweils neueste Version (`v0.x`) mit Sicherheitskorrekturen versorgt. Update: der Einzeiler aus der [README](README.md) installiert die neueste Version über die bestehende.

### Was bereits eingebaut ist
Anmeldung mit Argon2id, optionale Zwei-Faktor-Anmeldung, Sperre von Adressen nach Fehlversuchen, Prüfung der Herkunft von Anfragen, strenge Content-Security-Policy ohne Drittanbieter, Schutz vor Zugriffen auf interne Adressen, verschlüsselte Ablage der API-Keys, Herkunftsbestätigung (Attestierung) der Release-Pakete.

## English

### Reporting a vulnerability
Please do **not** open a public issue. Use **Security → Report a vulnerability** on this repository (a private report to the maintainers). Describe what happens, how to reproduce it and which version is affected. Do not include credentials, keys or real user data.

You will get a reply once someone has read it; the project is maintained in spare time and has no fixed response time. After the fix, the report is published with credit on request.

### Supported versions
Only the latest version (`v0.x`) receives security fixes. To update, run the one-line command from the [README](README.en.md); it installs the newest version over the existing one.

### What is already built in
Sign-in with Argon2id, optional two-factor sign-in, address blocking after failed attempts, request origin checks, a strict Content Security Policy without third parties, protection against requests to internal addresses, encrypted storage of API keys, provenance attestation of the release packages.
