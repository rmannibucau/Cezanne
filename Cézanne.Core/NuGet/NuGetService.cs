using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;

namespace Cézanne.Core.Maven
{
    public class NuGetService : IDisposable
    {
        private readonly NuGetConfiguration _configuration;
        private readonly HttpClient _httpClient;
        private readonly ILogger<MavenService> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

        private volatile string? _baseUrl = null;

        public NuGetService(NuGetConfiguration configuration, ILogger<MavenService> logger)
        {
            _logger = logger;
            _configuration = configuration;

            LocalRepository = _InitializeLocal();

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
                var result = await _DoFind(gav, onProgress);
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
            var segments = raw.Split(':');
            if (segments.Length < 2)
            {
                if (Directory.Exists(raw))
                {
                    return raw;
                }

                throw new ArgumentException($"Invalid location: {raw}");
            }

            var module = segments[0].ToLowerInvariant();
            if (module.Trim().Length == 0)
            {
                throw new ArgumentException($"Invalid module: {raw}", nameof(raw));
            }

            var version = segments[1];
            if (version.Trim().Length == 0)
            {
                throw new ArgumentException($"Invalid version: {raw}", nameof(raw));
            }

            var file = Path.Combine(LocalRepository, _ToPath(null, module, version));
            if (File.Exists(file))
            {
                _logger.LogTrace("Found {file}, skipping download", file);
                return file;
            }

            _baseUrl ??= await _FindBaseUrl();

            var resolvedVersion = await _FindVersion(module, version);
            if (!_configuration.EnableDownload)
            {
                throw new InvalidOperationException(
                    $"Downloads are disabled, cant download: '{raw}'"
                );
            }

            return await _Download(module, resolvedVersion, onProgress);
        }

        private async Task<string> _FindBaseUrl()
        {
            using (var indexResponse = await _GET(new Uri(_configuration.Repository)))
            {
                indexResponse.EnsureSuccessStatusCode();

                var payload = await indexResponse.Content.ReadAsStringAsync();
                var json = JsonSerializer.Deserialize(
                    payload,
                    CezanneJsonContext.Default.JsonObject
                );
                if (!(json?.TryGetPropertyValue("resources", out var resources) ?? false))
                {
                    throw new InvalidOperationException(
                        "Can't find PackageBaseAddress/3.0.0 type in\n" + payload
                    );
                }

                return resources!
                    .AsArray()
                    .Select(it => it!.AsObject())
                    .Where(it =>
                        it.TryGetPropertyValue("@type", out var type)
                        && type is not null
                        && type.ToString() == "PackageBaseAddress/3.0.0"
                    )
                    .Select(it =>
                        it.TryGetPropertyValue("@id", out var url)
                            ? url!.ToString()
                            : throw new InvalidOperationException("No @id in " + it)
                    )
                    .First();
            }
        }

        private async Task<string> _Download(
            string module,
            string resolvedVersion,
            IProgress<double>? onProgress
        )
        {
            var url = _ToPath(_baseUrl!, module, resolvedVersion);
            _logger.LogInformation("Downloading {url}", url);

            using var response = await _GET(new Uri(url));
            response.EnsureSuccessStatusCode();
            long size = -1;
            if ((response.Content.Headers.ContentLength ?? -1) > 0)
            {
                size = response.Content.Headers.ContentLength ?? 0;
            }

            var local = Path.Combine(LocalRepository, _ToPath(null, module, resolvedVersion));
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
                onProgress?.Report(100);
            }

            return local;
        }

        private async Task<string> _FindVersion(string module, string version)
        {
            if ("*" == version) // todo: better support of versions, for now we just handle "LATEST" like
            {
                var baseUrl =
                    _baseUrl!.Length == 0 ? "" : _baseUrl + (!_baseUrl.EndsWith('/') ? "/" : "");
                Uri index = new(baseUrl + module + "/index.json");
                var versions = await _GET(index);
                versions.EnsureSuccessStatusCode();

                var json = JsonSerializer.Deserialize(
                    await versions.Content.ReadAsStringAsync(),
                    CezanneJsonContext.Default.JsonObject
                );
                if (json!.TryGetPropertyValue("versions", out var list) && list is not null)
                {
                    return list.AsArray()
                        .Select(it => it!.ToString())
                        // no pre-release
                        .Where(it => !it.Contains("-"))
                        .Select(rawVersion =>
                        {
                            var segments = rawVersion.Split('.');
                            return (
                                segments.Length switch
                                {
                                    2
                                        => new Version(
                                            int.Parse(segments[0]),
                                            int.Parse(segments[1])
                                        ),
                                    3
                                        => new Version(
                                            int.Parse(segments[0]),
                                            int.Parse(segments[1]),
                                            int.Parse(segments[2])
                                        ),
                                    _ => new Version(rawVersion)
                                },
                                rawVersion
                            );
                        })
                        .OrderBy(it => it.Item1)
                        .First()
                        .rawVersion;
                }

                throw new InvalidOperationException($"Can't find latest version for '{module}'");
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

        private string _ToPath(string? baseUrl, string module, string version)
        {
            StringBuilder builder =
                new(
                    baseUrl == null
                        ? ""
                        : baseUrl.EndsWith("/")
                            ? baseUrl
                            : baseUrl + '/'
                );
            builder.Append(module).Append('/');
            builder.Append(version).Append('/');
            builder.Append(module).Append('.').Append(version).Append(".nupkg");
            return builder.ToString();
        }

        // very simplified java properties parsing since here we just deal with headers
        // FIXME?
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

        private string _InitializeLocal()
        {
            if (
                _configuration.LocalRepository is not null
                && _configuration.LocalRepository != "auto"
            )
            {
                return _configuration.LocalRepository;
            }

            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget/packages"
            );
            // todo: read ~/.nuget/NuGet/NuGet.Config, it contains packageSources for ex we can extract urls from

            return local;
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
            return request;
        }
    }
}
