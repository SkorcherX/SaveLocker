using System.Windows.Forms;

namespace SaveLocker.Agent;

/// <summary>The native "Select save folder" dialog, injected into the agent API server.</summary>
internal static class FolderPicker
{
    /// <summary>Open a Windows folder-picker on a dedicated STA thread and return the chosen path.</summary>
    public static Task<string?> ShowAsync()
    {
        var tcs = new TaskCompletionSource<string?>();
        var thread = new Thread(() =>
        {
            try
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Select save folder",
                    UseDescriptionForTitle = true,
                };
                // Parent to the first open form so the dialog appears in front of the agent window.
                var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                var chosen = dlg.ShowDialog(owner) == DialogResult.OK ? dlg.SelectedPath : null;
                tcs.SetResult(chosen);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
