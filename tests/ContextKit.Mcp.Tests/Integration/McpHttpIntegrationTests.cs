using System.Net.Http.Json;
using System.Text.Json;

namespace GroundKit.Mcp.Tests.Integration;

public sealed class McpHttpIntegrationTests
{
    [Fact]
    public async Task Should_Initialize_And_List_GroundKit_Tools_Over_Http()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = timeout.Token;
        await using var app = McpCommandLine.CreateHttpApp(
            libraries: null,
            urls: "http://127.0.0.1:0",
            host: null,
            port: null
        );

        await app.StartAsync(cancellationToken);

        using var client = new HttpClient();
        var endpoint = $"{app.Urls.Single()}/mcp";
        using var initialize = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "2025-03-26",
                        capabilities = new { },
                        clientInfo = new { name = "GroundKit.Tests", version = "1.0" },
                    },
                }
            ),
        };
        initialize.Headers.Add("Accept", "application/json, text/event-stream");

        using var initializeResponse = await client.SendAsync(initialize, cancellationToken);
        initializeResponse.IsSuccessStatusCode.ShouldBeTrue();
        _ = await ReadMcpPayloadAsync(initializeResponse, cancellationToken);

        using var tools = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/list",
                    @params = new { },
                }
            ),
        };
        tools.Headers.Add("Accept", "application/json, text/event-stream");
        if (initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds))
        {
            tools.Headers.Add("Mcp-Session-Id", sessionIds.Single());
        }

        using var toolsResponse = await client.SendAsync(tools, cancellationToken);
        toolsResponse.IsSuccessStatusCode.ShouldBeTrue();
        var toolNames = (await ReadMcpPayloadAsync(toolsResponse, cancellationToken))
            .RootElement.GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();

        toolNames.ShouldContain("resolve-source");
        toolNames.ShouldContain("query-docs");
        toolNames.ShouldContain("ask-docs");
        toolNames.ShouldContain("get_docs");
        toolNames.ShouldContain("find_libraries");
        toolNames.ShouldContain("search-docs");
        toolNames.ShouldContain("library_catalog");
    }

    private static async Task<JsonDocument> ReadMcpPayloadAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    )
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var json =
            content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["data:".Length..].Trim())
                .LastOrDefault() ?? content;
        return JsonDocument.Parse(json);
    }
}
