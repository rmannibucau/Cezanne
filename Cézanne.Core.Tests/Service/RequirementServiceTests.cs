using Cézanne.Core.Descriptor;
using Cézanne.Core.Service;

namespace Cézanne.Core.Tests.Service
{
    [FixtureLifeCycle(LifeCycle.SingleInstance)]
    public class RequirementServiceTests
    {
        [TestCaseSource(nameof(DataSet))]
        public bool Check((string, Manifest) versionAndManifest)
        {
            try
            {
                new RequirementService { BundlebeeVersion = versionAndManifest.Item1 }.CheckRequirements(
                    versionAndManifest.Item2);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static IEnumerable<TestCaseData> DataSet()
        {
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null, MaxBundlebeeVersion = null, ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null, MaxBundlebeeVersion = "0.9.9", ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "0.9.9", MaxBundlebeeVersion = null, ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.1", MaxBundlebeeVersion = null, ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["1.0.0"]
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["1.0.*"]
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["1.*.*"]
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.11",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["1.*.*"]
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.11",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["1.*.10"]
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = null,
                                MaxBundlebeeVersion = null,
                                ForbiddenVersions = ["*.*.*"]
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.0", MaxBundlebeeVersion = null, ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.0", MaxBundlebeeVersion = "0.9.9", ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(false);
            yield return new TestCaseData((
                    "1.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.*",
                                MaxBundlebeeVersion = "2.0.-1",
                                ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.0.10",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.*",
                                MaxBundlebeeVersion = "2.0.-1",
                                ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "1.1.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.*",
                                MaxBundlebeeVersion = "2.0.-1",
                                ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(true);
            yield return new TestCaseData((
                    "2.0.0",
                    new Manifest
                    {
                        Requirements =
                        [
                            new Manifest.Requirement
                            {
                                MinBundlebeeVersion = "1.0.*",
                                MaxBundlebeeVersion = "2.0.-1",
                                ForbiddenVersions = []
                            }
                        ]
                    }))
                .Returns(false);
        }
    }
}