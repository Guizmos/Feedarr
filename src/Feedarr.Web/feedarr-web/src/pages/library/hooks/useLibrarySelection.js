import { useCallback, useEffect, useState } from "react";

/**
 * Hook pour gérer le mode sélection de la bibliothèque
 */
export default function useLibrarySelection() {
  const [selectionMode, setSelectionMode] = useState(false);
  const [selectedIds, setSelectedIds] = useState(() => new Set());

  // Ajouter/retirer la classe CSS sur le body
  useEffect(() => {
    document.body.classList.toggle("library-select-mode", selectionMode);
    return () => document.body.classList.remove("library-select-mode");
  }, [selectionMode]);

  const toggleSelectionMode = useCallback(() => {
    setSelectionMode((v) => {
      const next = !v;
      if (!next) setSelectedIds(new Set());
      return next;
    });
  }, []);

  const toggleSelect = useCallback((it) => {
    if (!it?.id) return;
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(it.id)) next.delete(it.id);
      else next.add(it.id);
      return next;
    });
  }, []);

  const clearSelection = useCallback(() => {
    setSelectedIds(new Set());
  }, []);

  const selectAllVisible = useCallback((visibleItems) => {
    const ids = (visibleItems || []).map((it) => it.id).filter(Boolean);
    setSelectedIds(new Set(ids));
  }, []);

  const exitSelectionMode = useCallback(() => {
    setSelectionMode(false);
    setSelectedIds(new Set());
  }, []);

  return {
    selectionMode,
    selectedIds,
    toggleSelectionMode,
    toggleSelect,
    clearSelection,
    selectAllVisible,
    exitSelectionMode,
  };
}
