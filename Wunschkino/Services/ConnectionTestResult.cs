namespace Wunschkino.Services;

public sealed record ConnectionTestResult(bool Success, string Message)
{
    public static ConnectionTestResult Ok(string message) => new(true, message);
    public static ConnectionTestResult Fail(string message) => new(false, message);
}
