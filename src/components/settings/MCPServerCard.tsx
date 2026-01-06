import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Textarea } from '@/components/ui/textarea';
import { Card, CardContent } from '@/components/ui/card';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import type { McpItem } from './types';

type MCPServerCardProps = {
  item: McpItem;
  envText: string;
  onUpdate: (updates: Partial<McpItem>) => void;
  onEnvChange: (value: string) => void;
  onRemove: () => void;
};

const joinList = (items: string[]) => items.join(', ');

const parseList = (value: string) =>
  value
    .split(/[,\n]/)
    .map((entry) => entry.trim())
    .filter(Boolean);

export function MCPServerCard({ item, envText, onUpdate, onEnvChange, onRemove }: MCPServerCardProps) {
  return (
    <Card className="bg-muted/50">
      <CardContent className="pt-4 space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor={`mcp-name-${item.id}`}>Name</Label>
            <Input
              id={`mcp-name-${item.id}`}
              value={item.name}
              onChange={(e) => onUpdate({ name: e.target.value })}
              placeholder="exa"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor={`mcp-desc-${item.id}`}>Description</Label>
            <Input
              id={`mcp-desc-${item.id}`}
              value={item.description}
              onChange={(e) => onUpdate({ description: e.target.value })}
              placeholder="Web search via Exa"
            />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor={`mcp-cmd-${item.id}`}>Command</Label>
            <Input
              id={`mcp-cmd-${item.id}`}
              value={item.command}
              onChange={(e) => onUpdate({ command: e.target.value })}
              placeholder="npx"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor={`mcp-args-${item.id}`}>Args</Label>
            <Input
              id={`mcp-args-${item.id}`}
              value={joinList(item.args || [])}
              onChange={(e) => onUpdate({ args: parseList(e.target.value) })}
              placeholder="-y, exa-mcp-server"
            />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor={`mcp-url-${item.id}`}>URL</Label>
            <Input
              id={`mcp-url-${item.id}`}
              value={item.url}
              onChange={(e) => onUpdate({ url: e.target.value })}
              placeholder="http://localhost:8000/mcp"
            />
          </div>
          <div className="space-y-2">
            <Label htmlFor={`mcp-transport-${item.id}`}>Transport</Label>
            <NativeSelect
              id={`mcp-transport-${item.id}`}
              value={item.transport}
              onChange={(e) => onUpdate({ transport: e.target.value })}
            >
              <NativeSelectOption value="">Auto</NativeSelectOption>
              <NativeSelectOption value="stdio">stdio</NativeSelectOption>
              <NativeSelectOption value="http">http</NativeSelectOption>
              <NativeSelectOption value="sse">sse</NativeSelectOption>
            </NativeSelect>
          </div>
        </div>
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-2">
            <Label htmlFor={`mcp-env-${item.id}`}>Env (KEY=VALUE per line)</Label>
            <Textarea
              id={`mcp-env-${item.id}`}
              value={envText}
              onChange={(e) => onEnvChange(e.target.value)}
              placeholder="EXA_API_KEY=..."
              className="min-h-[80px]"
            />
          </div>
          <div className="flex items-end justify-end">
            <Button variant="destructive" size="sm" onClick={onRemove}>
              Remove
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
