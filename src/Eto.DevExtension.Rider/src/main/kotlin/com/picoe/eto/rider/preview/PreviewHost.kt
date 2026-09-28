package com.picoe.eto.rider.preview

import com.google.gson.JsonElement
import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.Disposable
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.service
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.util.concurrency.AppExecutorUtil
import com.intellij.util.containers.ContainerUtil
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.io.OutputStream
import java.nio.charset.StandardCharsets
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicInteger

// long enough for a first render that compiles code, short enough to recover from a hung control
private const val RENDER_TIMEOUT_MS = 30000L

data class RenderRequest(val fileName: String, val text: String, val width: Int?, val height: Int?, val scale: Double)

data class RenderResult(
    /** Base64 png. */
    val image: String? = null,
    val width: Int? = null,
    val height: Int? = null,
    val errorMessage: String? = null,
    val errorDetails: String? = null,
    /** Label of the platform it was drawn with. */
    val platform: String? = null,
)

/**
 * The .NET process that draws designer files to images, so project code runs outside Rider.
 * Renders one request at a time, and restarts the process whenever it exits or stops responding.
 */
@Service(Service.Level.PROJECT)
class PreviewHost(private val project: Project) : Disposable {
    private val executor = AppExecutorUtil.createBoundedApplicationPoolExecutor("Eto Preview", 1)
    private var process: Process? = null
    private var connection: RpcConnection? = null
    private var launchKey: List<String>? = null
    // hosts that couldn't start, usually for want of a runtime, so they aren't tried again
    private val unavailable = mutableSetOf<List<String>>()
    private val redrawListeners = ContainerUtil.createLockFreeCopyOnWriteList<() -> Unit>()

    val log: PreviewLog get() = project.service()
    val launcher = HostLauncher(project) { log.appendLine(it) }

    /** Called after the project is rebuilt, so previews can be redrawn. */
    fun onRedraw(parent: Disposable, listener: () -> Unit) {
        redrawListeners.add(listener)
        Disposer.register(parent) { redrawListeners.remove(listener) }
    }

    fun render(request: RenderRequest, platform: String): CompletableFuture<RenderResult> =
        CompletableFuture.supplyAsync({ renderNow(request, platform) }, executor)

    override fun dispose() {
        executor.shutdownNow()
        stop()
    }

    private fun renderNow(request: RenderRequest, platform: String): RenderResult {
        // a second try covers a host that exited after a rebuild, or that serves another project
        for (attempt in 0 until 2) {
            val launch = launcher.resolve(request.fileName, platform)
                ?: return error("Could not find the Eto preview host. Set its path in Settings | Tools | Eto.Forms Designer.")
            val connection = getConnection(launch)
                ?: return error("${launch.requirement} See the Log tab for details.")

            val params = JsonObject().apply {
                addProperty("fileName", request.fileName)
                addProperty("text", request.text)
                request.width?.let { addProperty("width", it) }
                request.height?.let { addProperty("height", it) }
                addProperty("scale", request.scale)
                add("assemblies", JsonNull.INSTANCE)
            }
            try {
                val result = connection.request("preview/render", params).get(RENDER_TIMEOUT_MS, TimeUnit.MILLISECONDS) as? JsonObject
                    ?: return error("The preview host returned nothing.")
                if (result.bool("restartRequired")) {
                    stop()
                    continue
                }
                val error = result.getAsJsonObject("error")
                return RenderResult(
                    image = result.string("image"),
                    width = result.int("width"),
                    height = result.int("height"),
                    errorMessage = error?.string("message"),
                    errorDetails = error?.string("details"),
                    platform = launch.platform,
                )
            } catch (e: TimeoutException) {
                stop()
                return error("The preview took too long to draw, so it was stopped.")
            } catch (e: ExecutionException) {
                stop()
                if (attempt > 0) return error("The preview host stopped unexpectedly.", e.cause?.toString())
            }
        }
        return error("The preview host could not load the project.")
    }

    private fun getConnection(launch: HostLaunch): RpcConnection? {
        val key = launch.command
        val running = connection
        if (launchKey == key && running != null && process?.isAlive == true) return running
        if (key in unavailable) return null
        stop()
        launchKey = key
        return start(launch, key)
    }

    private fun start(launch: HostLaunch, key: List<String>): RpcConnection? {
        try {
            val child = GeneralCommandLine(launch.command)
                .withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
                .withEnvironment(launch.env)
                .createProcess()
            process = child
            pump(child.errorStream)

            val rpc = RpcConnection(child.inputStream, child.outputStream) { method, params ->
                when (method) {
                    "preview/restart" -> {
                        log.appendLine("Project rebuilt, restarting the preview host.")
                        redrawListeners.forEach { it() }
                    }
                    "window/logMessage" -> params?.string("message")?.let { log.appendLine(it) }
                }
            }
            connection = rpc
            rpc.request("initialize", JsonObject().apply { addProperty("processId", ProcessHandle.current().pid()) }).get(RENDER_TIMEOUT_MS, TimeUnit.MILLISECONDS)
            rpc.notify("initialized", JsonObject())
            return rpc
        } catch (e: Exception) {
            unavailable.add(key)
            log.appendLine("Could not start the Eto preview host using \"${launch.command.first()}\": ${(e as? ExecutionException)?.cause ?: e}")
            stop()
            return null
        }
    }

    private fun pump(stream: InputStream) {
        AppExecutorUtil.getAppExecutorService().execute {
            try {
                stream.bufferedReader().forEachLine { log.appendLine(it) }
            } catch (_: Exception) {
            }
        }
    }

    private fun stop() {
        val rpc = connection
        val child = process
        connection = null
        process = null
        launchKey = null
        rpc?.close()
        child?.destroy()
    }
}

private fun error(message: String, details: String? = null) = RenderResult(errorMessage = message, errorDetails = details ?: message)

private fun JsonObject.string(name: String): String? = get(name)?.takeIf { it.isJsonPrimitive }?.asString
private fun JsonObject.int(name: String): Int? = get(name)?.takeIf { it.isJsonPrimitive }?.asInt
private fun JsonObject.bool(name: String): Boolean = get(name)?.takeIf { it.isJsonPrimitive }?.asBoolean == true

/** JSON-RPC with LSP style Content-Length framing, as spoken by the preview host. */
private class RpcConnection(
    input: InputStream,
    private val output: OutputStream,
    private val onNotification: (String, JsonObject?) -> Unit,
) {
    private val nextId = AtomicInteger()
    private val pending = ConcurrentHashMap<Int, CompletableFuture<JsonElement?>>()
    @Volatile
    private var closed = false

    init {
        val reader = BufferedInputStream(input)
        AppExecutorUtil.getAppExecutorService().execute {
            try {
                while (!closed) {
                    val message = read(reader) ?: break
                    dispatch(message)
                }
            } catch (e: Exception) {
                if (!closed) logger<PreviewHost>().info("Eto preview host connection closed", e)
            }
            fail(IllegalStateException("The preview host exited."))
        }
    }

    fun request(method: String, params: JsonElement): CompletableFuture<JsonElement?> {
        val id = nextId.incrementAndGet()
        val future = CompletableFuture<JsonElement?>()
        pending[id] = future
        try {
            write(JsonObject().apply {
                addProperty("jsonrpc", "2.0")
                addProperty("id", id)
                addProperty("method", method)
                add("params", params)
            })
        } catch (e: Exception) {
            pending.remove(id)
            future.completeExceptionally(e)
        }
        if (closed) fail(IllegalStateException("The preview host exited."))
        return future
    }

    fun notify(method: String, params: JsonElement) {
        write(JsonObject().apply {
            addProperty("jsonrpc", "2.0")
            addProperty("method", method)
            add("params", params)
        })
    }

    fun close() {
        closed = true
        fail(IllegalStateException("The preview host was stopped."))
        try {
            output.close()
        } catch (_: Exception) {
        }
    }

    private fun fail(error: Exception) {
        closed = true
        for (id in pending.keys.toList())
            pending.remove(id)?.completeExceptionally(error)
    }

    private fun dispatch(message: JsonObject) {
        val method = message.get("method")?.takeIf { it.isJsonPrimitive }?.asString
        val id = message.get("id")?.takeIf { it.isJsonPrimitive }?.asInt
        when {
            method == null && id != null -> {
                val future = pending.remove(id) ?: return
                val error = message.getAsJsonObject("error")
                if (error != null) future.completeExceptionally(IllegalStateException(error.get("message")?.asString ?: error.toString()))
                else future.complete(message.get("result"))
            }
            method != null && id == null -> onNotification(method, message.get("params") as? JsonObject)
            // the host has no requests for us, but mustn't be left waiting
            method != null -> write(JsonObject().apply {
                addProperty("jsonrpc", "2.0")
                addProperty("id", id)
                add("error", JsonObject().apply {
                    addProperty("code", -32601)
                    addProperty("message", "Method not found: $method")
                })
            })
        }
    }

    @Synchronized
    private fun write(message: JsonObject) {
        val body = message.toString().toByteArray(StandardCharsets.UTF_8)
        output.write("Content-Length: ${body.size}\r\n\r\n".toByteArray(StandardCharsets.US_ASCII))
        output.write(body)
        output.flush()
    }

    private fun read(input: InputStream): JsonObject? {
        var length = -1
        while (true) {
            val line = readLine(input) ?: return null
            if (line.isEmpty()) break
            if (line.startsWith("Content-Length:", ignoreCase = true))
                length = line.substring("Content-Length:".length).trim().toIntOrNull() ?: -1
        }
        if (length < 0) return JsonObject()
        val body = input.readNBytes(length)
        if (body.size < length) return null
        return JsonParser.parseString(String(body, StandardCharsets.UTF_8)).asJsonObject
    }

    private fun readLine(input: InputStream): String? {
        val line = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return null
            if (b == '\n'.code) break
            if (b != '\r'.code) line.write(b)
        }
        return line.toString(StandardCharsets.US_ASCII)
    }
}
