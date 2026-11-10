using System.Diagnostics;

namespace Feedarr.Api.Tests;

public sealed class DockerEntrypointPermissionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentVolume_FirstAndSecondStart_PreserveSecrets(bool trailingSlash)
    {
        if (OperatingSystem.IsWindows()) return; // entrypoint is a POSIX shell script
        using var workspace = new ShellWorkspace(trailingSlash);
        Assert.Equal(0, await workspace.RunAsync());
        var key = Path.Combine(workspace.Data, "keys", ".master-key");
        var original = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(key, original);
        File.SetUnixFileMode(key, (UnixFileMode)416); // reproduce old 0640 mode
        File.SetUnixFileMode(Path.GetDirectoryName(key)!, (UnixFileMode)511);
        Assert.Equal(0, await workspace.RunAsync());
        Assert.Equal(original, await File.ReadAllBytesAsync(key));
        Assert.Equal((UnixFileMode)384, File.GetUnixFileMode(key)); // 0600
        Assert.Equal((UnixFileMode)448, File.GetUnixFileMode(Path.GetDirectoryName(key)!)); // 0700
        Assert.False(File.Exists(workspace.BroadenedMarker));
        Assert.True(File.Exists(workspace.StartedMarker));
    }

    [Theory]
    [InlineData("chown")]
    [InlineData("chmod")]
    public async Task CannotSecureSecrets_StopsBeforeStartingApplication(string failure)
    {
        if (OperatingSystem.IsWindows()) return;
        using var workspace = new ShellWorkspace();
        Assert.NotEqual(0, await workspace.RunAsync(failure));
        Assert.False(File.Exists(workspace.StartedMarker));
        Assert.Contains("Cannot secure secrets directory", workspace.Error);
    }

    private sealed class ShellWorkspace : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "feedarr entrypoint", Guid.NewGuid().ToString("N"));
        private readonly string _bin;
        private readonly bool _trailingSlash;
        internal string Data => Path.Combine(_root, "volume");
        internal string BroadenedMarker => Path.Combine(_root, "broadened");
        internal string StartedMarker => Path.Combine(_root, "started");
        internal string Error = "";
        internal ShellWorkspace(bool trailingSlash = false)
        {
            _trailingSlash = trailingSlash;
            _bin = Path.Combine(_root, "bin");
            Directory.CreateDirectory(_bin);
            Stub("id", "echo 10001");
            Stub("getent", "echo 'feedarr:x:10001:'");
            Stub("usermod", "exit 0");
            Stub("groupmod", "exit 0");
            Stub("chown", "if [ \"${TEST_FAIL:-}\" = chown ]; then exit 1; fi\nexit 0");
            Stub("chmod", "if [ \"${TEST_FAIL:-}\" = chmod ]; then exit 1; fi\n" +
                "case \"$*\" in *u+rwX*keys*) touch \"$TEST_BROADENED\"; exit 1;; esac\nexec /bin/chmod \"$@\"");
            Stub("gosu", "touch \"$TEST_STARTED\"\nexit 0");
        }
        private void Stub(string name, string body)
        {
            var path = Path.Combine(_bin, name);
            File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)448);
        }
        internal async Task<int> RunAsync(string failure = "")
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "docker", "entrypoint.sh")))
                root = root.Parent;
            Assert.NotNull(root);
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root!.FullName, "docker", "entrypoint.sh"));
            start.Environment["PATH"] = _bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["App__DataDir"] = Data + (_trailingSlash ? "///" : "");
            start.Environment["PUID"] = "10001";
            start.Environment["PGID"] = "10001";
            start.Environment["TEST_FAIL"] = failure;
            start.Environment["TEST_BROADENED"] = BroadenedMarker;
            start.Environment["TEST_STARTED"] = StartedMarker;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(entireProcessTree: true); throw; }
            await output;
            Error = await error;
            return process.ExitCode;
        }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
