import { useCallback, useState } from "react";

export default function useIndexerModal({
  onPrepareAdd,
  onPrepareEdit,
  onAfterClose,
} = {}) {
  const [modalOpen, setModalOpen] = useState(false);
  const [editing, setEditing] = useState(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [confirmTarget, setConfirmTarget] = useState(null);
  const [modalStep, setModalStep] = useState(1);

  const openAdd = useCallback(() => {
    setEditing(null);
    setModalStep(1);
    onPrepareAdd?.();
    setModalOpen(true);
  }, [onPrepareAdd]);

  const openEdit = useCallback((source) => {
    setEditing(source);
    setModalStep(1);
    onPrepareEdit?.(source);
    setModalOpen(true);
  }, [onPrepareEdit]);

  const closeModal = useCallback(() => {
    setModalOpen(false);
    onAfterClose?.();
  }, [onAfterClose]);

  const openDeleteConfirm = useCallback((source) => {
    setConfirmTarget(source);
    setConfirmOpen(true);
  }, []);

  const closeDeleteConfirm = useCallback(() => {
    setConfirmOpen(false);
    setConfirmTarget(null);
  }, []);

  return {
    modalOpen,
    editing,
    confirmOpen,
    confirmTarget,
    modalStep,
    setModalStep,
    openAdd,
    openEdit,
    closeModal,
    openDeleteConfirm,
    closeDeleteConfirm,
  };
}
