interface SdkModelInfo {
  id: string;
  name: string;
  policy?: { state: string };
  capabilities: {
    supports: {
      vision: boolean;
      reasoningEffort: boolean;
    };
  };
  billing?: { multiplier?: number };
}

interface ModelCatalogClient {
  start(): Promise<void>;
  stop(): Promise<Error[]>;
  listModels(): Promise<SdkModelInfo[]>;
}

export interface ModelCatalogEntry {
  id: string;
  name: string;
  policy?: string;
  supportsVision: boolean;
  supportsReasoningEffort: boolean;
  billingMultiplier?: number;
}

let clientPromise: Promise<ModelCatalogClient> | undefined;

async function createClient(): Promise<ModelCatalogClient> {
  const { CopilotClient } = await import('@github/copilot-sdk');
  const client = new CopilotClient({ useLoggedInUser: true });
  try {
    await client.start();
    return client;
  } catch (error) {
    await client.stop().catch(() => []);
    throw error;
  }
}

async function getClient(): Promise<ModelCatalogClient> {
  if (!clientPromise) {
    clientPromise = createClient().catch((error) => {
      clientPromise = undefined;
      throw error;
    });
  }
  return clientPromise;
}

export function toModelCatalogEntry(model: SdkModelInfo): ModelCatalogEntry {
  return {
    id: model.id,
    name: model.name,
    policy: model.policy?.state,
    supportsVision: model.capabilities.supports.vision,
    supportsReasoningEffort: model.capabilities.supports.reasoningEffort,
    billingMultiplier: model.billing?.multiplier,
  };
}

/** Returns the SDK/CLI account catalog. The SDK owns the successful-result cache. */
export async function listAvailableModels(): Promise<ModelCatalogEntry[]> {
  const client = await getClient();
  return (await client.listModels()).map(toModelCatalogEntry);
}

export async function stopModelCatalog(): Promise<void> {
  const pendingClient = clientPromise;
  clientPromise = undefined;
  if (!pendingClient) return;

  try {
    const client = await pendingClient;
    const errors = await client.stop();
    for (const error of errors) {
      console.warn('[modelCatalog] SDK cleanup failed:', error.message);
    }
  } catch {
    // A failed startup already performs best-effort cleanup in createClient().
  }
}
