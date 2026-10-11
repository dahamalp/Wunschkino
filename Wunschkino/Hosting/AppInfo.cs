namespace Wunschkino.Hosting;

/// <summary>Angaben, die die App ihren Nutzern zeigt (Fußzeile und „Über“-Seite).</summary>
/// <param name="SourceUrl">Adresse des Quelltexts (AGPL-3.0, Abschnitt 13), bereits geprüft.</param>
/// <param name="Version">Versionsnummer der laufenden App; nur für angemeldete Nutzer sichtbar.</param>
public sealed record AppInfo(string SourceUrl, string Version);
