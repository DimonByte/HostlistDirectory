namespace HostlistDirectory;

public sealed class Log(bool verbose)
{
    private static readonly bool InActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    private readonly Lock _lock = new();

    public void Info(string msg) => Write(ConsoleColor.Gray, msg);
    public void Ok(string msg) => Write(ConsoleColor.Green, $"  ✓ {msg}");
    public void Detail(string msg) { if (verbose) Write(ConsoleColor.DarkGray, $"    {msg}"); }

    public void Warn(string msg)
    {
        if (InActions) Write(null, $"::warning::{Escape(msg)}");
        else Write(ConsoleColor.Yellow, $"  ! {msg}");
    }

    public void Fail(string msg)
    {
        if (InActions) Write(null, $"::error::{Escape(msg)}");
        else Write(ConsoleColor.Red, $"  ✗ {msg}");
    }

    private static string Escape(string s) => s.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");

    private void Write(ConsoleColor? color, string msg)
    {
        lock (_lock)
        {
            if (color is { } c && !Console.IsOutputRedirected) Console.ForegroundColor = c;
            Console.WriteLine(msg);
            if (color is not null && !Console.IsOutputRedirected) Console.ResetColor();
        }
    }
}
