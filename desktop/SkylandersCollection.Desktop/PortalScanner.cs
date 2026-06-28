using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SkylandersCollection.Desktop;

internal sealed class PortalScanner
{
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
	private const int PortalVendorId = 5168;

	private const int SpyroPortalProductId = 7959;

	private const int WiiUHidPortalProductId = 336;

	private const int InventoryPollMs = 300;

	private const int RemovalGraceMs = 1200;

	private const int RemovalEmptyPollsRequired = 2;

	private const int TraptaniumRemovalGraceMs = 2200;

	private const int TraptaniumRemovalEmptyPollsRequired = 2;

	private const int TraptaniumDuplicateSuppressMs = 45000;

	private const int ReconnectDelayMs = 1200;

	private const int TraptaniumLastSlot = 15;

	private const int TraptaniumFinalWindowMs = 240;

	private const int IdentityReadTimeoutMs = 50;

	private const int TraptaniumIdentityReadTimeoutMs = 70;

	private const int BlockReadTimeoutMs = 60;

	private const int TraptaniumBlockReadTimeoutMs = 80;

	private static readonly int[] StatsBlockIndexes = new int[16]
	{
		0, 1, 8, 9, 10, 12, 13, 16, 17, 36,
		37, 38, 40, 41, 44, 45
	};

	private readonly CollectionStore _collectionStore;

	private CancellationTokenSource? _scanCancellation;

	private Task? _scanTask;

	private string? _lastSwapDebugText;

	private string? _lastStatusDebugText;

	private string? _lastSavedPortalScanKey;

	private DateTime? _lastSavedPortalScanAt;

	private int? _lastSavedPortalProductId;

	private int _dumpNextFigureRequested;

	private int _rescanCurrentPresenceRequested;

	private readonly Dictionary<string, PendingVariantChoice> _pendingVariantChoices = new Dictionary<string, PendingVariantChoice>();

	private readonly Dictionary<string, Dictionary<string, string>> _resolvedVariantChoicesByPresence = new Dictionary<string, Dictionary<string, string>>();

	private readonly object _variantChoiceLock = new object();

	private bool _loggedFirstQueryAttempt;

	public bool IsRunning
	{
		get
		{
			Task? scanTask = _scanTask;
			if (scanTask != null)
			{
				return !scanTask.IsCompleted;
			}
			return false;
		}
	}

	public event EventHandler<ScannerMessage>? Message;

	public PortalScanner(CollectionStore collectionStore)
	{
		_collectionStore = collectionStore;
	}

	public void CheckPortal()
	{
		bool flag = IsPortalConnected();
		Publish(flag ? "portalReady" : "portalMissing", flag ? "Portal connected. Scanner ready." : "No supported portal found. Plug in a supported portal, then try scanning again.");
	}

	public void Start()
	{
		if (IsRunning)
		{
			Publish("status", "Scanner is already running.");
			return;
		}
		if (!IsPortalConnected())
		{
			Publish("portalMissing", "No supported portal found. Plug in a supported portal, then try scanning again.");
			return;
		}
		_scanCancellation = new CancellationTokenSource();
		_scanTask = Task.Run(delegate
		{
			ScanLoop(_scanCancellation.Token);
		});
	}

	public void Stop()
	{
		_scanCancellation?.Cancel();
		Publish("status", "Stopping scanner...");
	}

	public void RequestFigureDump()
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		Interlocked.Exchange(ref _dumpNextFigureRequested, 1);
		Publish("figureDumpArmed", "Figure data dump armed. Place a figure on the portal.");
	}

	public void ResetCollection()
	{
		try
		{
			_collectionStore.ClearCollection();
			Interlocked.Exchange(ref _rescanCurrentPresenceRequested, 1);
			Publish("collectionCleared", "Collection cleared.");
		}
		catch (Exception ex)
		{
			Publish("error", "Failed to clear collection: " + ex.Message);
		}
	}

	public void ResolveVariantChoice(string choiceToken, string selectedCatalogId)
	{
		PendingVariantChoice? value;
		lock (_variantChoiceLock)
		{
			_pendingVariantChoices.TryGetValue(choiceToken, out value);
		}
		if (value is null)
		{
			Publish("error", "That scan choice has expired. Scan the figure again.");
			return;
		}
		if (string.IsNullOrWhiteSpace(selectedCatalogId) || string.IsNullOrWhiteSpace(value.CurrentScanKey))
		{
			Publish("error", "No scan choice was selected.");
			return;
		}
		value.SelectedCatalogIds[value.CurrentScanKey] = selectedCatalogId;
		lock (_variantChoiceLock)
		{
			_resolvedVariantChoicesByPresence[value.PresenceSetKey] = new Dictionary<string, string>(value.SelectedCatalogIds);
		}
		SaveScans(value.Scans, value.SelectedCatalogIds, choiceToken);
	}

	public void CancelVariantChoice(string choiceToken)
	{
		bool removed;
		lock (_variantChoiceLock)
		{
			removed = _pendingVariantChoices.Remove(choiceToken);
		}
		if (removed)
		{
			Publish("status", "Scan choice cancelled. Remove and rescan the figure to choose again.");
		}
	}

	public void InjectScans(IReadOnlyList<FigureScan> scans)
	{
		if (scans.Count == 0)
		{
			Publish("error", "No injected scan data was provided.");
		}
		else
		{
			SaveScans(scans);
		}
	}

	private void ScanLoop(CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					Publish("status", "Looking for portal...");
					using IPortalConnection portalConnection = OpenPortal();
					LogPortal("sending StartPortal commands (0x52, 0x41 01, 0x43 00 60 FF)");
					StartPortal(portalConnection);
					LogPortal("StartPortal complete; entering scan loop");
					Publish("ready", portalConnection.Description + " ready. Place one Skylander at a time.");
					ScanConnectedPortal(portalConnection, cancellationToken);
				}
				catch (InvalidOperationException ex) when (ex.Message.StartsWith("No supported portal found", StringComparison.Ordinal))
				{
					Publish("portalMissing", ex.Message);
					return;
				}
				catch (Exception ex2) when (IsRecoverablePortalException(ex2) && !cancellationToken.IsCancellationRequested)
				{
					LogPortal("recoverable portal exception: " + ex2.GetType().Name + ": " + ex2.Message);
					Publish("reconnecting", "Portal connection stalled. Reconnecting... (" + ex2.Message + ")");
					cancellationToken.WaitHandle.WaitOne(1200);
				}
				catch (Exception ex3)
				{
					Publish("error", ex3.Message);
					return;
				}
			}
			Publish("stopped", "Scanner stopped.");
		}
		catch (OperationCanceledException)
		{
			Publish("stopped", "Scanner stopped.");
		}
	}

	private void ScanConnectedPortal(IPortalConnection portal, CancellationToken cancellationToken)
	{
		bool isTraptaniumPortal = IsTraptaniumPortal(portal);
		bool flag = false;
		bool flag2 = false;
		string? text = null;
		string text2 = string.Empty;
		Dictionary<string, FigureScan> dictionary = new Dictionary<string, FigureScan>();
		Dictionary<string, FigureScan> dictionary2 = new Dictionary<string, FigureScan>();
		// Scan-identity keys already revealed this presence session. Used so that adding a
		// new figure while others are still on the portal only re-reveals the new one,
		// instead of re-announcing (and overwriting the reveal with) figures already there.
		HashSet<string> revealedScanKeys = new HashSet<string>();
		DateTime dateTime = DateTime.UtcNow;
		DateTime? lastScanSeenAt = null;
		int num = 0;
		int num2 = 0;
		int swapTransitionPollCount = 0;
		string? lastTooManyFiguresKey = null;
		while (!cancellationToken.IsCancellationRequested)
		{
			bool num3 = DateTime.UtcNow >= dateTime;
			byte[] array = portal.Read();
			if (array.Length != 0)
			{
				string statusSignature = GetStatusSignature(array);
				if (statusSignature != text2)
				{
					text2 = statusSignature;
					LogStatusPacket(array);
				}
			}
			if (!num3)
			{
				continue;
			}
			dateTime = DateTime.UtcNow.AddMilliseconds(300.0);
			IReadOnlyList<FigureScan> readOnlyList = ReadFigureIdentityBlockSet(portal, cancellationToken);
			num++;
			int emptyPollsBeforeRead = num2;
			if (readOnlyList.Count == 0)
			{
				num2++;
				if (num2 == 1 || num2 == 5 || num2 == 20 || num2 % 60 == 0)
				{
					LogPortal($"inventory poll #{num} returned 0 scans (empty streak {num2})");
				}
				if (num2 == 1)
				{
					PublishPortalContents(readOnlyList);
				}
			}
			else
			{
				LogPortal($"inventory poll #{num} returned {readOnlyList.Count} scan(s) after {num2} empty");
				num2 = 0;
			}
			if (readOnlyList.Count > 0)
			{
				bool rescanCurrentPresence = Interlocked.Exchange(ref _rescanCurrentPresenceRequested, 0) != 0;
				if (rescanCurrentPresence || IsPlacementGapConfirmed(lastScanSeenAt, emptyPollsBeforeRead, isTraptaniumPortal))
				{
					LogPortal(rescanCurrentPresence
						? "collection changed while a figure was present; re-arming scan session"
						: $"placement gap detected after {emptyPollsBeforeRead} empty inventory polls; re-arming scan session");
					flag = false;
					flag2 = false;
					text = null;
					dictionary.Clear();
					dictionary2.Clear();
					revealedScanKeys.Clear();
					ClearVariantChoiceSession();
					swapTransitionPollCount = 0;
					lastTooManyFiguresKey = null;
				}
				readOnlyList = GatherSwapScans(portal, readOnlyList, cancellationToken);
				readOnlyList = FilterUnstableIdentityReads(readOnlyList, dictionary.Values);
				LogSwapScan("read", readOnlyList);
				lastScanSeenAt = DateTime.UtcNow;
				// If the raw poll already has more than one swap top or bottom, two different
				// swap-force figures are on the portal simultaneously. Catch this before
				// UpdateAccumulatedScans collapses both tops/bottoms into one pair (which would
				// make IsValidSingleFigureSet pass and silently scan the wrong figure).
				{
					int rawTops = readOnlyList.Count(IsSwapTopScan);
					int rawBottoms = readOnlyList.Count(IsSwapBottomScan);
					if (rawTops > 1 || rawBottoms > 1)
					{
						FigureScan[] rawArray = readOnlyList.ToArray();
						string rawPresenceKey = GetPresenceSetKey(rawArray);
						if (rawPresenceKey != lastTooManyFiguresKey)
						{
							lastTooManyFiguresKey = rawPresenceKey;
							Publish("tooManyFigures", "Place only one figure at a time (or one SWAP Force pair).");
						}
						PublishPortalContents(rawArray);
						flag = true;
						text = rawPresenceKey;
						continue;
					}
				}
				UpdateAccumulatedScans(dictionary, readOnlyList);
				MergePendingSwapBottoms(dictionary, dictionary2);
				FigureScan[] array2 = dictionary.Values.ToArray();
				string presenceSetKey = GetPresenceSetKey(array2);
				if (IsIncompleteSwapScanSet(array2))
				{
					RememberPendingSwapBottoms(array2, dictionary2);
					string phase = ((array2.Any(IsSwapTopScan) && dictionary2.Count == 0) ? "top only - scan bottom half separately" : "waiting for other half");
					LogSwapScan(phase, array2);
					flag = true;
					text = presenceSetKey;
					continue;
				}
				if (HasMissingPhysicalUid(array2))
				{
					LogSwapScan("waiting for UID", array2);
					flag = true;
					text = presenceSetKey;
					continue;
				}
				// Guard against saving a mismatched swap pair during the brief window when
				// one half of a new figure arrives before the other half is detected. E.g.
				// placing Dark Wash Buckler while Rattle Ranger is still on the portal: the
				// portal may return {DarkWash top, Ranger bottom} before Buckler is seen.
				// revealedScanKeys tells us which halves were already announced; if one half
				// is new and the other is already-announced, we are mid-transition — wait.
				if (IsSwapTransitionState(array2, revealedScanKeys))
				{
					swapTransitionPollCount++;
					if (swapTransitionPollCount <= 5)
					{
						LogSwapScan("swap transition — waiting for matching half", array2);
						flag = true;
						text = presenceSetKey;
						continue;
					}
					// After ~1.5 s give up and accept whatever pairing the portal settled on.
					LogSwapScan("swap transition timeout — accepting current combination", array2);
				}
				else
				{
					swapTransitionPollCount = 0;
				}
				// Only allow one figure (or one SWAP Force pair) at a time. Multiple figures
				// on the portal simultaneously cause UID cross-contamination between scans.
				if (!IsValidSingleFigureSet(array2))
				{
					if (presenceSetKey != lastTooManyFiguresKey)
					{
						lastTooManyFiguresKey = presenceSetKey;
						Publish("tooManyFigures", "Place only one figure at a time (or one SWAP Force pair).");
					}
					PublishPortalContents(array2);
					flag = true;
					text = presenceSetKey;
					continue;
				}
				lastTooManyFiguresKey = null;

				DumpScanSetIfRequested(portal, array2, cancellationToken);
				// Re-enter the save block if any figure in the current set hasn't been revealed
				// yet. This catches the case where a figure (e.g. a trap) returned uidPending on
				// the first poll because its UID was missing, but other figures in the same set
				// succeeded — flag2 was set true by those others, so the !flag2 guard wouldn't
				// re-trigger. Once the UID is available on the next poll the accumulated scan has
				// a UID key that isn't in revealedScanKeys yet, so we re-save and fire the event.
				bool hasUnrevealedScans = array2.Any(s => !revealedScanKeys.Contains(GetScanIdentityKey(s)));
				if (!flag || !flag2 || text != presenceSetKey || hasUnrevealedScans)
				{
					string scanSetKey = GetScanSetKey(array2);
					LogPortal($"saving scan: figurePresent={flag}, saved={flag2}, unrevealed={hasUnrevealedScans}, prevPresence='{text ?? "<null>"}', newPresence='{presenceSetKey}', scanKey='{scanSetKey}'");
					dictionary2.Clear();
					LogSwapScan("complete", array2);
					EnrichScansWithBlocks(portal, array2, cancellationToken);
					// Only reveal figures we haven't already announced this session, so
					// re-saving the set (because a new figure was added) doesn't re-reveal
					// the ones already on the portal.
					HashSet<string> newScanKeys = array2
						.Select(GetScanIdentityKey)
						.Where(key => !revealedScanKeys.Contains(key))
						.ToHashSet();
					IReadOnlyDictionary<string, string>? selectedCatalogIds = GetResolvedVariantChoices(presenceSetKey);
					bool savedForReal = SaveScans(array2, selectedCatalogIds, revealOnlyKeys: newScanKeys);
					foreach (FigureScan saved in array2)
					{
						revealedScanKeys.Add(GetScanIdentityKey(saved));
					}
					lastScanSeenAt = DateTime.UtcNow;
					// Only mark as saved when the collection was actually written.
					// If AddScans returned uidPending (no Entry), flag2 stays false so the
					// next poll (with the UID available) retries and fires the scan event.
					if (savedForReal) flag2 = true;
				}
				flag = true;
				text = presenceSetKey;
				// Use the accumulated scans (array2) rather than the raw poll so the
				// debug bar retains UIDs across polls where block-0 reads time out.
				PublishPortalContents(array2);
			}
			else if (flag && IsRemovalConfirmed(lastScanSeenAt, num2, isTraptaniumPortal))
			{
				LogPortal($"removal confirmed after {num2} empty inventory polls");
				flag = false;
				flag2 = false;
				text = null;
				dictionary.Clear();
				dictionary2.Clear();
				revealedScanKeys.Clear();
				ClearVariantChoiceSession();
				swapTransitionPollCount = 0;
				lastTooManyFiguresKey = null;
				lastScanSeenAt = null;
				Publish("removed", "Figure removed. Ready for the next one.");
			}
		}
	}

	private static bool IsRemovalConfirmed(DateTime? lastScanSeenAt, int emptyPollStreak, bool isTraptaniumPortal)
	{
		int num = isTraptaniumPortal ? TraptaniumRemovalEmptyPollsRequired : RemovalEmptyPollsRequired;
		int num2 = isTraptaniumPortal ? TraptaniumRemovalGraceMs : RemovalGraceMs;
		if (emptyPollStreak < num)
		{
			return false;
		}
		if (lastScanSeenAt.HasValue)
		{
			return DateTime.UtcNow - lastScanSeenAt.Value >= TimeSpan.FromMilliseconds(num2);
		}
		return true;
	}

	private static bool IsPlacementGapConfirmed(DateTime? lastScanSeenAt, int emptyPollStreak, bool isTraptaniumPortal)
	{
		int requiredEmptyPolls = isTraptaniumPortal ? 5 : 3;
		int requiredGapMs = isTraptaniumPortal ? 1400 : 800;
		if (emptyPollStreak < requiredEmptyPolls)
		{
			return false;
		}
		if (lastScanSeenAt.HasValue)
		{
			return DateTime.UtcNow - lastScanSeenAt.Value >= TimeSpan.FromMilliseconds(requiredGapMs);
		}
		return true;
	}

	private void LogSwapScan(string phase, IEnumerable<FigureScan> scans)
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		FigureScan[] array = (from scan in scans
			where IsSwapBottomScan(scan) || IsSwapTopScan(scan)
			orderby (!IsSwapTopScan(scan)) ? 1 : 0, scan.ToyId
			select scan).ToArray();
		if (array.Length != 0)
		{
			string text = phase + ": " + string.Join(", ", array.Select(FormatSwapScan));
			if (!string.Equals(text, _lastSwapDebugText, StringComparison.Ordinal))
			{
				_lastSwapDebugText = text;
				_collectionStore.AppendSwapDebug(text);
				Publish("swapDebug", text);
			}
		}
	}

	private static string FormatSwapScan(FigureScan scan)
	{
		string value = (IsSwapTopScan(scan) ? "Top" : (IsSwapBottomScan(scan) ? "Bottom" : "Other"));
		string value2 = (string.IsNullOrWhiteSpace(scan.FigureUid) ? "uid ?" : ("uid " + scan.FigureUid));
		return $"{value} {scan.ToyIdHex}/{scan.VariantIdHex} slot {scan.ToyIndexHex} {value2}";
	}

	private void LogStatusPacket(byte[] packet)
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		string text = "status: " + string.Join(" ", packet.Select((byte value) => value.ToString("X2")));
		if (!string.Equals(text, _lastStatusDebugText, StringComparison.Ordinal))
		{
			_lastStatusDebugText = text;
			_collectionStore.AppendSwapDebug(text);
			LogPortal($"first byte of status response: 0x{packet[0]:X2}, length={packet.Length}");
		}
	}

	private bool ShouldSuppressTraptaniumDuplicate(IPortalConnection portal, string scanKey)
	{
		if (IsTraptaniumPortal(portal) && !string.IsNullOrWhiteSpace(scanKey) && !string.IsNullOrWhiteSpace(_lastSavedPortalScanKey))
		{
			DateTime? lastSavedPortalScanAt = _lastSavedPortalScanAt;
			if (lastSavedPortalScanAt.HasValue && _lastSavedPortalProductId == portal.ProductId)
			{
				if (string.Equals(_lastSavedPortalScanKey, scanKey, StringComparison.Ordinal))
				{
					return DateTime.UtcNow - lastSavedPortalScanAt.Value < TimeSpan.FromMilliseconds(45000.0);
				}
				return false;
			}
		}
		return false;
	}

	private void RememberSavedPortalScan(IPortalConnection portal, string scanKey)
	{
		_lastSavedPortalScanKey = scanKey;
		_lastSavedPortalScanAt = DateTime.UtcNow;
		_lastSavedPortalProductId = portal.ProductId;
	}

	private void ForgetSavedPortalScan(IPortalConnection portal)
	{
		if (_lastSavedPortalProductId == portal.ProductId)
		{
			_lastSavedPortalScanKey = null;
			_lastSavedPortalScanAt = null;
			_lastSavedPortalProductId = null;
		}
	}

	private IReadOnlyList<FigureScan> GatherSwapScans(IPortalConnection portal, IReadOnlyList<FigureScan> initialScans, CancellationToken cancellationToken)
	{
		if (!ContainsSwapScan(initialScans))
		{
			return initialScans;
		}
		// Use a UID-independent key for SWAP halves so that UID read instability during
		// a quick swap (same physical half returning different block-0 bytes on each poll)
		// doesn't create phantom duplicate top/bottom entries.
		static string GatherKey(FigureScan s) =>
			(IsSwapTopScan(s) || IsSwapBottomScan(s))
				? $"{s.ToyId}:{s.VariantId}"
				: GetScanIdentityKey(s);
		Dictionary<string, FigureScan> dictionary = initialScans.ToDictionary(GatherKey, (FigureScan scan) => scan);
		for (int i = 0; i < 3; i++)
		{
			if (!IsIncompleteSwapScanSet(dictionary.Values.ToArray()))
			{
				break;
			}
			cancellationToken.WaitHandle.WaitOne(90);
			foreach (FigureScan item in ReadFigureIdentityBlockSet(portal, cancellationToken))
			{
				dictionary[GatherKey(item)] = item;
			}
		}
		return dictionary.Values.ToArray();
	}

	private static void UpdateAccumulatedScans(Dictionary<string, FigureScan> accumulatedScans, IReadOnlyList<FigureScan> scans)
	{
		bool flag = ContainsSwapScan(scans);
		bool flag2 = ContainsSwapScan(accumulatedScans.Values);
		if (accumulatedScans.Count > 0 && flag != flag2)
		{
			accumulatedScans.Clear();
		}
		if (flag && !IsIncompleteSwapScanSet(scans))
		{
			accumulatedScans.Clear();
		}
		// Remove accumulated non-swap entries for figures that are no longer present
		// in the current poll. Without this, removing a figure while others remain
		// leaves stale entries in the dictionary indefinitely (removal only resets
		// the dict when ALL figures leave the portal).
		string[] stale = accumulatedScans
			.Where(pair =>
				!IsSwapTopScan(pair.Value) && !IsSwapBottomScan(pair.Value) &&
				!NonSwapScanStillPresent(pair.Value, scans))
			.Select(pair => pair.Key)
			.ToArray();
		foreach (string key in stale)
			accumulatedScans.Remove(key);
		foreach (FigureScan scan in scans)
		{
			RemoveSameUidDifferentIdentity(accumulatedScans, scan);
			if (IsSwapTopScan(scan))
			{
				RemoveSwapPart(accumulatedScans, IsSwapTopScan);
			}
			else if (IsSwapBottomScan(scan))
			{
				RemoveSwapPart(accumulatedScans, IsSwapBottomScan);
			}
			else
			{
				RemoveSameFigureWithoutUid(accumulatedScans, scan);
			}
			accumulatedScans[GetScanIdentityKey(scan)] = scan;
		}
	}

	private IReadOnlyList<FigureScan> FilterUnstableIdentityReads(IReadOnlyList<FigureScan> scans, IEnumerable<FigureScan> accumulatedScans)
	{
		if (scans.Count == 0)
		{
			return scans;
		}

		FigureScan[] accumulatedArray = accumulatedScans.ToArray();
		Dictionary<string, HashSet<string>> stableKeysByUid = accumulatedArray
			.Where(scan => !string.IsNullOrWhiteSpace(scan.FigureUid) && _collectionStore.IsKnownVariant(scan.ToyId, scan.VariantId))
			.GroupBy(scan => scan.FigureUid!, StringComparer.OrdinalIgnoreCase)
			.ToDictionary(
				group => group.Key,
				group => group.Select(scan => GetVariantKey(scan.ToyId, scan.VariantId)).ToHashSet(StringComparer.Ordinal),
				StringComparer.OrdinalIgnoreCase);
		HashSet<string> knownUids = scans
			.Concat(accumulatedArray)
			.Where(scan => !string.IsNullOrWhiteSpace(scan.FigureUid) && _collectionStore.IsKnownVariant(scan.ToyId, scan.VariantId))
			.Select(scan => scan.FigureUid!)
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		List<FigureScan> filtered = [];
		foreach (FigureScan scan in scans)
		{
			bool isKnown = _collectionStore.IsKnownVariant(scan.ToyId, scan.VariantId);
			string? scanUid = string.IsNullOrWhiteSpace(scan.FigureUid) ? null : scan.FigureUid;
			string scanVariantKey = GetVariantKey(scan.ToyId, scan.VariantId);
			if (isKnown &&
				scanUid is not null &&
				stableKeysByUid.TryGetValue(scanUid, out HashSet<string>? stableKeys) &&
				stableKeys.Count == 1 &&
				!stableKeys.Contains(scanVariantKey))
			{
				LogPortal($"dropped known identity/UID collision {scan.ToyIdHex}/{scan.VariantIdHex} slot {scan.ToyIndexHex} uid {scan.FigureUid}");
				continue;
			}
			if (!isKnown)
			{
				bool uidBelongsToKnownFigure = !string.IsNullOrWhiteSpace(scan.FigureUid) && knownUids.Contains(scan.FigureUid);
				bool noUidEchoBesideKnownFigure = string.IsNullOrWhiteSpace(scan.FigureUid) && knownUids.Count > 0;
				if (uidBelongsToKnownFigure || noUidEchoBesideKnownFigure)
				{
					LogPortal($"dropped unstable identity read {scan.ToyIdHex}/{scan.VariantIdHex} slot {scan.ToyIndexHex} uid {scan.FigureUid ?? "?"}");
					continue;
				}
			}

			filtered.Add(scan);
		}

		if (filtered.Count == 0 && accumulatedArray.Length > 0)
		{
			LogPortal("all current identity reads were unstable; keeping previous stable portal contents");
			return accumulatedArray;
		}

		return filtered;
	}

	private static bool NonSwapScanStillPresent(FigureScan accumulatedScan, IReadOnlyList<FigureScan> currentScans)
	{
		FigureScan[] matchingCurrentScans = currentScans
			.Where(scan =>
				!IsSwapTopScan(scan) &&
				!IsSwapBottomScan(scan) &&
				scan.ToyId == accumulatedScan.ToyId &&
				scan.VariantId == accumulatedScan.VariantId)
			.ToArray();

		if (matchingCurrentScans.Length == 0)
		{
			return false;
		}

		if (string.IsNullOrWhiteSpace(accumulatedScan.FigureUid))
		{
			return true;
		}

		if (matchingCurrentScans.Any(scan =>
			string.Equals(scan.FigureUid, accumulatedScan.FigureUid, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}

		// If the portal briefly sees the same toy/variant but misses every UID, keep the
		// cached UID for now. The next successful UID read will prune removed duplicates.
		return matchingCurrentScans.All(scan => string.IsNullOrWhiteSpace(scan.FigureUid));
	}

	private static void RemoveSameFigureWithoutUid(Dictionary<string, FigureScan> scans, FigureScan currentScan)
	{
		KeyValuePair<string, FigureScan>[] array = scans.ToArray();
		foreach (KeyValuePair<string, FigureScan> keyValuePair in array)
		{
			keyValuePair.Deconstruct(out var key, out var value);
			string key2 = key;
			FigureScan figureScan = value;
			bool num = figureScan.ToyId == currentScan.ToyId && figureScan.VariantId == currentScan.VariantId;
			bool flag = string.IsNullOrWhiteSpace(figureScan.FigureUid) || string.IsNullOrWhiteSpace(currentScan.FigureUid) || string.Equals(figureScan.FigureUid, currentScan.FigureUid, StringComparison.OrdinalIgnoreCase);
			if (num && flag)
			{
				// If the accumulated entry had a UID but this poll's scan doesn't (block-0
				// read timed out), carry the known UID forward rather than losing it.
				if (!string.IsNullOrWhiteSpace(figureScan.FigureUid) && string.IsNullOrWhiteSpace(currentScan.FigureUid))
				{
					currentScan.FigureUid = figureScan.FigureUid;
				}
				scans.Remove(key2);
			}
		}
	}

	private static void RemoveSameUidDifferentIdentity(Dictionary<string, FigureScan> scans, FigureScan currentScan)
	{
		if (string.IsNullOrWhiteSpace(currentScan.FigureUid))
		{
			return;
		}

		KeyValuePair<string, FigureScan>[] matches = scans
			.Where(pair =>
				!string.IsNullOrWhiteSpace(pair.Value.FigureUid) &&
				string.Equals(pair.Value.FigureUid, currentScan.FigureUid, StringComparison.OrdinalIgnoreCase) &&
				(pair.Value.ToyId != currentScan.ToyId || pair.Value.VariantId != currentScan.VariantId))
			.ToArray();

		foreach (KeyValuePair<string, FigureScan> match in matches)
		{
			scans.Remove(match.Key);
		}
	}

	private static void RemoveSwapPart(Dictionary<string, FigureScan> scans, Func<FigureScan, bool> predicate)
	{
		Func<FigureScan, bool> predicate2 = predicate;
		string[] array = (from pair in scans
			where predicate2(pair.Value)
			select pair.Key).ToArray();
		foreach (string key in array)
		{
			scans.Remove(key);
		}
	}

	private static void RememberPendingSwapBottoms(IEnumerable<FigureScan> scans, Dictionary<string, FigureScan> pendingSwapBottomScans)
	{
		foreach (FigureScan item in scans.Where(IsSwapBottomScan))
		{
			pendingSwapBottomScans[GetScanIdentityKey(item)] = item;
		}
	}

	private static void MergePendingSwapBottoms(Dictionary<string, FigureScan> accumulatedScans, Dictionary<string, FigureScan> pendingSwapBottomScans)
	{
		if (!accumulatedScans.Values.Any(IsSwapTopScan) || accumulatedScans.Values.Any(IsSwapBottomScan))
		{
			return;
		}
		foreach (KeyValuePair<string, FigureScan> pendingSwapBottomScan in pendingSwapBottomScans)
		{
			pendingSwapBottomScan.Deconstruct(out var key, out var value);
			string key2 = key;
			FigureScan value2 = value;
			accumulatedScans[key2] = value2;
		}
	}

	private void EnrichScansWithBlocks(IPortalConnection portal, IReadOnlyList<FigureScan> scans, CancellationToken cancellationToken)
	{
		foreach (FigureScan scan in scans)
		{
			try
			{
			if (_collectionStore.SupportsFigureData(scan))
			{
				List<FigureBlockDump> list = new List<FigureBlockDump>();
				int[] statsBlockIndexes = StatsBlockIndexes;
				foreach (int num in statsBlockIndexes)
				{
					byte[]? array = ReadFigureBlock(portal, scan.ToyIndex, (byte)num, cancellationToken, 16, requireNonZeroData: false, GetBlockReadTimeoutMs(portal));
					list.Add(new FigureBlockDump
					{
						Block = num,
						BlockHex = $"0x{num:X2}",
						Success = (array != null),
						Data = FormatHex(array)
					});
				}
				scan.Blocks = list;
			}
			else if (_collectionStore.IsTrapScan(scan))
			{
				LogPortal($"trap enrichment start slot={scan.ToyIndexHex} block0={(scan.Block0 is null ? "<null>" : "ok")} block1={(scan.Block1 is null ? "<null>" : "ok")}");
				// Trap metadata is encrypted with the same block key as character stats.
				// Decode it before reading the trapped villain fields.
				byte[]? trapBlockRaw = ReadFigureBlock(portal, scan.ToyIndex, 9, cancellationToken,
					minimumDataLength: 2, requireNonZeroData: false, GetBlockReadTimeoutMs(portal));
				trapBlockRaw ??= ReadFigureBlock(portal, scan.ToyIndex, 9, cancellationToken,
					minimumDataLength: 2, requireNonZeroData: false, GetBlockReadTimeoutMs(portal), matchToyIndex: false);
				byte[]? trapBlock = DecryptTrapBlock(scan, trapBlockRaw, 9);
				if (trapBlock != null && trapBlock.Length >= 2)
				{
					scan.TrapVillainId = trapBlock[0];
					scan.TrapVillainEvolved = trapBlock[1] != 0;
				}
				// Block 8: trap metadata. Byte 0 = pre-trapped flag, byte 7 = original villain ID.
				// Both are needed to detect villain variant packs (e.g. Riot Shield Shredder).
				byte[]? trapMetaRaw = ReadFigureBlock(portal, scan.ToyIndex, 8, cancellationToken,
					minimumDataLength: 8, requireNonZeroData: false, GetBlockReadTimeoutMs(portal));
				trapMetaRaw ??= ReadFigureBlock(portal, scan.ToyIndex, 8, cancellationToken,
					minimumDataLength: 8, requireNonZeroData: false, GetBlockReadTimeoutMs(portal), matchToyIndex: false);
				byte[]? trapMeta = DecryptTrapBlock(scan, trapMetaRaw, 8);
				if (trapMeta != null && trapMeta.Length >= 8)
				{
					scan.TrapPreTrapped = trapMeta[0] == 1;
					scan.TrapVariantVillainId = trapMeta[7];
				}
				LogPortal($"trap decode slot={scan.ToyIndexHex} raw8={FormatHex(trapMetaRaw) ?? "<null>"} dec8={FormatHex(trapMeta) ?? "<null>"} raw9={FormatHex(trapBlockRaw) ?? "<null>"} dec9={FormatHex(trapBlock) ?? "<null>"} villain={scan.TrapVillainId?.ToString() ?? "<null>"} evolved={scan.TrapVillainEvolved?.ToString() ?? "<null>"}");
			}
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				LogPortal($"scan enrichment failed for {scan.ToyIdHex}/{scan.VariantIdHex} slot={scan.ToyIndexHex}: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}

	private static byte[]? DecryptTrapBlock(FigureScan scan, byte[]? rawBlock, int blockIndex)
	{
		if (rawBlock is null || rawBlock.Length != 16)
		{
			return null;
		}

		return FigureStatsReader.TryDecryptBlock(scan.Block0, scan.Block1, rawBlock, blockIndex);
	}

	private void DumpScanSetIfRequested(IPortalConnection portal, IReadOnlyList<FigureScan> scans, CancellationToken cancellationToken)
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		if (Interlocked.Exchange(ref _dumpNextFigureRequested, 0) == 0)
		{
			return;
		}
		try
		{
			List<FigureDiagnosticDump> list = new List<FigureDiagnosticDump>();
			foreach (FigureScan item in scans.OrderBy((FigureScan scan) => scan.ToyIndex))
			{
				List<FigureBlockDump> list2 = new List<FigureBlockDump>();
				for (int i = 0; i < 64; i++)
				{
					byte[]? array = ReadFigureBlock(portal, item.ToyIndex, (byte)i, cancellationToken, 16, requireNonZeroData: false, GetBlockReadTimeoutMs(portal));
					list2.Add(new FigureBlockDump
					{
						Block = i,
						BlockHex = $"0x{i:X2}",
						Success = (array != null),
						Data = FormatHex(array)
					});
				}
				list.Add(new FigureDiagnosticDump
				{
					ToyIndex = item.ToyIndex,
					ToyIndexHex = item.ToyIndexHex,
					ToyId = item.ToyId,
					ToyIdHex = item.ToyIdHex,
					VariantId = item.VariantId,
					VariantIdHex = item.VariantIdHex,
					FigureUid = item.FigureUid,
					Block0 = item.Block0,
					Block1 = item.Block1,
					Blocks = list2
				});
			}
			string text = _collectionStore.SaveFigureDump(list);
			Publish("figureDumpSaved", "Figure data dump saved: " + text);
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			Publish("error", "Failed to dump figure data: " + ex.Message);
		}
	}

	// Returns true when at least one entry was actually written to the collection.
	// Returns false when the save was deferred (e.g. uidPending, variantChoiceRequired).
	private bool SaveScans(IReadOnlyList<FigureScan> scans, IReadOnlyDictionary<string, string>? selectedCatalogIds = null, string? choiceToken = null, IReadOnlySet<string>? revealOnlyKeys = null)
	{
		IReadOnlyList<CollectionScanResult> results = _collectionStore.AddScans(scans, selectedCatalogIds);
		if (!PublishVariantChoiceIfNeeded(scans, selectedCatalogIds, results, choiceToken))
		{
			if (choiceToken != null)
			{
				lock (_variantChoiceLock)
				{
					_pendingVariantChoices.Remove(choiceToken);
				}
			}
			PublishScanResults(results, revealOnlyKeys);
		}
		return results.Any(r => r.Entry is not null);
	}

	private bool PublishVariantChoiceIfNeeded(IReadOnlyList<FigureScan> scans, IReadOnlyDictionary<string, string>? selectedCatalogIds, IReadOnlyList<CollectionScanResult> results, string? choiceToken)
	{
		CollectionScanResult? collectionScanResult = results.FirstOrDefault((CollectionScanResult result) => string.Equals(result.Status, "variantChoiceRequired", StringComparison.OrdinalIgnoreCase));
		if (collectionScanResult == null)
		{
			return false;
		}
		string text = choiceToken ?? Guid.NewGuid().ToString("N");
		Dictionary<string, string> selectedCatalogIds2 = ((selectedCatalogIds == null) ? new Dictionary<string, string>() : new Dictionary<string, string>(selectedCatalogIds));
		string currentScanKey = GetVariantKey(collectionScanResult.Scan.ToyId, collectionScanResult.Scan.VariantId);
		string presenceSetKey = GetPresenceSetKey(scans);
		lock (_variantChoiceLock)
		{
			if (choiceToken == null && _pendingVariantChoices.Values.Any(choice =>
				string.Equals(choice.PresenceSetKey, presenceSetKey, StringComparison.Ordinal) &&
				string.Equals(choice.CurrentScanKey, currentScanKey, StringComparison.Ordinal)))
			{
				return true;
			}

			_pendingVariantChoices[text] = new PendingVariantChoice(scans.ToArray(), selectedCatalogIds2, currentScanKey, presenceSetKey);
		}
		Publish("variantChoiceRequired", collectionScanResult.Message ?? "Which physical variant did you scan?", null, null, collectionScanResult.Choices, text);
		return true;
	}

	private IReadOnlyDictionary<string, string>? GetResolvedVariantChoices(string presenceSetKey)
	{
		lock (_variantChoiceLock)
		{
			return _resolvedVariantChoicesByPresence.TryGetValue(presenceSetKey, out Dictionary<string, string>? selectedCatalogIds)
				? new Dictionary<string, string>(selectedCatalogIds)
				: null;
		}
	}

	private void ClearVariantChoiceSession()
	{
		lock (_variantChoiceLock)
		{
			_pendingVariantChoices.Clear();
			_resolvedVariantChoicesByPresence.Clear();
		}
	}

	private void PublishScanResults(IReadOnlyList<CollectionScanResult> results, IReadOnlySet<string>? revealOnlyKeys = null)
	{
		// When revealOnlyKeys is set, only figures whose scan key is in it trigger the
		// on-screen reveal/announcement. Others were already on the portal and announced.
		bool ShouldReveal(CollectionScanResult result) =>
			revealOnlyKeys == null || revealOnlyKeys.Contains(GetScanIdentityKey(result.Scan));

		List<CollectionScanResult> list = new List<CollectionScanResult>();
		foreach (CollectionScanResult result in results)
		{
			if (result.Entry == null)
			{
				if (string.Equals(result.Status, "uidCollision", StringComparison.OrdinalIgnoreCase))
				{
					LogPortal(result.Message ?? "Ignored unstable UID collision scan.");
					continue;
				}
				if (!string.IsNullOrWhiteSpace(result.Status))
				{
					Publish(result.Status, result.Message ?? "This figure is not ready to scan yet.");
					continue;
				}
				Publish("unknownFigure", $"Unknown figure ({result.Scan.ToyIdHex}, variant {result.Scan.VariantIdHex}). Not added.");
			}
			else
			{
				list.Add(result);
			}
		}
		IReadOnlyList<CollectionScanResult> readOnlyList = list.Where(delegate(CollectionScanResult result)
		{
			string? text3 = result.Entry?.SwapPart;
			return (text3 == "top" || text3 == "bottom") ? true : false;
		}).ToArray();
		if (readOnlyList.Any((CollectionScanResult result) => string.Equals(result.Entry?.SwapPart, "top", StringComparison.OrdinalIgnoreCase)) && readOnlyList.Any((CollectionScanResult result) => string.Equals(result.Entry?.SwapPart, "bottom", StringComparison.OrdinalIgnoreCase)))
		{
			// Reveal the swap combo only if at least one of its halves is newly added.
			if (readOnlyList.Any(ShouldReveal))
			{
				CollectionEntry[] array = (from result in readOnlyList
					select result.Entry into entry
					orderby (!string.Equals(entry.SwapPart, "top", StringComparison.OrdinalIgnoreCase)) ? 1 : 0
					select entry).ToArray();
				bool num = readOnlyList.Any((CollectionScanResult result) => result.IsNew);
				string type = (num ? "newSwapDiscovery" : "swapScan");
				string text = (num ? ("New SWAP Force figure discovered: " + FormatSwapName(array) + ".") : ("Scanned " + FormatSwapName(array) + "."));
				Publish(type, text, array[0], array);
			}
		}
		// Villain results are paired with their originating trap by ToyIndex. Apply priority:
		// if the villain is new, show the villain reveal; otherwise show the trap reveal.
		List<CollectionScanResult> villainResults = list
			.Except(readOnlyList)
			.Where(r => IsVillainEntry(r.Entry))
			.ToList();
		foreach (CollectionScanResult item in list.Except(readOnlyList))
		{
			if (IsVillainEntry(item.Entry))
			{
				continue; // Villain reveals are handled via the trap loop below.
			}
			if (!ShouldReveal(item))
			{
				continue;
			}
			CollectionScanResult? villainItem = villainResults
				.FirstOrDefault(vr => vr.Scan.ToyIndex == item.Scan.ToyIndex);
			CollectionScanResult revealItem = villainItem?.IsNew == true ? villainItem : item;
			CollectionEntry? entry2 = revealItem.Entry;
			string type2 = (revealItem.IsNew ? "newDiscovery" : "scan");
			string text2 = (revealItem.IsNew ? ("New figure discovered: " + entry2?.Name + ".") : $"Scanned {entry2?.Name} ({entry2?.ToyIdHex}, variant {entry2?.VariantIdHex}).");
			Publish(type2, text2, entry2);
		}
		if (list.Count > 0)
		{
			Publish("collectionUpdated", $"{list.Count} figure scan saved.");
		}
	}

	private static string FormatSwapName(IEnumerable<CollectionEntry> entries)
	{
		CollectionEntry? collectionEntry = entries.FirstOrDefault((CollectionEntry entry) => string.Equals(entry.SwapPart, "top", StringComparison.OrdinalIgnoreCase));
		CollectionEntry? collectionEntry2 = entries.FirstOrDefault((CollectionEntry entry) => string.Equals(entry.SwapPart, "bottom", StringComparison.OrdinalIgnoreCase));
		string? text = DisplayNickname(collectionEntry) ?? collectionEntry?.Name;
		string? text2 = DisplayNickname(collectionEntry2) ?? collectionEntry2?.Name;
		return string.Join(" + ", new string?[2] { text, text2 }.Where((string? value) => !string.IsNullOrWhiteSpace(value)));
	}

	private static string? DisplayNickname(CollectionEntry? entry)
	{
		if (!string.IsNullOrWhiteSpace(entry?.Nickname))
		{
			return entry.Nickname;
		}
		return entry?.Stats?.Nickname;
	}

	private static bool IsVillainEntry(CollectionEntry? entry) =>
		string.Equals(entry?.Type, "Villain", StringComparison.OrdinalIgnoreCase);

	private static bool IsRecoverablePortalException(Exception exception)
	{
		if (exception is Win32Exception || exception is IOException || exception is TimeoutException)
		{
			return true;
		}
		return false;
	}

	private IPortalConnection OpenPortal()
	{
		if (TryOpenPortal(out IPortalConnection? connection) && connection != null)
		{
			return connection;
		}
		throw new InvalidOperationException("No supported portal found. Plug in a supported portal, then try scanning again.");
	}

	private bool TryOpenPortal(out IPortalConnection? connection)
	{
		connection = null;
		string? interfaceGuidText = FindWinUsbInterfaceGuid(7959);
		IReadOnlyList<string> readOnlyList = PortalUsb.FindDevicePaths(5168, 7959, interfaceGuidText);
		LogPortal($"scan winusb paths (pid=0x{7959:X4}): {readOnlyList.Count} found");
		foreach (string item in readOnlyList)
		{
			LogPortal("  winusb candidate: " + item);
			if (TryOpenWinUsbPortal(item, "Spyro/Giants/SWAP Force portal", out connection))
			{
				LogPortal("opened winusb portal at: " + item);
				return true;
			}
			LogPortal("  winusb open failed for: " + item);
		}
		IReadOnlyList<string> readOnlyList2 = HidPortal.FindDevicePaths(5168, 336);
		LogPortal($"scan hid paths (pid=0x{336:X4}): {readOnlyList2.Count} found");
		foreach (string item2 in readOnlyList2)
		{
			LogPortal("  hid candidate: " + item2);
			if (TryOpenHidPortal(item2, "Wii U HID portal", out connection))
			{
				LogPortal("opened hid portal at: " + item2);
				return true;
			}
			LogPortal("  hid open failed for: " + item2);
		}
		IReadOnlyList<string> interfaceGuidTexts = FindWinUsbInterfaceGuidsByVendor(5168);
		IReadOnlyList<string> readOnlyList3 = PortalUsb.FindDevicePathsByVendor(5168, interfaceGuidTexts);
		LogPortal($"scan winusb paths by vendor (vid=0x{5168:X4}): {readOnlyList3.Count} found");
		foreach (string item3 in readOnlyList3)
		{
			LogPortal("  winusb vendor candidate: " + item3);
			string text = FormatDetectedPortalDescription("WinUSB", item3);
			if (TryOpenWinUsbPortal(item3, text, out connection))
			{
				LogPortal("opened winusb vendor portal: " + text + " | " + item3);
				return true;
			}
			LogPortal("  winusb vendor open failed for: " + item3);
		}
		IReadOnlyList<string> readOnlyList4 = HidPortal.FindDevicePathsByVendor(5168);
		LogPortal($"scan hid paths by vendor (vid=0x{5168:X4}): {readOnlyList4.Count} found");
		foreach (string item4 in readOnlyList4)
		{
			LogPortal("  hid vendor candidate: " + item4);
			string text2 = FormatDetectedPortalDescription("HID", item4);
			if (TryOpenHidPortal(item4, text2, out connection))
			{
				LogPortal("opened hid vendor portal: " + text2 + " | " + item4);
				return true;
			}
			LogPortal("  hid vendor open failed for: " + item4);
		}
		LogPortal("no portal could be opened");
		return false;
	}

	private void LogPortal(string text)
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		_collectionStore.AppendSwapDebug("[portal] " + text);
	}

	private bool IsPortalConnected()
	{
		if (!TryOpenPortal(out IPortalConnection? connection) || connection == null)
		{
			return false;
		}
		connection.Dispose();
		return true;
	}

	private static bool TryOpenWinUsbPortal(string path, string description, out IPortalConnection? connection)
	{
		try
		{
			int? productId = TryGetProductId(path);
			PortalProtocol protocol = ((productId.GetValueOrDefault() == 336) ? PortalProtocol.HidStyle : PortalProtocol.SpyroPcUsb);
			connection = new WinUsbPortalConnection(PortalUsb.Open(path), description, protocol, productId);
			return true;
		}
		catch
		{
			connection = null;
			return false;
		}
	}

	private static bool TryOpenHidPortal(string path, string description, out IPortalConnection? connection)
	{
		try
		{
			connection = new HidPortalConnection(HidPortal.Open(path), description, TryGetProductId(path));
			return true;
		}
		catch
		{
			connection = null;
			return false;
		}
	}

	private static string FormatDetectedPortalDescription(string transport, string path)
	{
		int? num = TryGetProductId(path);
		if (num.HasValue)
		{
			return $"Detected Skylanders portal ({transport}, PID 0x{num.Value:X4})";
		}
		return "Detected Skylanders portal (" + transport + ")";
	}

	private static int? TryGetProductId(string path)
	{
		string text = path.ToLowerInvariant();
		int num = text.IndexOf("pid_", StringComparison.Ordinal);
		if (num < 0 || num + 8 > text.Length)
		{
			return null;
		}
		if (!int.TryParse(text.Substring(num + 4, 4), NumberStyles.HexNumber, null, out var result))
		{
			return null;
		}
		return result;
	}

	private static bool IsTraptaniumPortal(IPortalConnection portal)
	{
		return portal.ProductId.GetValueOrDefault() == 336;
	}

	private static int GetBlockReadTimeoutMs(IPortalConnection portal)
	{
		if (!IsTraptaniumPortal(portal))
		{
			return 80;
		}
		return 120;
	}

	private static void StartPortal(IPortalConnection portal)
	{
		portal.WriteCommand(82);
		Thread.Sleep(100);
		portal.WriteCommand(65, new byte[1] { 1 });
		Thread.Sleep(100);
		portal.WriteCommand(67, new byte[3] { 0, 96, 255 });
	}

	private IReadOnlyList<FigureScan> ReadFigureIdentityBlockSet(IPortalConnection portal, CancellationToken cancellationToken)
	{
		bool isTraptaniumPortal = IsTraptaniumPortal(portal);
		int lastSlot = isTraptaniumPortal ? TraptaniumLastSlot : 31;
		int finalWindowMs = isTraptaniumPortal ? TraptaniumFinalWindowMs : 180;
		int identityReadTimeoutMs = isTraptaniumPortal ? TraptaniumIdentityReadTimeoutMs : IdentityReadTimeoutMs;
		List<FigureScan> list = new List<FigureScan>();
		HashSet<string> seen = new HashSet<string>();
		bool flag = !_loggedFirstQueryAttempt;
		_loggedFirstQueryAttempt = true;
		for (int i = 0; i <= lastSlot; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (flag)
			{
				LogPortal($"sending Q 0x{i:X2} 0x01");
			}
			portal.WriteCommand(81, new byte[2]
			{
				(byte)i,
				1
			});
			DrainUntilIdentityResponse(portal, (byte)i, list, seen, DateTime.UtcNow.AddMilliseconds(identityReadTimeoutMs), cancellationToken, flag);
		}
		DrainFigureIdentityResponses(portal, list, seen, DateTime.UtcNow.AddMilliseconds(finalWindowMs), cancellationToken);
		if (list.Count > 0)
		{
			EnrichScansWithUids(portal, list, cancellationToken);
		}
		return list;
	}

	private void DrainUntilIdentityResponse(IPortalConnection portal, byte requestedToyIndex, List<FigureScan> scans, HashSet<string> seen, DateTime deadline, CancellationToken cancellationToken, bool verbose = false)
	{
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			byte[] array = portal.Read();
			if (array.Length == 0)
			{
				continue;
			}
			if (verbose)
			{
				LogPortal($"  reply: {string.Join(" ", from b in array.Take(8)
					select b.ToString("X2"))}... (len={array.Length})");
			}
			int portalCommandOffset = GetPortalCommandOffset(array);
			// Some portals reply one query late, so accept the Q response here and let
			// the UID/catalog filters below discard unstable identities.
			if (array.Length <= portalCommandOffset + 2 || array[portalCommandOffset] != 0x51)
			{
				continue;
			}
			byte resultIndex = array[portalCommandOffset + 1];
			if (resultIndex != 0x01)
			{
				AddFigureScanFromResponse(array, portalCommandOffset, scans, seen);
			}
			break; // slot acknowledged (empty or figure found) - move to next slot
		}
	}

	private static void DrainFigureIdentityResponses(IPortalConnection portal, List<FigureScan> scans, HashSet<string> seen, DateTime deadline, CancellationToken cancellationToken)
	{
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();
			byte[] array = portal.Read();
			if (array.Length != 0)
			{
				int portalCommandOffset = GetPortalCommandOffset(array);
				byte? block = (byte)1;
				if (IsQueryResponse(array, portalCommandOffset, null, block))
				{
					AddFigureScanFromResponse(array, portalCommandOffset, scans, seen);
				}
			}
		}
	}

	private static void AddFigureScanFromResponse(byte[] response, int offset, List<FigureScan> scans, HashSet<string> seen)
	{
		byte b = response[offset + 1];
		int num = offset + 3;
		int num2 = Math.Min(16, response.Length - num);
		if (num2 < 14)
		{
			return;
		}
		byte[] array = new byte[num2];
		Array.Copy(response, num, array, 0, num2);
		if (HasNonZeroByte(array))
		{
			int num3 = ReadLittleEndianUInt16(array, 0);
			int num4 = ReadLittleEndianUInt16(array, 12);
			if (seen.Add(b.ToString()))
			{
				scans.Add(new FigureScan
				{
					ToyIndex = b,
					ToyIndexHex = $"0x{b:X2}",
					ToyId = num3,
					ToyIdHex = $"0x{num3:X4}",
					VariantId = num4,
					VariantIdHex = $"0x{num4:X4}",
					Block0 = null,
					Block1 = FormatHex(array),
					ScannedAt = DateTimeOffset.Now
				});
			}
		}
	}

	private static void EnrichScansWithUids(IPortalConnection portal, List<FigureScan> scans, CancellationToken cancellationToken)
	{
		foreach (FigureScan scan in scans)
		{
			byte[]? array = ReadFigureBlock(portal, scan.ToyIndex, 0, cancellationToken, 8, requireNonZeroData: true, GetBlockReadTimeoutMs(portal));
			if (array != null)
			{
				scan.Block0 = FormatHex(array);
				scan.FigureUid = ExtractNtag7ByteUid(array);
			}
		}
	}

	private static byte[]? ReadFigureBlock(IPortalConnection portal, int toyIndex, byte block, CancellationToken cancellationToken, int minimumDataLength = 8, bool requireNonZeroData = true, int timeoutMs = 80, bool matchToyIndex = true)
	{
		for (int i = 0; i < 2; i++)
		{
			portal.WriteCommand(81, new byte[2]
			{
				(byte)toyIndex,
				block
			});
			DateTime dateTime = DateTime.UtcNow.AddMilliseconds(timeoutMs);
			while (DateTime.UtcNow < dateTime)
			{
				cancellationToken.ThrowIfCancellationRequested();
				byte[] array = portal.Read();
				if (array.Length == 0)
				{
					continue;
				}
				int portalCommandOffset = GetPortalCommandOffset(array);
				byte? expectedToyIndex = matchToyIndex ? (byte)toyIndex : null;
				if (!IsQueryResponse(array, portalCommandOffset, expectedToyIndex, block))
				{
					continue;
				}
				int num = portalCommandOffset + 3;
				int num2 = Math.Min(16, array.Length - num);
				if (num2 >= minimumDataLength)
				{
					byte[] array2 = new byte[num2];
					Array.Copy(array, num, array2, 0, num2);
					if (!requireNonZeroData || HasNonZeroByte(array2))
					{
						return array2;
					}
				}
			}
		}
		return null;
	}

	private static string? ExtractNtag7ByteUid(byte[] block0)
	{
		if (block0.Length < 8)
		{
			return null;
		}
		Span<byte> span = stackalloc byte[7];
		span[0] = block0[0];
		span[1] = block0[1];
		span[2] = block0[2];
		span[3] = block0[4];
		span[4] = block0[5];
		span[5] = block0[6];
		span[6] = block0[7];
		Span<byte> span2 = span;
		for (int i = 0; i < span2.Length; i++)
		{
			if (span2[i] != 0)
			{
				return Convert.ToHexString(span).ToLowerInvariant();
			}
		}
		return null;
	}

	private static string GetStatusSignature(byte[] packet)
	{
		if (packet.Length == 0)
		{
			return string.Empty;
		}
		List<byte> list = new List<byte>(packet.Length);
		for (int i = 0; i < packet.Length; i++)
		{
			if ((uint)(i - 6) > 1u)
			{
				list.Add(packet[i]);
			}
		}
		return string.Join(" ", list.Select((byte value) => value.ToString("X2")));
	}

	private static bool IsQueryResponse(byte[] response, int offset, byte? toyIndex = null, byte? block = null)
	{
		if (response.Length <= offset + 2 || response[offset] != 81)
		{
			return false;
		}
		if (toyIndex.HasValue && response[offset + 1] != toyIndex.Value)
		{
			return false;
		}
		if (block.HasValue)
		{
			return response[offset + 2] == block.Value;
		}
		return true;
	}

	private static string? FindWinUsbInterfaceGuid(int productId)
	{
		string name = $"SYSTEM\\CurrentControlSet\\Enum\\USB\\VID_{5168:X4}&PID_{productId:X4}";
		using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey(name);
		if (registryKey == null)
		{
			return null;
		}
		string[] subKeyNames = registryKey.GetSubKeyNames();
		foreach (string text in subKeyNames)
		{
			using RegistryKey? registryKey2 = registryKey.OpenSubKey(text + "\\Device Parameters");
			object? obj = registryKey2?.GetValue("DeviceInterfaceGUIDs");
			if (obj is string[] array && array.Length != 0)
			{
				return array[0].Trim('{', '}');
			}
			if (obj is string text2 && !string.IsNullOrWhiteSpace(text2))
			{
				return text2.Trim('{', '}');
			}
		}
		return null;
	}

	private static IReadOnlyList<string> FindWinUsbInterfaceGuidsByVendor(int vendorId)
	{
		List<string> list = new List<string>();
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\USB");
		if (registryKey == null)
		{
			return list;
		}
		string value = $"VID_{vendorId:X4}&PID_";
		string[] subKeyNames = registryKey.GetSubKeyNames();
		foreach (string text in subKeyNames)
		{
			if (!text.StartsWith(value, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			using RegistryKey? registryKey2 = registryKey.OpenSubKey(text);
			if (registryKey2 == null)
			{
				continue;
			}
			foreach (string item in ReadWinUsbInterfaceGuids(registryKey2))
			{
				if (hashSet.Add(item))
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	private static IEnumerable<string> ReadWinUsbInterfaceGuids(RegistryKey deviceRoot)
	{
		string[] subKeyNames = deviceRoot.GetSubKeyNames();
		foreach (string text in subKeyNames)
		{
			using RegistryKey? parameters = deviceRoot.OpenSubKey(text + "\\Device Parameters");
			object? obj = parameters?.GetValue("DeviceInterfaceGUIDs");
			if (obj is string[] array)
			{
				string[] array2 = array;
				for (int j = 0; j < array2.Length; j++)
				{
					string text2 = array2[j].Trim('{', '}');
					if (!string.IsNullOrWhiteSpace(text2))
					{
						yield return text2;
					}
				}
			}
			else if (obj is string text3 && !string.IsNullOrWhiteSpace(text3))
			{
				yield return text3.Trim('{', '}');
			}
		}
	}

	private static int GetPortalCommandOffset(byte[] packet)
	{
		if (packet.Length >= 4 && packet[0] == 0 && packet[1] == 11 && packet[2] == 20)
		{
			return 3;
		}
		if (packet.Length >= 3 && packet[0] == 11 && packet[1] == 20)
		{
			return 2;
		}
		if (packet.Length >= 2 && packet[0] == 0)
		{
			return 1;
		}
		return 0;
	}

	private static bool HasNonZeroByte(byte[]? bytes)
	{
		return bytes?.Any((byte value) => value != 0) ?? false;
	}

	private static int ReadLittleEndianUInt16(byte[] bytes, int offset)
	{
		return bytes[offset] | (bytes[offset + 1] << 8);
	}

	private static string? FormatHex(byte[]? bytes)
	{
		if (bytes != null && bytes.Length != 0)
		{
			return string.Join(" ", bytes.Select((byte value) => value.ToString("X2")));
		}
		return null;
	}

	private static string GetVariantKey(int toyId, int variantId)
	{
		return $"{toyId}:{variantId}";
	}

	private static string GetScanIdentityKey(FigureScan scan)
	{
		string variantKey = GetVariantKey(scan.ToyId, scan.VariantId);
		if (!string.IsNullOrWhiteSpace(scan.FigureUid))
		{
			return variantKey + ":uid:" + scan.FigureUid;
		}
		return variantKey;
	}

	private static string GetPresenceIdentityKey(FigureScan scan)
	{
		return (IsSwapTopScan(scan) ? "top" : (IsSwapBottomScan(scan) ? "bottom" : "figure")) + ":" + GetVariantKey(scan.ToyId, scan.VariantId);
	}

	private static bool IsSwapBottomScan(FigureScan scan)
	{
		int toyId = scan.ToyId;
		if (toyId >= 1000)
		{
			return toyId < 2000;
		}
		return false;
	}

	private static bool IsSwapTopScan(FigureScan scan)
	{
		int toyId = scan.ToyId;
		if (toyId >= 2000)
		{
			return toyId < 3000;
		}
		return false;
	}

	private static bool ContainsSwapScan(IEnumerable<FigureScan> scans)
	{
		return scans.Any((FigureScan scan) => IsSwapBottomScan(scan) || IsSwapTopScan(scan));
	}

	private static bool HasMissingPhysicalUid(IEnumerable<FigureScan> scans)
	{
		// Only block on a missing UID for SWAP Force halves — those need a UID to
		// distinguish physical copies before the halves are combined. Regular figures
		// (cores, giants, traps, vehicles, etc.) can be saved without a UID and will
		// be backfilled when the portal reads block 0 on the next poll.
		return scans.Any((FigureScan scan) =>
			!scan.IsInjectedTestScan &&
			string.IsNullOrWhiteSpace(scan.FigureUid) &&
			(IsSwapTopScan(scan) || IsSwapBottomScan(scan)));
	}

	// True when scans represent a valid single-figure set: exactly one non-swap figure, or
	// exactly one swap top + one swap bottom with no additional figures alongside them.
	private static bool IsValidSingleFigureSet(IReadOnlyList<FigureScan> scans)
	{
		int tops    = scans.Count(IsSwapTopScan);
		int bottoms = scans.Count(IsSwapBottomScan);
		int nonSwap = scans.Count(s => !IsSwapTopScan(s) && !IsSwapBottomScan(s));

		if (tops == 0 && bottoms == 0) return nonSwap == 1;
		if (tops == 1 && bottoms == 1) return nonSwap == 0;
		return false;
	}

	private static bool IsIncompleteSwapScanSet(IReadOnlyList<FigureScan> scans)
	{
		bool num = scans.Any(IsSwapBottomScan);
		bool flag = scans.Any(IsSwapTopScan);
		return num != flag;
	}

	// Returns true when we have exactly one top and one bottom but they came from different
	// physical figures — detected by one half being new (not yet in revealedScanKeys) and
	// the other being already-announced. In that case the caller should wait for the new
	// figure's missing half rather than saving the accidental cross-figure pairing.
	private static bool IsSwapTransitionState(IReadOnlyList<FigureScan> scans, HashSet<string> revealedScanKeys)
	{
		FigureScan? top = scans.FirstOrDefault(IsSwapTopScan);
		FigureScan? bottom = scans.FirstOrDefault(IsSwapBottomScan);
		if (top == null || bottom == null) return false;
		bool topIsNew = !revealedScanKeys.Contains(GetScanIdentityKey(top));
		bool bottomIsNew = !revealedScanKeys.Contains(GetScanIdentityKey(bottom));
		return topIsNew != bottomIsNew;
	}

	private static string GetScanSetKey(IReadOnlyList<FigureScan> scans)
	{
		return string.Join("|", scans.Select(GetScanIdentityKey).Order());
	}

	private static string GetPresenceSetKey(IReadOnlyList<FigureScan> scans)
	{
		return string.Join("|", scans.Select(GetPresenceIdentityKey).Order());
	}

	private void Publish(string type, string text, CollectionEntry? entry = null, IReadOnlyList<CollectionEntry>? entries = null, IReadOnlyList<CollectionEntry>? choices = null, string? choiceToken = null)
	{
		this.Message?.Invoke(this, new ScannerMessage(type, text, entry, entries, choices, choiceToken));
	}

	private void PublishPortalContents(IReadOnlyList<FigureScan> scans)
	{
		if (!DebugFeaturesEnabled)
		{
			return;
		}
		if (scans.Count == 0)
		{
			Publish("portalContents", "");
			return;
		}

		string lines = string.Join("\n", scans.Select(scan =>
		{
			string name = _collectionStore.GetFigureName(scan.ToyId, scan.VariantId) ?? "Unknown";
			string id = $"{scan.ToyIdHex}/{scan.VariantIdHex}";
			string uid = string.IsNullOrWhiteSpace(scan.FigureUid)
				? "no UID yet"
				: scan.FigureUid.ToUpperInvariant();
			string slot = scan.ToyIndexHex ?? "?";
			return $"{name}   slot {slot}   toyId {id}   uid {uid}";
		}));

		Publish("portalContents", lines);
	}
}

internal sealed record ScannerMessage(
    string Type,
    string Text,
    CollectionEntry? Entry,
    IReadOnlyList<CollectionEntry>? Entries = null,
    IReadOnlyList<CollectionEntry>? Choices = null,
    string? ChoiceToken = null);

internal sealed record PendingVariantChoice(
    IReadOnlyList<FigureScan> Scans,
    Dictionary<string, string> SelectedCatalogIds,
    string CurrentScanKey,
    string PresenceSetKey);

internal sealed class FigureBlockDump
{
    public int Block { get; init; }
    public string? BlockHex { get; init; }
    public bool Success { get; init; }
    public string? Data { get; init; }
}
