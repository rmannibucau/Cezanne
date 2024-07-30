using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Cezanne.Core.Interpolation
{
    public partial class SimpleCodec(string masterPassword)
    {
        private readonly byte[] _masterPassword = Encoding.UTF8.GetBytes(masterPassword);

        public string? Decipher(string input)
        {
            var match = _encryptedPattern().Match(input);
            if (!match.Success)
            {
                return input;
            }

            var value = match.Groups["value"].Value;
            if (value.StartsWith("${env.") && value.EndsWith("}"))
            {
                return Environment.GetEnvironmentVariable(value["${env.".Length..^1]);
            }

            if (value.StartsWith("${") && value.EndsWith('}'))
            {
                return Environment.GetEnvironmentVariable(value["${".Length..^1]);
            }

            if (value.Contains('[') && value.Contains(']') && value.Contains("type="))
            {
                throw new ArgumentException($"Unsupported encryption: '{value}'");
            }

            var allEncryptedBytes = Convert.FromBase64String(value.Replace("\r\n", ""));
            var salt = allEncryptedBytes[..8];
            var padLength = allEncryptedBytes[8] - (allEncryptedBytes[8] > 127 ? 256 : 0);
            var encryptedBytes = allEncryptedBytes[9..(allEncryptedBytes.Length - padLength)];

            var keyAndIv = new byte[16 * 2];
            var currentPos = 0;

            byte[] toDigest = [];

            using var digest = SHA256.Create();
            while (currentPos < keyAndIv.Length)
            {
                toDigest = [.. toDigest, .. _masterPassword, .. salt];
                var result = digest.ComputeHash(toDigest);

                var stillNeed = keyAndIv.Length - currentPos;
                if (result.Length > stillNeed)
                {
                    result = result[..stillNeed];
                }

                keyAndIv = [.. keyAndIv[..currentPos], .. result[..result.Length]];
                currentPos += result.Length;
                if (currentPos < keyAndIv.Length)
                {
                    digest.Clear();
                    toDigest = [.. result];
                }
            }

            using var cipher = Aes.Create();
            cipher.KeySize = 128;
            cipher.Padding = PaddingMode.PKCS7;
            cipher.Mode = CipherMode.CBC;
            cipher.Key = keyAndIv[..16];
            cipher.IV = keyAndIv[16..];

            using var decryptor = cipher.CreateDecryptor(cipher.Key, cipher.IV);
            return Encoding.UTF8.GetString(
                decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length)
            );
        }

        [GeneratedRegex(".*?[^\\\\]?\\{(?<value>.*?[^\\\\])\\}.*", RegexOptions.Singleline)]
        private static partial Regex _encryptedPattern();
    }
}
