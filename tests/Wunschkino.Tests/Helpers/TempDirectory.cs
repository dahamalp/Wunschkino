namespace Wunschkino.Tests.Helpers;

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("wunschkino-tests-").FullName;

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
