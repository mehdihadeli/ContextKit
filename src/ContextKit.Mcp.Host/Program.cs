using GroundKit.Mcp;

var app = McpCommandLine.CreateCommandApp();
return await app.RunAsync(McpCommandLine.NormalizeArguments(args));