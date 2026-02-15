export function getErrorMessage(error, fallbackMessage = "Une erreur est survenue") {
  const message = error?.message;
  if (typeof message === "string" && message.trim()) {
    return message.trim();
  }
  return fallbackMessage;
}

export async function executeAsync(action, options = {}) {
  const {
    context = "Async operation failed",
    setError,
    fallbackMessage = "Une erreur est survenue",
    clearError,
    onError,
    onFinally,
    rethrow = false,
  } = options;

  if (typeof clearError === "function") {
    clearError();
  }

  try {
    return await action();
  } catch (error) {
    // Silently ignore aborted requests
    if (options.ignoreAbort && error?.name === "AbortError") return null;

    console.error(context, error);
    const message = getErrorMessage(error, fallbackMessage);

    if (typeof setError === "function") {
      setError(message);
    }
    if (typeof onError === "function") {
      onError(error, message);
    }

    if (rethrow) {
      throw error;
    }
    return null;
  } finally {
    if (typeof onFinally === "function") {
      onFinally();
    }
  }
}
