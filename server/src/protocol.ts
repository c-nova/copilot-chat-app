/** An inline file (image or document) attached to a chat turn, sent base64-encoded over the wire. */
export interface AttachmentPayload {
  /** MIME type, e.g. "image/png" - used to pick a file extension for the temp file passed to the CLI. */
  mimeType: string;
  /** Base64-encoded file bytes. */
  data: string;
  /** Optional original file name (used for its extension if present). */
  fileName?: string;
}

/** Messages sent from client -> server over the WebSocket. */
export interface ClientChatMessage {
  type: 'chat';
  /** Client-generated id identifying a conversation thread. */
  conversationId: string;
  text: string;
  /** Images/files pasted or attached alongside the prompt (PBI-019). */
  attachments?: AttachmentPayload[];
  /**
   * Working directory to create a brand-new session in (folder-browse/git-clone flow). Only used
   * the first time a given conversationId is seen; ignored (server uses the session's own recorded
   * cwd instead) when resuming an existing session. Must fall within the server's BROWSE_ROOTS or
   * it's rejected in favor of the default workspace directory.
   */
  cwd?: string;
}

export interface ClientMcpListMessage {
  type: 'mcp:list';
  requestId: string;
}

export interface ClientMcpAddMessage {
  type: 'mcp:add';
  requestId: string;
  name: string;
  transport: 'stdio' | 'http' | 'sse';
  command?: string;
  args?: string[];
  url?: string;
  env?: Record<string, string>;
  headers?: Record<string, string>;
}

export interface ClientMcpRemoveMessage {
  type: 'mcp:remove';
  requestId: string;
  name: string;
}

export interface ClientSessionsListMessage {
  type: 'sessions:list';
  requestId: string;
}

export interface ClientSessionsHistoryMessage {
  type: 'sessions:history';
  requestId: string;
  sessionId: string;
}

/** Full-text search across session titles and actual conversation content (see sessionHistory.searchSessions). */
export interface ClientSessionsSearchMessage {
  type: 'sessions:search';
  requestId: string;
  query: string;
}

/** Lists subdirectories of `path` for the new-session folder picker. Omit `path` to start at the top. */
export interface ClientFsListDirMessage {
  type: 'fs:list-dir';
  requestId: string;
  path?: string;
}

/** Clones a git repository into a new subfolder of `parentPath`. */
export interface ClientFsGitCloneMessage {
  type: 'fs:git-clone';
  requestId: string;
  parentPath: string;
  repoUrl: string;
  /** Optional destination folder name; auto-derived from repoUrl if omitted. */
  destName?: string;
}

export interface ClientServerInfoMessage {
  type: 'server:info';
  requestId: string;
}

/** Updates our own sidecar annotations for a session (never touches the CLI's own db). Omit a field to leave it unchanged; pass `label: null` to clear the label. */
export interface ClientSessionsUpdateMetaMessage {
  type: 'sessions:update-meta';
  requestId: string;
  sessionId: string;
  label?: string | null;
  archived?: boolean;
}

export type ClientMessage =
  | ClientChatMessage
  | ClientMcpListMessage
  | ClientMcpAddMessage
  | ClientMcpRemoveMessage
  | ClientSessionsListMessage
  | ClientSessionsHistoryMessage
  | ClientSessionsSearchMessage
  | ClientFsListDirMessage
  | ClientFsGitCloneMessage
  | ClientServerInfoMessage
  | ClientSessionsUpdateMetaMessage;

/** Messages sent from server -> client over the WebSocket. */
export interface ServerDeltaMessage {
  type: 'delta';
  conversationId: string;
  text: string;
}

export interface ServerFinalMessage {
  type: 'final';
  conversationId: string;
  text: string;
}

export interface ServerErrorMessage {
  type: 'error';
  conversationId?: string;
  message: string;
}

export interface ServerToolMessage {
  type: 'tool';
  conversationId: string;
  status: 'start' | 'complete';
  name: string;
  summary?: string;
  detail?: string;
  success?: boolean;
}

export interface McpServerSummary {
  name: string;
  type: string;
  command?: string;
  args?: string[];
  url?: string;
  tools?: string[];
  source?: string;
}

export interface ServerMcpResultMessage {
  type: 'mcp:result';
  requestId: string;
  action: 'list' | 'add' | 'remove';
  ok: boolean;
  servers?: McpServerSummary[];
  server?: McpServerSummary;
  error?: string;
}

export interface SessionSummaryDto {
  id: string;
  /** Working directory this session was created with. */
  cwd: string;
  summary: string;
  createdAt: string;
  updatedAt: string;
  turnCount: number;
  /** Our own sidecar annotations (see sessionMeta.ts) - absent if never set. */
  label?: string;
  archived?: boolean;
}

export interface SessionTurnDto {
  turnIndex: number;
  userMessage: string;
  assistantResponse: string;
  timestamp: string;
  /** True when this turn was dispatched via the session-control MCP's run_turn_on_session tool rather than typed by this session's own human user (see sessionMeta.ts). Absent/false for normal turns. */
  fromOtherSession?: boolean;
}

export interface ServerSessionsListResultMessage {
  type: 'sessions:list-result';
  requestId: string;
  ok: boolean;
  sessions?: SessionSummaryDto[];
  error?: string;
}

export interface ServerSessionsSearchResultMessage {
  type: 'sessions:search-result';
  requestId: string;
  ok: boolean;
  sessions?: SessionSummaryDto[];
  error?: string;
}

export interface ServerSessionsHistoryResultMessage {
  type: 'sessions:history-result';
  requestId: string;
  ok: boolean;
  sessionId?: string;
  turns?: SessionTurnDto[];
  error?: string;
}

export interface FsEntryDto {
  name: string;
  path: string;
  isDir: boolean;
}

export interface ServerFsListDirResultMessage {
  type: 'fs:list-dir-result';
  requestId: string;
  ok: boolean;
  path?: string | null;
  parentPath?: string | null;
  entries?: FsEntryDto[];
  roots?: string[];
  error?: string;
}

export interface ServerFsGitCloneResultMessage {
  type: 'fs:git-clone-result';
  requestId: string;
  ok: boolean;
  path?: string;
  error?: string;
}

export interface ServerInfoDto {
  os: string;
  hostname: string;
  appVersion: string;
  copilotCliVersion: string;
  nodeVersion: string;
  model: string;
  workDir: string;
  browseRoots: string[];
}

export interface ServerInfoResultMessage {
  type: 'server:info-result';
  requestId: string;
  ok: boolean;
  info?: ServerInfoDto;
  error?: string;
}

export interface ServerSessionsUpdateMetaResultMessage {
  type: 'sessions:update-meta-result';
  requestId: string;
  ok: boolean;
  label?: string;
  archived?: boolean;
  error?: string;
}

export type ServerMessage =
  | ServerDeltaMessage
  | ServerFinalMessage
  | ServerErrorMessage
  | ServerToolMessage
  | ServerMcpResultMessage
  | ServerSessionsListResultMessage
  | ServerSessionsHistoryResultMessage
  | ServerSessionsSearchResultMessage
  | ServerFsListDirResultMessage
  | ServerFsGitCloneResultMessage
  | ServerInfoResultMessage
  | ServerSessionsUpdateMetaResultMessage;
