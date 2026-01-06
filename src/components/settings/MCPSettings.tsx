import { Button } from '@/components/ui/button';
import { Card, CardHeader, CardTitle, CardDescription, CardContent } from '@/components/ui/card';
import { MCPServerCard } from './MCPServerCard';
import { PoolSettings } from './PoolSettings';
import type { McpItem, MCPPoolSettings } from './types';

type MCPSettingsProps = {
  mcps: McpItem[];
  pool: MCPPoolSettings;
  envText: Record<string, string>;
  onMcpsChange: (mcps: McpItem[]) => void;
  onPoolChange: (updates: Partial<MCPPoolSettings>) => void;
  onEnvTextChange: (id: string, value: string) => void;
  onAddMcp: () => void;
  onRemoveMcp: (index: number) => void;
};

export function MCPSettings({
  mcps,
  pool,
  envText,
  onMcpsChange,
  onPoolChange,
  onEnvTextChange,
  onAddMcp,
  onRemoveMcp,
}: MCPSettingsProps) {
  const updateMcp = (index: number, updates: Partial<McpItem>) => {
    const updated = mcps.map((item, i) => (i === index ? { ...item, ...updates } : item));
    onMcpsChange(updated);
  };

  return (
    <div className="space-y-6">
      {/* MCP Servers Section */}
      <Card>
        <CardHeader className="pb-3">
          <div className="flex items-center justify-between">
            <div>
              <CardTitle className="text-base">MCP servers</CardTitle>
              <CardDescription>Configure MCP definitions shared across all projects</CardDescription>
            </div>
            <Button size="sm" onClick={onAddMcp}>
              + Add MCP
            </Button>
          </div>
        </CardHeader>
        <CardContent className="space-y-4">
          {mcps.length === 0 && (
            <div className="text-muted-foreground text-sm">No MCPs configured yet</div>
          )}
          {mcps.map((item, index) => (
            <MCPServerCard
              key={item.id}
              item={item}
              envText={envText[item.id] || ''}
              onUpdate={(updates) => updateMcp(index, updates)}
              onEnvChange={(value) => onEnvTextChange(item.id, value)}
              onRemove={() => onRemoveMcp(index)}
            />
          ))}
        </CardContent>
      </Card>

      {/* MCP Socket Pool Section */}
      <PoolSettings pool={pool} onPoolChange={onPoolChange} />
    </div>
  );
}
