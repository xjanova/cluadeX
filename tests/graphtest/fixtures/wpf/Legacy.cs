namespace Demo;

// new Demo.Services.SystemClock();
/* var vm = new Demo.ViewModels.ClockViewModel(); */
public static class Legacy
{
    public const string Text = "new Demo.Services.SystemClock()";
#if NEVER_DEFINED
    static object Old = new Demo.ViewModels.ClockViewModel();
#endif
}
