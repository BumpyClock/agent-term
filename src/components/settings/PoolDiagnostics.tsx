import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { Button } from '@/components/ui/button';
import { StatusDot } from './StatusDot';
import type { PoolStatusResponse, McpServerStatus } from '@/components/sidebar/settingsTypes';

type PoolDiagnosticsProps = {
  status: PoolStatusResponse | null;
  isLoading: boolean;
  error: string | null;
  onRefresh: () => void;
  onRestartServer: (name: string) => Promise<boolean>;
  onStopServer: (name: string) => Promise<boolean>;
  onStartServer: (name: string) => Promise<boolean>;
};

function formatUptime(seconds: number | null): string {
  if (seconds === null) return '-';
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const secs = seconds % 60;
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${secs}s`;
  }
  return `${secs}s`;
}

function ServerRow({
  server,
  onRestart,
  onStop,
  onStart,
}: {
  server: McpServerStatus;
  onRestart: () => void;
  onStop: () => void;
  onStart: () => void;
}) {
  const isRunning = server.status === 'Running';
  const isStarting = server.status === 'Starting';
  const canControl = server.owned;

  return (
    <tr className="border-b border-border/50 last:border-0">
      <td className="py-2 px-3">
        <div className="flex items-center gap-2">
          <StatusDot status={server.status} />
          <span className="font-medium">{server.name}</span>
          {!server.owned && (
            <span className="text-xs text-muted-foreground">(external)</span>
          )}
        </div>
      </td>
      <td className="py-2 px-3 text-sm text-muted-foreground">
        {server.status}
      </td>
      <td className="py-2 px-3 text-muted-foreground font-mono text-xs max-w-[200px] truncate">
        {server.socketPath}
      </td>
      <td className="py-2 px-3 text-sm text-muted-foreground">
        {formatUptime(server.uptimeSeconds)}
      </td>
      <td className="py-2 px-3 text-sm text-muted-foreground text-center">
        {server.connectionCount}
      </td>
      <td className="py-2 px-3">
        {canControl && (
          <div className="flex items-center gap-1">
            {isRunning || isStarting ? (
              <>
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-7 px-2 text-xs"
                  onClick={onRestart}
                  disabled={isStarting}
                >
                  Restart
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  className="h-7 px-2 text-xs"
                  onClick={onStop}
                  disabled={isStarting}
                >
                  Stop
                </Button>
              </>
            ) : (
              <Button
                variant="ghost"
                size="sm"
                className="h-7 px-2 text-xs"
                onClick={onStart}
              >
                Start
              </Button>
            )}
          </div>
        )}
      </td>
    </tr>
  );
}

export function PoolDiagnostics({
  status,
  isLoading,
  error,
  onRefresh,
  onRestartServer,
  onStopServer,
  onStartServer,
}: PoolDiagnosticsProps) {
  if (!status?.enabled) {
    return (
      <Card>
        <CardHeader className="pb-3">
          <CardTitle className="text-base">Pool Diagnostics</CardTitle>
          <CardDescription>MCP socket pool is not enabled</CardDescription>
        </CardHeader>
      </Card>
    );
  }

  return (
    <Card>
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div>
            <CardTitle className="text-base">Pool Diagnostics</CardTitle>
            <CardDescription>
              {status.serverCount} server{status.serverCount !== 1 ? 's' : ''} in pool
            </CardDescription>
          </div>
          <Button variant="outline" size="sm" onClick={onRefresh} disabled={isLoading}>
            {isLoading ? 'Refreshing...' : 'Refresh'}
          </Button>
        </div>
      </CardHeader>
      <CardContent>
        {error && (
          <div className="text-destructive text-sm mb-4">{error}</div>
        )}
        {status.servers.length === 0 ? (
          <div className="text-muted-foreground text-sm">
            No MCP servers currently in the pool
          </div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border">
                  <th className="py-2 px-3 text-left font-medium">Server</th>
                  <th className="py-2 px-3 text-left font-medium">Status</th>
                  <th className="py-2 px-3 text-left font-medium">Socket</th>
                  <th className="py-2 px-3 text-left font-medium">Uptime</th>
                  <th className="py-2 px-3 text-center font-medium">Conn.</th>
                  <th className="py-2 px-3 text-left font-medium">Actions</th>
                </tr>
              </thead>
              <tbody>
                {status.servers.map((server) => (
                  <ServerRow
                    key={server.name}
                    server={server}
                    onRestart={() => onRestartServer(server.name)}
                    onStop={() => onStopServer(server.name)}
                    onStart={() => onStartServer(server.name)}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
