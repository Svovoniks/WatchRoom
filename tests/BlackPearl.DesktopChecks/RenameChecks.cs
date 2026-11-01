using System.IO;
using BlackPearl.Core;

// The app was called Watchroom before it became BlackPearl; what people already had keeps working.
static class RenameChecks
{
    public static void Run(Action<bool, string> check)
    {
        var local = Path.Combine(Path.GetTempPath(), "bp-rename-" + Guid.NewGuid().ToString("N"));
        string current = Path.Combine(local, "BlackPearl"), former = Path.Combine(local, "Watchroom");
        try
        {
            check(BlackPearl.Desktop.Program.DefaultData(local) == current, "a new installation keeps its library in the BlackPearl folder");
            Directory.CreateDirectory(former); File.WriteAllText(Path.Combine(former, "library.db"), "");
            // The relaunch after an update creates BlackPearl\updates beside the old library; that must not hide it.
            Directory.CreateDirectory(Path.Combine(current, "updates"));
            check(BlackPearl.Desktop.Program.DefaultData(local) == former, "a library from before the rename stays in use from its Watchroom folder");
            File.WriteAllText(Path.Combine(current, "library.db"), "");
            check(BlackPearl.Desktop.Program.DefaultData(local) == current, "a BlackPearl library takes precedence over an older Watchroom one");
        }
        finally { if (Directory.Exists(local)) Directory.Delete(local, true); }
        const string name = "RENAME_CHECK_SETTING";
        try
        {
            Environment.SetEnvironmentVariable(AppEnvironment.FormerPrefix + name, "former");
            check(AppEnvironment.Get(name) == "former", "WATCHROOM_ settings still apply");
            Environment.SetEnvironmentVariable(AppEnvironment.Prefix + name, "current");
            check(AppEnvironment.Get(name) == "current", "BLACKPEARL_ settings take precedence over WATCHROOM_ ones");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppEnvironment.Prefix + name, null);
            Environment.SetEnvironmentVariable(AppEnvironment.FormerPrefix + name, null);
        }
    }
}
