using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkylandersCollection.Desktop;

internal sealed class CollectionStore
{
    private const bool SaveFigureStats = false;
    private const bool VillainsEnabled = false;

    private readonly string _catalogPath;
    private readonly string _collectionPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private Dictionary<int, CatalogEntry>? _figures;
    private Dictionary<string, CatalogEntry>? _variants;
    private Dictionary<string, List<CatalogEntry>>? _manualVariants;
    private Dictionary<string, CatalogEntry>? _entriesById;
    private Dictionary<int, VillainCatalogEntry>? _villains;

    private readonly string _villainsPath;

    public CollectionStore(string contentRoot)
    {
        _catalogPath = Path.Combine(contentRoot, "data", "catalog");
        _collectionPath = Path.Combine(contentRoot, "data", "collection.json");
        _villainsPath = Path.Combine(contentRoot, "data", "catalog", "villains.json");
    }

    public IReadOnlyList<CollectionScanResult> AddScans(
        IReadOnlyList<FigureScan> scans,
        IReadOnlyDictionary<string, string>? selectedCatalogIds = null)
    {
        EnsureCatalogLoaded();

        List<CollectionEntry> collection = ReadCollection();
        bool removedUidCollisions = RemoveConflictingUidEntries(collection) > 0;
        List<CollectionScanResult> changed = new List<CollectionScanResult>();
        List<(FigureScan Scan, ResolvedFigure Resolved)> resolvedScans = scans
            .Select(scan => (Scan: scan, Resolved: Resolve(scan)))
            .ToList();

        if (HasIncompleteSwapPair(resolvedScans))
        {
            FigureScan scan = resolvedScans.First(item => item.Resolved.IsSwapBottom || item.Resolved.IsSwapTop).Scan;
            changed.Add(new CollectionScanResult(
                null,
                false,
                scan,
                "incompleteSwap",
                "Incomplete SWAP Force figure detected. Add both halves to scan it."));

            return changed;
        }

        CollectionScanResult? uidPending = GetUidPendingResult(resolvedScans);
        if (uidPending is not null)
        {
            changed.Add(uidPending);
            return changed;
        }

        // Synthesize villain scans from traps that have a trapped villain, so each
        // villain gets its own collection entry (separate from the trap entry).
        foreach ((FigureScan scan, ResolvedFigure resolved) in resolvedScans.ToList())
        {
            if (VillainsEnabled && IsTrapType(resolved) && scan.TrapVillainId is > 0)
            {
                FigureScan villainScan = SynthesizeVillainScan(scan);
                resolvedScans.Add((villainScan, Resolve(villainScan)));
            }
        }

        foreach ((FigureScan scan, ResolvedFigure resolved) in resolvedScans)
        {
            if (!resolved.CharacterKnown || !resolved.VariantKnown)
            {
                changed.Add(new CollectionScanResult(null, false, scan));
                continue;
            }

            string scanKey = GetVariantKey(scan);
            if (GetVariantChoices(scan, resolved).Count > 1 &&
                selectedCatalogIds?.ContainsKey(scanKey) != true)
            {
                changed.Add(new CollectionScanResult(
                    null,
                    false,
                    scan,
                    "variantChoiceRequired",
                    $"Which {resolved.Name} did you scan?",
                    GetVariantChoices(scan, resolved)));

                return changed;
            }

            CatalogEntry? selectedCatalogEntry = TryGetSelectedCatalogEntry(scan, selectedCatalogIds);
            bool isPhysicalVariant = selectedCatalogEntry?.IsManualVariant == true;
            ResolvedFigure selectedResolved = selectedCatalogEntry is null
                ? resolved
                : Resolve(selectedCatalogEntry, resolved);
            bool canCreatePhysicalEntry = !RequiresUidForNewEntry(scan) ||
                !string.IsNullOrWhiteSpace(scan.FigureUid);

            CollectionEntry? uidCollision = collection.FirstOrDefault(entry =>
                !string.IsNullOrWhiteSpace(scan.FigureUid) &&
                string.Equals(entry.FigureUid, scan.FigureUid, StringComparison.OrdinalIgnoreCase) &&
                !EntryMatchesResolvedScan(entry, scan, selectedCatalogEntry));
            if (uidCollision is not null)
            {
                changed.Add(new CollectionScanResult(
                    null,
                    false,
                    scan,
                    "uidCollision",
                    $"Ignored unstable scan for {selectedResolved.Name}; UID already belongs to {uidCollision.Name}."));
                continue;
            }

            CollectionEntry? existing = collection.FirstOrDefault(entry =>
                isPhysicalVariant
                    ? entry.PhysicalVariantId == selectedCatalogEntry!.Id &&
                        UidMatchesOrCanBackfill(entry.FigureUid, scan.FigureUid)
                    : entry.ToyId == scan.ToyId &&
                        entry.VariantId == scan.VariantId &&
                        string.IsNullOrWhiteSpace(entry.PhysicalVariantId) &&
                        UidMatchesOrCanBackfill(entry.FigureUid, scan.FigureUid));
            bool isNew = existing is null;

            if (existing is null)
            {
                if (!canCreatePhysicalEntry)
                {
                    changed.Add(new CollectionScanResult(
                        null,
                        false,
                        scan,
                        "uidPending",
                        $"Read {resolved.Name}, but UID was not ready yet. Keep it on the portal and try again."));
                    continue;
                }

                existing = new CollectionEntry
                {
                    ToyId = scan.ToyId,
                    ToyIdHex = scan.ToyIdHex,
                    VariantId = scan.VariantId,
                    VariantIdHex = scan.VariantIdHex,
                    PhysicalVariantId = isPhysicalVariant ? selectedCatalogEntry?.Id : null,
                    FigureUid = scan.FigureUid,
                    FirstScannedAt = scan.ScannedAt,
                    ScanCount = 0
                };
                collection.Add(existing);
            }
            else
            {
                existing.FigureUid ??= scan.FigureUid;
            }

            existing.PhysicalVariantId = isPhysicalVariant ? selectedCatalogEntry?.Id : null;
            existing.Id = selectedResolved.Id;
            existing.Portrait = selectedResolved.Portrait;
            existing.Name = selectedResolved.Name;
            existing.BaseName = selectedResolved.BaseName;
            existing.Game = selectedResolved.Game;
            existing.Element = selectedResolved.Element;
            existing.Type = selectedResolved.Type;
            existing.Types = selectedResolved.Types;
            existing.SwapPart = selectedResolved.SwapPart;
            existing.EntrySource = isPhysicalVariant ? "scan-choice" : existing.EntrySource;
            existing.ScanNote = isPhysicalVariant ? "Selected after scan because this portal ID matches multiple physical variants." : existing.ScanNote;
            existing.VariantKnown = selectedResolved.VariantKnown;
            existing.CharacterKnown = selectedResolved.CharacterKnown;
            existing.LastScannedAt = scan.ScannedAt;
            existing.ScanCount += 1;
            if (scan.Blocks is not null && SupportsFigureNickname(selectedResolved))
            {
                FigureParsedStats? parsedStats = FigureStatsReader.TryRead(scan.Blocks);
                existing.Nickname = parsedStats?.Nickname;
                existing.Stats = SaveFigureStats && SupportsCharacterStats(selectedResolved) ? parsedStats : null;
            }
            else
            {
                existing.Nickname = null;
                existing.Stats = null;
            }
            if (VillainsEnabled && IsTrapType(selectedResolved) && scan.TrapVillainId.HasValue)
            {
                // Update trapped villain whenever we have a fresh block read. A read that
                // returns ID 0 means the trap is empty; keep the previous value on null
                // (block read timed out) so we don't erase a previously known villain.
                // isVariant: true when block 8 marks this as a pre-trapped villain pack and
                // the current villain matches the original pre-loaded villain → show variant name.
                bool isVariant = scan.TrapPreTrapped == true &&
                    scan.TrapVariantVillainId.HasValue &&
                    scan.TrapVariantVillainId.Value == scan.TrapVillainId.Value;
                existing.TrappedVillain = scan.TrapVillainId.Value == 0
                    ? null
                    : ResolveTrappedVillain(scan.TrapVillainId.Value, scan.TrapVillainEvolved == true, isVariant);
            }
            changed.Add(new CollectionScanResult(existing, isNew, scan));
        }

        if (removedUidCollisions || changed.Any(result => result.Entry is not null))
        {
            WriteCollection(collection);
        }

        return changed;
    }

    public void SetManualOwnership(ManualCollectionEntry entry, bool owned)
    {
        List<CollectionEntry> collection = ReadCollection();
        CollectionEntry? existing = collection.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(entry.PhysicalVariantId) &&
            item.PhysicalVariantId == entry.PhysicalVariantId);

        if (!owned)
        {
            if (existing is not null)
            {
                collection.Remove(existing);
                WriteCollection(collection);
            }

            return;
        }

        DateTimeOffset now = DateTimeOffset.Now;
        if (existing is null)
        {
            existing = new CollectionEntry
            {
                ToyId = entry.ToyId,
                ToyIdHex = entry.ToyIdHex,
                VariantId = entry.VariantId,
                VariantIdHex = entry.VariantIdHex,
                PhysicalVariantId = entry.PhysicalVariantId,
                FirstScannedAt = now,
                ScanCount = 0
            };
            collection.Add(existing);
        }

        existing.ToyId = entry.ToyId;
        existing.Id = entry.Id;
        existing.Portrait = entry.Portrait;
        existing.ToyIdHex = entry.ToyIdHex;
        existing.VariantId = entry.VariantId;
        existing.VariantIdHex = entry.VariantIdHex;
        existing.PhysicalVariantId = entry.PhysicalVariantId;
        existing.Name = entry.Name;
        existing.BaseName = entry.BaseName;
        existing.Game = entry.Game;
        existing.Element = entry.Element;
        existing.Type = entry.Type;
        existing.Types = entry.Types;
        existing.ScanNote = entry.ScanNote;
        existing.EntrySource = "manual";
        existing.CharacterKnown = true;
        existing.VariantKnown = true;
        existing.ManuallyMarkedAt = now;
        existing.LastScannedAt = now;

        WriteCollection(collection);
    }

    public bool RemoveCollectionEntry(CollectionEntryRemovalRequest request, out string? removedName)
    {
        List<CollectionEntry> collection = ReadCollection();
        CollectionEntry? existing = collection.FirstOrDefault(entry =>
            EntryMatchesRemovalRequest(entry, request));

        if (existing is null)
        {
            removedName = null;
            return false;
        }

        removedName = existing.Name;
        collection.Remove(existing);
        WriteCollection(collection);
        return true;
    }

    public int GetCollectionCount() => ReadCollection().Count;

    public string? GetFigureName(int toyId, int variantId)
    {
        EnsureCatalogLoaded();
        if (_variants?.TryGetValue(GetVariantKey(toyId, variantId), out CatalogEntry? variant) == true)
            return variant.Name;
        if (_figures?.TryGetValue(toyId, out CatalogEntry? figure) == true)
            return figure.Name;
        return null;
    }

    public bool IsKnownVariant(int toyId, int variantId)
    {
        EnsureCatalogLoaded();
        return _variants?.ContainsKey(GetVariantKey(toyId, variantId)) == true;
    }

    private List<CollectionEntry> ReadCollection()
    {
        if (!File.Exists(_collectionPath))
        {
            return new List<CollectionEntry>();
        }

        string json = File.ReadAllText(_collectionPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<CollectionEntry>();
        }

        return JsonSerializer.Deserialize<List<CollectionEntry>>(json, _jsonOptions) ?? new List<CollectionEntry>();
    }

    private void WriteCollection(List<CollectionEntry> collection)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_collectionPath)!);
        File.WriteAllText(_collectionPath, JsonSerializer.Serialize(collection, _jsonOptions));
    }

    private ResolvedFigure Resolve(FigureScan scan)
    {
        EnsureCatalogLoaded();

        _figures!.TryGetValue(scan.ToyId, out CatalogEntry? figure);
        _variants!.TryGetValue($"{scan.ToyId}:{scan.VariantId}", out CatalogEntry? variant);

        return new ResolvedFigure
        {
            Id = variant?.Id ?? figure?.Id,
            Portrait = variant?.Portrait ?? figure?.Portrait,
            Name = variant?.Name ?? figure?.Name ?? "Unknown Skylander",
            BaseName = variant?.Character ?? figure?.Character ?? figure?.CompanionCharacter ?? figure?.Name,
            Game = variant?.Game ?? figure?.Game,
            Element = variant?.Element ?? figure?.Element,
            Type = variant?.Type ?? figure?.Type,
            Types = MergeTypes(variant, figure),
            SwapPart = variant?.SwapPart ?? figure?.SwapPart,
            VariantKnown = variant is not null,
            CharacterKnown = figure is not null || variant is not null
        };
    }

    private ResolvedFigure Resolve(CatalogEntry entry, ResolvedFigure fallback)
    {
        CatalogEntry? figure = null;
        _figures?.TryGetValue(entry.ToyId, out figure);

        return new ResolvedFigure
        {
            Id = entry.Id ?? fallback.Id,
            Portrait = entry.Portrait ?? fallback.Portrait,
            Name = entry.Name ?? fallback.Name,
            BaseName = entry.Character ?? entry.CompanionCharacter ?? fallback.BaseName,
            Game = entry.Game ?? fallback.Game,
            Element = entry.Element ?? fallback.Element,
            Type = entry.Type ?? fallback.Type,
            Types = MergeTypes(entry, figure),
            SwapPart = entry.SwapPart ?? fallback.SwapPart,
            VariantKnown = true,
            CharacterKnown = true
        };
    }

    private CatalogEntry? TryGetSelectedCatalogEntry(
        FigureScan scan,
        IReadOnlyDictionary<string, string>? selectedCatalogIds)
    {
        if (selectedCatalogIds is null ||
            !selectedCatalogIds.TryGetValue(GetVariantKey(scan), out string? selectedId) ||
            string.IsNullOrWhiteSpace(selectedId) ||
            _entriesById?.TryGetValue(selectedId, out CatalogEntry? entry) != true)
        {
            return null;
        }

        return entry;
    }

    private IReadOnlyList<CollectionEntry> GetVariantChoices(FigureScan scan, ResolvedFigure standard)
    {
        if (_manualVariants?.TryGetValue(GetVariantKey(scan), out List<CatalogEntry>? manualVariants) != true ||
            manualVariants is null ||
            manualVariants.Count == 0)
        {
            return Array.Empty<CollectionEntry>();
        }

        List<CollectionEntry> choices =
        [
            CreateChoiceEntry(scan, standard)
        ];

        choices.AddRange(manualVariants.Select(entry =>
            CreateChoiceEntry(scan, Resolve(entry, standard), entry.Id)));

        return choices;
    }

    private static CollectionEntry CreateChoiceEntry(
        FigureScan scan,
        ResolvedFigure resolved,
        string? physicalVariantId = null)
    {
        return new CollectionEntry
        {
            Id = resolved.Id,
            Portrait = resolved.Portrait,
            ToyId = scan.ToyId,
            ToyIdHex = scan.ToyIdHex,
            VariantId = scan.VariantId,
            VariantIdHex = scan.VariantIdHex,
            PhysicalVariantId = physicalVariantId,
            Name = resolved.Name,
            BaseName = resolved.BaseName,
            Game = resolved.Game,
            Element = resolved.Element,
            Type = resolved.Type,
            Types = resolved.Types,
            SwapPart = resolved.SwapPart,
            EntrySource = physicalVariantId is null ? "scan" : "scan-choice",
            ScanNote = physicalVariantId is null
                ? "Standard catalog entry for this portal ID."
                : "Physical chase variant that shares this portal ID.",
            CharacterKnown = true,
            VariantKnown = true,
            FirstScannedAt = scan.ScannedAt,
            LastScannedAt = scan.ScannedAt,
            ScanCount = 1
        };
    }

    private static bool HasIncompleteSwapPair(IEnumerable<(FigureScan Scan, ResolvedFigure Resolved)> resolvedScans)
    {
        bool hasBottom = false;
        bool hasTop = false;

        foreach ((_, ResolvedFigure resolved) in resolvedScans)
        {
            hasBottom = hasBottom || resolved.IsSwapBottom;
            hasTop = hasTop || resolved.IsSwapTop;
        }

        return hasBottom != hasTop;
    }

    private CollectionScanResult? GetUidPendingResult(
        IEnumerable<(FigureScan Scan, ResolvedFigure Resolved)> resolvedScans)
    {
        List<CollectionEntry> collection = ReadCollection();

        foreach ((FigureScan scan, ResolvedFigure resolved) in resolvedScans)
        {
            if (!resolved.CharacterKnown ||
                !resolved.VariantKnown ||
                !RequiresUidForNewEntry(scan) ||
                !string.IsNullOrWhiteSpace(scan.FigureUid))
            {
                continue;
            }

            // A missing UID only blocks the save when this is a genuinely NEW figure
            // (not already in the collection). If an existing entry matches by toyId/variantId,
            // the UID will be backfilled and the scan should proceed.
            bool alreadyInCollection = collection.Any(entry =>
                entry.ToyId == scan.ToyId &&
                entry.VariantId == scan.VariantId &&
                string.IsNullOrWhiteSpace(entry.PhysicalVariantId));

            if (alreadyInCollection)
            {
                continue;
            }

            return new CollectionScanResult(
                null,
                false,
                scan,
                "uidPending",
                $"Read {resolved.Name}, but UID was not ready yet. Keep it on the portal and try again.");
        }

        return null;
    }

    private static string[]? MergeTypes(CatalogEntry? variant, CatalogEntry? figure)
    {
        IEnumerable<string?> values = (variant?.Types ?? Array.Empty<string>())
            .Concat(figure?.Types ?? Array.Empty<string>())
            .Concat([variant?.Type, figure?.Type]);

        string[] types = values
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Select(type => type!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return types.Length > 0 ? types : null;
    }

    private void EnsureCatalogLoaded()
    {
        if (_figures is not null && _variants is not null && _manualVariants is not null && _entriesById is not null)
        {
            return;
        }

        _figures = new Dictionary<int, CatalogEntry>();
        _variants = new Dictionary<string, CatalogEntry>();
        _manualVariants = new Dictionary<string, List<CatalogEntry>>();
        _entriesById = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(_catalogPath))
        {
            return;
        }

        List<CatalogEntry> entries = [];

        foreach (string file in Directory.EnumerateFiles(_catalogPath, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("villains.json", StringComparison.OrdinalIgnoreCase))
                continue;

            string json = File.ReadAllText(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                continue;
            }

            CatalogEntry[] fileEntries = JsonSerializer.Deserialize<CatalogEntry[]>(json, _jsonOptions)
                ?? Array.Empty<CatalogEntry>();

            entries.AddRange(fileEntries);
        }

        HashSet<string> standardScanKeys = entries
            .Where(entry => !entry.IsManualVariant)
            .Select(entry => GetVariantKey(entry.ToyId, entry.VariantId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (CatalogEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Id))
            {
                _entriesById[entry.Id] = entry;
            }

            string key = GetVariantKey(entry.ToyId, entry.VariantId);
            if (entry.IsManualVariant && standardScanKeys.Contains(key))
            {
                if (!_manualVariants.TryGetValue(key, out List<CatalogEntry>? variants))
                {
                    variants = [];
                    _manualVariants[key] = variants;
                }

                variants.Add(entry);
                continue;
            }

            if (!_figures.ContainsKey(entry.ToyId) || string.IsNullOrWhiteSpace(entry.VariantOf))
            {
                _figures[entry.ToyId] = entry;
            }

            _variants[key] = entry;
        }

        LoadVillainCatalog();
    }

    private void LoadVillainCatalog()
    {
        _villains = new Dictionary<int, VillainCatalogEntry>();
        if (!File.Exists(_villainsPath))
        {
            return;
        }

        string json = File.ReadAllText(_villainsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        VillainCatalogEntry[] entries = JsonSerializer.Deserialize<VillainCatalogEntry[]>(json, _jsonOptions)
            ?? Array.Empty<VillainCatalogEntry>();

        foreach (VillainCatalogEntry entry in entries)
        {
            int? villainId = entry.VariantId ?? TryParseVillainId(entry.Id);
            if (villainId.HasValue)
            {
                _villains[villainId.Value] = entry;
            }
        }
    }

    private static int? TryParseVillainId(string? value)
    {
        return int.TryParse(value, out int id) ? id : null;
    }

    public bool IsTrapScan(FigureScan scan)
    {
        EnsureCatalogLoaded();
        return IsTrapType(Resolve(scan));
    }

    private static bool IsTrapType(ResolvedFigure resolved) =>
        string.Equals(resolved.Type, "Trap", StringComparison.OrdinalIgnoreCase);

    private TrappedVillainInfo? ResolveTrappedVillain(int villainId, bool evolved, bool isVariant)
    {
        int lookupId = NormalizeVillainId(villainId, evolved);
        if (_villains!.TryGetValue(lookupId, out VillainCatalogEntry? entry))
        {
            return new TrappedVillainInfo
            {
                Id = lookupId,
                Name = isVariant && entry.VariantName is not null ? entry.VariantName : entry.Name,
                Element = entry.Element,
                Evolved = evolved,
            };
        }

        return new TrappedVillainInfo { Id = villainId, Evolved = evolved };
    }

    // Synthetic toyId used for all villain catalog entries and villain collection entries.
    // Real figures never produce toyId 0 from NFC, so there is no collision risk.
    public const int VillainToyId = 0;
    private const int MaxBaseVillainId = 46;
    // Evolved villains use a different ID on-chip: base villain ID + 65.
    private const int EvolvedVillainIdOffset = 65;

    // Maps evolved villain IDs (66–111) back to their base catalog IDs (1–46).
    private static int NormalizeVillainId(int rawId, bool evolved) =>
        evolved && rawId > MaxBaseVillainId ? rawId - EvolvedVillainIdOffset : rawId;

    private static FigureScan SynthesizeVillainScan(FigureScan trapScan)
    {
        int baseId = NormalizeVillainId(trapScan.TrapVillainId!.Value, trapScan.TrapVillainEvolved == true);
        return new FigureScan
        {
            ToyIndex = trapScan.ToyIndex,
            ToyIndexHex = trapScan.ToyIndexHex,
            ToyId = VillainToyId,
            ToyIdHex = "0x0000",
            VariantId = baseId,
            VariantIdHex = $"0x{baseId:X4}",
            ScannedAt = trapScan.ScannedAt,
            IsVillainScan = true,
        };
    }

    private static string GetVariantKey(FigureScan scan) => GetVariantKey(scan.ToyId, scan.VariantId);

    private static string GetVariantKey(int toyId, int variantId) => $"{toyId}:{variantId}";

    private static bool UidMatchesOrCanBackfill(string? entryUid, string? scanUid)
    {
        if (string.IsNullOrWhiteSpace(scanUid))
        {
            return string.IsNullOrWhiteSpace(entryUid);
        }

        return string.IsNullOrWhiteSpace(entryUid) ||
            string.Equals(entryUid, scanUid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresUidForNewEntry(FigureScan scan) =>
        !scan.IsInjectedTestScan && !scan.IsVillainScan;

    private static bool EntryMatchesRemovalRequest(CollectionEntry entry, CollectionEntryRemovalRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.PhysicalVariantId))
        {
            if (!string.Equals(entry.PhysicalVariantId, request.PhysicalVariantId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.PhysicalVariantId))
        {
            return false;
        }

        if (entry.ToyId != request.ToyId || entry.VariantId != request.VariantId)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(request.FigureUid)
            ? string.IsNullOrWhiteSpace(entry.FigureUid)
            : string.Equals(entry.FigureUid, request.FigureUid, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EntryMatchesResolvedScan(CollectionEntry entry, FigureScan scan, CatalogEntry? selectedCatalogEntry)
    {
        if (selectedCatalogEntry?.IsManualVariant == true)
        {
            return string.Equals(entry.PhysicalVariantId, selectedCatalogEntry.Id, StringComparison.OrdinalIgnoreCase);
        }

        return entry.ToyId == scan.ToyId &&
            entry.VariantId == scan.VariantId &&
            string.IsNullOrWhiteSpace(entry.PhysicalVariantId);
    }

    private static string GetPhysicalGroupKey(CollectionEntry entry) =>
        string.IsNullOrWhiteSpace(entry.PhysicalVariantId)
            ? $"scan:{GetVariantKey(entry.ToyId, entry.VariantId)}"
            : $"physical:{entry.PhysicalVariantId}";

    public void ClearCollection()
    {
        WriteCollection(new List<CollectionEntry>());
    }

    public int RemoveUidlessDuplicates()
    {
        List<CollectionEntry> collection = ReadCollection();
        HashSet<string> keysWithUid = collection
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FigureUid))
            .Select(GetPhysicalGroupKey)
            .ToHashSet(StringComparer.Ordinal);

        int before = collection.Count;
        collection = collection
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FigureUid) ||
                !keysWithUid.Contains(GetPhysicalGroupKey(entry)))
            .ToList();

        int removed = before - collection.Count;
        if (removed > 0)
        {
            WriteCollection(collection);
        }

        return removed;
    }

    private static int RemoveConflictingUidEntries(List<CollectionEntry> collection)
    {
        int removed = 0;
        foreach (IGrouping<string, CollectionEntry> group in collection
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FigureUid))
            .GroupBy(entry => entry.FigureUid!, StringComparer.OrdinalIgnoreCase)
            .ToArray())
        {
            CollectionEntry[] entries = group
                .OrderBy(entry => entry.FirstScannedAt)
                .ToArray();
            if (entries
                .Select(GetPhysicalGroupKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() <= 1)
            {
                continue;
            }

            foreach (CollectionEntry entry in entries.Skip(1))
            {
                collection.Remove(entry);
                removed++;
            }
        }

        return removed;
    }

    public void AppendSwapDebug(string text)
    {
        try
        {
            string dataFolder = Path.GetDirectoryName(_collectionPath)!;
            Directory.CreateDirectory(dataFolder);
            string logPath = Path.Combine(dataFolder, "swap-scan-debug.log");
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostic logging must never interrupt scanning.
        }
    }

    public bool SupportsFigureData(FigureScan scan)
    {
        EnsureCatalogLoaded();
        return SupportsFigureNickname(Resolve(scan));
    }

    public string SaveFigureDump(IReadOnlyList<FigureDiagnosticDump> figures)
    {
        EnsureCatalogLoaded();

        string dataFolder = Path.GetDirectoryName(_collectionPath)!;
        string dumpFolder = Path.Combine(dataFolder, "debug", "figure-dumps");
        Directory.CreateDirectory(dumpFolder);

        FigureDiagnosticDump first = figures.FirstOrDefault() ?? new FigureDiagnosticDump();
        string namePart = figures.Count == 1
            ? SanitizeFilePart(Resolve(first).Name)
            : "multi-figure";
        string uidPart = figures.Count == 1 && !string.IsNullOrWhiteSpace(first.FigureUid)
            ? SanitizeFilePart(first.FigureUid)
            : "no-uid";
        string timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(dumpFolder, $"{timestamp}-{namePart}-{uidPart}.json");

        object payload = new
        {
            generatedAt = DateTimeOffset.Now,
            note = "Raw encrypted NFC block data read by Portal Master Vault. No data was written to the figure.",
            figures = figures.Select(figure =>
            {
                ResolvedFigure resolved = Resolve(figure);
                FigureParsedStats? parsedStats = SaveFigureStats && SupportsFigureNickname(resolved) && figure.Blocks != null
                    ? FigureStatsReader.TryRead(figure.Blocks)
                    : null;
                return new
                {
                    figure.ToyIndex,
                    figure.ToyIndexHex,
                    figure.ToyId,
                    figure.ToyIdHex,
                    figure.VariantId,
                    figure.VariantIdHex,
                    figure.FigureUid,
                    resolved.Id,
                    resolved.Name,
                    resolved.Game,
                    resolved.Element,
                    resolved.Type,
                    resolved.Types,
                    resolved.SwapPart,
                    figure.Block0,
                    figure.Block1,
                    parsedStats,
                    figure.Blocks
                };
            }).ToArray()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, _jsonOptions));
        return path;
    }

    private static bool SupportsCharacterStats(ResolvedFigure resolved)
    {
        if (resolved.IsSwapBottom)
        {
            return false;
        }

        if (resolved.IsSwapTop)
        {
            return true;
        }

        string[] types = [resolved.Type ?? "", .. resolved.Types ?? Array.Empty<string>()];
        return types.Any(type =>
            string.Equals(type, "Core", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Giant", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "LightCore", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Sidekick", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Trap Master", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "Mini", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SupportsFigureNickname(ResolvedFigure resolved) =>
        resolved.IsSwapBottom || SupportsCharacterStats(resolved);

    private ResolvedFigure Resolve(FigureDiagnosticDump dump)
    {
        EnsureCatalogLoaded();

        _figures!.TryGetValue(dump.ToyId, out CatalogEntry? figure);
        _variants!.TryGetValue($"{dump.ToyId}:{dump.VariantId}", out CatalogEntry? variant);

        return new ResolvedFigure
        {
            Id = variant?.Id ?? figure?.Id,
            Portrait = variant?.Portrait ?? figure?.Portrait,
            Name = variant?.Name ?? figure?.Name ?? "Unknown Skylander",
            BaseName = variant?.Character ?? figure?.Character ?? figure?.CompanionCharacter ?? figure?.Name,
            Game = variant?.Game ?? figure?.Game,
            Element = variant?.Element ?? figure?.Element,
            Type = variant?.Type ?? figure?.Type,
            Types = MergeTypes(variant, figure),
            SwapPart = variant?.SwapPart ?? figure?.SwapPart,
            VariantKnown = variant is not null,
            CharacterKnown = figure is not null || variant is not null
        };
    }

    private static string SanitizeFilePart(string? value)
    {
        string text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            text = text.Replace(invalid, '-');
        }

        return string.Join(
            "-",
            text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToLowerInvariant();
    }
}

internal sealed class FigureScan
{
    public required int ToyIndex { get; init; }
    public required string ToyIndexHex { get; init; }
    public required int ToyId { get; init; }
    public required string ToyIdHex { get; init; }
    public required int VariantId { get; init; }
    public required string VariantIdHex { get; init; }
    public string? Block0 { get; set; }
    public string? Block1 { get; init; }
    public required DateTimeOffset ScannedAt { get; init; }
    public string? FigureUid { get; set; }
    public bool IsInjectedTestScan { get; init; }
    public IReadOnlyList<FigureBlockDump>? Blocks { get; set; }
    public int? TrapVillainId { get; set; }
    public bool? TrapVillainEvolved { get; set; }
    public bool? TrapPreTrapped { get; set; }
    public int? TrapVariantVillainId { get; set; }
    public bool IsVillainScan { get; init; }
}

internal sealed record CollectionScanResult(
    CollectionEntry? Entry,
    bool IsNew,
    FigureScan Scan,
    string? Status = null,
    string? Message = null,
    IReadOnlyList<CollectionEntry>? Choices = null);

internal sealed class CollectionEntry
{
    public string? Id { get; set; }
    public string? Portrait { get; set; }
    public int ToyId { get; set; }
    public string? ToyIdHex { get; set; }
    public int VariantId { get; set; }
    public string? VariantIdHex { get; set; }
    public string? PhysicalVariantId { get; set; }
    public string? Name { get; set; }
    public string? BaseName { get; set; }
    public string? Game { get; set; }
    public string? Element { get; set; }
    public string? Type { get; set; }
    public string[]? Types { get; set; }
    public string? SwapPart { get; set; }
    public string? FigureUid { get; set; }
    public string? Nickname { get; set; }
    public string? EntrySource { get; set; }
    public string? ScanNote { get; set; }
    public bool VariantKnown { get; set; }
    public bool CharacterKnown { get; set; }
    public DateTimeOffset? ManuallyMarkedAt { get; set; }
    public DateTimeOffset FirstScannedAt { get; set; }
    public DateTimeOffset LastScannedAt { get; set; }
    public int ScanCount { get; set; }
    public FigureParsedStats? Stats { get; set; }
    public TrappedVillainInfo? TrappedVillain { get; set; }
}

internal sealed class TrappedVillainInfo
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public string? Element { get; set; }
    public bool Evolved { get; set; }
}

internal sealed class VillainCatalogEntry
{
    public string? Id { get; set; }
    public int? VariantId { get; set; }
    public string? Name { get; set; }
    public string? Element { get; set; }
    public string? Game { get; set; }
    public string? VariantName { get; set; }
}

internal sealed class CatalogEntry
{
    public string? Id { get; set; }
    public string? Portrait { get; set; }
    public string? VariantOf { get; set; }
    public int ToyId { get; set; }
    public string? ToyIdHex { get; set; }
    public int VariantId { get; set; }
    public string? VariantIdHex { get; set; }
    public string? Name { get; set; }
    public string? Character { get; set; }
    public string? CompanionCharacter { get; set; }
    public string? Element { get; set; }
    public string? Type { get; set; }
    public string[]? Types { get; set; }
    public string? SwapPart { get; set; }
    public string? Game { get; set; }

    [JsonIgnore]
    public bool IsManualVariant =>
        string.Equals(Type, "Chase Variant", StringComparison.OrdinalIgnoreCase) ||
        Types?.Any(type => string.Equals(type, "Chase Variant", StringComparison.OrdinalIgnoreCase)) == true;
}

internal sealed class ManualCollectionEntry
{
    public string? Id { get; set; }
    public string? Portrait { get; set; }
    public int ToyId { get; set; }
    public string? ToyIdHex { get; set; }
    public int VariantId { get; set; }
    public string? VariantIdHex { get; set; }
    public string? PhysicalVariantId { get; set; }
    public string? Name { get; set; }
    public string? BaseName { get; set; }
    public string? Game { get; set; }
    public string? Element { get; set; }
    public string? Type { get; set; }
    public string[]? Types { get; set; }
    public string? SwapPart { get; set; }
    public string? ScanNote { get; set; }
}

internal sealed class CollectionEntryRemovalRequest
{
    public int ToyId { get; set; }
    public int VariantId { get; set; }
    public string? PhysicalVariantId { get; set; }
    public string? FigureUid { get; set; }
}

internal sealed class ResolvedFigure
{
    public string? Id { get; init; }
    public string? Portrait { get; init; }
    public required string Name { get; init; }
    public string? BaseName { get; init; }
    public string? Game { get; init; }
    public string? Element { get; init; }
    public string? Type { get; init; }
    public string[]? Types { get; init; }
    public string? SwapPart { get; init; }
    public bool VariantKnown { get; init; }
    public bool CharacterKnown { get; init; }

    [JsonIgnore]
    public bool IsSwapBottom => string.Equals(SwapPart, "bottom", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSwapTop => string.Equals(SwapPart, "top", StringComparison.OrdinalIgnoreCase);
}
