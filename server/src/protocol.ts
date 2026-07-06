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

export type ClientMessage =
  | ClientChatMessage
  | ClientMcpListMessage
  | ClientMcpAddMessage
  | ClientMcpRemoveMessage
  | ClientSessionsListMessage
  | ClientSessionsHistoryMessage;

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
}

export interface SessionTurnDto {
  turnIndex: number;
  userMessage: string;
  assistantResponse: string;
  timestamp: string;
}

export interface ServerSessionsListResultMessage {
  type: 'sessions:list-result';
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

export type ServerMessage =
  | ServerDeltaMessage
  | ServerFinalMessage
  | ServerErrorMessage
  | ServerToolMessage
  | ServerMcpResultMessage
  | ServerSessionsListResultMessage
  | ServerSessionsHistoryResultMessage;
