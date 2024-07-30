using Cezanne.Core.Descriptor;

namespace Cezanne.Core.Service
{
    public class RequirementService
    {
        private const string DefaultBundlebeeVersion = "1.0.28";

        public string BundlebeeVersion { get; init; } = DefaultBundlebeeVersion;

        public void CheckRequirements(Manifest manifest)
        {
            if (!manifest.Requirements.Any())
            {
                return;
            }

            foreach (var requirement in manifest.Requirements)
            {
                _Check(requirement);
            }
        }

        private void _Check(Manifest.Requirement requirement)
        {
            if (
                requirement.MinBundlebeeVersion != null
                && !string.IsNullOrWhiteSpace(requirement.MinBundlebeeVersion)
                && !_CompareVersion(requirement.MinBundlebeeVersion, true)
            )
            {
                throw new InvalidOperationException(
                    $"Invalid bundlebee version: {BundlebeeVersion} expected-min={requirement.MinBundlebeeVersion}"
                );
            }

            if (
                requirement.MaxBundlebeeVersion != null
                && !string.IsNullOrWhiteSpace(requirement.MaxBundlebeeVersion)
                && !_CompareVersion(requirement.MaxBundlebeeVersion, false)
            )
            {
                throw new InvalidOperationException(
                    $"Invalid bundlebee version: {BundlebeeVersion} expected-max={requirement.MaxBundlebeeVersion}"
                );
            }

            if (requirement.ForbiddenVersions != null)
            {
                foreach (var version in requirement.ForbiddenVersions)
                {
                    if (_CompareVersion(version, null))
                    {
                        throw new InvalidOperationException(
                            $"Invalid bundlebee version: {BundlebeeVersion} forbidden={requirement.ForbiddenVersions}"
                        );
                    }
                }
            }
        }

        // not using System.Version cause it is restrictive in terms of format but same spirit
        private bool _CompareVersion(string expected, bool? expectNegative)
        {
            var expectedSegments = expected.Split(".");
            var actualSegments = BundlebeeVersion.Split(".");
            var segmentLoopLength = Math.Min(expectedSegments.Length, actualSegments.Length);
            for (var i = 0; i < segmentLoopLength; i++)
            {
                var exp = expectedSegments[i];
                if ("*" == exp)
                {
                    continue;
                }

                var act = actualSegments[i];
                if (exp == act)
                {
                    continue;
                }

                try
                {
                    var expInt = int.Parse(exp);
                    var actInt = int.Parse(act);
                    var comp = expInt - actInt;
                    if (expectNegative == null && comp != 0)
                    {
                        return false;
                    }

                    if (comp != 0)
                    {
                        return expectNegative ?? false ? comp < 0 : comp > 0;
                    }
                }
                catch (ArgumentException)
                {
                    return false;
                }
                catch (FormatException)
                {
                    return false;
                }
            }

            return expectedSegments.Length < actualSegments.Length
                || expectedSegments.Length == actualSegments.Length;
        }
    }
}
