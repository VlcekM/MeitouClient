using System.Security.Cryptography;
using System.Text;

namespace Meitou.Reports;

/// <summary>
/// Who sent a feedback form, as a salted hash only.
/// </summary>
sealed class Reporters(Config config)
{
    /// <summary>
    /// The salted hash a reporter is stored as: counts distinct people, cannot be turned back into an IP.
    /// </summary>
    public string Hash(string key) => Convert.ToHexString(HMACSHA256.HashData(config.Salt, Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
}
