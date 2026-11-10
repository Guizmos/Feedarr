const statePath = "/api/system/backups/state";
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));

// Only a server-read manifest with this preparation's identity confirms success.
export async function prepareRestore(name, {
  get, post, confirmReplacement, sleep = delay, now = Date.now, timeoutMs = 30 * 60 * 1000,
}) {
  const before = await get(statePath, { timeoutMs: 15000 });
  if (before?.isBusy || before?.preparation?.state === "running") {
    throw new Error("Une opération de sauvegarde ou de restauration est déjà en cours.");
  }
  const pending = before?.restorePending;
  if (pending?.pending && (pending.state !== "pending" || !pending.id)) {
    throw new Error("État de restauration illisible. Un diagnostic est nécessaire avant de recommencer.");
  }
  if (pending?.pending && !confirmReplacement()) return null;
  const query = pending?.pending ? `&replacePendingId=${encodeURIComponent(pending.id)}` : "";
  const accepted = await post(`/api/system/backups/${encodeURIComponent(name)}/restore?confirm=true&background=true${query}`, {}, { timeoutMs: 15000 });
  if (accepted?.accepted !== true || !/^[a-f0-9]{32}$/i.test(accepted.preparationId || "")) {
    throw new Error("Le serveur n'a pas confirmé la prise en charge de la préparation.");
  }
  const id = accepted.preparationId;
  const deadline = now() + timeoutMs;
  let failures = 0;
  while (now() < deadline) {
    let state;
    try {
      state = await get(statePath, { timeoutMs: 15000 });
      failures = 0;
    } catch {
      if (++failures >= 3) throw new Error("Suivi de préparation indisponible. Elle peut continuer côté serveur ; consultez son état avant de réessayer.");
      await sleep(2000);
      continue;
    }
    const job = state?.preparation;
    const manifest = state?.restorePending;
    if (job?.id === id && job.state === "failed") throw new Error(job.error || "Échec de préparation de la restauration.");
    if (manifest?.pending && manifest.state === "pending" && manifest.id === id) {
      // After restart, the in-memory job can be absent while the durable manifest survives.
      if (!job || job.id !== id || job.state === "completed") return { ...(job?.id === id ? job.result : {}), needsRestart: true, preparationId: id, missingPosters: manifest.missingPosters };
    }
    if (!job || job.id !== id) throw new Error("Préparation interrompue ou remplacée. Aucun manifeste correspondant n'est confirmé.");
    if (job.state === "completed") throw new Error("Le serveur n'a pas confirmé de manifeste de restauration en attente.");
    await sleep(2000);
  }
  throw new Error("Délai de suivi dépassé. La préparation peut continuer côté serveur ; consultez son état avant de réessayer.");
}
