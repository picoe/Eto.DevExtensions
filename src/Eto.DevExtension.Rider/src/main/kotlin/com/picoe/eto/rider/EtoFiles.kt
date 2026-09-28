package com.picoe.eto.rider

import com.intellij.execution.configurations.PathEnvironmentVariableUtil
import com.intellij.ide.plugins.PluginManagerCore
import com.intellij.openapi.extensions.PluginId
import com.intellij.openapi.util.SystemInfo
import com.intellij.openapi.vfs.VirtualFile
import java.io.File
import java.nio.file.Path
import kotlin.io.path.exists

/** Designer files, and where to find the .NET tools bundled with the plugin. */
object EtoFiles {
    private const val PLUGIN_ID = "com.picoe.eto.designer"
    private val designer = Regex("\\.(xeto|jeto)$", RegexOption.IGNORE_CASE)
    private val previewable = Regex("\\.(xeto|jeto|eto\\.cs|eto\\.vb)$", RegexOption.IGNORE_CASE)

    /** Files the language server handles. */
    fun isDesigner(file: VirtualFile): Boolean = file.isInLocalFileSystem && designer.containsMatchIn(file.name)

    /** Files the preview can draw. */
    fun isPreviewable(file: VirtualFile): Boolean = file.isInLocalFileSystem && previewable.containsMatchIn(file.name)

    val pluginDir: Path?
        get() = PluginManagerCore.getPlugin(PluginId.getId(PLUGIN_ID))?.pluginPath

    /** A file bundled with the plugin, or null when it isn't there. */
    fun bundled(relative: String): Path? = pluginDir?.resolve(relative)?.takeIf { it.exists() }

    /** @param configured A path from the settings, which wins when set. */
    fun find(configured: String?, bundled: String): Path? {
        configured?.trim()?.takeIf { it.isNotEmpty() }?.let { return Path.of(it).takeIf { path -> path.exists() } }
        return bundled(bundled)
    }

    /** The dotnet executable from the settings, the PATH, or where the installers put it. */
    fun dotnet(): String {
        EtoSettings.instance.dotnetPath.takeIf { it.isNotBlank() }?.let { return it.trim() }
        PathEnvironmentVariableUtil.findExecutableInPathOnAnyOS("dotnet")?.let { return it.path }
        val exe = if (SystemInfo.isWindows) "dotnet.exe" else "dotnet"
        val home = System.getProperty("user.home")
        // IDEs started from the dock or a desktop launcher don't always get the shell's PATH
        val known = listOfNotNull(
            System.getenv("DOTNET_ROOT"),
            "$home/.dotnet",
            if (SystemInfo.isWindows) System.getenv("ProgramFiles")?.let { "$it\\dotnet" } else null,
            if (SystemInfo.isMac) "/usr/local/share/dotnet" else null,
            if (SystemInfo.isMac) "/opt/homebrew/share/dotnet" else null,
            if (SystemInfo.isLinux) "/usr/share/dotnet" else null,
            if (SystemInfo.isLinux) "/usr/lib/dotnet" else null,
            if (SystemInfo.isLinux) "/snap/dotnet-sdk/current" else null,
        )
        return known.map { File(it, exe) }.firstOrNull { it.canExecute() }?.path ?: "dotnet"
    }
}
