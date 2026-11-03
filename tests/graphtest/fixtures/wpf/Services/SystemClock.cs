namespace Demo.Services;

public sealed class SystemClock : IClock
{
    public System.DateTime Now() => System.DateTime.Now;
}
