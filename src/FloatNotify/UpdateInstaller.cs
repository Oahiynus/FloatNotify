using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace FloatNotify;

internal static class UpdateInstaller
{
    static readonly HttpClient Assets = new(new HttpClientHandler { UseCookies = false });
    internal static string FileName(Version version) => $"FloatNotify-{version}-windows-x64-Setup.exe";

    internal static async Task<string> DownloadAsync(Version version, string directory, Action<int> progress,
        CancellationToken stop, HttpClient? client = null)
    {
        string name = FileName(version);
        var installer = new Uri($"https://github.com/Oahiynus/FloatNotify/releases/download/v{version}/{name}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var http = client ?? Assets;
        using var checksum = await http.GetAsync(installer.AbsoluteUri + ".sha256", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        checksum.EnsureSuccessStatusCode();
        using var input = await checksum.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] line = new byte[1025];
        int count = await input.ReadAtLeastAsync(line, line.Length, false, token).ConfigureAwait(false);
        var fields = Encoding.UTF8.GetString(line,0,count).TrimStart('\uFEFF').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (count == line.Length || fields.Length != 2 || fields[1].TrimStart('*') != name ||
            fields[0].Length != 64 || !fields[0].All(Uri.IsHexDigit))
            throw new InvalidDataException("发布的校验文件与安装包不匹配。");

        using var response = await http.GetAsync(installer, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new FileStream(Path.Combine(directory,name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536];
        long total = 0;
        int reported = 0;
        long? length = response.Content.Headers.ContentLength;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            await output.WriteAsync(buffer.AsMemory(0,count), token).ConfigureAwait(false);
            hash.AppendData(buffer,0,count);
            total += count;
            if (length is > 0)
            {
                int percent = (int)Math.Min(100,100.0 * total / length.Value);
                if (percent >= reported+5) { reported = percent; progress(percent); }
            }
        }
        string actual = Convert.ToHexString(hash.GetHashAndReset());
        if (total == 0 || !actual.Equals(fields[0],StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装包 SHA-256 校验失败，请重新下载。");
        return actual;
    }

    internal static Process Start(Version version, string directory, string digest)
    {
        string script = Path.Combine(directory,"InstallUpdate.ps1");
        using var resource = typeof(UpdateInstaller).Assembly.GetManifestResourceStream("FloatNotify.InstallUpdate.ps1")
            ?? throw new InvalidOperationException("缺少更新组件。");
        using var reader = new StreamReader(resource);
        File.WriteAllText(script, reader.ReadToEnd(), new UTF8Encoding(true));
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute=false, CreateNoWindow=true, WorkingDirectory=Path.GetTempPath()
        };
        foreach (string arg in new[]{"-NoLogo","-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,
            "-InstallDirectory",Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),"-Version",version.ToString(),"-Sha256",digest})
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("无法启动更新组件。");
    }

    internal static void Discard(string directory, Version version)
    {
        try {
            File.Delete(Path.Combine(directory,FileName(version)));
            File.Delete(Path.Combine(directory,"InstallUpdate.ps1"));
            Directory.Delete(directory);
        } catch (Exception ex) when(ex is IOException or UnauthorizedAccessException) { Audit.Write("update-temp-cleanup "+ex.GetType().Name); }
    }
}
