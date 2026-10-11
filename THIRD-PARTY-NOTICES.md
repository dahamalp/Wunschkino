# Third-party notices / Drittkomponenten

Wunschkino itself is licensed under the AGPL-3.0 (see `LICENSE`). It ships with or uses the following components under their own licenses.
Wunschkino selbst steht unter der AGPL-3.0 (siehe `LICENSE`). Es liefert die folgenden Komponenten unter deren eigenen Lizenzen mit oder nutzt sie.

| Component | Use | License |
|---|---|---|
| [Geist](https://github.com/vercel/geist-font) (Vercel, Basement Studio) | Typeface, shipped as `wwwroot/fonts/Geist-Variable.woff2`; license text in `wwwroot/fonts/Geist-OFL.txt` | SIL Open Font License 1.1 |
| [MudBlazor](https://mudblazor.com) | UI components (including its embedded Material Design icons) | MIT; icons Apache-2.0 |
| [SecLists](https://github.com/danielmiessler/SecLists) (Daniel Miessler) | List of common passwords (12+ characters, lower-cased), shipped as `Auth/common-passwords.txt.gz` | MIT |
| [Isopoh.Cryptography.Argon2](https://github.com/mheyman/Isopoh.Cryptography.Argon2) | Password hashing | MIT |
| [Entity Framework Core](https://github.com/dotnet/efcore), [.NET](https://github.com/dotnet/runtime) | Data access, runtime | MIT |
| [axe-core](https://github.com/dequelabs/axe-core) | Accessibility checks, only in `tests/Wunschkino.E2E` (not shipped) | MPL-2.0 |
| [Playwright for .NET](https://github.com/microsoft/playwright-dotnet) | Browser tests, only in `tests/Wunschkino.E2E` (not shipped) | Apache-2.0 |

Data sources (queried by the server, no code included): [TMDB](https://www.themoviedb.org), [AniList](https://anilist.co), [Fribb/anime-lists](https://github.com/Fribb/anime-lists), your own Jellyfin server.
The TMDB logo and the names of Jellyfin, TMDB and AniList belong to their owners and are not covered by the AGPL.
