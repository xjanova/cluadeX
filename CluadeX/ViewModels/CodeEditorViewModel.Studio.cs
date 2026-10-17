namespace CluadeX.ViewModels;

public partial class CodeEditorViewModel
{
    /// <summary>Open a file and scroll to a line — the UI Studio and Node Graph jump to an element's source this way.</summary>
    public async Task OpenAtLineAsync(string fullPath, int line)
    {
        await OpenPathAsync(fullPath);
        if (line > 0)
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                () => ScrollToLineRequested?.Invoke(line));
    }
}
