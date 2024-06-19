using Cézanne.Core.Descriptor;
using Cézanne.Core.K8s;
using Cézanne.Core.Maven;
using Cézanne.Core.Runtime;
using HandlebarsDotNet;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using System.Collections.Specialized;
using System.Configuration;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cézanne.Core.Interpolation
{
    // probably not the best bet but has some history in Yupiik bundlebee and minimum is to inherit from this behavior
    // todo: enhance to behave more as a c# interpolation
    public sealed class Substitutor(
        [FromKeyedServices("cezannePlaceholderLookupCallback")]
        Func<string, string?, string?> lookup,
        K8SClient? k8s,
        MavenService? maven)
    {
        private const char Escape = '\\';
        private const string Prefix = "{{";
        private const string Suffix = "}}";
        private const string ValueDelimiter = ":-";

        private const int MaxIterations = 100;

        [ThreadStatic]
        private static IDictionary<string, string> contextualPlaceholders = ImmutableDictionary<string, string>.Empty;

        public Substitutor(Func<string, string?> lookup, K8SClient? k8s, MavenService? maven) : this(
            (key, _) => lookup(key), k8s, maven)
        {
        }

        private bool SkipConfiguration { get; set; }

        public T WithContext<T>(IDictionary<string, string> placeholders, Func<T> provider)
        {
            var old = contextualPlaceholders;
            contextualPlaceholders = placeholders;
            try
            {
                return provider();
            }
            finally
            {
                contextualPlaceholders = old;
            }
        }

        public string Replace(Manifest.Recipe? recipe, LoadedDescriptor? desc, string source, string? id)
        {
            return _DoReplace(recipe, desc, source, id) ?? "";
        }

        private string? _DoReplace(Manifest.Recipe? alveolus, LoadedDescriptor? desc, string? source, string? id)
        {
            if (source == null)
            {
                return null;
            }

            if (desc is not null && ("hb" == desc.Extension || "handlebars" == desc.Extension))
            {
                return _Handlebars(alveolus, desc, source, id);
            }

            var current = source;
            do
            {
                var previous = current;
                current = _Substitute(alveolus, desc, current, 0, id);
                if (previous == current)
                {
                    return previous.Replace(Escape + Prefix, Prefix);
                }
            } while (true);
        }

        private string _Substitute(Manifest.Recipe? alveolus, LoadedDescriptor? descriptor, string input, int iteration,
            string? id)
        {
            if (iteration > MaxIterations)
            {
                return input;
            }

            var from = 0;
            var start = -1;
            while (from < input.Length)
            {
                start = input.IndexOf(Prefix, from, StringComparison.Ordinal);
                if (start < 0)
                {
                    return input;
                }

                if (start == 0 || input[start - 1] != Escape)
                {
                    break;
                }

                from = start + 1;
            }

            var keyStart = start + Prefix.Length;
            var end = input.IndexOf(Suffix, keyStart, StringComparison.Ordinal);
            if (end < 0)
            {
                return input;
            }

            var key = input.Substring(start + Prefix.Length, end - (start + Prefix.Length));
            var nested = key.IndexOf(Prefix, StringComparison.Ordinal);
            if (nested >= 0 && !(nested > 0 && key[nested - 1] == Escape))
            {
                var nestedPlaceholder = key + Suffix;
                var newKey = _Substitute(alveolus, descriptor, nestedPlaceholder, iteration + 1, id);
                return input.Replace(nestedPlaceholder, newKey);
            }

            var startOfString = input[..start];
            var endOfString = input[(end + Suffix.Length)..];

            var sep = key.IndexOf(ValueDelimiter, StringComparison.Ordinal);
            if (sep > 0)
            {
                var actualKey = key[..sep];
                var fallback = key[(sep + ValueDelimiter.Length)..];
                return startOfString + _DoGetOrDefault(alveolus, descriptor, actualKey, fallback, id) + endOfString;
            }

            return startOfString + _DoGetOrDefault(alveolus, descriptor, key, null, id) + endOfString;
        }

        private string _Handlebars(Manifest.Recipe? recipe, LoadedDescriptor? desc, string source, string? id)
        {
            var hb = Handlebars.Create();

            HandlebarsHelper base64 = (output, context, args) =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes((string)args[1]));
            using (hb.Configure())
            {
                hb.RegisterHelper("base64", base64);
                hb.RegisterHelper("base64Url", base64); // not accurate, FIXME
                // todo: register fallback helper doing a standard substitution for strings to reuse all built-in subs
            }

            return hb.Compile(source)(new
            {
                // todo: if adopted we should move to IDictionnary which is way faster
                recipe, alveolus = recipe, descriptor = desc, executionId = id ?? ""
            });
        }

        private string _DoGetOrDefault(Manifest.Recipe? alveolus, LoadedDescriptor? descriptor, string varName,
            string? varDefaultValue, string? id)
        {
            return varName switch
            {
                "executionId" => id,
                "descriptor.name" => descriptor?.Configuration.Name,
                "alveolus.name" => alveolus?.Name,
                "alveolus.version" => alveolus?.Version,
                _ => _DoLookup(alveolus, descriptor, varName, varDefaultValue, id)
            } ?? "";
        }

        private string? _DoLookup(Manifest.Recipe? alveolus, LoadedDescriptor? desc, string varName,
            string? varDefaultValue, string? id)
        {
            try
            {
                return _DefaultLookups(alveolus, desc, varName, id) ??
                       lookup(varName, varDefaultValue) ?? varDefaultValue;
            }
            catch (Exception)
            {
                if (varDefaultValue != null)
                {
                    return varDefaultValue;
                }

                throw;
            }
        }

        private string? _DefaultLookups(Manifest.Recipe? recipe, LoadedDescriptor? desc, string varName, string? id)
        {
            return varName switch
            {
                "bundlebee-kubernetes-namespace" => k8s?.DefaultNamespace ?? "default",
                "timestamp" => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds.ToString(CultureInfo
                    .InvariantCulture),
                "timestampSec" => (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds.ToString(CultureInfo
                    .InvariantCulture),
                "now" => DateTime.Now.ToString("o"),
                "nowUTC" => DateTime.UtcNow.ToString("o"),
                _ => _DefaultLookupsWithParameters(recipe, desc, varName, id)
            };
        }

        // todo: for now it is implemented as in bundlebee but it can be neat to enable an IoC extension point
        private string? _DefaultLookupsWithParameters(Manifest.Recipe? recipe, LoadedDescriptor? desc,
            string placeholder,
            string? id)
        {
            if (placeholder.StartsWith("bundlebee-directory-json-key-value-pairs-content:"))
            {
                var result = Replace(
                    recipe, desc,
                    "{{bundlebee-directory-json-key-value-pairs:" +
                    placeholder["bundlebee-directory-json-key-value-pairs-content:".Length..] + "}}", id).Trim();
                return result[1..^1]; // drop brackets
            }

            if (placeholder.StartsWith("bundlebee-directory-json-key-value-pairs:"))
            {
                // enable easy injection of labels or more likely annotations
                var directory = placeholder["bundlebee-directory-json-key-value-pairs:".Length..];
                // we support the pattern "/my/dir" and will take all subfiles or "/my/dir/*.ext" and will filter files by a glob pattern
                var lastSep = directory.LastIndexOf("/*", StringComparison.Ordinal);
                var files =
                    (lastSep < 0 ? new DirectoryInfo(directory) : new DirectoryInfo(directory[..lastSep]))
                    .GetFiles(lastSep < 0 ? "*" : directory[(lastSep + 1)..],
                        new EnumerationOptions
                        {
                            MatchType = MatchType.Simple,
                            AttributesToSkip = FileAttributes.None,
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = false,
                            ReturnSpecialDirectories = false
                        });
                var data = files.Aggregate(new SortedDictionary<string, string>(),
                    (agg, file) =>
                    {
                        agg.Add(file.Name.Replace("____", "/"), File.ReadAllText(file.FullName));
                        return agg;
                    });
                return JsonSerializer.Serialize(data,
                    new JsonSerializerOptions() /* cache meta per execution since it is volatile data - for now */);
            }

            if (placeholder.StartsWith("bundlebee-inline-file:"))
            {
                var path = placeholder["bundlebee-inline-file:".Length..];
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }

            if (placeholder.StartsWith("bundlebee-inlined-file:"))
            {
                var path = placeholder["bundlebee-inlined-file:".Length..];
                return File.Exists(path) ? File.ReadAllText(path).Replace("\n", " ") : null;
            }

            if (placeholder.StartsWith("bundlebee-quote-escaped-inlined-file:"))
            {
                var path = placeholder["bundlebee-inlined-file:".Length..];
                return File.Exists(path)
                    ? File.ReadAllText(path)
                        .Replace("'", "\\'")
                        .Replace("\"", "\\\"")
                        .Replace("\n", "\\\\n")
                    : null;
            }

            if (placeholder.StartsWith("bundlebee-base64-file:"))
            {
                var path = placeholder["bundlebee-base64-file:".Length..];
                return File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null;
            }

            if (placeholder.StartsWith("bundlebee-base64-decode-file:"))
            {
                var path = placeholder["bundlebee-base64-decode-file:".Length..];
                return File.Exists(path)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path)))
                    : null;
            }

            if (placeholder.StartsWith("bundlebee-base64:"))
            {
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(placeholder["bundlebee-base64:".Length..]));
            }

            if (placeholder.StartsWith("bundlebee-base64-decode:"))
            {
                return Encoding.UTF8.GetString(
                    Convert.FromBase64String(placeholder["bundlebee-base64-decode:".Length..]));
            }

            if (placeholder.StartsWith("bundlebee-json-inline-file:"))
            {
                var path = placeholder["bundlebee-json-inline-file:".Length..];
                if (!File.Exists(path))
                {
                    return null;
                }

                var result = Replace(recipe, desc, File.ReadAllText(path), id);
                using MemoryStream memoryStream = new();
                using (Utf8JsonWriter utf8JsonWriter = new(memoryStream,
                           new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    utf8JsonWriter.WriteStringValue(result);
                }

                return Encoding.UTF8.GetString(memoryStream.ToArray())[1..^1];
            }

            if (placeholder.StartsWith("bundlebee-json-string:"))
            {
                using MemoryStream memoryStream = new();
                using (Utf8JsonWriter utf8JsonWriter = new(memoryStream,
                           new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    utf8JsonWriter.WriteStringValue(placeholder["bundlebee-json-string:".Length..]);
                }

                return Encoding.UTF8.GetString(memoryStream.ToArray())[1..^1];
            }

            if (placeholder.StartsWith("bundlebee-strip:"))
            {
                return placeholder["bundlebee-strip:".Length..].Trim();
            }

            if (placeholder.StartsWith("bundlebee-strip-leading:"))
            {
                return placeholder["bundlebee-strip-leading:".Length..].TrimStart();
            }

            if (placeholder.StartsWith("bundlebee-strip-trailing:"))
            {
                return placeholder["bundlebee-strip-trailing:".Length..].TrimEnd();
            }

            if (placeholder.StartsWith("bundlebee-uppercase:"))
            {
                return placeholder["bundlebee-uppercase:".Length..].ToUpperInvariant();
            }

            if (placeholder.StartsWith("bundlebee-lowercase:"))
            {
                return placeholder["bundlebee-lowercase:".Length..].ToLowerInvariant();
            }

            if (placeholder.StartsWith("date:"))
            {
                return DateTime.Now.ToString(placeholder["date:".Length..]);
            }

            if (placeholder.StartsWith("bundlebee-digest:"))
            {
                var text = placeholder["bundlebee-digest:".Length..];
                var sep1 = text.IndexOf(',');
                var sep2 = text.IndexOf(',', sep1 + 1);
                var digestAlgo = text[(sep1 + 1)..sep2].Trim().ToUpperInvariant();
                var encoding = text[..sep1].Trim();

                using var digest = _FindHashAlgorithm(digestAlgo);
                var value = digest.ComputeHash(Encoding.UTF8.GetBytes(text[(sep2 + 1)..].Trim()));
                return encoding switch
                {
                    "base64" => Convert.ToBase64String(value),
                    _ => throw new ArgumentException($"Unknown encoding '{encoding}'")
                };
            }

            if (placeholder.StartsWith("bundlebee-decipher:"))
            {
                var confAndValue = placeholder["bundlebee-decipher:".Length..];
                var sep = confAndValue.IndexOf(',');
                if (sep < 0)
                {
                    throw new ArgumentException("Usage: {{bundlebee-decipher:$masterKeyPlaceholder,$cipheredValue}}");
                }

                return new SimpleCodec(confAndValue[..sep]).Decipher(confAndValue[(sep + 1)..].Trim());
            }

            if (placeholder.StartsWith("bundlebee-indent:"))
            {
                var sub = placeholder["bundlebee-indent:".Length..];
                var sep = sub.IndexOf(':');
                if (sep < 0)
                {
                    return sub;
                }

                return _Indent(sub[(sep + 1)..], new string(' ', int.Parse(sub[..sep])), true);
            }


            if (placeholder.StartsWith("kubeconfig.cluster.") && placeholder.EndsWith(".ip"))
            {
                if (k8s == null)
                {
                    return null;
                }

                var name = placeholder["kubeconfig.cluster.".Length..(placeholder.Length - ".ip".Length)];
                var url = k8s.KubeConfig == null
                    ? k8s.Base
                    : k8s.KubeConfig.Clusters?
                        .FirstOrDefault(it => it.Name == name,
                            new KubeConfig.NamedCluster { Cluster = new KubeConfig.Cluster { Server = k8s.Base } })
                        .Cluster?.Server;
                return new Uri(url ?? "http://localhost:8080").Host;
            }

            if (placeholder.StartsWith("kubernetes."))
            {
                var value = _FindKubernetesValue(placeholder, "\\.");
                if (value != null)
                {
                    return value;
                }
            }

            // depending data key entry name we can switch the separator depending first one
            if (placeholder.StartsWith("kubernetes/"))
            {
                var value = _FindKubernetesValue(placeholder, "\\.");
                if (value != null)
                {
                    return value;
                }
            }

            if (placeholder.StartsWith("bundlebee-maven-server-username:"))
            {
                return maven?.FindMavenServer(placeholder["bundlebee-maven-server-username:".Length..])?.Username;
            }

            if (placeholder.StartsWith("bundlebee-maven-server-password:"))
            {
                return maven?.FindMavenServer(placeholder["bundlebee-maven-server-password:".Length..])?.Password;
            }

            if (SkipConfiguration) // we already checked it was not filled so skip
            {
                return null;
            }

            NameValueCollection? nvc;
            try
            {
                nvc = ConfigurationManager.GetSection("cezanne") as NameValueCollection;
            }
            catch (ConfigurationErrorsException)
            {
                try
                {
                    nvc = ConfigurationManager.AppSettings;
                }
                catch (ConfigurationErrorsException)
                {
                    SkipConfiguration =
                        true; // theorically would need to be "locked" but in practise it does not change much
                    return null;
                }
            }

            return nvc?[placeholder];
        }

        private string? _FindKubernetesValue(string key, string sep)
        {
            // depending the key we should accept both
            var segments = key.Split(sep);
            if (segments.Length >= 8 && segments.Length <= 10 && segments is
                    ["kubernetes", _, "serviceaccount", _, "secrets", _, "data", ..])
            {
                // kubernetes.<namespace>.serviceaccount.<account name>.secrets.<secret name prefix>.data.<entry name>[.<timeout in seconds>]
                var namespaceName = segments[1];
                var account = segments[3];
                var secretPrefix = segments[5];
                var dataName = segments[7];
                var timeout = segments.Length == 9 ? int.Parse(segments[8]) : 120;
                var secret = _FindSecret(namespaceName, account, secretPrefix, dataName, timeout);
                if (segments.Length == 10 && secret is not null)
                {
                    return _Indent(secret, new string(' ', int.Parse(segments[9])), false);
                }

                return secret;
            }

            return null;
        }

        // todo: move to async - but doesnt it make the substitutor too much async?
        private string? _FindSecret(string namespaceValue, string account, string secretPrefix,
            string dataKey, int timeout)
        {
            var iterations = 0;
            JsonSerializerOptions jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

            var end = DateTime.Now.AddSeconds(timeout);
            do
            {
                iterations++;
                using var result = k8s
                    ?.SendAsync(HttpMethod.Get, $"/api/v1/namespaces/{namespaceValue}/serviceaccounts/{account}")
                    .GetAwaiter()
                    .GetResult();
                if (result?.StatusCode != HttpStatusCode.OK)
                {
                    // todo: better handle this, if code == xx => throw
                    Thread.Sleep(500);
                    continue;
                }

                // todo: validate casting
                var serviceAccountJson = result?.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var serviceAccount =
                    JsonSerializer.Deserialize<JsonObject>(serviceAccountJson ?? "{}", jsonOptions);
                var secrets = serviceAccount?["secrets"];
                if (secrets is IEnumerable<JsonObject> objs)
                {
                    var selectedSecret =
                        objs.First(it => it["name"]?.ToString().StartsWith(secretPrefix) ?? false);
                    using var secretResponse = k8s?.SendAsync(HttpMethod.Get,
                            $"/api/v1/namespaces/{namespaceValue}/serviceaccounts/{selectedSecret["name"]}")
                        .GetAwaiter()
                        .GetResult();
                    if (secretResponse?.StatusCode != HttpStatusCode.OK)
                    {
                        // todo: better handle this, if code == xx => throw
                        Thread.Sleep(500);
                        continue;
                    }

                    var secretObj = JsonSerializer.Deserialize<JsonObject>(
                        secretResponse?.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}", jsonOptions);
                    if (secretObj?.ContainsKey("data") is false)
                    {
                        continue;
                    }

                    var data = secretObj?["data"]?.AsObject();
                    var value = data?[dataKey];
                    if (value is not null)
                    {
                        return Encoding.UTF8.GetString(Convert.FromBase64String(value.ToString()));
                    }
                }
            } while (DateTime.Now < end);

            return null;
        }

        private string _Indent(string raw, string indent, bool indentFirstLine)
        {
            var lines = raw.Split('\n');
            if (indentFirstLine)
            {
                return string.Join('\n', lines.Select(it => indent + it));
            }

            return string.Join('\n', [lines[0], .. lines.Length > 1 ? lines[1..].Select(it => indent + it) : []])
                .Trim();
        }

        private HashAlgorithm _FindHashAlgorithm(string digestAlgo)
        {
            return digestAlgo switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA384" => SHA384.Create(),
                "SHA512" => SHA512.Create(),
                _ => throw new ArgumentException($"Unkown digest '{digestAlgo}'")
            };
        }
    }
}