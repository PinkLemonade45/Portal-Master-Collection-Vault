using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkylandersCollection.Desktop;

// Talks to Discord's local IPC pipe directly, bypassing the DiscordRichPresence library
// which incorrectly applies a 32-char asset-key limit to external image URLs.
internal sealed class DiscordRpcService : IDisposable
{
    private const string ClientId = "1508001853777117295";
    private const string LogoKey = "logo";
    private const string PortraitBaseUrl = "https://raw.githubusercontent.com/ItsMeTyler25/Portal-Master-Vault-Portraits/main/";
    private const string ElementBaseUrl = "https://raw.githubusercontent.com/ItsMeTyler25/Portal-Master-Vault-Portraits/main/elements/";

    private static readonly HashSet<string> WebpPortraitIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "love-potion-pop-fizz",
        "eons-elite-whirlwind",
        "eons-elite-terrafin",
        "eons-elite-eruptor",
        "eons-elite-gill-grunt",
        "eons-elite-spyro",
        "eons-elite-trigger-happy",
        "eons-elite-stealth-elf",
        "eons-elite-chop-chop"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private NamedPipeClientStream? _pipe;
    private int _nonce;
    private Activity? _lastActivity;

    public DiscordRpcService() => TryConnect();

    // Called every second by the timer — reconnects if Discord was restarted.
    public void Invoke()
    {
        if (_pipe?.IsConnected == true) return;
        _pipe?.Dispose();
        _pipe = null;
        if (TryConnect() && _lastActivity is not null)
            Send(_lastActivity);
    }

    public void SetIdle(int collectionCount) =>
        Send(new Activity("Browsing their collection", CollectionState(collectionCount),
            assets: new ActivityAssets(LogoKey, "Portal Master Vault", null, null)));

    public void SetPortalMissing(int collectionCount) =>
        Send(new Activity("Portal offline", CollectionState(collectionCount),
            assets: new ActivityAssets(LogoKey, "Portal Master Vault", null, null)));

    public void SetScanning(int collectionCount) =>
        Send(new Activity(
            "Scanning on the portal",
            CollectionState(collectionCount),
            assets: new ActivityAssets(LogoKey, "Portal Master Vault", null, null),
            timestamps: new ActivityTimestamps(DateTimeOffset.UtcNow)));

    public void SetFigureOnPortal(string figureName, string? element, string? type, int collectionCount, string? figureId = null, string? portraitId = null)
    {
        string details = element != null ? $"{figureName} · {element}" : figureName;
        string portraitUrl = LargePortraitUrl(portraitId ?? figureId);
        string? smallUrl = SmallIconUrl(element, type);
        string? smallText = SmallIconText(element, type);

        Send(new Activity(
            details,
            CollectionState(collectionCount),
            assets: new ActivityAssets(portraitUrl, figureName, smallUrl, smallText),
            timestamps: new ActivityTimestamps(DateTimeOffset.UtcNow)));
    }

    public void SetSwapFigureOnPortal(
        string figureName,
        string? topElement,
        string? bottomElement,
        int collectionCount,
        string? figureId = null)
    {
        string? elementText = SwapElementText(topElement, bottomElement);
        string details = elementText != null ? $"{figureName} - {elementText}" : figureName;
        string portraitUrl = LargePortraitUrl(figureId);
        string? smallUrl = SwapElementIconUrl(topElement, bottomElement);

        Send(new Activity(
            details,
            CollectionState(collectionCount),
            assets: new ActivityAssets(portraitUrl, figureName, smallUrl, elementText),
            timestamps: new ActivityTimestamps(DateTimeOffset.UtcNow)));
    }

    private static string LargePortraitUrl(string? portraitId)
    {
        if (string.IsNullOrWhiteSpace(portraitId))
        {
            return LogoKey;
        }

        string extension = WebpPortraitIds.Contains(portraitId) ? "webp" : "png";
        return $"{PortraitBaseUrl}{portraitId}.{extension}";
    }

    private static string? SmallIconUrl(string? element, string? type) => type switch
    {
        "Magic Item"     => $"{ElementBaseUrl}magic-item.png",
        "Adventure Pack" => $"{ElementBaseUrl}adventure-pack.png",
        _ => element != null ? $"{ElementBaseUrl}{element.ToLower()}.png" : null
    };

    private static string? SmallIconText(string? element, string? type) => type switch
    {
        "Magic Item"     => "Magic Item",
        "Adventure Pack" => "Adventure Pack",
        _ => element
    };

    private static string? SwapElementIconUrl(string? topElement, string? bottomElement)
    {
        string? topSlug = ElementSlug(topElement);
        string? bottomSlug = ElementSlug(bottomElement);

        if (topSlug is not null && bottomSlug is not null)
        {
            return $"{ElementBaseUrl}swaps/{topSlug}-{bottomSlug}.png";
        }

        return SmallIconUrl(topElement ?? bottomElement, "SWAP Force");
    }

    private static string? SwapElementText(string? topElement, string? bottomElement)
    {
        if (!string.IsNullOrWhiteSpace(topElement) && !string.IsNullOrWhiteSpace(bottomElement))
        {
            if (string.Equals(topElement, bottomElement, StringComparison.OrdinalIgnoreCase))
            {
                return topElement;
            }

            return $"{topElement} + {bottomElement}";
        }

        return string.IsNullOrWhiteSpace(topElement) ? bottomElement : topElement;
    }

    private static string? ElementSlug(string? element)
    {
        if (string.IsNullOrWhiteSpace(element))
        {
            return null;
        }

        return element.Trim().ToLowerInvariant().Replace(" ", "-");
    }

    private void Send(Activity activity)
    {
        _lastActivity = activity;
        if (_pipe is null || !_pipe.IsConnected) return;

        try
        {
            WriteFrame(_pipe, Opcode.Frame, new RpcCommand(
                "SET_ACTIVITY",
                new RpcArgs(Environment.ProcessId, activity),
                (_nonce++).ToString()));
            ReadFrame(_pipe);
        }
        catch
        {
            _pipe?.Dispose();
            _pipe = null;
        }
    }

    private bool TryConnect()
    {
        for (int i = 0; i <= 9; i++)
        {
            try
            {
                var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut);
                pipe.Connect(50);
                WriteFrame(pipe, Opcode.Handshake, new { v = 1, client_id = ClientId });
                ReadFrame(pipe); // HELLO / READY
                _pipe = pipe;
                return true;
            }
            catch { }
        }
        return false;
    }

    private static void WriteFrame(PipeStream pipe, int opcode, object payload)
    {
        byte[] data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        byte[] header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), data.Length);
        pipe.Write(header);
        pipe.Write(data);
        pipe.Flush();
    }

    private static void ReadFrame(PipeStream pipe)
    {
        byte[] header = new byte[8];
        int read = 0;
        while (read < 8)
            read += pipe.Read(header, read, 8 - read);

        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length <= 0) return;

        byte[] data = new byte[length];
        read = 0;
        while (read < length)
            read += pipe.Read(data, read, length - read);
    }

    public void Dispose()
    {
        try
        {
            if (_pipe?.IsConnected == true)
            {
                // Clear the presence on exit
                WriteFrame(_pipe, Opcode.Frame, new RpcCommand(
                    "SET_ACTIVITY",
                    new RpcArgs(Environment.ProcessId, null),
                    (_nonce++).ToString()));
            }
        }
        catch { }
        _pipe?.Dispose();
    }

    private static string CollectionState(int count) =>
        count == 1 ? "1 figure collected" : $"{count} figures collected";

    private static class Opcode
    {
        public const int Handshake = 0;
        public const int Frame = 1;
    }

    // ── JSON model ────────────────────────────────────────────────────────────

    private sealed record RpcCommand(
        [property: JsonPropertyName("cmd")] string Cmd,
        [property: JsonPropertyName("args")] RpcArgs Args,
        [property: JsonPropertyName("nonce")] string Nonce);

    private sealed record RpcArgs(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("activity")] Activity? Activity);

    private sealed class Activity
    {
        public Activity(string details, string state,
            ActivityAssets? assets = null,
            ActivityTimestamps? timestamps = null)
        {
            Details = details;
            State = state;
            Assets = assets ?? new ActivityAssets(LogoKey, "Portal Master Vault", null, null);
            Timestamps = timestamps;
        }

        [JsonPropertyName("details")] public string Details { get; }
        [JsonPropertyName("state")] public string State { get; }
        [JsonPropertyName("assets")] public ActivityAssets Assets { get; }
        [JsonPropertyName("timestamps")] public ActivityTimestamps? Timestamps { get; }
    }

    private sealed class ActivityAssets
    {
        public ActivityAssets(string largeImage, string largeText, string? smallImage, string? smallText)
        {
            LargeImage = largeImage;
            LargeText = largeText;
            SmallImage = smallImage;
            SmallText = smallImage != null ? smallText : null;
        }

        [JsonPropertyName("large_image")] public string LargeImage { get; }
        [JsonPropertyName("large_text")] public string LargeText { get; }
        [JsonPropertyName("small_image")] public string? SmallImage { get; }
        [JsonPropertyName("small_text")] public string? SmallText { get; }
    }

    private sealed class ActivityTimestamps
    {
        public ActivityTimestamps(DateTimeOffset start) => Start = start.ToUnixTimeSeconds();
        [JsonPropertyName("start")] public long Start { get; }
    }
}
