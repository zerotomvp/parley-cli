import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";

type ChannelEvent = {
	type: "ready" | "wake";
	id?: string;
	sid?: string;
	version?: string;
	notification?: string;
};

type ChannelResponse = {
	id: string;
	success: boolean;
	error?: string;
};

const errorText = (error: unknown): string => error instanceof Error ? error.message : String(error);

export default function parleyExtension(pi: ExtensionAPI): void {
	let child: ChildProcessWithoutNullStreams | undefined;
	let generation = 0;

	const respond = (process: ChildProcessWithoutNullStreams, response: ChannelResponse): void => {
		process.stdin.write(`${JSON.stringify(response)}\n`);
	};

	const stop = async (): Promise<void> => {
		generation += 1;
		const running = child;
		child = undefined;
		if (!running || running.exitCode !== null) return;

		running.stdin.end();
		await Promise.race([
			new Promise<void>((resolve) => running.once("close", () => resolve())),
			new Promise<void>((resolve) => setTimeout(resolve, 1_000)),
		]);
		if (running.exitCode === null) running.kill();
	};

	const start = async (ctx: ExtensionContext): Promise<void> => {
		await stop();
		const currentGeneration = generation;
		const sid = ctx.sessionManager.getSessionId();
		const process = spawn("parley", ["integrations", "pi", "--sid", sid], {
			stdio: ["pipe", "pipe", "pipe"],
			env: processEnv(),
		});
		child = process;

		let stderr = "";
		let stdout = "";
		let ready = false;
		process.stderr.setEncoding("utf8");
		process.stderr.on("data", (chunk: string) => {
			stderr = `${stderr}${chunk}`.slice(-2_000);
		});

		process.stdout.setEncoding("utf8");
		process.stdout.on("data", (chunk: string) => {
			stdout += chunk;
			for (;;) {
				const newline = stdout.indexOf("\n");
				if (newline < 0) break;
				const line = stdout.slice(0, newline).trimEnd();
				stdout = stdout.slice(newline + 1);
				if (!line || child !== process || generation !== currentGeneration) continue;

				let event: ChannelEvent;
				try {
					event = JSON.parse(line) as ChannelEvent;
				} catch {
					ctx.ui.notify("Parley wake bridge emitted invalid JSON.", "error");
					continue;
				}

				if (event.type === "ready") {
					ready = true;
					continue;
				}
				if (event.type !== "wake" || !event.id || !event.notification) continue;

				try {
					pi.sendMessage(
						{ customType: "parley", content: event.notification, display: true },
						{ triggerTurn: true, deliverAs: "steer" },
					);
					respond(process, { id: event.id, success: true });
				} catch (error) {
					respond(process, { id: event.id, success: false, error: errorText(error) });
				}
			}
		});

		process.on("error", (error) => {
			if (child !== process || generation !== currentGeneration) return;
			ctx.ui.notify(`Parley wake bridge could not start: ${error.message}`, "error");
		});
		process.on("close", (code) => {
			if (child !== process || generation !== currentGeneration) return;
			child = undefined;
			if (!ready || code !== 0) {
				const detail = stderr.trim() || `exit code ${code ?? "unknown"}`;
				ctx.ui.notify(`Parley wake bridge stopped: ${detail}`, "error");
			}
		});
	};

	pi.on("session_start", async (_event, ctx) => start(ctx));
	pi.on("session_shutdown", async () => stop());
}

// Kept separate so tests and alternative runtimes can replace process.env without the
// extension retaining a stale snapshot across Pi session replacement.
function processEnv(): NodeJS.ProcessEnv {
	return { ...process.env };
}
