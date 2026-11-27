using FolderDeck.Core.Infrastructure;
using Microsoft.Win32;

namespace FolderDeck.Shell;

/// <summary>
/// The value <see cref="LoginStartup.ValueName"/> of <c>HKCU\…\Run</c> (no administrator rights).
/// Only the copy started without <c>--data-dir</c> uses it, so a test run never changes the
/// registration of the copy in use. The decisions are <see cref="LoginStartup"/>'s.
/// </summary>
public static class RunKeyRegistry
{
    /// <summary>The running exe (the apphost, also under <c>dotnet run</c>).</summary>
    public static string? ExePath => Environment.ProcessPath;

    /// <summary>What the Run key holds for this exe; <see cref="LoginStartupState.NotRegistered"/> when it cannot be read.</summary>
    public static LoginStartupState ReadState()
    {
        if (ExePath is not { } exe)
        {
            return LoginStartupState.NotRegistered;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LoginStartup.RunKeyPath, writable: false);
            return LoginStartup.StateOf(key?.GetValue(LoginStartup.ValueName) as string, exe);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("resident: could not read the Run key", ex);
            return LoginStartupState.NotRegistered;
        }
    }

    /// <summary>Writes (<paramref name="enabled"/>) or removes the value. Returns an error text, or null.</summary>
    public static string? Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(LoginStartup.RunKeyPath, writable: true);
            if (enabled)
            {
                if (ExePath is not { } exe)
                {
                    return "実行ファイルの場所が分かりません。";
                }

                key.SetValue(LoginStartup.ValueName, LoginStartup.BuildCommand(exe), RegistryValueKind.String);
                AppLog.Info($"resident: registered in the Run key ({LoginStartup.BuildCommand(exe)})");
            }
            else
            {
                key.DeleteValue(LoginStartup.ValueName, throwOnMissingValue: false);
                AppLog.Info("resident: removed from the Run key");
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn("resident: could not change the Run key", ex);
            return ex.Message;
        }
    }
}
