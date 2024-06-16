using Cézanne.Core.Descriptor;
using Cézanne.Core.Maven;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.IO.Compression;

namespace Cézanne.Core.Service
{
    public class ArchiveReader(ILogger<ArchiveReader> logger, ManifestReader manifestReader, MavenService? maven)
    {
        protected readonly MavenService? _maven = maven;

        public Archive Read(string coords, string path, string? id)
        {
            logger.LogTrace("Reading {location}", path);
            if (Directory.Exists(path))
            {
                string folderManifest = Path.Combine(path, "bundlebee/manifest.json");
                if (File.Exists(folderManifest))
                {
                    Manifest manifestJson = manifestReader.ReadManifest(
                        Path.GetFullPath(path),
                        () => File.OpenRead(folderManifest),
                        reference =>
                        {
                            if (Path.Exists(reference))
                            {
                                return File.OpenRead(reference);
                            }

                            string computed = Path.Combine(Path.Combine(path, "bundlebee"), reference);
                            if (File.Exists(computed))
                            {
                                return File.OpenRead(computed);
                            }

                            throw new ArgumentException($"Missing file {reference}", nameof(reference));
                        },
                        id);

                    // list potential descriptors to propagate them upfront
                    Dictionary<string, string> descriptors = Directory
                        .EnumerateFiles(Path.Combine(path, "bundlebee/kubernetes"), "*.*", SearchOption.AllDirectories)
                        .Where(it => it.EndsWith(".json") ||
                                     it.EndsWith(".yml") || it.EndsWith(".yaml") ||
                                     it.EndsWith(".hb") || it.EndsWith(".handlebars"))
                        .Aggregate(new Dictionary<string, string>(), (agg, it) =>
                        {
                            agg[Path.GetRelativePath(path, it)] = File.ReadAllText(it);
                            return agg;
                        });
                    return new Archive(path, manifestJson, descriptors);
                }
            }

            // else assume a zip
            using ZipArchive zip = ZipFile.OpenRead(path);
            ZipArchiveEntry manifest = zip.GetEntry("bundlebee/manifest.json") ??
                                       throw new ArgumentException($"Missing manifest in '{path}'", nameof(path));
            Manifest mf = manifestReader.ReadManifest(
                coords,
                manifest.Open,
                relative =>
                {
                    string entry = relative.StartsWith('/') ? relative : $"bundlebee/{relative}";
                    return (zip
                            .GetEntry(entry) ?? throw new ArgumentException($"No entry '{relative}' in '{path}'",
                            nameof(relative)))
                        .Open();
                },
                id);
            Dictionary<string, string> zipDescriptors = zip.Entries
                .Where(entry => entry.FullName.StartsWith("bundlebee/kubernetes/") && !entry.FullName.EndsWith('/'))
                .Aggregate(new Dictionary<string, string>(), (agg, it) =>
                {
                    using (StreamReader stream = new(it.Open()))
                    {
                        agg[it.FullName] = stream.ReadToEnd();
                    }

                    return agg;
                });
            return new Archive(coords, mf, zipDescriptors);
        }

        public Cache NewCache()
        {
            return new Cache(this);
        }

        public record Archive(string Location, Manifest Manifest, IDictionary<string, string> Descriptors)
        {
        }

        public class Cache(ArchiveReader archiveReader)
        {
            private readonly ArchiveReader _archiveReader = archiveReader;
            private readonly ConcurrentDictionary<string, Task<Archive>> _archives = new();

            public Task<Archive> LoadArchive(string coord, string? id)
            {
                return _archives.AddOrUpdate(coord, key => _DoLoadArchive(key, id), (_, old) => old);
            }

            private async Task<Archive> _DoLoadArchive(string coord, string? id)
            {
                if (Path.Exists(coord))
                {
                    return _archiveReader.Read(coord, coord, id);
                }

                string zip = await (_archiveReader._maven ??
                                    throw new ArgumentException("Missing maven service", nameof(_archiveReader)))
                    .FindOrDownload(coord);
                return _archiveReader.Read(coord, zip, id);
            }
        }
    }
}