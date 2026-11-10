using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Feedarr.Api.Services.Security;

namespace Feedarr.Api.Tests;

[CollectionDefinition("Master key environment", DisableParallelization = true)]
public sealed class MasterKeyEnvironmentCollection { }

[Collection("Master key environment")]
public sealed class FileMasterKeyProviderTests
{
    [Fact]
    public void FirstCreationAndRestart_PreserveKeyAndPrivatePermissions()
    {
        using var workspace = new KeyWorkspace();
        var first = new FileMasterKeyProvider(workspace.Keys).GetMasterKey();
        Assert.Equal(32, first.Length);
        Assert.Equal(first, File.ReadAllBytes(workspace.KeyFile));
        AssertPrivate(workspace);
        var second = new FileMasterKeyProvider(workspace.Keys).GetMasterKey();
        Assert.Equal(first, second);
        AssertPrivate(workspace);
    }

    [Theory]
    [InlineData(416)] // 0640
    [InlineData(438)] // 0666
    public void ExistingKey_RepairsPermissionsWithoutChangingBytes(int mode)
    {
        using var workspace = new KeyWorkspace();
        Directory.CreateDirectory(workspace.Keys);
        var original = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(workspace.KeyFile, original);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace.KeyFile, (UnixFileMode)mode);
            File.SetUnixFileMode(workspace.Keys, (UnixFileMode)511); // 0777
        }
        Assert.Equal(original, new FileMasterKeyProvider(workspace.Keys).GetMasterKey());
        Assert.Equal(original, File.ReadAllBytes(workspace.KeyFile));
        AssertPrivate(workspace);
        Assert.Equal(original, new FileMasterKeyProvider(workspace.Keys).GetMasterKey());
    }

    [Fact]
    public void CorruptExistingKey_IsNeverRegenerated()
    {
        using var workspace = new KeyWorkspace();
        Directory.CreateDirectory(workspace.Keys);
        var original = new byte[] { 1, 2, 3 };
        File.WriteAllBytes(workspace.KeyFile, original);
        Assert.Throws<InvalidOperationException>(() => new FileMasterKeyProvider(workspace.Keys));
        Assert.Equal(original, File.ReadAllBytes(workspace.KeyFile));
    }

    [Fact]
    public void EnvironmentKey_DoesNotReplaceExistingDiskKey()
    {
        using var workspace = new KeyWorkspace();
        Directory.CreateDirectory(workspace.Keys);
        var original = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(workspace.KeyFile, original);
        Environment.SetEnvironmentVariable("FEEDARR_MASTER_KEY", "test-only-environment-secret");
        var key = new FileMasterKeyProvider(workspace.Keys).GetMasterKey();
        Assert.Equal(SHA256.HashData("test-only-environment-secret"u8), key);
        Assert.Equal(original, File.ReadAllBytes(workspace.KeyFile));
        AssertPrivate(workspace);
    }

    [Fact]
    public void PermissionRepair_PreservesExistingEncryptedData()
    {
        using var workspace = new KeyWorkspace();
        var first = new FileMasterKeyProvider(workspace.Keys);
        var original = new XElement("key", new XElement("value", "test-data"));
        var encrypted = new AesXmlEncryptor(first.GetMasterKey()).Encrypt(original);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(workspace.KeyFile, (UnixFileMode)438);
        var restarted = new FileMasterKeyProvider(workspace.Keys);
        using var services = new ServiceCollection().AddSingleton<IMasterKeyProvider>(restarted).BuildServiceProvider();
        var decrypted = new AesXmlDecryptor(services).Decrypt(encrypted.EncryptedElement);
        Assert.True(XNode.DeepEquals(original, decrypted));
        AssertPrivate(workspace);
    }

    private static void AssertPrivate(KeyWorkspace workspace)
    {
        // Windows has no POSIX modes: key content/restart assertions still run there.
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(workspace.KeyFile));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(workspace.Keys));
    }

    private sealed class KeyWorkspace : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable("FEEDARR_MASTER_KEY");
        private readonly string _root = Path.Combine(Path.GetTempPath(), "feedarr-key-tests", Guid.NewGuid().ToString("N"));
        internal string Keys => Path.Combine(_root, "keys");
        internal string KeyFile => Path.Combine(Keys, ".master-key");
        internal KeyWorkspace() => Environment.SetEnvironmentVariable("FEEDARR_MASTER_KEY", null);
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("FEEDARR_MASTER_KEY", _previous);
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
