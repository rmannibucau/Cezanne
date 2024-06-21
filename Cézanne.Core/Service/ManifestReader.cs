using System.Text.Json;
using Cézanne.Core.Descriptor;
using Cézanne.Core.Interpolation;

namespace Cézanne.Core.Service
{
    public class ManifestReader(Substitutor _substitutor)
    {
        public Manifest ReadManifest(
            string? location,
            Func<Stream> manifest,
            Func<string, Stream> relativeResolver,
            string? id
        )
        {
            using StreamReader reader = new(manifest());
            var content = _substitutor.Replace(null, null, reader.ReadToEnd().Trim(), id);
            var mf =
                JsonSerializer.Deserialize<Manifest>(content, Jsons.Options)
                ?? throw new ArgumentException(
                    $"Invalid manifest descriptor: {content}",
                    nameof(location)
                );

            if (!string.IsNullOrEmpty(location) && mf.Recipes.Any())
            {
                foreach (
                    var it in mf.Recipes.SelectMany(it => it.Descriptors ?? [])
                        .Where(it => it.Location == null) ?? []
                )
                {
                    it.Location = location;
                }
            }

            _ResolveReferences(location, mf, relativeResolver, id);
            _InitInterpolateFlags(mf);

            return mf;
        }

        private void _InitInterpolateFlags(Manifest manifest)
        {
            foreach (var recipe in manifest.Recipes)
            {
                var parentValue = recipe.InterpolateDescriptors ?? manifest.InterpolateRecipe;
                if ((recipe.Descriptors ?? []).Any())
                {
                    foreach (var descriptor in recipe.Descriptors ?? [])
                    {
                        if (!descriptor.HasInterpolateValue())
                        {
                            descriptor.InitInterpolate(parentValue);
                        }
                    }
                }
            }
        }

        private void _ResolveReferences(
            string? location,
            Manifest main,
            Func<string, Stream> relativeResolver,
            string? id
        )
        {
            if (!main.References.Any())
            {
                return;
            }

            foreach (var reference in main.References)
            {
                var loaded = ReadManifest(
                    location,
                    () =>
                        relativeResolver(
                            reference.Path
                                ?? throw new ArgumentException(
                                    "No path set in reference",
                                    nameof(reference)
                                )
                        ),
                    relativeResolver,
                    id
                );
                if (loaded.References.Any())
                {
                    _ResolveReferences(location, loaded, relativeResolver, id);
                }

                if (loaded.Recipes.Any())
                {
                    main.Recipes = main.Recipes.Concat(loaded.Recipes);
                }
            }
        }
    }
}
