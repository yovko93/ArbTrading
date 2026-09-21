using Arbitrage.LocalTransport;

namespace Arbitrage.Infrastructure;

public sealed class LocalRuntimeLease : IDisposable
{
    private readonly List<FileStream> locks = [];
    public LocalRuntimeLease(params string[] directories)
    {
        try
        {
            foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ProtectedStorage.CreatePrivateDirectory(directory);
                var path = Path.Combine(directory, "backend.lock");
                ProtectedStorage.RejectLinks(path);
                locks.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
        }
        catch { Dispose(); throw new IOException("Local storage is unavailable or another backend owns this data/runtime directory."); }
    }
    public void Dispose() { foreach (var item in locks) item.Dispose(); locks.Clear(); }
}
