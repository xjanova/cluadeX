namespace CluadeX.Services.UiStudio;

/// <summary>How the UI Studio talks to the AI: the chat on screen in the app; a harness puts a scripted AI here.</summary>
public interface IStudioAi
{
    /// <summary>A reply is being written (the AI may be editing files).</summary>
    bool IsBusy { get; }

    /// <summary>Send a request as the user's next message; false when a reply is already running or nothing can answer.</summary>
    bool Send(string prompt);

    /// <summary>Raised on the UI thread when <see cref="IsBusy"/> changes.</summary>
    event Action? BusyChanged;
}
