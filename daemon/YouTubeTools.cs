using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SpotifyRadio;

/// <summary>
/// The two programs the YouTube source needs, and getting them. Neither ships with the mod: they
/// are downloaded from their own GitHub releases into the daemon's yt folder and checked against
/// the checksums published next to them.
///   yt-dlp  github.com/yt-dlp/yt-dlp        the unpacked Windows build, which starts about half
///                                           a second sooner per lookup than the single exe
///   ffmpeg  github.com/BtbN/FFmpeg-Builds   an LGPL "shared" build: half the download of the
///                                           single-exe one (77 against 171 MB), a bit more on disk
/// </summary>
public static class YouTubeTools
{
    /// <summary>For the prompt; measured on the 2026-10 releases.</summary>
    public const string Sizes = "about 95 MB to download, 190 MB on disk";

    public static string Dir => Path.Combine(Config.AppDir, "yt");

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static string? Find(string env, params string[] candidates)
    {
        if (Env(env) is { } over) return File.Exists(over) ? over : null;
        foreach (var c in candidates)
        {
            var path = Path.Combine(Dir, OperatingSystem.IsWindows() ? c + ".exe" : c);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string? YtDlp => Find("SPOTIFY_RADIO_YTDLP", Path.Combine("yt-dlp_win", "yt-dlp"), "yt-dlp");

    /// <summary>The downloaded build in yt\ffmpeg\, or a single ffmpeg.exe put into yt\ by hand.</summary>
    public static string? Ffmpeg => Find("SPOTIFY_RADIO_FFMPEG", Path.Combine("ffmpeg", "ffmpeg"), "ffmpeg");

    public static bool Installed => YtDlp is not null && Ffmpeg is not null;

    // where they come from; the test harness points these at a local server
    private static string YtDlpReleases => Env("SPOTIFY_RADIO_YTDLP_RELEASES") ?? "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    private static string FfmpegReleases => Env("SPOTIFY_RADIO_FFMPEG_RELEASES") ?? "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";

    // ------------------------------------------------------------------ asking

    /// <summary>First run, YouTube chosen and the tools missing: says what is needed and offers to get it.</summary>
    public static async Task<bool> OfferAsync(CancellationToken ct)
    {
        Console.WriteLine($"""

            YouTube needs two programs. They are downloaded from their own GitHub releases into
            {Dir}
              yt-dlp  finds a track's audio   github.com/yt-dlp/yt-dlp
              ffmpeg  decodes it              github.com/BtbN/FFmpeg-Builds (LGPL build)
            Together {Sizes}.

            """);
        Console.Write("Download them now? [Y/n] ");
        var answer = Console.ReadLine();
        if (answer is null || answer.Trim().Length > 0 && !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase)) return false;
        return await InstallAsync(update: false, ct);
    }

    /// <summary>Gets what is missing (or, with <paramref name="update"/>, a new yt-dlp). False when it failed; the reason is logged.</summary>
    public static async Task<bool> InstallAsync(bool update, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotifyRadio-daemon");
        try
        {
            Directory.CreateDirectory(Dir);
            if (update || YtDlp is null) await InstallYtDlpAsync(http, ct);
            if (Ffmpeg is null) await InstallFfmpegAsync(http, ct);
            Log.Info("YouTube: yt-dlp and ffmpeg are ready.");
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException
                                      or SpotifyException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Log.Info("YouTube: the download failed: " + e.Message);
            return false;
        }
    }

    /// <summary>"--install-youtube" (what is missing) and "--update-youtube" (a new yt-dlp) on the command line.</summary>
    public static async Task<int> CommandAsync(bool update)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        return await InstallAsync(update, cts.Token) ? 0 : 1;
    }

    // ------------------------------------------------------------------ the two programs

    private static async Task InstallYtDlpAsync(HttpClient http, CancellationToken ct)
    {
        const string name = "yt-dlp_win.zip";
        var sums = await SumsAsync(http, YtDlpReleases + "SHA2-256SUMS", ct);
        if (!sums.TryGetValue(name, out var expected)) throw new SpotifyException($"{name} is not in yt-dlp's checksum list");
        var zip = await DownloadAsync(http, YtDlpReleases + name, "yt-dlp", expected, ct);
        try
        {
            // the zip holds yt-dlp.exe and its _internal folder, no folder around them
            Replace(Path.Combine(Dir, "yt-dlp_win"), target => ZipFile.ExtractToDirectory(zip, target));
        }
        finally { TryDelete(zip); }
    }

    private static async Task InstallFfmpegAsync(HttpClient http, CancellationToken ct)
    {
        var sums = await SumsAsync(http, FfmpegReleases + "checksums.sha256", ct);
        var name = PickFfmpeg(sums.Keys) ?? throw new SpotifyException("no Windows LGPL ffmpeg build in BtbN's latest release");
        var zip = await DownloadAsync(http, FfmpegReleases + name, "ffmpeg", sums[name], ct);
        try
        {
            // only ffmpeg.exe, the libraries it loads and the licence: no ffplay, ffprobe, headers or docs
            Replace(Path.Combine(Dir, "ffmpeg"), target =>
            {
                Directory.CreateDirectory(target);
                using var archive = ZipFile.OpenRead(zip);
                var found = false;
                foreach (var entry in archive.Entries)
                {
                    var parts = entry.FullName.Split('/');
                    var file = parts[^1];
                    var take = parts.Length == 3 && parts[1] == "bin" && (file == "ffmpeg.exe" || file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                               || parts.Length == 2 && file == "LICENSE.txt";
                    if (!take) continue;
                    entry.ExtractToFile(Path.Combine(target, file == "LICENSE.txt" ? "LICENSE-ffmpeg.txt" : file), overwrite: true);
                    found |= file == "ffmpeg.exe";
                }
                if (!found) throw new InvalidDataException($"no bin/ffmpeg.exe in {name}");
            });
        }
        finally { TryDelete(zip); }
    }

    /// <summary>The newest stable branch's Windows LGPL shared build, else the master one.</summary>
    public static string? PickFfmpeg(IEnumerable<string> names)
    {
        var stable = new Regex(@"^ffmpeg-n(\d+)\.(\d+)-latest-win64-lgpl-shared-[\d.]+\.zip$");
        var best = names
            .Select(n => (Name: n, M: stable.Match(n)))
            .Where(x => x.M.Success)
            .OrderByDescending(x => int.Parse(x.M.Groups[1].Value))
            .ThenByDescending(x => int.Parse(x.M.Groups[2].Value))
            .Select(x => x.Name)
            .FirstOrDefault();
        return best ?? names.FirstOrDefault(n => n == "ffmpeg-master-latest-win64-lgpl-shared.zip");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A published checksum list: "hex  name" per line.</summary>
    private static async Task<Dictionary<string, string>> SumsAsync(HttpClient http, string url, CancellationToken ct)
    {
        var text = await http.GetStringAsync(url, ct);
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64) sums[parts[1].TrimStart('*')] = parts[0].ToLowerInvariant();
        }
        if (sums.Count == 0) throw new SpotifyException("empty checksum list at " + url);
        return sums;
    }

    /// <summary>Downloads into the yt folder with a progress line and checks the SHA-256. Returns the file.</summary>
    private static async Task<string> DownloadAsync(HttpClient http, string url, string label, string sha256, CancellationToken ct)
    {
        var file = Path.Combine(Dir, label + ".download");
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(file);
            var buffer = new byte[1 << 16];
            long done = 0;
            var shownMb = -1L;
            int n;
            while ((n = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (done / 1048576 == shownMb) continue;
                shownMb = done / 1048576;
                Console.Write(total > 0 ? $"\r  {label}: {shownMb} of {total / 1048576} MB   " : $"\r  {label}: {shownMb} MB   ");
            }
            Console.WriteLine();
        }
        await using (var check = File.OpenRead(file))
        {
            var got = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            if (got != sha256)
            {
                TryDelete(file);
                throw new InvalidDataException($"{label}: the download does not match its published checksum");
            }
        }
        return file;
    }

    /// <summary>Builds the new folder next to the old one, then swaps them, so a failure leaves the old one working.</summary>
    private static void Replace(string folder, Action<string> build)
    {
        var fresh = folder + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, recursive: true);
        build(fresh);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.Move(fresh, folder);
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
