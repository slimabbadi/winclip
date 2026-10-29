namespace WinClip.Core;

/// <summary>Encrypts history at rest. The Windows app uses DPAPI, which ties the key to the user's login.</summary>
public interface IDataProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] ciphertext);
}
