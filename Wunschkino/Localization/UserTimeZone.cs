namespace Wunschkino.Localization;

/// <summary>
/// Zeitzone des Browsers. Blazor Server läuft in der Zeitzone des Servers; ohne diese Angabe sähen Nutzer in anderen Zonen falsche Uhrzeiten.
/// Bis der Browser antwortet (und wenn er es nicht tut), gilt die Zeitzone des Servers.
/// </summary>
public sealed partial class UserTimeZone
{
    private const int MaxIdLength = 64;

    // IANA-Kennungen wie „Europe/Berlin“ oder „America/Argentina/Buenos_Aires“. Das Muster hält Eingaben fern, die der Browser
    // nicht sendet (Pfadteile wie „..“, Zeilenumbrüche), bevor das Betriebssystem sie als Dateinamen auflöst.
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+){0,2}\z")]
    private static partial System.Text.RegularExpressions.Regex ZoneIdPattern();

    private TimeZoneInfo? _zone;

    public event Action? Changed;

    public DateTime ToLocal(DateTime utc)
    {
        var value = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return _zone is null ? value.ToLocalTime() : TimeZoneInfo.ConvertTimeFromUtc(value, _zone);
    }

    /// <summary>Übernimmt die vom Browser gemeldete IANA-Kennung; unbekannte oder überlange Werte werden ignoriert.</summary>
    public bool TrySet(string? zoneId)
    {
        if (string.IsNullOrWhiteSpace(zoneId) || zoneId.Length > MaxIdLength || !ZoneIdPattern().IsMatch(zoneId))
            return false;

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }

        if (_zone?.Id == zone.Id)
            return true;

        _zone = zone;
        Changed?.Invoke();
        return true;
    }
}
