using System.Drawing;
using System.Windows.Forms;

namespace LocalGameSync.Agent;

/// <summary>App-wide UI resources: the tray/window icon and a robust clipboard copy.</summary>
internal static class AppResources
{
    /// <summary>The agent icon, loaded once from the embedded SaveLocker.ico.</summary>
    public static Icon Icon { get; } = LoadIcon();

    private static Icon LoadIcon()
    {
        try
        {
            var asm = typeof(AppResources).Assembly;
            using var stream = asm.GetManifestResourceStream("LocalGameSync.Agent.Assets.SaveLocker.ico");
            if (stream is null) return SystemIcons.Application;
            return new Icon(stream);
        }
        catch
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>Copy text to the clipboard, tolerating transient OLE failures.</summary>
    public static bool TryCopy(string text)
    {
        try { Clipboard.SetText(text); return true; }
        catch
        {
            try { Clipboard.SetDataObject(text, copy: true, retryTimes: 5, retryDelay: 150); return true; }
            catch { return false; }
        }
    }
}
