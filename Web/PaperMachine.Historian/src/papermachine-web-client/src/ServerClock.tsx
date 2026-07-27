import {
  createContext,
  useCallback,
  useContext,
  useMemo,
  type ReactNode,
} from "react";

type ServerClockValue = {
  now: () => Date;
  offsetMilliseconds: number | null;
};

const ServerClockContext = createContext<ServerClockValue>({
  now: () => new Date(),
  offsetMilliseconds: null,
});

export function ServerClockProvider({
  serverTimeUtc,
  children,
}: {
  serverTimeUtc: string | null | undefined;
  children: ReactNode;
}) {
  const offsetMilliseconds = useMemo(() => {
    if (!serverTimeUtc) return null;
    const serverTime = new Date(serverTimeUtc).getTime();
    return Number.isFinite(serverTime) ? serverTime - Date.now() : null;
  }, [serverTimeUtc]);
  const now = useCallback(
    () => new Date(Date.now() + (offsetMilliseconds ?? 0)),
    [offsetMilliseconds],
  );

  return (
    <ServerClockContext.Provider value={{ now, offsetMilliseconds }}>
      {children}
    </ServerClockContext.Provider>
  );
}

export function useServerClock() {
  return useContext(ServerClockContext);
}
