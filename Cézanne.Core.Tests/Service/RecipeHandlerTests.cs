using System.IO.Compression;
using System.Text;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Interpolation;
using Cézanne.Core.Maven;
using Cézanne.Core.Runtime;
using Cézanne.Core.Service;
using Cézanne.Core.Tests.Rule;
using Microsoft.Extensions.Logging;

namespace Cézanne.Core.Tests.Service
{
    [FixtureLifeCycle(LifeCycle.SingleInstance)]
    public class RecipeHandlerTests : ITempFolder
    {
        public string? Temp { get; set; }

        [Test]
        [TempFolder]
        public async Task Visit()
        {
            // setup IoC
            var (maven, recipeHandler, archiveReader) = _NewServices();
            using var mvn = maven;

            // create a fake recipe
            var zipLocation = _CreateZip(maven);

            // visit the recipe
            var recipes = await recipeHandler.FindRootRecipes(
                zipLocation,
                null,
                "test",
                "test",
                null
            );
            Assert.That(recipes.Count(), Is.EqualTo(1));

            List<LoadedDescriptor> visited = new();
            foreach (var recipe in recipes)
            {
                await recipeHandler.ExecuteOnRecipe(
                    "Visiting ",
                    recipe.Manifest,
                    recipe.Recipe,
                    null,
                    (ctx, desc) =>
                    {
                        lock (visited)
                        {
                            visited.Add(desc);
                        }

                        return Task.CompletedTask;
                    },
                    archiveReader.NewCache(),
                    desc => Task.CompletedTask,
                    "test",
                    null
                );
            }

            Assert.That(visited, Has.Count.EqualTo(1));
            Assert.That(visited[0].Configuration.Name, Is.EqualTo("desc.json"));
        }

        [Test]
        public void ContextExclude()
        {
            var exclusions =
                new RecipeHandler.RecipeContext(new Manifest(), new Manifest.Recipe())
                    .Exclude("foo", "bar,dummy")
                    .Recipe.ExcludedDescriptors ?? [];
            Assert.That(
                exclusions,
                Is.EqualTo(
                    (IEnumerable<Manifest.DescriptorRef>)

                        [
                            new Manifest.DescriptorRef { Name = "bar", Location = "*" },
                            new Manifest.DescriptorRef { Name = "dummy", Location = "*" },
                            new Manifest.DescriptorRef { Name = "*", Location = "foo" }
                        ]
                )
            );
        }

        [Test]
        [TempFolder]
        public async Task FindRootRecipes()
        {
            var (maven, recipeHandler, _) = _NewServices();
            var zipLocation = _CreateZip(maven);
            using (maven)
            {
                var ctx = await recipeHandler.FindRootRecipes(
                    "com.cezanne.test:recipe:1.0.0",
                    null,
                    "auto",
                    "test",
                    null
                );
                Assert.Multiple(() =>
                {
                    Assert.That(ctx.Count(), Is.EqualTo(1));
                    Assert.That(ctx.First().Recipe.Name, Is.EqualTo("test"));
                });
            }
        }

        [Test]
        [TempFolder]
        public async Task ResolveTwice()
        {
            var (maven, recipeHandler, _) = _NewServices();
            var zipLocation = _CreateZip(maven);
            using var mvn = maven;

            // twice to ensure the cache - nested, not global there - does not break anything in case of a later refactoring
            var mf1 = await recipeHandler.FindManifest(
                "com.cezanne.test:recipe:1.0.0",
                null,
                "test",
                null
            );
            var mf2 = await recipeHandler.FindManifest(
                "com.cezanne.test:recipe:1.0.0",
                null,
                "test",
                null
            );
            Action<Manifest?> asserts = mf =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(mf, Is.Not.Null);
                    Assert.That(
                        (mf ?? throw new ArgumentNullException(nameof(mf))).Recipes.Count,
                        Is.EqualTo(1)
                    );
                });
            };
            Assert.Multiple(() =>
            {
                asserts(mf1);
                asserts(mf2);
            });
        }

        private (MavenService, RecipeHandler, ArchiveReader) _NewServices()
        {
            LoggerFactory loggerFactory = new();
            Substitutor substitutor = new(static _ => null, null, null);
            ManifestReader manifestReader = new(substitutor);
            MavenService maven =
                new(
                    new MavenConfiguration
                    {
                        ForceCustomSettingsXml = true,
                        PreferLocalSettingsXml = true,
                        EnableDownload = false,
                        LocalRepository =
                            Temp ?? throw new ArgumentException("Temp is null", nameof(Temp))
                    },
                    new Logger<MavenService>(loggerFactory)
                );
            ArchiveReader archiveReader =
                new(new Logger<ArchiveReader>(loggerFactory), manifestReader, maven);
            RecipeHandler recipeHandler =
                new(
                    new Logger<RecipeHandler>(loggerFactory),
                    manifestReader,
                    archiveReader,
                    new RequirementService(),
                    new ConditionEvaluator(),
                    substitutor
                );
            return (maven, recipeHandler, archiveReader);
        }

        private string _CreateZip(MavenService maven)
        {
            var zipLocation = Path.Combine(
                maven.LocalRepository,
                "com/cezanne/test/recipe/1.0.0/recipe-1.0.0.jar"
            );
            (
                Directory.GetParent(zipLocation)
                ?? throw new ArgumentException("no parent", nameof(zipLocation))
            ).Create();
            using (var zip = ZipFile.Open(zipLocation, ZipArchiveMode.Create))
            {
                zip.CreateEntry("bundlebee/").Open().Close();

                using (var manifestJson = zip.CreateEntry("bundlebee/manifest.json").Open())
                {
                    manifestJson.Write(
                        Encoding.UTF8.GetBytes(
                            "{\"alveoli\":[{\"name\": \"test\",\"descriptors\":[{\"name\":\"desc.json\"}]}]}"
                        )
                    );
                }

                using var descJson = zip.CreateEntry("bundlebee/kubernetes/desc.json").Open();
                descJson.Write(
                    Encoding.UTF8.GetBytes(
                        "{\"apiVersion\":\"v1\",\"kind\":\"ConfigMap\",\"metadata\":{\"name\":\"cm\"}\"data\":{}}"
                    )
                );
            }

            return zipLocation;
        }
    }
}
