using System.Text;
using Cézanne.Core.Interpolation;
using Cézanne.Core.K8s;
using Cézanne.Core.Service;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cézanne.Core.Tests.Interpolation
{
    public class SubstitutorTests
    {
        [Test]
        public void Replace()
        {
            Assert.That(
                new Substitutor(static k => "key" == k ? "replaced" : null, null, null).Replace(
                    null,
                    null,
                    "foo {{key}} dummy",
                    null
                ),
                Is.EqualTo("foo replaced dummy")
            );
        }

        [Test]
        public void Fallback()
        {
            Assert.That(_SimpleReplacement("foo {{key:-or}} dummy"), Is.EqualTo("foo or dummy"));
        }

        [Test]
        public void Nested()
        {
            Assert.That(
                new Substitutor(
                    static k =>
                        k switch
                        {
                            "key" => "replaced",
                            _ => null
                        },
                    null,
                    null
                ).Replace(null, null, "foo {{k{{missing:-e}}y}} dummy", null),
                Is.EqualTo("foo replaced dummy")
            );
        }

        [Test]
        public void Escaped()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    _SimpleReplacement("foo \\{{key:-or}} dummy"),
                    Is.EqualTo("foo {{key:-or}} dummy")
                );
                Assert.That(
                    new Substitutor(
                        k =>
                            k switch
                            {
                                "suffix" => "after",
                                "prefix" => "before",
                                _ => null
                            },
                        null,
                        null
                    ).Replace(
                        null,
                        null,
                        "foo {{prefix}} \\{{key:-or}} / {{test:-\\{{key:-or2}}}} {{suffix}} dummy",
                        null
                    ),
                    Is.EqualTo("foo before {{key:-or}} / {{key:-or2}} after dummy")
                );
            });
        }

        [Test]
        public void Complex()
        {
            Assert.Multiple(() =>
            {
                Assert.That(
                    new Substitutor(
                        static k =>
                            k switch
                            {
                                "name" => "foo",
                                _ => null
                            },
                        null,
                        null
                    ).Replace(
                        null,
                        null,
                        "{{{{name}}.resources.limits.cpu:-{{resources.limits.cpu:-1}}}}",
                        null
                    ),
                    Is.EqualTo("1")
                );
                Assert.That(
                    new Substitutor(
                        k =>
                            k switch
                            {
                                "name" => "foo",
                                "foo.resources.limits.cpu" => "2",
                                _ => null
                            },
                        null,
                        null
                    ).Replace(
                        null,
                        null,
                        "{{{{name}}.resources.limits.cpu:-{{resources.limits.cpu:-1}}}}",
                        null
                    ),
                    Is.EqualTo("2")
                );
            });
        }

        [Test]
        public void Decipher()
        {
            Assert.That(
                new Substitutor(
                    static k =>
                        k switch
                        {
                            "decipher.masterKey" => "123456",
                            _ => null
                        },
                    null,
                    null
                ).Replace(
                    null,
                    null,
                    "result={{bundlebee-decipher:{{decipher.masterKey}},{Bq+CDyHYBFwH0d9qnBURgIV0sXIGsPKjva0P2QAYTWA=} }}",
                    null
                ),
                Is.EqualTo("result=foo")
            );
        }

        [Test]
        public void DirectoryJsonKeyValuePairsContent()
        {
            var baseDir = Path.GetFullPath(
                $"{AppDomain.CurrentDomain.BaseDirectory}/../../../Interpolation"
            );
            var text =
                $"{{{{bundlebee-directory-json-key-value-pairs-content:{baseDir}/substitutor/json/content/*.txt}}}}";
            Assert.That(
                _SimpleReplacement(text),
                Is.EqualTo(
                    "\"another/2.txt\":\"this\\nanother\\nfile = 2\\n\",\"file/1.txt\":\"this\\nis the file\\nnumber 1\\n\""
                )
            );
        }

        [Test]
        public void Uppercase()
        {
            Assert.That(_SimpleReplacement("{{bundlebee-uppercase:up}}"), Is.EqualTo("UP"));
        }

        [Test]
        public void Lowercase()
        {
            Assert.That(_SimpleReplacement("{{bundlebee-lowercase:LoW}}"), Is.EqualTo("low"));
        }

        [Test]
        public void Digest()
        {
            Assert.That(
                _SimpleReplacement("{{bundlebee-digest:base64,md5,was executed properly}}"),
                Is.EqualTo("vo6GaAnToZqq622SpCHmng==")
            );
        }

        [Test]
        public void Base64Encode()
        {
            Assert.That(
                _SimpleReplacement("{{bundlebee-base64:content}}"),
                Is.EqualTo(Convert.ToBase64String(Encoding.UTF8.GetBytes("content")))
            );
        }

        [Test]
        public void Base64Decode()
        {
            Assert.That(
                _SimpleReplacement(
                    "{{bundlebee-base64-decode:"
                        + Convert.ToBase64String(Encoding.UTF8.GetBytes("content"))
                        + "}}"
                ),
                Is.EqualTo("content")
            );
        }

        [Test]
        public void Base64UrlDecode()
        {
            Assert.That(
                _SimpleReplacement(
                    "{{bundlebee-base64url-decode:eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkPDqXphbm5lIiwiaWF0IjoxNTE2MjM5MDIyLCJjaGFycyI6InV-K8KwwqPCtSXCqMKjwrDCsMOgw6dfw6gtX8OnX1_DqC0ifQ}}"
                ),
                Is.EqualTo(
                    "{\"sub\":\"1234567890\",\"name\":\"Cézanne\",\"iat\":1516239022,\"chars\":\"u~+°£µ%¨£°°àç_è-_ç__è-\"}"
                )
            );
        }

        [Test]
        public async Task Namespace()
        {
            using NullLoggerFactory loggreFactory = new();
            await using K8SClient client =
                new(
                    new K8SClientConfiguration { Kubeconfig = "skip" },
                    new Logger<K8SClient>(loggreFactory),
                    new Logger<ApiPreloader>(loggreFactory)
                );
            Assert.That(
                new Substitutor(static k => null, client, null).Replace(
                    null,
                    null,
                    "{{bundlebee-kubernetes-namespace}}",
                    null
                ),
                Is.EqualTo("default")
            );
        }

        [Test]
        public void JsonFileFromFileWithEscaping() // this test is a bit buggy cause value evaluation doesnt escape but main loop does
        {
            Assert.That(
                _SimpleReplacement(
                    "{{bundlebee-json-inline-file:"
                        + Path.GetFullPath(
                            $"{AppDomain.CurrentDomain.BaseDirectory}/../../../Interpolation/substitutor/dont_escape.txt"
                        )
                        + "}}"
                ),
                Is.EqualTo("{\\\"foo\\\":dontescape}")
            );
        }

        [Test]
        public void Indent()
        {
            Assert.That(
                _SimpleReplacement(
                    "{{bundlebee-strip-trailing:{{bundlebee-indent:4:{{bundlebee-inline-file:"
                        + Path.GetFullPath(
                            $"{AppDomain.CurrentDomain.BaseDirectory}/../../../Interpolation/substitutor/indent.txt"
                        )
                        + "}}}}}}"
                ),
                Is.EqualTo("    content\n      foo\n    bar")
            );
        }

        private string? _SimpleReplacement(string text)
        {
            return new Substitutor(static k => null, null, null).Replace(null, null, text, null);
        }
    }
}
