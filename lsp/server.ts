import {
    createConnection,
    TextDocuments,
    ProposedFeatures,
    InitializeParams,
    TextDocumentSyncKind,
    InitializeResult,
    Diagnostic,
    Location,
    Range,
    Position
} from 'vscode-languageserver/node';
import { TextDocument } from 'vscode-languageserver-textdocument';
import { CSharpAnalysisService } from './csharp-service';
import * as path from 'path';
import { URI } from 'vscode-uri';

function getBackendPath(): string {
    const base = path.join(__dirname, "../../dp-service");

    if (process.platform === "win32") {
        return path.join(base, "win-x64", "DPService.exe");
    } else if (process.platform === "linux") {
        return path.join(base, "linux-x64", "DPService");
    } else if (process.platform === "darwin") {
        if (process.arch === "arm64") {
            return path.join(base, "osx-arm64", "DPService");
        } else {
            return path.join(base, "osx-x64", "DPService");
        }
    }
    throw new Error(`Unsupported platform: ${process.platform} ${process.arch}`);
}

const connection = createConnection(ProposedFeatures.all);
const documents = new TextDocuments(TextDocument);

const ANALYSIS_DEBOUNCE_MS = 500;
let timer: NodeJS.Timeout | null = null;

const csharpService = new CSharpAnalysisService(
    getBackendPath(),
    connection,
    documents,
    1000, // restartDelayMs
    10000 // requestTimeoutMs
);

connection.onInitialize((params: InitializeParams) => {
    connection.console.log('[DP Server] Server initialized');
    return {
        capabilities: {
            textDocumentSync: TextDocumentSyncKind.Incremental,
            definitionProvider: true,
        }
    };
});


connection.onDefinition(async (params) => {
    const uri = params.textDocument.uri;
    const doc = documents.get(uri);
    if (!doc) return null;

    try {
        const res = await csharpService.getDefinition({
            document: doc,
            position: params.position,
        });

        if (!res) return null;
        return res;
    } catch (err) {
        return null;
    }
});


documents.onDidChangeContent((change) => {
    csharpService.onUpdate(change.document);

    if (timer) {
        clearTimeout(timer);
    }
    timer = setTimeout(async () => {
        try {
            connection.console.log(`[DP Server] Analyzing: ${change.document.uri}`);
            const diagnostics = await csharpService.analyze(change.document);
            connection.sendDiagnostics({ uri: change.document.uri, diagnostics });
        } catch (error) {
            connection.console.error(`[DP Server] Analysis failed: ${error}`);
        }
    }, ANALYSIS_DEBOUNCE_MS);
});

documents.onDidOpen((event) => {
    connection.console.log(`[DP Server] Document opened: ${event.document.uri}`);
    csharpService.onOpenFile(event.document);
    timer = setTimeout(async () => {
        try {
            connection.console.log(`[DP Server] Analyzing: ${event.document.uri}`);
            const diagnostics = await csharpService.analyze(event.document);
            connection.sendDiagnostics({ uri: event.document.uri, diagnostics });
        } catch (error) {
            connection.console.error(`[DP Server] Analysis failed: ${error}`);
        }
    }, 1);
});

documents.onDidClose((event) => {
    connection.console.log(`[DP Server] Document closed: ${event.document.uri}`);
    csharpService.onCloseFile(event.document);
    connection.sendDiagnostics({ uri: event.document.uri, diagnostics: [] }); // Clear diagnostics
});

documents.onDidSave((event) => {
    connection.console.log(`[DP Server] Document saved: ${event.document.uri}`);
    if (timer) {
        clearTimeout(timer);
    }
    timer = setTimeout(async () => {
        try {
            connection.console.log(`[DP Server] Analyzing: ${event.document.uri}`);
            const diagnostics = await csharpService.analyze(event.document);
            connection.sendDiagnostics({ uri: event.document.uri, diagnostics });
        } catch (error) {
            connection.console.error(`[DP Server] Analysis failed: ${error}`);
        }
    }, 1);
});

connection.listen();
documents.listen(connection);