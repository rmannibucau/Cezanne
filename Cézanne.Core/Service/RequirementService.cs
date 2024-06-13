using Cézanne.Core.Descriptor;

namespace Cézanne.Core.Service
{
    public class RequirementService
    {
        public string? BundlebeeVersion { get; set; }

        public void CheckRequirements(Manifest manifest)
        {
            if (!manifest.Requirements.Any())
            {
                return;
            }

            foreach (Manifest.Requirement requirement in manifest.Requirements)
            {
                _Check(requirement);
            }
        }

        private void _Check(Manifest.Requirement requirement)
        {
            if (requirement.MinBundlebeeVersion != null &&
                !string.IsNullOrWhiteSpace(requirement.MinBundlebeeVersion) &&
                !_CompareVersion(requirement.MinBundlebeeVersion, true))
            {
                throw new InvalidOperationException(
                    $"Invalid bundlebee version: {BundlebeeVersion} expected-min={requirement.MinBundlebeeVersion}");
            }

            if (requirement.MaxBundlebeeVersion != null &&
                !string.IsNullOrWhiteSpace(requirement.MaxBundlebeeVersion) &&
                !_CompareVersion(requirement.MaxBundlebeeVersion, false))
            {
                throw new InvalidOperationException(
                    $"Invalid bundlebee version: {BundlebeeVersion} expected-max={requirement.MaxBundlebeeVersion}");
            }

            if (requirement.ForbiddenVersions != null)
            {
                foreach (string version in requirement.ForbiddenVersions)
                {
                    if (_CompareVersion(version, null))
                    {
                        throw new InvalidOperationException(
                            $"Invalid bundlebee version: {BundlebeeVersion} forbidden={requirement.ForbiddenVersions}");
                    }
                }
            }
        }

        private bool _CompareVersion(string expected, bool? expectNegative)
        {
            string[] expectedSegments = expected.Split(".");
            string[] actualSegments = (BundlebeeVersion ?? "1.0.28").Split(".");
            int segmentLoopLength = Math.Min(expectedSegments.Length, actualSegments.Length);
            for (int i = 0; i < segmentLoopLength; i++)
            {
                string exp = expectedSegments[i];
                if ("*" == exp)
                {
                    continue;
                }

                string act = actualSegments[i];
                if (exp == act)
                {
                    continue;
                }

                try
                {
                    int expInt = int.Parse(exp);
                    int actInt = int.Parse(act);
                    int comp = expInt - actInt;
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

            return expectedSegments.Length < actualSegments.Length || expectedSegments.Length == actualSegments.Length;
        }
    }
}