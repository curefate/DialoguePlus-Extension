import * as path from 'path';
import { workspace, ExtensionContext, window } from 'vscode';
import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
    State,
    TransportKind
} from 'vscode-languageclient/node';

let client: LanguageClient;

export function activate(context: ExtensionContext) {
    const serverModulePath = context.asAbsolutePath(
        path.join('out', 'lsp', 'server.js')
    );

    const serverOptions: ServerOptions = {
        run: { module: serverModulePath, transport: TransportKind.ipc },
        debug: {
            module: serverModulePath,
            transport: TransportKind.ipc,
            options: { execArgv: ['--nolazy', '--inspect=6009'] }
        }
    };

    const clientOptions: LanguageClientOptions = {
        documentSelector: [{ scheme: 'file', language: 'dp' }],
        synchronize: {
            fileEvents: workspace.createFileSystemWatcher('**/.dp')
        }
    };

    client = new LanguageClient(
        'dpLanguageServer',
        'DP Language Server',
        serverOptions,
        clientOptions
    );

    client.onDidChangeState(event => {
        if (event.newState === State.Stopped) {
            window.showErrorMessage('[DP] Language Server stopped unexpectedly.');
        }
    });

    client.start();
}

export function deactivate(): Thenable<void> | undefined {
    return client ? client.stop() : undefined;
}