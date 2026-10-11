namespace Wunschkino.Data;

public class Setting
{
    public required string Key { get; set; }
    public string? Value { get; set; }
    public bool IsProtected { get; set; }
}
