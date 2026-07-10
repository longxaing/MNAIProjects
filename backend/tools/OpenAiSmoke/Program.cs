using System.ClientModel;
using Microsoft.Extensions.Configuration;
using OpenAI.Responses;

var config = new ConfigurationBuilder()
    .AddUserSecrets("20f2d833-47c3-4fe4-be3d-c9dbc65ec14c")
    .Build();

var endpoint = config["AzureOpenAI:Endpoint"];
var key = config["AzureOpenAI:ApiKey"];
var deployment = config["AzureOpenAI:Deployment"] ?? "gpt-5.1";

if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("Missing AzureOpenAI:Endpoint / AzureOpenAI:ApiKey.");
    return 1;
}

var client = new ResponsesClient(new ApiKeyCredential(key), new ResponsesClientOptions { Endpoint = new Uri(endpoint) });

// 1) Plain streaming
var textOptions = new CreateResponseOptions
{
    Model = deployment,
    StreamingEnabled = true,
    Instructions = "You are terse."
};
textOptions.InputItems.Add(ResponseItem.CreateUserMessageItem("Say hello in exactly five words."));

Console.Write("[stream] ");
var deltas = 0;
var completed = false;
var outputItems = 0;
await foreach (var update in client.CreateResponseStreamingAsync(textOptions))
{
    switch (update)
    {
        case StreamingResponseOutputTextDeltaUpdate d:
            deltas++;
            Console.Write(d.Delta);
            break;
        case StreamingResponseCompletedUpdate c:
            completed = true;
            outputItems = c.Response.OutputItems.Count;
            break;
    }
}
Console.WriteLine();
Console.WriteLine($"[text] deltas={deltas} completed={completed} outputItems={outputItems}");

// 2) Function-call detection (the mechanism AgentRunner relies on)
var tool = ResponseTool.CreateFunctionTool(
    functionName: "get_weather",
    functionParameters: BinaryData.FromString("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}"""),
    strictModeEnabled: false,
    functionDescription: "Get the current weather for a city.");

var toolOptions = new CreateResponseOptions { Model = deployment, StreamingEnabled = true };
toolOptions.Tools.Add(tool);
toolOptions.InputItems.Add(ResponseItem.CreateUserMessageItem("What is the weather in Paris right now? Call the tool."));

ResponseResult? final = null;
await foreach (var update in client.CreateResponseStreamingAsync(toolOptions))
{
    if (update is StreamingResponseCompletedUpdate c)
    {
        final = c.Response;
    }
}

var calls = 0;
if (final is not null)
{
    foreach (var item in final.OutputItems)
    {
        if (item is FunctionCallResponseItem fc)
        {
            calls++;
            Console.WriteLine($"[tool] name={fc.FunctionName} callId={fc.CallId} args={fc.FunctionArguments}");
        }
    }
}
Console.WriteLine($"[tool] functionCalls={calls}");
return 0;
