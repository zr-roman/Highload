import { useCallback, useEffect, useRef, useState } from "react";

interface RequestState<T> {
  data: T | null;
  error: unknown;
  loading: boolean;
}

interface RunOptions {
  /** Keep the shown data while the request runs and if it fails: a refresh, not a new search. */
  keepData?: boolean;
}

/**
 * One request at a time per panel: loading, the data or the error, with the previous request aborted when a new one
 * starts and the current one aborted when the panel goes away. An aborted request changes nothing on the screen.
 */
export function useRequest<T>() {
  const [state, setState] = useState<RequestState<T>>({ data: null, error: null, loading: false });
  const active = useRef<AbortController | null>(null);

  useEffect(() => () => active.current?.abort(), []);

  const run = useCallback(async (call: (signal: AbortSignal) => Promise<T>, { keepData = false }: RunOptions = {}) => {
    active.current?.abort();
    const controller = new AbortController();
    active.current = controller;
    setState((previous) => ({ data: keepData ? previous.data : null, error: null, loading: true }));

    try {
      const data = await call(controller.signal);
      if (!controller.signal.aborted) setState({ data, error: null, loading: false });
    } catch (error) {
      if (!controller.signal.aborted) {
        setState((previous) => ({ data: keepData ? previous.data : null, error, loading: false }));
      }
    } finally {
      if (active.current === controller) active.current = null;
    }
  }, []);

  /** An error found before any request, such as invalid input: shown the same way, nothing is sent. */
  const fail = useCallback((error: unknown) => {
    active.current?.abort();
    setState({ data: null, error, loading: false });
  }, []);

  return { ...state, run, fail };
}
