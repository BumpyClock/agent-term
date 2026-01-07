import { ChevronLeft } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { NativeSelect, NativeSelectOption } from '@/components/ui/native-select';
import { Switch } from '@/components/ui/switch';

type MCPServerFormProps = {
  mode: 'add' | 'edit';
  name: string;
  command: string;
  args: string;
  description: string;
  url: string;
  transport: string;
  envText: string;
  isPooled: boolean;
  isPoolAllEnabled: boolean;
  onNameChange: (value: string) => void;
  onCommandChange: (value: string) => void;
  onArgsChange: (value: string) => void;
  onDescriptionChange: (value: string) => void;
  onUrlChange: (value: string) => void;
  onTransportChange: (value: string) => void;
  onEnvTextChange: (value: string) => void;
  onIsPooledChange: (value: boolean) => void;
  onSave: () => void;
  onBack: () => void;
  onDelete?: () => void;
  validationError?: string;
};

export function MCPServerForm({
  mode,
  name,
  command,
  args,
  description,
  url,
  transport,
  envText,
  isPooled,
  isPoolAllEnabled,
  onNameChange,
  onCommandChange,
  onArgsChange,
  onDescriptionChange,
  onUrlChange,
  onTransportChange,
  onEnvTextChange,
  onIsPooledChange,
  onSave,
  onBack,
  onDelete,
  validationError,
}: MCPServerFormProps) {
  return (
    <div className="flex flex-col min-h-0 h-full">
      <div className="flex items-center gap-2 mb-4 flex-shrink-0">
        <button
          onClick={onBack}
          className="p-1 -ml-1 rounded hover:bg-muted transition-colors"
        >
          <ChevronLeft className="h-5 w-5" />
        </button>
        <h3 className="text-lg font-semibold">
          {mode === 'add' ? 'Add MCP server' : 'Edit MCP server'}
        </h3>
      </div>

      <div className="flex-1 overflow-y-auto min-h-0 space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <label className="dialog-label">
            Name
            <input
              type="text"
              value={name}
              onChange={(e) => onNameChange(e.target.value)}
              placeholder="exa"
              autoFocus
            />
          </label>
          <label className="dialog-label">
            Description
            <input
              type="text"
              value={description}
              onChange={(e) => onDescriptionChange(e.target.value)}
              placeholder="Web search via Exa"
            />
          </label>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <label className="dialog-label">
            Command
            <input
              type="text"
              value={command}
              onChange={(e) => onCommandChange(e.target.value)}
              placeholder="npx"
            />
          </label>
          <label className="dialog-label">
            Args
            <input
              type="text"
              value={args}
              onChange={(e) => onArgsChange(e.target.value)}
              placeholder="-y, exa-mcp-server"
            />
          </label>
        </div>

        <div className="grid grid-cols-2 gap-4">
          <label className="dialog-label">
            URL
            <input
              type="text"
              value={url}
              onChange={(e) => onUrlChange(e.target.value)}
              placeholder="http://localhost:8000/mcp"
            />
          </label>
          <label className="dialog-label">
            Transport
            <NativeSelect
              value={transport}
              onChange={(e) => onTransportChange(e.target.value)}
            >
              <NativeSelectOption value="">Auto</NativeSelectOption>
              <NativeSelectOption value="stdio">stdio</NativeSelectOption>
              <NativeSelectOption value="http">http</NativeSelectOption>
              <NativeSelectOption value="sse">sse</NativeSelectOption>
            </NativeSelect>
          </label>
        </div>

        <label className="dialog-label">
          Environment (KEY=VALUE per line)
          <textarea
            value={envText}
            onChange={(e) => onEnvTextChange(e.target.value)}
            placeholder="EXA_API_KEY=..."
            className="min-h-[80px] w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
          />
        </label>

        <div className="flex items-center justify-between p-3 rounded-lg border bg-muted/50">
          <div className="flex-1">
            <div className="font-medium">MCP Pool</div>
            <div className="text-sm text-muted-foreground">
              {isPoolAllEnabled
                ? 'All MCPs are pooled (configured in pool settings)'
                : 'Include in MCP socket pool'}
            </div>
          </div>
          <Switch
            checked={isPooled}
            onCheckedChange={onIsPooledChange}
            disabled={isPoolAllEnabled}
          />
        </div>

        {validationError && (
          <div className="text-destructive text-sm">{validationError}</div>
        )}
      </div>

      <div className="flex justify-between pt-4 flex-shrink-0 border-t border-border mt-4">
        <div>
          {mode === 'edit' && onDelete && (
            <Button variant="destructive" onClick={onDelete}>
              Delete
            </Button>
          )}
        </div>
        <div className="flex gap-2">
          <Button variant="outline" onClick={onBack}>
            Cancel
          </Button>
          <Button onClick={onSave}>Save</Button>
        </div>
      </div>
    </div>
  );
}
