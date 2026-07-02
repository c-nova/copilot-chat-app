import * as fs from 'fs';
import { config } from './config';
import { createChatServer } from './wsServer';

fs.mkdirSync(config.workDir, { recursive: true });
console.log(`Copilot agent working directory: ${config.workDir}`);

createChatServer();
