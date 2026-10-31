using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ParaPhraser.Desktop.Services;

/// <summary>
/// Detects an MSIX install, where Windows redirects AppData and the registry.
/// </summary>
internal static class PackagedApp
{
    private const int InsufficientBuffer = 122;

    private static readonly string? FamilyName = ReadFamilyName();

    internal static bool IsPackaged => FamilyName is not null;

    /// <summary>
    /// The real location of a folder under LocalAppData. The app can use the normal
    /// path, but Explorer runs outside the package and would find nothing there.
    /// </summary>
    internal static string GetVisibleLocalFolder(string folder)
    {
        if (FamilyName is null)
        {
            return folder;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var redirected = Path.Combine(
            localAppData,
            "Packages",
            FamilyName,
            "LocalCache",
            "Local",
            Path.GetRelativePath(localAppData, folder));

        return Directory.Exists(redirected) ? redirected : folder;
    }

    private static string? ReadFamilyName()
    {
        var length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != InsufficientBuffer)
        {
            return null;
        }

        var name = new StringBuilder(length);
        return GetCurrentPackageFamilyName(ref length, name) == 0 ? name.ToString() : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, StringBuilder? familyName);
}
