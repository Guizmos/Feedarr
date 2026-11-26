import { useEffect, useState } from "react";
import { apiGet, apiPost, apiPut } from "../../api/client.js";
import { directRssHttpError } from "./directRssErrors.js";
import { SOURCE_COLOR_PALETTE } from "../../utils/sourceColors.js";
import { CATEGORY_GROUP_LABELS } from "../../domain/categories/index.js";

const emptyFeed = () => ({ id: 0, url: "", name: "", categoryKey: "", enabled: true });

export default function DirectRssForm({ source, onSaved, onCancel }) {
  const [name, setName] = useState(source?.name || "");
  const [color, setColor] = useState(source?.color || SOURCE_COLOR_PALETTE[0]);
  const [feeds, setFeeds] = useState([emptyFeed()]);
  const [loading, setLoading] = useState(!!source?.id);
  const [loaded, setLoaded] = useState(!source?.id);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState("");
  const [tests, setTests] = useState({});
  useEffect(() => {
    if (!source?.id) return;
    let cancelled = false;
    apiGet(`/api/sources/rss/${source.id}/feeds`).then((rows) => {
      if (!cancelled) { setFeeds(rows.map((row) => ({ ...row, url: "" }))); setLoaded(true); }
    }).catch((e) => { if (!cancelled) setError(directRssHttpError(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [source?.id]);

  function change(index, patch) {
    setFeeds((rows) => rows.map((row, i) => i === index ? { ...row, ...patch } : row));
    setTests({});
  }
  async function test(index) {
    setTests((previous) => ({ ...previous, [index]: { loading: true } }));
    try {
      const result = await apiPost("/api/sources/rss/test", { ...feeds[index], sourceId: source?.id });
      setTests((previous) => ({ ...previous, [index]: result }));
    } catch (e) {
      setTests((previous) => ({ ...previous, [index]: { ok: false, error: directRssHttpError(e) } }));
    }
  }
  async function save(event) {
    event.preventDefault();
    if (!loaded || saving) return;
    setSaving(true); setError("");
    try {
      const payload = { name: name.trim(), color, feeds };
      if (source?.id) await apiPut(`/api/sources/rss/${source.id}`, payload);
      else await apiPost("/api/sources/rss", payload);
      await onSaved();
    } catch (e) { setError(directRssHttpError(e)); }
    finally { setSaving(false); }
  }
  return <form className="formgrid formgrid--edit direct-rss-form" onSubmit={save}>
    <div className="field"><label htmlFor="rss-source-name" className="muted">Nom</label>
      <input id="rss-source-name" value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></div>
    <div className="field"><label className="muted">Couleur</label>
      <div className="color-slider is-open"><div className="color-slider__track">
        {SOURCE_COLOR_PALETTE.map((swatch) => <button key={swatch} className={`color-swatch${color === swatch ? " is-active" : ""}`}
          type="button" style={{ background: swatch }} aria-label={`Couleur ${swatch}`} aria-pressed={color === swatch} onClick={() => setColor(swatch)} />)}
      </div></div></div>
    {loading ? <div className="muted">Chargement des flux...</div> : feeds.map((feed, index) => <fieldset className="direct-rss-form__feed" key={feed.id || `new-${index}`} disabled={saving}>
      <legend title={feed.name || `Flux ${index + 1}`}>{feed.name || `Flux ${index + 1}`}</legend>
      <div className="field"><label className="muted" htmlFor={`rss-url-${index}`}>URL RSS</label>
        <input id={`rss-url-${index}`} type="password" autoComplete="new-password" value={feed.url} required={!feed.id} maxLength={8192}
          placeholder={feed.hasUrl ? "URL enregistrée — laisser vide pour la conserver" : "https://…"}
          onChange={(e) => change(index, { url: e.target.value })} /></div>
      <div className="field"><label className="muted" htmlFor={`rss-name-${index}`}>Nom du flux (facultatif)</label>
        <input id={`rss-name-${index}`} value={feed.name || ""} maxLength={200} onChange={(e) => change(index, { name: e.target.value })} /></div>
      <div className="field"><label className="muted" htmlFor={`rss-category-${index}`}>Catégorie Feedarr</label>
        <select id={`rss-category-${index}`} required value={feed.categoryKey} onChange={(e) => change(index, { categoryKey: e.target.value })}>
          <option value="">Choisir une catégorie...</option>
          {Object.entries(CATEGORY_GROUP_LABELS).map(([key, label]) => <option key={key} value={key}>{label}</option>)}
        </select></div>
      <label><input type="checkbox" checked={feed.enabled} onChange={(e) => change(index, { enabled: e.target.checked })} /> Activé</label>
      {feed.lastStatus === "error" && <div className="errorbox">Dernier échec : {feed.lastError}</div>}
      {feed.potentiallySaturated && <div className="muted">Fenêtre RSS potentiellement saturée : aucune release commune avec la synchronisation précédente.</div>}
      <div className="formactions">
        <button className="btn" type="button" onClick={() => test(index)} disabled={!feed.categoryKey || (!feed.url && !feed.id) || tests[index]?.loading}>
          {tests[index]?.loading ? "Test..." : "Tester le flux"}</button>
        <button className="btn btn-danger" type="button" onClick={() => { setFeeds((rows) => rows.filter((_, i) => i !== index)); setTests({}); }}>Retirer le flux</button>
      </div>
      {tests[index] && !tests[index].loading && <div className="direct-rss-form__diagnostic" role="status">
        {tests[index].ok ? <><div className="muted">Connexion réussie — {tests[index].itemsCount} releases</div>
          <ul className="direct-rss-form__preview">{tests[index].sample?.slice(0, 5).map((item, i) => <li key={i} title={item.title}>{item.title}</li>)}</ul></>
          : <div className="errorbox">{tests[index].error || "Échec du test"}</div>}
      </div>}
    </fieldset>)}
    <div className="formactions"><button className="btn" type="button" disabled={loading || !loaded || saving} onClick={() => setFeeds((rows) => [...rows, emptyFeed()])}>Ajouter un flux</button></div>
    {error && <div className="errorbox" role="alert">{error}</div>}
    <div className="formactions direct-rss-form__actions">
      <button className="btn btn-hover-ok" type="submit" disabled={loading || !loaded || saving}>{saving ? "Enregistrement..." : "Enregistrer"}</button>
      <button className="btn" type="button" disabled={saving} onClick={onCancel}>Annuler</button>
    </div>
  </form>;
}
