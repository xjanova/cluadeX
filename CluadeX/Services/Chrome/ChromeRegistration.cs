using Microsoft.Extensions.DependencyInjection;

namespace CluadeX.Services.Chrome;

/// <summary>
/// DI registration for the Claude in Chrome connector (team T4b owns this file). App.ConfigureServices calls
/// <see cref="AddChromeConnector"/>; register here, e.g. the connector, its tool policy as
/// <c>Mcp.IMcpToolPolicy</c> + <c>Mcp.IMcpLaunchResolver</c>, and its prompt section as
/// <c>Tools.IPromptSectionProvider</c>. The owner's route is Chrome THROUGH BrainX: a policy must match the
/// bridged tools by (server, leaf), e.g. server "brainx-brain", leaf "chrome__navigate" (Mcp.McpToolKey).
/// </summary>
public static class ChromeRegistration
{
    public static IServiceCollection AddChromeConnector(this IServiceCollection services) => services;
}
