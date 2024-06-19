using Cézanne.Core.Descriptor;
using Cézanne.Core.Interpolation;
using Cézanne.Core.Service;
using Cézanne.Core.Tests.Rule;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;

namespace Cézanne.Core.Tests.Service
{
    public class ManifestReaderTests : ITempFolder
    {
        private readonly ManifestReader reader = new(new Substitutor(static k => null, null, null));

        public string? Temp { get; set; }

        [Test]
        public void Read()
        {
            var manifest = reader.ReadManifest(null, () => new MemoryStream(Encoding.UTF8.GetBytes("{" +
                "  \"alveoli\":[" +
                "    {" +
                "      \"name\": \"test\"," +
                "      \"descriptors\":[" +
                "        {" +
                "          \"name\": \"foo\"," +
                "          \"location\": \"com.company:alv:1.0.0\"" +
                "        }" +
                "      ]" +
                "    }" +
                "  ]" +
                "}")), s => throw new ArgumentException(s, nameof(s)), null);
            _AssertManifest(manifest);
        }

        [Test]
        [TempFolder]
        public void ReferencedZip()
        {
            var zipLocation = Path.Combine(Temp ?? throw new ArgumentException("Temp is null", nameof(Temp)),
                "ReferencedZip.zip");
            Directory.CreateDirectory(Temp).Create();
            using (ZipArchive zip = new(File.Open(zipLocation, FileMode.CreateNew), ZipArchiveMode.Create))
            {
                zip.CreateEntry("bundlebee/").Open().Close(); // create the directory

                using (var dependencyManifest = zip.CreateEntry("bundlebee/manifest.json").Open())
                {
                    dependencyManifest.Write(Encoding.UTF8.GetBytes("""
                                                                    {
                                                                     "references":[{"path":"ref1.json"}],
                                                                     "alveoli":[{"name":"main"}]
                                                                    }
                                                                    """));
                }

                using var dependencyRef = zip.CreateEntry("bundlebee/ref1.json").Open();
                dependencyRef.Write(Encoding.UTF8.GetBytes("{\"alveoli\":[{\"name\":\"ref1-alveolus\"}]}"));
            }

            using var zipReader = ZipFile.OpenRead(zipLocation);
            var manifest = reader.ReadManifest(
                null,
                () => zipReader.GetEntry("bundlebee/manifest.json")?.Open() ??
                      throw new ArgumentException($"no manifest in {zipLocation}", nameof(zipLocation)),
                path => zipReader.GetEntry($"bundlebee/{path}")?.Open() ??
                        throw new ArgumentException($"no manifest in bundlebee/{path}", nameof(path)),
                "test");
            ImmutableList<string> recipeNames = (from it in manifest.Recipes select it.Name).ToImmutableList();
            Assert.That(recipeNames, Is.EqualTo((IList<string>) ["main", "ref1-alveolus"]));
        }

        private void _AssertManifest(Manifest manifest)
        {
            Assert.That(manifest.Recipes.Count, Is.EqualTo(1));

            var recipe = manifest.Recipes.First();
            Assert.Multiple(() =>
            {
                Assert.That(recipe.Name, Is.EqualTo("test"));
                Assert.That((recipe.Descriptors ?? []).Count, Is.EqualTo(1));
            });

            var descriptor = recipe.Descriptors?.First();
            Assert.Multiple(() =>
            {
                Assert.That(descriptor?.Type, Is.EqualTo("kubernetes"));
                Assert.That(descriptor?.Name, Is.EqualTo("foo"));
                Assert.That(descriptor?.Location, Is.EqualTo("com.company:alv:1.0.0"));
            });
        }
    }
}