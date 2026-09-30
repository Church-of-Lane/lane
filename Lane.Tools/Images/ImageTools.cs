using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Lane.Core.Memory;
using Lane.Core.Messages;
using Lane.Core.Sessions;
using Lane.Core.Tools;
using Lane.Tools.Notes;
using Microsoft.Extensions.Logging;

namespace Lane.Tools.Images;

/// <summary>What is known about a saved image. The bytes are stored separately under <see cref="ImageShelf.DataPrefix"/>.</summary>
public sealed record SavedImage(
    string         Name,
    string         MediaType,
    string         Source,
    string?        Caption,
    int            Size,
    DateTimeOffset SavedAt);

public sealed record SavedImageData(byte[] Data);

/// <summary>Shared rules for the saved-image tools, in the same global scope as the scratchpad.</summary>
internal static class ImageShelf
{
    internal static readonly ScopeKey Scope = new("global");

    internal const string Prefix     = "image:";
    internal const string DataPrefix = "image-data:";

    internal const string HttpClientName = "lane.images";

    /// <summary>Anthropic's limit for one image.</summary>
    internal const int MaxBytes  = 5 * 1024 * 1024;
    internal const int MaxImages = 128;
    internal const int MaxCaption = 500;

    internal static string? KeyFor(string? name) =>
        Scratchpad.Clean(name) is { } clean ? Prefix + clean.ToLowerInvariant() : null;

    internal static string DataKeyFor(string key) => DataPrefix + key[Prefix.Length..];

    /// <summary>The media type from the file's magic bytes, or null when it is not a format a model can see.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47])) return "image/png";
        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))       return "image/jpeg";
        if (data.StartsWith("GIF8"u8))                                     return "image/gif";

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        return null;
    }

    /// <summary>
    /// Connects only to public addresses, checked at connect time so a redirect or a DNS answer
    /// cannot point the download at the host's own network.
    /// </summary>
    internal static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);

        IPAddress target = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is not a public address.");

        Socket socket = new(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            await socket.ConnectAsync(target, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ||
                     address.Equals(IPAddress.IPv6Any));

        byte[] b = address.GetAddressBytes();

        return b[0] switch
        {
            0 or 10 or 127                      => false,
            100 when b[1] is >= 64 and <= 127   => false,
            169 when b[1] == 254                => false,
            172 when b[1] is >= 16 and <= 31    => false,
            192 when b[1] == 168                => false,
            >= 224                              => false,
            _                                   => true
        };
    }
}

[LaneTool]
public sealed class SaveImageTool(IKeyValueStore store, IHttpClientFactory http, ILogger<SaveImageTool> log)
    : Tool<SaveImageTool.Args>
{
    public sealed record Args(
        [property: Description("The image's url, e.g. one shown after [image: …] in a message.")] string Url,
        [property: Description("A short name to find it by later.")] string Name,
        [property: Description("What the image is, in a sentence. Optional.")] string? Caption = null);

    protected override string Name => "save_image";

    protected override string Description =>
        "Save an image from a url so you can look at it again later with view_image, even after the link stops working.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        if (!Uri.TryCreate(args.Url?.Trim(), UriKind.Absolute, out Uri? url) ||
            (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            return ToolResult.Error("That is not an http(s) url.");

        string? name = Scratchpad.Clean(args.Name);
        string? key  = ImageShelf.KeyFor(args.Name);

        if (name is null || key is null) return ToolResult.Error("The image needs a name.");

        string? caption = string.IsNullOrWhiteSpace(args.Caption) ? null : args.Caption.Trim();

        if (caption is { Length: > ImageShelf.MaxCaption })
            return ToolResult.Error($"Keep the caption under {ImageShelf.MaxCaption} characters.");

        if (await store.GetAsync<SavedImage>(ImageShelf.Scope, key, ct).ConfigureAwait(false) is { } existing)
            return ToolResult.Error($"You already have an image called '{existing.Name}'. Pick another name, or delete_image it first.");

        int count = (await store.ListKeysAsync(ImageShelf.Scope, ImageShelf.Prefix, ct).ConfigureAwait(false)).Count;

        if (count >= ImageShelf.MaxImages)
            return ToolResult.Error($"You already have {count} saved images, which is the limit. Delete one with delete_image first.");

        byte[] data;

        try
        {
            data = await DownloadAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            log.LogInformation(ex, "Could not download {Url}", url);
            return ToolResult.Error($"Could not download it: {ex.Message}");
        }
        catch (InvalidDataException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        if (ImageShelf.Sniff(data) is not { } mediaType)
            return ToolResult.Error("That url is not a png, jpeg, gif or webp image.");

        await store.SetAsync(ImageShelf.Scope, ImageShelf.DataKeyFor(key), new SavedImageData(data), ct).ConfigureAwait(false);
        await store.SetAsync(ImageShelf.Scope, key,
            new SavedImage(name, mediaType, url.ToString(), caption, data.Length, DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        log.LogInformation("Saved image '{Name}' ({Size} bytes) from {Url}", name, data.Length, url);

        return ToolResult.Ok($"Saved as '{name}'.")
                         .RememberAs($"[saved an image as '{name}'{(caption is null ? "" : $": {caption}")}]", MemoryScopeHint.Global);
    }

    private async Task<byte[]> DownloadAsync(Uri url, CancellationToken ct)
    {
        HttpClient client = http.CreateClient(ImageShelf.HttpClientName);

        using HttpResponseMessage response =
            await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > ImageShelf.MaxBytes)
            throw new InvalidDataException($"That image is over the {ImageShelf.MaxBytes / (1024 * 1024)} MB limit.");

        await using Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;

        while ((read = await body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ImageShelf.MaxBytes)
                throw new InvalidDataException($"That image is over the {ImageShelf.MaxBytes / (1024 * 1024)} MB limit.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

[LaneTool]
public sealed class ViewImageTool(IKeyValueStore store) : Tool<ViewImageTool.Args>
{
    public sealed record Args(
        [property: Description("Name of the image, as list_images gives it.")] string Name);

    protected override string Name => "view_image";

    protected override string Description => "Look at an image you saved with save_image.";

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? key = ImageShelf.KeyFor(args.Name);

        if (key is null) return ToolResult.Error("Which image? Try list_images.");

        SavedImage? image = await store.GetAsync<SavedImage>(ImageShelf.Scope, key, ct).ConfigureAwait(false);
        SavedImageData? data = image is null
            ? null
            : await store.GetAsync<SavedImageData>(ImageShelf.Scope, ImageShelf.DataKeyFor(key), ct).ConfigureAwait(false);

        if (image is null || data is null)
            return ToolResult.Error($"You have no image called '{args.Name?.Trim()}'. Try list_images.");

        string header = image.Caption is null ? image.Name : $"{image.Name}: {image.Caption}";

        return new ToolResult
        {
            Content = [new TextPart($"{header} (saved {Scratchpad.Ago(image.SavedAt)} from {image.Source})"),
                       new ImagePart(null, data.Data, image.MediaType)]
        };
    }
}

/// <summary>Posts a saved image into the conversation. Only offered where the channel can carry images.</summary>
[LaneTool]
public sealed class SendImageTool(IKeyValueStore store) : Tool<SendImageTool.Args>
{
    public sealed record Args(
        [property: Description("Name of the image, as list_images gives it.")] string Name,
        [property: Description("Text to send with it. Optional.")] string? Message = null);

    protected override string Name => "send_image";

    protected override string Description => "Send one of your saved images into the conversation.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override ToolAvailability Availability => new()
    {
        RequiredCapabilities = ChannelCapabilities.Images,
        AllowedTurns         = TurnKind.Respond,
        RequiresSession      = true
    };

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? key = ImageShelf.KeyFor(args.Name);

        if (key is null) return ToolResult.Error("Which image? Try list_images.");

        SavedImage? image = await store.GetAsync<SavedImage>(ImageShelf.Scope, key, ct).ConfigureAwait(false);
        SavedImageData? data = image is null
            ? null
            : await store.GetAsync<SavedImageData>(ImageShelf.Scope, ImageShelf.DataKeyFor(key), ct).ConfigureAwait(false);

        if (image is null || data is null)
            return ToolResult.Error($"You have no image called '{args.Name?.Trim()}'. Try list_images.");

        if (context.Session is not { } id || context.Sessions is null ||
            !context.Sessions.TryGet(id, out Session? session))
            return ToolResult.Error("I cannot tell which conversation this is.");

        IReadOnlyList<ITextOutput> outputs =
            session.ResolveOutputs<ITextOutput>(DeliveryTarget.Requiring(ChannelCapabilities.Images));

        if (outputs.Count == 0) return ToolResult.Error("You cannot send images here.");

        string text = args.Message?.Trim() ?? "";

        await outputs[0].SendAsync(
            new OutboundText(text, context.TriggerExternalId, [new ImagePart(null, data.Data, image.MediaType)]),
            ct).ConfigureAwait(false);

        return ToolResult.Ok($"Sent '{image.Name}'.")
                         .RememberAs($"[sent the image '{image.Name}'{(image.Caption is null ? "" : $": {image.Caption}")}]",
                                     MemoryScopeHint.CurrentSession);
    }
}

[LaneTool]
public sealed class ListImagesTool(IKeyValueStore store) : Tool<NoArgs>
{
    protected override string Name => "list_images";

    protected override string Description => "List the images you have saved, most recent first.";

    protected override async ValueTask<ToolResult> InvokeAsync(NoArgs args, ToolContext context, CancellationToken ct)
    {
        IReadOnlyList<string> keys =
            await store.ListKeysAsync(ImageShelf.Scope, ImageShelf.Prefix, ct).ConfigureAwait(false);

        List<SavedImage> found = [];

        foreach (string key in keys)
        {
            if (await store.GetAsync<SavedImage>(ImageShelf.Scope, key, ct).ConfigureAwait(false) is { } image)
                found.Add(image);
        }

        if (found.Count == 0) return ToolResult.Ok("(no saved images)");

        StringBuilder sb = new();

        foreach (SavedImage image in found.OrderByDescending(i => i.SavedAt))
        {
            sb.Append("- ").Append(image.Name).Append(" (").Append(Scratchpad.Ago(image.SavedAt)).Append(')');

            if (image.Caption is not null) sb.Append(": ").Append(image.Caption);

            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }
}

[LaneTool]
public sealed class DeleteImageTool(IKeyValueStore store) : Tool<DeleteImageTool.Args>
{
    public sealed record Args(
        [property: Description("Name of the image to throw away.")] string Name);

    protected override string Name => "delete_image";

    protected override string Description => "Throw away an image you saved once it is no longer any use.";

    protected override ToolSafety Safety => ToolSafety.Mutating;

    protected override async ValueTask<ToolResult> InvokeAsync(Args args, ToolContext context, CancellationToken ct)
    {
        string? key = ImageShelf.KeyFor(args.Name);

        if (key is null) return ToolResult.Error("Which image? Try list_images.");

        if (await store.GetAsync<SavedImage>(ImageShelf.Scope, key, ct).ConfigureAwait(false) is not { } image)
            return ToolResult.Error($"You have no image called '{args.Name.Trim()}'. Try list_images.");

        await store.RemoveAsync(ImageShelf.Scope, key, ct).ConfigureAwait(false);
        await store.RemoveAsync(ImageShelf.Scope, ImageShelf.DataKeyFor(key), ct).ConfigureAwait(false);

        return ToolResult.Ok($"Threw away '{image.Name}'.")
                         .RememberAs($"[threw away the saved image '{image.Name}']", MemoryScopeHint.Global);
    }
}
