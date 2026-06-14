#!/usr/bin/env node

/**
 * projectFrameCut MCP Skill
 * 用于 GitHub Copilot CLI 的 projectFrameCut MCP 服务器集成 Skill
 */

import { execSync, spawn, exec } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

interface CommandArgs {
  project?: string;
  transport?: 'stdio' | 'http';
  port?: number;
  bg?: boolean;
  all?: boolean;
  case?: string;
  language?: 'python' | 'js';
  output?: string;
  endpoint?: string;
  script?: string;
  [key: string]: any;
}

class ProjectFrameCutMCPSkill {
  private projectRoot: string;
  private mcpServerPath: string;

  constructor(projectRoot: string) {
    this.projectRoot = projectRoot;
    this.mcpServerPath = path.join(
      projectRoot,
      'projectFrameCut.McpServer'
    );
  }

  async execute(command: string, args: CommandArgs): Promise<void> {
    switch (command) {
      case 'start':
        await this.handleStart(args);
        break;
      case 'stop':
        await this.handleStop(args);
        break;
      case 'test':
        await this.handleTest(args);
        break;
      case 'generate-client':
        await this.handleGenerateClient(args);
        break;
      case 'project-info':
        await this.handleProjectInfo(args);
        break;
      case 'batch-edit':
        await this.handleBatchEdit(args);
        break;
      case 'status':
        await this.handleStatus(args);
        break;
      case 'help':
      case '--help':
      case '-h':
        this.printHelp();
        break;
      default:
        console.error(`❌ Unknown command: ${command}`);
        this.printHelp();
        process.exit(1);
    }
  }

  private async handleStart(args: CommandArgs): Promise<void> {
    const project = args.project;
    const transport = args.transport || 'stdio';
    const port = args.port || 32123;
    const bg = args.bg || false;

    if (!project) {
      throw new Error('❌ --project is required');
    }

    const projectPath = path.resolve(project);
    if (!fs.existsSync(projectPath)) {
      throw new Error(`❌ Project path not found: ${projectPath}`);
    }

    const requiredFiles = ['project.pjfc', 'timeline.json'];
    for (const file of requiredFiles) {
      if (!fs.existsSync(path.join(projectPath, file))) {
        throw new Error(
          `❌ Missing required file: ${file} in ${projectPath}`
        );
      }
    }

    const transportArg = transport === 'http' ? `--http --port ${port}` : '--stdio';
    const cmd = `dotnet run --project "${this.mcpServerPath}" -- --project "${projectPath}" ${transportArg}`;

    console.log('🚀 Starting MCP server...');
    console.log(`📍 Project: ${projectPath}`);
    console.log(`🔌 Transport: ${transport}${transport === 'http' ? ` (port ${port})` : ''}`);

    if (bg) {
      const childProcess = spawn('cmd', ['/c', cmd], {
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
      });
      childProcess.unref();
      console.log(`✅ Server started in background (PID: ${childProcess.pid})`);
      console.log(
        `💡 Tip: Use 'projectFrameCut-mcp status' to check server status`
      );
    } else {
      console.log('\n--- MCP Server Output ---\n');
      try {
        execSync(cmd, { stdio: 'inherit' });
      } catch (e) {
        console.error('❌ Server exited with error');
        process.exit(1);
      }
    }
  }

  private async handleStop(args: CommandArgs): Promise<void> {
    const all = args.all || false;

    console.log('🛑 Stopping MCP server...');

    if (all) {
      try {
        if (process.platform === 'win32') {
          execSync('taskkill /IM dotnet.exe /F 2>nul || echo No dotnet process found', {
            stdio: 'inherit',
          });
        } else {
          execSync('pkill -f "dotnet.*McpServer" || echo "No process found"', {
            stdio: 'inherit',
          });
        }
        console.log('✅ All MCP server instances stopped');
      } catch (e) {
        console.log('⚠️  No running servers found');
      }
    } else {
      console.log('💡 Tip: Use --all to stop all dotnet processes');
    }
  }

  private async handleTest(args: CommandArgs): Promise<void> {
    const testCase = args.case;
    const project = args.project;

    console.log('🧪 Test Cases Reference');
    console.log('========================\n');

    const testDocPath = path.join(this.mcpServerPath, 'MCP_TESTS.md');
    if (fs.existsSync(testDocPath)) {
      const content = fs.readFileSync(testDocPath, 'utf-8');
      console.log(content);
    } else {
      console.log(
        '⚠️  Test documentation not found at ' + testDocPath
      );
    }

    console.log('\n💡 Tip: Start the server first, then use a REST client to send requests');
  }

  private async handleGenerateClient(args: CommandArgs): Promise<void> {
    const language = args.language;
    const output = args.output;

    if (!language || !output) {
      throw new Error('❌ --language and --output are required');
    }

    if (!['python', 'js'].includes(language)) {
      throw new Error('❌ Language must be python or js');
    }

    console.log(`📝 Generating ${language} client...`);

    const outputPath = path.resolve(output);
    const outputDir = path.dirname(outputPath);

    if (!fs.existsSync(outputDir)) {
      fs.mkdirSync(outputDir, { recursive: true });
    }

    if (language === 'python') {
      const pythonClient = `#!/usr/bin/env python3
"""projectFrameCut MCP Python Client"""
import json
import requests
from typing import Any, Dict, Optional

class MCPClient:
    def __init__(self, endpoint: str = "http://localhost:32123"):
        self.endpoint = endpoint
        self.session = requests.Session()
    
    def call_tool(self, tool_name: str, arguments: Optional[Dict] = None) -> Dict[str, Any]:
        """Call an MCP tool"""
        if arguments is None:
            arguments = {}
        
        payload = {
            "jsonrpc": "2.0",
            "method": "tools/call",
            "params": {
                "name": tool_name,
                "arguments": arguments
            },
            "id": 1
        }
        
        response = self.session.post(self.endpoint, json=payload)
        response.raise_for_status()
        return response.json()
    
    def list_clips(self) -> Dict[str, Any]:
        return self.call_tool("list_clips")
    
    def get_clip(self, clip_id: str) -> Dict[str, Any]:
        return self.call_tool("get_clip", {"clipId": clip_id})
    
    def upsert_clip(self, clip: Dict[str, Any]) -> Dict[str, Any]:
        return self.call_tool("upsert_clip", {"clip": clip})
    
    def move_clip(self, clip_id: str, layer_index: int, start_frame: int) -> Dict[str, Any]:
        return self.call_tool("move_clip", {
            "clipId": clip_id,
            "layerIndex": layer_index,
            "startFrame": start_frame
        })
    
    def patch_clip(self, clip_id: str, patch: Dict[str, Any]) -> Dict[str, Any]:
        return self.call_tool("patch_clip", {
            "clipId": clip_id,
            "patch": patch
        })
    
    def delete_clip(self, clip_id: str) -> Dict[str, Any]:
        return self.call_tool("delete_clip", {"clipId": clip_id})
    
    def add_effect(self, clip_id: str, effect: Dict[str, Any]) -> Dict[str, Any]:
        return self.call_tool("add_effect", {
            "clipId": clip_id,
            "effect": effect
        })
    
    def remove_effect(self, clip_id: str, effect_name: str) -> Dict[str, Any]:
        return self.call_tool("remove_effect", {
            "clipId": clip_id,
            "effectName": effect_name
        })
    
    def get_timeline_info(self) -> Dict[str, Any]:
        return self.call_tool("get_timeline_info")
    
    def list_layers(self) -> Dict[str, Any]:
        return self.call_tool("list_layers")
    
    def list_available_effects(self) -> Dict[str, Any]:
        return self.call_tool("list_available_effects")
    
    def save_project(self, change_reason: str) -> Dict[str, Any]:
        return self.call_tool("save_project", {"changeReason": change_reason})

if __name__ == "__main__":
    import sys
    
    client = MCPClient()
    
    # Example: Get timeline info
    try:
        info = client.get_timeline_info()
        print(json.dumps(info, indent=2))
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(1)
`;
      fs.writeFileSync(outputPath, pythonClient);
      console.log(`✅ Python client generated at: ${outputPath}`);
    } else {
      const jsClient = `#!/usr/bin/env node
/**
 * projectFrameCut MCP JavaScript Client
 */

import fetch from 'node-fetch';

class MCPClient {
  constructor(endpoint = 'http://localhost:32123') {
    this.endpoint = endpoint;
  }

  async callTool(toolName, args = {}) {
    const payload = {
      jsonrpc: '2.0',
      method: 'tools/call',
      params: {
        name: toolName,
        arguments: args
      },
      id: 1
    };

    const response = await fetch(this.endpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    });

    if (!response.ok) {
      throw new Error(\`HTTP \${response.status}: \${response.statusText}\`);
    }

    return response.json();
  }

  listClips() {
    return this.callTool('list_clips');
  }

  getClip(clipId) {
    return this.callTool('get_clip', { clipId });
  }

  upsertClip(clip) {
    return this.callTool('upsert_clip', { clip });
  }

  moveClip(clipId, layerIndex, startFrame) {
    return this.callTool('move_clip', {
      clipId,
      layerIndex,
      startFrame
    });
  }

  patchClip(clipId, patch) {
    return this.callTool('patch_clip', { clipId, patch });
  }

  deleteClip(clipId) {
    return this.callTool('delete_clip', { clipId });
  }

  addEffect(clipId, effect) {
    return this.callTool('add_effect', { clipId, effect });
  }

  removeEffect(clipId, effectName) {
    return this.callTool('remove_effect', { clipId, effectName });
  }

  getTimelineInfo() {
    return this.callTool('get_timeline_info');
  }

  listLayers() {
    return this.callTool('list_layers');
  }

  listAvailableEffects() {
    return this.callTool('list_available_effects');
  }

  saveProject(changeReason) {
    return this.callTool('save_project', { changeReason });
  }
}

export default MCPClient;

// Example usage
if (import.meta.url === \`file://\${process.argv[1]}\`) {
  const client = new MCPClient();
  
  (async () => {
    try {
      const info = await client.getTimelineInfo();
      console.log(JSON.stringify(info, null, 2));
    } catch (err) {
      console.error('Error:', err.message);
      process.exit(1);
    }
  })();
}
`;
      fs.writeFileSync(outputPath, jsClient);
      console.log(`✅ JavaScript client generated at: ${outputPath}`);
    }

    console.log('\n💡 Next steps:');
    if (language === 'python') {
      console.log('  pip install requests');
      console.log(`  python ${outputPath}`);
    } else {
      console.log('  npm install node-fetch');
      console.log(`  node ${outputPath}`);
    }
  }

  private async handleProjectInfo(args: CommandArgs): Promise<void> {
    const project = args.project;

    if (!project) {
      throw new Error('❌ --project is required');
    }

    const projectPath = path.resolve(project);
    const pjfcFile = path.join(projectPath, 'project.pjfc');
    const timelineFile = path.join(projectPath, 'timeline.json');

    if (!fs.existsSync(pjfcFile) || !fs.existsSync(timelineFile)) {
      throw new Error(`❌ Project files not found in ${projectPath}`);
    }

    const pjfc = JSON.parse(fs.readFileSync(pjfcFile, 'utf-8'));
    const timeline = JSON.parse(fs.readFileSync(timelineFile, 'utf-8'));

    console.log('\n📊 Project Information');
    console.log('======================');
    console.log(`Name: ${pjfc.projectName}`);
    console.log(
      `Resolution: ${pjfc.relativeWidth}x${pjfc.relativeHeight}`
    );
    console.log(`Frame Rate: ${pjfc.targetFrameRate} fps`);
    console.log(`Clips: ${timeline.clips.length}`);
    console.log(`Last Changed: ${timeline.changeReason}`);
    console.log(`Saved At: ${timeline.savedAt}`);
    console.log(`Project Path: ${projectPath}\n`);
  }

  private async handleBatchEdit(args: CommandArgs): Promise<void> {
    const script = args.script;
    const project = args.project;

    if (!script || !project) {
      throw new Error('❌ --script and --project are required');
    }

    if (!fs.existsSync(script)) {
      throw new Error(`❌ Script file not found: ${script}`);
    }

    console.log('📋 Batch Edit Script');
    console.log('====================');
    console.log(`Script: ${script}`);
    console.log(`Project: ${project}\n`);

    const operations = JSON.parse(fs.readFileSync(script, 'utf-8'));
    console.log(`Operations: ${operations.length}`);

    for (let i = 0; i < operations.length; i++) {
      const op = operations[i];
      console.log(`\n[${i + 1}] ${op.operation || 'unknown'}`);
      if (op.params) {
        console.log(`    ${JSON.stringify(op.params, null, 6)}`);
      }
    }

    console.log('\n💡 Tip: Run with MCP server to execute these operations');
  }

  private async handleStatus(args: CommandArgs): Promise<void> {
    const endpoint = args.endpoint || 'http://localhost:32123';

    console.log(`\n🔍 Checking MCP server at ${endpoint}...`);

    try {
      const response = await fetch(endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          jsonrpc: '2.0',
          method: 'tools/list',
          params: null,
          id: 1
        })
      });

      if (response.ok) {
        const data = await response.json();
        console.log('✅ Server is running and responding\n');
        console.log('📋 Available Tools:');
        if (data.result && data.result.tools) {
          for (const tool of data.result.tools) {
            console.log(`  • ${tool.name}`);
          }
        }
      } else {
        console.log(`⚠️  Server returned HTTP ${response.status}`);
      }
    } catch (error) {
      console.log('❌ Server is not responding');
      console.log(`💡 Tip: Start the server with: projectFrameCut-mcp start --project <path> --bg`);
    }

    console.log();
  }

  private printHelp(): void {
    console.log(\`
projectFrameCut MCP Skill - CLI Integration for MCP Server
===========================================================

Usage: projectFrameCut-mcp <command> [options]

Commands:

  start [options]               Start MCP server
    --project <path>            (required) Project directory path
    --transport <stdio|http>    Transport mode (default: stdio)
    --port <number>             HTTP port (default: 32123, requires --transport http)
    --bg                        Run in background

  stop [options]                Stop running MCP server
    --all                       Stop all dotnet processes

  status [options]              Check server status
    --endpoint <url>            Server endpoint (default: http://localhost:32123)

  test [options]                Show test documentation
    --project <path>            Project directory path

  project-info <path>           Get project information
    --project <path>            (required) Project directory path

  generate-client [options]     Generate client code
    --language <python|js>      (required) Target language
    --output <path>             (required) Output file path

  batch-edit [options]          Show batch edit script
    --script <path>             (required) Batch edit JSON file
    --project <path>            (required) Project directory path

  help                          Show this help message

Examples:

  # Start server in stdio mode
  projectFrameCut-mcp start --project D:\\projects\\my_video

  # Start server in HTTP mode (background)
  projectFrameCut-mcp start --project D:\\projects\\my_video --transport http --port 32123 --bg

  # Check server status
  projectFrameCut-mcp status

  # Generate Python client
  projectFrameCut-mcp generate-client --language python --output ./client.py

  # Show project info
  projectFrameCut-mcp project-info --project D:\\projects\\my_video

Documentation: See SKILL_IMPLEMENTATION.md for detailed usage

\`);
  }
}

// Parse command line arguments
const args = process.argv.slice(2);
if (args.length === 0) {
  console.log('No command provided. Use --help for usage information.');
  process.exit(1);
}

const command = args[0];
const parsedArgs: CommandArgs = {};

for (let i = 1; i < args.length; i++) {
  if (args[i].startsWith('--')) {
    const key = args[i].substring(2);
    let value: any = true;

    if (i + 1 < args.length && !args[i + 1].startsWith('--')) {
      value = args[i + 1];
      i++;

      // Try to parse as number
      if (!isNaN(Number(value))) {
        value = Number(value);
      }
    }

    parsedArgs[key] = value;
  }
}

const projectRoot = process.env.PROJECT_ROOT || process.cwd();
const skill = new ProjectFrameCutMCPSkill(projectRoot);

skill
  .execute(command, parsedArgs)
  .catch((error) => {
    console.error(error.message);
    process.exit(1);
  });
