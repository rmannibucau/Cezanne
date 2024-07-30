using System.IO.Compression;
using System.Text;
using Cezanne.Core.Interpolation;
using Cezanne.Core.Service;
using Cezanne.Core.Tests.Rule;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cezanne.Core.Tests.Service
{
    [FixtureLifeCycle(LifeCycle.SingleInstance)]
    public class ArchiveReaderTests : ITempFolder
    {
        private readonly ArchiveReader reader =
            new(
                new Logger<ArchiveReader>(new NullLoggerFactory()),
                new ManifestReader(new Substitutor(static _ => null, null, null)),
                null,
                null
            );

        public string? Temp { get; set; }

        [Test]
        [TempFolder]
        public void Read()
        {
            var yaml =
                "apiVersion: v1\n"
                + "kind: Service\n"
                + "metadata:\n"
                + "  name: foo\n"
                + "  labels:\n"
                + "    app: foo\n"
                + "spec:\n"
                + "  type: NodePort\n"
                + "  ports:\n"
                + "   - port: 1234\n"
                + "     targetPort: 1234\n"
                + "  selector:\n"
                + "   app: foo\n";

            var zipLocation = Path.Combine(
                Temp ?? throw new ArgumentException("Temp is null", nameof(Temp)),
                "test.zip"
            );

            using (var zip = ZipFile.Open(zipLocation, ZipArchiveMode.Create))
            {
                zip.CreateEntry("bundlebee/").Open().Close();
                zip.CreateEntry("bundlebee/kubernetes/").Open().Close();
                using (var manifestJson = zip.CreateEntry("bundlebee/manifest.json").Open())
                {
                    manifestJson.Write(
                        Encoding.UTF8.GetBytes(
                            "{"
                                + "  \"alveoli\":["
                                + "    {"
                                + "      \"name\": \"test\","
                                + "      \"descriptors\":["
                                + "        {"
                                + "          \"name\": \"foo\","
                                + "          \"location\": \"com.company:alv:1.0.0\""
                                + "        }"
                                + "      ]"
                                + "    }"
                                + "  ]"
                                + "}"
                        )
                    );
                }

                using (var manifestJson = zip.CreateEntry("bundlebee/kubernetes/foo.yaml").Open())
                {
                    manifestJson.Write(Encoding.UTF8.GetBytes(yaml));
                }
            }

            var archive = reader.Read("test ignored", zipLocation, null);
            Assert.That(archive.Manifest.Recipes.Count, Is.EqualTo(1));

            var recipe = archive.Manifest.Recipes.First();
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
                Assert.That(archive.Descriptors["bundlebee/kubernetes/foo.yaml"], Is.EqualTo(yaml));
            });
        }
    }
}
