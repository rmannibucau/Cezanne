using Cezanne.Core.Interpolation;

namespace Cezanne.Core.Tests.Interpolation
{
    public class SimpleCodecTests
    {
        [Test]
        public void Decipher()
        {
            Assert.That(
                new SimpleCodec("123456").Decipher(
                    "{Bq+CDyHYBFwH0d9qnBURgIV0sXIGsPKjva0P2QAYTWA=}"
                ),
                Is.EqualTo("foo")
            );
        }
    }
}
