using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Glacier.Studio.Services;

/// <summary>
/// Self-contained cross-platform secret protector:
/// - Windows: DPAPI CryptProtectData / CryptUnprotectData with CRYPTPROTECT_UI_FORBIDDEN.
/// - Linux/macOS: AES-256-GCM authenticated encryption ("GST\x02") backed by a 0600 CSPRNG local key file.
/// </summary>
public static partial class StudioSecretProtector
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr LocalFree(IntPtr hMem);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x01;
    private static readonly byte[] MagicHeaderV2 = [0x47, 0x53, 0x54, 0x02]; // "GST" + v2 (AES-GCM)
    private static readonly object KeyFileLock = new();

    public static string Encrypt(string plainText, string? keyFilePath = null)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        if (OperatingSystem.IsWindows())
        {
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            DATA_BLOB dataIn = default;
            DATA_BLOB dataOut = default;
            DATA_BLOB entropy = default;

            try
            {
                dataIn = new DATA_BLOB
                {
                    cbData = plainBytes.Length,
                    pbData = Marshal.AllocHGlobal(plainBytes.Length)
                };
                Marshal.Copy(plainBytes, 0, dataIn.pbData, plainBytes.Length);

                if (CryptProtectData(ref dataIn, "GlacierStudioProviderKey", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut))
                {
                    byte[] cipherBytes = new byte[dataOut.cbData];
                    Marshal.Copy(dataOut.pbData, cipherBytes, 0, dataOut.cbData);
                    return Convert.ToBase64String(cipherBytes);
                }

                Trace.TraceWarning($"[StudioSecretProtector] Windows DPAPI CryptProtectData failed with Win32={Marshal.GetLastPInvokeError()}; falling back to AES-GCM.");
            }
            catch (Exception ex) when (ex is CryptographicException or OutOfMemoryException or DllNotFoundException or EntryPointNotFoundException)
            {
                Trace.TraceWarning($"[StudioSecretProtector] Windows DPAPI unavailable ({ex.Message}); falling back to AES-GCM.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plainBytes);
                if (dataIn.pbData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataIn.pbData);
                }
                if (dataOut.pbData != IntPtr.Zero)
                {
                    LocalFree(dataOut.pbData);
                }
            }
        }

        return EncryptAesGcm(plainText, keyFilePath);
    }

    public static string Decrypt(string cipherText, string? keyFilePath = null)
    {
        if (string.IsNullOrEmpty(cipherText))
            return "";

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(cipherText);
        }
        catch (FormatException ex)
        {
            Trace.TraceWarning($"[StudioSecretProtector] Invalid Base64 secret payload: {ex.Message}");
            return "";
        }

        if (HasAesGcmHeader(rawBytes))
        {
            return DecryptAesGcmBytes(rawBytes, keyFilePath);
        }

        if (OperatingSystem.IsWindows())
        {
            DATA_BLOB dataIn = default;
            DATA_BLOB dataOut = default;
            DATA_BLOB entropy = default;
            byte[]? plainBytes = null;

            try
            {
                dataIn = new DATA_BLOB
                {
                    cbData = rawBytes.Length,
                    pbData = Marshal.AllocHGlobal(rawBytes.Length)
                };
                Marshal.Copy(rawBytes, 0, dataIn.pbData, rawBytes.Length);

                if (CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut))
                {
                    plainBytes = new byte[dataOut.cbData];
                    Marshal.Copy(dataOut.pbData, plainBytes, 0, dataOut.cbData);
                    return Encoding.UTF8.GetString(plainBytes);
                }

                Trace.TraceWarning($"[StudioSecretProtector] Windows DPAPI CryptUnprotectData failed with Win32={Marshal.GetLastPInvokeError()}.");
            }
            catch (Exception ex) when (ex is CryptographicException or OutOfMemoryException or DllNotFoundException or EntryPointNotFoundException)
            {
                Trace.TraceWarning($"[StudioSecretProtector] Windows DPAPI decrypt failed: {ex.Message}");
            }
            finally
            {
                if (plainBytes != null)
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
                if (dataIn.pbData != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataIn.pbData);
                }
                if (dataOut.pbData != IntPtr.Zero)
                {
                    LocalFree(dataOut.pbData);
                }
            }
        }

        return "";
    }

    internal static string EncryptAesGcm(string plainText, string? keyFilePath = null)
    {
        if (string.IsNullOrEmpty(plainText))
            return "";

        byte[]? key = null;
        byte[]? plainBytes = null;
        try
        {
            key = GetOrCreateLocalAesKey(keyFilePath);
            plainBytes = Encoding.UTF8.GetBytes(plainText);

            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);
            byte[] tag = new byte[16];
            byte[] cipherBytes = new byte[plainBytes.Length];

            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
            }

            byte[] output = new byte[MagicHeaderV2.Length + nonce.Length + tag.Length + cipherBytes.Length];
            Buffer.BlockCopy(MagicHeaderV2, 0, output, 0, MagicHeaderV2.Length);
            Buffer.BlockCopy(nonce, 0, output, MagicHeaderV2.Length, nonce.Length);
            Buffer.BlockCopy(tag, 0, output, MagicHeaderV2.Length + nonce.Length, tag.Length);
            Buffer.BlockCopy(cipherBytes, 0, output, MagicHeaderV2.Length + nonce.Length + tag.Length, cipherBytes.Length);
            return Convert.ToBase64String(output);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceError($"[StudioSecretProtector] AES-GCM encryption failed: {ex.Message}");
            return "";
        }
        finally
        {
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static string DecryptAesGcm(string cipherText, string? keyFilePath = null)
    {
        if (string.IsNullOrEmpty(cipherText))
            return "";

        try
        {
            byte[] rawBytes = Convert.FromBase64String(cipherText);
            return HasAesGcmHeader(rawBytes) ? DecryptAesGcmBytes(rawBytes, keyFilePath) : "";
        }
        catch (FormatException ex)
        {
            Trace.TraceWarning($"[StudioSecretProtector] AES-GCM invalid Base64 payload: {ex.Message}");
            return "";
        }
    }

    private static bool HasAesGcmHeader(ReadOnlySpan<byte> data) =>
        data.Length >= MagicHeaderV2.Length + 12 + 16 &&
        data[0] == MagicHeaderV2[0] &&
        data[1] == MagicHeaderV2[1] &&
        data[2] == MagicHeaderV2[2] &&
        data[3] == MagicHeaderV2[3];

    private static string DecryptAesGcmBytes(byte[] cipherBytes, string? keyFilePath)
    {
        byte[]? key = null;
        byte[]? plainBytes = null;
        try
        {
            key = GetOrCreateLocalAesKey(keyFilePath);
            int nonceOffset = MagicHeaderV2.Length;
            int tagOffset = nonceOffset + 12;
            int payloadOffset = tagOffset + 16;
            int payloadLen = cipherBytes.Length - payloadOffset;

            plainBytes = new byte[payloadLen];
            using (var aesGcm = new AesGcm(key, 16))
            {
                aesGcm.Decrypt(
                    cipherBytes.AsSpan(nonceOffset, 12),
                    cipherBytes.AsSpan(payloadOffset, payloadLen),
                    cipherBytes.AsSpan(tagOffset, 16),
                    plainBytes);
            }
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"[StudioSecretProtector] AES-GCM decryption failed: {ex.Message}");
            return "";
        }
        finally
        {
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
            if (key != null) CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static string GetDefaultLocalKeyPath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Path.Combine(appData, "GlacierStudio", "local.key");
    }

    internal static string GetDefaultSecretsStorePath()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        return Path.Combine(appData, "GlacierStudio", "provider-keys.enc.json");
    }

    private static byte[] GetOrCreateLocalAesKey(string? explicitKeyPath)
    {
        string keyPath = string.IsNullOrWhiteSpace(explicitKeyPath)
            ? GetDefaultLocalKeyPath()
            : Path.GetFullPath(explicitKeyPath);
        string keyDir = Path.GetDirectoryName(keyPath) ?? ".";

        lock (KeyFileLock)
        {
            if (File.Exists(keyPath))
            {
                byte[] existing = File.ReadAllBytes(keyPath);
                if (existing.Length == 32)
                {
                    return existing;
                }
                CryptographicOperations.ZeroMemory(existing);
            }

            Directory.CreateDirectory(keyDir);
            byte[] newKey = new byte[32];
            RandomNumberGenerator.Fill(newKey);
            string tempPath = Path.Combine(keyDir, $"local.key.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, newKey);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                File.Move(tempPath, keyPath, overwrite: true);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Trace.TraceWarning($"[StudioSecretProtector] Failed deleting temp key file '{tempPath}': {ex.Message}");
                    }
                }
                throw;
            }
            return newKey;
        }
    }
}
