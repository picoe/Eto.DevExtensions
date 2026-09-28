// the replacements for the deprecated LSP API are newer than Rider 2025.1, which is still supported
@file:Suppress("DEPRECATION")

package com.picoe.eto.rider.lsp

import com.google.gson.JsonObject
import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerManager
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import com.picoe.eto.rider.EtoFiles
import com.picoe.eto.rider.EtoSettings

private const val SERVER_DLL = "Eto.DevExtension.LanguageServer.dll"

class EtoLspServerSupportProvider : LspServerSupportProvider {
    override fun fileOpened(project: Project, file: VirtualFile, serverStarter: LspServerSupportProvider.LspServerStarter) {
        if (EtoFiles.isDesigner(file))
            serverStarter.ensureServerStarted(EtoLspServerDescriptor(project))
    }
}

private class EtoLspServerDescriptor(project: Project) : ProjectWideLspServerDescriptor(project, "Eto.Forms Designer") {
    override fun isSupportedFile(file: VirtualFile): Boolean = EtoFiles.isDesigner(file)

    override fun getLanguageId(file: VirtualFile): String = file.extension?.lowercase() ?: super.getLanguageId(file)

    override fun createCommandLine(): GeneralCommandLine {
        val server = EtoFiles.find(EtoSettings.instance.languageServerPath, "tools/server/$SERVER_DLL")
            ?: throw ExecutionException("Could not find $SERVER_DLL. Set its path in Settings | Tools | Eto.Forms Designer.")
        return GeneralCommandLine(EtoFiles.dotnet(), server.toString())
            .withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
    }

    override fun createInitializationOptions(): Any = JsonObject().apply {
        EtoSettings.instance.etoAssemblyPath.takeIf { it.isNotEmpty() }?.let { addProperty("etoAssemblyPath", it) }
    }
}

/** Only one Eto.Forms version is loaded per session, so this picks up a different project's. */
class RestartLanguageServerAction : AnAction() {
    override fun getActionUpdateThread() = ActionUpdateThread.BGT

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabled = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        LspServerManager.getInstance(project).stopAndRestartIfNeeded(EtoLspServerSupportProvider::class.java)
    }
}
