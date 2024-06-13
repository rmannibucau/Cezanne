using Cézanne.Core.Descriptor;
using Cézanne.Core.Interpolation;
using Cézanne.Core.Maven;
using Cézanne.Core.Runtime;
using Cézanne.Core.Service;
using Cézanne.Core.Tests.Rule;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Compression;
using System.Text;

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
            (MavenService maven, RecipeHandler recipeHandler, ArchiveReader archiveReader) = _NewServices();
            using var mvn = maven;

            // create a fake recipe
            string zipLocation = _CreateZip(maven);

            // visit the recipe
            var recipes = await recipeHandler.FindRootRecipes(zipLocation, null, "test", "test");
            Assert.That(recipes.Count(), Is.EqualTo(1));

            var visited = new List<LoadedDescriptor>();
            foreach (var recipe in recipes) // todo: add some descriptor to the recipe to test it
            {
                await recipeHandler.ExecuteOnRecipe(
                    "Visiting ", recipe.Manifest, recipe.Recipe,
                    null,
                    (ctx, desc) =>
                    {
                        lock (visited) {
                            visited.Add(desc);
                        }
                        return Task.CompletedTask;
                    },
                    archiveReader.NewCache(),
                    desc => Task.CompletedTask,
                    "test");
            }

            Assert.That(visited, Has.Count.EqualTo(1));
            Assert.That(visited[0].Configuration.Name, Is.EqualTo("desc.json"));
        }

        [Test]
        public void ContextExclude()
        {
            IEnumerable<Manifest.DescriptorRef> exclusions =
                new RecipeHandler.RecipeContext(new Manifest(), new Manifest.Recipe())
                    .Exclude("foo", "bar,dummy")
                    .Recipe
                    .ExcludedDescriptors ?? [];
            Assert.That(exclusions, Is.EqualTo((IEnumerable<Manifest.DescriptorRef>)
            [
                new Manifest.DescriptorRef { Name = "bar", Location = "*" },
                new Manifest.DescriptorRef { Name = "dummy", Location = "*" },
                new Manifest.DescriptorRef { Name = "*", Location = "foo" }
            ]));
        }

        [Test]
        [TempFolder]
        public async Task FindRootRecipes()
        {
            (MavenService maven, RecipeHandler recipeHandler, _) = _NewServices();
            string zipLocation = _CreateZip(maven);
            using (maven)
            {
                IEnumerable<RecipeHandler.RecipeContext> ctx =
                    await recipeHandler.FindRootRecipes("com.cezanne.test:recipe:1.0.0", null, "auto", "test");
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
            (MavenService maven, RecipeHandler recipeHandler, _) = _NewServices();
            string zipLocation = _CreateZip(maven);
            using MavenService mvn = maven;

            // twice to ensure the cache - nested, not global there - does not break anything in case of a later refactoring
            Manifest? mf1 = await recipeHandler.FindManifest("com.cezanne.test:recipe:1.0.0", null, "test");
            Manifest? mf2 = await recipeHandler.FindManifest("com.cezanne.test:recipe:1.0.0", null, "test");
            Action<Manifest?> asserts = mf =>
            {
                Assert.Multiple(() =>
                {
                    Assert.That(mf, Is.Not.Null);
                    Assert.That((mf ?? throw new ArgumentNullException(nameof(mf))).Recipes.Count, Is.EqualTo(1));
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
            var loggerFactory = new LoggerFactory();
            var substitutor = new Substitutor(static _ => null, null, null);
            var manifestReader = new ManifestReader(substitutor);
            var maven = new MavenService(
                new MavenConfiguration
                {
                    ForceCustomSettingsXml = true,
                    PreferLocalSettingsXml = true,
                    EnableDownload = false,
                    LocalRepository = Temp ?? throw new ArgumentException("Temp is null", nameof(Temp))
                }, loggerFactory);
            var archiveReader = new ArchiveReader(loggerFactory, manifestReader, maven);
            var recipeHandler = new RecipeHandler(loggerFactory, manifestReader,
                archiveReader, new RequirementService(),new ConditionEvaluator(), substitutor);
            return (maven, recipeHandler, archiveReader);
        }

        private string _CreateZip(MavenService maven)
        {
            string zipLocation = Path.Combine(maven.LocalRepository, "com/cezanne/test/recipe/1.0.0/recipe-1.0.0.jar");
            (Directory.GetParent(zipLocation) ?? throw new ArgumentException("no parent", nameof(zipLocation)))
                .Create();
            using (ZipArchive zip = ZipFile.Open(zipLocation, ZipArchiveMode.Create))
            {
                zip.CreateEntry("bundlebee/").Open().Close();

                using (var manifestJson = zip.CreateEntry("bundlebee/manifest.json").Open()) {
                    manifestJson.Write(Encoding.UTF8.GetBytes("{\"alveoli\":[{\"name\": \"test\",\"descriptors\":[{\"name\":\"desc.json\"}]}]}"));
                }

                using var descJson = zip.CreateEntry("bundlebee/kubernetes/desc.json").Open();
                descJson.Write(Encoding.UTF8.GetBytes("{\"apiVersion\":\"v1\",\"kind\":\"ConfigMap\",\"metadata\":{\"name\":\"cm\"}\"data\":{}}"));
            }

            return zipLocation;
        }
    }
}