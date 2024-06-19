using Cézanne.Core.Maven;
using Cézanne.Core.Tests.Rule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cézanne.Core.Tests.Maven
{
    public class MavenTests : ITempFolder
    {
        public string? Temp { get; set; }

        [Test]
        [TempFolder]
        public void FindServer()
        {
            MavenService maven =
                new(
                    new MavenConfiguration
                    {
                        PreferLocalSettingsXml = false,
                        ForceCustomSettingsXml = true,
                        LocalRepository = Temp ?? throw new ArgumentNullException("Temp")
                    }, new Logger<MavenService>(new NullLoggerFactory()));

            Directory.CreateDirectory(Temp);
            var settings = Path.Combine(Temp, "settings.xml");
            File.WriteAllText(settings, """
                                        <settings>
                                          <servers>
                                            <server>
                                              <id>test</id>
                                              <username>usr</username>
                                              <password>pwd</password>
                                            </server>
                                          </servers>
                                        </settings>
                                        """);

            var server = maven.FindMavenServer("test");
            Assert.Multiple(() =>
            {
                Assert.That(server, Is.Not.Null);
                Assert.That(server?.Username, Is.EqualTo("usr"));
                Assert.That(server?.Password, Is.EqualTo("pwd"));
            });
        }

        [Test]
        [TempFolder]
        public async Task Download()
        {
            var mockServer = WebApplication.Create();
            mockServer.Urls.Add("http://127.0.0.1:0");
            mockServer.MapGet("/io/yupiik/bundlebee/test/1.2.3/test-1.2.3.jar", () => "worked");
            await mockServer.StartAsync();

            using var server = mockServer;
            using MavenService maven = new(
                new MavenConfiguration
                {
                    PreferLocalSettingsXml = false,
                    ForceCustomSettingsXml = true,
                    EnableDownload = true,
                    ReleaseRepository = mockServer.Urls.First(),
                    LocalRepository = Temp ?? throw new ArgumentNullException("Temp")
                }, new Logger<MavenService>(new NullLoggerFactory()));

            var expectedLocal = Path.Combine(Temp, "io/yupiik/bundlebee/test/1.2.3/test-1.2.3.jar");
            Directory.GetParent(expectedLocal)?.Create();
            Assert.That(File.Exists(expectedLocal), Is.False, "{0} exist", expectedLocal);

            var downloaded = await maven.FindOrDownload("io.yupiik.bundlebee:test:1.2.3", null);
            Assert.Multiple(() =>
            {
                Assert.That(downloaded, Is.EqualTo(expectedLocal));
                Assert.That(File.Exists(downloaded), Is.True, "{0} does not exist", downloaded);
                Assert.That(File.ReadAllText(downloaded), Is.EqualTo("worked"));
            });
        }
    }
}