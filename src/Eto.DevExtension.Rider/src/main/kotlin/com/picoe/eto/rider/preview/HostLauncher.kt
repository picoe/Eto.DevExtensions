package com.picoe.eto.rider.preview

import com.intellij.openapi.project.Project
import com.intellij.openapi.util.SystemInfo
import com.picoe.eto.rider.EtoFiles
import com.picoe.eto.rider.EtoSettings
import java.io.File
import java.nio.file.Path
import kotlin.io.path.pathString

private const val HOST_DLL = "Eto.DevExtension.PreviewHost.dll"
private const val MAC_EXECUTABLE = "Eto Preview Host.app/Contents/MacOS/Eto.DevExtension.PreviewHost"

// scanning projects on every keystroke is too slow, but an edited project should still be noticed
private const val CACHE_MS = 10000L

const val AUTO = "auto"

/** A platform the preview can draw with. */
data class PlatformOption(val id: String, val label: String) {
    override fun toString() = label
}

/** How to start a preview host process. */
data class HostLaunch(
    val command: List<String>,
    val env: Map<String, String> = emptyMap(),
    /** Label of the platform it draws with. */
    val platform: String,
    /** Shown when the process can't be started. */
    val requirement: String,
)

private class Platform(
    val id: String,
    val label: String,
    /** Matches a project that uses this platform, from a package or project reference. */
    val reference: Regex,
    val launch: () -> HostLaunch?,
)

/**
 * Picks the preview host for a file. The platforms offered depend on the OS and what's installed,
 * and Auto picks the first one the solution references, otherwise the OS's usual one.
 */
class HostLauncher(private val project: Project, private val log: (String) -> Unit) {
    private val autoCache = mutableMapOf<String, Pair<Long, String>>()
    private var lastMessage: String? = null

    /** Platforms available on this machine, best first. */
    fun getPlatforms(): List<PlatformOption> = getAvailable().map { PlatformOption(it.id, it.label) }

    /** @param choice A platform id, or [AUTO]. */
    fun resolve(fileName: String, choice: String): HostLaunch? {
        val platforms = getAvailable()
        val platform = platforms.firstOrNull { it.id == choice } ?: detect(fileName, platforms)
        return platform?.launch?.invoke()
    }

    private fun getAvailable(): List<Platform> {
        val dotnet = EtoFiles.dotnet()
        val configured = EtoSettings.instance.previewHostPath
        val netHost = { EtoFiles.find(configured, "tools/preview/net/$HOST_DLL") }
        val platforms = mutableListOf<Platform>()

        if (SystemInfo.isWindows) {
            val windowsHost = { EtoFiles.find(configured, "tools/preview/win/$HOST_DLL") }
            val requirement = "The preview needs the .NET 8 Desktop Runtime (or newer)."
            platforms += Platform("Wpf", "WPF", Regex("Eto\\.Platform\\.Wpf\\b|Eto\\.Wpf\\.csproj", RegexOption.IGNORE_CASE)) { withDotnet(dotnet, windowsHost(), "Wpf", "WPF", requirement) }
            platforms += Platform("WinForms", "WinForms", Regex("Eto\\.Platform\\.Windows\\b|Eto\\.WinForms\\.csproj", RegexOption.IGNORE_CASE)) { withDotnet(dotnet, windowsHost(), "WinForms", "WinForms", requirement) }
        }
        if (SystemInfo.isMac) {
            EtoFiles.bundled("tools/preview/macos/$MAC_EXECUTABLE")?.let { app ->
                // zip extraction doesn't always keep the executable bit
                app.toFile().takeIf { !it.canExecute() }?.setExecutable(true)
                platforms += Platform("macOS", "macOS", Regex("Eto\\.Platform\\.macOS\\b|Eto\\.macOS\\.csproj", RegexOption.IGNORE_CASE)) {
                    HostLaunch(listOf(app.pathString), platform = "macOS", requirement = "The Eto.macOS preview host could not be started.")
                }
            }
            platforms += Platform("Mac64", "Mac64", Regex("Eto\\.Platform\\.Mac64\\b|Eto\\.Mac64\\.csproj", RegexOption.IGNORE_CASE)) {
                withDotnet(dotnet, netHost(), "Mac64", "Mac64", "The preview needs the .NET 8 runtime (or newer).")
            }
        }

        findGtk()?.let { env ->
            platforms += Platform("Gtk", "Gtk", Regex("Eto\\.Platform\\.Gtk\\b|Eto\\.Gtk\\.csproj", RegexOption.IGNORE_CASE)) {
                withDotnet(dotnet, netHost(), "Gtk", "Gtk", "The preview needs the .NET 8 runtime (or newer) and GTK 3.")?.copy(env = env)
            }
        }
        return platforms
    }

    private fun detect(fileName: String, platforms: List<Platform>): Platform? {
        val key = File(fileName).parent.orEmpty()
        autoCache[key]?.let { (time, id) ->
            val cached = platforms.firstOrNull { it.id == id }
            if (cached != null && System.currentTimeMillis() - time < CACHE_MS) return cached
        }
        if (platforms.isEmpty()) return null

        val texts = (findSolutionProjects(fileName) ?: findWorkspaceProjects()).map(::readText)
        val referenced = platforms.firstOrNull { platform -> texts.any { platform.reference.containsMatchIn(it) } }
        val platform = referenced ?: platforms[0]
        autoCache[key] = System.currentTimeMillis() to platform.id
        log(
            if (referenced != null) "Previewing with ${platform.label}, as the solution uses it."
            else "Previewing with ${platform.label}, as the solution doesn't reference an Eto platform available here."
        )
        return platform
    }

    private fun log(message: String) {
        if (message != lastMessage) {
            lastMessage = message
            log.invoke(message)
        }
    }

    private fun findWorkspaceProjects(): List<String> {
        val base = project.basePath?.let(::File) ?: return emptyList()
        return base.walkTopDown()
            .onEnter { it.name !in setOf("bin", "obj", "node_modules", ".git", ".idea") }
            .filter { it.isFile && Regex("\\.(cs|vb|fs)proj$", RegexOption.IGNORE_CASE).containsMatchIn(it.name) }
            .take(500)
            .map { it.path }
            .toList()
    }
}

private fun withDotnet(dotnet: String, dll: Path?, id: String, label: String, requirement: String): HostLaunch? =
    dll?.let { HostLaunch(listOf(dotnet, it.pathString, "--platform", id), platform = label, requirement = requirement) }

/** GTK 3, and the environment the host needs to find it, or null when it isn't installed. */
private fun findGtk(): Map<String, String>? {
    if (SystemInfo.isMac) {
        // Homebrew (Apple silicon, then Intel) or MacPorts, none of which are on the default library path
        val dir = listOf("/opt/homebrew/lib", "/usr/local/lib", "/opt/local/lib").firstOrNull { File(it, "libgtk-3.0.dylib").exists() }
        return dir?.let { mapOf("DYLD_FALLBACK_LIBRARY_PATH" to joinPath(it, System.getenv("DYLD_FALLBACK_LIBRARY_PATH"))) }
    }
    if (SystemInfo.isWindows) {
        // where GtkSharp's build installs it, or anywhere on PATH such as MSYS2
        val installed = System.getenv("LOCALAPPDATA")?.let { File(it, "Gtk/3.24.24").path }
        val path = System.getenv("PATH").orEmpty()
        val dir = (listOfNotNull(installed) + path.split(File.pathSeparator)).firstOrNull { it.isNotEmpty() && File(it, "libgtk-3-0.dll").exists() }
        return dir?.let { mapOf("PATH" to joinPath(it, path)) }
    }
    // the desktop's own toolkit, so assume it's there
    return emptyMap()
}

private fun joinPath(first: String, rest: String?): String = if (rest.isNullOrEmpty()) first else "$first${File.pathSeparator}$rest"

/** Projects in the nearest solution above the file, or null when there's none. */
private fun findSolutionProjects(fileName: String): List<String>? {
    var dir: File? = File(fileName).parentFile
    while (dir != null) {
        val solution = dir.listFiles().orEmpty().firstOrNull { Regex("\\.slnx?$", RegexOption.IGNORE_CASE).containsMatchIn(it.name) }
        if (solution != null) {
            val pattern = if (solution.name.endsWith(".slnx", ignoreCase = true))
                Regex("<Project\\s+Path=\"([^\"]+\\.(?:cs|vb|fs)proj)\"", RegexOption.IGNORE_CASE)
            else
                Regex("^Project\\(\"[^\"]*\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+\\.(?:cs|vb|fs)proj)\"", setOf(RegexOption.IGNORE_CASE, RegexOption.MULTILINE))
            val root = dir
            return pattern.findAll(readText(solution)).map { File(root, it.groupValues[1].replace('\\', File.separatorChar)).path }.toList()
        }
        dir = dir.parentFile
    }
    return null
}

private fun readText(file: String): String = readText(File(file))

private fun readText(file: File): String = try {
    file.readText()
} catch (e: Exception) {
    ""
}
