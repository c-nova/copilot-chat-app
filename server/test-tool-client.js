const WebSocket = require('ws');

const ws = new WebSocket('ws://localhost:5219', {
  headers: { Authorization: 'Bearer test-secret-token-123' },
});

const conversationId = 'test-tool-conv';

ws.on('open', () => {
  console.log('connected, asking to create a file via shell');
  ws.send(JSON.stringify({
    type: 'chat',
    conversationId,
    text: 'Create a file called hello.txt containing the text "hello from copilot" using the shell tool, then confirm.',
  }));
});

ws.on('message', (data) => {
  const msg = JSON.parse(data.toString());
  if (msg.type === 'tool') {
    console.log(`\n[TOOL ${msg.status}] ${msg.name}${msg.summary ? ' - ' + msg.summary : ''}${msg.success !== undefined ? ' success=' + msg.success : ''}`);
  } else if (msg.type === 'delta') {
    process.stdout.write(msg.text);
  } else if (msg.type === 'final') {
    console.log('\n[FINAL]', msg.text);
    ws.close();
    process.exit(0);
  } else if (msg.type === 'error') {
    console.error('[ERROR]', msg.message);
    process.exit(1);
  }
});

ws.on('error', (err) => {
  console.error('WS error:', err);
  process.exit(1);
});
