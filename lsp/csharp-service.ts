import { ChildProcess, spawn } from 'child_process';
import { TextDocument } from 'vscode-languageserver-textdocument';
import { Diagnostic, Connection, Location, Range, Position } from 'vscode-languageserver';
import { URI } from 'vscode-uri';
import { json } from 'stream/consumers';


export class CSharpAnalysisService {
    private process: ChildProcess | null = null;
    private restartAttempts = 0;
    private readonly maxRestartAttempts = 5;
    private analyzeRequests: Map<string, { resolve: (diags: Diagnostic[]) => void, reject: (err: Error) => void }> = new Map();
    private definitionRequests: Map<string, { resolve: (loc: Location | null) => void, reject: (err: Error) => void }> = new Map();
    private buffer = '';

    constructor(
        private readonly exePath: string,
        private readonly connection: Connection,
        private readonly documents: { all: () => TextDocument[] },
        private readonly restartDelayMs = 1000,
        private readonly requestTimeoutMs = 10000
    ) {
        this.spawnProcess();
    }

    private spawnProcess(): void {
        this.process = spawn(this.exePath, [], {
            stdio: ['pipe', 'pipe', 'inherit'],
            windowsVerbatimArguments: true,
            env: { ...process.env, NODE_ENV: 'production' },
            windowsHide: true
        });

        this.connection.console.log(`[DP] C# process started (PID: ${this.process.pid})`);

        this.process.stdout?.on('data', (data: Buffer) => {
            this.buffer += data.toString();
            const lines = this.buffer.split('\n');

            if (lines.length > 1) {
                this.buffer = lines.pop()!;
                for (const line of lines) {
                    if (line.trim() === '') continue;
                    try {
                        const result = JSON.parse(line);
                        if (result.Error) {
                            this.connection.console.error(`[DP] Analysis error: ${result.Error}`);
                            this.clearRequests(new Error(result.Error));
                        } else {
                            switch (result.Type) {
                                case 'AnalyzeResult':
                                    const diags = this.mapDiagnostics(result.Diagnostics || []);
                                    this.resolveAnalyzeRequests(diags);
                                    break;
                                case 'DefinitionResult':
                                    const positions = result.Positions;
                                    if (Array.isArray(positions) && positions.length > 0) {
                                        const pos = positions[0];
                                        const location = Location.create(
                                            URI.file(pos.FilePath).toString(),
                                            Range.create(
                                                Position.create(pos.StartLine, pos.StartColumn),
                                                Position.create(pos.EndLine, pos.EndColumn)
                                            )
                                        );
                                        this.resolveDefinitionRequests(location);
                                    } else {
                                        this.resolveDefinitionRequests(null);
                                    }
                                    break;
                                default:
                                    this.connection.console.error(`[DP] Unknown result type: ${result.Type}`);
                                    break;
                            }
                        }
                    } catch (err) {
                        this.connection.console.error(`[DP] JSON parse error: ${err}, data: ${line}`);
                    }
                }
            }
        });

        this.process.on('exit', (err) => {
            this.connection.console.error(`[DP] C# process exited, error: ${err}`);
            this.scheduleRestart();
        });

        this.process.on('error', (err) => {
            this.connection.console.error(`[DP] C# process error: ${err}`);
            this.scheduleRestart();
        });
    }

    private resolveAnalyzeRequests(diagnostics: Diagnostic[]): void {
        this.analyzeRequests.forEach(({ resolve }) => resolve(diagnostics));
        this.analyzeRequests.clear();
    }

    private resolveDefinitionRequests(location: Location | null): void {
        this.definitionRequests.forEach(({ resolve }) => resolve(location));
        this.definitionRequests.clear();
    }

    private clearRequests(error: Error): void {
        this.analyzeRequests.forEach(({ reject }) => reject(error));
        this.analyzeRequests.clear();
        this.definitionRequests.forEach(({ reject }) => reject(error));
        this.definitionRequests.clear();
    }

    private scheduleRestart(): void {
        if (this.restartAttempts >= this.maxRestartAttempts) {
            this.connection.console.error('[DP] Max restart attempts reached. Giving up.');
            this.clearRequests(new Error('C# process unavailable'));
            return;
        }

        this.restartAttempts++;
        setTimeout(() => {
            this.connection.console.log(`[DP] Restarting C# process (attempt ${this.restartAttempts})`);
            this.spawnProcess();
        }, this.restartDelayMs);
    }

    public async analyze(document: TextDocument): Promise<Diagnostic[]> {
        const requestId = Date.now().toString();

        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                this.analyzeRequests.delete(requestId);
                reject(new Error('Analysis timeout'));
            }, this.requestTimeoutMs);

            this.analyzeRequests.set(requestId, {
                resolve: (diags) => {
                    clearTimeout(timeout);
                    resolve(diags);
                },
                reject: (err) => {
                    clearTimeout(timeout);
                    reject(err);
                }
            });

            const filePath = URI.parse(document.uri).fsPath;
            const payload = {
                type: 'analyze',
                id: requestId,
                filePath,
            };

            this.process?.stdin?.write(JSON.stringify(payload) + '\n', (err) => {
                if (err) {
                    this.analyzeRequests.delete(requestId);
                    reject(err);
                }
            });
        });
    }

    private mapDiagnostics(diags: any[]): Diagnostic[] {
        return diags.map(d => ({
            range: {
                start: { line: d.Line >= 0 ? d.Line : 0, character: d.Column >= 0 ? d.Column : 0 },
                end: { line: d.Line >= 0 ? d.Line : 0, character: Math.max(d.Column >= 0 ? d.Column : 0, 0) + 1 }
            },
            message: d.Message || 'unknown error',
            source: 'dp',
            severity: d.Severity || 1
        }));
    }

    public onUpdate(
        document: TextDocument,
    ): void {
        const filePath = URI.parse(document.uri).fsPath;
        const payload = {
            type: 'update',
            filePath,
            changes: document.getText(),
        };
        this.process?.stdin?.write(JSON.stringify(payload) + '\n', (err) => {
            if (err) {
                this.connection.console.error(`[DP] Failed to send incremental update: ${err}`);
            }
        });
    }

    public onOpenFile(document: TextDocument): void {
        const filePath = URI.parse(document.uri).fsPath;
        const payload = {
            type: 'openFile',
            filePath,
            content: document.getText(),
        };
        this.process?.stdin?.write(JSON.stringify(payload) + '\n', (err) => {
            if (err) {
                this.connection.console.error(`[DP] Failed to send open file: ${err}`);
            }
        });
    }

    public onCloseFile(document: TextDocument): void {
        const filePath = URI.parse(document.uri).fsPath;
        const payload = {
            type: 'closeFile',
            filePath,
        };
        this.process?.stdin?.write(JSON.stringify(payload) + '\n', (err) => {
            if (err) {
                this.connection.console.error(`[DP] Failed to send close file: ${err}`);
            }
        });
    }

    public async getDefinition(params: { document: TextDocument, position: { line: number, character: number } }): Promise<Location | null> {
        const requestId = Date.now().toString();
        return new Promise((resolve, reject) => {
            const timeout = setTimeout(() => {
                this.definitionRequests.delete(requestId);
                reject(new Error('Definition timeout'));
            }, this.requestTimeoutMs);

            this.definitionRequests.set(requestId, {
                resolve: (loc) => {
                    clearTimeout(timeout);
                    resolve(loc);
                },
                reject: (err) => {
                    clearTimeout(timeout);
                    reject(err);
                }
            });

            const filePath = URI.parse(params.document.uri).fsPath;
            const payload = {
                type: 'definition',
                id: requestId,
                filePath,
                position: params.position
            };

            this.process?.stdin?.write(JSON.stringify(payload) + '\n', (err) => {
                if (err) {
                    this.definitionRequests.delete(requestId);
                    reject(err);
                }
            });
        });

    }
}