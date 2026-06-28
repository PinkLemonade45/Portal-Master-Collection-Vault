using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace SkylandersCollection.Desktop;

public partial class MainWindow : Window
{
    private const string AppHost = "skylanders.local";
    private static bool DebugFeaturesEnabled
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private PortalScanner? _scanner;
    private CollectionStore? _collectionStore;
    private string? _contentRoot;
    private DiscordRpcService? _discordRpc;
    private DispatcherTimer? _discordTimer;
    private int _collectionCount;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await InitializeWebViewAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Portal Master Vault could not start.\n\n{ex.Message}",
                "Portal Master Vault",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task InitializeWebViewAsync()
    {
        _contentRoot = FindContentRoot();
        string userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SkylandersCollection",
            "WebView2");

        Directory.CreateDirectory(userDataFolder);

        CoreWebView2EnvironmentOptions options = new()
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
        };

        CoreWebView2Environment environment =
            await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder, options: options);

        await Browser.EnsureCoreWebView2Async(environment);

        if (!DebugFeaturesEnabled)
        {
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        }

        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            AppHost,
            _contentRoot,
            CoreWebView2HostResourceAccessKind.Allow);

        _collectionStore = new CollectionStore(_contentRoot);
        _collectionCount = _collectionStore.GetCollectionCount();
        _scanner = new PortalScanner(_collectionStore);
        _scanner.Message += OnScannerMessage;

        try
        {
            _discordRpc = new DiscordRpcService();
            _discordTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _discordTimer.Tick += (_, _) =>
            {
                try { _discordRpc?.Invoke(); } catch { }
            };
            _discordTimer.Start();
        }
        catch
        {
            // Discord RPC is optional — app works fine without it
        }

        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        if (DebugFeaturesEnabled)
        {
            Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            Browser.CoreWebView2.Settings.AreDevToolsEnabled = true;
            Browser.CoreWebView2.Settings.IsStatusBarEnabled = true;
        }
        Browser.Source = new Uri($"https://{AppHost}/app/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
            string? type = document.RootElement.TryGetProperty("type", out JsonElement typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "checkPortal":
                    _scanner?.CheckPortal();
                    break;
                case "startScanner":
                    _scanner?.Start();
                    break;
                case "stopScanner":
                    _scanner?.Stop();
                    break;
                case "requestFigureDump":
                    if (DebugFeaturesEnabled)
                    {
                        _scanner?.RequestFigureDump();
                    }
                    break;
                case "resetCollection":
                    _scanner?.ResetCollection();
                    break;
                case "removeUidlessDuplicates":
                    if (DebugFeaturesEnabled)
                    {
                        RemoveUidlessDuplicates();
                    }
                    break;
                case "removeCollectionEntry":
                    RemoveCollectionEntry(document.RootElement);
                    break;
                case "setManualOwnership":
                    SetManualOwnership(document.RootElement);
                    break;
                case "resolveScanVariant":
                    ResolveScanVariant(document.RootElement);
                    break;
                case "cancelScanVariant":
                    CancelScanVariant(document.RootElement);
                    break;
                case "injectScan":
                    if (DebugFeaturesEnabled)
                    {
                        InjectScan(document.RootElement);
                    }
                    break;
                case "saveUpgradeCatalog":
                    SaveUpgradeCatalog(document.RootElement);
                    break;
            }
        }
        catch (JsonException)
        {
            SendMessageToWeb(new { type = "scannerError", text = "The desktop app received an invalid UI command." });
        }
    }

    private void RemoveUidlessDuplicates()
    {
        if (_collectionStore is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "The collection store is not ready yet." });
            return;
        }

        int removed = _collectionStore.RemoveUidlessDuplicates();
        SendMessageToWeb(new
        {
            type = "scanner",
            status = "collectionCleaned",
            text = removed == 1
                ? "Removed 1 no-UID duplicate."
                : $"Removed {removed} no-UID duplicates."
        });
    }

    private void RemoveCollectionEntry(JsonElement root)
    {
        if (_collectionStore is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "The collection store is not ready yet." });
            return;
        }

        if (!root.TryGetProperty("entry", out JsonElement entryElement))
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "No collection entry was provided." });
            return;
        }

        CollectionEntryRemovalRequest? request = entryElement.Deserialize<CollectionEntryRemovalRequest>(_jsonOptions);
        if (request is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "That collection entry could not be removed." });
            return;
        }

        bool removed = _collectionStore.RemoveCollectionEntry(request, out string? removedName);
        SendMessageToWeb(new
        {
            type = "scanner",
            status = "collectionEntryRemoved",
            text = removed
                ? $"{removedName ?? "Entry"} removed."
                : "That collection entry was already gone."
        });
    }

    private void SetManualOwnership(JsonElement root)
    {
        if (_collectionStore is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "The collection store is not ready yet." });
            return;
        }

        if (!root.TryGetProperty("entry", out JsonElement entryElement))
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "No manual variant entry was provided." });
            return;
        }

        bool owned = root.TryGetProperty("owned", out JsonElement ownedElement) && ownedElement.GetBoolean();
        ManualCollectionEntry? entry = entryElement.Deserialize<ManualCollectionEntry>(_jsonOptions);
        if (entry is null || string.IsNullOrWhiteSpace(entry.PhysicalVariantId))
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "That manual variant is missing its catalog ID." });
            return;
        }

        _collectionStore.SetManualOwnership(entry, owned);

        SendMessageToWeb(new
        {
            type = "scanner",
            status = "manualOwnershipUpdated",
            text = owned ? $"{entry.Name} marked owned." : $"{entry.Name} removed."
        });
    }

    private void ResolveScanVariant(JsonElement root)
    {
        string? choiceToken = root.TryGetProperty("choiceToken", out JsonElement tokenElement)
            ? tokenElement.GetString()
            : null;
        string? selectedId = root.TryGetProperty("selectedId", out JsonElement selectedElement)
            ? selectedElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(choiceToken) || string.IsNullOrWhiteSpace(selectedId))
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "No scan variant was selected." });
            return;
        }

        _scanner?.ResolveVariantChoice(choiceToken, selectedId);
    }

    private void CancelScanVariant(JsonElement root)
    {
        string? choiceToken = root.TryGetProperty("choiceToken", out JsonElement tokenElement)
            ? tokenElement.GetString()
            : null;

        if (!string.IsNullOrWhiteSpace(choiceToken))
        {
            _scanner?.CancelVariantChoice(choiceToken);
        }
    }

    private void InjectScan(JsonElement root)
    {
        if (_scanner is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "The scanner is not ready yet." });
            return;
        }

        if (!root.TryGetProperty("entries", out JsonElement entriesElement) ||
            entriesElement.ValueKind is not JsonValueKind.Array)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "No injected scan entries were provided." });
            return;
        }

        List<FigureScan> scans = [];
        int toyIndex = 0x40;
        foreach (JsonElement entry in entriesElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("toyId", out JsonElement toyIdElement) ||
                !entry.TryGetProperty("variantId", out JsonElement variantIdElement) ||
                !toyIdElement.TryGetInt32(out int toyId) ||
                !variantIdElement.TryGetInt32(out int variantId))
            {
                continue;
            }

            string toyIdHex = entry.TryGetProperty("toyIdHex", out JsonElement toyHexElement) &&
                !string.IsNullOrWhiteSpace(toyHexElement.GetString())
                    ? toyHexElement.GetString()!
                    : $"0x{toyId:X4}";
            string variantIdHex = entry.TryGetProperty("variantIdHex", out JsonElement variantHexElement) &&
                !string.IsNullOrWhiteSpace(variantHexElement.GetString())
                    ? variantHexElement.GetString()!
                    : $"0x{variantId:X4}";

            scans.Add(new FigureScan
            {
                ToyIndex = toyIndex,
                ToyIndexHex = $"0x{toyIndex:X2}",
                ToyId = toyId,
                ToyIdHex = toyIdHex,
                VariantId = variantId,
                VariantIdHex = variantIdHex,
                Block1 = $"Injected test scan for {toyIdHex}:{variantIdHex}",
                ScannedAt = DateTimeOffset.Now,
                IsInjectedTestScan = true
            });

            toyIndex++;
        }

        _scanner.InjectScans(scans);
    }

    private void SaveUpgradeCatalog(JsonElement root)
    {
        if (_contentRoot is null)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "The app content folder is not ready yet." });
            return;
        }

        if (!root.TryGetProperty("catalog", out JsonElement catalogElement) ||
            catalogElement.ValueKind is not JsonValueKind.Object ||
            !catalogElement.TryGetProperty("entries", out JsonElement entriesElement) ||
            entriesElement.ValueKind is not JsonValueKind.Array)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "Upgrade data was not in the expected format." });
            return;
        }

        try
        {
            JsonSerializerOptions writeOptions = new(_jsonOptions)
            {
                WriteIndented = true
            };
            string json = JsonSerializer.Serialize(catalogElement, writeOptions);
            string sourceCatalogPath = FindEditableUpgradeCatalogPath(_contentRoot);
            WriteTextAtomically(sourceCatalogPath, json);

            string runtimeCatalogPath = Path.Combine(_contentRoot, "data", "catalog", "upgrades.json");
            if (!PathsEqual(sourceCatalogPath, runtimeCatalogPath))
            {
                WriteTextAtomically(runtimeCatalogPath, json);
            }

            SendMessageToWeb(new
            {
                type = "scanner",
                status = "upgradeCatalogSaved",
                text = $"Upgrade editor saved to {sourceCatalogPath}."
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SendMessageToWeb(new { type = "scanner", status = "error", text = "Failed to save upgrade data: " + ex.Message });
        }
    }

    private void OnScannerMessage(object? sender, ScannerMessage message)
    {
        Dispatcher.Invoke(() =>
        {
            SendMessageToWeb(new
            {
                type = "scanner",
                status = message.Type,
                text = message.Text,
                entry = message.Entry,
                entries = message.Entries,
                choices = message.Choices,
                choiceToken = message.ChoiceToken
            });

            try { UpdateDiscordPresence(message); }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "skylanders_discord.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] UpdateDiscordPresence({message.Type}): {ex.GetType().Name}: {ex.Message}\n");
                }
                catch { }
            }
        });
    }

    private void UpdateDiscordPresence(ScannerMessage message)
    {
        if (_discordRpc is null)
        {
            return;
        }

        switch (message.Type)
        {
            case "portalMissing":
                _discordRpc.SetPortalMissing(_collectionCount);
                break;

            case "portalReady":
            case "ready":
                _discordRpc.SetScanning(_collectionCount);
                break;

            case "removed":
                _discordRpc.SetScanning(_collectionCount);
                break;

            case "newDiscovery":
                _collectionCount++;
                _discordRpc.SetFigureOnPortal(
                    DisplayName(message.Entry),
                    message.Entry?.Element,
                    message.Entry?.Type,
                    _collectionCount,
                    message.Entry?.Id,
                    message.Entry?.Portrait);
                break;

            case "newSwapDiscovery":
            {
                _collectionCount += message.Entries?.Count ?? 1;
                SetSwapDiscordPresence(message);
                break;
            }

            case "scan":
                _discordRpc.SetFigureOnPortal(
                    DisplayName(message.Entry),
                    message.Entry?.Element,
                    message.Entry?.Type,
                    _collectionCount,
                    message.Entry?.Id,
                    message.Entry?.Portrait);
                break;

            case "swapScan":
            {
                SetSwapDiscordPresence(message);
                break;
            }

            case "collectionCleared":
                _collectionCount = 0;
                _discordRpc.SetScanning(_collectionCount);
                break;

            case "stopped":
                _discordRpc.SetIdle(_collectionCount);
                break;
        }
    }

    private void SetSwapDiscordPresence(ScannerMessage message)
    {
        if (_discordRpc is null)
        {
            return;
        }

        CollectionEntry? swapTop = message.Entries?.FirstOrDefault(e =>
            string.Equals(e.SwapPart, "top", StringComparison.OrdinalIgnoreCase));
        CollectionEntry? swapBottom = message.Entries?.FirstOrDefault(e =>
            string.Equals(e.SwapPart, "bottom", StringComparison.OrdinalIgnoreCase));

        _discordRpc.SetSwapFigureOnPortal(
            FormatSwapCombinationName(message.Entries),
            swapTop?.Element ?? message.Entry?.Element,
            swapBottom?.Element ?? message.Entry?.Element,
            _collectionCount,
            SwapPortraitId(swapTop, swapBottom) ?? message.Entry?.Id);
    }

    // Returns the portrait key for one SWAP half.
    // In-game variants (dark, legendary, etc.) use their variant prefix (e.g. "dark-blast").
    // Chase variants don't have combination portraits, so they return just the half name (e.g. "blast").
    private static string? GetSwapPortraitKey(CollectionEntry? entry)
    {
        if (entry?.SwapPart is null || entry.Id is null) return null;
        string marker = $"-{entry.SwapPart}-";
        int idx = entry.Id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string halfName = entry.Id[(idx + marker.Length)..];

        // Chase variants fall back to the canonical combination portrait
        bool isChase = string.Equals(entry.Type, "Chase Variant", StringComparison.OrdinalIgnoreCase)
            || entry.Types?.Any(t => string.Equals(t, "Chase Variant", StringComparison.OrdinalIgnoreCase)) == true;
        if (isChase) return halfName;

        string[] segments = entry.Id[..idx].Split('-');
        string prefix = segments.Length > 2 ? string.Join("-", segments[..^2]) : "";
        return prefix.Length > 0 ? $"{prefix}-{halfName}" : halfName;
    }

    // Returns the figureId path for a swap combination portrait, e.g. "swap/blast-buckler".
    private static string? SwapPortraitId(CollectionEntry? top, CollectionEntry? bottom)
    {
        string? topKey = GetSwapPortraitKey(top);
        string? bottomKey = GetSwapPortraitKey(bottom);
        return topKey != null && bottomKey != null ? $"swap/{topKey}-{bottomKey}" : top?.Id;
    }

    private static string? GetSwapHalfName(CollectionEntry? entry)
    {
        if (entry?.SwapPart is null || entry.Id is null) return null;
        string marker = $"-{entry.SwapPart}-";
        int idx = entry.Id.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        string part = entry.Id[(idx + marker.Length)..];
        return part.Length > 0 ? char.ToUpper(part[0]) + part[1..] : null;
    }

    private static string FormatSwapCombinationName(IReadOnlyList<CollectionEntry>? entries)
    {
        if (entries is null || entries.Count == 0) return "Unknown SWAP Force";
        CollectionEntry? top = entries.FirstOrDefault(e =>
            string.Equals(e.SwapPart, "top", StringComparison.OrdinalIgnoreCase));
        CollectionEntry? bottom = entries.FirstOrDefault(e =>
            string.Equals(e.SwapPart, "bottom", StringComparison.OrdinalIgnoreCase));
        string? topHalf = GetSwapHalfName(top);
        string? bottomHalf = GetSwapHalfName(bottom);
        string? topNickname = DisplayNickname(top);
        string? bottomNickname = DisplayNickname(bottom);
        if (!string.IsNullOrWhiteSpace(topNickname) || !string.IsNullOrWhiteSpace(bottomNickname))
        {
            topHalf = topNickname ?? topHalf;
            bottomHalf = bottomNickname ?? bottomHalf;
            return string.Join(" ", new[] { topHalf, bottomHalf }.Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return topHalf != null && bottomHalf != null
            ? $"{topHalf} {bottomHalf}"
            : entries[0].Name ?? "Unknown SWAP Force";
    }

    private static string DisplayName(CollectionEntry? entry) =>
        DisplayNickname(entry) ?? entry?.Name ?? "Unknown Skylander";

    private static string? DisplayNickname(CollectionEntry? entry) =>
        string.IsNullOrWhiteSpace(entry?.Nickname)
            ? entry?.Stats?.Nickname
            : entry.Nickname;

    private void SendMessageToWeb(object message)
    {
        if (Browser.CoreWebView2 is null)
        {
            return;
        }

        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message, _jsonOptions));
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _scanner?.Stop();
        _discordTimer?.Stop();
        _discordRpc?.Dispose();
    }

    private static string FindContentRoot()
    {
        foreach (string candidate in GetContentRootCandidates())
        {
            if (File.Exists(Path.Combine(candidate, "app", "index.html")) &&
                File.Exists(Path.Combine(candidate, "data", "collection.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find the app and data folders beside the desktop app or in the project root.");
    }

    private static string FindEditableUpgradeCatalogPath(string contentRoot)
    {
        foreach (string candidate in GetContentRootCandidates())
        {
            string catalogPath = Path.Combine(candidate, "data", "catalog", "upgrades.json");
            string projectPath = Path.Combine(candidate, "desktop", "SkylandersCollection.Desktop", "SkylandersCollection.Desktop.csproj");
            if (File.Exists(catalogPath) && File.Exists(projectPath))
            {
                return catalogPath;
            }
        }

        return Path.Combine(contentRoot, "data", "catalog", "upgrades.json");
    }

    private static void WriteTextAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, text);
        File.Copy(tempPath, path, overwrite: true);
        File.Delete(tempPath);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetContentRootCandidates()
    {
        yield return Directory.GetCurrentDirectory();
        yield return AppContext.BaseDirectory;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }

        yield return Directory.GetCurrentDirectory();
    }
}
