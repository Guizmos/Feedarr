import { getActiveUiLanguage } from "./locale.js";

const TRANSLATABLE_ATTRIBUTES = ["placeholder", "title", "aria-label"];
const ORIGINAL_ATTR_PREFIX = "data-i18n-original-";
const ELISION_PREFIXES = new Set(["l", "d", "j", "c", "n", "s", "m", "t", "qu"]);

const textOriginals = new WeakMap();
let currentLanguage = getLanguageCode(getActiveUiLanguage());
let observer = null;
let initialized = false;
let languageListener = null;

const t = (en, overrides = {}) => ({ en, ...overrides });

const PHRASE_TRANSLATIONS = {
  "configuration de l'application": t("Application settings"),
  "browser reload required": t("Browser Reload Required"),
  "rechargement du navigateur requis": t("Browser Reload Required"),
  "sync all": t("Sync all"),
  "top 24h": t("Top 24h"),
  language: t("Language"),
  "media info language": t("Media Info Language"),
  "ui language": t("UI Language"),
  "a propos": t("About"),
  "mise a jour": t("Update"),
  "mise a jour disponible": t("Update available"),
  "a jour": t("Up to date"),
  "version actuelle": t("Current version"),
  "derniere release": t("Latest release"),
  "publie le": t("Published on"),
  statut: t("Status"),
  "intervalle de verification": t("Check interval"),
  environnement: t("Environment"),
  "base de donnees": t("Database"),
  "dossier data": t("Data folder"),
  "chemin db": t("DB path"),
  "espace disque": t("Disk space"),
  emplacement: t("Location"),
  "espace libre": t("Free space"),
  "espace total": t("Total space"),
  utilisation: t("Usage"),
  "aucun volume detecte": t("No volume detected"),
  "requetes et synchronisations": t("Requests and synchronizations"),
  "synchronisations et evenements": t("Synchronizations and events"),
  "categories sync": t("Sync categories"),
  "temps de reponse": t("Response time"),
  "total entrees": t("Total records"),
  "chargement de l'historique": t("Loading history..."),
  "historique indisponible": t("History unavailable"),
  "erreur chargement historique": t("History loading error"),
  "erreur purge historique": t("History purge error"),
  "effacer l'historique": t("Clear history"),
  "confirmer la suppression de l'historique": t("Confirm history deletion"),
  "chargement des logs": t("Loading logs..."),
  "erreur chargement activite": t("Activity loading error"),
  "erreur purge logs": t("Log purge error"),
  "effacer les logs": t("Clear logs"),
  "confirmer la purge des logs": t("Confirm log purge"),
  "premiere page": t("First page"),
  "page precedente": t("Previous page"),
  "page suivante": t("Next page"),
  "derniere page": t("Last page"),
  "niveau de log": t("Log level"),
  "derniers items": t("Last items"),
  "temps moyen": t("Average time"),
  resume: t("Summary"),
  "verifie la configuration puis lance feedarr": t("Check the configuration then launch Feedarr"),
  "configuration rapide en 6 etapes": t("Quick setup in 6 steps"),
  "configuration rapide en 7 etapes": t("Quick setup in 7 steps"),
  "langue par defaut": t("Default language"),
  "choisis la langue par defaut de feedarr": t("Choose Feedarr default language"),
  "cette etape applique la langue ui et la langue metadata par defaut": t("This step sets both UI and metadata default language"),
  "langue utilisee pour les infos media (tmdb: synopsis, titres, cast)": t("Language used for media info (TMDB: overview, titles, cast)"),
  "langue de l'interface (formatage dates/heures et preference ui)": t("UI language (date/time formatting and UI preference)"),
  "marquer \"vu\" par defaut": t("Mark \"seen\" by default"),
  "activer vue sans poster": t("Enable no-poster view"),
  "animations de l'interface": t("Interface animations"),
  "badge pour info": t("Badge for Info"),
  "badge pour warn": t("Badge for Warn"),
  "badge pour error": t("Badge for Error"),
  "toutes categories": t("All categories"),
  "toutes qualites": t("All qualities"),
  "toutes apps": t("All apps"),
  "masquer apps": t("Hide apps"),
  "pas vu": t("Unseen"),
  "actions de maintenance": t("Maintenance actions"),
  "supprimer tous les posters en cache": t("Delete all cached posters"),
  "supprime tous les posters telecharges localement": t("Delete all locally downloaded posters"),
  "purger logs": t("Purge logs"),
  "supprime les logs d'activite (tous, historique ou logs uniquement)": t("Delete activity logs (all, history or logs only)"),
  "optimiser la base de donnees": t("Optimize database"),
  "compacte la base sqlite (vacuum) pour recuperer de l'espace": t("Compacts SQLite database (VACUUM) to free space"),
  "nettoyer posters orphelins": t("Clean orphan posters"),
  "supprime les fichiers de posters non references en base": t("Delete poster files not referenced in database"),
  "tester connectivite providers": t("Test provider connectivity"),
  "verifie la connexion tmdb, tvmaze, fanart, igdb": t("Check TMDB, TvMaze, Fanart and IGDB connectivity"),
  "re-parser les titres": t("Re-parse titles"),
  "recalcule les metadonnees de toutes les releases (titre, saison, resolution...)": t("Recompute metadata for all releases (title, season, resolution...)"),
  "detecter les doublons": t("Detect duplicates"),
  "identifie et supprime les releases en double par source": t("Identify and delete duplicate releases by source"),
  "statistiques": t("Statistics"),
  "metrique": t("Metric"),
  "valeur": t("Value"),
  "releases en base": t("Releases in database"),
  "taille base de donnees": t("Database size"),
  "cache de matching": t("Matching cache"),
  "entites media": t("Media entities"),
  "doublons detectes": t("Detected duplicates"),
  "logs d'activite": t("Activity logs"),
  "uptime serveur": t("Server uptime"),
  "par categorie": t("By category"),
  "confirmer la suppression": t("Confirm deletion"),
  "options de purge": t("Purge options"),
  "tous les logs": t("All logs"),
  "historique (sync uniquement)": t("History (sync only)"),
  "logs (hors sync)": t("Logs (excluding sync)"),
  "confirmer l'optimisation": t("Confirm optimization"),
  "confirmer le nettoyage": t("Confirm cleanup"),
  "confirmer le re-parsing": t("Confirm re-parsing"),
  "confirmer la suppression des doublons": t("Confirm duplicate deletion"),
  "aucune donnee": t("No data"),
  "aucune application connectee": t("No connected application"),
  "posters affiches": t("Displayed posters"),
  "couverture posters": t("Poster coverage"),
  "reutilisation posters": t("Poster reuse"),
  "fichiers uniques": t("unique files"),
  "releases par jour": t("Releases per day"),
  "releases par poster": t("releases per poster"),
  "posters pour": t("posters for"),
  "repartition stockage": t("Storage distribution"),
  "taille base": t("Database size"),
  "posters locaux": t("Local posters"),
  "requetes totales": t("Total queries"),
  "echecs requetes": t("Query failures"),
  "echecs sync": t("Sync failures"),
  "releases par fournisseur": t("Releases by provider"),
  "categories par fournisseur": t("Categories by provider"),
  "detail fournisseurs": t("Provider details"),
  "total appels": t("Total calls"),
  "total echecs": t("Total failures"),
  "taux d'echec global": t("Global failure rate"),
  "detail metadonnees": t("Metadata details"),
  "sante des indexeurs": t("Indexer health"),
  "sante des fournisseurs": t("Provider health"),
  "prochain sync": t("Next sync"),
  "items dernier sync": t("Last sync items"),
  "fournisseurs indisponibles": t("Providers unavailable"),
  "sources indisponibles": t("Sources unavailable"),
  "aucune source configuree": t("No source configured"),
  "erreur chargement statistiques": t("Statistics loading error"),
  "elements matches": t("matched items"),
  "categories par indexeur": t("Categories by indexer"),
  "detail indexeurs": t("Indexer details"),
  "detail providers": t("Provider details"),
  "temps de reponse moyen": t("Average response time"),
  "taux d'echec": t("Failure rate"),
  "top categories": t("Top categories"),
  "top releases (grabs)": t("Top releases (grabs)"),
  "distribution par taille": t("Size distribution"),
  "distribution seeders": t("Seeders distribution"),
  "date d'ajout": t("Added date"),
  "titre original": t("Original title"),
  "nouveau title": t("New title"),
  "nouveau titre": t("New title"),
  "resultats": t("Results"),
  "selectionner une seule carte": t("Select a single card"),
  "poster manuel": t("Manual poster"),
  "filtrer par identifiant de source": t("Filter by source identifier"),
  "aucune sauvegarde presente": t("No backup found"),
  "nouvelle sauvegarde": t("New backup"),
  "creer une nouvelle sauvegarde": t("Create a new backup"),
  "restaurer une sauvegarde": t("Restore a backup"),
  "supprimer une sauvegarde": t("Delete a backup"),
  "confirmer la restauration": t("Confirm restore"),
  "cette action est definitive": t("This action is permanent"),
  "derniere synchro": t("Last sync"),
  "base url": t("Base URL"),
  "laisse vide pour ne pas changer": t("Leave blank to keep current"),
  "options de synchronisation": t("Synchronization options"),
  "synchronisation auto": t("Automatic sync"),
  "intervalle sync rss (minutes)": t("RSS sync interval (minutes)"),
  "limite rss par categorie": t("RSS limit per category"),
  "remplissage progressif par categorie, purge des plus anciens": t("Progressive fill per category, purge oldest items"),
  "limite rss globale (par source)": t("Global RSS limit (per source)"),
  "plafond global par source, purge des plus anciens": t("Global cap per source, purge oldest items"),
  "choisir un fournisseur...": t("Choose a provider..."),
  "choisir un indexeur...": t("Choose an indexer..."),
  "ajouter manuellement...": t("Add manually..."),
  "tester categories": t("Test categories"),
  "masquer options avancees": t("Hide advanced options"),
  "test reussi": t("Test successful"),
  "suivant": t("Next"),
  "tous les indexeurs sont deja ajoutes": t("All indexers are already added"),
  "pas besoin de cle sauf abonnement": t("No key needed unless you have a subscription"),
  "provider invalide": t("Invalid provider"),
  "client id et client secret requis": t("Client ID and Client Secret required"),
  "cle api requise": t("API key required"),
  "connexion reussie ! vous pouvez enregistrer": t("Connection successful! You can save"),
  "configuration enregistree. ajoute les indexeurs manuellement a l'etape suivante": t("Configuration saved. Add indexers manually at the next step"),
  "cles deja enregistrees": t("Keys already saved"),
  "voir sur tmdb / igdb": t("View on TMDB / IGDB"),
  "ajouter un fournisseur": t("Add a provider"),
  "ajouter une source": t("Add a source"),
  "ajouter une application": t("Add an application"),
  "supprimer l'application": t("Delete application"),
  "options applications": t("Application options"),
  "aucune application configuree": t("No configured application"),
  "mode d'envoi": t("Delivery mode"),
  "integration active": t("Active integration"),
  "a sauvegarder": t("To save"),
  "etat de synchronisation": t("Synchronization status"),
  "dernier sync": t("Last sync"),
  "nom (optionnel)": t("Name (optional)"),
  "entrez la cle api": t("Enter API key"),
  "entrez le client id": t("Enter Client ID"),
  "entrez le client secret": t("Enter Client Secret"),
  "test en cours": t("Test in progress"),
  "confirmer la desactivation": t("Confirm disable"),
  "confirmer l'action": t("Confirm action"),
  "activez d'abord le provider": t("Enable the provider first"),
  "cette action est irreversible": t("This action is irreversible"),
  "cette action va desactiver le provider et supprimer la cle api associee": t("This action will disable the provider and remove the associated API key"),
  "cette action va desactiver le provider (la cle api est conservee)": t("This action will disable the provider (API key is kept)"),
  "cette action va activer le provider": t("This action will enable the provider"),
  "chargement de la configuration": t("Loading configuration"),
  "-- selectionner --": t("-- Select --"),
  "id du profil": t("Profile ID"),
  "type de serie": t("Series type"),
  "mode de surveillance": t("Monitoring mode"),
  "tous les episodes": t("All episodes"),
  "futurs episodes": t("Future episodes"),
  "episodes manquants": t("Missing episodes"),
  "episodes existants": t("Existing episodes"),
  "premiere saison": t("First season"),
  "dossiers par saison": t("Season folders"),
  "rechercher episodes manquants": t("Search missing episodes"),
  "rechercher cutoff non atteint": t("Search unmet cutoff"),
  "disponibilite minimale": t("Minimum availability"),
  "annonce": t("Announced"),
  "en salle": t("In theaters"),
  "sorti": t("Released"),
  "rechercher le film a l'ajout": t("Search for movie on add"),
  "cette action va supprimer tous les posters telecharges localement": t("This action will delete all locally downloaded posters"),
  "elle ne nettoie pas uniquement les orphelins": t("This does not only clean orphan files"),
  "ils seront re-telecharges automatiquement lors du prochain affichage": t("They will be downloaded again automatically on next display"),
  "cette action execute un vacuum sqlite pour compacter la base de donnees et recuperer l'espace disque": t("This runs SQLite VACUUM to compact the database and reclaim disk space"),
  "l'operation peut prendre quelques secondes": t("This operation may take a few seconds"),
  "cette action va scanner le dossier de posters et supprimer les fichiers qui ne sont plus references par aucune release en base": t("This action will scan the posters folder and delete files no longer referenced by any release"),
  "cette action va supprimer l'application": t("This action will delete application"),
  "cette action va re-parser tous les titres de releases pour recalculer les metadonnees (titre nettoye, saison, episode, resolution, codec, etc)": t("This action will re-parse all release titles to recompute metadata (clean title, season, episode, resolution, codec, etc.)"),
  "utile apres une mise a jour du parser": t("Useful after a parser update"),
  "l'operation peut prendre un moment selon le nombre de releases": t("This operation may take a while depending on release count"),
  "pour chaque groupe de doublons, seule la release la plus recente sera conservee": t("For each duplicate group, only the most recent release is kept"),
  "cette action cree une archive zip locale contenant la base de donnees et les metadonnees de configuration (sans exposer les secrets en clair dans `config.json`)": t("This action creates a local ZIP archive containing the database and configuration metadata (without exposing plain secrets in `config.json`)"),
  "un redemarrage de l'application sera requis apres restauration": t("An application restart is required after restore"),
  "afficher seulement les categories recommandees": t("Show only recommended categories"),
  "aucune categorie recommandee. desactive le filtre pour tout voir": t("No recommended category. Disable the filter to show all"),
  "aucune categorie disponible": t("No category available"),
  "laisse vide pour utiliser la cle du fournisseur": t("Leave blank to use provider API key"),
  "colle l'url copy torznab feed": t("Paste the Copy Torznab Feed URL"),
  "depuis jackett/prowlarr, utilise \"copy torznab feed\", puis colle l'url complete": t("From Jackett/Prowlarr, use \"Copy Torznab Feed\", then paste the full URL"),
  "categories retenues": t("Selected categories"),
  "nouvel indexeur": t("New indexer"),
  "nom de l'indexeur": t("Indexer name"),
  "plus vieux que (jours)": t("Older than (days)"),
  "testez d'abord la connexion avant d'enregistrer": t("Test the connection first before saving"),
  "toutes les applications sont deja ajoutees": t("All applications are already added"),
};

const WORD_TRANSLATIONS = {
  bibliotheque: t("Library"),
  parametres: t("Settings"),
  fournisseur: t("Provider"),
  fournisseurs: t("Providers"),
  provider: t("Provider"),
  providers: t("Providers"),
  indexeur: t("Indexer"),
  indexeurs: t("Indexers"),
  indexer: t("Indexer"),
  applications: t("Applications"),
  application: t("Application"),
  rss: t("RSS"),
  maintenance: t("Maintenance"),
  sauvegarde: t("Backup"),
  sauvegardes: t("Backups"),
  systeme: t("System"),
  historique: t("History"),
  logs: t("Logs"),
  configuration: t("Configuration"),
  charge: t("Loading"),
  chargement: t("Loading"),
  erreur: t("Error"),
  rafraichir: t("Refresh"),
  effacer: t("Clear"),
  sauver: t("Save"),
  ajouter: t("Add"),
  ajoute: t("Added"),
  ajoutee: t("Added"),
  ajoutees: t("Added"),
  options: t("Options"),
  enregistrer: t("Save"),
  enregistre: t("Saved"),
  enregistree: t("Saved"),
  enregistrees: t("Saved"),
  enregistrement: t("Saving"),
  sauvegarder: t("Save"),
  annuler: t("Cancel"),
  supprimer: t("Delete"),
  suppression: t("Deletion"),
  desactiver: t("Disable"),
  desactivation: t("Disable"),
  activer: t("Enable"),
  actif: t("Active"),
  actifs: t("Active"),
  active: t("Active"),
  actives: t("Active"),
  desactive: t("Disabled"),
  desactivee: t("Disabled"),
  desactivees: t("Disabled"),
  inactif: t("Inactive"),
  irreversible: t("irreversible"),
  modifier: t("Edit"),
  editer: t("Edit"),
  derniere: t("Last"),
  synchro: t("sync"),
  metadonnees: t("Metadata"),
  statut: t("Status"),
  environnement: t("Environment"),
  dossier: t("Folder"),
  chemin: t("Path"),
  db: t("DB"),
  espace: t("Space"),
  libre: t("Free"),
  emplacement: t("Location"),
  utilisation: t("Usage"),
  fichier: t("file"),
  fichiers: t("files"),
  unique: t("unique"),
  uniques: t("unique"),
  prochain: t("Next"),
  manuel: t("Manual"),
  verification: t("Check"),
  actuelle: t("Current"),
  actuel: t("Current"),
  requete: t("query"),
  requetes: t("queries"),
  elements: t("items"),
  matches: t("matched"),
  sante: t("Health"),
  securite: t("Security"),
  theme: t("Theme"),
  apparence: t("Appearance"),
  clair: t("Light"),
  sombre: t("Dark"),
  langue: t("Language"),
  filtre: t("Filter"),
  date: t("Date"),
  source: t("Source"),
  sources: t("Sources"),
  categorie: t("Category"),
  categories: t("Categories"),
  qualite: t("Quality"),
  toutes: t("All"),
  tous: t("All"),
  vue: t("View"),
  liste: t("List"),
  tri: t("Sort"),
  limite: t("Limit"),
  test: t("Test"),
  tester: t("Test"),
  executer: t("Run"),
  analyser: t("Analyze"),
  analyse: t("Analyzing"),
  niveau: t("Level"),
  avertissement: t("Warning"),
  nom: t("Name"),
  couleur: t("Color"),
  connexion: t("Connection"),
  connectivite: t("connectivity"),
  reussie: t("successful"),
  retro: t("Retro"),
  fetch: t("Fetch"),
  arreter: t("Stop"),
  demarrer: t("Start"),
  lancer: t("Launch"),
  lancement: t("Launching"),
  restauration: t("Restore"),
  telechargement: t("Download"),
  telecharger: t("Download"),
  operation: t("Operation"),
  cours: t("in progress"),
  redemarrage: t("Restart"),
  requis: t("required"),
  apres: t("after"),
  teste: t("tested"),
  echec: t("failed"),
  echecs: t("failures"),
  succes: t("success"),
  synchronisation: t("Synchronization"),
  synchronisations: t("Synchronizations"),
  evenements: t("Events"),
  automatique: t("Automatic"),
  intervalle: t("Interval"),
  minutes: t("minutes"),
  etat: t("Status"),
  jamais: t("Never"),
  dernier: t("Last"),
  derniers: t("Last"),
  temps: t("Time"),
  items: t("items"),
  demandes: t("requests"),
  avancees: t("advanced"),
  mode: t("Mode"),
  general: t("General"),
  statistiques: t("Statistics"),
  stockage: t("Storage"),
  wizard: t("wizard"),
  aucun: t("No"),
  aucune: t("No"),
  non: t("Not"),
  configure: t("Configured"),
  configuree: t("Configured"),
  configurees: t("Configured"),
  configures: t("Configured"),
  configurer: t("Configure"),
  choisir: t("Choose"),
  choisis: t("Choose"),
  valider: t("Validate"),
  valide: t("Valid"),
  invalide: t("Invalid"),
  verifier: t("Check"),
  verifie: t("Check"),
  impossible: t("Unable"),
  resume: t("Summary"),
  poster: t("Poster"),
  posters: t("Posters"),
  locaux: t("Local"),
  local: t("Local"),
  manquant: t("Missing"),
  manquants: t("Missing"),
  recherche: t("Search"),
  pourcentage: t("Percentage"),
  matching: t("Matching"),
  detecter: t("Detect"),
  detectes: t("Detected"),
  doublon: t("Duplicate"),
  doublons: t("Duplicates"),
  groupe: t("Group"),
  groupes: t("Groups"),
  purge: t("Purge"),
  purger: t("Purge"),
  nettoyer: t("Clean"),
  nettoyage: t("Cleanup"),
  optimiser: t("Optimize"),
  optimisation: t("Optimization"),
  base: t("Database"),
  donnees: t("Data"),
  metrique: t("Metric"),
  valeur: t("Value"),
  entites: t("Entities"),
  media: t("Media"),
  activite: t("Activity"),
  taille: t("Size"),
  serveur: t("Server"),
  uptime: t("Uptime"),
  reparser: t("Re-parse"),
  "re-parsing": t("Re-parsing"),
  titres: t("Titles"),
  titre: t("Title"),
  annee: t("Year"),
  nouveau: t("New"),
  original: t("Original"),
  saison: t("Season"),
  episode: t("Episode"),
  episodes: t("Episodes"),
  resolution: t("Resolution"),
  entrees: t("entries"),
  entree: t("entry"),
  jour: t("day"),
  jours: t("days"),
  top: t("Top"),
  telecharge: t("Downloaded"),
  telecharges: t("Downloaded"),
  telechargee: t("Downloaded"),
  recent: t("Recent"),
  recents: t("Recent"),
  plus: t("most"),
  seedes: t("seeded"),
  api: t("API"),
  cle: t("Key"),
  client: t("Client"),
  secret: t("Secret"),
  envoi: t("Delivery"),
  integration: t("Integration"),
  associee: t("associated"),
  conservee: t("kept"),
  "d'abord": t("first"),
  entrez: t("Enter"),
  laisse: t("Leave"),
  vide: t("blank"),
  changer: t("change"),
  fermer: t("Close"),
  retour: t("Back"),
  optionnel: t("Optional"),
  afficher: t("Show"),
  masquer: t("Hide"),
  confirmer: t("Confirm"),
  cette: t("This"),
  va: t("will"),
  action: t("action"),
  definitive: t("permanent"),
  temporairement: t("temporarily"),
  verrouillees: t("locked"),
  presentes: t("present"),
  presente: t("present"),
  nouvelle: t("New"),
  creer: t("Create"),
  creee: t("Created"),
  cree: t("Created"),
  terminee: t("Completed"),
  termine: t("Completed"),
  restaurer: t("Restore"),
  vu: t("Seen"),
  pas: t("Not"),
  renommer: t("Rename"),
  retirer: t("Remove"),
  selectionner: t("Select"),
  selectionne: t("Selected"),
  selectionnee: t("Selected"),
  selectionnees: t("Selected"),
  carte: t("Card"),
  cartes: t("Cards"),
  details: t("Details"),
  detail: t("Detail"),
  precedente: t("Previous"),
  suivante: t("Next"),
  premiere: t("First"),
  total: t("Total"),
  avec: t("With"),
  sans: t("Without"),
  global: t("Global"),
  taux: t("Rate"),
  moyen: t("Average"),
  reponse: t("Response"),
  appels: t("Calls"),
  queries: t("Queries"),
  grabs: t("Grabs"),
  distribution: t("Distribution"),
  seeders: t("Seeders"),
};

function getLanguageCode(uiLanguage) {
  const raw = String(uiLanguage || "fr-FR").trim().toLowerCase();
  if (raw.startsWith("fr")) return "fr";
  if (raw.startsWith("en")) return "en";
  return "en";
}

function normalizeTextKey(value) {
  return String(value || "")
    .replace(/[’]/g, "'")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/\s+/g, " ")
    .trim()
    .replace(/[.!?]+$/g, "")
    .toLowerCase();
}

function normalizeToken(value) {
  return String(value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .replace(/['’]/g, "")
    .toLowerCase();
}

function toCasePattern(source, translated) {
  if (!source || !translated) return translated;
  if (source.toUpperCase() === source) return translated.toUpperCase();
  if (source[0] && source[0].toUpperCase() === source[0]) {
    return translated[0] ? translated[0].toUpperCase() + translated.slice(1) : translated;
  }
  return translated;
}

function getPhraseTranslation(text, language) {
  const key = normalizeTextKey(text);
  const phrase = PHRASE_TRANSLATIONS[key];
  if (!phrase) return null;
  return phrase[language] || phrase.en || null;
}

function getWordTranslation(token, language) {
  const key = normalizeToken(token);
  const entry = WORD_TRANSLATIONS[key];
  if (entry) {
    const translated = entry[language] || entry.en;
    if (translated) return toCasePattern(token, translated);
  }

  const apostropheIndex = token.search(/['’]/);
  if (apostropheIndex > 0 && apostropheIndex < token.length - 1) {
    const prefix = normalizeToken(token.slice(0, apostropheIndex));
    if (ELISION_PREFIXES.has(prefix)) {
      const rest = token.slice(apostropheIndex + 1);
      const restTranslated = getWordTranslation(rest, language);
      if (restTranslated) return toCasePattern(token, restTranslated);
    }
  }

  return null;
}

function applyPatternTranslation(text, language) {
  if (!text) return null;
  const normalized = normalizeTextKey(text);

  const addToMatch = normalized.match(/^ajouter\s+[a]\s+(.+)$/);
  if (addToMatch) {
    const target = text.replace(/^Ajouter\s+[àa]\s+/i, "");
    return language === "en" ? `Add to ${target}` : `Add to ${target}`;
  }

  const fallbackMatch = normalized.match(/^non trouve\s*-\s*ouvrir\s+(.+)$/);
  if (fallbackMatch) {
    const target = text.replace(/^.*-\s*Ouvrir\s+/i, "");
    return language === "en" ? `Not found - Open ${target}` : `Not found - Open ${target}`;
  }

  const deleteLinkedMatch = normalized.match(
    /^supprimer aussi (\d+) indexeur(?:s)? lie(?:s)? \(desactivation automatique\)$/
  );
  if (deleteLinkedMatch) {
    const count = Number(deleteLinkedMatch[1] || 0);
    const suffix = count > 1 ? "s" : "";
    return language === "en"
      ? `Also delete ${count} linked indexer${suffix} (automatic disable).`
      : `Also delete ${count} linked indexer${suffix} (automatic disable).`;
  }

  const lastSyncMatch = normalized.match(/^derniere synchro\s*:\s*(.+)$/);
  if (lastSyncMatch) {
    const target = text.replace(/^Derni(?:e|è)re synchro\s*:\s*/i, "");
    return language === "en" ? `Last sync: ${target}` : `Last sync: ${target}`;
  }

  const deleteDuplicateReleasesMatch = normalized.match(/^cette action va supprimer (\d+) releases en double$/);
  if (deleteDuplicateReleasesMatch) {
    const count = Number(deleteDuplicateReleasesMatch[1] || 0);
    return language === "en"
      ? `This action will delete ${count} duplicate releases.`
      : `This action will delete ${count} duplicate releases.`;
  }

  const selectedCategoriesMatch = normalized.match(/^categories retenues\s*\((\d+)\s*\/\s*(\d+)\)$/);
  if (selectedCategoriesMatch) {
    return language === "en"
      ? `Selected categories (${selectedCategoriesMatch[1]}/${selectedCategoriesMatch[2]})`
      : `Selected categories (${selectedCategoriesMatch[1]}/${selectedCategoriesMatch[2]})`;
  }

  if (/^cette action remplace la base actuelle par la sauvegarde\s*:?\s*$/.test(normalized)) {
    return language === "en"
      ? "This action replaces the current database with backup:"
      : "This action replaces the current database with backup:";
  }

  if (/^cette action va supprimer la sauvegarde\s*:?\s*$/.test(normalized)) {
    return language === "en"
      ? "This action will delete backup:"
      : "This action will delete backup:";
  }

  if (/^plus vieux que \(jours\)/.test(normalized)) {
    return language === "en"
      ? "Older than (days)"
      : "Older than (days)";
  }

  const deletedEntriesMatch = normalized.match(/^(\d+)\s+entrees supprimees$/);
  if (deletedEntriesMatch) {
    const count = Number(deletedEntriesMatch[1] || 0);
    return language === "en" ? `${count} entries deleted` : `${count} entries deleted`;
  }

  const deletedScannedFreedMatch = normalized.match(/^(\d+)\s+supprimes?\s*\/\s*(\d+)\s+scannes?\s*\((.+)\s+liberes\)$/);
  if (deletedScannedFreedMatch) {
    const deleted = deletedScannedFreedMatch[1];
    const scanned = deletedScannedFreedMatch[2];
    const freed = deletedScannedFreedMatch[3];
    return language === "en"
      ? `${deleted} deleted / ${scanned} scanned (${freed} freed)`
      : `${deleted} deleted / ${scanned} scanned (${freed} freed)`;
  }

  const groupsDuplicatesMatch = normalized.match(/^(\d+)\s+groupes,\s*(\d+)\s+doublons detectes$/);
  if (groupsDuplicatesMatch) {
    const groups = groupsDuplicatesMatch[1];
    const duplicates = groupsDuplicatesMatch[2];
    return language === "en"
      ? `${groups} groups, ${duplicates} duplicates detected`
      : `${groups} groups, ${duplicates} duplicates detected`;
  }

  const deletedDuplicatesMatch = normalized.match(/^(\d+)\s+doublons supprimes$/);
  if (deletedDuplicatesMatch) {
    const count = deletedDuplicatesMatch[1];
    return language === "en"
      ? `${count} duplicates deleted`
      : `${count} duplicates deleted`;
  }

  return null;
}

function translateTextContent(text, language) {
  if (language === "fr") return text;

  const raw = String(text || "");
  if (!raw.trim()) return raw;

  const leading = raw.match(/^\s*/)?.[0] || "";
  const trailing = raw.match(/\s*$/)?.[0] || "";
  const core = raw.slice(leading.length, raw.length - trailing.length);

  const punctuationSuffix = core.match(/[.!?]+$/)?.[0] || "";
  const coreWithoutPunctuation = punctuationSuffix ? core.slice(0, -punctuationSuffix.length) : core;

  const patternTranslation = applyPatternTranslation(coreWithoutPunctuation, language);
  if (patternTranslation) return `${leading}${patternTranslation}${punctuationSuffix}${trailing}`;

  const phrase = getPhraseTranslation(coreWithoutPunctuation, language);
  if (phrase) return `${leading}${phrase}${punctuationSuffix}${trailing}`;

  let changed = false;
  const translatedCore = core.replace(/([A-Za-zÀ-ÿ0-9'’_-]+)/g, (word) => {
    const translated = getWordTranslation(word, language);
    if (!translated) return word;
    if (translated !== word) changed = true;
    return translated;
  });

  return changed ? `${leading}${translatedCore}${trailing}` : raw;
}

function shouldSkipElement(element) {
  if (!element) return true;
  const tag = element.tagName?.toLowerCase();
  if (!tag) return true;
  if (tag === "script" || tag === "style" || tag === "code" || tag === "pre") return true;
  if (element.closest("[data-no-ui-translate='1']")) return true;
  return false;
}

function translateTextNode(node) {
  const parent = node.parentElement;
  if (!parent || shouldSkipElement(parent)) return;

  const currentValue = node.nodeValue;
  if (currentValue == null) return;

  let original = textOriginals.has(node) ? textOriginals.get(node) : currentValue;
  if (!textOriginals.has(node)) {
    textOriginals.set(node, original);
  } else {
    const expectedCurrent = translateTextContent(original, currentLanguage);
    if (currentValue !== expectedCurrent) {
      original = currentValue;
      textOriginals.set(node, original);
    }
  }

  const translated = translateTextContent(original, currentLanguage);
  if (translated !== currentValue) node.nodeValue = translated;
}

function attrStorageKey(attrName) {
  return `${ORIGINAL_ATTR_PREFIX}${attrName}`;
}

function translateElementAttributes(element) {
  if (!element || shouldSkipElement(element)) return;

  TRANSLATABLE_ATTRIBUTES.forEach((attr) => {
    const currentValue = element.getAttribute(attr);
    if (currentValue == null) return;

    const storageKey = attrStorageKey(attr);
    let original = element.getAttribute(storageKey) ?? currentValue;
    if (!element.hasAttribute(storageKey)) {
      element.setAttribute(storageKey, original);
    } else {
      const expectedCurrent = translateTextContent(original, currentLanguage);
      if (currentValue !== expectedCurrent) {
        original = currentValue;
        element.setAttribute(storageKey, original);
      }
    }

    const translated = translateTextContent(original, currentLanguage);
    if (translated !== currentValue) {
      element.setAttribute(attr, translated);
    }
  });
}

function translateSubtree(root) {
  if (!root) return;

  if (root.nodeType === Node.TEXT_NODE) {
    translateTextNode(root);
    return;
  }

  if (root.nodeType !== Node.ELEMENT_NODE) return;
  const element = root;
  if (shouldSkipElement(element)) return;

  translateElementAttributes(element);

  for (const child of element.childNodes) {
    translateSubtree(child);
  }
}

function handleMutations(mutations) {
  for (const mutation of mutations) {
    if (mutation.type === "characterData") {
      translateSubtree(mutation.target);
      continue;
    }

    if (mutation.type === "attributes") {
      translateSubtree(mutation.target);
      continue;
    }

    mutation.addedNodes.forEach((node) => translateSubtree(node));
  }
}

function refreshEntireDocument() {
  if (typeof document === "undefined" || !document.body) return;
  translateSubtree(document.body);
}

function onLanguageChanged(event) {
  const language = event?.detail?.language;
  currentLanguage = getLanguageCode(language || getActiveUiLanguage());
  refreshEntireDocument();
}

export function initRuntimeTranslation() {
  if (initialized) {
    refreshEntireDocument();
    return;
  }

  if (typeof window === "undefined" || typeof document === "undefined") return;

  currentLanguage = getLanguageCode(getActiveUiLanguage());
  refreshEntireDocument();

  observer = new MutationObserver(handleMutations);
  observer.observe(document.body, {
    childList: true,
    subtree: true,
    characterData: true,
    attributes: true,
    attributeFilter: TRANSLATABLE_ATTRIBUTES,
  });

  languageListener = (event) => onLanguageChanged(event);
  window.addEventListener("feedarr:ui-language-changed", languageListener);
  initialized = true;
}

export function disposeRuntimeTranslation() {
  if (!initialized) return;

  if (observer) {
    observer.disconnect();
    observer = null;
  }

  if (languageListener && typeof window !== "undefined") {
    window.removeEventListener("feedarr:ui-language-changed", languageListener);
  }
  languageListener = null;
  initialized = false;
}
