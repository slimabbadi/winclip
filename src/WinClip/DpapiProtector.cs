using System.Security.Cryptography;
using WinClip.Core;

namespace WinClip;

/// <summary>
/// Windows DPAPI, scoped to the current user: only this Windows account can decrypt the history, and copying
/// history.db to another account or machine yields unreadable data.
/// </summary>
internal sealed class DpapiProtector : IDataProtector
{
    private static readonly byte[] Entropy = "WinClip.History.v2"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] ciphertext) => ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
}
