using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Arbitrage.LocalTransport;

public sealed record LocalConnection(string BaseUrl, string Credential)
{
    public override string ToString() => "Local connection metadata (credential redacted)";
}

public interface ILocalConnectionFile
{
    Task<LocalConnection> ReadAsync(CancellationToken cancellationToken);
    Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken);
}

public static class LocalPaths
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArbitrageTrading");
    public static string Runtime => Path.Combine(Root, "runtime");
    public static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !IPAddress.IsLoopback(ip) ||
            uri.Port < 1024 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Local endpoint must be HTTP on a literal loopback address with a port from 1024 to 65535 and no path, credentials, query, or fragment.");
        return uri;
    }
}

// Shared source adapter: neither Desktop nor Contracts depends on Infrastructure.
// Windows DACLs and Unix modes are kept outside Domain/Application.
public static class ProtectedStorage
{
    public static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Runtime storage must not contain symbolic links or reparse points.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Runtime storage must not contain symbolic links or reparse points.");
    }

    public static void CreatePrivateDirectory(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("OS account unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            var directory = new DirectoryInfo(path);
            if (!directory.Exists) FileSystemAclExtensions.Create(directory, security);
            else FileSystemAclExtensions.SetAccessControl(directory, security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static void VerifyPrivateFile(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new FileInfo(path).GetAccessControl();
            if (!Equals(security.GetOwner(typeof(SecurityIdentifier)), identity.User))
                throw new IOException("Connection metadata owner is invalid.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !Equals(rule.IdentityReference, identity.User))
                    throw new IOException("Connection metadata permissions are too broad.");
        }
        else
        {
            var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(path) & forbidden) != 0)
                throw new IOException("Connection metadata permissions are too broad.");
        }
    }

    public static void RestrictFile(string path)
    {
        RejectLinks(path);
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User ?? throw new IOException("OS account unavailable.");
            var security = new FileSecurity();
            security.SetOwner(sid);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public sealed class ProtectedLocalConnectionFile(string directory) : ILocalConnectionFile
{
    private readonly string path = Path.Combine(directory, "connection.json");
    public async Task<LocalConnection> ReadAsync(CancellationToken cancellationToken)
    {
        ProtectedStorage.VerifyPrivateFile(path);
        // Permit atomic replacement while a desktop refresh still reads the prior credential.
        await using var stream = new FileStream(path, new FileStreamOptions
        { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read | FileShare.Delete, Options = FileOptions.Asynchronous });
        var connection = await JsonSerializer.DeserializeAsync<LocalConnection>(stream, cancellationToken: cancellationToken)
            ?? throw new IOException("Connection metadata is invalid.");
        LocalPaths.ValidateBaseUrl(connection.BaseUrl);
        if (connection.Credential is null || connection.Credential.Length != 64 || !connection.Credential.All(Uri.IsHexDigit))
            throw new IOException("Connection metadata is invalid.");
        return connection;
    }

    public async Task WriteAsync(LocalConnection connection, CancellationToken cancellationToken)
    {
        ProtectedStorage.CreatePrivateDirectory(directory);
        ProtectedStorage.RejectLinks(path);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
                await JsonSerializer.SerializeAsync(stream, connection, cancellationToken: cancellationToken);
            ProtectedStorage.RestrictFile(temporary);
            ProtectedStorage.VerifyPrivateFile(temporary);
            // Windows can deny overwrite briefly while a reader or OS scanner holds the old file.
            // Retry the atomic operation; never delete the destination or weaken its permissions.
            for (var attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { File.Move(temporary, path, true); break; }
                catch (Exception exception) when (OperatingSystem.IsWindows() && attempt < 40 &&
                    exception is IOException or UnauthorizedAccessException)
                { await Task.Delay(50, cancellationToken); }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
