# Lane.Node.Sdk

Lend [Lane](https://github.com/Church-of-Lane/lane) a model over a WebSocket. The SDK handles the connection, reconnecting, concurrency, cancellation and signing. You supply a key and a function that turns a `ModelRequest` into a `ModelResponse`.

```csharp
using Lane.Core.Messages;
using Lane.Core.Models;
using Lane.Node.Sdk;

using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

using FileNodeKey key = FileNodeKey.LoadOrCreate("node-key.pem");

LaneNode node = new(
    new LaneNodeOptions
    {
        LaneUrl = "ws://lane.example.com:5070",
        Model   = "my-model",
        Pool    = "default"
    },
    key,
    async (request, ct) =>
    {
        string text = await MyModel.CompleteAsync(request, ct);

        return new ModelResponse([new TextPart(text)], StopReason.EndTurn, default);
    });

await node.RunAsync(stop.Token);
```

The key file is the node's identity: back it up and keep it private.

Documentation: https://lane-bot.readthedocs.io/en/latest/nodes/
