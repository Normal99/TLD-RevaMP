using System.Diagnostics;

namespace TLDInstalls;

internal static class GitHubAuth
{
    internal static async Task<string?> Token(Func<string, string?>? env = null, Func<Task<string?>>? gh = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        foreach (var key in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (env(key) is { } token && !string.IsNullOrWhiteSpace(token)) return token.Trim();
        return await (gh ?? FromCli)();
    }

    static async Task<string?> FromCli()
    {
        var exe = "gh";
        if (Env.Windows)
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
            if (File.Exists(installed)) exe = installed;
        }
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("auth"); psi.ArgumentList.Add("token");
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(true); return null; }
            await error;
            var token = (await output).Trim();
            return p.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
