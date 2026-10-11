# Vorlage: Datenschutzerklärung für eine Wunschkino-Installation

> **Keine Rechtsberatung.** Dies ist ein Ausgangspunkt. Ersetze alles in `[eckigen Klammern]`, streiche, was bei Dir nicht zutrifft, und lass die fertige Erklärung vor dem öffentlichen Betrieb von einer Fachperson prüfen. Unter **Einstellungen → Rechtliches** fügst Du den Text als „Datenschutzerklärung“ ein (nur Text, kein HTML; Leerzeilen trennen Absätze). Ohne eigenen Text zeigt Wunschkino unter `/datenschutz` einen Standardtext, der nur beschreibt, was die Software verarbeitet.

## 1. Verantwortlicher

[Name, Anschrift, E-Mail-Adresse des Betreibers]

## 2. Zweck dieses Dienstes

Wunschkino ist ein Portal für [Familie und Freunde / Mitglieder von …], mit dem angemeldete Nutzer den Bestand eines Jellyfin-Servers ansehen und sich neue Filme und Serien wünschen können.

## 3. Verarbeitete Daten und Rechtsgrundlagen

| Daten | Zweck | Rechtsgrundlage | Speicherdauer |
|---|---|---|---|
| Benutzername, Passwort (nur als Hash), Rolle, Anlagedatum | Konto und Anmeldung | Art. 6 Abs. 1 lit. b DSGVO | bis zur Löschung des Kontos |
| Wünsche mit Titel, optionaler Notiz, Zeitpunkt, Stimmen und Status | Wunschfunktion | Art. 6 Abs. 1 lit. b DSGVO | bis zur Löschung des Wunsches |
| Protokoll der Verwaltungsaktionen und Anmeldungen (mit IP-Adresse nur bei Anmeldungen und gesperrten Adressen) | Sicherheit, Nachvollziehbarkeit | Art. 6 Abs. 1 lit. f DSGVO | Einträge 365 Tage, IP-Adressen 90 Tage |
| IP-Adresse bei Fehlversuchen der Anmeldung | Schutz vor Missbrauch (zeitweise Sperre) | Art. 6 Abs. 1 lit. f DSGVO | bis zu 90 Tage nach dem letzten Fehlversuch |
| Systemprotokoll des Servers: Zeitpunkt, IP-Adresse, aufgerufene Adresse, bei fehlgeschlagenen Anmeldungen der eingegebene Benutzername | Betrieb und Fehlersuche | Art. 6 Abs. 1 lit. f DSGVO | [z. B. 14 Tage] |
| Protokoll des Reverse-Proxys [Nginx Proxy Manager o. ä.]: IP-Adresse, Zeitpunkt, Anfrage | Betrieb und Sicherheit | Art. 6 Abs. 1 lit. f DSGVO | [z. B. 7 Tage, danach IP-Adressen kürzen oder löschen] |

Wünsche erscheinen für andere Nutzer [anonym / mit Namen], je nach Einstellung des Betreibers. Admins sehen den Namen immer.

## 4. Weitergabe an Dritte

- **TMDB (The Movie Database)** und, bei der Anime-Suche, **AniList** erhalten vom Server die Suchbegriffe und IMDb-Nummern, die Nutzer eingeben. Der Browser der Nutzer kontaktiert diese Dienste nicht.
- **GitHub** liefert dem Server einmal täglich eine öffentliche Zuordnungsliste für Anime; dabei werden keine Nutzerdaten übertragen.
- **Jellyfin** läuft beim Betreiber und liefert Bestand und Poster.
- [Weitere Empfänger, z. B. Hosting-Anbieter: …]

Es gibt keine Werbung, keine Webanalyse und keine eingebetteten Inhalte Dritter. Schrift, Symbole, Skripte und Bilder kommen vom eigenen Server.

## 5. Cookies und Browser-Speicher

| Name | Zweck | Dauer |
|---|---|---|
| `Wunschkino.Auth` (bei sicheren Cookies `__Host-Wunschkino.Auth`) | hält die Anmeldung | bis zum Schließen des Browsers, bei „Angemeldet bleiben“ bis zu 14 Tage (höchstens 30 Tage seit der Anmeldung) |
| `.AspNetCore.Antiforgery.…` | Schutz von Formularen vor gefälschten Anfragen | bis zum Schließen des Browsers |
| `Wunschkino.TwoFactor` | nur bei eingeschalteter Zwei-Faktor-Anmeldung: merkt sich zwischen Passwort und Code, wer sich gerade anmeldet | höchstens 5 Minuten, wird nach der Anmeldung gelöscht |

Alle sind technisch erforderlich (§ 25 Abs. 2 Nr. 2 TDDDG); eine Einwilligung ist nicht nötig. Weitere Cookies, `localStorage` oder `sessionStorage` werden nicht verwendet. Die gewählte Darstellung (hell, dunkel, System) liegt beim Konto in der Datenbank.

## 6. Ihre Rechte

Sie haben das Recht auf Auskunft (Art. 15 DSGVO), Berichtigung (Art. 16), Löschung (Art. 17), Einschränkung der Verarbeitung (Art. 18), Datenübertragbarkeit (Art. 20) und Widerspruch gegen Verarbeitungen auf Grundlage berechtigter Interessen (Art. 21). Wenden Sie sich dafür an den Verantwortlichen (Ziffer 1). Außerdem können Sie sich bei einer Datenschutz-Aufsichtsbehörde beschweren, in [Bundesland] bei [zuständige Behörde].

## 7. Hinweise für Betreiber (vor dem Veröffentlichen löschen)

- Auftragsverarbeiter (z. B. ein Hoster) brauchen einen Vertrag nach Art. 28 DSGVO.
- Lässt Du Kinder zu, gelten besondere Regeln (Art. 8 DSGVO).
- Wenn Du nichts verkaufst und das Portal nur für einen geschlossenen Kreis betreibst, entfällt oft die Impressumspflicht; prüfe das im Einzelfall.
