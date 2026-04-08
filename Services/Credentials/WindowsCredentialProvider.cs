using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Babel.Player.Services.Credentials;

/// <summary>
/// A credential provider that uses the Windows Credential Manager (advapi32.dll).
/// Keys stored here are protected by the OS and can be backed by TPM/Windows Hello.
/// </summary>
public sealed partial class WindowsCredentialProvider : ISecureCredentialProvider
{
    private const string TargetPrefix = "BabelPlayer:";
    private const uint CredTypeGeneric = 1;

    public string StorageProviderName => "Windows Credential Manager (TPM-Backed)";

    public bool HasKey(string provider)
    {
        var target = GetTargetName(provider);
        if (NativeMethods.CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            NativeMethods.CredFree(ptr);
            return true;
        }
        return false;
    }

    public void SetKey(string provider, string key)
    {
        var target = GetTargetName(provider);
        var credential = new NativeMethods.CREDENTIAL
        {
            Type = CredTypeGeneric,
            TargetName = target,
            CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(key),
            CredentialBlob = Marshal.StringToCoTaskMemUni(key),
            Persist = 2 // CRED_PERSIST_LOCAL_MACHINE (survives reboot)
        };

        var ptr = Marshal.AllocCoTaskMem(Marshal.SizeOf(credential));
        try
        {
            Marshal.StructureToPtr(credential, ptr, false);
            if (!NativeMethods.CredWrite(ptr, 0))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"Failed to write credential to Windows Vault (Error: {error})");
            }
        }
        finally
        {
            if (credential.CredentialBlob != IntPtr.Zero)
                Marshal.FreeCoTaskMem(credential.CredentialBlob);

            Marshal.DestroyStructure<NativeMethods.CREDENTIAL>(ptr);
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public string GetKey(string provider)
    {
        var target = GetTargetName(provider);
        if (NativeMethods.CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            try
            {
                var cred = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(ptr);
                return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
            }
            finally
            {
                NativeMethods.CredFree(ptr);
            }
        }
        return "";
    }

    public void ClearKey(string provider)
    {
        var target = GetTargetName(provider);
        NativeMethods.CredDelete(target, CredTypeGeneric, 0);
    }

    public IEnumerable<string> GetStoredProviders()
    {
        // Enumerate is more complex with P/Invoke, we'll leave it simple for now 
        // or just return the known ones if needed. 
        // For Babel-Player, we usually know which providers we are looking for.
        yield break; 
    }

    private static string GetTargetName(string provider) => $"{TargetPrefix}{provider}";

    private static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredWrite(IntPtr credential, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredRead(string target, uint type, uint reserved, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool CredDelete(string target, uint type, uint flags);

        [LibraryImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        public static partial void CredFree(IntPtr credential);
    }
}
