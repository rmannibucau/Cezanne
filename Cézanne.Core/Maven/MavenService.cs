using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace Cézanne.Core.Maven
{
    public class MavenService : IDisposable
    {
        private readonly MavenConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly ILogger<MavenService> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

        public MavenService(MavenConfiguration configuration, ILogger<MavenService> logger)
        {
            _logger = logger;
            _configuration = configuration;

            LocalRepository = _InitializeM2();

            _HttpHeaders = new Dictionary<string, IDictionary<string, string>>();
            _ParseHeadersConfiguration();

            // todo: more configuration, custom pem etc
            HttpClientHandler messageHandler =
                new()
                {
                    AllowAutoRedirect = true,
                    MaxAutomaticRedirections = 5,
                    AutomaticDecompression = DecompressionMethods.GZip,
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
                };
            _httpClient = new HttpClient(messageHandler)
            {
                Timeout = new TimeSpan(0, 0, configuration.Timeout)
            };
        }

        public string LocalRepository { get; }

        private IDictionary<string, IDictionary<string, string>> _HttpHeaders { get; }

        public void Dispose()
        {
            _httpClient.Dispose();
            foreach (var it in locks)
            {
                it.Value.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        public async Task<string> FindOrDownload(string gav, IProgress<double>? onProgress)
        {
            var semaphore = locks.GetOrAdd(gav, _ => new SemaphoreSlim(1));
            await semaphore.WaitAsync();
            try
            {
                var result = await _DoFind(_RemoveRepoIfPresent(gav), onProgress);
                semaphore.Release();
                return result;
            }
            catch (Exception)
            {
                semaphore.Release();
                throw;
            }
        }

        private async Task<string> _DoFind(string raw, IProgress<double>? onProgress)
        {
            var segments = raw[(raw.IndexOf('!') + 1)..].Split(':');
            if (segments.Length < 3)
            {
                if (Directory.Exists(raw))
                {
                    return raw;
                }

                throw new ArgumentException($"Invalid location: {raw}");
            }

            var group = segments[0];
            if (group.Trim().Length == 0)
            {
                throw new ArgumentException($"Invalid groupId: {raw}", nameof(raw));
            }

            var artifact = segments[1];
            if (artifact.Trim().Length == 0)
            {
                throw new ArgumentException($"Invalid artifactId: {raw}", nameof(raw));
            }

            string type;
            if (segments.Length >= 4 && segments[3].Trim().Length > 0)
            {
                type = segments[3];
            }
            else
            {
                type = "jar";
            }

            string? fullClassifier;
            if (segments.Length >= 5 && segments[4].Trim().Length > 0)
            {
                fullClassifier = "-" + segments[4];
            }
            else
            {
                fullClassifier = null;
            }

            var version = segments[2];
            if (version.Trim().Length == 0)
            {
                throw new ArgumentException($"Invalid version: {raw}", nameof(raw));
            }

            var file = Path.Combine(
                LocalRepository,
                _ToRelativePath(null, group, artifact, version, fullClassifier, type, version)
            );
            if (File.Exists(file))
            {
                _logger.LogTrace("Found {file}, skipping download", file);
                return file;
            }

            string repoBase;
            var sep = raw.LastIndexOf('!');
            if (sep > 0)
            {
                repoBase = raw[..sep];
            }
            else
            {
                repoBase = version.EndsWith("-SNAPSHOT")
                    ? _configuration.SnapshotRepository
                        ?? throw new InvalidOperationException("No snapshot repository configured")
                    : _configuration.ReleaseRepository;
            }

            var resolvedVersion = await _FindVersion(repoBase, group, artifact, version);
            if (!_configuration.EnableDownload)
            {
                throw new InvalidOperationException(
                    $"Downloads are disabled, cant download: '{raw}'"
                );
            }

            return await _Download(
                group,
                artifact,
                resolvedVersion,
                fullClassifier,
                type,
                _ToRelativePath(
                    repoBase,
                    group,
                    artifact,
                    resolvedVersion,
                    fullClassifier,
                    type,
                    version
                ),
                version,
                onProgress
            );
        }

        private async Task<string> _Download(
            string group,
            string artifact,
            string resolvedVersion,
            string? fullClassifier,
            string type,
            string url,
            string version,
            IProgress<double>? onProgress
        )
        {
            _logger.LogInformation("Downloading {url}", url);
            var local = Path.Combine(
                LocalRepository,
                _ToRelativePath(
                    null,
                    group,
                    artifact,
                    resolvedVersion,
                    fullClassifier,
                    type,
                    version
                )
            );
            Directory.GetParent(local)?.Create();

            using var response = await _GET(new Uri(url));
            response.EnsureSuccessStatusCode();
            long size = -1;
            if ((response.Content.Headers.ContentLength ?? -1) > 0)
            {
                size = response.Content.Headers.ContentLength ?? 0;
            }

            using FileStream localStream = new(local, FileMode.Create);
            using var content = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[65_536];
            int read;
            long current = 0;
            try
            {
                while (true)
                {
                    read = await content.ReadAsync(buffer).ConfigureAwait(false);
                    if (read is 0)
                    {
                        break;
                    }

                    await localStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    if (size > 0 && onProgress is not null)
                    {
                        current += read;
                        var percent = (double)current / size;
                        if (percent < 100)
                        {
                            onProgress.Report(percent);
                        }
                    }
                }
            }
            finally
            {
                onProgress?.Report(100); // when progress was broken cause size was not known just report it is done
            }

            return local;
        }

        private async Task<string> _FindVersion(
            string repoBase,
            string group,
            string artifact,
            string version
        )
        {
            var baseUrl =
                repoBase == null || repoBase.Length == 0
                    ? ""
                    : repoBase + (!repoBase.EndsWith("/") ? "/" : "");
            if (("LATEST" == version || "LATEST-SNAPSHOT" == version) && baseUrl.StartsWith("http"))
            {
                Uri meta =
                    new(baseUrl + group.Replace('.', '/') + "/" + artifact + "/maven-metadata.xml");
                try
                {
                    var xml = await _LoadMeta(meta);
                    if (version.EndsWith("-SNAPSHOT"))
                    {
                        return xml.SelectSingleNode("/metadata/versioning/latest")?.InnerText
                            ?? version;
                    }

                    return xml.SelectSingleNode("/metadata/versioning/release")?.InnerText
                        ?? version;
                }
                catch (Exception e)
                {
                    _logger.LogError(
                        "Can't fetch latest version from {baseUrl}, will default to {version}\n{error}",
                        baseUrl,
                        version,
                        e
                    );
                    return version;
                }
            }

            if (version.EndsWith("-SNAPSHOT") && baseUrl.StartsWith("http"))
            {
                Uri meta =
                    new(
                        baseUrl
                            + group.Replace('.', '/')
                            + '/'
                            + artifact
                            + '/'
                            + version
                            + "/maven-metadata.xml"
                    );
                try
                {
                    var xml = await _LoadMeta(meta);
                    var snapshot = xml.SelectSingleNode("/metadata/versioning/snapshot");
                    if (snapshot is not null)
                    {
                        var buildNumber = snapshot.SelectNodes("./buildNumber");
                        var timestamp = snapshot.SelectNodes("./timestamp");
                        if (buildNumber is not null && timestamp is not null)
                        {
                            return string.Join(
                                '-',
                                version[..(version.Length - "-SNAPSHOT".Length)],
                                timestamp,
                                buildNumber
                            );
                        }
                    }

                    return version;
                }
                catch (Exception e)
                {
                    _logger.LogError(
                        "Can't fetch latest version from {baseUrl}, will default to {version}\n{error}",
                        baseUrl,
                        version,
                        e
                    );
                    return version;
                }
            }

            return version;
        }

        private async Task<HttpResponseMessage> _GET(Uri uri)
        {
            var response = await _httpClient.SendAsync(
                _SetupHttpHeaders(
                    uri.Host,
                    new HttpRequestMessage { Method = HttpMethod.Get, RequestUri = uri }
                ),
                HttpCompletionOption.ResponseHeadersRead
            );
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Invalid {uri} response: {response}");
            }
            ;
            return response;
        }

        private async Task<XmlDocument> _LoadMeta(Uri meta)
        {
            using var response = await _GET(meta);
            var content = await response.Content.ReadAsStringAsync();
            XmlDocument xml = new();
            xml.LoadXml(content);
            return xml;
        }

        private string _ToRelativePath(
            string? baseUrl,
            string group,
            string artifact,
            string version,
            string? classifier,
            string type,
            string rootVersion
        )
        {
            StringBuilder builder =
                new(
                    baseUrl == null
                        ? ""
                        : baseUrl.EndsWith("/")
                            ? baseUrl
                            : baseUrl + '/'
                );
            builder.Append(group.Replace('.', '/')).Append('/');
            builder.Append(artifact).Append('/');
            builder.Append(rootVersion).Append('/');
            builder.Append(artifact).Append('-').Append(version);
            if (!string.IsNullOrWhiteSpace(classifier))
            {
                builder.Append(classifier);
            }

            return builder.Append('.').Append(type).ToString();
        }

        private string _RemoveRepoIfPresent(string url)
        {
            var sep = url.IndexOf('!') + 1;
            if (sep != 0)
            {
                return url[..sep] + url[sep..].Replace(':', '/');
            }

            return url;
        }

        public string? FindSettingsXml()
        {
            var settings =
                _configuration.PreferLocalSettingsXml || _configuration.ForceCustomSettingsXml
                    ? Path.Combine(LocalRepository, "settings.xml")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".m2/settings.xml"
                    );
            if (!File.Exists(settings))
            {
                settings =
                    _configuration.PreferLocalSettingsXml || !_configuration.ForceCustomSettingsXml
                        ? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".m2/settings.xml"
                        )
                        : Path.Combine(LocalRepository, "settings.xml");
                if (!File.Exists(settings))
                {
                    throw new ArgumentException(
                        $"No {settings} found, ensure your credentials configuration is valid"
                    );
                }
            }

            return settings;
        }

        public Server? FindMavenServer(string serverId)
        {
            var settingsXml = FindSettingsXml();
            if (settingsXml is null)
            {
                return null;
            }

            var settingsSecurity = Path.Combine(settingsXml, "../settings-security.xml");
            if (settingsSecurity is null)
            {
                return null;
            }

            XmlDocument xml = new();
            xml.Load(settingsXml);

            var servers = xml.DocumentElement?.SelectNodes("/settings/servers/server");
            if (servers is null)
            {
                return null;
            }

            for (var i = 0; i < servers.Count; i++)
            {
                var node = servers[i];

                if (node?.SelectSingleNode("./id")?.InnerText == serverId)
                {
                    return new Server(
                        node.SelectSingleNode("./username")?.InnerText,
                        node.SelectSingleNode("./password")?.InnerText
                    );
                }
            }

            return null;
        }

        public string? FindMasterPassword()
        {
            var settingsXml = FindSettingsXml() ?? throw new ArgumentException("no settings.xml");
            var settingsSecurity = Path.Combine(settingsXml, "../settings-security.xml");
            if (settingsSecurity is null)
            {
                return null;
            }

            XmlDocument xml = new();
            xml.Load(settingsSecurity);
            return xml.DocumentElement?.SelectSingleNode("/settingsSecurity/master")?.Value;
        }

        // very simplified java properties parsing since here we just deal with headers
        private void _ParseHeadersConfiguration()
        {
            if (_configuration.HttpHeaders is null)
            {
                return;
            }

            foreach (var line in _configuration.HttpHeaders.Split("\n"))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("#") || line.Contains('='))
                {
                    continue;
                }

                var sep = line.IndexOf('=');

                var valueSep = line[(sep + 1)..].TrimStart().IndexOf('=');
                var valueBuilder = ImmutableDictionary.CreateBuilder<string, string>();
                valueBuilder.Add(
                    line[(sep + 1)..valueSep].Trim(),
                    line[(valueSep + 1)..].TrimStart()
                );

                _HttpHeaders.Add(line[..sep].Trim(), valueBuilder.ToImmutable());
            }
        }

        private string _InitializeM2()
        {
            if (
                _configuration.LocalRepository is not null
                && _configuration.LocalRepository != "auto"
            )
            {
                return _configuration.LocalRepository;
            }

            var m2 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".m2/repository"
            );
            var settingsXml = Path.Combine(m2, "settings.xml");
            if (File.Exists(settingsXml))
            {
                try
                {
                    var content = File.ReadAllText(settingsXml);
                    var start = content.IndexOf("<localRepository>");
                    if (start > 0)
                    {
                        var end = content.IndexOf("</localRepository>", start);
                        if (end > 0)
                        {
                            var localM2RepositoryFromSettings = content[
                                (start + "<localRepository>".Length)..end
                            ];
                            if (!string.IsNullOrWhiteSpace(localM2RepositoryFromSettings))
                            {
                                m2 = localM2RepositoryFromSettings;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(
                        "An error occured loading '{settingsXml}':\n{e}",
                        settingsXml,
                        e
                    );
                }
            }

            return m2;
        }

        private HttpRequestMessage _SetupHttpHeaders(string host, HttpRequestMessage request)
        {
            if (_HttpHeaders.TryGetValue(host, out var found))
            {
                foreach (var it in found)
                {
                    request.Headers.Add(it.Key, it.Value);
                }
            }
            else
            {
                try
                {
                    if (
                        FindMavenServer("bundlebee." + host) is
                        { Username: not null, Password: not null } server
                    )
                    {
                        var key = $"{server.Username}:{server.Password}";
                        request.Headers.Add(
                            "Authorization",
                            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(key))}"
                        );
                    }
                }
                catch (Exception re)
                {
                    _logger.LogDebug(
                        "Can't look up for a maven server for host {host}:\n{re}",
                        host,
                        re
                    );
                }
            }

            return request;
        }

        public record Server(string? Username, string? Password) { }
    }
}
