import { useMemo } from 'react';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import type { MCPPoolSettings } from './types';

type PoolSettingsProps = {
  pool: MCPPoolSettings;
  onPoolChange: (updates: Partial<MCPPoolSettings>) => void;
};

const joinList = (items: string[]) => items.join(', ');

const parseList = (value: string) =>
  value
    .split(/[,\n]/)
    .map((entry) => entry.trim())
    .filter(Boolean);

export function PoolSettings({ pool, onPoolChange }: PoolSettingsProps) {
  const poolMcpsText = useMemo(() => joinList(pool.poolMcps || []), [pool.poolMcps]);
  const excludeMcpsText = useMemo(() => joinList(pool.excludeMcps || []), [pool.excludeMcps]);

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-base">MCP socket pool</CardTitle>
        <CardDescription>Share MCP processes across agents to save memory</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-4 sm:grid-cols-3">
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-enabled"
              checked={pool.enabled}
              onCheckedChange={(checked) => onPoolChange({ enabled: checked === true })}
            />
            <Label htmlFor="pool-enabled" className="text-sm font-normal">
              Enable socket pool
            </Label>
          </div>
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-autostart"
              checked={pool.autoStart}
              onCheckedChange={(checked) => onPoolChange({ autoStart: checked === true })}
            />
            <Label htmlFor="pool-autostart" className="text-sm font-normal">
              Auto-start pool
            </Label>
          </div>
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-ondemand"
              checked={pool.startOnDemand}
              onCheckedChange={(checked) => onPoolChange({ startOnDemand: checked === true })}
            />
            <Label htmlFor="pool-ondemand" className="text-sm font-normal">
              Start on demand
            </Label>
          </div>
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-shutdown"
              checked={pool.shutdownOnExit}
              onCheckedChange={(checked) => onPoolChange({ shutdownOnExit: checked === true })}
            />
            <Label htmlFor="pool-shutdown" className="text-sm font-normal">
              Shutdown on exit
            </Label>
          </div>
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-all"
              checked={pool.poolAll}
              onCheckedChange={(checked) => onPoolChange({ poolAll: checked === true })}
            />
            <Label htmlFor="pool-all" className="text-sm font-normal">
              Pool all MCPs
            </Label>
          </div>
          <div className="flex items-center space-x-2">
            <Checkbox
              id="pool-fallback"
              checked={pool.fallbackToStdio}
              onCheckedChange={(checked) => onPoolChange({ fallbackToStdio: checked === true })}
            />
            <Label htmlFor="pool-fallback" className="text-sm font-normal">
              Fallback to stdio
            </Label>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor="pool-mcps">Pool MCPs (comma or newline separated)</Label>
            <Input
              id="pool-mcps"
              value={poolMcpsText}
              onChange={(e) => onPoolChange({ poolMcps: parseList(e.target.value) })}
              placeholder="exa, memory, firecrawl"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="exclude-mcps">Exclude MCPs</Label>
            <Input
              id="exclude-mcps"
              value={excludeMcpsText}
              onChange={(e) => onPoolChange({ excludeMcps: parseList(e.target.value) })}
              placeholder="chrome-devtools"
            />
          </div>
        </div>
        <div className="grid grid-cols-3 gap-4 items-end">
          <div className="space-y-2">
            <Label htmlFor="port-start">Port start</Label>
            <Input
              id="port-start"
              type="number"
              value={pool.portStart}
              onChange={(e) => onPoolChange({ portStart: Number(e.target.value || 0) })}
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor="port-end">Port end</Label>
            <Input
              id="port-end"
              type="number"
              value={pool.portEnd}
              onChange={(e) => onPoolChange({ portEnd: Number(e.target.value || 0) })}
            />
          </div>
          <div className="flex items-center space-x-2 pb-2">
            <Checkbox
              id="pool-status"
              checked={pool.showPoolStatus}
              onCheckedChange={(checked) => onPoolChange({ showPoolStatus: checked === true })}
            />
            <Label htmlFor="pool-status" className="text-sm font-normal">
              Show pool status
            </Label>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
