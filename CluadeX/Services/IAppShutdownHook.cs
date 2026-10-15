namespace CluadeX.Services;

/// <summary>
/// Work that must happen when CluadeX exits normally — e.g. killing background dev servers with their whole
/// process tree. Register in DI as <c>IAppShutdownHook</c> (forwarding to the singleton that owns the state).
/// App.OnExit runs every hook once, after settings and chats are flushed and before the DI container is
/// disposed. A hook must be quick (bounded, well under a second or two) and must not throw — an exception is
/// swallowed so the other hooks still run. A crash does not run hooks: anything that must also die then needs
/// its own guarantee (a job object with KILL_ON_JOB_CLOSE).
/// </summary>
public interface IAppShutdownHook
{
    void OnAppShutdown();
}
