using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Wunschkino.E2E;

/// <summary>Minimaler Jellyfin-Ersatz mit Demo-Titeln und selbst gezeichneten Postern (keine Rechte Dritter berührt).</summary>
public sealed class FakeJellyfin : IAsyncDisposable
{
    public const string ApiKey = "demo-key-0123456789";

    private static readonly (string Id, string Name, string Type, int Year, bool Poster)[] Titles =
    [
        ("m01", "Der Zauberer vom Südpol", "Movie", 1998, true),
        ("m02", "Nachtzug nach Nirgendwo", "Movie", 2004, true),
        ("m03", "Die lange Geschichte eines außergewöhnlich langen Filmtitels mit vielen Wörtern", "Movie", 2019, true),
        ("m04", "Kleine Wunder", "Movie", 2011, true),
        ("m05", "Ohne Plakat", "Movie", 1987, false),
        ("m06", "Sturmhöhe 2", "Movie", 2022, true),
        ("m07", "Der Leuchtturm am Ende der Welt", "Movie", 2015, true),
        ("m08", "Zwei Brüder", "Movie", 2001, true),
        ("m09", "Papierschiffe", "Movie", 2017, true),
        ("m10", "Die Reise des Kartografen", "Movie", 2008, true),
        ("m11", "Echo", "Movie", 2023, true),
        ("m12", "Sommer in Ostende", "Movie", 1995, true),
        ("s01", "Die Archivare", "Series", 2020, true),
        ("s02", "Küstenwache Nord", "Series", 2012, true),
        ("s03", "Kleine Dinge, große Wirkung: Eine Serie mit sehr langem Titel", "Series", 2021, true),
        ("s04", "Nebelstadt", "Series", 2016, true),
        ("s05", "Serie ohne Bild", "Series", 2009, false),
        ("s06", "Die Werkstatt", "Series", 2018, true),
        ("s07", "Hinter dem Horizont", "Series", 2014, true),
        ("s08", "Funkstille", "Series", 2024, true)
    ];

    private readonly WebApplication _app;

    private FakeJellyfin(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public static async Task<FakeJellyfin> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapGet("/System/Info", () => Results.Json(new { ServerName = "Demo-Jellyfin", Version = "10.10.0" }));
        app.MapGet("/Items", () => Results.Json(new
        {
            TotalRecordCount = Titles.Length,
            Items = Titles.Select((t, i) => new
            {
                Id = t.Id,
                Name = t.Name,
                Type = t.Type,
                ProductionYear = t.Year,
                DateCreated = DateTimeOffset.UtcNow.AddDays(-i * 3),
                ProviderIds = new Dictionary<string, string> { ["Tmdb"] = (1000 + i).ToString() },
                ImageTags = t.Poster ? new Dictionary<string, string> { ["Primary"] = "x" } : new Dictionary<string, string>()
            })
        }));
        app.MapGet("/Items/{id}/Images/Primary", (string id) =>
        {
            var title = Titles.FirstOrDefault(t => t.Id == id);
            return title.Id is null ? Results.NotFound() : Results.Bytes(PosterPng(Array.IndexOf(Titles, title)), "image/png");
        });
        app.MapGet("/Shows/{id}/Episodes", () => Results.Json(new { Items = Array.Empty<object>() }));

        await app.StartAsync();
        var url = app.Urls.First();
        return new FakeJellyfin(app, url);
    }

    // Der Bild-Proxy lässt nur Rasterbilder durch, deshalb ein PNG mit Farbverlauf (ohne Bibliothek); den Titel zeigt die Karte darunter.
    private static byte[] PosterPng(int index)
    {
        const int width = 60;
        const int height = 90;
        var hue = index * 47 % 360;
        var from = HslToRgb(hue, 0.45, 0.38);
        var to = HslToRgb((hue + 40) % 360, 0.5, 0.18);

        var pixels = new byte[height * (1 + width * 3)];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            pixels[offset++] = 0;
            for (var x = 0; x < width; x++)
            {
                var t = (x / (double)(width - 1) + y / (double)(height - 1)) / 2;
                for (var c = 0; c < 3; c++)
                    pixels[offset++] = (byte)Math.Round(from[c] + (to[c] - from[c]) * t);
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(pixels);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        header[9] = 2;
        WritePngChunk(png, "IHDR", header);
        WritePngChunk(png, "IDAT", compressed.ToArray());
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        var typeAndData = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type, typeAndData);
        data.CopyTo(typeAndData, 4);

        Span<byte> number = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        stream.Write(typeAndData);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(typeAndData));
        stream.Write(number);
    }

    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }

        return ~crc;
    }

    private static double[] HslToRgb(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        var x = chroma * (1 - Math.Abs(hue / 60 % 2 - 1));
        var m = lightness - chroma / 2;
        var (r, g, b) = (int)(hue / 60) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
