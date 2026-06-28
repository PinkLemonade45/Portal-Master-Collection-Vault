const catalogFiles = [
  "../data/catalog/spyros-adventure.json",
  "../data/catalog/giants.json",
  "../data/catalog/swap-force.json",
  "../data/catalog/trap-team.json",
  "../data/catalog/superchargers.json",
  "../data/catalog/imaginators.json"
];
const upgradeCatalogFile = "../data/catalog/upgrades.json";
const releaseBuild = Boolean(window.PORTAL_MASTER_VAULT_CONFIG?.releaseBuild);
const debugFeaturesEnabled = !releaseBuild;
const figureStatsEnabled = false;
const villainsEnabled = false;

const elementOrder = [
  "Air",
  "Earth",
  "Fire",
  "Water",
  "Magic",
  "Tech",
  "Life",
  "Undead",
  "Light",
  "Dark",
  "Kaos",
  "None"
];

const typeOrder = [
  "Core",
  "Core Skylander",
  "Skylander",
  "Giant",
  "SWAP Force",
  "Trap Master",
  "Sensei",
  "Villain Sensei",
  "Creation Crystal",
  "LightCore",
  "Legendary",
  "Legendary LightCore",
  "Dark Skylander",
  "Dark",
  "Legendary Skylander",
  "Eon's Elite",
  "Special",
  "Chase Variant",
  "Mini",
  "Sidekick",
  "Trap",
  "Villain",
  "Magic Item",
  "MagicItem",
  "Adventure Pack",
  "Expansion Pack",
  "LevelPiece",
  "BattlePiece"
];

const gameOrder = [
  "Spyro's Adventure",
  "Giants",
  "SWAP Force",
  "Trap Team",
  "SuperChargers",
  "Imaginators"
];

const settingsStorageKey = "portal-master-vault-settings";
const defaultSettings = {
  musicEnabled: true,
  musicVolume: 32,
  soundsEnabled: true,
  soundVolume: 90,
  scanAnimations: true
};

const state = {
  collection: [],
  catalog: [],
  upgrades: null,
  ownedKeys: new Set(),
  settings: loadSettings(),
  view: "owned",
  selectedUpgradeId: "",
  upgradeDirty: false,
  game: "All",
  type: "All",
  variant: "All",
  element: "All",
  activeFilterFields: [],
  filterModes: {
    game: "include",
    type: "include",
    variant: "include",
    element: "include"
  },
  filterDrawerOpen: true,
  groupBy: "element",
  search: "",
  missingOnly: false,
  unknownOnly: false
};

const advancedFilterDefinitions = [
  { field: "game", label: "Game" },
  { field: "type", label: "Type" },
  { field: "variant", label: "Variant" },
  { field: "element", label: "Element" }
];

const els = {
  workspace: document.querySelector("#workspace"),
  syncStatus: document.querySelector("#syncStatus"),
  summaryStrip: document.querySelector("#summaryStrip"),
  searchInput: document.querySelector("#searchInput"),
  filterDrawer: document.querySelector("#filterDrawer"),
  filterDrawerToggle: document.querySelector("#filterDrawerToggle"),
  advancedFilterRows: document.querySelector("#advancedFilterRows"),
  filterFieldSelect: document.querySelector("#filterFieldSelect"),
  addFilterButton: document.querySelector("#addFilterButton"),
  filterMatchSummary: document.querySelector("#filterMatchSummary"),
  groupByFilter: document.querySelector("#groupByFilter"),
  resultCount: document.querySelector("#resultCount"),
  collectionGrid: document.querySelector("#collectionGrid"),
  tabs: Array.from(document.querySelectorAll(".tab")),
  scannerPanel: document.querySelector("#scannerPanel"),
  scannerStatus: document.querySelector("#scannerStatus"),
  portalContents: document.querySelector("#portalContents"),
  portalGate: document.querySelector("#portalGate"),
  portalGateTitle: document.querySelector("#portalGateTitle"),
  portalGateText: document.querySelector("#portalGateText"),
  scanReveal: document.querySelector("#scanReveal"),
  scanVignette: document.querySelector("#scanVignette"),
  elementParticles: document.querySelector("#elementParticles"),
  scanRevealPortal: document.querySelector("#scanRevealPortal"),
  scanRevealElement: document.querySelector("#scanRevealElement"),
  scanRevealPortrait: document.querySelector("#scanRevealPortrait"),
  scanRevealLabel: document.querySelector("#scanRevealLabel"),
  scanRevealName: document.querySelector("#scanRevealName"),
  scanRevealLogoLeft: document.querySelector("#scanRevealLogoLeft"),
  scanRevealLogoRight: document.querySelector("#scanRevealLogoRight"),
  scanRevealDetails: document.querySelector("#scanRevealDetails"),
  scanRevealVillain: document.querySelector("#scanRevealVillain"),
  scanRevealVillainName: document.querySelector("#scanRevealVillainName"),
  backgroundMusic: document.querySelector("#backgroundMusic")
};

const desktopBridge = window.chrome?.webview;
let scannerRunning = false;
let revealTimeout = null;
const revealQueue = [];
let revealQueueTimer = null;
let portalCheckTimer = null;
let portalGateHideTimer = null;
let activeVariantChoiceToken = null;
const highlightedKeys = new Set();
const duplicateHighlightedKeys = new Set();
let currentSummonAudio = null;
let discoverySummonAudio = null;

function loadSettings() {
  try {
    const stored = JSON.parse(localStorage.getItem(settingsStorageKey) || "{}");
    return normalizeSettings({ ...defaultSettings, ...stored });
  } catch {
    return { ...defaultSettings };
  }
}

function normalizeSettings(settings) {
  return {
    musicEnabled: Boolean(settings.musicEnabled),
    musicVolume: clampPercent(settings.musicVolume),
    soundsEnabled: Boolean(settings.soundsEnabled),
    soundVolume: clampPercent(settings.soundVolume),
    scanAnimations: Boolean(settings.scanAnimations)
  };
}

function clampPercent(value) {
  const number = Number(value);
  if (!Number.isFinite(number)) return 0;
  return Math.max(0, Math.min(100, Math.round(number)));
}

function saveSettings() {
  try {
    localStorage.setItem(settingsStorageKey, JSON.stringify(state.settings));
  } catch {
    // Settings are still applied for this session if persistent storage is unavailable.
  }
}

function applySettings() {
  document.body.classList.toggle("reduce-scan-motion", !state.settings.scanAnimations);

  const music = els.backgroundMusic;
  if (!music) return;

  music.volume = state.settings.musicVolume / 100;
  if (!state.settings.musicEnabled || state.settings.musicVolume === 0) {
    music.pause();
  } else {
    music.play().catch(() => {});
  }
}

function startBackgroundMusic() {
  const music = els.backgroundMusic;
  if (!music) return;

  const playMusic = () => applySettings();

  playMusic();
  window.addEventListener("pointerdown", playMusic, { once: true });
  window.addEventListener("keydown", playMusic, { once: true });
}

function keyFor(toyId, variantId) {
  return `${Number(toyId)}:${Number(variantId)}`;
}

function baseCollectionKeyFor(item) {
  return item.physicalVariantId
    ? `physical:${item.physicalVariantId}`
    : `scan:${keyFor(item.toyId, item.variantId)}`;
}

function collectionKeyFor(item) {
  if (!item.physicalVariantId && item.figureUid) {
    return `uid:${keyFor(item.toyId, item.variantId)}:${normalize(item.figureUid)}`;
  }

  return baseCollectionKeyFor(item);
}

function formatDate(value) {
  if (!value) return "Not scanned";
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "medium",
    timeStyle: "short"
  }).format(new Date(value));
}

function formatFigureUid(value) {
  const text = String(value ?? "").replace(/[^a-f0-9]/gi, "").toUpperCase();
  return text ? text.match(/.{1,2}/g)?.join(":") ?? text : "Unknown";
}

function normalize(value) {
  return String(value ?? "").toLowerCase();
}

function withFallback(value, fallback = "None") {
  return value === null || value === undefined || value === "" ? fallback : value;
}

function itemTypes(item) {
  const values = [item.type, ...(Array.isArray(item.types) ? item.types : [])].filter(Boolean);
  const seen = new Set();
  return values.filter(value => {
    const key = normalize(value);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function itemTypeLabel(item) {
  const types = itemTypes(item);
  return types.length > 0 ? types.join(", ") : "None";
}

function itemPrimaryType(item) {
  return item.type || itemTypes(item)[0] || null;
}

function itemVariantLabels(item) {
  const types = itemTypes(item).map(normalize);
  const labels = [];
  if (types.some(type => type.includes("legendary"))) labels.push("Legendary");
  if (types.some(type => type.includes("dark"))) labels.push("Dark");
  if (types.some(type => type.includes("lightcore"))) labels.push("LightCore");
  if (types.some(type => type === "eons elite" || type === "eon's elite")) labels.push("Eon's Elite");
  if (types.some(type => type === "chase variant")) labels.push("Chase Variant");
  if (types.some(type => type === "special")) labels.push("Special");
  return labels.length > 0 ? labels : ["Standard"];
}

function supportsStatsDisplay(item) {
  if (!figureStatsEnabled) return false;

  const part = normalize(item?.swapPart);
  if (part === "bottom") return false;
  if (part === "top") return true;

  const characterTypes = new Set(["core", "giant", "lightcore", "sidekick", "trap master", "mini"]);
  return itemTypes(item).some(type => characterTypes.has(normalize(type)));
}

function figureNickname(item) {
  const nickname = item?.nickname || (figureStatsEnabled ? item?.stats?.nickname : null);
  return typeof nickname === "string" && nickname.trim() ? nickname.trim() : null;
}

function mergeItemTypes(...items) {
  const seen = new Set();
  return items.flatMap(itemTypes).filter(type => {
    const key = normalize(type);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}

function elementAssetName(element) {
  return typeSlug(element) || null;
}

function typeSlug(value) {
  return normalize(value).replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
}

function typeAssetName(type) {
  const normalized = typeSlug(type);
  if (normalized === "magic-item" || normalized === "magicitem") return "magic-item";
  if (normalized === "adventure-pack" || normalized === "expansion-pack" || normalized === "levelpiece") return "adventure-pack";
  if (normalized === "battlepiece") return "magic-item";
  return null;
}

function isMiscType(type) {
  const normalized = typeSlug(type);
  return [
    "magic-item",
    "magicitem",
    "adventure-pack",
    "expansion-pack",
    "levelpiece",
    "battlepiece"
  ].includes(normalized);
}

function itemIconAssetName(item) {
  return itemTypes(item).map(typeAssetName).find(Boolean) || elementAssetName(withFallback(item.element));
}

function isCharacterFigure(item) {
  return !itemTypes(item).some(typeAssetName) && Boolean(item.character || item.baseName || item.companionCharacter);
}

function isVillainItem(item) {
  return itemTypes(item).some(type => normalize(type) === "villain");
}

function discoveryLabelFor(item, isNew) {
  if (!isNew) return "Already in your vault";
  if (isVillainItem(item)) return "New villain discovered!";
  return isCharacterFigure(item) ? "New Skylander discovered!" : "New item discovered!";
}

function assetSlug(value) {
  return normalize(value)
    .replaceAll("'", "")
    .replace(/\s+/g, "-")
    .replace(/[^a-z0-9-]+/g, "")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
}

function gameLogoPath(game) {
  if (game === "All") {
    return "/assets/images/logos/logo.png";
  }

  const gameSlug = assetSlug(game);
  return gameSlug ? `/assets/images/logos/${gameSlug}.webp` : null;
}

function renderGameFilter(game) {
  const logoPath = gameLogoPath(game);
  const activeClass = game === state.game ? " active" : "";
  const allClass = game === "All" ? " is-all" : "";
  const logo = logoPath
    ? `<img class="game-filter-logo" src="${escapeHtml(logoPath)}" alt="" loading="lazy" onerror="this.hidden=true; this.nextElementSibling.className='game-filter-label';">`
    : "";
  const labelClass = logoPath ? "visually-hidden game-filter-label" : "game-filter-label";

  return `
    <button type="button" class="game-filter${activeClass}${allClass}" data-game="${escapeHtml(game)}" title="${escapeHtml(game)}">
      ${logo}
      <span class="${labelClass}">${escapeHtml(game)}</span>
    </button>
  `;
}

function portraitPathFor(item) {
  return portraitCandidatesFor(item)[0] || null;
}

function portraitCandidatesFor(item) {
  const isVillain = isVillainItem(item);
  const gameSlug = isVillain ? 'villains' : assetSlug(item.game);
  if (!gameSlug) {
    return [];
  }

  const catalogEntry = catalogEntryFor(item);
  const candidates = [
    item.portrait,
    catalogEntry?.portrait,
    item.id,
    catalogEntry?.id,
    item.variantOf,
    catalogEntry?.variantOf,
    item.name,
    item.baseName,
    item.character,
    item.companionCharacter
  ];

  const seen = new Set();
  return candidates
    .map(assetSlug)
    .filter(Boolean)
    .filter(slug => {
      if (seen.has(slug)) return false;
      seen.add(slug);
      return true;
    })
    .map(slug => `/assets/images/portraits/${gameSlug}/${slug}.webp`);
}

function applyImageFallback(img, candidates, onFailed) {
  const paths = [...candidates];
  const tryNext = () => {
    const next = paths.shift();
    if (!next) {
      onFailed?.();
      return;
    }

    img.src = next;
  };

  img.onerror = tryNext;
  tryNext();
}

function catalogEntryFor(item) {
  return state.catalog.find(row => baseCollectionKeyFor(row) === baseCollectionKeyFor(item));
}

function mergedCatalogEntry(item) {
  const catalogEntry = catalogEntryFor(item);
  return catalogEntry ? { ...catalogEntry, ...item, types: mergeItemTypes(item, catalogEntry) } : item;
}

function summonSoundSlugsFor(item) {
  const entry = mergedCatalogEntry(item);
  const candidates = [
    entry.id,
    entry.portrait,
    entry.name,
    entry.character,
    entry.companionCharacter,
    entry.baseName
  ];

  const seen = new Set();
  return candidates
    .map(assetSlug)
    .filter(Boolean)
    .filter(slug => {
      if (seen.has(slug)) return false;
      seen.add(slug);
      return true;
    });
}

async function playSummonSound(entry, options = {}) {
  if (!state.settings.soundsEnabled || state.settings.soundVolume === 0) return;

  const slugs = summonSoundSlugsFor(entry);
  const voiceCandidates = [];
  for (const slug of slugs) {
    voiceCandidates.push(`/assets/sounds/summon/${slug}.mp3`);
    voiceCandidates.push(`/assets/sounds/${slug}.mp3`);
  }

  // Helper to try playing one of the voice candidates immediately
  const tryPlayVoice = async () => {
    for (const src of voiceCandidates) {
      if (!src) continue;
      try {
        currentSummonAudio?.pause();
        const audio = new Audio(src);
        audio.volume = state.settings.soundVolume / 100;
        audio.addEventListener('ended', () => {
          if (currentSummonAudio === audio) currentSummonAudio = null;
        }, { once: true });
        await audio.play();
        currentSummonAudio = audio;
        return true;
      } catch (e) {
        // try next
      }
    }
    return false;
  };

  // If this is a new discovery, prefer to play the portal/discovery sound first,
  // then queue the character voice to play after the discovery sound ends.
  if (options.isNew) {
    const portalSrc = '/assets/sounds/portal_appear.mp3';
    try {
      discoverySummonAudio?.pause();
      discoverySummonAudio = new Audio(portalSrc);
      discoverySummonAudio.volume = state.settings.soundVolume / 100;
      discoverySummonAudio.addEventListener('ended', () => {
        discoverySummonAudio = null;
      }, { once: true });
      await discoverySummonAudio.play();
      // Start the character voice shortly after the portal sound begins
      const voiceDelay = typeof options.voiceDelay === 'number' ? options.voiceDelay : 450;
      setTimeout(() => tryPlayVoice(), voiceDelay);
      return;
    } catch (e) {
      discoverySummonAudio = null;
      // fall through to try voice immediately
    }
  }

  // Not a new discovery or portal sound failed - try voice immediately.
  await tryPlayVoice();
}

function renderElementLogo(element, className) {
  const assetName = elementAssetName(element);
  if (!assetName || assetName === "none") {
    return `<span class="${className} element-logo-fallback" data-element="${escapeHtml(element)}"></span>`;
  }

  return `<img class="${className}" src="/assets/images/elements/${escapeHtml(assetName)}.webp" alt="" loading="lazy">`;
}

function renderItemIcon(item, className) {
  const assetName = itemIconAssetName(item);
  const element = withFallback(item.element);
  if (!assetName || assetName === "none") {
    return `<span class="${className} element-logo-fallback" data-element="${escapeHtml(element)}"></span>`;
  }

  return `<img class="${className}" src="/assets/images/elements/${escapeHtml(assetName)}.webp" alt="" loading="lazy">`;
}

function renderCardMedia(item) {
  const element = withFallback(item.element);
  const icon = renderItemIcon(item, "card-element-logo card-fallback-icon");
  const portraitPaths = portraitCandidatesFor(item);

  if (portraitPaths.length === 0) {
    return icon;
  }

  const fallbackScript = "this.dataset.fallbackIndex=(Number(this.dataset.fallbackIndex||0)+1).toString();const paths=this.dataset.fallbackSrcs.split('|');if(Number(this.dataset.fallbackIndex)<paths.length){this.src=paths[Number(this.dataset.fallbackIndex)];}else{this.hidden=true;this.nextElementSibling.hidden=false;}";

  return `
    <img
      class="card-portrait"
      src="${escapeHtml(portraitPaths[0])}"
      data-fallback-srcs="${escapeHtml(portraitPaths.join("|"))}"
      data-fallback-index="0"
      alt=""
      loading="lazy"
      onerror="${escapeHtml(fallbackScript)}">
    <span class="card-fallback-media" data-element="${escapeHtml(element)}" hidden>${icon}</span>
  `;
}

async function loadJson(path) {
  const response = await fetch(path, { cache: "no-store" });
  if (!response.ok) throw new Error(`${path} returned ${response.status}`);
  return response.json();
}

async function loadData() {
  const [collection, upgrades, ...catalogParts] = await Promise.all([
    loadJson("../data/collection.json"),
    loadJson(upgradeCatalogFile).catch(() => null),
    ...catalogFiles.map(loadJson)
  ]);

  const rawCatalog = catalogParts.flatMap(part => Array.isArray(part) ? part : [part]);
  const manualVariantKeys = getManualVariantKeys(rawCatalog);
  state.catalog = rawCatalog.map(item => normalizeCatalogEntry(item, manualVariantKeys));
  state.catalog = enrichCatalogTypes(state.catalog);
  state.collection = enrichCollectionTypes(Array.isArray(collection) ? collection : [collection])
    .filter(item => villainsEnabled || !isVillainItem(item));
  state.ownedKeys = new Set(state.collection.map(baseCollectionKeyFor));
  if (!state.upgradeDirty || !state.upgrades) {
    state.upgrades = normalizeUpgradeCatalog(upgrades);
    if (!state.selectedUpgradeId) {
      state.selectedUpgradeId = state.upgrades.entries[0]?.id || "";
    }
  }

  setHeaderStatus("Collection loaded");
  buildFilters();
  render();
}

async function reloadCollectionAfterScan() {
  await loadData();
  state.view = "owned";
  updateTabs();
  render();
}

async function reloadCollectionAfterManualChange() {
  await loadData();
  updateTabs();
  render();
}

function getManualVariantKeys(catalog) {
  const standardKeys = new Set();
  const chaseKeys = new Set();

  for (const item of catalog) {
    const key = keyFor(item.toyId, item.variantId ?? 0);
    const isChase = itemTypes(item).some(type => normalize(type) === "chase variant");
    if (isChase) {
      chaseKeys.add(key);
    } else {
      standardKeys.add(key);
    }
  }

  return new Set([...chaseKeys].filter(key => standardKeys.has(key)));
}

function normalizeCatalogEntry(item, manualVariantKeys = new Set()) {
  const types = itemTypes(item);
  const chaseVariant = types.some(type => normalize(type) === "chase variant");
  const manualVariant = chaseVariant && manualVariantKeys.has(keyFor(item.toyId, item.variantId ?? 0));
  const physicalVariantId = manualVariant ? item.id : null;
  const baseName = item.character || item.companionCharacter || item.name;

  return {
    id: item.id,
    portrait: item.portrait,
    toyId: item.toyId,
    toyIdHex: item.toyIdHex,
    variantId: item.variantId ?? 0,
    variantIdHex: item.variantIdHex || "0x0000",
    physicalVariantId,
    name: item.name,
    character: item.character,
    baseName,
    companionCharacter: item.companionCharacter,
    swapPart: item.swapPart,
    halfName: item.halfName,
    game: item.game,
    element: item.element,
    type: item.type,
    types,
    variantOf: item.variantOf,
    characterKnown: true,
    variantKnown: true,
    physicalVariant: manualVariant,
    manualSelectable: manualVariant,
    scanNote: manualVariant ? "Chase variant; expected to scan as the standard figure." : null,
    owned: state.ownedKeys.has(manualVariant ? `physical:${physicalVariantId}` : `scan:${keyFor(item.toyId, item.variantId ?? 0)}`)
  };
}

function enrichCatalogTypes(catalog) {
  const byId = new Map(catalog.map(item => [item.id, item]));
  return catalog.map(item => {
    const base = item.variantOf ? byId.get(item.variantOf) : null;
    if (!base) return item;

    return {
      ...item,
      types: mergeItemTypes(item, base)
    };
  });
}

function enrichCollectionTypes(collection) {
  const catalogByCollectionKey = new Map(state.catalog.map(item => [baseCollectionKeyFor(item), item]));
  return collection.map(item => {
    const catalogEntry = catalogByCollectionKey.get(baseCollectionKeyFor(item));
    if (!catalogEntry) {
      return {
        ...item,
        types: itemTypes(item)
      };
    }

    return {
      ...catalogEntry,
      ...item,
      type: item.type || catalogEntry.type,
      types: mergeItemTypes(item, catalogEntry)
    };
  });
}

function normalizeUpgradeCatalog(catalog) {
  const base = catalog && typeof catalog === "object" ? catalog : {};
  const entries = Array.isArray(base.entries) ? base.entries : [];

  return {
    schemaVersion: base.schemaVersion || 1,
    imageRoot: base.imageRoot || "assets/images/upgrades",
    notes: base.notes || "",
    entries: entries.map(normalizeUpgradeEntry)
  };
}

function normalizeUpgradeEntry(entry) {
  const normalized = {
    id: entry.id || "",
    name: entry.name || "",
    character: entry.character || entry.id || "",
    variantOf: entry.variantOf || "",
    game: entry.game || "",
    type: entry.type,
    imageFolder: entry.imageFolder || `assets/images/upgrades/${entry.id || ""}`
  };

  if (Array.isArray(entry.parts)) {
    normalized.type = entry.type || "SWAP Force";
    normalized.parts = entry.parts.map(part => ({
      id: part.id || "",
      part: part.part || "",
      name: part.name || part.part || "",
      displayName: part.displayName || part.name || "",
      imageFolder: part.imageFolder || normalized.imageFolder,
      common: normalizeUpgradeList(part.common),
      paths: normalizeUpgradePaths(part.paths),
      extra: normalizeUpgradeList(part.extra)
    }));
    return normalized;
  }

  normalized.common = normalizeUpgradeList(entry.common);
  normalized.paths = normalizeUpgradePaths(entry.paths);
  normalized.extra = normalizeUpgradeList(entry.extra);
  return normalized;
}

function normalizeUpgradePaths(paths) {
  const normalized = Array.isArray(paths) ? paths.slice(0, 2) : [];
  while (normalized.length < 2) {
    normalized.push({ id: normalized.length === 0 ? "path-a" : "path-b", name: "", description: "", upgrades: [] });
  }

  return normalized.map((path, index) => ({
    id: path.id || (index === 0 ? "path-a" : "path-b"),
    name: path.name || "",
    description: path.description || "",
    upgrades: normalizeUpgradeList(path.upgrades)
  }));
}

function normalizeUpgradeList(upgrades) {
  return (Array.isArray(upgrades) ? upgrades : [])
    .map(upgrade => ({
      number: Number(upgrade.number) || 0,
      name: upgrade.name || "",
      description: upgrade.description || "",
      image: upgrade.image || ""
    }))
    .sort((a, b) => Number(a.number || 0) - Number(b.number || 0));
}

function buildFilters() {
  pruneActiveFilterFields();
  renderAdvancedFilters();
  renderFilterMatchSummary();
  applyFilterDrawerState();
}

function pruneActiveFilterFields() {
  state.activeFilterFields = state.activeFilterFields.filter((field, index, fields) =>
    advancedFilterDefinitions.some(definition => definition.field === field) &&
    fields.indexOf(field) === index);
}

function renderAdvancedFilters() {
  if (!els.advancedFilterRows) return;

  els.advancedFilterRows.innerHTML = state.activeFilterFields.map(field => renderAdvancedFilterRow(field)).join("");
  renderFilterFieldPicker();
}

function renderAdvancedFilterRow(field) {
  const definition = advancedFilterDefinitions.find(item => item.field === field);
  if (!definition) return "";

  const selected = getFilterFieldValue(field);
  const selectedMode = getFilterFieldMode(field);
  const options = getAvailableFilterOptions(field);
  const optionHtml = options.map(value => {
    const disabled = value !== selected && !filterOptionHasMatches(field, value, selectedMode);
    return `<option value="${escapeHtml(value)}"${value === selected ? " selected" : ""}${disabled ? " disabled" : ""}>${escapeHtml(value)}</option>`;
  }).join("");

  return `
    <div class="advanced-filter-row" data-filter-field="${escapeHtml(field)}">
      <label>
        <span>${escapeHtml(definition.label)}</span>
        <select data-advanced-filter-value="${escapeHtml(field)}">${optionHtml}</select>
      </label>
      <label class="filter-mode-field">
        <span>Mode</span>
        <select data-advanced-filter-mode="${escapeHtml(field)}">
          <option value="include"${selectedMode === "include" ? " selected" : ""}>Include</option>
          <option value="exclude"${selectedMode === "exclude" ? " selected" : ""}>Exclude</option>
        </select>
      </label>
      <button class="filter-remove" type="button" data-remove-filter="${escapeHtml(field)}" aria-label="Remove ${escapeHtml(definition.label)} filter">x</button>
    </div>
  `;
}

function renderFilterFieldPicker() {
  if (!els.filterFieldSelect || !els.addFilterButton) return;

  const available = advancedFilterDefinitions.filter(definition => !state.activeFilterFields.includes(definition.field));
  els.filterFieldSelect.innerHTML = available
    .map(definition => `<option value="${escapeHtml(definition.field)}">${escapeHtml(definition.label)}</option>`)
    .join("");
  els.addFilterButton.disabled = available.length === 0;
  els.filterFieldSelect.disabled = available.length === 0;
}

function getFilterFieldValue(field) {
  return field === "variant" ? state.variant : state[field] || "All";
}

function setFilterFieldValue(field, value) {
  if (field === "variant") {
    state.variant = value || "All";
    return;
  }
  if (["game", "type", "element"].includes(field)) {
    state[field] = value || "All";
  }
}

function isEditableTarget(target) {
  const element = target instanceof Element ? target : target?.parentElement;
  return Boolean(element?.closest("input, textarea, select, [contenteditable='true']"));
}

function initializeReleaseMode() {
  if (!releaseBuild) return;

  document.body.classList.add("release-build");
  document.querySelector('[data-view="upgrades"]')?.remove();
  els.tabs = Array.from(document.querySelectorAll(".tab"));

  document.addEventListener("contextmenu", event => {
    if (isEditableTarget(event.target)) return;
    event.preventDefault();
  });

  document.addEventListener("dragstart", event => {
    event.preventDefault();
  });

  document.addEventListener("selectstart", event => {
    if (isEditableTarget(event.target)) return;
    event.preventDefault();
  });
}

function getFilterFieldMode(field) {
  return state.filterModes?.[field] === "exclude" ? "exclude" : "include";
}

function setFilterFieldMode(field, mode) {
  if (!state.filterModes) {
    state.filterModes = {};
  }
  state.filterModes[field] = mode === "exclude" ? "exclude" : "include";
}

function clearFilterField(field) {
  setFilterFieldValue(field, "All");
  setFilterFieldMode(field, "include");
  state.activeFilterFields = state.activeFilterFields.filter(value => value !== field);
}

function getAvailableFilterOptions(field) {
  const rows = getFilterOptionSourceRows();
  const values = unique(rows.flatMap(item => valuesForFilterField(item, field)).filter(Boolean));
  const order = field === "game"
    ? gameOrder
    : field === "type"
      ? typeOrder
      : field === "element"
        ? elementOrder
        : ["Standard", "Legendary", "Dark", "LightCore", "Eon's Elite", "Special", "Chase Variant"];

  return ["All", ...sortByKnownOrder(values, order)];
}

function valuesForFilterField(item, field) {
  switch (field) {
    case "game":
      return [item.game];
    case "type":
      return itemTypes(item);
    case "variant":
      return itemVariantLabels(item);
    case "element":
      return [item.element];
    default:
      return [];
  }
}

function filterOptionHasMatches(field, value, mode = getFilterFieldMode(field)) {
  return getFilterOptionSourceRows().some(item => matchesStructuredFilters(item, { [field]: value }, {
    ignoreMissingView: true,
    modeOverrides: { [field]: mode }
  }));
}

function getFilterOptionSourceRows() {
  const collectionByKey = new Map(state.collection.map(item => [baseCollectionKeyFor(item), item]));
  return [
    ...state.catalog.map(item => {
      const owned = collectionByKey.get(baseCollectionKeyFor(item));
      return owned ? { ...item, ...owned, owned: true } : { ...item, owned: false };
    }),
    ...state.collection
      .filter(item => !state.catalog.some(catalog => baseCollectionKeyFor(catalog) === baseCollectionKeyFor(item)))
      .map(item => ({ ...item, owned: true }))
  ];
}

function matchesStructuredFilters(item, overrides = {}, options = {}) {
  const game = Object.prototype.hasOwnProperty.call(overrides, "game") ? overrides.game : state.game;
  const type = Object.prototype.hasOwnProperty.call(overrides, "type") ? overrides.type : state.type;
  const variant = Object.prototype.hasOwnProperty.call(overrides, "variant") ? overrides.variant : state.variant;
  const element = Object.prototype.hasOwnProperty.call(overrides, "element") ? overrides.element : state.element;
  const modeOverrides = options.modeOverrides || {};

  if (!matchesFilterValue([item.game], game, modeOverrides.game || getFilterFieldMode("game"))) return false;
  if (!matchesFilterValue(itemTypes(item), type, modeOverrides.type || getFilterFieldMode("type"))) return false;
  if (!matchesFilterValue(itemVariantLabels(item), variant, modeOverrides.variant || getFilterFieldMode("variant"))) return false;
  if (!matchesFilterValue([item.element], element, modeOverrides.element || getFilterFieldMode("element"))) return false;
  if (!options.ignoreMissingView && state.missingOnly && item.owned) return false;
  if (!options.ignoreMissingView && state.unknownOnly && item.characterKnown !== false && item.variantKnown !== false) return false;

  return true;
}

function matchesFilterValue(values, selected, mode) {
  if (!selected || selected === "All") return true;

  const hasValue = values.includes(selected);
  return mode === "exclude" ? !hasValue : hasValue;
}

function renderFilterMatchSummary() {
  if (!els.filterMatchSummary) return;

  const matching = getFilterOptionSourceRows()
    .filter(item => matchesTextFilter(item))
    .filter(item => matchesStructuredFilters(item, {}, { ignoreMissingView: true }))
    .filter(isEntryForFilterTotals);
  const total = matching.length;
  const owned = matching.filter(item => item.owned).length;
  els.filterMatchSummary.innerHTML = `<strong>${escapeHtml(String(owned))}</strong><span>/ ${escapeHtml(String(total))} figures</span>`;
}

function isEntryForFilterTotals(item) {
  if (!isKnownCollectionItem(item)) return false;
  return villainsEnabled || !isVillainItem(item);
}

function applyFilterDrawerState() {
  els.workspace?.classList.toggle("filters-collapsed", !state.filterDrawerOpen);
  els.filterDrawerToggle?.setAttribute("aria-expanded", String(state.filterDrawerOpen));
}

function unique(values) {
  return Array.from(new Set(values)).sort((a, b) => String(a).localeCompare(String(b)));
}

function fillSelect(select, values, selected) {
  select.innerHTML = values.map(value => (
    `<option value="${escapeHtml(value)}" ${value === selected ? "selected" : ""}>${escapeHtml(value)}</option>`
  )).join("");
}

function getRows() {
  const collectionByKey = new Map(state.collection.map(item => [baseCollectionKeyFor(item), item]));
  const catalogRows = state.catalog.map(item => {
    const owned = collectionByKey.get(baseCollectionKeyFor(item));
    return owned ? { ...item, ...owned, owned: true } : { ...item, owned: false };
  });

  const orphanRows = state.collection
    .filter(item => !state.catalog.some(catalog => baseCollectionKeyFor(catalog) === baseCollectionKeyFor(item)))
    .map(item => ({ ...item, owned: true }));

  const baseRows = state.view === "catalog"
    ? [...catalogRows, ...orphanRows]
    : state.collection.map(item => ({ ...item, owned: true }));

  return baseRows.filter(matchesFilters);
}

function matchesTextFilter(item) {
  const text = [
    item.name,
    item.baseName,
    item.game,
    item.element,
    item.type,
    itemTypeLabel(item),
    item.toyIdHex,
    item.variantIdHex,
    item.id,
    item.variantOf,
    item.companionCharacter,
    item.swapPart,
    item.halfName,
    item.figureUid,
    item.physicalVariantId,
    item.scanNote
  ].map(normalize).join(" ");

  return !state.search || text.includes(state.search);
}

function matchesFilters(item) {
  return matchesTextFilter(item) && matchesStructuredFilters(item);
}

function render() {
  if (releaseBuild && state.view === "upgrades") {
    state.view = "owned";
    updateTabs();
  }

  applyWorldTheme();

  renderSummaryStrip();
  renderFilterMatchSummary();

  if (state.view === "settings") {
    els.resultCount.textContent = "Settings";
    renderSettings();
    return;
  }

  if (state.view === "upgrades") {
    renderUpgradeEditor();
    return;
  }

  if (state.view === "swap-grid") {
    renderSwapGrid();
    return;
  }

  const rows = getRows();
  els.resultCount.textContent = `${rows.length} ${rows.length === 1 ? "result" : "results"}`;
  renderRows(rows);
}

function applyWorldTheme() {
  const themeGame = getFilterFieldMode("game") === "exclude" ? "All" : state.game || "All";
  document.body.dataset.game = assetSlug(themeGame) || "all";
}

function isKnownCollectionItem(item) {
  return item.characterKnown !== false && item.variantKnown !== false;
}

function isMiscCollectionItem(item) {
  return itemTypes(item).some(type => isMiscType(type) || typeAssetName(type));
}

function renderSummaryStrip() {
  const collected = state.collection.filter(isKnownCollectionItem);
  const elementCounts = Object.fromEntries(elementOrder
    .filter(element => element !== "None")
    .map(element => [element, 0]));
  let magicItemCount = 0;

  collected.forEach(item => {
    if (isMiscCollectionItem(item)) {
      magicItemCount++;
      return;
    }

    const element = withFallback(item.element);
    if (Object.prototype.hasOwnProperty.call(elementCounts, element)) {
      elementCounts[element]++;
    }
  });

  const cards = [
    renderSummaryCard("Total", collected.length, null),
    ...elementOrder
      .filter(element => element !== "None")
      .map(element => renderSummaryCard(element, elementCounts[element], element)),
    renderSummaryCard("Misc", magicItemCount, "Magic Items")
  ];

  els.summaryStrip.innerHTML = cards.join("");
}

function renderSummaryCard(label, count, icon) {
  const active = icon && (icon === state.element || (icon === "Magic Items" && isMiscType(state.type)));
  const element = icon && icon !== "Magic Items" ? icon : "None";
  const iconHtml = icon === "Magic Items"
    ? `<img class="summary-icon" src="/assets/images/elements/magic-item.webp" alt="" loading="lazy">`
    : icon
      ? renderElementLogo(icon, "summary-icon")
      : `<span class="summary-icon summary-total-icon">#</span>`;

  return `
    <div class="summary-item${active ? " is-active" : ""}" data-element="${escapeHtml(element)}" title="${escapeHtml(label)}: ${escapeHtml(String(count))}">
      <span class="summary-count">${count}</span>
      <div class="summary-rune" aria-label="${escapeHtml(label)}">${iconHtml}</div>
    </div>
  `;
}

function renderRows(rows) {
  if (rows.length === 0) {
    els.collectionGrid.innerHTML = `<div class="empty-state">No matching entries</div>`;
    return;
  }

  const sortedRows = sortRows(rows);

  if (state.groupBy === "none") {
    els.collectionGrid.innerHTML = sortedRows.map(renderCard).join("");
    return;
  }

  const groupedRows = sortedRows.reduce((groups, item) => {
    const group = getGroupLabel(item);
    if (!groups.has(group)) groups.set(group, []);
    groups.get(group).push(item);
    return groups;
  }, new Map());

  els.collectionGrid.innerHTML = Array.from(groupedRows.entries())
    .map(([group, items]) => renderGroup(group, items))
    .join("");
}

function renderUpgradeEditor() {
  const catalog = state.upgrades;
  const entries = catalog?.entries || [];
  if (entries.length === 0) {
    els.resultCount.textContent = "Upgrades";
    els.collectionGrid.innerHTML = `<div class="empty-state">No upgrade catalog found</div>`;
    return;
  }

  if (!entries.some(entry => entry.id === state.selectedUpgradeId)) {
    state.selectedUpgradeId = entries[0].id;
  }

  const entry = getSelectedUpgradeEntry();
  const dirtyText = state.upgradeDirty ? "Unsaved changes" : "Upgrades";
  els.resultCount.textContent = dirtyText;
  els.collectionGrid.innerHTML = `
    <section class="upgrade-editor" aria-label="Upgrade editor">
      <div class="upgrade-editor-toolbar">
        <label class="upgrade-picker">
          <span>Figure</span>
          <select id="upgradeEntrySelect">
            ${entries.map(item => `<option value="${escapeHtml(item.id)}"${item.id === entry.id ? " selected" : ""}>${escapeHtml(upgradeEntryLabel(item))}</option>`).join("")}
          </select>
        </label>
        <label class="upgrade-picker upgrade-copy-picker">
          <span>Load from</span>
          <select id="upgradeCopySourceSelect">
            ${entries
              .filter(item => item.id !== entry.id)
              .map(item => `<option value="${escapeHtml(item.id)}">${escapeHtml(upgradeEntryLabel(item))}</option>`)
              .join("")}
          </select>
        </label>
        <button id="loadUpgradePath" class="upgrade-load" type="button">Load path</button>
        <button id="saveUpgradeCatalog" class="upgrade-save" type="button"${desktopBridge ? "" : " disabled"}>${state.upgradeDirty ? "Save changes" : "Saved"}</button>
      </div>
      ${entry.parts ? renderSwapUpgradeEditor(entry) : renderNormalUpgradeEditor(entry)}
    </section>
  `;
}

function upgradeEntryLabel(entry) {
  const label = entry.name || entry.character || entry.id;
  return entry.game ? `${label} - ${entry.game}` : label;
}

function getSelectedUpgradeEntry() {
  return state.upgrades?.entries?.find(entry => entry.id === state.selectedUpgradeId)
    || state.upgrades?.entries?.[0]
    || null;
}

function renderNormalUpgradeEditor(entry) {
  return `
    <div class="upgrade-flow">
      ${renderUpgradeSection(entry, null, "common", "Main path", entry.common)}
      <div class="upgrade-split">
        ${entry.paths.map((path, index) => renderUpgradePathSection(entry, null, path, index)).join("")}
      </div>
      ${renderUpgradeSection(entry, null, "extra", "Merged path", entry.extra || [])}
    </div>
  `;
}

function renderSwapUpgradeEditor(entry) {
  return `
    <div class="upgrade-parts">
      ${entry.parts.map((part, index) => `
        <section class="upgrade-part">
          <header>
            <h2>${escapeHtml(part.displayName || part.name || part.part)}</h2>
            <small>${escapeHtml(part.imageFolder || "")}</small>
          </header>
          <div class="upgrade-flow">
            ${renderUpgradeSection(entry, index, "common", "Main path", part.common)}
            <div class="upgrade-split">
              ${part.paths.map((path, pathIndex) => renderUpgradePathSection(entry, index, path, pathIndex)).join("")}
            </div>
            ${renderUpgradeSection(entry, index, "extra", "Merged path", part.extra || [])}
          </div>
        </section>
      `).join("")}
    </div>
  `;
}

function renderUpgradePathSection(entry, partIndex, path, pathIndex) {
  return `
    <section class="upgrade-section upgrade-path-section">
      <header class="upgrade-section-head">
        <div>
          <h2>${escapeHtml(pathIndex === 0 ? "Path A" : "Path B")}</h2>
          <input class="upgrade-path-title" data-upgrade-path-field="name" data-part-index="${partIndex ?? ""}" data-path-index="${pathIndex}" value="${escapeHtml(path.name || "")}" placeholder="Path name">
        </div>
        <button class="upgrade-add" type="button" data-upgrade-add="path" data-part-index="${partIndex ?? ""}" data-path-index="${pathIndex}">Add</button>
      </header>
      <textarea class="upgrade-path-description" data-upgrade-path-field="description" data-part-index="${partIndex ?? ""}" data-path-index="${pathIndex}" placeholder="Path description">${escapeHtml(path.description || "")}</textarea>
      <div class="upgrade-list">
        ${(path.upgrades || []).map((upgrade, index) => renderUpgradeItem(entry, partIndex, "path", index, upgrade, pathIndex)).join("") || `<p class="upgrade-empty">No upgrades yet</p>`}
      </div>
    </section>
  `;
}

function renderUpgradeSection(entry, partIndex, section, label, upgrades) {
  return `
    <section class="upgrade-section">
      <header class="upgrade-section-head">
        <div>
          <h2>${escapeHtml(label)}</h2>
          <small>${escapeHtml(getUpgradeImageFolder(entry, partIndex))}</small>
        </div>
        <button class="upgrade-add" type="button" data-upgrade-add="${escapeHtml(section)}" data-part-index="${partIndex ?? ""}">Add</button>
      </header>
      <div class="upgrade-list">
        ${(upgrades || []).map((upgrade, index) => renderUpgradeItem(entry, partIndex, section, index, upgrade)).join("") || `<p class="upgrade-empty">No upgrades yet</p>`}
      </div>
    </section>
  `;
}

function renderUpgradeItem(entry, partIndex, section, index, upgrade, pathIndex = "") {
  const image = upgrade.image ? `/${upgrade.image}` : "";
  const imageHtml = image
    ? `<img src="${escapeHtml(image)}" alt="" loading="lazy">`
    : `<span>${escapeHtml(String(upgrade.number || "?"))}</span>`;

  return `
    <article class="upgrade-item" data-upgrade-section="${escapeHtml(section)}" data-part-index="${partIndex ?? ""}" data-path-index="${pathIndex}" data-item-index="${index}">
      <div class="upgrade-preview">${imageHtml}</div>
      <label>
        <span>Image</span>
        <input type="number" min="0" step="1" value="${escapeHtml(String(upgrade.number || ""))}" data-upgrade-field="number">
      </label>
      <label>
        <span>Name</span>
        <input type="text" value="${escapeHtml(upgrade.name || "")}" data-upgrade-field="name" placeholder="Upgrade name">
      </label>
      <label class="upgrade-description-field">
        <span>Description</span>
        <textarea data-upgrade-field="description" placeholder="Upgrade description">${escapeHtml(upgrade.description || "")}</textarea>
      </label>
      <button class="upgrade-remove" type="button" data-upgrade-remove>Remove</button>
    </article>
  `;
}

function getUpgradeImageFolder(entry, partIndex = null) {
  if (partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]) {
    return entry.parts[Number(partIndex)].imageFolder || entry.imageFolder;
  }

  return entry.imageFolder || `${state.upgrades?.imageRoot || "assets/images/upgrades"}/${entry.id}`;
}

function getUpgradeTarget(entry, partIndex, section, pathIndex = null) {
  const root = partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]
    ? entry.parts[Number(partIndex)]
    : entry;

  if (section === "path") {
    return root.paths?.[Number(pathIndex)]?.upgrades || [];
  }

  if (!Array.isArray(root[section])) {
    root[section] = [];
  }

  return root[section];
}

function getUpgradePath(entry, partIndex, pathIndex) {
  const root = partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]
    ? entry.parts[Number(partIndex)]
    : entry;
  return root.paths?.[Number(pathIndex)] || null;
}

function findUpgradeFromElement(element) {
  const item = element.closest(".upgrade-item");
  const entry = getSelectedUpgradeEntry();
  if (!item || !entry) return null;

  const list = getUpgradeTarget(entry, item.dataset.partIndex, item.dataset.upgradeSection, item.dataset.pathIndex);
  const upgrade = list[Number(item.dataset.itemIndex)];
  return { entry, item, list, upgrade };
}

function markUpgradeDirty() {
  state.upgradeDirty = true;
  els.resultCount.textContent = "Unsaved changes";
  const save = document.querySelector("#saveUpgradeCatalog");
  if (save) save.textContent = "Save changes";
}

function getAllUpgradesForRoot(root) {
  return [
    root.common,
    root.extra,
    ...(root.paths || []).map(path => path.upgrades)
  ].flatMap(list => Array.isArray(list) ? list : []);
}

function extensionForUpgradeNumber(entry, partIndex, number) {
  const root = partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]
    ? entry.parts[Number(partIndex)]
    : entry;
  const image = getAllUpgradesForRoot(root)
    .find(upgrade => Number(upgrade.number) === Number(number) && upgrade.image)?.image;
  const match = String(image || "").match(/\.[a-z0-9]+$/i);
  return match ? match[0].toLowerCase() : null;
}

function extensionForUpgradeFolder(entry, partIndex) {
  const root = partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]
    ? entry.parts[Number(partIndex)]
    : entry;
  const image = getAllUpgradesForRoot(root)
    .map(upgrade => upgrade.image)
    .find(Boolean);
  const match = String(image || "").match(/\.[a-z0-9]+$/i);
  return match ? match[0].toLowerCase() : ".jpg";
}

function setUpgradeImageFromNumber(entry, partIndex, upgrade) {
  const number = Number(upgrade.number) || 0;
  const folder = getUpgradeImageFolder(entry, partIndex);
  const extension = extensionForUpgradeNumber(entry, partIndex, number) || extensionForUpgradeFolder(entry, partIndex);
  upgrade.image = number > 0 ? `${folder}/${number}${extension}` : "";
}

function addUpgrade(section, partIndex = "", pathIndex = "") {
  const entry = getSelectedUpgradeEntry();
  if (!entry) return;

  const list = getUpgradeTarget(entry, partIndex, section, pathIndex);
  const root = partIndex !== null && partIndex !== "" && entry.parts?.[Number(partIndex)]
    ? entry.parts[Number(partIndex)]
    : entry;
  const allNumbers = getAllUpgradesForRoot(root).map(upgrade => Number(upgrade.number) || 0);
  const upgrade = {
    number: Math.max(0, ...allNumbers) + 1,
    name: "",
    description: "",
    image: ""
  };

  setUpgradeImageFromNumber(entry, partIndex, upgrade);
  list.push(upgrade);
  list.sort((a, b) => Number(a.number || 0) - Number(b.number || 0));
  markUpgradeDirty();
  renderUpgradeEditor();
}

function removeUpgrade(button) {
  const found = findUpgradeFromElement(button);
  if (!found) return;
  found.list.splice(Number(found.item.dataset.itemIndex), 1);
  markUpgradeDirty();
  renderUpgradeEditor();
}

function copyUpgradeText(source, target) {
  if (!source || !target) return false;

  target.name = source.name || "";
  target.description = source.description || "";
  return true;
}

function copyUpgradeListText(sourceList = [], targetList = []) {
  const sourceByNumber = new Map(sourceList.map(upgrade => [Number(upgrade.number), upgrade]));
  let changed = false;

  for (const target of targetList) {
    const source = sourceByNumber.get(Number(target.number));
    if (!source) continue;
    copyUpgradeText(source, target);
    changed = true;
  }

  return changed;
}

function copyUpgradeRootText(source, target) {
  if (!source || !target) return false;

  let changed = false;
  changed = copyUpgradeListText(source.common, target.common) || changed;
  changed = copyUpgradeListText(source.extra, target.extra) || changed;

  for (let index = 0; index < Math.min(source.paths?.length || 0, target.paths?.length || 0); index++) {
    const sourcePath = source.paths[index];
    const targetPath = target.paths[index];
    targetPath.name = sourcePath.name || "";
    targetPath.description = sourcePath.description || "";
    changed = true;
    changed = copyUpgradeListText(sourcePath.upgrades, targetPath.upgrades) || changed;
  }

  return changed;
}

function loadUpgradePathFrom(sourceId) {
  const target = getSelectedUpgradeEntry();
  const source = state.upgrades?.entries?.find(entry => entry.id === sourceId);
  if (!target || !source || target.id === source.id) return;

  let changed = false;
  if (source.parts && target.parts) {
    for (let index = 0; index < target.parts.length; index++) {
      const targetPart = target.parts[index];
      const sourcePart = source.parts.find(part => normalize(part.part) === normalize(targetPart.part))
        || source.parts[index];
      changed = copyUpgradeRootText(sourcePart, targetPart) || changed;
    }
  } else if (!source.parts && !target.parts) {
    changed = copyUpgradeRootText(source, target);
  } else {
    alert("Those upgrade entries use different layouts, so their paths cannot be loaded into each other.");
    return;
  }

  if (changed) {
    markUpgradeDirty();
    renderUpgradeEditor();
    setHeaderStatus(`Loaded upgrade path from ${upgradeEntryLabel(source)}.`);
  }
}

function saveUpgradeCatalog() {
  if (!desktopBridge || !state.upgrades) return;
  desktopBridge.postMessage({
    type: "saveUpgradeCatalog",
    catalog: state.upgrades
  });
  setHeaderStatus("Saving upgrade editor...");
}

function renderSettings() {
  const resetDisabled = desktopBridge ? "" : " disabled";
  const musicChecked = state.settings.musicEnabled ? " checked" : "";
  const soundsChecked = state.settings.soundsEnabled ? " checked" : "";
  const animationsChecked = state.settings.scanAnimations ? " checked" : "";
  const cleanupHtml = debugFeaturesEnabled
    ? `<button id="removeUidlessDuplicates" type="button" class="settings-cleanup"${resetDisabled}>Remove no-UID duplicates</button>`
    : "";
  const debugSettingsHtml = debugFeaturesEnabled ? `
      <div class="settings-card">
        <h2>Diagnostics</h2>
        <p>Reads all NFC blocks from the next figure and saves a debug file without writing to the toy.</p>
        <button id="requestFigureDump" type="button" class="settings-dump"${desktopBridge ? "" : " disabled"}>Dump next figure</button>
      </div>

      <div class="settings-card">
        <h2>Scan Injection</h2>
        <label class="settings-select">
          <span>Figure</span>
          <select id="scanInjectSelect"${desktopBridge ? "" : " disabled"}>
            ${renderInjectionOptions()}
          </select>
        </label>
        <button id="injectSelectedScan" type="button" class="inject-scan settings-inject"${desktopBridge ? "" : " disabled"}>Inject scan</button>
      </div>

      <div class="settings-card">
        <h2>SWAP Injection</h2>
        <label class="settings-select">
          <span>Top half</span>
          <select id="swapInjectTopSelect"${desktopBridge ? "" : " disabled"}>
            ${renderSwapInjectionOptions("top")}
          </select>
        </label>
        <label class="settings-select">
          <span>Bottom half</span>
          <select id="swapInjectBottomSelect"${desktopBridge ? "" : " disabled"}>
            ${renderSwapInjectionOptions("bottom")}
          </select>
        </label>
        <button id="injectSwapScan" type="button" class="inject-scan settings-inject"${desktopBridge ? "" : " disabled"}>Inject SWAP scan</button>
      </div>
` : "";

  els.collectionGrid.innerHTML = `
    <section class="settings-panel" aria-label="Settings">
      <div class="settings-card">
        <h2>Audio</h2>
        <label class="settings-toggle">
          <input type="checkbox" data-setting="musicEnabled"${musicChecked}>
          <span>Background music</span>
        </label>
        <label class="settings-slider">
          <span>Music volume <output data-setting-output="musicVolume">${state.settings.musicVolume}%</output></span>
          <input type="range" min="0" max="100" step="1" value="${state.settings.musicVolume}" data-setting="musicVolume">
        </label>
        <label class="settings-toggle">
          <input type="checkbox" data-setting="soundsEnabled"${soundsChecked}>
          <span>Summon sounds</span>
        </label>
        <label class="settings-slider">
          <span>Summon volume <output data-setting-output="soundVolume">${state.settings.soundVolume}%</output></span>
          <input type="range" min="0" max="100" step="1" value="${state.settings.soundVolume}" data-setting="soundVolume">
        </label>
      </div>

      <div class="settings-card">
        <h2>Display</h2>
        <label class="settings-toggle">
          <input type="checkbox" data-setting="scanAnimations"${animationsChecked}>
          <span>Scan animations</span>
        </label>
      </div>

      ${debugSettingsHtml}

      <div class="settings-card danger-zone">
        <h2>Collection</h2>
        <p>Reset removes every scanned and manually marked figure from this vault.</p>
        ${cleanupHtml}
        <button id="resetCollection" type="button" class="danger settings-reset"${resetDisabled}>Reset collection</button>
      </div>
    </section>
  `;
}

function renderInjectionOptions() {
  return [...state.catalog]
    .filter(item => normalize(item.swapPart) !== "top" && normalize(item.swapPart) !== "bottom")
    .sort((a, b) =>
      String(a.game || "").localeCompare(String(b.game || "")) ||
      String(a.name || "").localeCompare(String(b.name || "")) ||
      Number(a.variantId || 0) - Number(b.variantId || 0))
    .map(item => {
      const id = item.id || `${item.toyId}:${item.variantId}`;
      const label = `${item.name} - ${item.game || "Unlisted"} - ${item.toyIdHex || item.toyId}:${item.variantIdHex || item.variantId}`;
      return `<option value="${escapeHtml(id)}">${escapeHtml(label)}</option>`;
    })
    .join("");
}

function renderSwapInjectionOptions(part) {
  return [...state.catalog]
    .filter(item => normalize(item.swapPart) === part)
    .sort((a, b) =>
      String(a.character || a.name || "").localeCompare(String(b.character || b.name || "")) ||
      Number(a.variantId || 0) - Number(b.variantId || 0) ||
      String(a.name || "").localeCompare(String(b.name || "")))
    .map(item => {
      const id = item.id || `${item.toyId}:${item.variantId}`;
      const label = `${item.name} - ${item.toyIdHex || item.toyId}:${item.variantIdHex || item.variantId}`;
      return `<option value="${escapeHtml(id)}">${escapeHtml(label)}</option>`;
    })
    .join("");
}

function findCatalogEntryById(id) {
  return state.catalog.find(item => (item.id || `${item.toyId}:${item.variantId}`) === id);
}

function getInjectionEntries(item) {
  if (!item) return [];

  const part = normalize(item.swapPart);
  if (part !== "top" && part !== "bottom") {
    return [item];
  }

  const otherPart = part === "top" ? "bottom" : "top";
  const character = normalize(item.character || item.baseName || item.variantOf);
  const counterpart = state.catalog.find(candidate =>
    normalize(candidate.swapPart) === otherPart &&
    normalize(candidate.character || candidate.baseName || candidate.variantOf) === character &&
    Number(candidate.variantId ?? 0) === Number(item.variantId ?? 0));

  return [item, counterpart]
    .filter(Boolean)
    .sort((a, b) => swapPartRank(a.swapPart) - swapPartRank(b.swapPart));
}

function injectCatalogScan(item) {
  if (!desktopBridge || !item) return;

  const entries = getInjectionEntries(item);
  injectEntries(entries, item.name);
}

function injectSwapScan(top, bottom) {
  if (!desktopBridge || !top || !bottom) return;

  const entries = [top, bottom].sort((a, b) => swapPartRank(a.swapPart) - swapPartRank(b.swapPart));
  const topName = top.halfName || top.name || "Top";
  const bottomName = bottom.halfName || bottom.name || "Bottom";
  injectEntries(entries, `${topName} ${bottomName}`);
}

function injectEntries(entries, label) {
  if (!desktopBridge || !entries.length) return;

  desktopBridge.postMessage({
    type: "injectScan",
    entries: entries.map(entry => ({
      toyId: entry.toyId,
      toyIdHex: entry.toyIdHex,
      variantId: entry.variantId ?? 0,
      variantIdHex: entry.variantIdHex || "0x0000"
    }))
  });

  setHeaderStatus(`Injecting ${label}...`);
}

function swapVariantTag(item) {
  const id = item.id || "";
  if (id.startsWith("dark-")) return "Dark";
  if (id.startsWith("legendary-")) return "Lgnd";
  if (id.startsWith("jade-")) return "Jade";
  if (id.startsWith("enchanted-")) return "Ench";
  if (id.startsWith("nitro-")) return "Nitro";
  if (id.startsWith("gold-")) return "Gold";
  if (id.startsWith("color-shift-")) return "CS";
  if (id.startsWith("silver-")) return "Slvr";
  if (id.startsWith("bronze-")) return "Brnz";
  if (id.startsWith("quickdraw-")) return "QD";
  return null;
}

function swapEntryKey(item) {
  return `${item.toyId}:${item.variantId ?? 0}`;
}

function getSwapGridData() {
  // Find the standard representative per toyId (lowest variantId)
  const standardTopReps = new Map();
  const standardBotReps = new Map();
  for (const item of state.catalog) {
    if (item.swapPart !== "top" && item.swapPart !== "bottom") continue;
    const repMap = item.swapPart === "top" ? standardTopReps : standardBotReps;
    const existing = repMap.get(item.toyId);
    if (!existing || (item.variantId ?? 0) < (existing.variantId ?? 0)) {
      repMap.set(item.toyId, item);
    }
  }

  // Build owned lookup keyed by toyId:variantId
  const catalogByCKey = new Map(state.catalog.map(x => [baseCollectionKeyFor(x), x]));
  const ownedTopByKey = new Map();
  const ownedBotByKey = new Map();
  for (const entry of state.collection) {
    const cat = catalogByCKey.get(baseCollectionKeyFor(entry));
    if (!cat) continue;
    const k = swapEntryKey(cat);
    if (cat.swapPart === "top") ownedTopByKey.set(k, cat);
    if (cat.swapPart === "bottom") ownedBotByKey.set(k, cat);
  }

  // Rows = standard reps always; owned non-standard variants added on top
  const topRows = new Map();
  for (const rep of standardTopReps.values()) topRows.set(swapEntryKey(rep), rep);
  for (const [k, entry] of ownedTopByKey) if (!topRows.has(k)) topRows.set(k, entry);

  const botCols = new Map();
  for (const rep of standardBotReps.values()) botCols.set(swapEntryKey(rep), rep);
  for (const [k, entry] of ownedBotByKey) if (!botCols.has(k)) botCols.set(k, entry);

  const tops = [...topRows.values()].sort((a, b) => a.toyId - b.toyId || (a.variantId ?? 0) - (b.variantId ?? 0));
  const bots = [...botCols.values()].sort((a, b) => a.toyId - b.toyId || (a.variantId ?? 0) - (b.variantId ?? 0));

  return { tops, bots, ownedTopKeys: new Set(ownedTopByKey.keys()), ownedBotKeys: new Set(ownedBotByKey.keys()) };
}

function renderSwapGrid() {
  const { tops, bots, ownedTopKeys, ownedBotKeys } = getSwapGridData();

  const ownedTopCount = tops.filter(t => ownedTopKeys.has(swapEntryKey(t))).length;
  const ownedBotCount = bots.filter(b => ownedBotKeys.has(swapEntryKey(b))).length;
  let ownedCombos = 0;
  for (const top of tops) {
    for (const bot of bots) {
      if (ownedTopKeys.has(swapEntryKey(top)) && ownedBotKeys.has(swapEntryKey(bot))) ownedCombos++;
    }
  }
  const totalCombos = tops.length * bots.length;

  els.resultCount.textContent = `${ownedCombos} / ${totalCombos} combinations`;

  const headerRow = `<tr>
    <th class="swap-grid-corner"><span class="swap-grid-corner-label">Top \\ Bot</span></th>
    ${bots.map(bot => {
      const pp = portraitPathFor(bot);
      const img = pp ? `<img src="${escapeHtml(pp)}" alt="" loading="lazy">` : "";
      const owned = ownedBotKeys.has(swapEntryKey(bot));
      const tag = swapVariantTag(bot);
      const title = (tag ? tag + " " : "") + (bot.halfName || bot.name || "");
      return `<th class="swap-grid-col-head${owned ? " is-owned" : ""}" title="${escapeHtml(title)}">${img}<span class="swap-grid-half-name">${escapeHtml(bot.halfName || "?")}</span>${tag ? `<span class="swap-grid-variant-tag">${escapeHtml(tag)}</span>` : ""}</th>`;
    }).join("")}
  </tr>`;

  const bodyRows = tops.map(top => {
    const topOwned = ownedTopKeys.has(swapEntryKey(top));
    const pp = portraitPathFor(top);
    const img = pp ? `<img src="${escapeHtml(pp)}" alt="" loading="lazy">` : "";
    const topTag = swapVariantTag(top);

    const cells = bots.map(bot => {
      const owned = topOwned && ownedBotKeys.has(swapEntryKey(bot));
      const botTag = swapVariantTag(bot);
      const title = `${topTag ? topTag + " " : ""}${top.halfName || "?"} + ${botTag ? botTag + " " : ""}${bot.halfName || "?"}`;
      return `<td class="swap-grid-cell${owned ? " is-owned" : ""}" title="${escapeHtml(title)}">${owned ? `<span class="swap-grid-check" aria-label="Owned">✓</span>` : ""}</td>`;
    }).join("");

    return `<tr>
      <th class="swap-grid-row-head${topOwned ? " is-owned" : ""}">
        <div class="swap-grid-row-head-inner">${img}<div class="swap-grid-row-head-text"><span class="swap-grid-half-name">${escapeHtml(top.halfName || "?")}</span>${topTag ? `<span class="swap-grid-variant-tag">${escapeHtml(topTag)}</span>` : ""}</div></div>
      </th>
      ${cells}
    </tr>`;
  }).join("");

  els.collectionGrid.innerHTML = `
    <div class="swap-grid-panel">
      <div class="swap-grid-stats">
        <span class="swap-grid-stat"><strong>${ownedCombos}</strong> / ${totalCombos} combinations</span>
        <span class="swap-grid-stat"><strong>${ownedTopCount}</strong> / ${tops.length} tops owned</span>
        <span class="swap-grid-stat"><strong>${ownedBotCount}</strong> / ${bots.length} bottoms owned</span>
      </div>
      <div class="swap-grid-scroll">
        <table class="swap-grid-table">
          <thead>${headerRow}</thead>
          <tbody>${bodyRows}</tbody>
        </table>
      </div>
    </div>
  `;
}

function getGroupLabel(item) {
  switch (state.groupBy) {
    case "game":
      return withFallback(item.game);
    case "type":
      return withFallback(itemPrimaryType(item));
    case "element":
    default:
      return withFallback(item.element);
  }
}

function renderGroup(group, items) {
  let icon = `<span class="group-badge">${escapeHtml(group.charAt(0).toUpperCase())}</span>`;
  if (state.groupBy === "element") {
    icon = renderElementLogo(group, "element-logo");
  } else if (state.groupBy === "type") {
    const typeAsset = typeAssetName(group);
    if (typeAsset) {
      icon = `<img class="element-logo" src="/assets/images/elements/${escapeHtml(typeAsset)}.webp" alt="" loading="lazy">`;
    }
  }

  return `
    <section class="element-group">
      <div class="element-heading">
        ${icon}
        <h2>${escapeHtml(group)}</h2>
        <span>${items.length}</span>
      </div>
      <div class="element-group-grid">
        ${items.map(renderCard).join("")}
      </div>
    </section>
  `;
}

function renderCard(item) {
  const known = item.characterKnown !== false && item.variantKnown !== false;
  const element = withFallback(item.element);
  const collectionKey = collectionKeyFor(item);
  const highlightClass = highlightedKeys.has(collectionKey) ? " is-newly-scanned" : "";
  const duplicateHighlightClass = duplicateHighlightedKeys.has(collectionKey) ? " is-duplicate-scanned" : "";
  const typeSlug = itemTypes(item).map(assetSlug).join(" ") || "none";
  const gameSlug = assetSlug(item.game || "All") || "all";
  const nickname = figureNickname(item);
  const displayName = nickname || item.name || "Unknown Skylander";
  const displayNameLength = Array.from(displayName).length;
  const displayNameSize = Math.max(14, Math.min(30, 30 - Math.max(0, displayNameLength - 12) * 0.7));

  return `
    <article class="sky-card${highlightClass}${duplicateHighlightClass}" data-collection-key="${escapeHtml(collectionKey)}" data-element="${escapeHtml(element)}" data-game="${escapeHtml(gameSlug)}" data-type="${escapeHtml(typeSlug)}">
      <button class="sky-card-token" type="button" data-card-detail-key="${escapeHtml(collectionKey)}" aria-label="Open details for ${escapeHtml(displayName)}">
        <div class="card-hero-medallion">
          <div class="element-mark" data-element="${escapeHtml(element)}">
            ${renderCardMedia(item)}
          </div>
          <div class="card-element-badge" data-element="${escapeHtml(element)}">
            ${renderItemIcon(item, "card-element-badge-logo")}
          </div>
        </div>
        <div class="sky-name">
          <h3 style="font-size:${displayNameSize.toFixed(1)}px">${escapeHtml(displayName)}</h3>
          ${nickname ? `<p class="sky-base-name">${escapeHtml(item.name)}</p>` : ""}
        </div>
      </button>
    </article>
  `;
}

function renderCardDetailContent(item) {
  const known = item.characterKnown !== false && item.variantKnown !== false;
  const statusClass = item.owned ? (known ? "" : "warn") : "missing";
  const status = item.owned ? (known ? "Owned" : "Catalog") : (item.manualSelectable ? "Manual" : "Missing");
  const element = withFallback(item.element);
  const dateLabel = item.entrySource === "manual" ? "Confirmed" : "Discovered";
  const dateValue = item.entrySource === "manual" ? item.manuallyMarkedAt || item.lastScannedAt : item.lastScannedAt;
  const typeTags = itemTypes(item).map(type => `<span class="meta-tag type-tag">${escapeHtml(type)}</span>`).join("");
  const stats = supportsStatsDisplay(item) ? item.stats : null;
  const nickname = figureNickname(item);
  const nicknameStatHtml = nickname
    ? `<span class="figure-stat figure-stat-nickname" title="Nickname">Nickname: ${escapeHtml(nickname)}</span>`
    : "";
  const figureStatHtml = stats ? `
      <span class="figure-stat figure-stat-level" title="Level">
        <svg class="stat-icon" viewBox="0 0 16 16" fill="none" aria-hidden="true"><polygon points="8,1 10.5,6 16,6.9 12,10.8 13,16 8,13.2 3,16 4,10.8 0,6.9 5.5,6" fill="currentColor"/></svg>
        Lv.&nbsp;${escapeHtml(String(stats.level ?? "?"))}
      </span>
      <span class="figure-stat figure-stat-gold" title="Gold">
        <svg class="stat-icon" viewBox="0 0 16 16" aria-hidden="true"><circle cx="8" cy="8" r="7" fill="currentColor"/><text x="8" y="12" text-anchor="middle" font-size="10" font-weight="bold" fill="#7a4e00">G</text></svg>
        ${escapeHtml(String(stats.gold ?? "?"))}
      </span>
  ` : "";
  const statsHtml = nicknameStatHtml || figureStatHtml ? `
    <div class="figure-stats">
      ${nicknameStatHtml}
      ${figureStatHtml}
    </div>
  ` : "";

  return `
    <div class="card-detail-heading">
      <span class="owned-pill ${statusClass}">${status}</span>
      <h2>${escapeHtml(nickname || item.name || "Unknown Skylander")}</h2>
      ${nickname ? `<p>${escapeHtml(item.name)}</p>` : ""}
    </div>
    ${statsHtml}
    <div class="meta-tags">
      <span class="meta-tag element-tag">${escapeHtml(element)}</span>
      ${typeTags || `<span class="meta-tag type-tag">${escapeHtml(itemTypeLabel(item))}</span>`}
      <span class="meta-tag date-tag"><small>${escapeHtml(dateLabel)}</small>${escapeHtml(formatDate(dateValue))}</span>
    </div>
    <details class="card-details" open>
      <summary>Details</summary>
      <div class="id-row">
        <span class="id-chip">${escapeHtml(item.toyIdHex || `#${item.toyId}`)}</span>
        <span class="id-chip">${escapeHtml(item.variantIdHex || `v${item.variantId}`)}</span>
        ${item.figureUid ? `<span class="id-chip">UID ${escapeHtml(formatFigureUid(item.figureUid))}</span>` : ""}
        ${nickname ? `<span class="id-chip">Nickname ${escapeHtml(nickname)}</span>` : ""}
        ${item.physicalVariant ? `<span class="id-chip">Manual variant</span>` : ""}
      </div>
      ${item.scanNote ? `<p class="scan-note">${escapeHtml(item.scanNote)}</p>` : ""}
    </details>
    ${renderCardAction(item)}
  `;
}

function renderCardAction(item) {
  if (!desktopBridge) return "";

  const actions = [];

  if (debugFeaturesEnabled) {
    const injectId = item.id || `${item.toyId}:${item.variantId}`;
    actions.push(`
      <button
        type="button"
        class="inject-scan"
        data-inject-id="${escapeHtml(injectId)}">
        Inject scan
      </button>
    `);
  }

  if (item.owned && !item.manualSelectable) {
    actions.push(`
      <button
        type="button"
        class="collection-remove"
        data-toy-id="${escapeHtml(item.toyId)}"
        data-variant-id="${escapeHtml(item.variantId)}"
        data-physical-id="${escapeHtml(item.physicalVariantId || "")}"
        data-figure-uid="${escapeHtml(item.figureUid || "")}"
        data-name="${escapeHtml(item.name || "Entry")}">
        Remove entry
      </button>
    `);
  }

  if (item.manualSelectable) {
    const action = item.owned ? "remove" : "add";
    const label = item.owned ? "Remove manual entry" : "Mark owned";
    actions.push(`
      <button
        type="button"
        class="manual-toggle ${item.owned ? "is-owned" : ""}"
        data-action="${action}"
        data-physical-id="${escapeHtml(item.physicalVariantId)}">
        ${escapeHtml(label)}
      </button>
    `);
  }

  return `<div class="card-actions">${actions.join("")}</div>`;
}

function findCardItemByCollectionKey(collectionKey) {
  return [...state.collection, ...state.catalog].find(item => collectionKeyFor(item) === collectionKey) || null;
}

function ensureCardDetailMenu() {
  let menu = document.querySelector("#cardDetailMenu");
  if (menu) return menu;

  menu = document.createElement("section");
  menu.id = "cardDetailMenu";
  menu.className = "card-detail-menu";
  menu.hidden = true;
  document.body.appendChild(menu);
  return menu;
}

function showCardDetailMenu(item) {
  if (!item) return;

  const menu = ensureCardDetailMenu();
  const element = withFallback(item.element);
  const gameSlug = assetSlug(item.game || "All") || "all";
  menu.dataset.element = element;
  menu.dataset.game = gameSlug;
  menu.hidden = false;
  menu.innerHTML = `
    <button type="button" class="card-detail-backdrop" aria-label="Close details"></button>
    <div class="card-detail-dialog" role="dialog" aria-modal="true" aria-label="${escapeHtml(item.name || "Skylander details")}">
      <button type="button" class="card-detail-close" aria-label="Close details">×</button>
      ${renderCardDetailContent(item)}
    </div>
  `;
}

function hideCardDetailMenu() {
  const menu = document.querySelector("#cardDetailMenu");
  if (menu) {
    menu.hidden = true;
    menu.innerHTML = "";
  }
}

function ensureVariantChoicePrompt() {
  let prompt = document.querySelector("#variantChoicePrompt");
  if (prompt) return prompt;

  prompt = document.createElement("section");
  prompt.id = "variantChoicePrompt";
  prompt.className = "variant-choice-prompt";
  prompt.setAttribute("aria-live", "polite");
  prompt.hidden = true;
  document.body.appendChild(prompt);
  return prompt;
}

function showVariantChoicePrompt(message) {
  if (!desktopBridge || !message.choiceToken || !Array.isArray(message.choices) || message.choices.length === 0) {
    return;
  }

  activeVariantChoiceToken = message.choiceToken;
  const prompt = ensureVariantChoicePrompt();
  prompt.hidden = false;
  prompt.innerHTML = `
    <div class="variant-choice-backdrop"></div>
    <div class="variant-choice-dialog" role="dialog" aria-modal="true" aria-labelledby="variantChoiceTitle">
      <div class="variant-choice-heading">
        <span>Scan match</span>
        <h2 id="variantChoiceTitle">Which figure is on the portal?</h2>
        <p>${escapeHtml(message.text || "This portal ID matches more than one physical figure.")}</p>
      </div>
      <div class="variant-choice-list">
        ${message.choices.map(renderVariantChoice).join("")}
      </div>
      <button type="button" class="variant-choice-cancel">Cancel scan</button>
    </div>
  `;
}

function renderVariantChoice(choice) {
  const element = withFallback(choice.element);
  const portraitPaths = portraitCandidatesFor(choice);
  const portraitPath = portraitPaths[0];
  const selectedId = choice.id || choice.physicalVariantId;
  const typeLabel = itemTypeLabel(choice);
  const note = choice.physicalVariantId ? "Physical chase variant" : "Standard scanned figure";
  const fallbackScript = "this.dataset.fallbackIndex=(Number(this.dataset.fallbackIndex||0)+1).toString();const paths=this.dataset.fallbackSrcs.split('|');if(Number(this.dataset.fallbackIndex)<paths.length){this.src=paths[Number(this.dataset.fallbackIndex)];}else{this.hidden=true;this.nextElementSibling.hidden=false;}";

  return `
    <button type="button" class="variant-choice-option" data-selected-id="${escapeHtml(selectedId)}" data-element="${escapeHtml(element)}">
      <span class="variant-choice-media">
        ${portraitPath
          ? `<img src="${escapeHtml(portraitPath)}" data-fallback-srcs="${escapeHtml(portraitPaths.join("|"))}" data-fallback-index="0" alt="" loading="lazy" onerror="${escapeHtml(fallbackScript)}">`
          : ""}
        <span ${portraitPath ? "hidden" : ""}>${renderItemIcon(choice, "variant-choice-icon")}</span>
      </span>
      <span class="variant-choice-copy">
        <strong>${escapeHtml(choice.name || "Unknown figure")}</strong>
        <small>${escapeHtml([choice.game, element, typeLabel].filter(Boolean).join(" - "))}</small>
        <em>${escapeHtml(note)}</em>
      </span>
    </button>
  `;
}

function hideVariantChoicePrompt() {
  const prompt = document.querySelector("#variantChoicePrompt");
  if (prompt) {
    prompt.hidden = true;
    prompt.innerHTML = "";
  }
  activeVariantChoiceToken = null;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function sortRows(rows) {
  return [...rows].sort((a, b) => {
    return compareByGroup(a, b)
      || String(a.baseName || a.name).localeCompare(String(b.baseName || b.name))
      || compareByKnownOrder(withFallback(a.element), withFallback(b.element), elementOrder)
      || compareByKnownOrder(withFallback(itemPrimaryType(a)), withFallback(itemPrimaryType(b)), typeOrder)
      || compareByKnownOrder(withFallback(a.game), withFallback(b.game), gameOrder)
      || Number(a.variantId || 0) - Number(b.variantId || 0)
      || Number(a.toyId || 0) - Number(b.toyId || 0);
  });
}

function compareByGroup(a, b) {
  switch (state.groupBy) {
    case "game":
      return compareByKnownOrder(withFallback(a.game), withFallback(b.game), gameOrder);
    case "type":
      return compareByKnownOrder(withFallback(itemPrimaryType(a)), withFallback(itemPrimaryType(b)), typeOrder);
    case "none":
      return 0;
    case "element":
    default:
      return compareByKnownOrder(withFallback(a.element), withFallback(b.element), elementOrder);
  }
}

function sortByKnownOrder(values, order) {
  return [...values].sort((a, b) => compareByKnownOrder(a, b, order));
}

function compareByKnownOrder(a, b, order) {
  const aIndex = order.indexOf(a);
  const bIndex = order.indexOf(b);
  const safeA = aIndex === -1 ? Number.MAX_SAFE_INTEGER : aIndex;
  const safeB = bIndex === -1 ? Number.MAX_SAFE_INTEGER : bIndex;

  return safeA - safeB || String(a).localeCompare(String(b));
}

initializeReleaseMode();

els.searchInput.addEventListener("input", event => {
  state.search = normalize(event.target.value);
  render();
});

els.filterDrawerToggle?.addEventListener("click", () => {
  state.filterDrawerOpen = !state.filterDrawerOpen;
  applyFilterDrawerState();
});

els.addFilterButton?.addEventListener("click", () => {
  const field = els.filterFieldSelect?.value;
  if (!field || state.activeFilterFields.includes(field)) return;

  state.activeFilterFields.push(field);
  setFilterFieldMode(field, "include");
  const firstAvailable = getAvailableFilterOptions(field).find(value => value !== "All" && filterOptionHasMatches(field, value));
  setFilterFieldValue(field, firstAvailable || "All");
  buildFilters();
  render();
});

els.advancedFilterRows?.addEventListener("change", event => {
  const modeSelect = event.target.closest("[data-advanced-filter-mode]");
  if (modeSelect) {
    setFilterFieldMode(modeSelect.dataset.advancedFilterMode, modeSelect.value);
    buildFilters();
    render();
    return;
  }

  const select = event.target.closest("[data-advanced-filter-value]");
  if (!select) return;

  setFilterFieldValue(select.dataset.advancedFilterValue, select.value);
  buildFilters();
  render();
});

els.advancedFilterRows?.addEventListener("click", event => {
  const removeButton = event.target.closest("[data-remove-filter]");
  if (!removeButton) return;

  clearFilterField(removeButton.dataset.removeFilter);
  buildFilters();
  render();
});

els.groupByFilter.addEventListener("change", event => {
  state.groupBy = event.target.value;
  render();
});

els.tabs.forEach(tab => {
  tab.addEventListener("click", () => {
    if (releaseBuild && tab.dataset.view === "upgrades") return;
    state.view = tab.dataset.view;
    if (state.view === "owned") {
      state.missingOnly = false;
    }
    updateTabs();
    render();
  });
});

els.collectionGrid.addEventListener("input", event => {
  if (releaseBuild && event.target.closest("[data-upgrade-field], [data-upgrade-path-field]")) {
    return;
  }

  const upgradeField = event.target.closest("[data-upgrade-field]");
  if (upgradeField) {
    const found = findUpgradeFromElement(upgradeField);
    if (!found?.upgrade) return;

    const field = upgradeField.dataset.upgradeField;
    if (field === "number") {
      found.upgrade.number = Math.max(0, Math.round(Number(upgradeField.value) || 0));
      setUpgradeImageFromNumber(found.entry, found.item.dataset.partIndex, found.upgrade);
      const preview = found.item.querySelector(".upgrade-preview img");
      if (preview && found.upgrade.image) {
        preview.src = `/${found.upgrade.image}`;
      }
    } else {
      found.upgrade[field] = upgradeField.value;
    }
    markUpgradeDirty();
    return;
  }

  const pathField = event.target.closest("[data-upgrade-path-field]");
  if (pathField) {
    const entry = getSelectedUpgradeEntry();
    const path = entry ? getUpgradePath(entry, pathField.dataset.partIndex, pathField.dataset.pathIndex) : null;
    if (!path) return;
    path[pathField.dataset.upgradePathField] = pathField.value;
    markUpgradeDirty();
    return;
  }

  updateSettingFromControl(event.target.closest("[data-setting]"));
});

els.collectionGrid.addEventListener("change", event => {
  const selector = event.target.closest("#upgradeEntrySelect");
  if (selector) {
    if (releaseBuild) return;
    state.selectedUpgradeId = selector.value;
    renderUpgradeEditor();
    return;
  }

  updateSettingFromControl(event.target.closest("[data-setting]"));
});

document.addEventListener("click", event => {
  const detailToken = event.target.closest(".sky-card-token");
  if (detailToken) {
    const item = findCardItemByCollectionKey(detailToken.dataset.cardDetailKey);
    showCardDetailMenu(item);
    return;
  }

  if (event.target.closest(".card-detail-close, .card-detail-backdrop")) {
    hideCardDetailMenu();
    return;
  }

  const resetButton = event.target.closest("#resetCollection");
  if (resetButton) {
    if (!desktopBridge || resetButton.disabled) return;
    if (!confirm("Reset the entire collection? This cannot be undone.")) return;
    desktopBridge.postMessage({ type: "resetCollection" });
    setHeaderStatus("Resetting collection...");
    return;
  }

  const saveUpgradeButton = event.target.closest("#saveUpgradeCatalog");
  if (saveUpgradeButton) {
    if (releaseBuild) return;
    if (!desktopBridge || saveUpgradeButton.disabled) return;
    saveUpgradeCatalog();
    return;
  }

  const loadUpgradeButton = event.target.closest("#loadUpgradePath");
  if (loadUpgradeButton) {
    if (releaseBuild) return;
    const sourceId = document.querySelector("#upgradeCopySourceSelect")?.value;
    const source = state.upgrades?.entries?.find(entry => entry.id === sourceId);
    const target = getSelectedUpgradeEntry();
    if (!source || !target) return;
    if (!confirm(`Load upgrade names and descriptions from ${upgradeEntryLabel(source)} into ${upgradeEntryLabel(target)}?`)) return;
    loadUpgradePathFrom(sourceId);
    return;
  }

  const upgradeAddButton = event.target.closest("[data-upgrade-add]");
  if (upgradeAddButton) {
    if (releaseBuild) return;
    addUpgrade(
      upgradeAddButton.dataset.upgradeAdd,
      upgradeAddButton.dataset.partIndex,
      upgradeAddButton.dataset.pathIndex
    );
    return;
  }

  const upgradeRemoveButton = event.target.closest("[data-upgrade-remove]");
  if (upgradeRemoveButton) {
    if (releaseBuild) return;
    removeUpgrade(upgradeRemoveButton);
    return;
  }

  const cleanupButton = event.target.closest("#removeUidlessDuplicates");
  if (cleanupButton) {
    if (!debugFeaturesEnabled || !desktopBridge || cleanupButton.disabled) return;
    desktopBridge.postMessage({ type: "removeUidlessDuplicates" });
    setHeaderStatus("Removing no-UID duplicates...");
    return;
  }

  const dumpButton = event.target.closest("#requestFigureDump");
  if (dumpButton) {
    if (!debugFeaturesEnabled || !desktopBridge || dumpButton.disabled) return;
    desktopBridge.postMessage({ type: "requestFigureDump" });
    setHeaderStatus("Figure dump armed...");
    return;
  }

  const removeButton = event.target.closest(".collection-remove");
  if (removeButton) {
    if (!desktopBridge || removeButton.disabled) return;
    const name = removeButton.dataset.name || "this entry";
    if (!confirm(`Remove ${name} from your collection?`)) return;

    desktopBridge.postMessage({
      type: "removeCollectionEntry",
      entry: {
        toyId: Number(removeButton.dataset.toyId),
        variantId: Number(removeButton.dataset.variantId),
        physicalVariantId: removeButton.dataset.physicalId || null,
        figureUid: removeButton.dataset.figureUid || null
      }
    });
    setHeaderStatus(`Removing ${name}...`);
    return;
  }

  const injectButton = event.target.closest(".inject-scan");
  if (injectButton) {
    if (!debugFeaturesEnabled || !desktopBridge || injectButton.disabled) return;

    if (injectButton.id === "injectSwapScan") {
      const top = findCatalogEntryById(document.querySelector("#swapInjectTopSelect")?.value);
      const bottom = findCatalogEntryById(document.querySelector("#swapInjectBottomSelect")?.value);
      injectSwapScan(top, bottom);
      return;
    }

    const selectedId = injectButton.dataset.injectId || document.querySelector("#scanInjectSelect")?.value;
    const item = findCatalogEntryById(selectedId);
    injectCatalogScan(item);
    return;
  }

  const button = event.target.closest(".manual-toggle");
  if (!button || !desktopBridge) return;

  const item = state.catalog.find(row => row.physicalVariantId === button.dataset.physicalId);
  if (!item) return;

  desktopBridge.postMessage({
    type: "setManualOwnership",
    owned: button.dataset.action === "add",
    entry: {
      toyId: item.toyId,
      toyIdHex: item.toyIdHex,
      variantId: item.variantId,
      variantIdHex: item.variantIdHex,
      physicalVariantId: item.physicalVariantId,
      name: item.name,
      baseName: item.baseName,
      game: item.game,
      element: item.element,
      type: item.type,
      types: itemTypes(item),
      id: item.id,
      portrait: item.portrait,
      scanNote: item.scanNote
    }
  });

  setHeaderStatus(button.dataset.action === "add"
    ? `Adding ${item.name}...`
    : `Removing ${item.name}...`);
});

document.addEventListener("click", event => {
  const option = event.target.closest(".variant-choice-option");
  if (option && desktopBridge && activeVariantChoiceToken) {
    desktopBridge.postMessage({
      type: "resolveScanVariant",
      choiceToken: activeVariantChoiceToken,
      selectedId: option.dataset.selectedId
    });
    setHeaderStatus("Saving selected variant...");
    hideVariantChoicePrompt();
    return;
  }

  if (event.target.closest(".variant-choice-cancel") && desktopBridge && activeVariantChoiceToken) {
    desktopBridge.postMessage({
      type: "cancelScanVariant",
      choiceToken: activeVariantChoiceToken
    });
    hideVariantChoicePrompt();
  }
});

function updateSettingFromControl(control) {
  if (!control) return;

  const key = control.dataset.setting;
  if (!(key in state.settings)) return;

  state.settings[key] = control.type === "checkbox"
    ? control.checked
    : clampPercent(control.value);

  saveSettings();
  applySettings();
  updateSettingsReadouts();
}

function updateSettingsReadouts() {
  els.collectionGrid.querySelectorAll("[data-setting-output]").forEach(output => {
    const key = output.dataset.settingOutput;
    output.textContent = `${state.settings[key]}%`;
  });
}

function triggerVignette(element) {
  const el = els.scanVignette;
  if (!el || document.body.classList.contains('reduce-scan-motion')) return;
  el.dataset.element = element;
  el.classList.remove('is-active');
  void el.offsetWidth;
  el.classList.add('is-active');
}

const PARTICLE_ELEMENTS = new Set([
  'adventure-pack',
  'air',
  'dark',
  'earth',
  'fire',
  'life',
  'light',
  'magic',
  'magic-item',
  'tech',
  'undead',
  'water'
]);

function spawnElementParticles(...elements) {
  const container = els.elementParticles;
  if (!container) return;
  container.innerHTML = '';
  if (document.body.classList.contains('reduce-scan-motion')) return;

  const slugs = elements.map(elementAssetName).filter(s => s && PARTICLE_ELEMENTS.has(s));
  if (slugs.length === 0) return;

  const count = 22;
  for (let i = 0; i < count; i++) {
    const slug = slugs[i % slugs.length];
    const img = document.createElement('img');
    img.src = `/assets/images/elements/${slug}-simple.webp`;
    img.className = 'element-particle';
    img.dataset.element = slug.charAt(0).toUpperCase() + slug.slice(1);
    img.style.setProperty('--x', `${5 + Math.random() * 90}%`);
    img.style.setProperty('--size', `${14 + Math.random() * 24}px`);
    img.style.setProperty('--rotation', `${(Math.random() - 0.5) * 80}deg`);
    img.style.setProperty('--drift', `${(Math.random() - 0.5) * 110}px`);
    img.style.setProperty('--delay', `${Math.random() * 1800}ms`);
    img.style.setProperty('--duration', `${2600 + Math.random() * 1800}ms`);
    container.appendChild(img);
  }
}

function clearElementParticles() {
  if (els.elementParticles) els.elementParticles.innerHTML = '';
}

// Queue scan reveal animations so they play sequentially rather than instantly
// replacing each other. New discoveries play for their full 5200ms. Already-scanned
// figures play for 2500ms — long enough to read, short enough not to pile up.
// Multiple pending already-scanned entries are collapsed to the most recent one so
// the queue never grows unbounded when many figures are detected at once.
function enqueueReveal(fn, hold, isNew) {
  if (!isNew) {
    for (let i = revealQueue.length - 1; i >= 0; i--) {
      if (!revealQueue[i].isNew) revealQueue.splice(i, 1);
    }
  }
  revealQueue.push({ fn, hold, isNew });
  if (revealQueueTimer === null && revealQueue.length === 1) {
    drainRevealQueue();
  }
}

function drainRevealQueue() {
  revealQueueTimer = null;
  if (revealQueue.length === 0) return;
  const { fn, hold } = revealQueue.shift();
  fn();
  revealQueueTimer = window.setTimeout(drainRevealQueue, hold);
}

function clearRevealQueue() {
  revealQueue.length = 0;
  if (revealQueueTimer !== null) {
    window.clearTimeout(revealQueueTimer);
    revealQueueTimer = null;
  }
}

function showScanReveal(entry, options = {}) {
  if (!entry || !els.scanReveal) return;
  clearSwapReveal();
  clearRevealVillain();

  const resolvedEntry = mergedCatalogEntry(entry);
  const isNew = options.isNew !== false;
  const element = withFallback(resolvedEntry.element);
  const nickname = figureNickname(resolvedEntry);
  triggerVignette(element);
  const iconAsset = itemIconAssetName(resolvedEntry);
  const portraitPath = portraitPathFor(resolvedEntry);
  const key = collectionKeyFor(resolvedEntry);
  const highlightSet = isNew ? highlightedKeys : duplicateHighlightedKeys;

  playSummonSound(resolvedEntry, { isNew });

  highlightSet.add(key);
  window.setTimeout(() => {
    highlightSet.delete(key);
    document.querySelector(`[data-collection-key="${CSS.escape(key)}"]`)?.classList.remove(isNew ? "is-newly-scanned" : "is-duplicate-scanned");
  }, 4200);

  els.scanRevealPortal.dataset.element = element;
  const gameSlug = assetSlug(resolvedEntry.game || "All") || "all";
  els.scanReveal.dataset.game = gameSlug;
  els.scanRevealPortal.dataset.game = gameSlug;
  if (els.scanRevealLabel) {
    els.scanRevealLabel.textContent = discoveryLabelFor(resolvedEntry, isNew);
  }
  els.scanRevealName.textContent = nickname || resolvedEntry.name || "Unknown Skylander";
  els.scanRevealDetails.textContent = [
    nickname ? resolvedEntry.name : null,
    resolvedEntry.game,
    element === "None" ? null : element
  ].filter(Boolean).join(" - ");

  const villain = villainsEnabled ? resolvedEntry.trappedVillain : null;
  if (els.scanRevealVillain) {
    if (villain && villain.id !== 0) {
      els.scanRevealVillain.hidden = false;
      const baseName = villain.name || `Unknown villain (ID: ${villain.id})`;
      els.scanRevealVillainName.textContent = villain.evolved ? `${baseName} (Evolved)` : baseName;
    } else {
      clearRevealVillain();
    }
  }

  if (iconAsset && iconAsset !== "none") {
    els.scanRevealElement.hidden = false;
    els.scanRevealElement.src = `/assets/images/elements/${iconAsset}.webp`;
  } else {
    els.scanRevealElement.hidden = true;
    els.scanRevealElement.removeAttribute("src");
  }

  if (portraitPath) {
    els.scanRevealPortrait.hidden = false;
    applyImageFallback(els.scanRevealPortrait, portraitCandidatesFor(resolvedEntry), () => {
      els.scanRevealPortrait.hidden = true;
      els.scanRevealPortrait.removeAttribute("src");
    });
  } else {
    els.scanRevealPortrait.hidden = true;
    els.scanRevealPortrait.removeAttribute("src");
  }

  // Set nameplate element logos (left and right)
  try {
    const elementAsset = iconAsset && iconAsset !== "none" ? iconAsset : null;
    if (els.scanRevealLogoLeft) {
      if (elementAsset) {
        els.scanRevealLogoLeft.hidden = false;
        els.scanRevealLogoLeft.src = `/assets/images/elements/${elementAsset}.webp`;
        els.scanRevealLogoLeft.alt = element;
      } else {
        els.scanRevealLogoLeft.hidden = true;
        els.scanRevealLogoLeft.removeAttribute('src');
      }
    }

    if (els.scanRevealLogoRight) {
      if (elementAsset) {
        els.scanRevealLogoRight.hidden = false;
        els.scanRevealLogoRight.src = `/assets/images/elements/${elementAsset}.webp`;
        els.scanRevealLogoRight.alt = element;
      } else {
        els.scanRevealLogoRight.hidden = true;
        els.scanRevealLogoRight.removeAttribute('src');
      }
    }
  } catch (e) {
    // ignore asset errors
  }

  const centerReveal = isNew && state.settings.scanAnimations;

  if (centerReveal) {
    els.scanReveal.classList.add('is-center');
    spawnElementParticles(iconAsset || element);
  } else {
    els.scanReveal.classList.remove('is-center');
    clearElementParticles();
  }

  els.scanReveal.hidden = false;
  els.scanReveal.classList.toggle("is-duplicate", !isNew);
  els.scanReveal.classList.remove("is-warning");
  els.scanReveal.classList.remove("is-swap-combo");
  els.scanReveal.classList.remove("is-swap");
  els.scanReveal.classList.remove("is-playing");
  void els.scanReveal.offsetWidth;
  els.scanReveal.classList.add("is-playing");

  window.clearTimeout(revealTimeout);
  revealTimeout = window.setTimeout(() => {
    els.scanReveal.classList.remove("is-playing");
    if (centerReveal) {
      els.scanReveal.classList.remove('is-center');
    }
    els.scanReveal.hidden = true;
    clearElementParticles();
  }, 5200);
}

function clearSwapReveal() {
  els.scanReveal?.querySelector(".swap-reveal-halves")?.remove();
}

function clearRevealVillain() {
  if (els.scanRevealVillain) {
    els.scanRevealVillain.hidden = true;
  }
  if (els.scanRevealVillainName) {
    els.scanRevealVillainName.textContent = "";
  }
}

function showSwapReveal(entries, options = {}) {
  if (!Array.isArray(entries) || entries.length === 0 || !els.scanReveal) return;

  clearSwapReveal();
  clearRevealVillain();

  const resolvedEntries = entries
    .map(mergedCatalogEntry)
    .sort((a, b) => swapPartRank(a.swapPart) - swapPartRank(b.swapPart));
  const top = resolvedEntries.find(entry => normalize(entry.swapPart) === "top") || resolvedEntries[0];
  const bottom = resolvedEntries.find(entry => normalize(entry.swapPart) === "bottom") || resolvedEntries[1] || resolvedEntries[0];
  const isNew = options.isNew !== false;
  const element = withFallback(top?.element || bottom?.element);
  triggerVignette(element);
  const gameSlug = assetSlug(top?.game || bottom?.game || "All") || "all";
  const swapPortraitPath = swapCombinationPortraitPath(top, bottom);

  playSummonSound(top, { isNew });

  for (const entry of resolvedEntries) {
    const key = collectionKeyFor(entry);
    const highlightSet = isNew ? highlightedKeys : duplicateHighlightedKeys;
    highlightSet.add(key);
    window.setTimeout(() => {
      highlightSet.delete(key);
      document.querySelector(`[data-collection-key="${CSS.escape(key)}"]`)?.classList.remove(isNew ? "is-newly-scanned" : "is-duplicate-scanned");
    }, 4200);
  }

  els.scanReveal.dataset.game = gameSlug;
  els.scanRevealPortal.dataset.game = gameSlug;
  els.scanRevealPortal.dataset.element = element;
  els.scanRevealLabel.textContent = isNew ? "New SWAP Force discovered!" : "Already in your vault";
  els.scanRevealName.textContent = formatSwapRevealName(top, bottom);
  els.scanRevealDetails.textContent = [top?.game || bottom?.game, "Top + Bottom"].filter(Boolean).join(" - ");
  setSwapNameplateLogos(top, bottom);

  els.scanRevealElement.hidden = true;
  els.scanRevealElement.removeAttribute("src");
  els.scanRevealPortrait.hidden = !swapPortraitPath;
  if (swapPortraitPath) {
    els.scanRevealPortrait.src = swapPortraitPath;
    els.scanRevealPortrait.onerror = () => {
      els.scanReveal.classList.remove("is-swap-combo");
      els.scanRevealPortrait.hidden = true;
      els.scanRevealPortrait.removeAttribute("src");
      showSwapRevealHalves(top, bottom);
    };
  } else {
    els.scanRevealPortrait.removeAttribute("src");
  }

  if (!swapPortraitPath) {
    showSwapRevealHalves(top, bottom);
  }

  const centerReveal = isNew && state.settings.scanAnimations;

  if (centerReveal) {
    spawnElementParticles(withFallback(top?.element), withFallback(bottom?.element));
  } else {
    clearElementParticles();
  }

  els.scanReveal.classList.remove("is-warning");
  els.scanReveal.classList.toggle("is-center", centerReveal);
  els.scanReveal.classList.toggle("is-duplicate", !isNew);
  els.scanReveal.classList.toggle("is-swap-combo", Boolean(swapPortraitPath));
  els.scanReveal.classList.add("is-swap");
  els.scanReveal.hidden = false;
  els.scanReveal.classList.remove("is-playing");
  void els.scanReveal.offsetWidth;
  els.scanReveal.classList.add("is-playing");

  window.clearTimeout(revealTimeout);
  revealTimeout = window.setTimeout(() => {
    els.scanReveal.classList.remove("is-playing", "is-swap", "is-swap-combo", "is-duplicate", "is-center");
    clearSwapReveal();
    els.scanReveal.hidden = true;
    clearElementParticles();
  }, 5200);
}

function setSwapNameplateLogos(top, bottom) {
  const topElement = withFallback(top?.element);
  const bottomElement = withFallback(bottom?.element || top?.element);
  const topSlug = elementAssetName(topElement);
  const bottomSlug = elementAssetName(bottomElement);

  const setLogo = (logo, slug, label) => {
    if (!logo) return;
    if (slug) {
      logo.hidden = false;
      logo.src = `/assets/images/elements/${slug}.webp`;
      logo.alt = label;
    } else {
      logo.hidden = true;
      logo.removeAttribute("src");
    }
  };

  setLogo(els.scanRevealLogoLeft, topSlug, topElement);
  setLogo(els.scanRevealLogoRight, bottomSlug, bottomElement);
}

function showSwapRevealHalves(top, bottom) {
  clearSwapReveal();

  const halves = document.createElement("div");
  halves.className = "swap-reveal-halves";
  halves.appendChild(renderSwapRevealHalf(top, "Top"));
  halves.appendChild(renderSwapRevealHalf(bottom, "Bottom"));
  els.scanRevealPortal.appendChild(halves);
}

function swapCombinationPortraitPath(top, bottom) {
  const topKey = swapPortraitKey(top);
  const bottomKey = swapPortraitKey(bottom);
  return topKey && bottomKey ? `/assets/images/portraits/swap-force/swaps/${topKey}-${bottomKey}.webp` : null;
}

function swapPortraitKey(entry) {
  if (!entry?.id || !entry?.swapPart) return null;

  const marker = `-${normalize(entry.swapPart)}-`;
  const id = normalize(entry.id);
  const index = id.indexOf(marker);
  if (index < 0) return null;

  const halfName = id.slice(index + marker.length);

  // Chase variants don't have combination portraits — fall back to the canonical portrait.
  const allTypes = [entry.type, ...(entry.types || [])].map(t => normalize(t ?? ""));
  if (allTypes.includes("chase variant")) return halfName;

  const segments = id.slice(0, index).split("-");
  const prefix = segments.length > 2 ? segments.slice(0, -2).join("-") : "";
  return prefix ? `${prefix}-${halfName}` : halfName;
}

function swapPartRank(part) {
  const normalized = normalize(part);
  if (normalized === "top") return 0;
  if (normalized === "bottom") return 1;
  return 2;
}

function formatSwapRevealName(top, bottom) {
  const topLabel = top?.halfName || top?.name || "Top";
  const bottomLabel = bottom?.halfName || bottom?.name || "Bottom";
  const topNickname = figureNickname(top);
  const bottomNickname = figureNickname(bottom);
  if (topNickname || bottomNickname) {
    return `${topNickname || topLabel} ${bottomNickname || bottomLabel}`;
  }

  return `${topLabel} ${bottomLabel}`;
}

function renderSwapRevealHalf(entry, label) {
  const item = entry || {};
  const portraitPath = portraitPathFor(item);
  const half = document.createElement("div");
  half.className = "swap-reveal-half";

  const img = document.createElement("img");
  img.alt = "";
  img.src = portraitPath || `/assets/images/elements/${itemIconAssetName(item) || "magic-item"}.webp`;
  img.onerror = () => {
    img.src = `/assets/images/elements/${itemIconAssetName(item) || "magic-item"}.webp`;
  };

  const span = document.createElement("span");
  span.textContent = label;

  half.appendChild(img);
  half.appendChild(span);
  return half;
}

function showWarningReveal(title, detail = "") {
  if (!els.scanReveal) return;
  clearSwapReveal();
  clearRevealVillain();

  els.scanReveal.classList.remove("is-center", "is-duplicate", "is-swap", "is-swap-combo");
  els.scanReveal.classList.add("is-warning");
  els.scanReveal.dataset.game = assetSlug(state.game || "All") || "all";
  els.scanRevealPortal.dataset.game = els.scanReveal.dataset.game;
  els.scanRevealPortal.dataset.element = "None";

  if (els.scanRevealLabel) {
    els.scanRevealLabel.textContent = "Warning";
  }
  els.scanRevealName.textContent = title || "Warning";
  els.scanRevealDetails.textContent = detail || "";

  els.scanRevealElement.hidden = true;
  els.scanRevealElement.removeAttribute("src");
  els.scanRevealPortrait.hidden = true;
  els.scanRevealPortrait.removeAttribute("src");

  for (const logo of [els.scanRevealLogoLeft, els.scanRevealLogoRight]) {
    if (!logo) continue;
    logo.hidden = true;
    logo.removeAttribute("src");
  }

  els.scanReveal.hidden = false;
  els.scanReveal.classList.remove("is-playing");
  void els.scanReveal.offsetWidth;
  els.scanReveal.classList.add("is-playing");

  window.clearTimeout(revealTimeout);
  revealTimeout = window.setTimeout(() => {
    els.scanReveal.classList.remove("is-playing", "is-warning");
    els.scanReveal.hidden = true;
  }, 5200);
}

startBackgroundMusic();

if (desktopBridge) {
  els.scannerPanel.hidden = true;

  desktopBridge.addEventListener("message", event => {
    const message = event.data;
    if (!message || message.type !== "scanner") return;

    if (message.status === "portalContents") {
      if (!debugFeaturesEnabled) return;
      if (els.portalContents) {
        const text = message.text || "";
        if (!text) {
          els.portalContents.hidden = true;
          els.portalContents.innerHTML = "";
        } else {
          els.portalContents.hidden = false;
          els.portalContents.innerHTML = text.split("\n")
            .map(line => `<code>${escapeHtml(line)}</code>`)
            .join("");
        }
      }
      return;
    }

    if (["ready", "scan", "newDiscovery", "swapScan", "newSwapDiscovery", "removed"].includes(message.status)) {
      setScannerState(true, message.text);
    } else if (message.status === "incompleteSwap") {
      const text = message.text || "Incomplete SWAP Force figure detected. Add both halves to scan it.";
      setScannerState(true, text);
      setHeaderStatus(text, "warning");
      showWarningReveal("Incomplete SWAP figure", text);
    } else if (message.status === "swapDebug") {
      if (!debugFeaturesEnabled) return;
      setScannerState(true, message.text || "SWAP diagnostic updated.");
    } else if (message.status === "tooManyFigures") {
      const text = message.text || "Place only one figure at a time.";
      setScannerState(true, text);
      setHeaderStatus(text, "warning");
      showWarningReveal("Too many figures", text);
    } else if (message.status === "unknownFigure") {
      const text = message.text || "Unknown figure. Not added.";
      setScannerState(true, text);
      setHeaderStatus(text, "warning");
      showWarningReveal("Unknown figure", text);
    } else if (message.status === "variantChoiceRequired") {
      const text = message.text || "Choose which physical variant is on the portal.";
      setScannerState(true, text);
      setHeaderStatus(text, "warning");
      showVariantChoicePrompt(message);
    } else if (message.status === "portalReady") {
      setScannerState(false, message.text || "Portal connected. Scanner ready.");
      showPortalConnected();
      stopPortalPolling();
      startScannerIfNeeded();
    } else if (message.status === "portalMissing") {
      setScannerState(false, message.text);
      setPortalMissing(true);
      setPortalGate(true, "searching", "Searching for Portal...", "Connect a Portal of Power to continue.");
      startPortalPolling();
    } else if (message.status === "figureDumpArmed") {
      if (!debugFeaturesEnabled) return;
      setScannerState(true, message.text || "Figure data dump armed.");
      setHeaderStatus(message.text || "Figure data dump armed.");
    } else if (message.status === "figureDumpSaved") {
      if (!debugFeaturesEnabled) return;
      setScannerState(true, message.text || "Figure data dump saved.");
      setHeaderStatus(message.text || "Figure data dump saved.");
    } else if (message.status === "upgradeCatalogSaved") {
      state.upgradeDirty = false;
      setScannerState(scannerRunning, message.text || "Upgrade editor saved.");
      setHeaderStatus(message.text || "Upgrade editor saved.");
      if (state.view === "upgrades") {
        renderUpgradeEditor();
      }
    } else if (["stopped", "error"].includes(message.status)) {
      setScannerState(false, message.text);
    } else {
      setScannerState(scannerRunning, message.text || "Scanner running");
    }

    if (message.status === "newDiscovery") {
      enqueueReveal(() => showScanReveal(message.entry, { isNew: true }), 5200, true);
    } else if (message.status === "scan") {
      try {
        showScanReveal(message.entry, { isNew: false });
      } catch (e) {
        console.error("showScanReveal error:", e);
        showWarningReveal(message.entry?.name || "Figure", "Already in your vault");
      }
    } else if (message.status === "newSwapDiscovery") {
      enqueueReveal(() => showSwapReveal(message.entries, { isNew: true }), 5200, true);
    } else if (message.status === "swapScan") {
      try {
        showSwapReveal(message.entries, { isNew: false });
      } catch (e) {
        console.error("showSwapReveal error:", e);
      }
    } else if (message.status === "removed") {
      clearRevealQueue();
      hideVariantChoicePrompt();
    }

    if (message.status === "collectionUpdated") {
      reloadCollectionAfterScan().catch(error => {
        console.error(error);
        const text = "Scanned, but the collection view did not refresh";
        setHeaderStatus(text, "warning");
        showWarningReveal("Collection refresh failed", text);
      });
    }
    else if (message.status === "collectionCleared" || message.status === "collectionCleaned") {
      reloadCollectionAfterScan().then(() => {
        setHeaderStatus(message.text || "Collection updated");
      }).catch(error => {
        console.error(error);
        setHeaderStatus(message.text || "Collection updated");
      });
    }
    else if (message.status === "manualOwnershipUpdated" || message.status === "collectionEntryRemoved") {
      reloadCollectionAfterManualChange().then(() => {
        setHeaderStatus(message.text || "Collection updated");
      }).catch(error => {
        console.error(error);
        setHeaderStatus(message.text || "Collection updated");
      });
    }
  });

  desktopBridge.postMessage({ type: "checkPortal" });
} else {
  setPortalGate(false);
}

function setScannerState(isRunning, text) {
  scannerRunning = isRunning;
  els.scannerStatus.textContent = text;
  setPortalMissing(false);
  if (!isRunning && els.portalContents) {
    els.portalContents.hidden = true;
    els.portalContents.innerHTML = "";
  }
}

function setHeaderStatus(text, mode = "") {
  els.syncStatus.textContent = text;
  els.syncStatus.classList.toggle("warning", mode === "warning");
}

function startScannerIfNeeded() {
  if (scannerRunning) {
    return;
  }

  desktopBridge?.postMessage({ type: "startScanner" });
  setScannerState(true, "Scanner starting...");
}

function setPortalMissing(isMissing) {
  els.scannerPanel?.classList.toggle("is-missing", isMissing);
}

function setPortalGate(isBlocked, stateName = "searching", title = "Searching for Portal...", text = "Connect a Portal of Power to continue.") {
  if (portalGateHideTimer) {
    window.clearTimeout(portalGateHideTimer);
    portalGateHideTimer = null;
  }

  document.body.classList.toggle("portal-blocked", isBlocked);
  if (els.portalGate) {
    els.portalGate.hidden = !isBlocked;
    els.portalGate.dataset.state = stateName;
  }
  if (els.portalGateTitle) {
    els.portalGateTitle.textContent = title;
  }
  if (els.portalGateText) {
    els.portalGateText.textContent = text;
  }
}

function showPortalConnected() {
  setPortalGate(true, "connected", "Portal connected!", "Place a Skylander on the Portal.");
  portalGateHideTimer = window.setTimeout(() => {
    setPortalGate(false, "connected", "Portal connected!", "Place a Skylander on the Portal.");
  }, 950);
}

function startPortalPolling() {
  if (portalCheckTimer) {
    return;
  }

  portalCheckTimer = window.setInterval(() => {
    desktopBridge?.postMessage({ type: "checkPortal" });
  }, 1400);
}

function stopPortalPolling() {
  if (!portalCheckTimer) {
    return;
  }

  window.clearInterval(portalCheckTimer);
  portalCheckTimer = null;
}

function updateTabs() {
  els.tabs.forEach(tab => {
    tab.classList.toggle("active", tab.dataset.view === state.view);
  });
}

loadData().catch(error => {
  console.error(error);
  els.syncStatus.textContent = "Could not load collection";
  els.collectionGrid.innerHTML = `<div class="empty-state">${escapeHtml(error.message)}</div>`;
});
