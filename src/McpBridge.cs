using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace PaperTodo;

internal static class McpBridge
{
    public const string CommandLineSwitch = "--mcp";

    public static bool IsRequested(IReadOnlyList<string> args)
        => args.Any(argument =>
            string.Equals(
                argument?.Trim(),
                CommandLineSwitch,
                StringComparison.OrdinalIgnoreCase));

    public static async Task RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        var builder = Host.CreateEmptyApplicationBuilder(
            new HostApplicationBuilderSettings
            {
                Args = args,
                ApplicationName = typeof(McpBridge).Assembly.FullName
            });

        // MCP owns stdout. Diagnostics must never corrupt the stdio protocol.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<McpPipeClient>();
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<McpTools>();

        await builder.Build().RunAsync(cancellationToken);
    }
}
