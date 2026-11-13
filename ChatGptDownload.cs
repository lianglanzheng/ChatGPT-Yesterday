#!/usr/bin/env -S dotnet run
#:property TargetFramework=net10.0

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    if (!Console.IsInputRedirected)
        throw new InvalidDataException("Pipe the JSON output of ChatGptVersions.cs into this program.");

    var response = await JsonSerializer.DeserializeAsync(
        Console.OpenStandardInput(), DownloadJsonContext.Default.CatalogResponse, cancellation.Token)
        ?? throw new InvalidDataException("Missing catalog response.");
    if (response.Error is not null)
        throw new InvalidDataException($"Catalog query failed: {response.Error}");

    var installers = Catalog.Validate(response.Installers);
    using var terminal = Terminal.Open();
    var menu = new InstallerMenu(installers, terminal, cancellation.Token);
    while (true)
    {
        var installer = await menu.SelectAsync();
        if (installer is null)
            return 0;

        Console.WriteLine($"\nDownload URL: {installer.Url}");
        if (!await terminal.ConfirmAsync("Download this installer?", cancellation.Token))
            continue;

        var destination = Path.Combine(Environment.CurrentDirectory, InstallerDownload.FileName(new Uri(installer.Url)));
        var overwrite = false;
        if (File.Exists(destination))
        {
            overwrite = await terminal.ConfirmAsync($"File already exists: {destination}\nOverwrite it?", cancellation.Token);
            if (!overwrite)
                continue;
        }

        await InstallerDownload.SaveAsync(new Uri(installer.Url), destination, overwrite, cancellation.Token);
        return 0;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(cancellation.IsCancellationRequested ? "\nCancelled." : "\nDownload timed out.");
    return cancellation.IsCancellationRequested ? 130 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nError: {ex.Message}");
    return 1;
}

static class Catalog
{
    public static Installer[] Validate(Installer[] installers)
    {
        if (installers is null || installers.Length == 0)
            throw new InvalidDataException("The catalog contains no installers.");

        foreach (var installer in installers)
        {
            if (installer is null || installer.Os is not ("windows" or "macos" or "linux")
                || string.IsNullOrWhiteSpace(installer.Arch) || string.IsNullOrWhiteSpace(installer.Version)
                || string.IsNullOrWhiteSpace(installer.Format)
                || new[] { installer.Arch, installer.Version, installer.Format, installer.Url }
                    .Any(value => value is null || value.Any(char.IsControl))
                || !Uri.TryCreate(installer.Url, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
                throw new InvalidDataException("The catalog contains an invalid installer.");
        }

        return installers.Distinct().ToArray();
    }
}

sealed class InstallerMenu(Installer[] installers, Terminal terminal, CancellationToken cancellationToken)
{
    private const int PageSize = 10;
    private static readonly string[] Labels = ["OS", "architecture", "version", "format"];
    private readonly string[] selections = new string[4];
    private readonly int[] pages = new int[4];
    private int level;

    public async Task<Installer?> SelectAsync()
    {
        while (true)
        {
            var candidates = installers.Where(installer => Enumerable.Range(0, level)
                .All(index => Field(installer, index) == selections[index])).ToArray();
            var options = candidates.Select(installer => Field(installer, level)).Distinct().ToArray();
            if (level == 2)
                Array.Sort(options, (left, right) => VersionOrder.Compare(right, left, selections[0]));
            else
                Array.Sort(options, StringComparer.Ordinal);

            var pageCount = (options.Length + PageSize - 1) / PageSize;
            pages[level] = Math.Clamp(pages[level], 0, pageCount - 1);
            var offset = pages[level] * PageSize;
            var visible = options.Skip(offset).Take(PageSize).ToArray();
            var context = level == 0 ? "" : $" ({string.Join(" / ", selections.Take(level))})";
            Console.WriteLine($"\nSelect {Labels[level]}{context} - page {pages[level] + 1}/{pageCount}");
            for (var index = 0; index < visible.Length; index++)
                Console.WriteLine($"{(index + 1) % 10,2}. {visible[index]}");

            var commands = new List<string>();
            if (pages[level] + 1 < pageCount)
                commands.Add("n: next page");
            if (pages[level] > 0)
                commands.Add("p: previous page");
            if (level > 0)
                commands.Add("b: back");
            commands.Add("q: quit");
            Console.WriteLine(string.Join(" | ", commands));
            var input = (await terminal.ReadAsync("Press a key: ", cancellationToken))?.Trim().ToLowerInvariant();
            if (input is null or "q")
                return null;

            if (input == "b" && level > 0)
            {
                level--;
                continue;
            }

            if (input == "n" && pages[level] + 1 < pageCount)
            {
                pages[level]++;
                continue;
            }

            if (input == "p" && pages[level] > 0)
            {
                pages[level]--;
                continue;
            }

            var choice = Choice(input);
            if (choice < 1 || choice > visible.Length)
            {
                Console.WriteLine("Press a listed key (0 selects item 10).");
                continue;
            }

            selections[level] = visible[choice - 1];
            if (level < 3)
            {
                pages[++level] = 0;
                continue;
            }

            var matches = candidates.Where(installer => installer.Format == selections[3]).ToArray();
            if (matches.Length == 1)
                return matches[0];

            var selected = await SelectUrlAsync(matches);
            if (selected.Quit)
                return null;
            if (selected.Installer is not null)
                return selected.Installer;
        }
    }

    private async Task<(Installer? Installer, bool Quit)> SelectUrlAsync(Installer[] matches)
    {
        var page = 0;
        var pageCount = (matches.Length + PageSize - 1) / PageSize;

        while (true)
        {
            var visible = matches.Skip(page * PageSize).Take(PageSize).ToArray();
            Console.WriteLine($"\nMultiple download URLs - page {page + 1}/{pageCount}");
            for (var index = 0; index < visible.Length; index++)
                Console.WriteLine($"{(index + 1) % 10,2}. {visible[index].Url}");

            Console.WriteLine("n: next page | p: previous page | b: back | q: quit");
            var input = (await terminal.ReadAsync("Press a key: ", cancellationToken))?.Trim().ToLowerInvariant();
            if (input is null or "q")
                return (null, true);
            if (input == "b")
                return (null, false);
            if (input == "n" && page + 1 < pageCount)
                page++;
            else if (input == "p" && page > 0)
                page--;
            else if (Choice(input) is var choice && choice >= 1 && choice <= visible.Length)
                return (visible[choice - 1], false);
            else
                Console.WriteLine("Press a listed key (0 selects item 10).");
        }
    }

    private static int Choice(string? input) => input switch
    {
        "0" => 10,
        { Length: 1 } when input[0] is >= '1' and <= '9' => input[0] - '0',
        _ => -1
    };

    private static string Field(Installer installer, int level) => level switch
    {
        0 => installer.Os,
        1 => installer.Arch,
        2 => installer.Version,
        _ => installer.Format
    };
}

static class VersionOrder
{
    public static int Compare(string left, string right, string os)
    {
        if (os == "macos")
        {
            var build = CompareParts(left.Split('+').Last(), right.Split('+').Last());
            if (build != 0)
                return build;
        }

        var a = left.Split(':', 2);
        var b = right.Split(':', 2);
        var epoch = CompareNumber(a.Length == 2 ? a[0] : "0", b.Length == 2 ? b[0] : "0");
        var result = epoch != 0 ? epoch : CompareParts(a[^1], b[^1]);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left, right);
    }

    private static int CompareParts(string left, string right)
    {
        var a = Regex.Matches(left, @"~|\^|[0-9]+|[A-Za-z]+").Select(match => match.Value).ToArray();
        var b = Regex.Matches(right, @"~|\^|[0-9]+|[A-Za-z]+").Select(match => match.Value).ToArray();
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var x = index < a.Length ? a[index] : "";
            var y = index < b.Length ? b[index] : "";
            if (x == y)
                continue;
            if (x == "~" || y == "~")
                return x == "~" ? -1 : 1;
            if (x.Length == 0 || y.Length == 0)
                return x.Length == 0 ? -1 : 1;
            if (x == "^" || y == "^")
                return x == "^" ? -1 : 1;

            var numericX = char.IsAsciiDigit(x[0]);
            var numericY = char.IsAsciiDigit(y[0]);
            var comparison = numericX && numericY ? CompareNumber(x, y)
                : numericX != numericY ? (numericX ? 1 : -1) : StringComparer.Ordinal.Compare(x, y);
            if (comparison != 0)
                return comparison;
        }

        return 0;
    }

    private static int CompareNumber(string left, string right)
    {
        left = left.TrimStart('0');
        right = right.TrimStart('0');
        return left.Length != right.Length ? left.Length.CompareTo(right.Length) : StringComparer.Ordinal.Compare(left, right);
    }
}

sealed class Terminal(Func<CancellationToken, ValueTask<char?>> readKey, Action close) : IDisposable
{
    public static Terminal Open()
    {
        if (OperatingSystem.IsWindows())
            return OpenWindows();

        var path = "/dev/tty";
        var savedMode = SetUnixMode("-g");
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            SetUnixMode("-icanon", "-echo", "min", "1", "time", "0");
            return new Terminal(token => ReadUnixKeyAsync(handle, token), () =>
            {
                try { SetUnixMode(savedMode); }
                finally { handle.Dispose(); }
            });
        }
        catch
        {
            handle.Dispose();
            SetUnixMode(savedMode);
            throw;
        }
    }

    private static Terminal OpenWindows()
    {
        var handle = CreateFileW("CONIN$", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetConsoleMode(handle, out var mode))
        {
            handle.Dispose();
            throw new IOException("An interactive console is required in addition to the JSON input pipe.");
        }

        if (!SetConsoleMode(handle, mode & ~0x0006u))
        {
            handle.Dispose();
            throw new IOException("Cannot enable single-key console input.");
        }

        return new Terminal(token => ReadWindowsKeyAsync(handle, token), () =>
        {
            SetConsoleMode(handle, mode);
            handle.Dispose();
        });
    }

    private static async ValueTask<char?> ReadWindowsKeyAsync(SafeFileHandle handle, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var status = WaitForSingleObject(handle, 0);
            if (status == 0)
            {
                if (!ReadConsoleInputW(handle, out var input, 1, out var count))
                    throw new IOException("Cannot read console input.");

                if (count == 1 && input.EventType == 1 && input.KeyDown != 0 && input.Character != '\0')
                    return input.Character;
            }
            else if (status != 258)
                throw new IOException("Cannot wait for console input.");
            else
                await Task.Delay(25, token);
        }
    }

    private static async ValueTask<char?> ReadUnixKeyAsync(SafeFileHandle handle, CancellationToken token)
    {
        var descriptor = new PollDescriptor { Descriptor = handle.DangerousGetHandle().ToInt32(), Events = 1 };
        var escape = false;
        var escapeStart = Stopwatch.StartNew();
        var buffer = new byte[1];

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (Poll(ref descriptor, 1, 0) < 0)
                throw new IOException("Cannot poll the terminal input device.");

            if ((descriptor.ReturnedEvents & 1) != 0)
            {
                if (Read(descriptor.Descriptor, buffer, 1) != 1)
                    return null;

                var key = (char)buffer[0];
                if (key == '\u001b')
                {
                    escape = true;
                    escapeStart.Restart();
                }
                else if (!escape && key < 128)
                    return key;
            }
            else if ((descriptor.ReturnedEvents & (8 | 16 | 32)) != 0)
                return null;
            else
            {
                if (escape && escapeStart.ElapsedMilliseconds >= 50)
                    escape = false;
                await Task.Delay(10, token);
            }
        }
    }

    private static string SetUnixMode(params string[] arguments)
    {
        var start = new ProcessStartInfo("stty")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(OperatingSystem.IsMacOS() ? "-f" : "-F");
        start.ArgumentList.Add("/dev/tty");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new IOException("Cannot start stty.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException($"An interactive terminal and stty are required: {error.Trim()}");

        return output.Trim();
    }

    public async Task<string?> ReadAsync(string prompt, CancellationToken cancellationToken)
    {
        Console.Write(prompt);
        while (true)
        {
            var key = await readKey(cancellationToken);
            if (key is null)
                return null;
            if (key is '\u0003')
                throw new OperationCanceledException(cancellationToken);
            if (key is '\r' or '\n' || char.IsControl(key.Value))
                continue;

            Console.WriteLine(key.Value);
            return key.Value.ToString();
        }
    }

    public async Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        while (true)
        {
            var answer = (await ReadAsync($"{prompt} [y/n]: ", cancellationToken))?.ToLowerInvariant();
            if (answer is null or "n" or "q")
                return false;
            if (answer == "y")
                return true;
            Console.WriteLine("Press y or n.");
        }
    }

    public void Dispose() => close();

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct ConsoleInput
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(14)] public char Character;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool ReadConsoleInputW(SafeFileHandle handle, out ConsoleInput input, uint length, out uint count);

    [DllImport("libc", EntryPoint = "poll")]
    private static extern int Poll(ref PollDescriptor descriptor, nuint count, int timeout);

    [DllImport("libc", EntryPoint = "read")]
    private static extern nint Read(int descriptor, [Out] byte[] buffer, nuint count);
}

static class InstallerDownload
{
    public static string FileName(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Any(char.IsControl)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')
            || (OperatingSystem.IsWindows() && (name.EndsWith('.') || name.EndsWith(' ')
                || Regex.IsMatch(name, @"\A(?:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)", RegexOptions.IgnoreCase))))
            throw new InvalidDataException("The URL does not contain a valid local filename.");

        return name;
    }

    public static async Task SaveAsync(Uri uri, string destination, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.part");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Installer-Downloader/1.0");
        Console.WriteLine($"Saving to: {destination}");

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var clock = Stopwatch.StartNew();
            var nextUpdate = TimeSpan.Zero;
            long received = 0;

            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[131072];
                using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                while (true)
                {
                    inactivity.CancelAfter(TimeSpan.FromMinutes(2));
                    var count = await input.ReadAsync(buffer, inactivity.Token);
                    if (count == 0)
                        break;

                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    received += count;
                    if (clock.Elapsed >= nextUpdate)
                    {
                        ShowProgress(received, total, clock.Elapsed, false);
                        nextUpdate = clock.Elapsed + TimeSpan.FromMilliseconds(250);
                    }
                }

                await output.FlushAsync(cancellationToken);
            }

            if (total.HasValue && received != total.Value)
                throw new IOException("The download size does not match Content-Length.");

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
            ShowProgress(received, total, clock.Elapsed, true);
            Console.WriteLine($"Downloaded: {destination}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void ShowProgress(long received, long? total, TimeSpan elapsed, bool complete)
    {
        var size = received / 1048576d;
        var speed = size / Math.Max(elapsed.TotalSeconds, 0.001);
        var progress = total is > 0 ? $"{100d * received / total.Value,6:F1}% | {size:F1}/{total.Value / 1048576d:F1} MiB"
            : $"{size:F1} MiB";
        var line = $"{progress} | {speed:F1} MiB/s";
        if (Console.IsOutputRedirected)
            Console.WriteLine(line);
        else
        {
            Console.Write("\r" + line.PadRight(78));
            if (complete)
                Console.WriteLine();
        }
    }
}

record Installer(string Os, string Arch, string Version, string Format, string Url);

record CatalogResponse(Installer[] Installers, string? Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(CatalogResponse))]
partial class DownloadJsonContext : JsonSerializerContext
{
}
