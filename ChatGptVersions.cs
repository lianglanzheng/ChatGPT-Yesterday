#!/usr/bin/env -S dotnet run
#:property TargetFramework=net10.0
#:package Microsoft.Data.Sqlite@11.0.0
#:package YamlDotNet@18.1.0

using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using YamlDotNet.RepresentationModel;

CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
Console.OutputEncoding = new UTF8Encoding(false);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
{
    Timeout = TimeSpan.FromMinutes(2),
    MaxResponseContentBufferSize = 128 * 1024 * 1024
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("ChatGPT-Version-Link-Viewer/1.0");
CatalogResponse response;
var exitCode = 0;

try
{
    var catalog = new InstallerCatalog(http, cancellation.Token);
    response = new(await catalog.ReadAsync(), null);
}
catch (Exception ex)
{
    var error = ex switch
    {
        OperationCanceledException => cancellation.IsCancellationRequested ? "CANCELLED" : "TIMEOUT",
        HttpRequestException => "NETWORK_ERROR",
        InvalidDataException or XmlException or YamlDotNet.Core.YamlException
            or FormatException or KeyNotFoundException or OverflowException => "INVALID_DATA",
        SqliteException => "INDEX_ERROR",
        IOException or UnauthorizedAccessException => "IO_ERROR",
        _ => "INTERNAL_ERROR"
    };
    response = new([], error);
    exitCode = error == "CANCELLED" ? 130 : 1;
}

Console.WriteLine(JsonSerializer.Serialize(response, CatalogJsonContext.Default.CatalogResponse));
return exitCode;

sealed class InstallerCatalog(HttpClient http, CancellationToken cancellationToken)
{
    private static readonly Uri WinGetSource = new("https://cdn.winget.microsoft.com/cache/");
    private static readonly Uri OfficialSource = new("https://persistent.oaistatic.com/codex-app-prod/");

    public async Task<Installer[]> ReadAsync()
    {
        var results = await Task.WhenAll(
            ReadWindowsAsync(),
            ReadMacAsync("appcast.xml", "arm64"),
            ReadMacAsync("appcast-x64.xml", "x64"),
            ReadDebAsync("amd64"),
            ReadDebAsync("arm64"),
            ReadRpmAsync("x86_64"),
            ReadRpmAsync("aarch64"),
            ReadArchAsync());
        return results.SelectMany(items => items).Distinct().ToArray();
    }

    private async Task<List<Installer>> ReadWindowsAsync()
    {
        var bytes = await DownloadAsync(new Uri(WinGetSource, "source.msix"));
        var manifests = await WinGetIndex.ReadAsync(bytes, WinGetSource, cancellationToken);
        var results = new List<Installer>();

        foreach (var manifest in manifests)
            results.AddRange(ParseWindowsManifest(await DownloadAsync(manifest.Url), manifest.Version));

        return RequireInstallers(results);
    }

    private static List<Installer> ParseWindowsManifest(byte[] bytes, string version)
    {
        var results = new List<Installer>();
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Encoding.UTF8.GetString(bytes)));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root
            || Scalar(root, "PackageIdentifier") != "OpenAI.ChatGPT" || Scalar(root, "PackageVersion") != version
            || Node(root, "Installers") is not YamlSequenceNode installers || installers.Children.Count == 0)
            throw new InvalidDataException("Invalid Windows installer manifest.");

        foreach (var node in installers)
        {
            if (node is not YamlMappingNode map)
                throw new InvalidDataException("Invalid Windows installer entry.");

            results.Add(CreateInstaller("windows", Scalar(map, "Architecture"), version,
                Scalar(map, "InstallerType") ?? Scalar(root, "InstallerType"),
                Scalar(map, "InstallerUrl") ?? Scalar(root, "InstallerUrl")));
        }

        return RequireInstallers(results);
    }

    private async Task<List<Installer>> ReadMacAsync(string file, string arch)
    {
        var document = ReadXml(await DownloadAsync(new Uri(OfficialSource, file)));
        return ParseMacFeed(document, arch);
    }

    private static List<Installer> ParseMacFeed(XDocument document, string arch)
    {
        XNamespace sparkle = "http://www.andymatuschak.org/xml-namespaces/sparkle";
        var results = new List<Installer>();

        foreach (var item in document.Root?.Element("channel")?.Elements("item") ?? [])
        {
            var version = Required((string?)item.Element(sparkle + "shortVersionString"));
            var build = Required((string?)item.Element(sparkle + "version"));
            var hardware = (string?)item.Element(sparkle + "hardwareRequirements");
            if (hardware is not null && NormalizeArchitecture(hardware) != arch)
                throw new InvalidDataException("Unexpected macOS architecture.");

            foreach (var enclosure in item.Elements("enclosure"))
            {
                if (enclosure.Attribute(sparkle + "deltaFrom") is not null)
                    continue;

                var url = Resolve(OfficialSource, (string?)enclosure.Attribute("url"));
                if (!url.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsupported macOS installer format.");

                results.Add(CreateInstaller("macos", arch, $"{version}+{build}", "zip", url.AbsoluteUri));
            }
        }

        return RequireInstallers(results);
    }

    private async Task<List<Installer>> ReadDebAsync(string arch)
    {
        var repository = new Uri(OfficialSource, "linux/deb/");
        var index = new Uri(repository, $"dists/stable/main/binary-{arch}/Packages.gz");
        var text = Encoding.UTF8.GetString(Decompress(await DownloadAsync(index)));
        return ParseDebIndex(text, repository, arch);
    }

    private static List<Installer> ParseDebIndex(string text, Uri repository, string arch)
    {
        var results = new List<Installer>();

        foreach (var paragraph in Regex.Split(text.Replace("\r\n", "\n"), @"\n[ \t]*\n"))
        {
            var fields = Regex.Matches(paragraph, @"(?m)^(?<key>[A-Za-z0-9-]+):[ \t]*(?<value>[^\n]*)$")
                .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value.Trim());
            if (fields.GetValueOrDefault("Package") != "chatgpt")
                continue;

            CheckArchitecture(fields.GetValueOrDefault("Architecture"), arch);
            results.Add(CreateInstaller("linux", arch, fields.GetValueOrDefault("Version"), "deb",
                Resolve(repository, fields.GetValueOrDefault("Filename")).AbsoluteUri));
        }

        return RequireInstallers(results);
    }

    private async Task<List<Installer>> ReadRpmAsync(string arch)
    {
        var repository = new Uri(OfficialSource, $"linux/rpm/{arch}/");
        var document = ReadXml(await DownloadAsync(new Uri(repository, "repodata/repomd.xml")));
        XNamespace repo = "http://linux.duke.edu/metadata/repo";
        var primary = document.Root?.Elements(repo + "data")
            .Where(element => (string?)element.Attribute("type") == "primary").ToArray() ?? [];
        if (primary.Length != 1)
            throw new InvalidDataException("Missing or ambiguous RPM primary metadata.");

        var url = Resolve(repository, (string?)primary[0].Element(repo + "location")?.Attribute("href"));
        var bytes = await DownloadAsync(url);
        if (url.AbsolutePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            bytes = Decompress(bytes);
        else if (!url.AbsolutePath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported RPM metadata compression.");

        return ParseRpmPackages(ReadXml(bytes), repository, arch);
    }

    private static List<Installer> ParseRpmPackages(XDocument document, Uri repository, string arch)
    {
        XNamespace common = "http://linux.duke.edu/metadata/common";
        var results = new List<Installer>();

        foreach (var package in document.Root?.Elements(common + "package") ?? [])
        {
            if ((string?)package.Element(common + "name") != "chatgpt")
                continue;

            CheckArchitecture((string?)package.Element(common + "arch"), arch);
            var version = package.Element(common + "version");
            var epoch = (string?)version?.Attribute("epoch");
            var upstream = Required((string?)version?.Attribute("ver"));
            var revision = Required((string?)version?.Attribute("rel"));
            var nativeVersion = $"{(epoch is null ? "" : epoch + ":")}{upstream}-{revision}";
            results.Add(CreateInstaller("linux", arch, nativeVersion, "rpm",
                Resolve(repository, (string?)package.Element(common + "location")?.Attribute("href")).AbsoluteUri));
        }

        return RequireInstallers(results);
    }

    private async Task<List<Installer>> ReadArchAsync()
    {
        var script = Encoding.UTF8.GetString(await DownloadAsync(new Uri(OfficialSource, "linux/install-arch.sh")));
        var versions = ParseArchVersions(script);
        var results = new List<Installer>();

        foreach (var version in versions.Distinct())
        {
            var packages = await Task.WhenAll(ReadArchDatabaseAsync(version, "x86_64"), ReadArchDatabaseAsync(version, "aarch64"));
            results.AddRange(packages.SelectMany(items => items));
        }

        return results;
    }

    private static string[] ParseArchVersions(string script)
    {
        var declarations = Regex.Matches(script, @"(?m)^versions=\((?<versions>[^)]*)\)[ \t]*\r?$");
        if (declarations.Count != 1)
            throw new InvalidDataException("Missing or ambiguous Arch versions array.");

        var versions = declarations[0].Groups["versions"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (versions.Length == 0 || versions.Length > 100
            || versions.Any(version => !Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){1,3}\z")))
            throw new InvalidDataException("Invalid Arch versions array.");

        return versions;
    }

    private async Task<List<Installer>> ReadArchDatabaseAsync(string version, string arch)
    {
        var repository = new Uri(OfficialSource, $"linux/arch/{version}/{arch}/");
        using var stream = new MemoryStream(Decompress(await DownloadAsync(new Uri(repository, "openai-chatgpt.db"))));
        using var archive = new TarReader(stream);
        var results = new List<Installer>();

        while (archive.GetNextEntry() is { } entry)
        {
            if (!entry.Name.EndsWith("/desc", StringComparison.Ordinal)
                || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
                continue;

            using var reader = new StreamReader(entry.DataStream, Encoding.UTF8, leaveOpen: true);
            var text = await reader.ReadToEndAsync(cancellationToken);
            if (ParseArchPackage(text, repository, version, arch) is { } installer)
                results.Add(installer);
        }

        return RequireInstallers(results);
    }

    private static Installer? ParseArchPackage(string text, Uri repository, string version, string arch)
    {
        var fields = Regex.Matches(text, @"(?m)^%(?<key>[A-Z0-9_]+)%\r?\n(?<value>[^\r\n]+)")
            .ToDictionary(match => match.Groups["key"].Value, match => match.Groups["value"].Value);
        if (fields.GetValueOrDefault("NAME") != "chatgpt-bin")
            return null;

        CheckArchitecture(fields.GetValueOrDefault("ARCH"), arch);
        var nativeVersion = Required(fields.GetValueOrDefault("VERSION"));
        if (!Regex.IsMatch(nativeVersion, @"\A(?:[0-9]+:)?" + Regex.Escape(version) + @"-[0-9]+(?:\.[0-9]+)*\z"))
            throw new InvalidDataException("Arch package version does not match its repository.");

        return CreateInstaller("linux", arch, nativeVersion, "pkg.tar.zst",
            Resolve(repository, fields.GetValueOrDefault("FILENAME")).AbsoluteUri);
    }

    private async Task<byte[]> DownloadAsync(Uri uri)
    {
        _ = Resolve(uri.Host == WinGetSource.Host ? WinGetSource : OfficialSource, uri.AbsoluteUri);
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;

        while ((count = gzip.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length + count > 64 * 1024 * 1024)
                throw new InvalidDataException("Metadata exceeds 64 MiB.");

            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    private static XDocument ReadXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024
        });
        return XDocument.Load(reader);
    }

    private static Installer CreateInstaller(string os, string? arch, string? version, string? format, string? url)
    {
        if (!Uri.TryCreate(Required(url), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Invalid HTTPS installer URL.");

        return new(os, NormalizeArchitecture(Required(arch)), Required(version), Required(format), uri.AbsoluteUri);
    }

    private static Uri Resolve(Uri root, string? path)
    {
        if (!Uri.TryCreate(root, Required(path), out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != root.Host || uri.Port != root.Port
            || !uri.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Invalid repository URL.");

        return uri;
    }

    private static List<Installer> RequireInstallers(List<Installer> installers) =>
        installers.Count > 0 ? installers : throw new InvalidDataException("No installers found.");

    private static string Required(string? value) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException("Missing package field.");

    private static void CheckArchitecture(string? actual, string expected)
    {
        if (actual != expected)
            throw new InvalidDataException("Unexpected package architecture.");
    }

    private static string NormalizeArchitecture(string value) => value.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" => "x64",
        "aarch64" => "arm64",
        _ => value.ToLowerInvariant()
    };

    private static YamlNode? Node(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    private static string? Scalar(YamlMappingNode map, string key) => (Node(map, key) as YamlScalarNode)?.Value;
}

static class WinGetIndex
{
    public static async Task<List<Manifest>> ReadAsync(byte[] package, Uri source, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("chatgpt-winget-");

        try
        {
            var path = Path.Combine(directory.FullName, "index.db");
            await ExtractAsync(package, path, cancellationToken);
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            database.Open();
            CheckSchema(database);

            var manifests = ReadRows(database, cancellationToken)
                .Select(row => new Manifest(row.Version, ResolvePath(database, source, row.PathId, cancellationToken)))
                .OrderByDescending(manifest => Version.Parse(manifest.Version))
                .ToList();
            if (manifests.Count == 0)
                throw new InvalidDataException("OpenAI.ChatGPT was not found in the community index.");

            return manifests;
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task ExtractAsync(byte[] package, string path, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var entry = archive.GetEntry("Public/index.db")
            ?? throw new InvalidDataException("The source package does not contain Public/index.db.");
        if (entry.Length > 256 * 1024 * 1024)
            throw new InvalidDataException("The source index exceeds the supported size limit.");

        using var input = entry.Open();
        using var output = File.Create(path);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void CheckSchema(SqliteConnection database)
    {
        using var command = database.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE name = 'majorVersion'";
        if (Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) != "1")
            throw new InvalidDataException("Unsupported WinGet index schema. This app supports schema 1.x.");
    }

    private static List<(string Version, long PathId)> ReadRows(SqliteConnection database, CancellationToken cancellationToken)
    {
        using var command = database.CreateCommand();
        command.CommandText = """
            SELECT v.version, m.pathpart
            FROM manifest m
            JOIN ids i ON i.rowid = m.id
            JOIN versions v ON v.rowid = m.version
            JOIN channels c ON c.rowid = m.channel
            WHERE i.id = $id COLLATE NOCASE AND c.channel = ''
            """;
        command.Parameters.AddWithValue("$id", "OpenAI.ChatGPT");
        using var reader = command.ExecuteReader();
        var rows = new List<(string, long)>();

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows;
    }

    private static Uri ResolvePath(SqliteConnection database, Uri source, long? pathId, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var visited = new HashSet<long>();

        while (pathId.HasValue)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(pathId.Value) || visited.Count > 64)
                throw new InvalidDataException("Invalid manifest path hierarchy in the index.");

            using var command = database.CreateCommand();
            command.CommandText = "SELECT parent, pathpart FROM pathparts WHERE rowid = $id";
            command.Parameters.AddWithValue("$id", pathId.Value);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                throw new InvalidDataException("A manifest path component is missing.");

            var part = reader.GetString(1);
            if (string.IsNullOrEmpty(part) || part is "." or ".." || part.Contains('/') || part.Contains((char)92))
                throw new InvalidDataException("Invalid manifest path component.");

            parts.Add(Uri.EscapeDataString(part));
            pathId = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        }

        parts.Reverse();
        return new Uri(source, string.Join("/", parts));
    }

    public sealed record Manifest(string Version, Uri Url);
}

record Installer(string Os, string Arch, string Version, string Format, string Url);

record CatalogResponse(Installer[] Installers, string? Error);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CatalogResponse))]
partial class CatalogJsonContext : JsonSerializerContext
{
}
