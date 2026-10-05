namespace Arbitrage.Desktop.ViewModels;

// Tracks only obsolete read/availability feedback. A later form or command notice must survive polling.
internal sealed class ReadNotice(string? initial = null)
{
    private string? previous = initial;
    public void Forget() => previous = null;
    public string Remember(string message) => previous = message;
    public string Recover(string current)
    {
        var recovered = previous is not null && current == previous;
        previous = null;
        return recovered ? "" : current;
    }
}
